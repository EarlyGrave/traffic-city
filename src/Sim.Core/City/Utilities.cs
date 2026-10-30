using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Demand;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Выездные службы (building-lifecycle.md §2.10, services.md §2.3-§2.4): заявки ЖКХ в стадии «здания», общий диспетчер по кольцам записей (фаза 9r, тики кратные <see cref="Step"/>: сначала все
/// срочные кольца по индексу записи, затем обычные), станция - минимум оценки времени в пути по скиму, связь машины с бригадой через <c>VehicleStore.Resident = -2 - crew</c>, ремонт до потолка
/// из фонда <c>Utility</c>, приоритет <c>OnCall</c> по признакам вызова (у ЖКХ - никогда), отклик, возврат, агрегатный режим (<c>DueTick</c> по скиму), расходы на границе суток.
/// Всё - в последовательных фазах главного потока; 0 Б/тик.
/// </summary>
internal static class Utilities
{
    /// <summary>Шаг выезда и проверки <c>DueTick</c> бригад, тиков (раз в секунду).</summary>
    public const int Step = 20;

    /// <summary>Заявка - при <c>Ceil - Cond</c> не меньше этого, б.п.</summary>
    public const int MinGapBp = 500;

    /// <summary>Скорость оценки поездки без скима, м/с (Manhattan).</summary>
    private const int FallbackSpeedMs = 10;

    // ------------------------------------------------------------------ BuildService

    /// <summary>
    /// Создаёт станцию на участке (после валидатора): здание <c>Active</c> без стройки, без мест, бригады - первые свободные слоты. Без режима - депо BL (бригад = <c>Vehicles</c> типа, все на линии);
    /// в режиме <c>city.services</c> - слотов <c>UnitsMax</c>, на линии <c>Duty</c> по ползунку записи, остальные <c>OffDuty</c>. false - нет места в пуле зданий.
    /// </summary>
    public static bool BuildDepot(World w, CityWorld c, int lot, int type)
    {
        CityServices sv = c.Services!;
        CrewStore u = sv.Crews;
        if (!sv.CanAllocSite(c.Buildings) || !c.Buildings.TryCreate(lot, type, BuildingState.Active, w.Tick, out int bi))
        {
            return false;
        }

        BuildingStore b = c.Buildings;
        CityLife.OnCreate(c, bi);
        b.Jobs[bi] = 0;
        c.Lots.Building[lot] = bi;
        c.Lots.Version++;
        b.Version++;
        int site = sv.AllocSite(b, bi, type);   // станция записи (services.md §2.3)
        int need;
        int duty;
        if (sv.Table.Explicit)
        {
            need = UnitsMax(sv, site);
            duty = DutyOf(sv, site);
        }
        else
        {
            need = c.Types.Rows[type].Vehicles;
            duty = need;
        }

        sv.Sites.UnitsMax[site] = (byte)need;
        sv.Sites.Duty[site] = (byte)duty;
        for (int k = 0; k < u.Capacity && need > 0; k++)
        {
            if (u.State[k] == CrewState.Free)
            {
                u.Reset(k);
                u.Site[k] = site;
                u.State[k] = duty > 0 ? CrewState.Idle : CrewState.OffDuty;
                duty--;
                need--;
            }
        }

        w.Counters.BuildingsBuilt++;
        return true;
    }

    /// <summary>Бригад, нужных станции типа <paramref name="type"/> при постройке: в режиме - <c>UnitsMax</c> уровня, иначе <c>Vehicles</c> типа.</summary>
    public static int CrewsFor(CityWorld c, int type)
    {
        CityServices sv = c.Services!;
        int rec = sv.Table.RecOf(type);
        if (!sv.Table.Explicit || rec < 0)
        {
            return c.Types.Rows[type].Vehicles;
        }

        ref readonly ServiceLevelDesc l = ref sv.Table.LevelRow(rec, sv.Table.LevelOf(type));
        return UnitsMaxOf(l.Units, 0);
    }

    /// <summary><c>units'</c> станции (§2.4): <c>units</c> уровня + апгрейды <c>units</c>.</summary>
    public static int UnitsNow(CityServices sv, int site)
    {
        int rec = sv.Sites.Svc[site];
        ref readonly ServiceLevelDesc l = ref sv.Table.LevelRow(rec, sv.Sites.Level[site]);
        return l.Units + sv.Table.UpgradeEffect(in l, sv.Sites.UpgradeMask[site], ServiceUpgradeEffect.Units);
    }

    private static int UnitsMaxOf(int units, int upgrades) => Math.Min(255, units + upgrades + ((units + 1) / 2));

    /// <summary><c>UnitsMax = units + апгрейды + ceil(units / 2)</c> (гараж под 150 %, §2.4).</summary>
    public static int UnitsMax(CityServices sv, int site)
    {
        int rec = sv.Sites.Svc[site];
        ref readonly ServiceLevelDesc l = ref sv.Table.LevelRow(rec, sv.Sites.Level[site]);
        return UnitsMaxOf(l.Units, sv.Table.UpgradeEffect(in l, sv.Sites.UpgradeMask[site], ServiceUpgradeEffect.Units));
    }

    /// <summary><c>Duty = clamp(round(units' · pct / 100), 1, UnitsMax)</c> (§2.4); без машин у уровня - 0.</summary>
    public static int DutyOf(CityServices sv, int site)
    {
        int units = UnitsNow(sv, site);
        int max = UnitsMax(sv, site);
        if (units <= 0 || max <= 0)
        {
            return 0;
        }

        int pct = sv.Pct[sv.Sites.Svc[site]];
        return Math.Clamp(((units * pct) + 50) / 100, 1, max);
    }

    /// <summary>Станция перестала работать (заброшена или удаляется): свободные, снятые с линии и работающие на месте бригады распускаются; едущие - по прибытии.</summary>
    public static void OnDepotLost(CityWorld c, int bi)
    {
        CityServices? sv = c.Services;
        if (sv == null)
        {
            return;
        }

        CrewStore u = sv.Crews;
        int gen = c.Buildings.Pool.GenerationOf(bi);
        for (int k = 0; k < u.Capacity; k++)
        {
            if (sv.SiteBuilding(k) != bi || sv.SiteGen(k) != gen)
            {
                continue;
            }

            if (u.State[k] == CrewState.Working)
            {
                ClearRequested(c, u.Target[k], u.TargetGen[k]);
                u.Reset(k);
            }
            else if (u.State[k] is CrewState.Idle or CrewState.OffDuty)
            {
                u.Reset(k);
            }
        }
    }

    // ------------------------------------------------------------------ вызовы

    /// <summary>
    /// Вызов в кольцо записи <paramref name="rec"/> (срочное или обычное - по флагу <c>urgent</c> вида вызова): цель - здание <paramref name="target"/>, ссылка <paramref name="refIdx"/> (житель
    /// или пожар; -1 - нет). false - у записи нет такого вызова или кольцо полно.
    /// </summary>
    public static bool Call(World w, CityWorld c, int rec, ServiceCallKind kind, int target, int refIdx = -1, int refGen = 0)
    {
        CityServices sv = c.Services!;
        int ci = sv.Table.CallOf(rec, (int)kind);
        if (ci < 0 || !c.Buildings.IsAlive(target))
        {
            return false;
        }

        var q = new ServiceCall
        {
            Target = target,
            TargetGen = c.Buildings.Pool.GenerationOf(target),
            Ref = refIdx,
            RefGen = refGen,
            Tick = w.Tick,
            Kind = (byte)kind,
            Svc = (byte)rec,
        };
        RingBuffer<ServiceCall> ring = (sv.Table.Calls[ci].Flags & ServiceCallFlags.Urgent) != 0 ? sv.Urgent[rec] : sv.Normal[rec];
        if (!ring.TryEnqueue(in q))
        {
            return false;
        }

        sv.RecCallsQueued[rec]++;
        w.Counters.CallsQueued++;
        return true;
    }

    /// <summary>
    /// Заявка на ремонт (стадия «здания», после износа): <c>Active</c>, <c>Cond &lt; repair_request_bp</c>, <c>Ceil - Cond &gt;= 500</c>, <c>Cond &gt;= 50 %</c>, флага <c>Requested</c> нет -
    /// в очередь. Очередь полна - повтор завтра.
    /// </summary>
    public static void Request(World w, CityWorld c, int bi)
    {
        CityServices? sv = c.Services;
        BuildingStore b = c.Buildings;
        if (sv == null || sv.Table.Utility < 0 || b.State[bi] != (byte)BuildingState.Active || (b.LifeFlags[bi] & BuildingLifeFlags.Requested) != 0)
        {
            return;
        }

        int cond = b.Cond[bi];
        if (cond >= c.Cfg.Lifecycle.RepairRequestBp || cond < CityLife.AbandonBp || b.Ceil[bi] - cond < MinGapBp)
        {
            return;
        }

        if (Call(w, c, sv.Table.Utility, ServiceCallKind.Repair, bi))
        {
            b.LifeFlags[bi] |= BuildingLifeFlags.Requested;   // Version не трогаем: смена флага не меняет набор зданий для рынков и скимов
        }
    }

    /// <summary>Цена ремонта (§2.10): <c>(Ceil - Cond) · Capacity · repair_per_unit / 10 000</c>.</summary>
    public static long RepairCost(CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        long gap = Math.Max(0, b.Ceil[bi] - b.Cond[bi]);
        return gap * c.Types.Rows[b.Type[bi]].Capacity * c.Cfg.Lifecycle.RepairPerUnit / CityLife.Full;
    }

    // ------------------------------------------------------------------ фаза 9r

    /// <summary>Фаза 9r, каждый тик (до прибытий жителей): разбор <c>CrewArrivals</c>; на тиках, кратных <see cref="Step"/>, - сроки бригад, затем выезд.</summary>
    public static void Tick(World w, CityWorld c)
    {
        CityServices sv = c.Services!;
        CrewStore u = sv.Crews;
        while (u.Arrivals.TryDequeue(out int k))
        {
            Arrive(w, c, sv, k);
        }

        if (w.Tick % Step != 0)
        {
            return;
        }

        long now = w.Tick;
        for (int k = 0; k < u.Capacity; k++)
        {
            byte st = u.State[k];
            if (st is CrewState.Free or CrewState.Idle or CrewState.OffDuty || u.DueTick[k] > now)
            {
                continue;
            }

            if (st == CrewState.Working)
            {
                FinishWork(w, c, sv, k);
            }
            else if (c.Exec == TripExecution.Aggregate)
            {
                Arrive(w, c, sv, k);   // агрегат: машин нет, DueTick - прибытие по скиму
            }
            else
            {
                Timeout(w, c, sv, k);
            }
        }

        Dispatch(w, c, sv);
    }

    /// <summary>Ближайший тик (не раньше <paramref name="tick"/>), на котором службы что-то делают; <c>long.MaxValue</c> - ничего (для прыжков агрегата).</summary>
    public static long NextWorkTick(CityWorld c, long tick)
    {
        CityServices? sv = c.Services;
        if (sv == null)
        {
            return long.MaxValue;
        }

        CrewStore u = sv.Crews;
        if (!u.Arrivals.IsEmpty)
        {
            return tick;
        }

        long next = AnyCalls(sv) ? tick : long.MaxValue;
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.State[k] is not (CrewState.Free or CrewState.Idle or CrewState.OffDuty))
            {
                next = Math.Min(next, Math.Max(tick, u.DueTick[k]));
            }
        }

        if (next == long.MaxValue)
        {
            return next;
        }

        long r = next % Step;
        return r == 0 ? next : next + (Step - r);
    }

    private static bool AnyCalls(CityServices sv)
    {
        for (int r = 0; r < sv.Table.Count; r++)
        {
            if (!sv.Normal[r].IsEmpty || !sv.Urgent[r].IsEmpty)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Выезд (§2.3): до <c>max_dispatch_per_tick</c> за тик - сначала срочные кольца по индексу записи, затем обычные. Голова кольца: проверка цели и срока, выбор станции, поездка.
    /// Нет свободной машины ни на одной станции записи в компоненте - вызов остаётся головой, диспетчер идёт к следующему кольцу; фонд ЖКХ пуст или очередь поездок полна - выезд в следующий шаг.
    /// </summary>
    private static void Dispatch(World w, CityWorld c, CityServices sv)
    {
        if (!AnyCalls(sv))
        {
            return;
        }

        FillIdle(sv);
        c.Skims.Prepare(w);
        int sent = 0;
        int max = c.Cfg.Lifecycle.MaxDispatchPerTick;
        for (int pass = 0; pass < 2 && sent < max; pass++)
        {
            for (int r = 0; r < sv.Table.Count && sent < max; r++)
            {
                RingBuffer<ServiceCall> ring = pass == 0 ? sv.Urgent[r] : sv.Normal[r];
                if (!DispatchRing(w, c, sv, ring, ref sent, max))
                {
                    return;
                }
            }
        }
    }

    /// <summary>Разбор одного кольца. false - выезд в этом шаге прекращается целиком (очередь поездок полна).</summary>
    private static bool DispatchRing(World w, CityWorld c, CityServices sv, RingBuffer<ServiceCall> ring, ref int sent, int max)
    {
        CrewStore u = sv.Crews;
        BuildingStore b = c.Buildings;
        Ledger lg = c.Ledger;
        while (sent < max && ring.TryPeek(out ServiceCall req))
        {
            int bi = req.Target;
            bool repair = req.Kind == (byte)ServiceCallKind.Repair;
            if (!ValidTarget(c, req.Kind, bi, req.TargetGen))
            {
                ring.TryDequeue(out _);
                ClearRequested(c, bi, req.TargetGen);   // снесено, заброшено или уже в норме: заявка снята
                if (req.Kind == (byte)ServiceCallKind.Critical)
                {
                    Illness.Lose(w, c, req.Ref, req.RefGen);   // дом снесён: ехать некуда (житель уже выселен - ничего)
                }

                continue;
            }

            if (Late(c, sv, req.Svc, req.Kind, req.Tick, w.Tick))
            {
                ring.TryDequeue(out _);
                OnLate(w, c, sv, req.Svc, req.Kind, req.Ref, req.RefGen);   // ждал дольше deadline_s: последствия вызова (SR3/SR4), вызов снят
                continue;
            }

            int k = PickCrew(w, c, sv, req.Svc, req.Kind, b.Lot[bi], out bool anyStation);
            if (k < 0)
            {
                if (anyStation)
                {
                    break;   // все машины записи в компоненте заняты: голова FIFO ждёт (перегруз, §2.3.1)
                }

                ring.TryDequeue(out _);
                ClearRequested(c, bi, req.TargetGen);
                if (repair)
                {
                    w.Counters.RepairNoDepot++;
                }
                else
                {
                    OnLate(w, c, sv, req.Svc, req.Kind, req.Ref, req.RefGen);   // некому ехать (нет станции с ролью в компоненте): как опоздание
                }

                continue;
            }

            if (repair && sv.Table.Records[req.Svc].Fund == ServiceFund.Utility)
            {
                long price = RepairCost(c, bi);
                if (lg.Utility < price)
                {
                    if (!Subsidy(c))
                    {
                        break;   // фонд пуст: заявка ждёт денег (решение владельца §9.4 В), голова кольца остаётся; прочие кольца - дальше
                    }

                    lg.Transfer(lg.CityAccount, lg.UtilityAccount, price - lg.Utility, FlowKind.UtilitySubsidy);
                }
            }

            int depotLot = b.Lot[sv.SiteBuilding(k)];
            int siteLot = b.Lot[bi];
            if (c.Exec == TripExecution.Aggregate)
            {
                u.DueTick[k] = w.Tick + TravelTicks(w, c, depotLot, siteLot);
            }
            else
            {
                if (w.Trips.IsFull)
                {
                    return false;   // очередь поездок полна: выезд в следующий шаг
                }

                if (!Submit(w, c, sv, k, depotLot, siteLot))
                {
                    ring.TryDequeue(out _);
                    ClearRequested(c, bi, req.TargetGen);   // нет полос у участка: заявка снята, как без депо
                    if (repair)
                    {
                        w.Counters.RepairNoDepot++;
                    }
                    else
                    {
                        OnLate(w, c, sv, req.Svc, req.Kind, req.Ref, req.RefGen);
                    }

                    continue;
                }

                u.DueTick[k] = w.Tick + TimeoutTicks(w, c, depotLot, siteLot);
            }

            ring.TryDequeue(out _);
            u.State[k] = CrewState.ToSite;
            u.Target[k] = bi;
            u.TargetGen[k] = req.TargetGen;
            u.SiteLot[k] = siteLot;
            u.Vehicle[k] = -1;
            u.VehicleGen[k] = 0;
            u.ReqTick[k] = req.Tick;
            u.Kind[k] = req.Kind;
            u.Ref[k] = req.Ref;
            u.RefGen[k] = req.RefGen;
            NextIdle(sv, u.Site[k], k);
            sent++;
        }

        return true;
    }

    /// <summary>Цель вызова годна: ремонт - живое, то же поколение, <c>Active</c>, <c>50 % &lt;= Cond &lt; Ceil</c>; прочие виды - живое здание того же поколения.</summary>
    private static bool ValidTarget(CityWorld c, int kind, int bi, int gen)
    {
        BuildingStore b = c.Buildings;
        if (!b.Pool.IsAlive(bi, gen))
        {
            return false;
        }

        return kind != (int)ServiceCallKind.Repair || (b.State[bi] == (byte)BuildingState.Active && b.Cond[bi] >= CityLife.AbandonBp && b.Cond[bi] < b.Ceil[bi]);
    }

    /// <summary>Срок вызова (§2.3.1): <c>deadline_s</c> записи для вида вызова пройден к тику <paramref name="now"/>; 0 - без срока.</summary>
    private static bool Late(CityWorld c, CityServices sv, int rec, int kind, long callTick, long now)
    {
        int ci = sv.Table.CallOf(rec, kind);
        int dl = ci >= 0 ? sv.Table.Calls[ci].DeadlineS : 0;
        return dl > 0 && now - callTick > (long)dl * Simulation.TicksPerSecond;
    }

    /// <summary>Опоздание вызова: счётчики; последствия по виду - critical: исход §10.3 (<see cref="Illness.Lose"/>, SR3); fire - SR4.</summary>
    private static void OnLate(World w, CityWorld c, CityServices sv, int rec, int kind, int refIdx, int refGen)
    {
        sv.RecCallsLate[rec]++;
        w.Counters.CallsLate++;
        if (kind == (int)ServiceCallKind.Critical)
        {
            Illness.Lose(w, c, refIdx, refGen);
        }
    }

    /// <summary>Станция работает: живое, то же поколение, <c>Active</c>.</summary>
    private static bool DepotOk(CityWorld c, int depot, int gen) =>
        c.Buildings.Pool.IsAlive(depot, gen) && c.Buildings.State[depot] == (byte)BuildingState.Active;

    /// <summary>Первая свободная бригада на линии каждой станции (<see cref="CityServices.IdleScratch"/>), один проход по бригадам.</summary>
    private static void FillIdle(CityServices sv)
    {
        CrewStore u = sv.Crews;
        int[] idle = sv.IdleScratch;
        Array.Fill(idle, -1);
        for (int k = u.Capacity - 1; k >= 0; k--)
        {
            if (u.State[k] == CrewState.Idle && (uint)u.Site[k] < (uint)idle.Length)
            {
                idle[u.Site[k]] = k;
            }
        }
    }

    /// <summary>Бригада <paramref name="k"/> станции <paramref name="site"/> уехала: следующая свободная той же станции.</summary>
    private static void NextIdle(CityServices sv, int site, int k)
    {
        CrewStore u = sv.Crews;
        int next = -1;
        for (int j = k + 1; j < u.Capacity && next < 0; j++)
        {
            next = u.State[j] == CrewState.Idle && u.Site[j] == site ? j : -1;
        }

        sv.IdleScratch[site] = next;
    }

    /// <summary>
    /// Станция для вызова (§2.3, решение владельца 10.2 А): среди работающих станций записи <paramref name="rec"/> с ролью вызова в той же дорожной компоненте и со свободной машиной на
    /// линии - минимум оценки времени в пути по скиму (<see cref="EstDs"/>); равенство - меньший индекс станции. Возвращает бригаду или -1.
    /// <paramref name="anyStation"/> - в компоненте есть такая станция с бригадами (тогда вызов ждёт, а не снимается).
    /// </summary>
    private static int PickCrew(World w, CityWorld c, CityServices sv, int rec, int kind, int targetLot, out bool anyStation)
    {
        anyStation = false;
        LotStore l = c.Lots;
        SiteStore s = sv.Sites;
        int comp = l.RoadComp[targetLot];
        if (comp < 0 || l.CarLane[targetLot] < 0)
        {
            return -1;
        }

        ServiceRole role = ServiceTable.RoleOf(kind);
        int best = -1;
        int bestEst = int.MaxValue;
        for (int i = 0; i < s.Capacity; i++)
        {
            int bi = s.Building[i];
            if (bi < 0 || s.Svc[i] != rec || s.UnitsMax[i] == 0 || !DepotOk(c, bi, s.Gen[i]) || (sv.Table.LevelRow(rec, s.Level[i]).Roles & role) == 0)
            {
                continue;
            }

            int dl = c.Buildings.Lot[bi];
            if (l.RoadComp[dl] != comp || l.CarLane[dl] < 0)
            {
                continue;
            }

            anyStation = true;
            if (sv.IdleScratch[i] < 0)
            {
                continue;
            }

            int est = EstDs(c, dl, targetLot, c.PeriodOf(w.Tick));
            if (est < bestEst)
            {
                best = i;
                bestEst = est;
            }
        }

        return best >= 0 ? sv.IdleScratch[best] : -1;
    }

    /// <summary>
    /// Оценка времени в пути машиной (§2.3), дс: ским ячеек <c>Skims.Get</c> (наблюдённые ноги - с пробками; без данных - априорная по свободному потоку); одна ячейка или нет оценки -
    /// манхэттен ×1.3 на 8 м/с + 60 с (формула <c>TravelSlots</c>). Перед вызовом - <c>Skims.Prepare</c>.
    /// </summary>
    internal static int EstDs(CityWorld c, int fromLot, int toLot, int period)
    {
        LotStore l = c.Lots;
        int a = l.Cell[fromLot];
        int b = l.Cell[toLot];
        int ds = a != b ? c.Skims.Get(a, b, TravelMode.Car, period) : 0;
        return ds > 0 ? ds : ((l.ManhattanM(fromLot, toLot) * 13 / 10 / 8) + 60) * 10;
    }

    /// <summary>Субсидия ЖКХ из казны включена (бит политики <see cref="BudgetPolicyFlags.UtilitySubsidy"/>).</summary>
    private static bool Subsidy(CityWorld c) => c.Budget != null && (c.Budget.Policy.Flags & BudgetPolicyFlags.UtilitySubsidy) != 0;

    /// <summary>
    /// Тип машины бригады: в режиме - <c>dispatch.vehicle</c> уровня станции, если он есть в таблице ТС; иначе <c>VehicleType</c> здания станции, если есть; иначе первый тип категории <c>Car</c>.
    /// </summary>
    private static int VehicleTypeOf(World w, CityWorld c, CityServices sv, int crew)
    {
        int site = sv.Crews.Site[crew];
        if (sv.Table.Explicit && site >= 0)
        {
            int dv = sv.Table.LevelRow(sv.Sites.Svc[site], sv.Sites.Level[site]).DispatchVehicle;
            if ((uint)dv < (uint)w.Types.Length)
            {
                return dv;
            }
        }

        int t = c.Types.Rows[c.Buildings.Type[sv.SiteBuilding(crew)]].VehicleType;
        if ((uint)t < (uint)w.Types.Length)
        {
            return t;
        }

        for (int i = 0; i < w.Types.Length; i++)
        {
            if (w.Types[i].Category == VehicleCategory.Car)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Заявка поездки машины бригады <paramref name="k"/> от участка <paramref name="fromLot"/> к <paramref name="toLot"/> (<c>ResidentRef = -(k + 1)</c>). false - нет полос.</summary>
    private static bool Submit(World w, CityWorld c, CityServices sv, int k, int fromLot, int toLot)
    {
        LotStore l = c.Lots;
        int from = l.CarLane[fromLot];
        int dest = l.CarLane[toLot];
        if ((uint)from >= (uint)w.Net.LaneCount || (uint)dest >= (uint)w.Net.LaneCount)
        {
            return false;
        }

        int type = VehicleTypeOf(w, c, sv, k);
        float laneEnd = MathF.Max(0f, w.Net.Lanes[dest].Length - w.Types[type].MinGap);
        var trip = new PendingTrip
        {
            FromZone = -1,
            ToZone = -1,
            FromLane = from,
            FromS = 0f,
            Type = (byte)type,
            DestLane = dest,
            DestS = MathF.Min(l.CarS[toLot], laneEnd),
            Route = -1,
            ResidentRef = -(k + 1),
        };
        return w.Trips.TryEnqueue(in trip);
    }

    /// <summary>Время поездки машиной по скиму (тики, не меньше 2); без скима - Manhattan на 10 м/с.</summary>
    private static long TravelTicks(World w, CityWorld c, int fromLot, int toLot)
    {
        Skims s = c.Skims;
        s.Prepare(w);
        int ds = Choice.LegCost(c, s, true, fromLot, toLot, TravelMode.Car, c.PeriodOf(w.Tick));
        if (ds <= 0 || ds == DetLogit.NoCost)
        {
            return Math.Max(2L, (long)c.Lots.ManhattanM(fromLot, toLot) * Simulation.TicksPerSecond / FallbackSpeedMs);
        }

        return Math.Max(2L, Calendar.DsToTicks(ds));
    }

    /// <summary>Таймаут поездки micro (§2.10): <c>3 × скима + 1 игровой час</c>.</summary>
    private static long TimeoutTicks(World w, CityWorld c, int fromLot, int toLot) => (3 * TravelTicks(w, c, fromLot, toLot)) + (c.DayTicks / 24);

    /// <summary>
    /// Работа на месте: без режима - <c>repair_minutes</c> игрового времени (BL); в режиме - <c>onsite_s · onsite_q</c> (§2.4: <c>onsite_q = 100 / pct</c> × эффект апгрейдов <c>onsite_pct</c>).
    /// Не меньше одного тика.
    /// </summary>
    private static long WorkTicks(CityWorld c, CityServices sv, int k)
    {
        if (!sv.Table.Explicit)
        {
            return Math.Max(1L, (long)c.Cfg.Lifecycle.RepairMinutes * c.DayTicks / 1440);
        }

        int site = sv.Crews.Site[k];
        int rec = sv.Sites.Svc[site];
        int ci = sv.Table.CallOf(rec, sv.Crews.Kind[k]);
        long baseTicks = (ci >= 0 ? sv.Table.Calls[ci].OnsiteS : 0) * (long)Simulation.TicksPerSecond;
        int up = sv.Table.UpgradeEffect(in sv.Table.LevelRow(rec, sv.Sites.Level[site]), sv.Sites.UpgradeMask[site], ServiceUpgradeEffect.OnsitePct);
        return Math.Max(1L, baseTicks * up / Math.Max(1, (int)sv.Pct[rec]));
    }

    /// <summary>
    /// Прибытие бригады (кольцо фазы 8 или <c>DueTick</c> агрегата): к зданию - отклик (§2.3), затем работа (или назад при опоздании и негодной цели); на станцию - <c>Idle</c>.
    /// </summary>
    private static void Arrive(World w, CityWorld c, CityServices sv, int k)
    {
        CrewStore u = sv.Crews;
        u.Vehicle[k] = -1;
        u.VehicleGen[k] = 0;
        if (u.State[k] == CrewState.ToSite)
        {
            if (ValidTarget(c, u.Kind[k], u.Target[k], u.TargetGen[k]))
            {
                int site = u.Site[k];
                int rec = sv.Sites.Svc[site];
                OnResponse(w, c, sv, k, rec);
                if (Late(c, sv, rec, u.Kind[k], u.ReqTick[k], w.Tick))
                {
                    OnLate(w, c, sv, rec, u.Kind[k], u.Ref[k], u.RefGen[k]);   // не успели (§2.3.1): без действия на месте, назад
                    u.Kind[k] = 0;
                    u.Ref[k] = -1;
                    u.RefGen[k] = 0;
                    StartReturn(w, c, sv, k);
                    return;
                }

                u.State[k] = CrewState.Working;
                u.DueTick[k] = w.Tick + WorkTicks(c, sv, k);
                return;
            }

            ClearRequested(c, u.Target[k], u.TargetGen[k]);   // снесено или заброшено к приезду: без ремонта, назад
            if (u.Kind[k] == (byte)ServiceCallKind.Critical)
            {
                Illness.Lose(w, c, u.Ref[k], u.RefGen[k]);
            }

            u.Kind[k] = 0;
            u.Ref[k] = -1;
            u.RefGen[k] = 0;
            StartReturn(w, c, sv, k);
        }
        else if (u.State[k] == CrewState.Return)
        {
            ToDepot(w, c, sv, k);
        }
    }

    /// <summary>
    /// Отклик вызова <c>arrive − call.Tick</c> (§2.3): счётчики записи и мира, переменная часть расхода станции (<c>per_call</c>), EWMA клетки 128 м цели (<c>RespEwma</c>, только в режиме).
    /// </summary>
    private static void OnResponse(World w, CityWorld c, CityServices sv, int k, int rec)
    {
        CrewStore u = sv.Crews;
        long resp = w.Tick - u.ReqTick[k];
        sv.RecCallsDone[rec]++;
        sv.RecRespTicks[rec] += resp;
        w.Counters.CallsDone++;
        w.Counters.RespTicksTotal += resp;
        if (!sv.Table.Explicit)
        {
            return;
        }

        int ci = sv.Table.CallOf(rec, u.Kind[k]);
        if (ci >= 0)
        {
            sv.SiteVarDay[(u.Site[k] * 2) + (c.Day & 1)] += sv.Table.Calls[ci].PerCall;
        }

        NeighborhoodFields? f = c.Fields;
        if (f != null && sv.Cells > 0 && u.SiteLot[k] >= 0)
        {
            int cell = f.CellOf(c.Lots.X[u.SiteLot[k]], c.Lots.Z[u.SiteLot[k]]);
            int v = (int)Math.Clamp(resp / Simulation.TicksPerSecond, 1L, ushort.MaxValue);
            int idx = (rec * sv.Cells) + cell;
            int e = sv.RespEwma[idx];
            sv.RespEwma[idx] = (ushort)(e == 0 ? v : Math.Clamp(e + ((v - e) >> 3), 1, ushort.MaxValue));
        }
    }

    /// <summary>Конец работы: действие вызова (ремонт - до потолка из фонда, дефицит - субсидия, если включена; прочие виды - SR3/SR4), заявка снята, обратная поездка.</summary>
    private static void FinishWork(World w, CityWorld c, CityServices sv, int k)
    {
        CrewStore u = sv.Crews;
        int bi = u.Target[k];
        if (u.Kind[k] == (byte)ServiceCallKind.Repair && ValidTarget(c, u.Kind[k], bi, u.TargetGen[k]))
        {
            Ledger lg = c.Ledger;
            long price = RepairCost(c, bi);
            if (lg.Utility < price && Subsidy(c))
            {
                lg.Transfer(lg.CityAccount, lg.UtilityAccount, price - lg.Utility, FlowKind.UtilitySubsidy);
            }

            lg.Transfer(lg.UtilityAccount, lg.ExternalAccount, price, FlowKind.Repair);
            BuildingStore b = c.Buildings;
            b.Cond[bi] = b.Ceil[bi];
            if (b.Cond[bi] >= CityLife.NormBp)
            {
                b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.WornNotified);
            }

            w.Counters.RepairsDone++;
            w.Counters.RepairDelayTicksTotal += w.Tick - u.ReqTick[k];
        }
        else if (u.Kind[k] == (byte)ServiceCallKind.Critical && !Illness.Waiting(c, u.Ref[k], u.RefGen[k]))
        {
            u.Ref[k] = -1;   // пациента уже нет (уехал, выселен): назад пустыми (SR3, §2.3)
            u.RefGen[k] = 0;
        }

        ClearRequested(c, bi, u.TargetGen[k]);
        StartReturn(w, c, sv, k);
    }

    /// <summary>Обратная поездка (micro - машина от участка здания к станции; агрегат - <c>DueTick</c> по скиму). Станции нет или полос нет - бригада сразу на станции (или распущена).</summary>
    private static void StartReturn(World w, CityWorld c, CityServices sv, int k)
    {
        CrewStore u = sv.Crews;
        u.Target[k] = -1;
        u.TargetGen[k] = 0;
        int site = u.SiteLot[k];
        if (!DepotOk(c, sv.SiteBuilding(k), sv.SiteGen(k)) || site < 0)
        {
            ToDepot(w, c, sv, k);
            return;
        }

        int depotLot = c.Buildings.Lot[sv.SiteBuilding(k)];
        if (c.Exec == TripExecution.Aggregate)
        {
            u.State[k] = CrewState.Return;
            u.DueTick[k] = w.Tick + TravelTicks(w, c, site, depotLot);
            return;
        }

        if (w.Trips.IsFull || !Submit(w, c, sv, k, site, depotLot))
        {
            ToDepot(w, c, sv, k);   // поездку не подать: бригада возвращается без машины
            return;
        }

        u.State[k] = CrewState.Return;
        u.Vehicle[k] = -1;
        u.VehicleGen[k] = 0;
        u.DueTick[k] = w.Tick + TimeoutTicks(w, c, site, depotLot);
    }

    /// <summary>
    /// Бригада на станции: <c>Idle</c>, либо слот свободен, если станция больше не работает. В режиме: на линии бригад больше <c>Duty</c> (ползунок урезан) - <c>OffDuty</c> (§2.4).
    /// </summary>
    private static void ToDepot(World w, CityWorld c, CityServices sv, int k)
    {
        CrewStore u = sv.Crews;
        bool ok = DepotOk(c, sv.SiteBuilding(k), sv.SiteGen(k));
        if (u.Kind[k] == (byte)ServiceCallKind.Critical && u.Ref[k] >= 0)
        {
            if (ok && u.State[k] is CrewState.Return or CrewState.Working)
            {
                Illness.AdmitIcu(w, c, sv, u.Site[k], u.Ref[k], u.RefGen[k]);   // доставка в реанимацию станции (SR3, §2.3); Working - обратную поездку не подать, вернулись без машины
            }
            else
            {
                Illness.Lose(w, c, u.Ref[k], u.RefGen[k]);   // не доехали (таймаут, поездка снята) или станции больше нет
            }
        }

        if (!ok)
        {
            u.Reset(k);
            return;
        }

        int site = u.Site[k];
        u.Reset(k);
        u.Site[k] = site;
        u.State[k] = CrewState.Idle;
        if (sv.Table.Explicit && OnLine(sv, site) > sv.Sites.Duty[site])
        {
            u.State[k] = CrewState.OffDuty;
        }
    }

    /// <summary>Бригад станции на линии (не свободных и не снятых с линии).</summary>
    private static int OnLine(CityServices sv, int site)
    {
        CrewStore u = sv.Crews;
        int n = 0;
        for (int k = 0; k < u.Capacity; k++)
        {
            n += u.Site[k] == site && u.State[k] is not (CrewState.Free or CrewState.OffDuty) ? 1 : 0;
        }

        return n;
    }

    /// <summary>Таймаут поездки micro (§2.10): бригада сразу на станции (<c>CrewTimeouts</c>), машина доезжает пустой; заявка к зданию снята (завтра - новая).</summary>
    private static void Timeout(World w, CityWorld c, CityServices sv, int k)
    {
        CrewStore u = sv.Crews;
        w.Counters.CrewTimeouts++;
        Detach(w, sv.Crews, k);
        if (u.State[k] == CrewState.ToSite)
        {
            ClearRequested(c, u.Target[k], u.TargetGen[k]);
        }

        ToDepot(w, c, sv, k);
    }

    /// <summary>Отвязывает машину бригады (поставленную или ещё в очереди поездок): дальше она едет пустой, как поездка OD.</summary>
    private static void Detach(World w, CrewStore u, int k)
    {
        int v = u.Vehicle[k];
        if (v >= 0)
        {
            if (w.Vehicles.Pool.IsAlive(v, u.VehicleGen[k]) && w.Vehicles.Resident[v] == -2 - k)
            {
                w.Vehicles.Resident[v] = -1;
            }

            return;
        }

        PendingTrips trips = w.Trips;
        for (int i = 0; i < trips.Count; i++)
        {
            PendingTrip t = trips.At(i);
            if (t.ResidentRef == -(k + 1))
            {
                t.ResidentRef = 0;
                trips.Set(i, in t);
                return;
            }
        }
    }

    private static void ClearRequested(CityWorld c, int bi, int gen)
    {
        BuildingStore b = c.Buildings;
        if (bi >= 0 && b.Pool.IsAlive(bi, gen) && (b.LifeFlags[bi] & BuildingLifeFlags.Requested) != 0)
        {
            b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.Requested);
        }
    }

    // ------------------------------------------------------------------ связь с машинами (фазы 2, 3, 8)

    /// <summary>
    /// Машина бригады <paramref name="crew"/> поставлена на дорогу (<c>Spawner.TryPlace</c>): <c>Resident = -2 - crew</c>. Приоритет VC (§2.3): к цели - при <c>on_call</c> вида вызова,
    /// назад - при <c>on_call_return</c>; только у машины класса <c>special</c>; у ЖКХ - никогда (валидатор записей).
    /// </summary>
    internal static void OnVehiclePlaced(World w, int crew, int vehicle)
    {
        CityServices? sv = w.City?.Services;
        CrewStore? u = sv?.Crews;
        if (u == null || (uint)crew >= (uint)u.Capacity || u.State[crew] is not (CrewState.ToSite or CrewState.Return) || u.Vehicle[crew] >= 0)
        {
            return;   // страховка: бригада уже не ждёт машину - машина едет пустой
        }

        u.Vehicle[crew] = vehicle;
        u.VehicleGen[crew] = w.Vehicles.Pool.GenerationOf(vehicle);
        w.Vehicles.Resident[vehicle] = -2 - crew;
        if (!sv!.Table.Explicit)
        {
            return;   // без режима - как BL
        }

        w.Vehicles.Flags[vehicle] &= unchecked((byte)~(byte)VehicleFlags.OnCall);   // в режиме приоритетом машины службы владеет диспетчер, не demand.special_on_call
        int site = u.Site[crew];
        if (site < 0 || u.Kind[crew] == 0 || w.Types[w.Vehicles.Type[vehicle]].Category != VehicleCategory.Special)
        {
            return;
        }

        byte flags = sv.Table.CallFlagsOf(sv.Sites.Svc[site], u.Kind[crew]);
        byte need = u.State[crew] == CrewState.ToSite ? ServiceCallFlags.OnCall : ServiceCallFlags.OnCallReturn;
        if ((flags & need) != 0)
        {
            w.Vehicles.Flags[vehicle] |= (byte)VehicleFlags.OnCall;
        }
    }

    /// <summary>Фаза 8 / <c>QueueMeso.Arrive</c>: машина бригады прибыла (до деспауна) - в кольцо <c>CrewArrivals</c>, разбор в 9r.</summary>
    internal static void OnVehicleArrived(World w, int vehicle)
    {
        int crew = -2 - w.Vehicles.Resident[vehicle];
        CrewStore? u = w.City?.Services?.Crews;
        w.Vehicles.Resident[vehicle] = -1;
        w.Vehicles.Flags[vehicle] &= unchecked((byte)~(byte)VehicleFlags.OnCall);   // приоритет снимается на прибытии (§2.3)
        if (u != null && (uint)crew < (uint)u.Capacity && u.Vehicle[crew] == vehicle && u.VehicleGen[crew] == w.Vehicles.Pool.GenerationOf(vehicle)
            && u.State[crew] is CrewState.ToSite or CrewState.Return)
        {
            u.Arrivals.TryEnqueue(in crew);
        }
    }

    /// <summary>Поездка бригады снята без постановки (нет пути, плохая полоса): к зданию - заявка снята, бригада на станции; назад - бригада на станции.</summary>
    internal static void OnTripDropped(World w, int crew)
    {
        CityWorld? c = w.City;
        CrewStore? u = c?.Services?.Crews;
        if (u == null || (uint)crew >= (uint)u.Capacity || u.Vehicle[crew] >= 0 || u.State[crew] is not (CrewState.ToSite or CrewState.Return))
        {
            return;
        }

        if (u.State[crew] == CrewState.ToSite)
        {
            ClearRequested(c!, u.Target[crew], u.TargetGen[crew]);
        }

        ToDepot(w, c!, c!.Services!, crew);
    }

    // ------------------------------------------------------------------ сутки

    /// <summary>
    /// Граница суток (фаза 9r, после <c>Day++</c>, до казны). Без режима служб - зарплата бригад BL <c>crew_day_cost</c> × бригад из фонда (всегда, даже в минус). В режиме - ожидающий ползунок
    /// записей (§2.4), <c>Duty</c> станций и бригады на линии, обнуление суточных слотов новых суток (расход станции списывается в стадии «здания», <see cref="StationDay"/>).
    /// Затем субсидия: фонд в минусе и бит политики - казна покрывает минус.
    /// </summary>
    public static void OnDayBoundary(CityWorld c)
    {
        CityServices? sv = c.Services;
        if (sv == null)
        {
            return;
        }

        Ledger lg = c.Ledger;
        if (sv.Table.Explicit)
        {
            NewDay(c, sv);
        }
        else
        {
            lg.Transfer(lg.UtilityAccount, lg.ExternalAccount, (long)c.Cfg.Lifecycle.CrewDayCost * sv.Crews.Crews(), FlowKind.CrewWages);
        }

        if (lg.Utility < 0 && Subsidy(c))
        {
            lg.Transfer(lg.CityAccount, lg.UtilityAccount, -lg.Utility, FlowKind.UtilitySubsidy);
        }
    }

    private static void NewDay(CityWorld c, CityServices sv)
    {
        int d = c.Day & 1;
        for (int r = 0; r < sv.Table.Count; r++)
        {
            sv.SvcCostDay[(r * 2) + d] = 0;
            sv.Pct[r] = sv.PctPending[r];
        }

        SiteStore s = sv.Sites;
        for (int i = 0; i < s.Capacity; i++)
        {
            sv.SiteVarDay[(i * 2) + d] = 0;
            if (s.Building[i] >= 0 && DepotOk(c, s.Building[i], s.Gen[i]))
            {
                s.Duty[i] = (byte)DutyOf(sv, i);
                Rebalance(sv, i);
            }
        }

        Illness.ReleaseDay(c, sv);   // выписка коек и реанимации (SR3)
    }

    /// <summary>Бригады станции на линии к <c>Duty</c>: не хватает - снятые с линии выходят (с меньшего индекса); лишние свободные - снимаются (с большего); занятые доезжают (<see cref="ToDepot"/>).</summary>
    private static void Rebalance(CityServices sv, int site)
    {
        CrewStore u = sv.Crews;
        int on = OnLine(sv, site);
        int duty = sv.Sites.Duty[site];
        for (int k = 0; k < u.Capacity && on < duty; k++)
        {
            if (u.Site[k] == site && u.State[k] == CrewState.OffDuty)
            {
                u.State[k] = CrewState.Idle;
                on++;
            }
        }

        for (int k = u.Capacity - 1; k >= 0 && on > duty; k--)
        {
            if (u.Site[k] == site && u.State[k] == CrewState.Idle)
            {
                u.State[k] = CrewState.OffDuty;
                on--;
            }
        }
    }

    /// <summary>
    /// Расход станции здания <paramref name="bi"/> за сутки (§2.4, стадия «здания», только в режиме): <c>(upkeep_day + Σ upkeep апгрейдов + per_unit_day · Duty) · pct / 100</c> + переменная часть
    /// вчера (Σ <c>per_call</c>); поток <c>ServiceUpkeep</c> со счёта <c>fund</c> записи на <c>External</c>, разбивка - <c>SvcCostDay</c>.
    /// </summary>
    public static void StationDay(CityWorld c, int bi)
    {
        CityServices? sv = c.Services;
        if (sv == null || !sv.Table.Explicit)
        {
            return;
        }

        SiteStore s = sv.Sites;
        int gen = c.Buildings.Pool.GenerationOf(bi);
        for (int i = 0; i < s.Capacity; i++)
        {
            if (s.Building[i] != bi || s.Gen[i] != gen)
            {
                continue;
            }

            int rec = s.Svc[i];
            ref readonly ServiceLevelDesc l = ref sv.Table.LevelRow(rec, s.Level[i]);
            long fixedCost = l.UpkeepDay + sv.Table.UpgradeUpkeep(in l, s.UpgradeMask[i]) + ((long)l.PerUnitDay * s.Duty[i]);
            long cost = (fixedCost * sv.Pct[rec] / 100) + sv.SiteVarDay[(i * 2) + ((c.Day + 1) & 1)];
            if (cost > 0)
            {
                Ledger lg = c.Ledger;
                int from = sv.Table.Records[rec].Fund == ServiceFund.Utility ? lg.UtilityAccount : lg.CityAccount;
                lg.Transfer(from, lg.ExternalAccount, cost, FlowKind.ServiceUpkeep);
                sv.SvcCostDay[(rec * 2) + (c.Day & 1)] += cost;
            }

            return;
        }
    }

    /// <summary>
    /// <c>SetServiceBudget</c> (§2.4, §6): режим <c>city.services</c> (иначе <c>NotSupported</c>), запись есть и <c>Pct</c> 50..150 шагом 10 (иначе <c>InvalidArgument</c>). Ожидающее значение,
    /// применяется на границе суток; та же величина, что действует, снимает ожидание.
    /// </summary>
    public static RejectReason SetBudget(World w, in Command cmd)
    {
        CityServices? sv = w.City?.Services;
        if (sv == null || !sv.Table.Explicit)
        {
            return RejectReason.NotSupported;
        }

        int rec = cmd.SetServiceBudget.Svc;
        int pct = cmd.SetServiceBudget.Pct;
        if ((uint)rec >= (uint)sv.Table.Count || pct is < 50 or > 150 || pct % 10 != 0)
        {
            return RejectReason.InvalidArgument;
        }

        sv.PctPending[rec] = (byte)pct;
        return RejectReason.None;
    }

    // ------------------------------------------------------------------ квартплата жителя

    /// <summary>
    /// Цена тарифа для жителя (§2.10): тариф (сотые в сутки) выше <c>tariff_burden_pct</c> его <c>IncomeEwma</c> - раз в сутки жребий <c>tariff_leave</c>: <c>Leaving</c>, случай K4, жалоба
    /// <c>HighRent</c> у дома. Ниже порога PRNG не трогается.
    /// </summary>
    public static void TariffLeave(World w, CityWorld c, int r)
    {
        CityBudget? bud = c.Budget;
        CityResidents res = c.Residents;
        int home = res.Home[r];
        if (bud == null || home < 0 || bud.Policy.R0 == 0)
        {
            return;
        }

        // Вариант Б (решение владельца 2026-10-30): жребий только у рабочей силы; иждивенцы (дети, пенсионеры) не в счёте.
        if ((res.Flags[r] & ResidentFlags.Labour) == 0 || bud.Policy.R0 <= res.IncomeEwma[r] * c.Cfg.Lifecycle.TariffBurdenPct)
        {
            return;
        }

        if ((w.RngDemand.NextUInt() >> 16) < (uint)c.Cfg.Lifecycle.TariffLeaveQ16)
        {
            res.Flags[r] |= ResidentFlags.Leaving;
            c.Kpi.OnHighRent(home, c.Buildings.Pool.GenerationOf(home));
            w.Counters.TariffLeaves++;
        }
    }
}
