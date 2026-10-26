using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Фаза 3 (core-api 7, routing.md 9.6): маршруты. Порядок: (1) дозаполнение окон из <c>RefillQueue</c> (все, без бюджета: нужно машинам на дороге);
/// (2) проверка дрейфа плановой перемаршрутизации (O(окно + план), без поиска); (3) промахи (<c>Rerouting</c>, kind = Miss), затем плановые по дрейфу
/// (доля 1/4), затем поездки <see cref="PendingTrips"/> без маршрута - пока работа запросов (релаксации) меньше <c>work_budget_per_tick</c> и их
/// число меньше <c>pathfind_budget_per_tick</c> (проверка между запросами, запрос не прерывается); (4) пересчёт весов по номеру тика и старт волны
/// кастомизации; (5) волна в пределах <c>customize_budget_per_tick</c>. Неудача (<c>NoPath</c>) возвращает запись в хвост с
/// <see cref="RouteNav.RerouteRetryTicks"/>; нет пути у поездки - она удаляется (<c>TripsDropped</c>, <c>RouteFailed</c>). Бюджет исчерпан или
/// <see cref="BuildStatus.Deferred"/> - остальные ждут следующего тика (<c>RouteDeferred</c> += число ждущих поездок). Поездка, для которой нельзя
/// выбрать точку прибытия/старт (зона без полос), остаётся в очереди без затрат бюджета. Аллокаций нет.
/// </summary>
internal static class RoutePhase
{
    /// <summary>Период пересчёта весов по загрузке по умолчанию, тиков (30 с, ADR-0006); сценарий задаёт <c>routing.cost_recalc_s</c>.</summary>
    public const int DefaultCostRecalcTicks = 600;

    /// <summary>Период проверки дрейфа одной машины, тиков (60 с); машины разнесены по индексу.</summary>
    public const int TimedPeriod = 1200;

    /// <summary>Новый маршрут принимается, если дешевле старого хотя бы на эту долю.</summary>
    public const float TimedGain = 0.10f;

    /// <summary>Не перестраивать, если до конца сегмента меньше, м (машина уже готовится к повороту).</summary>
    public const float TimedMinSegmentRemaining = 80f;

    /// <summary>Устаревшая плановая запись (не дошла очередь) отбрасывается через столько тиков.</summary>
    public const int TimedExpiryTicks = 600;

    /// <summary>Доля <c>PathfindBudgetPerTick</c> на плановые запросы: 1/<c>TimedBudgetDivisor</c>.</summary>
    public const int TimedBudgetDivisor = 4;

    private const float Dt = 0.05f;

    /// <summary>Бюджет запросов тика: число запросов и работа (релаксации); 0 в сценарии = без ограничения.</summary>
    private struct Budget
    {
        public int MaxQueries;
        public int MaxWork;
        public int Served;
        public bool Stopped;

        public readonly bool Has(Router rt) => Served < MaxQueries && rt.WorkThisTick < MaxWork;
    }

    public static void Run(World w)
    {
        Router rt = w.Router;
        RefillWindows(w);
        rt.WorkThisTick = 0; // дозаполнения в бюджет запросов не входят

        var budget = new Budget
        {
            MaxQueries = w.Routing.PathfindBudgetPerTick > 0 ? w.Routing.PathfindBudgetPerTick : int.MaxValue,
            MaxWork = w.Routing.WorkBudgetPerTick > 0 ? w.Routing.WorkBudgetPerTick : int.MaxValue,
        };

        EnqueueTimed(w);
        RerouteVehicles(w, rt, ref budget);
        if (!budget.Stopped)
        {
            RouteTrips(w, rt, ref budget);
        }

        if (budget.Stopped)
        {
            w.Counters.RouteDeferred += w.Trips.Unrouted;
        }

        RecalcWeights(w, rt);
        RunCustomizeWave(w);
        w.Counters.PlanCompactions = w.Routes.Compactions;
    }

    // (1) Дозаполнение окон (routing.md 9.5): по порядку очереди, все. Не вышло - промах (маршрут освободит конец фазы 7).
    private static void RefillWindows(World w)
    {
        RingBuffer<RefillEntry> q = w.RefillQueue;
        VehicleStore v = w.Vehicles;
        int n = q.Count;
        for (int k = 0; k < n; k++)
        {
            q.TryDequeue(out RefillEntry e);
            int i = e.Index;
            if (!v.Pool.IsAlive(i, e.Generation))
            {
                continue;
            }

            v.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.RefillQueued);
            v.MarkRouteDirty(i);
            if (v.Route[i] < 0 || (v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0)
            {
                continue;
            }

            if (!w.Router.Refill(i))
            {
                v.Flags[i] |= (byte)VehicleFlags.RouteMissed;
                v.MarkRouteDirty(i);
            }
        }
    }

    // Пересчёт весов по номеру тика (ADR-0006). Пока идёт волна кастомизации оверлея, новый пересчёт откладывается до её конца
    // (routing.md 9.3 п. 4): RecalcPending держит просроченный пересчёт, каждый тик ожидания - CustomizeLagTicks.
    private static void RecalcWeights(World w, Router rt)
    {
        Overlay? ov = w.Overlay;
        bool overlay = ov != null && !ov.Disabled;
        if (w.Tick > 0 && w.Tick % w.Routing.CostRecalcTicks == 0)
        {
            w.RecalcPending = true;
        }

        if (!w.RecalcPending)
        {
            return;
        }

        if (overlay && ov!.WaveActive)
        {
            w.Counters.CustomizeLagTicks++;
            return;
        }

        w.RecalcPending = false;
        if (w.Graph.BuiltVersion != w.Net.Version)
        {
            w.Graph.Rebuild();
        }

        System.Diagnostics.Debug.Assert(w.SegmentLoadsTick == w.Tick - 1, "SegmentLoads not computed at the end of the previous tick (MetricsPhase.NeedsLoadsNow)");
        Rules.Intersections.SignalDelays.Update(w);   // 2.5, intersections.md 6.9: измеренная задержка светофоров -> штрафы дуг
        w.Graph.RefreshSignalCosts();
        w.Graph.RecalcCosts(w.SegmentLoads, w.Net.SegmentCount);
        w.Counters.CostRecalcs++;
        if (overlay)
        {
            ov!.AfterRecalc(w.Graph.WeightsVersion);
        }
    }

    // Конец фазы: волна кастомизации в пределах бюджета тика; счётчики оверлея переносятся в SimCounters.
    private static void RunCustomizeWave(World w)
    {
        Overlay? ov = w.Overlay;
        if (ov == null)
        {
            return;
        }

        if (!ov.Disabled)
        {
            ov.RunWave(w.Routing.CustomizeBudgetPerTick);
        }

        ref SimCounters c = ref w.Counters;
        c.CustomizeRows = ov.CustomizeRows;
        c.CustomizeWork = ov.CustomizeWork;
        c.OverlayRebuilds = ov.Rebuilds;
        c.OverlayDisabled = ov.Disabled ? 1 : 0;
    }

    // ---- перемаршрутизация ----

    private static void RerouteVehicles(World w, Router rt, ref Budget budget)
    {
        RingBuffer<RerouteEntry> q = w.Rerouting;
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int n = q.Count;
        int timedCap = budget.MaxQueries == int.MaxValue ? int.MaxValue : Math.Max(1, budget.MaxQueries / TimedBudgetDivisor);
        int timedServed = 0;

        // Плановые запросы (дрейф) не должны вытеснять поездки: им не больше 1/TimedBudgetDivisor и числа запросов, и работы (на s100k
        // запрос стоит ~20 тыс. релаксаций, и без лимита по работе плановые съедали половину work_budget, поездки голодали - рампа вставала).
        int timedWorkCap = budget.MaxWork == int.MaxValue ? int.MaxValue : Math.Max(1, budget.MaxWork / TimedBudgetDivisor);
        int timedWork = 0;
        for (int k = 0; k < n; k++)
        {
            q.TryPeek(out RerouteEntry e);
            int i = e.Index;
            if (e.Kind == RouteNav.KindTimed)
            {
                if (!v.Pool.IsAlive(i, e.Generation) || v.Route[i] < 0 || v.Lane[i] < 0 ||
                    net.Lanes[v.Lane[i]].Kind != LaneKind.Normal || (v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0 ||
                    w.Tick - e.ReadyTick > TimedExpiryTicks)
                {
                    q.TryDequeue(out _);
                    continue;
                }

                if (timedServed >= timedCap || timedWork >= timedWorkCap)
                {
                    q.TryDequeue(out _); // доля бюджета (запросы/работа) на плановые запросы исчерпана: в хвост
                    q.TryEnqueue(in e);
                    continue;
                }

                if (!budget.Has(rt))
                {
                    budget.Stopped = true;
                    return;
                }

                int work0 = rt.WorkThisTick;
                BuildStatus ts = TimedReroute(w, rt, i);
                timedWork += rt.WorkThisTick - work0;
                if (ts == BuildStatus.Deferred)
                {
                    budget.Stopped = true;
                    return;
                }

                q.TryDequeue(out _);
                if (ts != BuildStatus.Skipped)
                {
                    budget.Served++;
                    timedServed++;
                }

                continue;
            }

            if (!v.Pool.IsAlive(i, e.Generation) || v.Route[i] >= 0 || v.Lane[i] < 0)
            {
                q.TryDequeue(out _); // машина уже прибыла / исчезла
                continue;
            }

            if (e.ReadyTick > w.Tick)
            {
                q.TryDequeue(out _); // ещё рано: в хвост
                q.TryEnqueue(in e);
                continue;
            }

            if (!budget.Has(rt))
            {
                budget.Stopped = true;
                return;
            }

            int lane = v.Lane[i];
            float s = v.S[i];
            int cursor = 0;
            if (net.Lanes[lane].Kind == LaneKind.Connector)
            {
                // Маршрут начинается с полосы за connector'ом; курсор -1: connector в маршрут не входит.
                lane = net.Lanes[lane].ToLane;
                s = 0f;
                cursor = -1;
            }

            RouteHandle h = RouteHandle.Null;
            BuildStatus st = lane >= 0
                ? rt.Build(lane, s, v.DestLane[i], v.DestS[i], w.Tick, out h)
                : BuildStatus.NoPath;
            if (st == BuildStatus.Deferred)
            {
                budget.Stopped = true;
                return; // нет слота / места в арене плана: голова остаётся в очереди
            }

            budget.Served++;
            q.TryDequeue(out _);
            if (st == BuildStatus.Ok)
            {
                v.Route[i] = h.Index;
                v.RouteGen[i] = h.Generation;
                v.RouteCursor[i] = cursor;
                v.MarkRouteDirty(i);
                v.NextConn[i] = -1; // пересчитает AssignConnectors в конце фазы 7
            }
            else
            {
                e.ReadyTick = w.Tick + RouteNav.RerouteRetryTicks;
                q.TryEnqueue(in e);
            }
        }
    }

    // ---- плановая перемаршрутизация по дрейфу (ADR-0006, routing.md 9.7) ----

    // Каждый тик берёт машины с индексом i % TimedPeriod == Tick % TimedPeriod: за период обходится каждая живая машина один раз.
    // Проверка дрейфа - без поиска: остаток стоимости плана при текущих весах против ожидаемого; запрос ставится только при росте.
    private static void EnqueueTimed(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        Router rt = w.Router;
        RoutingDesc cfg = w.Routing;
        int hw = v.HighWater;
        for (int i = (int)(w.Tick % TimedPeriod); i < hw; i += TimedPeriod)
        {
            int lane = v.Lane[i];
            if (lane < 0 || !v.Pool.IsAlive(i) || v.Route[i] < 0 || net.Lanes[lane].Kind != LaneKind.Normal ||
                (v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0 || v.DestLane[i] < 0 || v.RouteCursor[i] < 0)
            {
                continue;
            }

            if (net.Segments[net.Lanes[lane].Segment].Length - v.S[i] < TimedMinSegmentRemaining)
            {
                continue;
            }

            w.Counters.TimedChecks++;
            int r = v.Route[i];
            float remaining = rt.RemainingCost(i);
            float expected = Math.Max(w.Routes.PlanCost[r] - ((w.Tick - w.Routes.PlanTick[r]) * Dt), 0f);
            if (!(remaining > (expected * (1f + cfg.TimedDrift)) + (cfg.TimedDriftAbsTicks * Dt)))
            {
                continue;
            }

            w.Counters.TimedTriggered++;
            var entry = new RerouteEntry { Index = i, Generation = v.Pool.GenerationOf(i), ReadyTick = w.Tick, Kind = RouteNav.KindTimed };
            w.Rerouting.TryEnqueue(in entry);
        }
    }

    // Skipped - ничего не строилось (бюджет не тратится); Ok - искали (маршрут заменён или нет); NoPath - поиск без результата.
    private static BuildStatus TimedReroute(World w, Router rt, int i)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int lane = v.Lane[i];
        if (net.Segments[net.Lanes[lane].Segment].Length - v.S[i] < TimedMinSegmentRemaining)
        {
            return BuildStatus.Skipped;
        }

        int route = v.Route[i];
        int cursor = v.RouteCursor[i];
        if (cursor < 0 || cursor >= w.Routes.WinCount[route])
        {
            return BuildStatus.Skipped;
        }

        float oldCost = rt.RemainingCost(i);
        BuildStatus st = rt.Build(lane, v.S[i], v.DestLane[i], v.DestS[i], w.Tick, out RouteHandle h);
        if (st != BuildStatus.Ok)
        {
            return st;
        }

        if (w.Routes.PlanCost[h.Index] < oldCost * (1f - TimedGain))
        {
            w.Routes.Free(new RouteHandle(route, v.RouteGen[i]));
            v.Route[i] = h.Index;
            v.RouteGen[i] = h.Generation;
            v.RouteCursor[i] = 0;
            v.MarkRouteDirty(i);
            v.NextConn[i] = -1;
            w.Counters.TimedReroutes++;
        }
        else
        {
            w.Routes.Free(h);
            w.Routes.PlanCost[route] = oldCost; // новая точка отсчёта дрейфа: иначе проверка срабатывала бы каждый период
            w.Routes.PlanTick[route] = w.Tick;
        }

        return BuildStatus.Ok;
    }

    // ---- поездки ----

    private static void RouteTrips(World w, Router rt, ref Budget budget)
    {
        PendingTrips trips = w.Trips;
        NetworkStore net = w.Net;
        int k = 0;
        int seen = 0;
        int unrouted = trips.Unrouted;
        while (seen < unrouted && k < trips.Count)
        {
            PendingTrip t = trips.At(k);
            if (t.HasRoute)
            {
                k++;
                continue;
            }

            seen++;
            if (t.DestLane < 0 && !Destinations.Pick(net, t.ToZone, ref w.RngRouting, out t.DestLane, out t.DestS))
            {
                k++; // у зоны назначения нет полос: поездка ждёт (как зона без полос спавна), бюджет не тратим
                continue;
            }

            int fromZone = t.FromZone;
            if (t.FromLane < 0)
            {
                if ((uint)fromZone >= (uint)net.ZoneCount || net.Zones[fromZone].SpawnLaneCount <= 0)
                {
                    trips.Set(k, in t);
                    k++;
                    continue;
                }

                ref readonly ZoneData z = ref net.Zones[fromZone];
                t.FromLane = net.ZoneLanes[z.FirstSpawnLane + (int)((w.Tick + t.ToZone) % z.SpawnLaneCount)];
            }

            if (!budget.Has(rt))
            {
                trips.Set(k, in t);
                budget.Stopped = true;
                return;
            }

            BuildStatus st = rt.Build(t.FromLane, t.FromS, t.DestLane, t.DestS, w.Tick, out RouteHandle h);
            if (st == BuildStatus.Deferred)
            {
                trips.Set(k, in t);
                budget.Stopped = true;
                return;
            }

            budget.Served++;
            if (st == BuildStatus.NoPath)
            {
                trips.RemoveAt(k); // не на дороге: поездка отбрасывается (RouteFailed считает роутер)
                w.Counters.TripsDropped++;
                if (t.ResidentRef != 0)
                {
                    City.TripLegs.OnTripDropped(w, t.ResidentRef - 1);   // нога жителя без пути: другой способ или отмена тура (очередь поездок не трогается)
                }

                continue;
            }

            t.Route = h.Index;
            t.RouteGen = h.Generation;
            trips.Set(k, in t);
            k++;
        }
    }
}
