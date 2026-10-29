using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Peds;

namespace Sim.Core.City;

/// <summary>
/// Исполнение ног micro (zoning-demand.md §4, <c>residents.trip_execution = "agents"</c>): нога жителя - машина (<see cref="PendingTrip"/> с <c>ResidentRef</c>, затем <c>VehicleStore.Resident</c>) либо
/// пешеход (<see cref="PedWorld.RequestLeg"/>; <c>PedStore.Resident</c> - индекс жителя города). Житель в <c>WaitDepart</c>, пока агент не вышел (дедлайн <c>wait_max_s</c> - событие колеса
/// <c>Deadline</c>), в <c>InLeg</c> - пока агент в пути. Прибытие кладёт жителя в <see cref="ArrivalRing"/>, фаза 9r завершает ногу (<see cref="OnLegArrived"/>): наблюдение в скимы,
/// состояние по типу цели, следующее событие плана. Все события жителя - один слот <c>EvTick/EvKind</c> плюс колесо; нога без события в пути.
/// </summary>
internal static class TripLegs
{
    private enum Begin
    {
        Submitted,      // заявка принята: ждём агента, дедлайн в колесе
        Backlog,        // очередь спавна/заявок полна: повтор на следующем тике
        NoSpot,         // парковка: у цели нет места, заявка не подана; повтор через retry_s (parking.md §5)
        Instant,        // парковка: цель позади на том же сегменте, машина переставлена без выезда, нога уже в стадии ходьбы или в кольце прибытий
        Infeasible,     // ни один способ недоступен
    }

    // ------------------------------------------------------------------ начало ноги

    /// <summary>
    /// Нога тура <c>PlanCursor</c> жителя: туда (<paramref name="outbound"/>) из дома к цели тура либо обратно домой, способом <paramref name="mode"/>. Недоступный автобус заменяется пешком, недоступная
    /// машина - пешком (если есть пешеходы). Заполняет поля ноги, ставит <c>WaitDepart</c> и подаёт заявку.
    /// </summary>
    internal static bool StartLeg(World w, CityWorld c, int r, bool outbound, TravelMode mode)
    {
        if (c.Exec == TripExecution.Aggregate)
        {
            return Demand.AggregateExec.StartLeg(w, c, r, outbound, mode);   // нога без агента (§9)
        }

        CityResidents res = c.Residents;
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        int home = res.Home[r];
        int far = res.PlanDest[tour];
        int origin = outbound ? home : far;
        int dest = outbound ? far : home;
        int lo = c.LotOf(origin);
        int ld = c.LotOf(dest);
        long now = w.Tick;
        int stopA = -1;
        int stopB = -1;
        if (!Resolve(w, c, r, lo, ld, ref mode, out stopA, out stopB))
        {
            return false;
        }

        if (res.PlanMode[tour] != (byte)mode)
        {
            res.PlanMode[tour] = (byte)mode;   // обратная нога идёт тем же способом
        }

        res.LegMode[r] = (byte)mode;
        res.LegO[r] = (ushort)c.Lots.Cell[lo];
        res.LegD[r] = (ushort)c.Lots.Cell[ld];
        res.LegReq[r] = now;
        res.LegWalk[r] = 0;
        res.LegWait[r] = 0;
        res.LegRide[r] = 0;
        res.LegDest[r] = dest;
        res.LegStopA[r] = stopA;
        res.LegStopB[r] = stopB;
        res.Agent[r] = -1;
        res.AgentGen[r] = 0;
        res.State[r] = (byte)ResidentState.WaitDepart;
        res.StateTick[r] = now;
        bool parked = c.Parking != null;
        if (parked)
        {
            res.LegStage[r] = LegStages.None;
            res.LegWaitFrom[r] = now;
            res.LegParkWait[r] = 0;
        }

        switch (mode)
        {
            case TravelMode.Car:
                w.Counters.ResLegsCar++;
                break;
            case TravelMode.Bus:
                w.Counters.ResLegsBus++;
                break;
            default:
                w.Counters.ResLegsWalk++;
                break;
        }

        if (parked && mode == TravelMode.Car && res.CarWalkMm[r] > 0)
        {
            // Пешком от двери к машине (parking.md §6): таймер колеса, заявка подаётся по его окончании.
            int ticks = Parking.ParkingOps.WalkTicks(c.Parking!, res.CarWalkMm[r]);
            res.State[r] = (byte)ResidentState.InLeg;
            res.LegStage[r] = LegStages.WalkToCar;
            res.LegWalk[r] += ticks;
            w.Counters.ParkWalkTicks += ticks;
            CityPhase.SetEvent(w, c, r, now + ticks, ResidentEvent.AccessWalkDone);
            return true;
        }

        ArmRequest(w, c, r, outbound, Submit(w, c, r));
        return true;
    }

    /// <summary>Фаза 2r: конец пешего участка у машины. <c>WalkToCar</c> - житель ждёт выезда (<c>LegWaitFrom = Tick</c>), заявка подаётся; <c>WalkFromCar</c> - нога закончена, житель в кольцо прибытий.</summary>
    internal static void OnAccessWalkDone(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if (!res.IsAlive(r) || res.State[r] != (byte)ResidentState.InLeg)
        {
            return;
        }

        long now = w.Tick;
        byte stage = res.LegStage[r];
        res.LegStage[r] = LegStages.None;
        if (stage == LegStages.WalkFromCar)
        {
            c.Arrivals.Push(r);
        }
        else if (stage == LegStages.WalkToCar)
        {
            res.State[r] = (byte)ResidentState.WaitDepart;
            res.StateTick[r] = now;
            res.LegWaitFrom[r] = now;
            ArmRequest(w, c, r, res.LegDest[r] != res.Home[r], Submit(w, c, r));
        }
    }

    /// <summary>
    /// Машина доступна для ноги в режиме парковки: у жителя есть машина, она стоит на объекте (не в пути) и рядом со зданием, где житель (<c>CarWalkMm &gt;= 0</c>; -1 - эвакуация, §7). Нет - нога пойдёт другим способом.
    /// </summary>
    private static bool CarUsable(CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        return (res.Flags[r] & ResidentFlags.HasCar) != 0 && res.CarFac[r] >= 0 && res.CarWalkMm[r] >= 0;
    }

    /// <summary>Способ ноги между участками: машина (полосы есть), автобус (пара остановок), пешком (пешеходы города, одна пешеходная компонента). false - ничего не доступно.</summary>
    private static bool Resolve(World w, CityWorld c, int r, int lo, int ld, ref TravelMode mode, out int stopA, out int stopB)
    {
        stopA = -1;
        stopB = -1;
        if (mode == TravelMode.Car && (c.Lots.CarLane[lo] < 0 || c.Lots.CarLane[ld] < 0 || c.Lots.RoadComp[lo] != c.Lots.RoadComp[ld] || (c.Parking != null && !CarUsable(c, r))))
        {
            mode = TravelMode.Walk;
        }

        if (mode == TravelMode.Bus && !(PedFeasible(w, c, lo, ld) && w.Peds!.FindBusPair(w, c.Lots.PedEdge[lo], c.Lots.PedSMm[lo], c.Lots.PedEdge[ld], c.Lots.PedSMm[ld], out stopA, out stopB)))
        {
            mode = TravelMode.Walk;
        }

        return mode == TravelMode.Car || PedFeasible(w, c, lo, ld);
    }

    /// <summary>Нога пешком/автобусом возможна: пешеходы города включены, у обоих участков есть точки тротуара одной связной компоненты.</summary>
    internal static bool PedFeasible(World w, CityWorld c, int lo, int ld) =>
        w.Peds != null && w.Peds.CitySource && c.Lots.PedEdge[lo] >= 0 && c.Lots.PedEdge[ld] >= 0 && c.Lots.PedComp[lo] >= 0 && c.Lots.PedComp[lo] == c.Lots.PedComp[ld];

    /// <summary>Подаёт заявку текущей ноги жителя (<c>WaitDepart</c>, поля ноги заполнены).</summary>
    private static Begin Submit(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        int dest = res.LegDest[r];
        bool outbound = dest != res.Home[r];
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        int origin = outbound ? res.Home[r] : res.PlanDest[tour];
        int lo = c.LotOf(origin);
        int ld = c.LotOf(dest);
        var mode = (TravelMode)res.LegMode[r];
        if (mode == TravelMode.Car)
        {
            if (w.Trips.IsFull || OverloadValve.Saturated(w, c))
            {
                return Begin.Backlog;   // очередь полна либо сеть упёрлась в потолок плотности клапана (ADR-0035): повтор на следующем тике
            }

            if (c.Parking != null)
            {
                return SubmitParked(w, c, r, dest, lo, ld);
            }

            int type = CarType(w);
            ref readonly VehicleTypeDesc t = ref w.Types[type];
            bool toOutside = OutsideRef.Is(dest);
            int destLane = toOutside ? c.Outside!.InLane[OutsideRef.Conn(dest)] : c.Lots.CarLane[ld];   // к подключению - до конца полосы въезда в узел, машина исчезает у края (ADR-0037)
            float laneEnd = MathF.Max(0f, w.Net.Lanes[destLane].Length - t.MinGap);
            float destS = toOutside ? laneEnd : MathF.Min(c.Lots.CarS[ld], laneEnd);
            var trip = new PendingTrip
            {
                FromZone = -1,
                ToZone = -1,
                FromLane = c.Lots.CarLane[lo],
                FromS = 0f,   // машина встаёт в начало полосы участка (как у зон): ближе к узлу активные множества ворот не успевают её увидеть (ADR-0019)
                Type = (byte)type,
                DestLane = destLane,
                DestS = destS,
                Route = -1,
                ResidentRef = r + 1,
            };
            return w.Trips.TryEnqueue(in trip) ? Begin.Submitted : Begin.Backlog;
        }

        return w.Peds!.RequestLeg(r, res.LegReq[r], c.Lots.PedEdge[lo], c.Lots.PedSMm[lo], c.Lots.PedEdge[ld], c.Lots.PedSMm[ld], mode, res.LegStopA[r], res.LegStopB[r])
            ? Begin.Submitted
            : Begin.Backlog;
    }

    /// <summary>
    /// Машинная заявка в режиме парковки (parking.md §5-§6): резерв места у цели до заявки (нет места - заявка не подаётся, <see cref="Begin.NoSpot"/>), поездка из места машины жителя (<c>AccessLane/AccessS</c>
    /// по правилу горизонта ворот) к точке въезда резерва (дом: к месту дома либо к въезду участка). Резерв гостя записан в заявку (<c>ParkRef/ParkGen</c>) и переходит к машине при постановке.
    /// </summary>
    private static Begin SubmitParked(World w, CityWorld c, int r, int dest, int lo, int ld)
    {
        CityResidents res = c.Residents;
        Parking.ParkingStore p = c.Parking!;
        if (!Parking.ParkingOps.TryReserve(w, r, dest, out int fac))
        {
            if (res.LegParkWait[r] == 0)
            {
                res.LegParkWait[r] = -1;   // «ждёт место»: отметка первого отказа на ногу; при успехе заменится на реальное ожидание
                w.Counters.ParkNoSpotWaits++;
                c.Kpi.OnNoParking(c, dest);   // случай недовольства NoParking и жалоба у здания цели (progression.md §6.1)
                w.Places.OnNoSpot(w, c, dest);   // EV1: ParkingFull у здания цели (с окном и остыванием)
            }

            return Begin.NoSpot;
        }

        bool guest = dest != res.Home[r];
        if (!Parking.ParkingOps.TryAccess(w, fac, ld, out int destLane, out float destS))
        {
            if (guest)
            {
                Parking.ParkingOps.Release(w, fac);
            }

            return Begin.NoSpot;
        }

        if (!Parking.ParkingOps.TryOrigin(w, r, out int fromLane, out float fromS))
        {
            fromLane = c.Lots.CarLane[lo];
            fromS = 0f;
        }

        if (Rules.RuleEnv.DirSegOf(w.Net, fromLane) == Rules.RuleEnv.DirSegOf(w.Net, destLane) && fromS > destS)
        {
            // Цель позади на том же направленном сегменте: маршрута «назад» нет (поиск возвращает NoPath), а ехать нужно несколько метров. Машина перемещается на место без выезда на дорогу (§6).
            res.LegParkWait[r] = (int)Math.Min(int.MaxValue, w.Tick - res.LegWaitFrom[r]);
            Parking.ParkingOps.Hop(w, r, guest ? fac : -1);
            long now = w.Tick;
            w.Counters.ResWaitDepartTicks += now - res.StateTick[r];
            res.State[r] = (byte)ResidentState.InLeg;
            res.StateTick[r] = now;
            res.Agent[r] = -1;
            res.AgentGen[r] = 0;
            CityPhase.SetEvent(w, c, r, -1, ResidentEvent.None);
            int walkMm = Math.Max(0, res.CarWalkMm[r]);
            if (walkMm > 0)
            {
                int ticks = Parking.ParkingOps.WalkTicks(p, walkMm);
                res.LegStage[r] = LegStages.WalkFromCar;
                res.LegWalk[r] += ticks;
                w.Counters.ParkWalkTicks += ticks;
                CityPhase.SetEvent(w, c, r, now + ticks, ResidentEvent.AccessWalkDone);
            }
            else
            {
                c.Arrivals.Push(r);
            }

            return Begin.Instant;
        }

        var trip = new PendingTrip
        {
            FromZone = -1,
            ToZone = -1,
            FromLane = fromLane,
            FromS = fromS,
            Type = (byte)CarType(w),
            DestLane = destLane,
            DestS = destS,
            Route = -1,
            ResidentRef = r + 1,
            ParkRef = guest ? fac + 1 : 0,
            ParkGen = guest ? p.Pool.GenerationOf(fac) : 0,
        };
        if (!w.Trips.TryEnqueue(in trip))
        {
            if (guest)
            {
                Parking.ParkingOps.Release(w, fac);
            }

            return Begin.Backlog;
        }

        res.LegParkWait[r] = (int)Math.Min(int.MaxValue, w.Tick - res.LegWaitFrom[r]);
        return Begin.Submitted;
    }

    /// <summary>Начало ожидания выезда для дедлайна <c>wait_max</c>: в режиме парковки - после пешего участка к машине (<c>LegWaitFrom</c>), иначе заявка ноги.</summary>
    private static long WaitBase(CityWorld c, int r) => c.Parking != null ? c.Residents.LegWaitFrom[r] : c.Residents.LegReq[r];

    /// <summary>Событие жителя после попытки подачи: дедлайн <c>wait_max</c> (заявка принята), повтор через <c>retry_s</c> (нет мест) либо через тик (очередь полна).</summary>
    private static void ArmRequest(World w, CityWorld c, int r, bool outbound, Begin begin)
    {
        long deadline = WaitBase(c, r) + c.Cfg.Residents.WaitMaxTicks;
        byte retry = outbound ? ResidentEvent.Depart : ResidentEvent.EndActivity;
        switch (begin)
        {
            case Begin.Submitted:
                CityPhase.SetEvent(w, c, r, deadline, ResidentEvent.Deadline);
                break;
            case Begin.Instant:
                break;
            case Begin.NoSpot:
                long when = Math.Min(w.Tick + c.Parking!.Cfg.RetryTicks, deadline);   // повтор не позже дедлайна: он сам переключит способ
                CityPhase.SetEvent(w, c, r, Math.Max(when, w.Tick + 1), retry);
                break;
            default:
                CityPhase.SetEvent(w, c, r, w.Tick + 1, retry);
                break;
        }
    }

    /// <summary>Повтор заявки жителя в <c>WaitDepart</c> (очередь была полна, нет мест) либо дедлайн, если ждёт дольше <c>wait_max</c>.</summary>
    internal static void RetryRequest(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if (w.Tick >= WaitBase(c, r) + c.Cfg.Residents.WaitMaxTicks)
        {
            OnWaitTimeout(w, c, r);
            return;
        }

        if (res.LegMode[r] == (byte)TravelMode.Car && OverloadValve.Stale(w, c, WaitBase(c, r)))
        {
            c.Valve.StaleLegs++;   // сеть насыщена, а заявка не вошла в очередь дольше порога: безнадёжна (ADR-0035)
            OnWaitTimeout(w, c, r);
            return;
        }

        bool outbound = res.LegDest[r] != res.Home[r];
        ArmRequest(w, c, r, outbound, Submit(w, c, r));
    }

    private static int CarType(World w)
    {
        for (int i = 0; i < w.Types.Length; i++)
        {
            if (w.Types[i].Category == VehicleCategory.Car)
            {
                return i;
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ дедлайн WaitDepart

    /// <summary>
    /// Ожидание агента дольше <c>wait_max_s</c> (или заявка потеряна: нет пути): другой доступный способ, иначе тур отменяется и житель остаётся дома; обратная нога идёт домой агрегатом
    /// (клапан перегрузки, ADR-0035; <c>res_stuck++</c> - только если домой не попасть ни одним способом).
    /// </summary>
    internal static void OnWaitTimeout(World w, CityWorld c, int r, bool dropped = false)
    {
        CityResidents res = c.Residents;
        bool queued = CancelRequest(w, r);
        w.Counters.ResWaitDepartTicks += w.Tick - res.StateTick[r];
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        bool outbound = res.LegDest[r] != res.Home[r];
        var mode = (TravelMode)res.LegMode[r];
        int origin = outbound ? res.Home[r] : res.PlanDest[tour];
        int dest = res.LegDest[r];
        int lo = c.LotOf(origin);
        int ld = c.LotOf(dest);
        if ((res.Flags[r] & ResidentFlags.Arriving) != 0 && c.Outside != null)
        {
            c.Outside.ArrivalTimeouts++;   // переселенец не дождался выезда от края: домой его доведёт клапан ниже (не застревание)
        }

        bool parkWait = c.Parking != null && mode == TravelMode.Car && !queued && !dropped;   // ждал место: заявка в очередь так и не попала
        bool keepCar = c.Parking != null && mode == TravelMode.Car && !outbound;               // домой машина едет всегда: иначе она осталась бы стоять у цели
        if (!keepCar && Alternative(w, c, mode, lo, ld, out TravelMode alt) && StartLeg(w, c, r, outbound, alt))
        {
            if (parkWait)
            {
                w.Counters.ParkModeSwitch++;
            }

            return;
        }

        if (outbound)
        {
            w.Counters.ResToursCancelled++;
            c.Kpi.OnTourCancelled(c, r);   // случай TourCancelled и жалоба у дома (progression.md §6.1)
            if (parkWait)
            {
                w.Counters.ParkToursDropped++;
            }

            FinishTour(w, c, r);
            return;
        }

        // Клапан перегрузки (ADR-0035): обратная нога без агента дольше дедлайна не ставится в очередь заново, а исполняется агрегатом; стоимость - от исходной заявки, с ожиданием.
        if (!ValveHome(w, c, r, mode))
        {
            ArriveWithoutLeg(w, c, r);
        }
    }

    /// <summary>
    /// Клапан обратной ноги (ADR-0035): житель едет домой агрегатом (время - модель BPR либо ским), <c>LegReq</c> прежний - вся задержка входит в стоимость ноги и в скимы. В режиме парковки машина
    /// переставляется домой без выезда (<see cref="Parking.ParkingOps.Hop"/>), как если бы доехала. false - домой не попасть ни одним способом.
    /// </summary>
    private static bool ValveHome(World w, CityWorld c, int r, TravelMode mode)
    {
        CityResidents res = c.Residents;
        long req = res.LegReq[r];
        if (c.Parking != null && mode == TravelMode.Car && res.CarFac[r] >= 0)
        {
            Parking.ParkingOps.Hop(w, r, -1);
        }

        if (!Demand.AggregateExec.StartLeg(w, c, r, false, mode, req))
        {
            return false;
        }

        c.Valve.HomeLegs++;
        return true;
    }

    private static bool Alternative(World w, CityWorld c, TravelMode mode, int lo, int ld, out TravelMode alt)
    {
        alt = TravelMode.Walk;
        if (mode == TravelMode.Walk || !PedFeasible(w, c, lo, ld))
        {
            return false;
        }

        if (c.Lots.EuclidM(lo, ld) * 1000L <= c.Cfg.Choice.MaxWalkMm)
        {
            return true;
        }

        if (mode == TravelMode.Car && w.Peds!.FindBusPair(w, c.Lots.PedEdge[lo], c.Lots.PedSMm[lo], c.Lots.PedEdge[ld], c.Lots.PedSMm[ld], out _, out _))
        {
            alt = TravelMode.Bus;
            return true;
        }

        return false;
    }

    /// <summary>Снимает необработанную заявку: машина - поездка из <see cref="PendingTrips"/> (маршрут освобождается); заявка пешехода устаревает сама (<see cref="IsLiveRequest"/>).</summary>
    private static bool CancelRequest(World w, int r)
    {
        PendingTrips trips = w.Trips;
        for (int k = 0; k < trips.Count; k++)
        {
            ref readonly PendingTrip t = ref trips.At(k);
            if (t.ResidentRef == r + 1)
            {
                if (t.HasRoute)
                {
                    w.Routes.Free(new RouteHandle(t.Route, t.RouteGen));
                }

                Parking.ParkingOps.ReleaseRef(w, t.ParkRef, t.ParkGen);   // резерв места снимается вместе с заявкой (§5)
                trips.RemoveAt(k);
                return true;
            }
        }

        return false;
    }

    /// <summary>Тур закончен или отменён, житель дома: следующий тур плана либо план следующих суток.</summary>
    internal static void FinishTour(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        long now = w.Tick;
        res.State[r] = (byte)ResidentState.AtHome;
        res.StateTick[r] = now;
        res.Agent[r] = -1;
        res.AgentGen[r] = 0;
        res.LegDest[r] = -1;
        res.LegStopA[r] = -1;
        res.LegStopB[r] = -1;
        if ((res.Flags[r] & ResidentFlags.Arriving) != 0)
        {
            Market.OnArrived(c, r);   // переселенец дома (ADR-0037): дальше обычный автомат
        }

        if ((res.Flags[r] & ResidentFlags.Leaving) != 0)
        {
            c.Market.Emigrate(w, c, r);   // единственная точка «житель дома, события нет»: отъезд по дедлайну
            return;
        }

        if ((res.Flags[r] & ResidentFlags.Evicted) != 0 && c.Market.TryRelocate(w, c, r, true))
        {
            res.Flags[r] = (byte)(res.Flags[r] & ~ResidentFlags.Evicted);
            res.EvictDays[r] = 0;
        }

        res.PlanCursor[r]++;
        if (res.PlanCursor[r] < res.PlanCount[r])
        {
            int slot = res.PlanSlot[(r * CityResidents.MaxTours) + res.PlanCursor[r]];
            long when = c.TickOfSlot(Calendar.DayOf(c, now), slot);
            CityPhase.SetEvent(w, c, r, when > now ? when : now + 1, ResidentEvent.Depart);
        }
        else
        {
            CityPhase.SetEvent(w, c, r, Calendar.NextPlanTick(c, r, now), ResidentEvent.Plan);
        }
    }

    /// <summary>Обратная нога невозможна ни одним способом: житель переносится домой без наблюдения (учёт <c>res_stuck</c>).</summary>
    internal static void ArriveWithoutLeg(World w, CityWorld c, int r)
    {
        w.Counters.ResStuck++;
        FinishTour(w, c, r);
    }

    // ------------------------------------------------------------------ агент вышел

    /// <summary>Машина жителя поставлена на дорогу (<c>Spawner.TryPlace</c>): ссылки, <c>InLeg</c>, дедлайн снят.</summary>
    internal static void OnVehiclePlaced(World w, int r, int vehicle)
    {
        CityWorld c = w.City!;
        CityResidents res = c.Residents;
        w.Vehicles.Resident[vehicle] = r;
        if (!res.IsAlive(r) || res.State[r] != (byte)ResidentState.WaitDepart || res.LegMode[r] != (byte)TravelMode.Car)
        {
            if (c.Parking != null)
            {
                Parking.ParkingOps.ReleaseRef(w, w.Vehicles.ParkFac[vehicle] + 1, w.Vehicles.ParkFacGen[vehicle]);   // резерв не должен остаться на машине без жителя
                w.Vehicles.ParkFac[vehicle] = -1;
                w.Vehicles.ParkFacGen[vehicle] = 0;
            }

            return;   // страховка: заявку сняли (дедлайн) раньше постановки; машина доедет и закроет ногу без жителя
        }

        if (c.Parking != null)
        {
            Parking.ParkingOps.OnDeparture(w, r);   // место освобождается в момент выезда (§6)
        }

        EnterLeg(w, c, r, vehicle, w.Vehicles.Pool.GenerationOf(vehicle));
    }

    /// <summary>Пешеход жителя запущен (<c>PedWorld.SpawnCityLegs</c>).</summary>
    internal static void OnPedSpawned(World w, int r, int ped) => EnterLeg(w, w.City!, r, ped, w.Peds!.P.Pool.GenerationOf(ped));

    private static void EnterLeg(World w, CityWorld c, int r, int agent, int generation)
    {
        CityResidents res = c.Residents;
        long now = w.Tick;
        w.Counters.ResWaitDepartTicks += now - res.StateTick[r];
        res.LegWait[r] = 0;
        res.Agent[r] = agent;
        res.AgentGen[r] = generation;
        res.State[r] = (byte)ResidentState.InLeg;
        res.StateTick[r] = now;
        CityPhase.SetEvent(w, c, r, -1, ResidentEvent.None);
    }

    /// <summary>Поездка жителя удалена из очереди без постановки (нет пути, некорректная полоса): как дедлайн, но сразу.</summary>
    internal static void OnTripDropped(World w, int r, in PendingTrip trip)
    {
        CityWorld c = w.City!;
        CityResidents res = c.Residents;
        if (c.Parking != null)
        {
            Parking.ParkingOps.ReleaseRef(w, trip.ParkRef, trip.ParkGen);   // заявка уже убрана из очереди вызывающим: резерв снимаем здесь
        }

        if (res.IsAlive(r) && res.State[r] == (byte)ResidentState.WaitDepart && res.LegMode[r] == (byte)TravelMode.Car)
        {
            OnWaitTimeout(w, c, r, true);   // поездка уже убрана из очереди вызывающим: CancelRequest ничего не найдёт и безопасен
        }
    }

    // ------------------------------------------------------------------ прибытие

    /// <summary>
    /// Фаза 8 и <c>QueueMeso.Arrive</c>: машина жителя прибыла (до деспауна). В режиме парковки здесь постановка (<see cref="Parking.ParkingOps.OnArrival"/>): <c>Park</c>, затем либо пеший участок
    /// <c>WalkFromCar</c> (таймер), либо сразу кольцо прибытий.
    /// </summary>
    internal static void OnVehicleArrived(World w, int vehicle)
    {
        int r = w.Vehicles.Resident[vehicle];
        CityWorld? c = w.City;
        if (c != null && (uint)r < (uint)c.Residents.HighWater && c.Residents.IsAlive(r) && c.Residents.State[r] == (byte)ResidentState.InLeg && c.Residents.Agent[r] == vehicle)
        {
            if (c.Parking != null)
            {
                CityResidents res = c.Residents;
                int walkMm = Parking.ParkingOps.OnArrival(w, vehicle, r);
                res.Agent[r] = -1;   // машина сейчас исчезнет: дальше житель идёт пешком либо ждёт кольца
                res.AgentGen[r] = 0;
                if (walkMm > 0)
                {
                    int ticks = Parking.ParkingOps.WalkTicks(c.Parking, walkMm);
                    res.LegStage[r] = LegStages.WalkFromCar;
                    res.LegWalk[r] += ticks;
                    w.Counters.ParkWalkTicks += ticks;
                    CityPhase.SetEvent(w, c, r, w.Tick + ticks, ResidentEvent.AccessWalkDone);
                    return;
                }
            }

            c.Arrivals.Push(r);
        }
        else if (c != null && c.Parking != null)
        {
            Parking.ParkingOps.ReleaseRef(w, w.Vehicles.ParkFac[vehicle] + 1, w.Vehicles.ParkFacGen[vehicle]);   // житель потерян: резерв не оставляем
            w.Vehicles.ParkFac[vehicle] = -1;
        }
    }

    /// <summary>Нога пешехода закончена (<c>PedWorld.Arrive</c>, пешеход ещё жив): накопитель <c>walk</c> = всё время в пути без ожидания и поездки.</summary>
    internal static void OnPedLegEnd(World w, int ped)
    {
        PedStore ps = w.Peds!.P;
        CityWorld c = w.City!;
        int r = ps.Resident[ped];
        if ((uint)r >= (uint)c.Residents.HighWater || !c.Residents.IsAlive(r) || c.Residents.State[r] != (byte)ResidentState.InLeg)
        {
            return;
        }

        CityResidents res = c.Residents;
        long total = w.Tick - ps.TripTick[ped];
        long walk = total - res.LegWait[r] - res.LegRide[r];
        res.LegWalk[r] = (int)(walk < 0 ? 0 : walk);
        c.Arrivals.Push(r);
    }

    /// <summary>Пути пешком нет: нога выполняется «мгновенно» (тур отменён, если нога туда; обратная нога - житель дома), без наблюдения.</summary>
    internal static void OnPedNoPath(World w, int ped)
    {
        CityWorld c = w.City!;
        int r = w.Peds!.P.Resident[ped];
        CityResidents res = c.Residents;
        if ((uint)r >= (uint)res.HighWater || !res.IsAlive(r) || res.State[r] != (byte)ResidentState.InLeg)
        {
            return;
        }

        if (res.LegDest[r] != res.Home[r])
        {
            w.Counters.ResToursUnreachable++;
        }
        else
        {
            w.Counters.ResStuck++;
        }

        FinishTour(w, c, r);
    }

    /// <summary>Вызов <c>BuildRoad</c>: все пешеходы убраны, жители в пути пешком возвращаются в точку выхода (туда - тур отменён, обратно - житель на активности, повторный выезд).</summary>
    internal static void OnPedsReset(World w)
    {
        CityWorld? c = w.City;
        if (c == null)
        {
            return;
        }

        CityResidents res = c.Residents;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (!res.IsAlive(r))
            {
                continue;
            }

            bool pedLeg = res.LegMode[r] != (byte)TravelMode.Car;
            bool inLeg = res.State[r] == (byte)ResidentState.InLeg;
            bool waiting = res.State[r] == (byte)ResidentState.WaitDepart;
            if (!pedLeg || !(inLeg || waiting))
            {
                continue;
            }

            res.Agent[r] = -1;
            res.AgentGen[r] = 0;
            bool outbound = res.LegDest[r] != res.Home[r];
            if (outbound)
            {
                w.Counters.ResToursCancelled++;
                c.Kpi.OnTourCancelled(c, r);   // случай TourCancelled и жалоба у дома (progression.md §6.1)
                FinishTour(w, c, r);
            }
            else
            {
                int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
                res.State[r] = (byte)(res.PlanKind[tour] == TourKind.Work ? ResidentState.AtWork : ResidentState.AtShop);
                res.StateTick[r] = w.Tick;
                res.LegDest[r] = -1;
                CityPhase.SetEvent(w, c, r, w.Tick, ResidentEvent.EndActivity);   // команды идут до фазы 2r: событие этого тика ещё впереди
            }
        }
    }

    /// <summary>Фаза 9r: нога жителя закончена - наблюдение в скимы, состояние по типу цели, событие плана.</summary>
    internal static void OnLegArrived(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if (!res.IsAlive(r) || res.State[r] != (byte)ResidentState.InLeg)
        {
            return;
        }

        var mode = (TravelMode)res.LegMode[r];
        long total = w.Tick - res.LegReq[r];
        if (c.Parking != null && mode == TravelMode.Car)
        {
            Parking.ParkingOps.RecordSearchTime(w, Math.Max(0, res.LegParkWait[r]));   // ожидание места и поиск этой ноги (§11)
        }

        CompleteLeg(w, c, r, LegCostDs(c, mode, total, res.LegWait[r], res.LegWalk[r], res.LegRide[r]));
    }

    /// <summary>Фаза 2r: прибытие ноги агрегата (событие <see cref="ResidentEvent.Arrive"/>); стоимость - всё время ноги в децисекундах (модель уже дала обобщённое время).</summary>
    internal static void OnAggregateArrived(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if (!res.IsAlive(r) || res.State[r] != (byte)ResidentState.InLeg)
        {
            return;
        }

        CompleteLeg(w, c, r, Calendar.TicksToDs(w.Tick - res.LegReq[r]));
    }

    /// <summary>Нога закончена со стоимостью <paramref name="cost"/> (дс): наблюдение в скимы, загрузка клеток (машина), состояние по типу цели, событие плана. Общий код micro и aggregate.</summary>
    private static void CompleteLeg(World w, CityWorld c, int r, int cost)
    {
        CityResidents res = c.Residents;
        long now = w.Tick;
        var mode = (TravelMode)res.LegMode[r];
        int period = c.Skims.PeriodOf(res.LegReq[r], c.DayTicks);
        if (c.Skims.Observe(res.LegO[r], res.LegD[r], mode, period, cost))
        {
            w.Counters.SkimObserved++;
        }
        else
        {
            w.Counters.SkimObsDropped++;
        }

        if (mode == TravelMode.Car)
        {
            c.Loads.AddCarLeg(c.Grid.CellsX, res.LegO[r], res.LegD[r], period, c.Day);   // L[c, p] для модели BPR (§9.1)
        }

        int skim = c.Skims.Get(res.LegO[r], res.LegD[r], mode, period);
        long limit = Math.Max(3L * skim, skim + 18_000L);
        if (skim > 0 && cost > limit)
        {
            w.Counters.ResLegOverdue++;
        }

        w.Counters.ResLegsDone++;
        w.Counters.ResLegCostDsTotal += cost;
        int slot = ((Calendar.DayOf(c, now) & 1) * Skims3) + (int)mode;
        c.LegsDay[slot]++;
        c.LegCostDay[slot] += cost;

        if (mode == TravelMode.Bus && c.Budget != null)
        {
            BudgetFlows.ChargeFare(w, c, r);   // плата за проезд на закрытии ноги, micro и aggregate по одному пути (city-economy.md §3)
        }

        res.Agent[r] = -1;
        res.AgentGen[r] = 0;
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        bool returning = res.LegDest[r] == res.Home[r];
        c.Kpi.OnLeg(mode, Calendar.TicksToDs(now - res.LegReq[r]), !returning && res.PlanKind[tour] == TourKind.Work, skim, res.Home[r], res.Home[r] >= 0 ? c.Buildings.Pool.GenerationOf(res.Home[r]) : 0);   // показатели (progression.md §2.3): от заявки до прибытия
        if (returning)
        {
            if (c.Parking != null && res.CarWalkMm[r] < 0)
            {
                Parking.ParkingOps.OnReturnedHome(c, r);   // машину эвакуировали домой, пока житель был в туре: ходьба от двери до неё снова известна (§7)
            }

            FinishTour(w, c, r);
            return;
        }

        res.StateTick[r] = now;
        if (res.PlanKind[tour] == TourKind.Work)
        {
            res.State[r] = (byte)ResidentState.AtWork;
            int firm = res.PlanDest[tour];   // фирма тура: работу могли сменить или потерять в пути
            int endSlot = ((res.Seq[r] % 7) - 3) + (OutsideRef.Is(firm) ? OutsideState.ShiftStartSlot + OutsideState.ShiftSlots
                : c.Types.Rows[c.Buildings.Type[firm]].ShiftStartSlot + c.Types.Rows[c.Buildings.Type[firm]].ShiftSlots);
            long end = c.TickOfSlot(Calendar.DayOf(c, res.LegReq[r]), endSlot);
            CityPhase.SetEvent(w, c, r, end > now ? end : now + 1, ResidentEvent.EndActivity);
        }
        else
        {
            res.State[r] = (byte)ResidentState.AtShop;
            w.Counters.ResVisits++;
            Economy.Visit(w, c, r, res.PlanDest[tour]);
            CityPhase.SetEvent(w, c, r, now + ((long)Math.Max(1, c.Cfg.Residents.ShopSlots) * c.SlotTicks), ResidentEvent.EndActivity);
        }
    }

    private const int Skims3 = Demand.Skims.Modes;

    /// <summary>
    /// Обобщённая стоимость ноги, децисекунды (§5.2, §6.1): <c>ride + (w_wait · wait &gt;&gt; 8) + (w_walk · walk &gt;&gt; 8)</c>. Машина и пешая нога - всё время от заявки (включая <c>WaitDepart</c>) это
    /// время в самом способе (<c>ride</c>, без весов: в априорной оценке §5.4 пешком - то же время); автобус - ожидание на остановке, поездка и пеший хвост (подход, отход, включая <c>WaitDepart</c>).
    /// </summary>
    internal static int LegCostDs(CityWorld c, TravelMode mode, long totalTicks, int waitTicks, int walkTicks, int rideTicks)
    {
        if (mode != TravelMode.Bus)
        {
            return Calendar.TicksToDs(totalTicks);
        }

        long walk = totalTicks - waitTicks - rideTicks;
        if (walk < 0)
        {
            walk = 0;
        }

        long ds = Calendar.TicksToDs(rideTicks) + (((long)c.Cfg.Choice.WWaitQ8 * Calendar.TicksToDs(waitTicks)) >> 8) + (((long)c.Cfg.Choice.WWalkQ8 * Calendar.TicksToDs(walk)) >> 8);
        return (int)Math.Min(ds, int.MaxValue);
    }

    // ------------------------------------------------------------------ хуки остановок (Peds/Stops.cs)

    internal static int StopA(World w, int r) => (uint)r < (uint)w.City!.Residents.HighWater ? w.City.Residents.LegStopA[r] : -1;

    internal static int StopB(World w, int r) => (uint)r < (uint)w.City!.Residents.HighWater ? w.City.Residents.LegStopB[r] : -1;

    /// <summary>Посадка: накопитель ожидания на остановке.</summary>
    internal static void OnBoarded(World w, int r, long waitTicks)
    {
        if ((uint)r < (uint)w.City!.Residents.HighWater)
        {
            w.City.Residents.LegWait[r] += (int)waitTicks;
            int stop = w.City.Residents.LegStopA[r];
            int stopGen = (uint)stop < (uint)w.Net.BusStopCount ? w.Net.BusStops[stop].Generation : 0;
            w.City.Kpi.OnBoarded(waitTicks, stop, stopGen);   // случай LongStopWait и жалоба у остановки (progression.md §6.1)
        }
    }

    /// <summary>Высадка: накопитель поездки (тики на борту).</summary>
    internal static void OnAlighted(World w, int r, long rideTicks)
    {
        if ((uint)r < (uint)w.City!.Residents.HighWater)
        {
            w.City.Residents.LegRide[r] += (int)rideTicks;
        }
    }

    /// <summary>Точка назначения текущей ноги на тротуаре; false - у участка нет точки.</summary>
    internal static bool DestPoint(World w, int r, out int edge, out int sMm)
    {
        CityWorld c = w.City!;
        edge = -1;
        sMm = 0;
        if ((uint)r >= (uint)c.Residents.HighWater || c.Residents.LegDest[r] < 0)
        {
            return false;   // нет ноги или нога к внешнему подключению (только машиной)
        }

        int lot = c.Buildings.Lot[c.Residents.LegDest[r]];
        edge = c.Lots.PedEdge[lot];
        sMm = c.Lots.PedSMm[lot];
        return edge >= 0;
    }

    // ------------------------------------------------------------------ заявки и восстановление

    /// <summary>Заявка в FIFO ещё нужна: житель ждёт именно её (тот же <c>LegReq</c>, нет агента, пешая нога).</summary>
    internal static bool IsLiveRequest(World w, in LegRequest req)
    {
        CityResidents res = w.City!.Residents;
        int r = req.Resident;
        return (uint)r < (uint)res.HighWater && res.IsAlive(r) && res.State[r] == (byte)ResidentState.WaitDepart && res.LegReq[r] == req.ReqTick && res.Agent[r] == -1 &&
               res.LegMode[r] == req.Mode;
    }

    /// <summary>После загрузки: FIFO заявок пешеходов из жителей <c>WaitDepart</c> с пешей ногой, порядок (<c>LegReq</c>, индекс) - тот же, что давала живая очередь (заявки подаются событиями колеса по возрастанию индекса).</summary>
    internal static void RestoreDerived(World w)
    {
        PedWorld? pw = w.Peds;
        CityWorld c = w.City!;
        if (pw == null || !pw.CitySource)
        {
            return;
        }

        pw.LegRequests.Clear();
        CityResidents res = c.Residents;
        int n = 0;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (res.Pool.IsAlive(r) && res.State[r] == (byte)ResidentState.WaitDepart && res.LegMode[r] != (byte)TravelMode.Car && res.EvKind[r] == ResidentEvent.Deadline)
            {
                n++;
            }
        }

        if (n == 0)
        {
            return;
        }

        var keys = new long[n];
        int m = 0;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (res.Pool.IsAlive(r) && res.State[r] == (byte)ResidentState.WaitDepart && res.LegMode[r] != (byte)TravelMode.Car && res.EvKind[r] == ResidentEvent.Deadline)
            {
                keys[m++] = (res.LegReq[r] << 24) + r;
            }
        }

        Array.Sort(keys);
        for (int k = 0; k < n; k++)
        {
            int r = (int)(keys[k] & ((1L << 24) - 1));
            int dest = res.LegDest[r];
            bool outbound = dest != res.Home[r];
            int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
            int lo = c.Buildings.Lot[outbound ? res.Home[r] : res.PlanDest[tour]];
            int ld = c.Buildings.Lot[dest];
            pw.RequestLeg(r, res.LegReq[r], c.Lots.PedEdge[lo], c.Lots.PedSMm[lo], c.Lots.PedEdge[ld], c.Lots.PedSMm[ld], (TravelMode)res.LegMode[r], res.LegStopA[r], res.LegStopB[r]);
        }
    }
}
