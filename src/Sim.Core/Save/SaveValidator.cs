using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Save;

/// <summary>
/// Проверки загруженного состояния (save-format.md §6.1): ёмкости, диапазоны ссылок, целостность пулов и списков полос, конечность чисел. Вызываются стадиями
/// загрузки (хуки блоков <see cref="BlockDef.Before"/>/<see cref="BlockDef.After"/>) сразу, как появляется всё нужное для стадии, и всегда <b>до</b> кода, который
/// ходит по этим данным (построение производных, обход списков). Ошибка - код <see cref="LoadStatus"/> и место в <see cref="LoadContext.FailColumn"/>/<see cref="LoadContext.FailRow"/>.
/// После <c>Ok</c> <c>Step</c> безопасен: ни одна ссылка не выходит за массив.
/// </summary>
internal static class SaveValidator
{
    private const float SBackTolerance = 1f;      // м: допуск S ниже нуля
    private const float SForwardTolerance = 20f;  // м: допуск S выше длины полосы (машина дошла до конца, переход - в фазе 7)
    private const long MaxEstimatedBytes = 16L << 30;

    private static LoadStatus Bad(LoadContext c, long row, uint column = 0, LoadStatus st = LoadStatus.InvalidValue)
    {
        c.FailRow = row;
        c.FailColumn = column;
        return st;
    }

    private static bool Fin(float v) => float.IsFinite(v);

    // ---------------------------------------------------------------- параметры (до создания мира)

    public static LoadStatus ValidateParams(LoadContext c)
    {
        if (WorldBuilder.ValidateCapacities(in c.Desc.Capacities) != CreateStatus.Ok)
        {
            return LoadStatus.CapacityExceeded;
        }

        c.Rules = WorldBuilder.WithDefaults(c.Rules);
        c.Routing = WorldBuilder.WithDefaults(c.Routing);
        c.Lod = WorldBuilder.WithDefaults(c.Lod);
        c.Desc.Rules = c.Rules;
        c.Desc.Routing = c.Routing;
        c.Desc.Lod = c.Lod;
        if (c.Rules.DefaultYellowTicks < 0 || c.Rules.DefaultAllRedTicks < 0 || !Fin(c.Rules.MinPhysicalGap) || !Fin(c.Rules.StuckSpeedThreshold) ||
            !Fin(c.Rules.GridlockStuckShare) || !Fin(c.Rules.YieldGapTime))
        {
            return LoadStatus.InvalidValue;
        }

        if (WorldBuilder.ValidateRouting(in c.Desc) != CreateStatus.Ok || WorldBuilder.ValidateLod(in c.Desc.Lod) != CreateStatus.Ok ||
            WorldBuilder.ValidateVehicleTypes(c.Types) != CreateStatus.Ok || WorldBuilder.ValidateDemand(in c.Desc) != CreateStatus.Ok ||
            WorldBuilder.ValidateTransit(in c.Desc) != CreateStatus.Ok)
        {
            return LoadStatus.InvalidValue;
        }

        DemandDesc d = c.Desc.Demand;
        TransitDesc tr = c.Desc.Transit;
        if (!Fin(d.ZoneSize) || !Fin(d.GravityExponent) || !Fin(d.WeightJitter) || !Fin(d.ExternalShare) || !Fin(d.MinTripLength) || !Fin(tr.StopSpacing) ||
            !Fin(tr.DwellPerPassengerTicks) || !Fin(tr.PassengerRatePerMin) || !Fin(tr.HoldAlpha) || tr.HoldAlpha < 0f || tr.HoldMaxTicks < 0 || tr.HoldEveryN < 0 || tr.Routes < 0 || tr.DwellBaseTicks < 0)
        {
            return LoadStatus.InvalidValue;
        }

        if (c.Lod.Mode != LodMode.Off && c.Lod.MesoModel == MesoModel.Queue && c.Lod.Mode != LodMode.AllMeso)
        {
            return LoadStatus.InvalidValue;   // Q-meso только в all_meso (WorldBuilder.BuildLod)
        }

        if (!Fin(c.NetLaneWidth) || c.NetLaneWidth <= 0f || c.NetLaneWidth > 100f || c.NetConnectorSamples < 2 || c.NetConnectorSamples > 64)
        {
            return LoadStatus.InvalidValue;
        }

        return Estimate(in c.Desc.Capacities, c.Routing.WindowSteps) > MaxEstimatedBytes ? LoadStatus.CapacityExceeded : LoadStatus.Ok;
    }

    /// <summary>Грубая верхняя оценка памяти мира по ёмкостям (защита от файла, просящего гигабайты у крошечного сейва).</summary>
    private static long Estimate(in SimCapacities c, int windowSteps)
    {
        long b = (long)c.MaxVehicles * 420;
        b += (long)c.MaxLanes * 520;
        b += (long)c.MaxLanePoints * 40;
        b += (long)c.MaxNodes * 220;
        b += (long)c.MaxSegments * 700;
        b += (long)c.MaxRoutes * ((windowSteps * 8L) + 64);
        b += (long)c.MaxPlanSteps * 4;
        b += (long)c.MaxPendingTrips * 40;
        b += (long)c.MaxConnectors * 120;
        b += (long)c.MaxConflicts * 8;
        b += (long)c.MaxVehicles * 64;   // очереди
        return b;
    }

    // ---------------------------------------------------------------- сеть (перед VEHC: проверки + производные)

    /// <summary>Статическая сеть, connector'ы, светофоры и динамика уровня сети; затем производные (тем же кодом, что <c>Create</c>/<c>BuildRoad</c>).</summary>
    public static LoadStatus ValidateNetworkAndDerive(LoadContext c)
    {
        World w = c.W;
        NetworkStore n = w.Net;
        LoadStatus st = ValidateNetwork(c, w, n);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        n.RebuildLaneDerived();
        if (w.Builder.RebuildIndexes() != NetworkStatus.Ok)
        {
            return LoadStatus.CapacityExceeded;
        }

        for (int l = 0; l < n.LaneCount; l++)
        {
            // Производное ворот обязано совпасть с прежним расчётом по цепочке полос (иначе фазы 4-7 увидели бы рассинхрон).
            bool same = n.Lanes[l].Kind == LaneKind.Connector
                ? (n.LaneGateNode[l] >= 0) == GateRules.IsGatedConnectorSlow(n, in n.Lanes[l])
                : n.LaneGateNode[l] == GateRules.GateNodeSlow(n, l);
            if (!same)
            {
                return Bad(c, l, 25);
            }
        }

        ConnectorStore cs = w.Connectors;
        for (int g = 0; g < cs.Count; g++)
        {
            cs.Length[g] = n.Lanes[cs.Lane[g]].Length;
            cs.InLength[g] = n.Lanes[cs.InLane[g]].Length;
        }

        // Статика светофоров (рукава, оси, входные полосы Adaptive): производное от сети, строится тем же кодом, что при TryAdd.
        w.Signals.RebuildStatics(w);

        return LoadStatus.Ok;
    }

    private static LoadStatus ValidateNetwork(LoadContext c, World w, NetworkStore n)
    {
        int nodes = n.NodeCount;
        int segs = n.SegmentCount;
        int lanes = n.LaneCount;
        int lanePts = n.LanePointCount;
        int segPts = n.SegmentPointCount;
        int arms = n.NodeArmCount;
        int next = n.LaneNextCount;
        int zones = n.ZoneCount;
        int stops = n.BusStopCount;
        int routes = n.BusRouteCount;
        ConnectorStore cs = w.Connectors;
        SignalStore sg = w.Signals;

        for (int i = 0; i < nodes; i++)
        {
            ref readonly NodeData nd = ref n.Nodes[i];
            bool built = nd.FirstArm >= 0;
            if (!Fin(nd.Position.X) || !Fin(nd.Position.Z) || !Fin(nd.Elevation) || !Fin(nd.Radius) || nd.Radius < 0f ||
                (uint)nd.Control > (uint)NodeControl.RightHandRule || nd.Signal < -1 || nd.Signal >= sg.Count || nd.Zone < -1 || nd.Zone >= zones)
            {
                return Bad(c, i, 1);
            }

            if (built)
            {
                if (nd.ArmCount < 0 || nd.ArmCount > NetworkStore.MaxArms || (long)nd.FirstArm + nd.ArmCount > arms || nd.FirstConnector < 0 ||
                    nd.ConnectorCount < 0 || nd.ConnectorCount > NetworkBuilder.MaxConnectorsPerNode || (long)nd.FirstConnector + nd.ConnectorCount > lanes)
                {
                    return Bad(c, i, 2);
                }
            }
            else if (nd.ArmCount != 0 || nd.ConnectorCount != 0)
            {
                return Bad(c, i, 2);
            }
        }

        for (int i = 0; i < segs; i++)
        {
            ref readonly SegmentData sd = ref n.Segments[i];
            if ((uint)sd.NodeA >= (uint)nodes || (uint)sd.NodeB >= (uint)nodes || !Fin(sd.Length) || sd.Length <= 0f || !Fin(sd.SpeedLimit) || sd.SpeedLimit <= 0f ||
                (byte)sd.Kind > (byte)RoadKind.Arterial || sd.LanesAB > NetworkBuilder.MaxLanesPerDirection || sd.LanesBA > NetworkBuilder.MaxLanesPerDirection ||
                sd.PointCount < 2 || sd.FirstPoint < 0 || (long)sd.FirstPoint + sd.PointCount > segPts)
            {
                return Bad(c, i, 3);
            }

            if ((sd.LanesAB > 0 && (sd.FirstLaneAB < 0 || (long)sd.FirstLaneAB + sd.LanesAB > lanes)) ||
                (sd.LanesBA > 0 && (sd.FirstLaneBA < 0 || (long)sd.FirstLaneBA + sd.LanesBA > lanes)))
            {
                return Bad(c, i, 4);
            }
        }

        for (int i = 0; i < lanes; i++)
        {
            ref readonly LaneData l = ref n.Lanes[i];
            if ((byte)l.Kind > (byte)LaneKind.BusBay || l.Direction > 1 || !Fin(l.Length) || l.Length <= 0f || !Fin(l.SpeedLimit) || l.SpeedLimit < 0f ||
                l.PointCount < 2 || l.FirstPoint < 0 || (long)l.FirstPoint + l.PointCount > lanePts || l.FirstNext < 0 || l.NextCount < 0 ||
                (long)l.FirstNext + l.NextCount > next || l.Left < -1 || l.Left >= lanes || l.Right < -1 || l.Right >= lanes || l.ConflictGroup < -1 ||
                l.ConflictGroup >= cs.Count)
            {
                return Bad(c, i, 5);
            }

            if (l.Kind == LaneKind.Connector)
            {
                if ((uint)l.Node >= (uint)nodes || (uint)l.FromLane >= (uint)lanes || (uint)l.ToLane >= (uint)lanes ||
                    n.Lanes[l.FromLane].Kind == LaneKind.Connector || n.Lanes[l.ToLane].Kind == LaneKind.Connector)   // концы connector'а - обычные полосы (граф и ворота читают их Segment)
                {
                    return Bad(c, i, 6);
                }
            }
            else if ((uint)l.Segment >= (uint)segs || l.FromLane < -1 || l.FromLane >= lanes || l.ToLane < -1 || l.ToLane >= lanes || l.Node < -1 || l.Node >= nodes)
            {
                return Bad(c, i, 6);
            }

            if (l.ConflictGroup >= 0 && (l.Kind != LaneKind.Connector || cs.Lane[l.ConflictGroup] != i))
            {
                return Bad(c, i, 7);
            }
        }

        // Диапазон connector'ов узла: только connector'ы этого узла (RefreshLaneGateNode / BuildStatics читают FromLane и Segment по этим полосам).
        for (int i = 0; i < nodes; i++)
        {
            ref readonly NodeData nd = ref n.Nodes[i];
            if (nd.FirstArm < 0)
            {
                continue;
            }

            for (int p = 0; p < nd.ConnectorCount; p++)
            {
                ref readonly LaneData cl = ref n.Lanes[nd.FirstConnector + p];
                if (cl.Kind != LaneKind.Connector || cl.Node != i)
                {
                    return Bad(c, i, 2);
                }
            }
        }

        for (int i = 0; i < arms; i++)
        {
            if ((uint)n.NodeArms[i] >= (uint)segs || !Fin(n.ArmDir[i].X) || !Fin(n.ArmDir[i].Z))
            {
                return Bad(c, i, 8);
            }
        }

        for (int i = 0; i < next; i++)
        {
            if ((uint)n.LaneNext[i] >= (uint)lanes)
            {
                return Bad(c, i, 9);
            }
        }

        // Граф полос (его читают NextLane/DefaultConnector/ворота): обычная полоса ссылается только на свои connector'ы (FromLane == она сама),
        // connector - ровно на одну следующую полосу, свою ToLane. Иначе NextConn/ворота не сходятся с полным расчётом.
        for (int i = 0; i < lanes; i++)
        {
            ref readonly LaneData l = ref n.Lanes[i];
            if (l.Kind == LaneKind.Connector)
            {
                if (l.NextCount != 1 || n.LaneNext[l.FirstNext] != l.ToLane)
                {
                    return Bad(c, i, 29);
                }

                continue;
            }

            for (int k = 0; k < l.NextCount; k++)
            {
                ref readonly LaneData cl = ref n.Lanes[n.LaneNext[l.FirstNext + k]];
                if (cl.Kind != LaneKind.Connector || cl.FromLane != i)
                {
                    return Bad(c, i, 29);
                }
            }
        }

        for (int i = 0; i < segPts; i++)
        {
            if (!Fin(n.SegmentPoints[i].X) || !Fin(n.SegmentPoints[i].Z))
            {
                return Bad(c, i, 10);
            }
        }

        for (int i = 0; i < lanePts; i++)
        {
            if (!Fin(n.LanePoints[i].X) || !Fin(n.LanePoints[i].Z) || !Fin(n.LanePointS[i]))
            {
                return Bad(c, i, 11);
            }
        }

        for (int i = 0; i < lanes; i++)
        {
            ref readonly LaneData l = ref n.Lanes[i];
            for (int k = 1; k < l.PointCount; k++)
            {
                if (n.LanePointS[l.FirstPoint + k] < n.LanePointS[l.FirstPoint + k - 1])
                {
                    return Bad(c, i, 12);
                }
            }
        }

        for (int i = 0; i < zones; i++)
        {
            ref readonly ZoneData z = ref n.Zones[i];
            if (!Fin(z.Center.X) || !Fin(z.Center.Z) || !Fin(z.HalfSize) || !Fin(z.Weight) || z.FirstSpawnLane < 0 || z.SpawnLaneCount < 0 ||
                (long)z.FirstSpawnLane + z.SpawnLaneCount > n.ZoneLaneCount || z.IsExternal > 1)
            {
                return Bad(c, i, 13);
            }
        }

        for (int i = 0; i < n.ZoneLaneCount; i++)
        {
            if ((uint)n.ZoneLanes[i] >= (uint)lanes)
            {
                return Bad(c, i, 14);
            }
        }

        for (int i = 0; i < stops; i++)
        {
            ref readonly BusStopData s = ref n.BusStops[i];
            if ((uint)s.Lane >= (uint)lanes || !Fin(s.S) || (uint)s.Kind > (uint)BusStopKind.Bay || s.Route < -1 || s.Route >= routes)
            {
                return Bad(c, i, 15);
            }
        }

        for (int i = 0; i < routes; i++)
        {
            ref readonly BusRouteData r = ref n.BusRoutes[i];
            if (r.FirstStop < 0 || r.StopCount < 0 || (long)r.FirstStop + r.StopCount > stops || r.HeadwayTicks < 0 || r.ActiveBuses < 0 ||
                (uint)r.VehicleType >= (uint)w.Types.Length)
            {
                return Bad(c, i, 16);
            }
        }

        for (int i = 0; i < n.BusRouteStopCount; i++)
        {
            if ((uint)n.BusRouteStops[i] >= (uint)stops)
            {
                return Bad(c, i, 17);
            }
        }

        // connector'ы и конфликты
        for (int g = 0; g < cs.Count; g++)
        {
            int ln = cs.Lane[g];
            if ((uint)ln >= (uint)lanes || n.Lanes[ln].Kind != LaneKind.Connector || (uint)cs.Node[g] >= (uint)nodes || (uint)cs.InLane[g] >= (uint)lanes ||
                (uint)cs.OutLane[g] >= (uint)lanes || cs.LocalBit[g] >= NetworkBuilder.MaxConnectorsPerNode || cs.FirstConflict[g] < 0 ||
                cs.ConflictCount[g] < 0 || (long)cs.FirstConflict[g] + cs.ConflictCount[g] > cs.ConflictsUsed || cs.Maneuver[g] > (byte)Maneuver.UTurn)
            {
                return Bad(c, g, 18);
            }

            int cc = n.Nodes[cs.Node[g]].ConnectorCount;
            for (int e = cs.FirstConflict[g]; e < cs.FirstConflict[g] + cs.ConflictCount[g]; e++)
            {
                if (cs.Conflicts[e].OtherLocal >= cc)
                {
                    return Bad(c, e, 19);
                }
            }
        }

        // светофоры
        for (int s = 0; s < sg.Count; s++)
        {
            int nd = sg.Node[s];
            if ((uint)nd >= (uint)nodes || n.Nodes[nd].Signal != s || sg.PhaseCount[s] > SignalStore.MaxPhases || (uint)sg.Mode[s] > (uint)SignalMode.Adaptive ||
                (byte)sg.Interval[s] > (byte)SignalInterval.Off || sg.Phase[s] >= SignalStore.MaxPhases)
            {
                return Bad(c, s, 20);
            }

            if (sg.Machine[s] > 1 || sg.Sub[s] > 1 || sg.NextSub[s] > 1 || sg.NextAxis[s] >= SignalStore.MaxPhases)
            {
                return Bad(c, s, 26);
            }

            // Рукавов у узла <= ArmCap: биты стрелки за ними и подсчёт уступания по несуществующим рукавам читать нечем.
            int armMask = (1 << Math.Min(n.Nodes[nd].ArmCount, SignalStore.ArmCap)) - 1;
            if (((sg.ArrowArms[s] | sg.NextArrowArms[s]) & ~armMask) != 0)
            {
                return Bad(c, s, 27);
            }

            for (int a = 0; a < SignalStore.ArmCap; a++)
            {
                int head = sg.ArmLeftHead[(s * SignalStore.ArmCap) + a];
                if (head < -1 || head >= w.Caps.MaxVehicles)
                {
                    return Bad(c, s, 28);
                }
            }
        }

        for (int s = 0; s < w.SignalCount; s++)
        {
            if ((uint)w.SignalStates[s].Node >= (uint)nodes || (byte)w.SignalStates[s].Interval > (byte)SignalInterval.Off)
            {
                return Bad(c, s, 21);
            }
        }

        for (int i = 0; i < lanes; i++)
        {
            if (!Fin(n.OutReserved[i]) || w.LaneSignals[i] > (byte)LaneSignal.Green)
            {
                return Bad(c, i, 22);
            }
        }

        for (int i = 0; i < segs; i++)
        {
            if (!Fin(w.SegmentLoads[i].Density) || !Fin(w.SegmentLoads[i].SpeedRatio))
            {
                return Bad(c, i, 23);
            }
        }

        for (int i = 0; i < stops; i++)
        {
            if (n.BusStops[i].LastArrivalTick < -1 || n.BusStops[i].LastDepartTick < -1)
            {
                return Bad(c, i, 24);
            }
        }

        return LoadStatus.Ok;
    }

    // ---------------------------------------------------------------- машины, пулы, списки полос

    /// <summary>Пул, поля машин и списки полос (после VEHC; LDYN уже загружен).</summary>
    public static LoadStatus ValidateVehicles(LoadContext c)
    {
        World w = c.W;
        VehicleStore v = w.Vehicles;
        NetworkStore n = w.Net;
        int hw = v.HighWater;
        int lanes = n.LaneCount;
        LoadStatus st = ValidatePool(c, v.Pool);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int lane = v.Lane[i];
            if (lane < -1 || lane >= lanes || v.GhostLane[i] < -1 || v.GhostLane[i] >= lanes || !Fin(v.S[i]) || !Fin(v.Speed[i]) || v.Speed[i] < 0f ||
                !Fin(v.Accel[i]) || !Fin(v.Length[i]) || v.Length[i] <= 0f || v.Type[i] >= w.Types.Length || !Fin(v.DesiredSpeed[i]) ||
                !Fin(v.DestS[i]) || !Fin(v.Lateral[i]) || v.LaneChangeDir[i] < -1 || v.LaneChangeDir[i] > 1 || v.Flags[i] > 63)
            {
                return Bad(c, i, 30);
            }

            if (lane >= 0 && (v.S[i] < -SBackTolerance || v.S[i] > n.Lanes[lane].Length + SForwardTolerance))
            {
                return Bad(c, i, 31);
            }

            if (!LinkOk(v, v.Ahead[i], hw) || !LinkOk(v, v.Behind[i], hw) || !LinkOk(v, v.GhostAhead[i], hw) || !LinkOk(v, v.GhostBehind[i], hw))
            {
                return Bad(c, i, 32);
            }

            if (v.Route[i] < -1 || v.Route[i] >= w.Routes.Pool.Capacity)
            {
                return Bad(c, i, 330);
            }

            if (v.RouteCursor[i] < -1 || v.RouteCursor[i] > (1 << 24))   // -1: старт до первого шага окна (RouteNav.NeedsRefill, cur < 0)
            {
                return Bad(c, i, 331);
            }

            if (v.DestZone[i] < -1 || v.DestZone[i] >= n.ZoneCount)
            {
                return Bad(c, i, 332);
            }

            if (v.DestLane[i] < -1 || v.DestLane[i] >= lanes)
            {
                return Bad(c, i, 333);
            }

            if (v.NextConn[i] < -1 || v.NextConn[i] >= lanes)
            {
                return Bad(c, i, 334);
            }

            if (c.W.Gate.DenyReason[i] > (byte)GateDeny.Tie)
            {
                return Bad(c, i, 340);
            }

            if (v.GrantConnector[i] < -1 || v.GrantConnector[i] >= lanes)
            {
                return Bad(c, i, 335);
            }

            if (v.BusRoute[i] < -1 || v.BusRoute[i] >= n.BusRouteCount)
            {
                return Bad(c, i, 336);
            }

            if (v.BusNextStop[i] < 0)
            {
                return Bad(c, i, 337);
            }

            if (v.Lod.Length != 0 && (v.Lod[i] > 31 || !Fin(v.MesoAllow[i]) || !Fin(v.MesoNext[i])))
            {
                return Bad(c, i, 34);
            }
        }

        return ValidateLists(c, w, v);
    }

    private static bool LinkOk(VehicleStore v, int link, int hw) => link == -1 || ((uint)link < (uint)hw && v.Pool.IsAlive(link));

    private static LoadStatus ValidatePool(LoadContext c, Util.HandlePool p)
    {
        int hw = p.HighWater;
        int[] gen = p.GenerationArray;
        int dead = 0;
        for (int i = 0; i < hw; i++)
        {
            if (gen[i] < 0)
            {
                return Bad(c, i, 4);
            }

            if ((gen[i] & 1) == 0)
            {
                dead++;
            }
        }

        if (dead != p.FreeCount)
        {
            return Bad(c, dead, 2);
        }

        var seen = new ulong[(hw + 63) >> 6];
        for (int k = 0; k < p.FreeCount; k++)
        {
            int slot = p.FreeListArray[k];
            if ((uint)slot >= (uint)hw || (gen[slot] & 1) != 0 || (seen[slot >> 6] & (1UL << (slot & 63))) != 0)
            {
                return Bad(c, k, 3);
            }

            seen[slot >> 6] |= 1UL << (slot & 63);
        }

        return LoadStatus.Ok;
    }

    /// <summary>Списки полос: обход от хвоста, взаимные ссылки, длина, роль машины (основная/ghost), каждая машина - в ровно одном основном списке.</summary>
    private static LoadStatus ValidateLists(LoadContext c, World w, VehicleStore v)
    {
        LaneOccupancy occ = w.Occupancy;
        int lanes = w.Net.LaneCount;
        int hw = v.HighWater;
        var primary = new ulong[(hw + 63) >> 6];
        var ghost = new ulong[(hw + 63) >> 6];
        long totalMembers = 0;
        for (int lane = 0; lane < lanes; lane++)
        {
            int count = occ.LaneCount[lane];
            int head = occ.LaneHead[lane];
            int tail = occ.LaneTail[lane];
            if (count < 0 || count > hw * 2 || !LinkOk(v, head, hw) || !LinkOk(v, tail, hw) || (count == 0) != (head == -1) || (count == 0) != (tail == -1))
            {
                return Bad(c, lane, 40);
            }

            int prev = -1;
            int x = tail;
            for (int k = 0; k < count; k++)
            {
                if (x < 0 || x >= hw)
                {
                    return Bad(c, lane, 41);
                }

                bool isPrimary = v.Lane[x] == lane;
                bool isGhost = !isPrimary && v.GhostLane[x] == lane;
                if (!isPrimary && !isGhost)
                {
                    return Bad(c, lane, 42);
                }

                ulong[] mark = isPrimary ? primary : ghost;
                if ((mark[x >> 6] & (1UL << (x & 63))) != 0)
                {
                    return Bad(c, lane, 43);
                }

                mark[x >> 6] |= 1UL << (x & 63);
                int behind = isPrimary ? v.Behind[x] : v.GhostBehind[x];
                int ahead = isPrimary ? v.Ahead[x] : v.GhostAhead[x];
                if (behind != prev || (k == count - 1) != (ahead == -1))
                {
                    return Bad(c, lane, 44);
                }

                prev = x;
                x = ahead;
            }

            if (count > 0 && prev != head)
            {
                return Bad(c, lane, 45);
            }

            totalMembers += count;
        }

        // Каждая живая машина с полосой - в основном списке; с ghost - в списке ghost.
        long expected = 0;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            if (v.Lane[i] >= 0)
            {
                expected++;
                if ((primary[i >> 6] & (1UL << (i & 63))) == 0)
                {
                    return Bad(c, i, 46);
                }
            }
            else if (v.Ahead[i] != -1 || v.Behind[i] != -1)
            {
                return Bad(c, i, 47);
            }

            if (v.GhostLane[i] >= 0)
            {
                expected++;
                if ((ghost[i >> 6] & (1UL << (i & 63))) == 0)
                {
                    return Bad(c, i, 48);
                }
            }
        }

        return expected == totalMembers ? LoadStatus.Ok : Bad(c, 0, 49);
    }

    // ---------------------------------------------------------------- очереди, маршруты

    public static LoadStatus ValidateTrips(LoadContext c)
    {
        World w = c.W;
        NetworkStore n = w.Net;
        for (int i = 0; i < w.Trips.Count; i++)
        {
            ref readonly PendingTrip t = ref w.Trips.At(i);
            if (t.FromZone < -1 || t.FromZone >= n.ZoneCount || t.ToZone < -1 || t.ToZone >= n.ZoneCount || t.FromLane < -1 || t.FromLane >= n.LaneCount ||
                t.Type >= w.Types.Length || t.DestLane < -1 || t.DestLane >= n.LaneCount || !Fin(t.FromS) || !Fin(t.DestS) || t.Route < -1 ||
                t.Route >= w.Routes.Pool.Capacity || t.RouteGen < 0)
            {
                return Bad(c, i, 50);
            }
        }

        return LoadStatus.Ok;
    }

    public static LoadStatus ValidateQueues(LoadContext c)
    {
        World w = c.W;
        int cap = w.Vehicles.Capacity;
        for (int i = 0; i < w.Rerouting.Count; i++)
        {
            ref readonly RerouteEntry e = ref w.Rerouting.At(i);
            if ((uint)e.Index >= (uint)cap || e.Kind < 0 || e.Kind > 1)
            {
                return Bad(c, i, 51);
            }
        }

        for (int i = 0; i < w.RefillQueue.Count; i++)
        {
            if ((uint)w.RefillQueue.At(i).Index >= (uint)cap)
            {
                return Bad(c, i, 52);
            }
        }

        return LoadStatus.Ok;
    }

    public static LoadStatus ValidateRoutes(LoadContext c)
    {
        World w = c.W;
        RouteStore r = w.Routes;
        LoadStatus st = ValidatePool(c, r.Pool);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        int dirs = 2 * w.Net.SegmentCount;
        int levels = w.Routing.Levels;
        int lanes = w.Net.LaneCount;
        int nodes = w.Net.NodeCount;
        int ws = r.WindowSteps;
        for (int i = 0; i < r.Pool.HighWater; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            if (r.WinCount[i] > ws || r.PlanFirst[i] < 0 || r.PlanCount[i] < 0 || r.PlanCap[i] < r.PlanCount[i] ||
                (long)r.PlanFirst[i] + r.PlanCap[i] > r.PlanUsed || !Fin(r.PlanCost[i]))
            {
                return Bad(c, i, 60);
            }

            for (int k = 0; k < r.WinCount[i]; k++)
            {
                RouteStep s = r.Window[(i * ws) + k];
                if ((uint)s.Lane >= (uint)lanes || s.Node < -1 || s.Node >= nodes)
                {
                    return Bad(c, i, 61);
                }
            }

            for (int k = 0; k < r.PlanCount[i]; k++)
            {
                int e = r.PlanArena[r.PlanFirst[i] + k];
                int seg = e & OverlaySearcher.SegMask;
                int lvl = e >> OverlaySearcher.LevelShift;
                if (e < 0 || seg >= dirs || lvl > levels)
                {
                    return Bad(c, i, 62);
                }
            }
        }

        return LoadStatus.Ok;
    }

    // ---------------------------------------------------------------- LOD, Q-meso, остальное

    public static LoadStatus ValidateLod(LoadContext c)
    {
        World w = c.W;
        LodCells lc = w.Lod!;
        for (int i = 0; i < lc.Cells; i++)
        {
            if (lc.CellLevel[i] > LodCells.LevelMeso || lc.CellHoldUntil[i] < 0)
            {
                return Bad(c, i, 70);
            }
        }

        for (int s = 0; s < LodCells.SlotCount; s++)
        {
            LodFocusSlot f = lc.Focus[s];
            if (f.Kind > (byte)LodFocusKind.Pin)
            {
                return Bad(c, s, 71);
            }

            if (f.Kind != 0 && (f.Cx0 < 0 || f.Cx0 > f.Cx1 || f.Cx1 >= lc.CellsX || f.Cz0 < 0 || f.Cz0 > f.Cz1 || f.Cz1 >= lc.CellsZ ||
                (uint)f.CenterCx >= (uint)lc.CellsX || (uint)f.CenterCz >= (uint)lc.CellsZ))
            {
                return Bad(c, s, 72);
            }
        }

        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i) && (v.Lod[i] & ~(LodBits.Meso | LodBits.Fresh | LodBits.Queue | LodBits.Head | LodBits.Parked)) != 0)
            {
                return Bad(c, i, 73);
            }

            if (v.Pool.IsAlive(i) && w.LodCfg.MesoModel != MesoModel.Queue && (v.Lod[i] & (LodBits.Queue | LodBits.Head | LodBits.Parked)) != 0)
            {
                return Bad(c, i, 74);
            }
        }

        return LoadStatus.Ok;
    }

    public static LoadStatus ValidateQueueMeso(LoadContext c)
    {
        World w = c.W;
        VehicleStore v = w.Vehicles;
        QueueMeso q = w.Queue!;
        long tick = w.Tick;
        for (int i = 0; i < v.HighWater; i++)
        {
            int t = q.WheelTickArray[i];
            if (t == -1)
            {
                continue;
            }

            if (!v.Pool.IsAlive(i) || (v.Lod[i] & LodBits.Queue) == 0 || t < tick || t > tick + QueueMeso.WheelSize - 1)
            {
                return Bad(c, i, 80);
            }
        }

        return LoadStatus.Ok;
    }

    /// <summary>Конец загрузки: проверки, не привязанные к одному блоку.</summary>
    public static LoadStatus ValidateRest(LoadContext c)
    {
        World w = c.W;
        if (w.TargetVehicles < 0 || w.GridlockStreak < 0 || w.Tick < 0)
        {
            return Bad(c, 0, 90);
        }

        for (int g = 0; g < w.Connectors.Count; g++)
        {
            if (!Fin(w.Gate.ConnDelay[g]) || w.Gate.ConnDelay[g] < 0f || w.Gate.ConnWaitSum[g] < 0L || w.Gate.ConnGrantCount[g] < 0)
            {
                return Bad(c, g, 92);
            }
        }

        // Маршрут принадлежит одной машине: живой слот с совпавшим поколением, без совладельцев (общий маршрут при шаге одной машины
        // сдвигает окно другой, NextConn расходится с полным расчётом).
        VehicleStore v = w.Vehicles;
        var owned = new ulong[(w.Routes.Pool.Capacity + 63) >> 6];
        for (int i = 0; i < v.HighWater; i++)
        {
            int r = v.Route[i];
            if (r < 0 || !v.Pool.IsAlive(i))
            {
                continue;
            }

            if (!w.Routes.Pool.IsAlive(r, v.RouteGen[i]) || (owned[r >> 6] & (1UL << (r & 63))) != 0)
            {
                return Bad(c, i, 93);
            }

            owned[r >> 6] |= 1UL << (r & 63);
        }

        if ((w.Overlay != null) != (w.Routing.Levels > 0) || (w.Lod != null) != (w.LodCfg.Mode != LodMode.Off) ||
            (w.Queue != null) != (w.Lod != null && w.LodCfg.MesoModel == MesoModel.Queue))
        {
            return Bad(c, 0, 91);
        }

        return LoadStatus.Ok;
    }
}
