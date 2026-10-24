using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Metrics;

/// <summary>Хэш состояния симуляции (core-api 9): FNV-1a 64 по битовым образам, порядок включения фиксирован.</summary>
internal static class StateHash
{
    public static ulong Compute(World w)
    {
        var h = Fnv1a64.Create();

        // 1. Время и версия сети.
        h.Add(w.Tick);
        h.Add(w.Net.Version);

        // 2. PRNG в порядке RngStream.
        AddRng(ref h, in w.RngCityGen);
        AddRng(ref h, in w.RngZones);
        AddRng(ref h, in w.RngSpawn);
        AddRng(ref h, in w.RngBehaviour);
        AddRng(ref h, in w.RngTransit);
        AddRng(ref h, in w.RngRouting);

        // 3. Машины по слотам.
        VehicleStore v = w.Vehicles;
        int high = v.HighWater;
        h.Add(high);
        for (int i = 0; i < high; i++)
        {
            h.Add(v.Pool.GenerationOf(i));
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            h.Add(v.Lane[i]);
            h.Add(v.S[i]);
            h.Add(v.Speed[i]);
            h.Add(v.Accel[i]);
            h.Add(v.Type[i]);
            h.Add(v.Route[i]);
            h.Add(v.RouteCursor[i]);
            h.Add((byte)v.LaneChangeDir[i]);
            h.Add(v.GhostLane[i]);
            h.Add(v.Flags[i]);
            h.Add((int)v.StillTicks[i]);
        }

        // 4. Полосы.
        int lanes = w.Net.LaneCount;
        h.Add(lanes);
        for (int l = 0; l < lanes; l++)
        {
            h.Add(w.Occupancy.LaneHead[l]);
            h.Add(w.Occupancy.LaneTail[l]);
            h.Add(w.Occupancy.LaneCount[l]);
        }

        // 5. Сигналы.
        h.Add(w.SignalCount);
        for (int s = 0; s < w.SignalCount; s++)
        {
            h.Add((int)w.SignalStates[s].Phase);
            h.Add((int)w.SignalStates[s].Interval);
            h.Add((int)w.SignalStates[s].TicksToChange);
        }

        // 6. Transit.
        for (int s = 0; s < w.Net.BusStopCount; s++)
        {
            h.Add((int)w.Net.BusStops[s].Waiting);
            h.Add(w.Net.BusStops[s].LastArrivalTick);
        }

        for (int r = 0; r < w.Net.BusRouteCount; r++)
        {
            h.Add(w.Net.BusRoutes[r].ActiveBuses);
            h.Add(w.Net.BusRoutes[r].LastDispatchTick);
        }

        // 7. Счётчики (по полям в порядке объявления).
        AddCounters(ref h, in w.Counters);

        // 8. Очередь поездок в порядке очереди.
        int pending = w.Trips.Count;
        h.Add(pending);
        for (int p = 0; p < pending; p++)
        {
            ref readonly PendingTrip t = ref w.Trips.At(p);
            h.Add(t.FromZone);
            h.Add(t.ToZone);
            h.Add(t.FromLane);
            h.Add(t.FromS);
            h.Add(t.Type);
        }

        // 9. Управляющее состояние команд.
        h.Add(w.TargetVehicles);
        h.Add(w.Selected.Packed);

        return h.Value;
    }

    private static void AddRng(ref Fnv1a64 h, in Pcg32 rng)
    {
        h.Add(rng.State);
        h.Add(rng.Inc);
    }

    private static void AddCounters(ref Fnv1a64 h, in SimCounters c)
    {
        h.Add(c.Ticks);
        h.Add(c.VehiclesSpawned);
        h.Add(c.TripsCompleted);
        h.Add(c.TripsDropped);
        h.Add(c.TripTimeTicksTotal);
        h.Add(c.DistanceTotalMm);
        h.Add(c.VehicleTicksTotal);
        h.Add(c.MovingVehicleTicksTotal);
        h.Add(c.ActiveVehicles);
        h.Add(c.StuckVehicles);
        h.Add(c.StuckEventsTotal);
        h.Add(c.StuckTicksTotal);
        h.Add(c.GridlockFlag);
        h.Add(c.GridlockTicksTotal);
        h.Add(c.LaneChanges);
        h.Add(c.LaneChangesAborted);
        h.Add(c.HardClamps);
        h.Add(c.PathfindQueries);
        h.Add(c.PathfindCacheHits);
        h.Add(c.RouteDeferred);
        h.Add(c.PathfindExpandedNodes);
        h.Add(c.ConnectorEntries);
        h.Add(c.CommandsApplied);
        h.Add(c.CommandsRejected);
        h.Add(c.CommandsDropped);
        h.Add(c.EventsDropped);
        h.Add(c.SpawnDeferredTicks);
        h.Add(c.InvalidHandleOps);
        h.Add(c.SignalizedNodes);
        h.Add(c.NodeCount);
        h.Add(c.SegmentCount);
        h.Add(c.LaneCount);
        h.Add(c.ZoneCount);
        h.Add(c.BusDispatched);
        h.Add(c.BusStopArrivals);
        h.Add(c.BusHeadwayDeviationTicksTotal);
        h.Add(c.BusPassengersBoarded);
    }
}
