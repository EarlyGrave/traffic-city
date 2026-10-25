using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Save;

// Блоки сети: статика (N*, CONN), динамика (*DYN), светофоры (SIGS).
internal static partial class SaveSchema
{
    /// <summary>Счётчик структуры: значение из файла обязано влезать в ёмкость.</summary>
    private static ColumnDef Cnt(uint id, string name, Func<World, int> get, Action<World, int> set, Func<World, int> max) =>
        Col.Scalar<int>(id, name, get, (c, v) =>
        {
            if (v < 0 || v > max(c.W))
            {
                return LoadStatus.CapacityExceeded;
            }

            set(c.W, v);
            return LoadStatus.Ok;
        });

    private static BlockDef NetworkCounts() => new("NETW", "счётчики занятых частей NetworkStore", new[]
    {
        Cnt(1, "NodeCount", w => w.Net.NodeCount, (w, v) => w.Net.NodeCount = v, w => w.Net.Nodes.Length),
        Cnt(2, "SegmentCount", w => w.Net.SegmentCount, (w, v) => w.Net.SegmentCount = v, w => w.Net.Segments.Length),
        Cnt(3, "LaneCount", w => w.Net.LaneCount, (w, v) => w.Net.LaneCount = v, w => w.Net.Lanes.Length),
        Cnt(4, "LanePointCount", w => w.Net.LanePointCount, (w, v) => w.Net.LanePointCount = v, w => w.Net.LanePoints.Length),
        Cnt(5, "SegmentPointCount", w => w.Net.SegmentPointCount, (w, v) => w.Net.SegmentPointCount = v, w => w.Net.SegmentPoints.Length),
        Cnt(6, "NodeArmCount", w => w.Net.NodeArmCount, (w, v) => w.Net.NodeArmCount = v, w => w.Net.NodeArms.Length),
        Cnt(7, "LaneNextCount", w => w.Net.LaneNextCount, (w, v) => w.Net.LaneNextCount = v, w => w.Net.LaneNext.Length),
        Cnt(8, "ZoneCount", w => w.Net.ZoneCount, (w, v) => w.Net.ZoneCount = v, w => w.Net.Zones.Length),
        Cnt(9, "ZoneLaneCount", w => w.Net.ZoneLaneCount, (w, v) => w.Net.ZoneLaneCount = v, w => w.Net.ZoneLanes.Length),
        Cnt(10, "BusStopCount", w => w.Net.BusStopCount, (w, v) => w.Net.BusStopCount = v, w => w.Net.BusStops.Length),
        Cnt(11, "BusRouteCount", w => w.Net.BusRouteCount, (w, v) => w.Net.BusRouteCount = v, w => w.Net.BusRoutes.Length),
        Cnt(12, "BusRouteStopCount", w => w.Net.BusRouteStopCount, (w, v) => w.Net.BusRouteStopCount = v, w => w.Net.BusRouteStops.Length),
    });

    private static BlockDef Nodes() => new("NNOD", "узлы", new[]
    {
        Col.Field<NodeData>(1, "PositionX", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Position), 0, ColumnType.F32),
        Col.Field<NodeData>(2, "PositionZ", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Position), 4, ColumnType.F32),
        Col.Field<NodeData>(3, "Elevation", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Elevation)),
        Col.Field<NodeData>(4, "Radius", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Radius)),
        Col.Field<NodeData>(5, "Control", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Control)),
        Col.Field<NodeData>(6, "FirstArm", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.FirstArm), def: "-1"),
        Col.Field<NodeData>(7, "ArmCount", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.ArmCount)),
        Col.Field<NodeData>(8, "FirstConnector", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.FirstConnector)),
        Col.Field<NodeData>(9, "ConnectorCount", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.ConnectorCount)),
        Col.Field<NodeData>(10, "Signal", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Signal), def: "-1"),
        Col.Field<NodeData>(11, "Zone", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Zone), def: "-1"),
        Col.Field<NodeData>(12, "Generation", w => w.Net.Nodes, _nodeN, "NodeCount", nameof(NodeData.Generation)),
    });

    private static BlockDef Segments() => new("NSEG", "сегменты", new[]
    {
        Col.Field<SegmentData>(1, "NodeA", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.NodeA)),
        Col.Field<SegmentData>(2, "NodeB", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.NodeB)),
        Col.Field<SegmentData>(3, "Length", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.Length)),
        Col.Field<SegmentData>(4, "SpeedLimit", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.SpeedLimit)),
        Col.Field<SegmentData>(5, "Kind", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.Kind)),
        Col.Field<SegmentData>(6, "LanesAB", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.LanesAB)),
        Col.Field<SegmentData>(7, "LanesBA", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.LanesBA)),
        Col.Field<SegmentData>(8, "FirstLaneAB", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.FirstLaneAB), def: "-1"),
        Col.Field<SegmentData>(9, "FirstLaneBA", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.FirstLaneBA), def: "-1"),
        Col.Field<SegmentData>(10, "FirstPoint", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.FirstPoint)),
        Col.Field<SegmentData>(11, "PointCount", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.PointCount)),
        Col.Field<SegmentData>(12, "Generation", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.Generation)),
    });

    private static BlockDef Lanes() => new("NLAN", "полосы", new[]
    {
        Col.Field<LaneData>(1, "Kind", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Kind)),
        Col.Field<LaneData>(2, "LaneNo", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.LaneNo)),
        Col.Field<LaneData>(3, "Direction", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Direction)),
        Col.Field<LaneData>(4, "Flags", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Flags)),
        Col.Field<LaneData>(5, "Segment", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Segment), def: "-1"),
        Col.Field<LaneData>(6, "Node", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Node), def: "-1"),
        Col.Field<LaneData>(7, "FromLane", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.FromLane), def: "-1"),
        Col.Field<LaneData>(8, "ToLane", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.ToLane), def: "-1"),
        Col.Field<LaneData>(9, "Length", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Length)),
        Col.Field<LaneData>(10, "SpeedLimit", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.SpeedLimit)),
        Col.Field<LaneData>(11, "FirstPoint", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.FirstPoint)),
        Col.Field<LaneData>(12, "PointCount", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.PointCount)),
        Col.Field<LaneData>(13, "Left", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Left), def: "-1"),
        Col.Field<LaneData>(14, "Right", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Right), def: "-1"),
        Col.Field<LaneData>(15, "FirstNext", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.FirstNext)),
        Col.Field<LaneData>(16, "NextCount", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.NextCount)),
        Col.Field<LaneData>(17, "ConflictGroup", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.ConflictGroup), def: "-1"),
        Col.Field<LaneData>(18, "Generation", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Generation)),
    });

    private static BlockDef Arms() => new("NARM", "рукава узлов", new[]
    {
        Col.Array<int>(1, "NodeArms", w => w.Net.NodeArms, _armN, "NodeArmCount"),
        Col.Array<byte>(2, "ArmFlags", w => w.Net.ArmFlags, _armN, "NodeArmCount"),
        Col.Field<Vec2>(3, "ArmDirX", w => w.Net.ArmDir, _armN, "NodeArmCount", nameof(Vec2.X)),
        Col.Field<Vec2>(4, "ArmDirZ", w => w.Net.ArmDir, _armN, "NodeArmCount", nameof(Vec2.Z)),
    });

    private static BlockDef LaneNext() => new("NLNX", "исходящие connector'ы полос", new[]
    {
        Col.Array<int>(1, "LaneNext", w => w.Net.LaneNext, _laneNextN, "LaneNextCount"),
    });

    private static BlockDef Points() => new("NPTS", "осевые точки сегментов и точки полос", new[]
    {
        Col.Field<Vec2>(1, "SegmentPointsX", w => w.Net.SegmentPoints, _segPointN, "SegmentPointCount", nameof(Vec2.X)),
        Col.Field<Vec2>(2, "SegmentPointsZ", w => w.Net.SegmentPoints, _segPointN, "SegmentPointCount", nameof(Vec2.Z)),
        Col.Field<Vec2>(3, "LanePointsX", w => w.Net.LanePoints, _lanePointN, "LanePointCount", nameof(Vec2.X)),
        Col.Field<Vec2>(4, "LanePointsZ", w => w.Net.LanePoints, _lanePointN, "LanePointCount", nameof(Vec2.Z)),
        Col.Array<float>(5, "LanePointS", w => w.Net.LanePointS, _lanePointN, "LanePointCount"),
    });

    private static BlockDef Zones() => new("NZON", "зоны и их полосы появления", new[]
    {
        Col.Field<ZoneData>(1, "CenterX", w => w.Net.Zones, _zoneN, "ZoneCount", nameof(ZoneData.Center), 0, ColumnType.F32),
        Col.Field<ZoneData>(2, "CenterZ", w => w.Net.Zones, _zoneN, "ZoneCount", nameof(ZoneData.Center), 4, ColumnType.F32),
        Col.Field<ZoneData>(3, "HalfSize", w => w.Net.Zones, _zoneN, "ZoneCount", nameof(ZoneData.HalfSize)),
        Col.Field<ZoneData>(4, "Weight", w => w.Net.Zones, _zoneN, "ZoneCount", nameof(ZoneData.Weight)),
        Col.Field<ZoneData>(5, "FirstSpawnLane", w => w.Net.Zones, _zoneN, "ZoneCount", nameof(ZoneData.FirstSpawnLane)),
        Col.Field<ZoneData>(6, "SpawnLaneCount", w => w.Net.Zones, _zoneN, "ZoneCount", nameof(ZoneData.SpawnLaneCount)),
        Col.Field<ZoneData>(7, "IsExternal", w => w.Net.Zones, _zoneN, "ZoneCount", nameof(ZoneData.IsExternal)),
        Col.Array<int>(8, "ZoneLanes", w => w.Net.ZoneLanes, _zoneLaneN, "ZoneLaneCount"),
    });

    private static BlockDef BusStatic() => new("NBUS", "автобусные остановки и маршруты (статика)", new[]
    {
        Col.Field<BusStopData>(1, "StopLane", w => w.Net.BusStops, _stopN, "BusStopCount", nameof(BusStopData.Lane)),
        Col.Field<BusStopData>(2, "StopS", w => w.Net.BusStops, _stopN, "BusStopCount", nameof(BusStopData.S)),
        Col.Field<BusStopData>(3, "StopKind", w => w.Net.BusStops, _stopN, "BusStopCount", nameof(BusStopData.Kind)),
        Col.Field<BusStopData>(4, "StopRoute", w => w.Net.BusStops, _stopN, "BusStopCount", nameof(BusStopData.Route), def: "-1"),
        Col.Field<BusRouteData>(5, "RouteFirstStop", w => w.Net.BusRoutes, _busRouteN, "BusRouteCount", nameof(BusRouteData.FirstStop)),
        Col.Field<BusRouteData>(6, "RouteStopCount", w => w.Net.BusRoutes, _busRouteN, "BusRouteCount", nameof(BusRouteData.StopCount)),
        Col.Field<BusRouteData>(7, "RouteHeadwayTicks", w => w.Net.BusRoutes, _busRouteN, "BusRouteCount", nameof(BusRouteData.HeadwayTicks)),
        Col.Field<BusRouteData>(8, "RouteVehicleType", w => w.Net.BusRoutes, _busRouteN, "BusRouteCount", nameof(BusRouteData.VehicleType)),
        Col.Field<BusRouteData>(9, "RouteGeneration", w => w.Net.BusRoutes, _busRouteN, "BusRouteCount", nameof(BusRouteData.Generation)),
        Col.Array<int>(10, "BusRouteStops", w => w.Net.BusRouteStops, _busRouteStopN, "BusRouteStopCount"),
    });

    private static BlockDef Connectors() => new("CONN", "connector'ы и конфликтные пары (раскладка - по узлам в порядке индекса)", new[]
    {
        Col.Scalar<int>(1, "Count", w => w.Connectors.Count, (c, v) =>
        {
            if (v < 0 || v > c.W.Connectors.MaxConnectors)
            {
                return LoadStatus.CapacityExceeded;
            }

            c.W.Connectors.Count = v;
            return LoadStatus.Ok;
        }),
        Col.Scalar<int>(2, "ConflictsUsed", w => w.Connectors.ConflictsUsed, (c, v) =>
        {
            if (v < 0 || v > c.W.Connectors.MaxConflicts)
            {
                return LoadStatus.CapacityExceeded;
            }

            c.W.Connectors.ConflictsUsed = v;
            return LoadStatus.Ok;
        }),
        Col.Array<int>(3, "Lane", w => w.Connectors.Lane, _connN, "Count"),
        Col.Array<int>(4, "Node", w => w.Connectors.Node, _connN, "Count"),
        Col.Array<int>(5, "InLane", w => w.Connectors.InLane, _connN, "Count"),
        Col.Array<int>(6, "OutLane", w => w.Connectors.OutLane, _connN, "Count"),
        Col.Array<byte>(7, "LocalBit", w => w.Connectors.LocalBit, _connN, "Count"),
        Col.Array<byte>(8, "Maneuver", w => w.Connectors.Maneuver, _connN, "Count"),
        Col.Array<byte>(9, "FromArm", w => w.Connectors.FromArm, _connN, "Count"),
        Col.Array<byte>(10, "Flags", w => w.Connectors.Flags, _connN, "Count"),
        Col.Array<int>(11, "FirstConflict", w => w.Connectors.FirstConflict, _connN, "Count"),
        Col.Array<int>(12, "ConflictCount", w => w.Connectors.ConflictCount, _connN, "Count"),
        Col.Array<ulong>(13, "ConflictMask", w => w.Connectors.ConflictMask, _connN, "Count"),
        Col.Field<ConflictEntry>(14, "OtherLocal", w => w.Connectors.Conflicts, _conflictN, "ConflictsUsed", nameof(ConflictEntry.OtherLocal)),
        Col.Field<ConflictEntry>(15, "ConflictFlags", w => w.Connectors.Conflicts, _conflictN, "ConflictsUsed", nameof(ConflictEntry.Flags)),
        Col.Field<ConflictEntry>(16, "SMineCm", w => w.Connectors.Conflicts, _conflictN, "ConflictsUsed", nameof(ConflictEntry.SMineCm)),
        Col.Field<ConflictEntry>(17, "SOtherCm", w => w.Connectors.Conflicts, _conflictN, "ConflictsUsed", nameof(ConflictEntry.SOtherCm)),
    });

    private static BlockDef Signals() => new("SIGS", "светофоры: планы, фазы, выходы фазы 4", new[]
    {
        Col.Scalar<int>(1, "Count", w => w.Signals.Count, (c, v) =>
        {
            if (v < 0 || v > c.W.Signals.Capacity)
            {
                return LoadStatus.CapacityExceeded;
            }

            c.W.Signals.Count = v;
            return LoadStatus.Ok;
        }),
        Col.Scalar<int>(2, "SignalCount", w => w.SignalCount, (c, v) =>
        {
            if (v < 0 || v > c.W.SignalStates.Length)
            {
                return LoadStatus.CapacityExceeded;
            }

            c.W.SignalCount = v;
            return LoadStatus.Ok;
        }),
        Col.Array<int>(3, "Node", w => w.Signals.Node, _signalN, "Count", def: "-1"),
        Col.Array<SignalMode>(4, "Mode", w => w.Signals.Mode, _signalN, "Count"),
        Col.Array<byte>(5, "PhaseCount", w => w.Signals.PhaseCount, _signalN, "Count"),
        Col.Array<ushort>(6, "YellowTicks", w => w.Signals.YellowTicks, _signalN, "Count"),
        Col.Array<ushort>(7, "AllRedTicks", w => w.Signals.AllRedTicks, _signalN, "Count"),
        Col.Array<ushort>(8, "OffsetTicks", w => w.Signals.OffsetTicks, _signalN, "Count"),
        Col.Array<byte>(9, "Phase", w => w.Signals.Phase, _signalN, "Count"),
        Col.Array<SignalInterval>(10, "Interval", w => w.Signals.Interval, _signalN, "Count"),
        Col.Array<ushort>(11, "TicksLeft", w => w.Signals.TicksLeft, _signalN, "Count"),
        Col.Array<ushort>(12, "GreenTicks", w => w.Signals.GreenTicks, w => w.Signals.Count * SignalStore.MaxPhases, "Count * 8"),
        Col.Array<ulong>(13, "PhaseMask", w => w.Signals.PhaseMask, w => w.Signals.Count * SignalStore.MaxPhases, "Count * 8"),
        Col.Field<SignalState>(14, "StateNode", w => w.SignalStates, _signalStateN, "SignalCount", nameof(SignalState.Node)),
        Col.Field<SignalState>(15, "StatePhase", w => w.SignalStates, _signalStateN, "SignalCount", nameof(SignalState.Phase)),
        Col.Field<SignalState>(16, "StateInterval", w => w.SignalStates, _signalStateN, "SignalCount", nameof(SignalState.Interval)),
        Col.Field<SignalState>(17, "StateTicksToChange", w => w.SignalStates, _signalStateN, "SignalCount", nameof(SignalState.TicksToChange)),
        // 2.5 S2 (ADR-0018): параметры плана Adaptive/стрелки и состояние автомата с подфазами. Статика слота (рукава, оси, входные полосы) - производное (RebuildStatics).
        Col.Array<ushort>(18, "MinGreenTicks", w => w.Signals.MinGreenTicks, _signalN, "Count"),
        Col.Array<ushort>(19, "GapTicks", w => w.Signals.GapTicks, _signalN, "Count"),
        Col.Array<ushort>(20, "ArrowTriggerTicks", w => w.Signals.ArrowTriggerTicks, _signalN, "Count"),
        Col.Array<ushort>(21, "ArrowMinTicks", w => w.Signals.ArrowMinTicks, _signalN, "Count"),
        Col.Array<ushort>(22, "ArrowMaxTicks", w => w.Signals.ArrowMaxTicks, _signalN, "Count"),
        Col.Array<ushort>(23, "ArrowGapTicks", w => w.Signals.ArrowGapTicks, _signalN, "Count"),
        Col.Array<ushort>(24, "CycleTicks", w => w.Signals.CycleTicks, _signalN, "Count"),
        Col.Array<byte>(25, "PlanFlags", w => w.Signals.Flags, _signalN, "Count"),
        Col.Array<byte>(26, "Machine", w => w.Signals.Machine, _signalN, "Count"),
        Col.Array<byte>(27, "Sub", w => w.Signals.Sub, _signalN, "Count"),
        Col.Array<ulong>(28, "GreenMask", w => w.Signals.GreenMask, _signalN, "Count"),
        Col.Array<ulong>(29, "NextMask", w => w.Signals.NextMask, _signalN, "Count"),
        Col.Array<byte>(30, "NextAxis", w => w.Signals.NextAxis, _signalN, "Count"),
        Col.Array<byte>(31, "NextSub", w => w.Signals.NextSub, _signalN, "Count"),
        Col.Array<ushort>(32, "NextArrowArms", w => w.Signals.NextArrowArms, _signalN, "Count"),
        Col.Array<ushort>(33, "ArrowArms", w => w.Signals.ArrowArms, _signalN, "Count"),
        Col.Array<ushort>(34, "GreenElapsed", w => w.Signals.GreenElapsed, _signalN, "Count"),
        Col.Array<ushort>(35, "GapLeft", w => w.Signals.GapLeft, _signalN, "Count"),
        Col.Array<ushort>(36, "ArmLeftYield", w => w.Signals.ArmLeftYield, w => w.Signals.Count * SignalStore.ArmCap, "Count * 16"),
        Col.Array<int>(37, "ArmLeftHead", w => w.Signals.ArmLeftHead, w => w.Signals.Count * SignalStore.ArmCap, "Count * 16", "-1"),
        Col.Array<ushort>(38, "SpillTicks", w => w.Signals.SpillTicks, _signalN, "Count"),
    });

    private static BlockDef NodeDynamic() => new("NDYN", "узлы: билеты ворот и пропускная способность", new[]
    {
        Col.Array<uint>(1, "NodeTicket", w => w.Net.NodeTicket, _nodeN, "NodeCount"),
        Col.Array<uint>(2, "NodeThroughput", w => w.NodeThroughput, _nodeN, "NodeCount"),
    });

    private static BlockDef LaneDynamic() => new("LDYN", "полосы: порядок машин, резерв выезда, сигналы фазы 4", new[]
    {
        Col.Array<int>(1, "LaneHead", w => w.Occupancy.LaneHead, _laneN, "LaneCount", def: "-1"),
        Col.Array<int>(2, "LaneTail", w => w.Occupancy.LaneTail, _laneN, "LaneCount", def: "-1"),
        Col.Array<int>(3, "LaneCount", w => w.Occupancy.LaneCount, _laneN, "LaneCount"),
        Col.Array<float>(4, "OutReserved", w => w.Net.OutReserved, _laneN, "LaneCount"),
        Col.Array<byte>(5, "LaneSignals", w => w.LaneSignals, _laneN, "LaneCount"),
    });

    private static BlockDef SegmentDynamic() => new("SDYN", "сегменты: нагрузка (читает фаза 3 следующего тика)", new[]
    {
        Col.Field<SegmentLoad>(1, "Density", w => w.SegmentLoads, _segN, "SegmentCount", nameof(SegmentLoad.Density)),
        Col.Field<SegmentLoad>(2, "SpeedRatio", w => w.SegmentLoads, _segN, "SegmentCount", nameof(SegmentLoad.SpeedRatio), def: "1"),
        Col.Field<SegmentLoad>(3, "LoadCount", w => w.SegmentLoads, _segN, "SegmentCount", nameof(SegmentLoad.Count)),
    });

    private static BlockDef BusDynamic() => new("BDYN", "автобусы: ожидающие, прибытия, выпуск, статистика маршрутов", new[]
    {
        Col.Field<BusStopData>(1, "Waiting", w => w.Net.BusStops, _stopN, "BusStopCount", nameof(BusStopData.Waiting)),
        Col.Field<BusStopData>(2, "LastArrivalTick", w => w.Net.BusStops, _stopN, "BusStopCount", nameof(BusStopData.LastArrivalTick), def: "-1"),
        Col.Field<BusRouteData>(3, "ActiveBuses", w => w.Net.BusRoutes, _busRouteN, "BusRouteCount", nameof(BusRouteData.ActiveBuses)),
        Col.Field<BusRouteData>(4, "LastDispatchTick", w => w.Net.BusRoutes, _busRouteN, "BusRouteCount", nameof(BusRouteData.LastDispatchTick), def: "-1"),
        Col.Field<BusRouteStats>(5, "StatsActiveBuses", w => w.BusRouteStats, _busRouteN, "BusRouteCount", nameof(BusRouteStats.ActiveBuses)),
        Col.Field<BusRouteStats>(6, "StatsArrivals", w => w.BusRouteStats, _busRouteN, "BusRouteCount", nameof(BusRouteStats.Arrivals)),
        Col.Field<BusRouteStats>(7, "StatsHeadwayDeviationTicksTotal", w => w.BusRouteStats, _busRouteN, "BusRouteCount", nameof(BusRouteStats.HeadwayDeviationTicksTotal)),
        Col.Field<BusRouteStats>(8, "StatsLastDispatchTick", w => w.BusRouteStats, _busRouteN, "BusRouteCount", nameof(BusRouteStats.LastDispatchTick), def: "-1"),
        Col.Field<BusStopData>(9, "LastDepartTick", w => w.Net.BusStops, _stopN, "BusStopCount", nameof(BusStopData.LastDepartTick), def: "-1"),
    });
}
