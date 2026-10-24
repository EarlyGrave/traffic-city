using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Хранилище сети (core-api §6.1, §6.2). Все массивы выделяются один раз по <see cref="SimCapacities"/>;
/// после построения аллокаций нет. Данные — AoS-структуры <c>*Data</c> и плоские массивы геометрии.
/// </summary>
internal sealed class NetworkStore
{
    public const int MaxArms = 16;

    public readonly NodeData[] Nodes;
    public readonly SegmentData[] Segments;
    public readonly LaneData[] Lanes;
    public readonly int[] NodeArms;          // индексы сегментов, инцидентных узлу, по часовой стрелке
    public readonly byte[] ArmFlags;         // параллелен NodeArms: бит0 ArmMinor (intersections.md §2.3)
    public readonly Vec2[] ArmDir;           // параллелен NodeArms: единичный вектор от узла вдоль рукава
    public readonly int[] LaneNext;          // индексы connector'ов (диапазоны из LaneData.FirstNext)
    public readonly Vec2[] SegmentPoints;    // осевые полилинии сегментов (между отступами от узлов)
    public readonly Vec2[] LanePoints;
    public readonly float[] LanePointS;
    public readonly ZoneData[] Zones;
    public readonly int[] ZoneLanes;         // полосы появления зон подряд; диапазон зоны - ZoneData.FirstSpawnLane/SpawnLaneCount
    public readonly BusStopData[] BusStops;
    public readonly BusRouteData[] BusRoutes;
    public readonly int[] BusRouteStops;

    // Служебные индексы построения: список инцидентности (по концам сегментов) и сетка прилипания.
    internal readonly int[] IncHead;         // [MaxNodes] -> код конца (2*seg + end) или -1
    internal readonly int[] IncNext;         // [2*MaxSegments]
    internal readonly int[] CellNext;        // [MaxNodes] цепочка узлов в ячейке сетки
    internal readonly FixedHashMap Cells;    // ключ ячейки -> первый узел

    public NetworkStore(in SimCapacities caps)
    {
        int nodes = Math.Max(0, caps.MaxNodes);
        int segs = Math.Max(0, caps.MaxSegments);
        int lanes = Math.Max(0, caps.MaxLanes);
        int pts = Math.Max(0, caps.MaxLanePoints);
        Nodes = new NodeData[nodes];
        Segments = new SegmentData[segs];
        Lanes = new LaneData[lanes];
        NodeArms = new int[2 * segs];
        ArmFlags = new byte[2 * segs];
        ArmDir = new Vec2[2 * segs];
        LaneNext = new int[2 * lanes];
        SegmentPoints = new Vec2[4 * segs];
        LanePoints = new Vec2[pts];
        LanePointS = new float[pts];
        Zones = new ZoneData[Math.Max(0, caps.MaxZones)];
        ZoneLanes = new int[lanes];
        BusStops = new BusStopData[Math.Max(0, caps.MaxBusStops)];
        BusRoutes = new BusRouteData[Math.Max(0, caps.MaxBusRoutes)];
        BusRouteStops = new int[Math.Max(0, caps.MaxBusRoutes) * Math.Max(0, caps.MaxBusStopsPerRoute)];
        IncHead = new int[nodes];
        IncNext = new int[2 * segs];
        CellNext = new int[nodes];
        Cells = new FixedHashMap(Math.Max(1, nodes));
    }

    public int NodeCount { get; internal set; }

    public int SegmentCount { get; internal set; }

    public int LaneCount { get; internal set; }

    public int LanePointCount { get; internal set; }

    public int SegmentPointCount { get; internal set; }

    public int NodeArmCount { get; internal set; }

    public int LaneNextCount { get; internal set; }

    public int ZoneCount { get; internal set; }

    public int ZoneLaneCount { get; internal set; }

    public int BusStopCount { get; internal set; }

    public int BusRouteCount { get; internal set; }

    public int BusRouteStopCount { get; internal set; }

    /// <summary>++ при каждом изменении сети (клиент перестраивает меши по смене).</summary>
    public int Version { get; internal set; }

    public NetworkView GetView() => new(
        Version,
        new ReadOnlySpan<NodeData>(Nodes, 0, NodeCount),
        new ReadOnlySpan<SegmentData>(Segments, 0, SegmentCount),
        new ReadOnlySpan<LaneData>(Lanes, 0, LaneCount),
        new ReadOnlySpan<Vec2>(LanePoints, 0, LanePointCount),
        new ReadOnlySpan<float>(LanePointS, 0, LanePointCount),
        new ReadOnlySpan<ZoneData>(Zones, 0, ZoneCount),
        new ReadOnlySpan<BusStopData>(BusStops, 0, BusStopCount),
        new ReadOnlySpan<BusRouteData>(BusRoutes, 0, BusRouteCount),
        new ReadOnlySpan<int>(BusRouteStops, 0, BusRouteStopCount));

    /// <summary>FNV-1a 64 по занятой части всех массивов сети (детерминизм построения).</summary>
    public ulong Hash()
    {
        var h = Fnv1a64.Create();
        h.Add(NodeCount);
        h.Add(SegmentCount);
        h.Add(LaneCount);
        h.Add(LanePointCount);
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<NodeData>(Nodes, 0, NodeCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<SegmentData>(Segments, 0, SegmentCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<LaneData>(Lanes, 0, LaneCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(NodeArms, 0, NodeArmCount)));
        h.Add(new ReadOnlySpan<byte>(ArmFlags, 0, NodeArmCount));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(ArmDir, 0, NodeArmCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(LaneNext, 0, LaneNextCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(SegmentPoints, 0, SegmentPointCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(LanePoints, 0, LanePointCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(LanePointS, 0, LanePointCount)));
        return h.Value;
    }
}
