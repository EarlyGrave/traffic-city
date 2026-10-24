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
    public static void Run(World w)
    {
        RouteBuilder rb = w.Router;
        rb.BeginTick();
        int budget = w.Caps.PathfindBudgetPerTick > 0 ? w.Caps.PathfindBudgetPerTick : int.MaxValue;
        int served = 0;
        bool stopped = false;

        RerouteVehicles(w, rb, budget, ref served, ref stopped);
        if (!stopped)
        {
            RouteTrips(w, rb, budget, ref served, ref stopped);
        }

        if (stopped)
        {
            w.Counters.RouteDeferred += w.Trips.Unrouted;
        }

        ref SimCounters c = ref w.Counters;
        c.PathfindQueries = rb.Queries;
        c.PathfindCacheHits = rb.CacheHits;
        c.PathfindExpandedNodes = rb.ExpandedNodes;
        c.RouteFailed = rb.Failed;
        c.RouteCompactions = rb.Compactions;
        c.RouteCacheSkips = rb.CacheSkips;
    }

    // ---- (2) перемаршрутизация ----

    private static void RerouteVehicles(World w, RouteBuilder rb, int budget, ref int served, ref bool stopped)
    {
        RingBuffer<RerouteEntry> q = w.Rerouting;
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int n = q.Count;
        for (int k = 0; k < n; k++)
        {
            q.TryPeek(out RerouteEntry e);
            int i = e.Index;
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
