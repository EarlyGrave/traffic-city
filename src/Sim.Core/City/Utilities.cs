using System;
using System.Runtime.InteropServices;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Demand;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>Состояние бригады (<see cref="UtilityService.State"/>, building-lifecycle.md §2.10).</summary>
internal static class CrewState
{
    public const byte Free = 0;      // слот свободен
    public const byte Idle = 1;      // в депо, ждёт заявку
    public const byte ToSite = 2;    // едет к зданию
    public const byte Working = 3;   // ремонтирует на месте до DueTick
    public const byte Return = 4;    // едет назад в депо
}

/// <summary>Заявка на ремонт в очереди <see cref="UtilityService.Queue"/>: здание (handle) и тик заявки.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct RepairRequest
{
    public int Building;
    public int Gen;
    public long Tick;
}

/// <summary>
/// ЖКХ - первая выездная служба (building-lifecycle.md §2.10, BL3): бригады депо (SoA ёмкости <c>max_service_vehicles</c>), очередь заявок FIFO (ёмкость <c>max_buildings</c>) и кольцо
/// прибытий машин бригад (фаза 8 -&gt; 9r, пусто на границе тиков, в сейв не входит). Есть только в режиме <c>city.lifecycle</c>. Все массивы - при создании; 0 Б/тик.
/// </summary>
internal sealed class UtilityService
{
    public readonly int Capacity;
    public readonly int[] Depot;        // здание-депо или -1
    public readonly int[] DepotGen;
    public readonly byte[] State;       // CrewState
    public readonly int[] Target;       // здание заявки или -1
    public readonly int[] TargetGen;
    public readonly int[] SiteLot;      // участок здания заявки (обратная поездка, если здание исчезло) или -1
    public readonly int[] Vehicle;      // машина бригады (micro) или -1, пока не поставлена
    public readonly int[] VehicleGen;
    public readonly long[] DueTick;     // micro: таймаут поездки / конец работы; агрегат: прибытие / конец работы
    public readonly long[] ReqTick;     // тик заявки (задержка ремонта)
    public readonly RingBuffer<RepairRequest> Queue;
    public readonly RingBuffer<int> Arrivals;   // CrewArrivals: бригады, чьи машины прибыли в этом тике

    public UtilityService(int crews, int maxBuildings)
    {
        Capacity = Math.Max(0, crews);
        Depot = new int[Capacity];
        DepotGen = new int[Capacity];
        State = new byte[Capacity];
        Target = new int[Capacity];
        TargetGen = new int[Capacity];
        SiteLot = new int[Capacity];
        Vehicle = new int[Capacity];
        VehicleGen = new int[Capacity];
        DueTick = new long[Capacity];
        ReqTick = new long[Capacity];
        Array.Fill(Depot, -1);
        Array.Fill(Target, -1);
        Array.Fill(SiteLot, -1);
        Array.Fill(Vehicle, -1);
        Queue = new RingBuffer<RepairRequest>(Math.Max(1, maxBuildings));
        Arrivals = new RingBuffer<int>(Math.Max(1, Capacity));
    }

    /// <summary>Бригад в работе (не свободных слотов).</summary>
    public int Crews()
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++)
        {
            n += State[i] != CrewState.Free ? 1 : 0;
        }

        return n;
    }

    /// <summary>Свободных слотов бригад.</summary>
    public int FreeSlots() => Capacity - Crews();

    /// <summary>Слот в исходное (свободен).</summary>
    public void Reset(int k)
    {
        Depot[k] = -1;
        DepotGen[k] = 0;
        State[k] = CrewState.Free;
        Target[k] = -1;
        TargetGen[k] = 0;
        SiteLot[k] = -1;
        Vehicle[k] = -1;
        VehicleGen[k] = 0;
        DueTick[k] = 0;
        ReqTick[k] = 0;
    }
}

/// <summary>
/// Логика ЖКХ (building-lifecycle.md §2.10): заявки в стадии «здания», выезд ближайшей свободной бригады (фаза 9r, тики кратные <see cref="Step"/>), связь машины с бригадой через
/// <c>VehicleStore.Resident = -2 - crew</c>, ремонт до потолка из фонда <c>Utility</c>, возврат, агрегатный режим (<c>DueTick</c> по скиму), зарплата бригад и субсидия из казны на границе суток.
/// У машины ЖКХ нет приоритета: <c>OnCall</c> не ставится никогда (VC §9.2). Всё - в последовательных фазах главного потока; 0 Б/тик.
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

    /// <summary>Создаёт депо на участке (после валидатора): здание <c>Active</c> без стройки, без мест, бригады - первые свободные слоты. false - нет места в пуле зданий.</summary>
    public static bool BuildDepot(World w, CityWorld c, int lot, int type)
    {
        UtilityService u = c.Utility!;
        if (!c.Buildings.TryCreate(lot, type, BuildingState.Active, w.Tick, out int bi))
        {
            return false;
        }

        BuildingStore b = c.Buildings;
        CityLife.OnCreate(c, bi);
        b.Jobs[bi] = 0;
        c.Lots.Building[lot] = bi;
        c.Lots.Version++;
        b.Version++;
        int gen = b.Pool.GenerationOf(bi);
        int need = c.Types.Rows[type].Vehicles;
        for (int k = 0; k < u.Capacity && need > 0; k++)
        {
            if (u.State[k] == CrewState.Free)
            {
                u.Reset(k);
                u.Depot[k] = bi;
                u.DepotGen[k] = gen;
                u.State[k] = CrewState.Idle;
                need--;
            }
        }

        w.Counters.BuildingsBuilt++;
        return true;
    }

    /// <summary>Депо перестало работать (заброшено или удаляется): свободные и работающие на месте бригады распускаются; едущие - по прибытии.</summary>
    public static void OnDepotLost(CityWorld c, int bi)
    {
        UtilityService? u = c.Utility;
        if (u == null)
        {
            return;
        }

        int gen = c.Buildings.Pool.GenerationOf(bi);
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.Depot[k] != bi || u.DepotGen[k] != gen)
            {
                continue;
            }

            if (u.State[k] == CrewState.Working)
            {
                ClearRequested(c, u.Target[k], u.TargetGen[k]);
                u.Reset(k);
            }
            else if (u.State[k] == CrewState.Idle)
            {
                u.Reset(k);
            }
        }
    }

    // ------------------------------------------------------------------ заявки (стадия «здания»)

    /// <summary>
    /// Заявка на ремонт (стадия «здания», после износа): <c>Active</c>, <c>Cond &lt; repair_request_bp</c>, <c>Ceil - Cond &gt;= 500</c>, <c>Cond &gt;= 50 %</c>, флага <c>Requested</c> нет -
    /// в очередь. Очередь полна - повтор завтра.
    /// </summary>
    public static void Request(World w, CityWorld c, int bi)
    {
        UtilityService? u = c.Utility;
        BuildingStore b = c.Buildings;
        if (u == null || b.State[bi] != (byte)BuildingState.Active || (b.LifeFlags[bi] & BuildingLifeFlags.Requested) != 0)
        {
            return;
        }

        int cond = b.Cond[bi];
        if (cond >= c.Cfg.Lifecycle.RepairRequestBp || cond < CityLife.AbandonBp || b.Ceil[bi] - cond < MinGapBp)
        {
            return;
        }

        var req = new RepairRequest { Building = bi, Gen = b.Pool.GenerationOf(bi), Tick = w.Tick };
        if (u.Queue.TryEnqueue(in req))
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
        UtilityService u = c.Utility!;
        while (u.Arrivals.TryDequeue(out int k))
        {
            Arrive(w, c, u, k);
        }

        if (w.Tick % Step != 0)
        {
            return;
        }

        long now = w.Tick;
        for (int k = 0; k < u.Capacity; k++)
        {
            byte st = u.State[k];
            if (st is CrewState.Free or CrewState.Idle || u.DueTick[k] > now)
            {
                continue;
            }

            if (st == CrewState.Working)
            {
                FinishWork(w, c, u, k);
            }
            else if (c.Exec == TripExecution.Aggregate)
            {
                Arrive(w, c, u, k);   // агрегат: машин нет, DueTick - прибытие по скиму
            }
            else
            {
                Timeout(w, c, u, k);
            }
        }

        Dispatch(w, c, u);
    }

    /// <summary>Ближайший тик (не раньше <paramref name="tick"/>), на котором ЖКХ что-то делает; <c>long.MaxValue</c> - ничего (для прыжков агрегата).</summary>
    public static long NextWorkTick(CityWorld c, long tick)
    {
        UtilityService? u = c.Utility;
        if (u == null)
        {
            return long.MaxValue;
        }

        if (!u.Arrivals.IsEmpty)
        {
            return tick;
        }

        long next = u.Queue.IsEmpty ? long.MaxValue : tick;
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.State[k] is not (CrewState.Free or CrewState.Idle))
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

    private static void Dispatch(World w, CityWorld c, UtilityService u)
    {
        BuildingStore b = c.Buildings;
        Ledger lg = c.Ledger;
        int sent = 0;
        int max = c.Cfg.Lifecycle.MaxDispatchPerTick;
        while (sent < max && u.Queue.TryPeek(out RepairRequest req))
        {
            int bi = req.Building;
            if (!ValidTarget(c, bi, req.Gen))
            {
                u.Queue.TryDequeue(out _);
                ClearRequested(c, bi, req.Gen);   // снесено, заброшено или уже в норме: заявка снята
                continue;
            }

            int k = PickCrew(c, u, b.Lot[bi], out bool anyDepot);
            if (k < 0)
            {
                if (anyDepot)
                {
                    break;   // все бригады компоненты заняты: голова FIFO ждёт
                }

                u.Queue.TryDequeue(out _);
                ClearRequested(c, bi, req.Gen);
                w.Counters.RepairNoDepot++;
                continue;
            }

            long price = RepairCost(c, bi);
            if (lg.Utility < price)
            {
                if (!Subsidy(c))
                {
                    break;   // фонд пуст: заявка ждёт денег (решение владельца §9.4 В)
                }

                lg.Transfer(lg.CityAccount, lg.UtilityAccount, price - lg.Utility, FlowKind.UtilitySubsidy);
            }

            int depotLot = b.Lot[u.Depot[k]];
            int siteLot = b.Lot[bi];
            if (c.Exec == TripExecution.Aggregate)
            {
                u.DueTick[k] = w.Tick + TravelTicks(w, c, depotLot, siteLot);
            }
            else
            {
                if (w.Trips.IsFull)
                {
                    break;   // очередь поездок полна: выезд в следующий шаг
                }

                if (!Submit(w, c, k, u.Depot[k], depotLot, siteLot))
                {
                    u.Queue.TryDequeue(out _);
                    ClearRequested(c, bi, req.Gen);   // нет полос у участка: заявка снята, как без депо
                    w.Counters.RepairNoDepot++;
                    continue;
                }

                u.DueTick[k] = w.Tick + TimeoutTicks(w, c, depotLot, siteLot);
            }

            u.Queue.TryDequeue(out _);
            u.State[k] = CrewState.ToSite;
            u.Target[k] = bi;
            u.TargetGen[k] = req.Gen;
            u.SiteLot[k] = siteLot;
            u.Vehicle[k] = -1;
            u.VehicleGen[k] = 0;
            u.ReqTick[k] = req.Tick;
            sent++;
        }
    }

    /// <summary>Здание годно к ремонту: живое, то же поколение, <c>Active</c>, <c>50 % &lt;= Cond &lt; Ceil</c>.</summary>
    private static bool ValidTarget(CityWorld c, int bi, int gen)
    {
        BuildingStore b = c.Buildings;
        return b.Pool.IsAlive(bi, gen) && b.State[bi] == (byte)BuildingState.Active && b.Cond[bi] >= CityLife.AbandonBp && b.Cond[bi] < b.Ceil[bi];
    }

    /// <summary>Депо работает: живое, то же поколение, <c>Active</c>.</summary>
    private static bool DepotOk(CityWorld c, int depot, int gen) =>
        c.Buildings.Pool.IsAlive(depot, gen) && c.Buildings.State[depot] == (byte)BuildingState.Active;

    /// <summary>
    /// Ближайшая по Manhattan свободная бригада работающего депо той же дорожной компоненты (равенство - меньший индекс депо, затем бригады); -1 - нет.
    /// <paramref name="anyDepot"/> - в компоненте есть работающее депо с бригадами (тогда заявка ждёт, а не снимается).
    /// </summary>
    private static int PickCrew(CityWorld c, UtilityService u, int siteLot, out bool anyDepot)
    {
        anyDepot = false;
        LotStore l = c.Lots;
        BuildingStore b = c.Buildings;
        int comp = l.RoadComp[siteLot];
        if (comp < 0 || l.CarLane[siteLot] < 0)
        {
            return -1;
        }

        int best = -1;
        int bestDist = int.MaxValue;
        int bestDepot = int.MaxValue;
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.State[k] == CrewState.Free || !DepotOk(c, u.Depot[k], u.DepotGen[k]))
            {
                continue;
            }

            int dl = b.Lot[u.Depot[k]];
            if (l.RoadComp[dl] != comp || l.CarLane[dl] < 0)
            {
                continue;
            }

            anyDepot = true;
            if (u.State[k] != CrewState.Idle)
            {
                continue;
            }

            int d = l.ManhattanM(dl, siteLot);
            if (d < bestDist || (d == bestDist && u.Depot[k] < bestDepot))
            {
                best = k;
                bestDist = d;
                bestDepot = u.Depot[k];
            }
        }

        return best;
    }

    /// <summary>Субсидия ЖКХ из казны включена (бит политики <see cref="BudgetPolicyFlags.UtilitySubsidy"/>).</summary>
    private static bool Subsidy(CityWorld c) => c.Budget != null && (c.Budget.Policy.Flags & BudgetPolicyFlags.UtilitySubsidy) != 0;

    /// <summary>Тип машины бригады: <c>VehicleType</c> депо, если он есть в таблице ТС, иначе первый тип категории <c>Car</c>.</summary>
    private static int VehicleTypeOf(World w, CityWorld c, int depot)
    {
        int t = c.Types.Rows[c.Buildings.Type[depot]].VehicleType;
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
    private static bool Submit(World w, CityWorld c, int k, int depot, int fromLot, int toLot)
    {
        LotStore l = c.Lots;
        int from = l.CarLane[fromLot];
        int dest = l.CarLane[toLot];
        if ((uint)from >= (uint)w.Net.LaneCount || (uint)dest >= (uint)w.Net.LaneCount)
        {
            return false;
        }

        int type = VehicleTypeOf(w, c, depot);
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

    /// <summary>Работа на месте: <c>repair_minutes</c> игрового времени (сутки = 1 440 игровых минут), не меньше одного тика.</summary>
    private static long WorkTicks(CityWorld c) => Math.Max(1L, (long)c.Cfg.Lifecycle.RepairMinutes * c.DayTicks / 1440);

    /// <summary>Прибытие бригады (кольцо фазы 8 или <c>DueTick</c> агрегата): к зданию - работа или назад; в депо - <c>Idle</c>.</summary>
    private static void Arrive(World w, CityWorld c, UtilityService u, int k)
    {
        u.Vehicle[k] = -1;
        u.VehicleGen[k] = 0;
        if (u.State[k] == CrewState.ToSite)
        {
            if (ValidTarget(c, u.Target[k], u.TargetGen[k]))
            {
                u.State[k] = CrewState.Working;
                u.DueTick[k] = w.Tick + WorkTicks(c);
                return;
            }

            ClearRequested(c, u.Target[k], u.TargetGen[k]);   // снесено или заброшено к приезду: без ремонта, назад
            StartReturn(w, c, u, k);
        }
        else if (u.State[k] == CrewState.Return)
        {
            ToDepot(c, u, k);
        }
    }

    /// <summary>Конец работы: ремонт до потолка, плата из фонда (дефицит - субсидия, если включена), заявка снята, обратная поездка.</summary>
    private static void FinishWork(World w, CityWorld c, UtilityService u, int k)
    {
        int bi = u.Target[k];
        if (ValidTarget(c, bi, u.TargetGen[k]))
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

        ClearRequested(c, bi, u.TargetGen[k]);
        StartReturn(w, c, u, k);
    }

    /// <summary>Обратная поездка (micro - машина от участка здания к депо; агрегат - <c>DueTick</c> по скиму). Депо нет или полос нет - бригада сразу в депо (или распущена).</summary>
    private static void StartReturn(World w, CityWorld c, UtilityService u, int k)
    {
        u.Target[k] = -1;
        u.TargetGen[k] = 0;
        int site = u.SiteLot[k];
        if (!DepotOk(c, u.Depot[k], u.DepotGen[k]) || site < 0)
        {
            ToDepot(c, u, k);
            return;
        }

        int depotLot = c.Buildings.Lot[u.Depot[k]];
        if (c.Exec == TripExecution.Aggregate)
        {
            u.State[k] = CrewState.Return;
            u.DueTick[k] = w.Tick + TravelTicks(w, c, site, depotLot);
            return;
        }

        if (w.Trips.IsFull || !Submit(w, c, k, u.Depot[k], site, depotLot))
        {
            ToDepot(c, u, k);   // поездку не подать: бригада возвращается без машины
            return;
        }

        u.State[k] = CrewState.Return;
        u.Vehicle[k] = -1;
        u.VehicleGen[k] = 0;
        u.DueTick[k] = w.Tick + TimeoutTicks(w, c, site, depotLot);
    }

    /// <summary>Бригада в депо: <c>Idle</c>, либо слот свободен, если депо больше не работает.</summary>
    private static void ToDepot(CityWorld c, UtilityService u, int k)
    {
        if (!DepotOk(c, u.Depot[k], u.DepotGen[k]))
        {
            u.Reset(k);
            return;
        }

        int depot = u.Depot[k];
        int gen = u.DepotGen[k];
        u.Reset(k);
        u.Depot[k] = depot;
        u.DepotGen[k] = gen;
        u.State[k] = CrewState.Idle;
    }

    /// <summary>Таймаут поездки micro (§2.10): бригада сразу в депо (<c>CrewTimeouts</c>), машина доезжает пустой; заявка к зданию снята (завтра - новая).</summary>
    private static void Timeout(World w, CityWorld c, UtilityService u, int k)
    {
        w.Counters.CrewTimeouts++;
        Detach(w, u, k);
        if (u.State[k] == CrewState.ToSite)
        {
            ClearRequested(c, u.Target[k], u.TargetGen[k]);
        }

        ToDepot(c, u, k);
    }

    /// <summary>Отвязывает машину бригады (поставленную или ещё в очереди поездок): дальше она едет пустой, как поездка OD.</summary>
    private static void Detach(World w, UtilityService u, int k)
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

    /// <summary>Машина бригады <paramref name="crew"/> поставлена на дорогу (<c>Spawner.TryPlace</c>): <c>Resident = -2 - crew</c>, без <c>OnCall</c>.</summary>
    internal static void OnVehiclePlaced(World w, int crew, int vehicle)
    {
        UtilityService? u = w.City?.Utility;
        if (u == null || (uint)crew >= (uint)u.Capacity || u.State[crew] is not (CrewState.ToSite or CrewState.Return) || u.Vehicle[crew] >= 0)
        {
            return;   // страховка: бригада уже не ждёт машину - машина едет пустой
        }

        u.Vehicle[crew] = vehicle;
        u.VehicleGen[crew] = w.Vehicles.Pool.GenerationOf(vehicle);
        w.Vehicles.Resident[vehicle] = -2 - crew;
    }

    /// <summary>Фаза 8 / <c>QueueMeso.Arrive</c>: машина бригады прибыла (до деспауна) - в кольцо <c>CrewArrivals</c>, разбор в 9r.</summary>
    internal static void OnVehicleArrived(World w, int vehicle)
    {
        int crew = -2 - w.Vehicles.Resident[vehicle];
        UtilityService? u = w.City?.Utility;
        w.Vehicles.Resident[vehicle] = -1;
        if (u != null && (uint)crew < (uint)u.Capacity && u.Vehicle[crew] == vehicle && u.VehicleGen[crew] == w.Vehicles.Pool.GenerationOf(vehicle)
            && u.State[crew] is CrewState.ToSite or CrewState.Return)
        {
            u.Arrivals.TryEnqueue(in crew);
        }
    }

    /// <summary>Поездка бригады снята без постановки (нет пути, плохая полоса): к зданию - заявка снята, бригада в депо; назад - бригада в депо.</summary>
    internal static void OnTripDropped(World w, int crew)
    {
        CityWorld? c = w.City;
        UtilityService? u = c?.Utility;
        if (u == null || (uint)crew >= (uint)u.Capacity || u.Vehicle[crew] >= 0 || u.State[crew] is not (CrewState.ToSite or CrewState.Return))
        {
            return;
        }

        if (u.State[crew] == CrewState.ToSite)
        {
            ClearRequested(c!, u.Target[crew], u.TargetGen[crew]);
        }

        ToDepot(c!, u, crew);
    }

    // ------------------------------------------------------------------ сутки

    /// <summary>
    /// Граница суток (фаза 9r, до казны): зарплата бригад <c>crew_day_cost</c> × бригад из фонда (всегда, даже в минус), затем субсидия: фонд в минусе и бит политики - казна покрывает минус.
    /// </summary>
    public static void OnDayBoundary(CityWorld c)
    {
        UtilityService? u = c.Utility;
        if (u == null)
        {
            return;
        }

        Ledger lg = c.Ledger;
        lg.Transfer(lg.UtilityAccount, lg.ExternalAccount, (long)c.Cfg.Lifecycle.CrewDayCost * u.Crews(), FlowKind.CrewWages);
        if (lg.Utility < 0 && Subsidy(c))
        {
            lg.Transfer(lg.CityAccount, lg.UtilityAccount, -lg.Utility, FlowKind.UtilitySubsidy);
        }
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

        if (bud.Policy.R0 <= res.IncomeEwma[r] * c.Cfg.Lifecycle.TariffBurdenPct)
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
