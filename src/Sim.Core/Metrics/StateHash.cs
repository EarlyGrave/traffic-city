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
        bool lodOn = w.Lod != null; // п.3/п.10 (ADR-0013): при Lod.Mode = Off ничего не добавляется - хэш прежний
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
            h.Add(v.RouteGen[i]);
            h.Add(v.RouteCursor[i]);
            h.Add(v.DestLane[i]);
            h.Add(v.DestS[i]);
            h.Add((byte)v.LaneChangeDir[i]);
            h.Add(v.GhostLane[i]);
            h.Add(v.Flags[i]);
            h.Add((int)v.StillTicks[i]);
            h.Add(v.DestZone[i]);
            h.Add(v.NextConn[i]);
            h.Add(v.SpawnTick[i]);
            h.Add(v.GateState[i]);
            h.Add(v.GrantConnector[i]);
            h.Add(v.Ticket[i]);
            h.Add((int)v.WaitTicks[i]);
            if (lodOn)
            {
                byte lod = v.Lod[i];
                h.Add(lod);
                if ((lod & Lod.LodBits.Meso) != 0)
                {
                    h.Add(v.MesoAllow[i]);
                }
            }
        }

        // 4. Полосы.
        int lanes = w.Net.LaneCount;
        h.Add(lanes);
        for (int l = 0; l < lanes; l++)
        {
            h.Add(w.Occupancy.LaneHead[l]);
            h.Add(w.Occupancy.LaneTail[l]);
            h.Add(w.Occupancy.LaneCount[l]);
            h.Add(w.Net.OutReserved[l]);
        }

        // 4a. Узлы: билеты ворот.
        int nodes = w.Net.NodeCount;
        h.Add(nodes);
        for (int n = 0; n < nodes; n++)
        {
            h.Add(w.Net.NodeTicket[n]);
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
        h.Add(w.Graph.WeightsVersion);

        // 7a. Оверлей маршрутизации (routing.md 9.8): курсор волны и версия весов; матрицы производные - не хэшируются.
        Routing.Overlay? ov = w.Overlay;
        if (ov != null)
        {
            h.Add(ov.WeightsVersion);
            h.Add(ov.WaveActive ? 1 : 0);
            h.Add(ov.WaveCell);
            h.Add(ov.WaveRow);
            h.Add(ov.Disabled ? 1 : 0);
            h.Add(w.RecalcPending ? 1 : 0);
            h.Add(w.Counters.CustomizeRows);
            h.Add(w.Counters.CustomizeWork);
            h.Add(w.Counters.CustomizeLagTicks);
            h.Add(w.Counters.OverlayRebuilds);
        }

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
            h.Add(t.DestLane);
            h.Add(t.DestS);
            h.Add(t.Route);
            h.Add(t.RouteGen);
        }

        // 8a. Очередь перемаршрутизации в порядке очереди.
        int rer = w.Rerouting.Count;
        h.Add(rer);
        for (int p = 0; p < rer; p++)
        {
            ref readonly Routing.RerouteEntry re = ref w.Rerouting.At(p);
            h.Add(re.Index);
            h.Add(re.Generation);
            h.Add(re.ReadyTick);
            h.Add(re.Kind);
        }

        // 8b. Очередь дозаполнения окон в порядке очереди и живые слоты RouteStore (routing.md 9.8): окна и планы целиком не хэшируются -
        // они производные от маршрута и весов; WinCount, PlanCount, PlanCost достаточны для обнаружения расхождения.
        int refills = w.RefillQueue.Count;
        h.Add(refills);
        for (int p = 0; p < refills; p++)
        {
            ref readonly Routing.RefillEntry rf = ref w.RefillQueue.At(p);
            h.Add(rf.Index);
            h.Add(rf.Generation);
        }

        Routing.RouteStore rs = w.Routes;
        int slots = rs.Pool.HighWater;
        h.Add(slots);
        for (int r = 0; r < slots; r++)
        {
            h.Add(rs.Pool.GenerationOf(r));
            if (!rs.Pool.IsAlive(r))
            {
                continue;
            }

            h.Add((int)rs.WinCount[r]);
            h.Add(rs.PlanCount[r]);
            h.Add(rs.PlanCost[r]);
        }

        // 9. Управляющее состояние команд.
        h.Add(w.TargetVehicles);
        h.Add(w.Selected.Packed);
        h.Add(w.GridlockStreak);

        // 10. Уровни детализации (core-api 9 п.10, только при Lod.Mode != Off). RenderView не входит.
        if (w.Lod != null)
        {
            Lod.LodCells lc = w.Lod;
            h.Add(lc.Cells);
            for (int c = 0; c < lc.Cells; c++)
            {
                h.Add(lc.CellLevel[c]);
                h.Add(lc.CellHoldUntil[c]);
                h.Add(lc.CellCongStreak[c]);
            }

            for (int s = 0; s < Lod.LodCells.SlotCount; s++)
            {
                Lod.LodFocusSlot f = lc.Focus[s];
                h.Add(f.Kind);
                h.Add(f.Cx0);
                h.Add(f.Cz0);
                h.Add(f.Cx1);
                h.Add(f.Cz1);
                h.Add(f.CenterCx);
                h.Add(f.CenterCz);
            }

            h.Add(lc.LodEvalRequested ? 1 : 0);
            h.Add(lc.LastLodEvalTick);
        }

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
        h.Add(c.RouteDeferred);
        h.Add(c.PathfindExpandedNodes);
        h.Add(c.RouteFailed);
        h.Add(c.Reroutes);
        h.Add(c.PathfindWork);
        h.Add(c.PlanCompactions);
        h.Add(c.PlanUnpacks);
        h.Add(c.UnpackWork);
        h.Add(c.WindowRefills);
        h.Add(c.RefillLate);
        h.Add(c.TimedChecks);
        h.Add(c.TimedTriggered);
        h.Add(c.CostRecalcs);
        h.Add(c.TimedReroutes);
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
        h.Add(c.BusHeadwaySamples);
        h.Add(c.BusPassengersBoarded);
        h.Add(c.Grants);
        h.Add(c.GateDeniedExit);
        h.Add(c.PriorityInversions);
        h.Add(c.BoxWaitTicks);
        h.Add(c.GateWaitTicksTotal);
    }
}
