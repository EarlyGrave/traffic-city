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
        int lo = c.Buildings.Lot[origin];
        int ld = c.Buildings.Lot[dest];
        long now = w.Tick;
        int stopA = -1;
        int stopB = -1;
        if (!Resolve(w, c, lo, ld, ref mode, out stopA, out stopB))
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

        ArmRequest(w, c, r, outbound, Submit(w, c, r) == Begin.Submitted);
        return true;
    }

    /// <summary>Способ ноги между участками: машина (полосы есть), автобус (пара остановок), пешком (пешеходы города, одна пешеходная компонента). false - ничего не доступно.</summary>
    private static bool Resolve(World w, CityWorld c, int lo, int ld, ref TravelMode mode, out int stopA, out int stopB)
    {
        stopA = -1;
        stopB = -1;
        if (mode == TravelMode.Car && (c.Lots.CarLane[lo] < 0 || c.Lots.CarLane[ld] < 0 || c.Lots.RoadComp[lo] != c.Lots.RoadComp[ld]))
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
        int lo = c.Buildings.Lot[origin];
        int ld = c.Buildings.Lot[dest];
        var mode = (TravelMode)res.LegMode[r];
        if (mode == TravelMode.Car)
        {
            if (w.Trips.IsFull)
            {
                return Begin.Backlog;
            }

            int type = CarType(w);
            ref readonly VehicleTypeDesc t = ref w.Types[type];
            float destS = MathF.Min(c.Lots.CarS[ld], MathF.Max(0f, w.Net.Lanes[c.Lots.CarLane[ld]].Length - t.MinGap));
            var trip = new PendingTrip
            {
                FromZone = -1,
                ToZone = -1,
                FromLane = c.Lots.CarLane[lo],
                FromS = 0f,   // машина встаёт в начало полосы участка (как у зон): ближе к узлу активные множества ворот не успевают её увидеть (ADR-0019)
                Type = (byte)type,
                DestLane = c.Lots.CarLane[ld],
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

    /// <summary>Событие жителя после подачи заявки: дедлайн <c>LegReq + wait_max</c> либо повтор через тик (очередь полна).</summary>
    private static void ArmRequest(World w, CityWorld c, int r, bool outbound, bool submitted)
    {
        CityResidents res = c.Residents;
        if (submitted)
        {
            CityPhase.SetEvent(w, c, r, res.LegReq[r] + c.Cfg.Residents.WaitMaxTicks, ResidentEvent.Deadline);
        }
        else
        {
            CityPhase.SetEvent(w, c, r, w.Tick + 1, outbound ? ResidentEvent.Depart : ResidentEvent.EndActivity);
        }
    }

    /// <summary>Повтор заявки жителя в <c>WaitDepart</c> (очередь была полна) либо дедлайн, если ждёт дольше <c>wait_max</c>.</summary>
    internal static void RetryRequest(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if (w.Tick >= res.LegReq[r] + c.Cfg.Residents.WaitMaxTicks)
        {
            OnWaitTimeout(w, c, r);
            return;
        }

        bool outbound = res.LegDest[r] != res.Home[r];
        ArmRequest(w, c, r, outbound, Submit(w, c, r) == Begin.Submitted);
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
    /// Ожидание агента дольше <c>wait_max_s</c> (или заявка потеряна: нет пути): другой доступный способ, иначе тур отменяется и житель остаётся дома; обратная нога ждёт дальше (<c>res_stuck++</c>).
    /// </summary>
    internal static void OnWaitTimeout(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        CancelRequest(w, r);
        w.Counters.ResWaitDepartTicks += w.Tick - res.StateTick[r];
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        bool outbound = res.LegDest[r] != res.Home[r];
        var mode = (TravelMode)res.LegMode[r];
        int origin = outbound ? res.Home[r] : res.PlanDest[tour];
        int dest = res.LegDest[r];
        int lo = c.Buildings.Lot[origin];
        int ld = c.Buildings.Lot[dest];
        if (Alternative(w, c, mode, lo, ld, out TravelMode alt) && StartLeg(w, c, r, outbound, alt))
        {
            return;
        }

        if (outbound)
        {
            w.Counters.ResToursCancelled++;
            FinishTour(w, c, r);
            return;
        }

        w.Counters.ResStuck++;   // обратная нога без агента дольше дедлайна: ждём дальше (норма 0)
        if (!StartLeg(w, c, r, false, mode))
        {
            ArriveWithoutLeg(w, c, r);
        }
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
    private static void CancelRequest(World w, int r)
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

                trips.RemoveAt(k);
                return;
            }
        }
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
            return;   // страховка: заявку сняли (дедлайн) раньше постановки; машина доедет и закроет ногу без жителя
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
    internal static void OnTripDropped(World w, int r)
    {
        CityWorld c = w.City!;
        CityResidents res = c.Residents;
        if (res.IsAlive(r) && res.State[r] == (byte)ResidentState.WaitDepart && res.LegMode[r] == (byte)TravelMode.Car)
        {
            OnWaitTimeout(w, c, r);   // поездка уже убрана из очереди вызывающим: CancelRequest ничего не найдёт и безопасен
        }
    }

    // ------------------------------------------------------------------ прибытие

    /// <summary>Фаза 8: машина жителя прибыла (до деспауна) - житель в кольцо прибытий.</summary>
    internal static void OnVehicleArrived(World w, int vehicle)
    {
        int r = w.Vehicles.Resident[vehicle];
        CityWorld? c = w.City;
        if (c != null && (uint)r < (uint)c.Residents.HighWater && c.Residents.IsAlive(r) && c.Residents.State[r] == (byte)ResidentState.InLeg && c.Residents.Agent[r] == vehicle)
        {
            c.Arrivals.Push(r);
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

        res.Agent[r] = -1;
        res.AgentGen[r] = 0;
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        bool returning = res.LegDest[r] == res.Home[r];
        if (returning)
        {
            FinishTour(w, c, r);
            return;
        }

        res.StateTick[r] = now;
        if (res.PlanKind[tour] == TourKind.Work)
        {
            res.State[r] = (byte)ResidentState.AtWork;
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[c.Buildings.Type[res.PlanDest[tour]]];   // фирма тура: работу могли сменить или потерять в пути
            int endSlot = t.ShiftStartSlot + ((res.Seq[r] % 7) - 3) + t.ShiftSlots;
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
            return false;
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
