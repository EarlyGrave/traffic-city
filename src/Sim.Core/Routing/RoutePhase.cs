using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Фаза 3 (core-api 7, routing.md 3): маршруты. Каждый тик: (1) <see cref="RouteBuilder.BeginTick"/>; (2) очередь перемаршрутизации
/// (машины, промахнувшиеся мимо полосы, едут по умолчанию) - не более <c>PathfindBudgetPerTick</c> запросов; неудача - повтор
/// через <see cref="RouteNav.RerouteRetryTicks"/>; (3) поездки <see cref="PendingTrips"/> без маршрута по порядку очереди в остатке
/// бюджета: точка прибытия (если ещё не выбрана), стартовая полоса, <see cref="RouteBuilder.Build"/>. Нет пути - поездка удаляется из
/// очереди (<c>TripsDropped</c>, <c>RouteFailed</c>): на дороге такая машина не появляется. Бюджет исчерпан или
/// <see cref="BuildStatus.Deferred"/> - остальные ждут следующего тика (<c>RouteDeferred</c> += число ждущих поездок). Поездка, для которой
/// нельзя выбрать точку прибытия/старт (зона без полос), остаётся в очереди без затрат бюджета. В конце тика счётчики построителя
/// переносятся в <see cref="SimCounters"/>. Аллокаций нет.
/// </summary>
internal static class RoutePhase
{
    /// <summary>Период пересчёта весов по загрузке по умолчанию, тиков (30 с, ADR-0006); сценарий задаёт <c>routing.cost_recalc_s</c>.</summary>
    public const int DefaultCostRecalcTicks = 600;

    /// <summary>Период плановой перемаршрутизации одной машины, тиков (60 с); машины разнесены по индексу.</summary>
    public const int TimedPeriod = 1200;

    /// <summary>Минимальный остаток маршрута для плановой перемаршрутизации, м.</summary>
    public const float TimedMinRemaining = 1000f;

    /// <summary>Новый маршрут принимается, если дешевле старого хотя бы на эту долю.</summary>
    public const float TimedGain = 0.10f;

    /// <summary>Не перестраивать, если до конца сегмента меньше, м (машина уже готовится к повороту).</summary>
    public const float TimedMinSegmentRemaining = 80f;

    /// <summary>Устаревшая плановая запись (не дошла очередь) отбрасывается через столько тиков.</summary>
    public const int TimedExpiryTicks = 600;

    /// <summary>Доля <c>PathfindBudgetPerTick</c> на плановые запросы: 1/<c>TimedBudgetDivisor</c>.</summary>
    public const int TimedBudgetDivisor = 4;

    public static void Run(World w)
    {
        RouteBuilder rb = w.Router;
        rb.BeginTick();
        int budget = w.Caps.PathfindBudgetPerTick > 0 ? w.Caps.PathfindBudgetPerTick : int.MaxValue;
        int served = 0;
        bool stopped = false;

        RecalcWeights(w, rb);

        EnqueueTimed(w);
        RerouteVehicles(w, rb, budget, ref served, ref stopped);
        if (!stopped)
        {
            RouteTrips(w, rb, budget, ref served, ref stopped);
        }

        if (stopped)
        {
            w.Counters.RouteDeferred += w.Trips.Unrouted;
        }

        RunCustomizeWave(w);

        ref SimCounters c = ref w.Counters;
        c.PathfindQueries = rb.Queries;
        c.PathfindCacheHits = rb.CacheHits;
        c.PathfindExpandedNodes = rb.ExpandedNodes;
        c.RouteFailed = rb.Failed;
        c.RouteCompactions = rb.Compactions;
        c.RouteCacheSkips = rb.CacheSkips;
    }

    // Пересчёт весов по номеру тика (ADR-0006). Пока идёт волна кастомизации оверлея, новый пересчёт откладывается до её конца
    // (routing.md 9.3 п. 4): RecalcPending держит просроченный пересчёт, каждый тик ожидания - CustomizeLagTicks.
    private static void RecalcWeights(World w, RouteBuilder rb)
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
        if (rb.Graph.BuiltVersion != w.Net.Version)
        {
            rb.Graph.Rebuild();
        }

        rb.Graph.RecalcCosts(w.SegmentLoads, w.Net.SegmentCount);
        w.Counters.CostRecalcs++;
        if (overlay)
        {
            ov!.AfterRecalc(rb.Graph.WeightsVersion);
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

    // ---- (2) перемаршрутизация ----

    private static void RerouteVehicles(World w, RouteBuilder rb, int budget, ref int served, ref bool stopped)
    {
        RingBuffer<RerouteEntry> q = w.Rerouting;
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int n = q.Count;
        int timedCap = budget == int.MaxValue ? int.MaxValue : Math.Max(1, budget / TimedBudgetDivisor);
        int timedServed = 0;
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

                if (timedServed >= timedCap)
                {
                    q.TryDequeue(out _); // доля бюджета на плановые запросы исчерпана: в хвост
                    q.TryEnqueue(in e);
                    continue;
                }

                if (served >= budget)
                {
                    stopped = true;
                    return;
                }

                BuildStatus ts = TimedReroute(w, rb, i);
                if (ts == BuildStatus.Deferred)
                {
                    stopped = true;
                    return;
                }

                q.TryDequeue(out _);
                if (ts != BuildStatus.Skipped)
                {
                    served++;
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

            if (served >= budget)
            {
                stopped = true;
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
                ? rb.Build(lane, s, v.DestLane[i], v.DestS[i], -1, v.DestZone[i], w.Tick, out h)
                : BuildStatus.NoPath;
            if (st == BuildStatus.Deferred)
            {
                stopped = true;
                return; // бюджет раскрытий / нет места в пуле: голова остаётся в очереди
            }

            served++;
            q.TryDequeue(out _);
            if (st == BuildStatus.Ok)
            {
                v.Route[i] = h.Index;
                v.RouteGen[i] = h.Generation;
                v.RouteCursor[i] = cursor;
                v.NextConn[i] = -1; // пересчитает AssignConnectors в конце фазы 7
            }
            else
            {
                e.ReadyTick = w.Tick + RouteNav.RerouteRetryTicks;
                q.TryEnqueue(in e);
            }
        }
    }

    // ---- плановая перемаршрутизация (ADR-0006) ----

    // Каждый тик берёт машины с индексом i % TimedPeriod == Tick % TimedPeriod: за период обходится каждая живая машина один раз.
    private static void EnqueueTimed(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int hw = v.HighWater;
        for (int i = (int)(w.Tick % TimedPeriod); i < hw; i += TimedPeriod)
        {
            int lane = v.Lane[i];
            if (lane < 0 || !v.Pool.IsAlive(i) || v.Route[i] < 0 || net.Lanes[lane].Kind != LaneKind.Normal ||
                (v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0 || v.DestLane[i] < 0)
            {
                continue;
            }

            if (net.Segments[net.Lanes[lane].Segment].Length - v.S[i] < TimedMinSegmentRemaining ||
                RemainingLength(w, i) <= TimedMinRemaining)
            {
                continue;
            }

            var entry = new RerouteEntry { Index = i, Generation = v.Pool.GenerationOf(i), ReadyTick = w.Tick, Kind = RouteNav.KindTimed };
            w.Rerouting.TryEnqueue(in entry);
        }
    }

    // Длина маршрута от текущей позиции до конца, м (по обычным полосам шагов от курсора).
    private static float RemainingLength(World w, int i)
    {
        VehicleStore v = w.Vehicles;
        int r = v.Route[i];
        int first = w.Routes.First[r];
        int count = w.Routes.Count[r];
        float len = -v.S[i];
        for (int c = Math.Max(0, v.RouteCursor[i]); c < count; c++)
        {
            int lane = w.Routes.Steps[first + c].Lane;
            ref readonly LaneData ld = ref w.Net.Lanes[lane];
            if (ld.Kind == LaneKind.Normal)
            {
                len += w.Net.Segments[ld.Segment].Length;
            }
        }

        return len;
    }

    // Стоимость маршрута с шага cursor (чётный), без первого сегмента: сумма дуг и весов сегментов графа, с.
    private static float RouteCost(World w, int route, int cursor)
    {
        RouteGraph g = w.Router.Graph;
        int first = w.Routes.First[route];
        int count = w.Routes.Count[route];
        float cost = 0f;
        int prev = RouteGraph.DirSegOfLane(w.Net, w.Routes.Steps[first + cursor].Lane);
        for (int c = cursor + 2; c < count; c += 2)
        {
            int u = RouteGraph.DirSegOfLane(w.Net, w.Routes.Steps[first + c].Lane);
            int arc = g.FindArc(prev, u);
            cost += g.SegCost[u] + (arc >= 0 ? g.ArcCost[arc] : 0f);
            prev = u;
        }

        return cost;
    }

    // Skipped - ничего не строилось (бюджет не тратится); Ok - искали (маршрут заменён или нет); NoPath - поиск без результата.
    private static BuildStatus TimedReroute(World w, RouteBuilder rb, int i)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int lane = v.Lane[i];
        if (net.Segments[net.Lanes[lane].Segment].Length - v.S[i] < TimedMinSegmentRemaining)
        {
            return BuildStatus.Skipped;
        }

        int cursor = v.RouteCursor[i];
        int route = v.Route[i];
        if (cursor < 0 || (cursor & 1) != 0 || cursor >= w.Routes.Count[route])
        {
            return BuildStatus.Skipped;
        }

        BuildStatus st = rb.Build(lane, v.S[i], v.DestLane[i], v.DestS[i], -1, v.DestZone[i], w.Tick, out RouteHandle h);
        if (st != BuildStatus.Ok)
        {
            return st;
        }

        // Build мог уплотнить арену, но handle старого маршрута остаётся валидным (меняется только First).
        float oldCost = RouteCost(w, route, cursor);
        float newCost = RouteCost(w, h.Index, 0);
        if (newCost < oldCost * (1f - TimedGain))
        {
            w.Routes.Free(new RouteHandle(route, v.RouteGen[i]));
            v.Route[i] = h.Index;
            v.RouteGen[i] = h.Generation;
            v.RouteCursor[i] = 0;
            v.NextConn[i] = -1;
            w.Counters.TimedReroutes++;
        }
        else
        {
            w.Routes.Free(h);
        }

        return BuildStatus.Ok;
    }

    // ---- (3) поездки ----

    private static void RouteTrips(World w, RouteBuilder rb, int budget, ref int served, ref bool stopped)
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

            if (served >= budget)
            {
                trips.Set(k, in t);
                stopped = true;
                return;
            }

            BuildStatus st = rb.Build(t.FromLane, t.FromS, t.DestLane, t.DestS, t.FromZone, t.ToZone, w.Tick, out RouteHandle h);
            if (st == BuildStatus.Deferred)
            {
                trips.Set(k, in t);
                stopped = true;
                return;
            }

            served++;
            if (st == BuildStatus.NoPath)
            {
                trips.RemoveAt(k); // не на дороге: поездка отбрасывается (RouteFailed считает построитель)
                w.Counters.TripsDropped++;
                continue;
            }

            t.Route = h.Index;
            t.RouteGen = h.Generation;
            trips.Set(k, in t);
            k++;
        }
    }
}
