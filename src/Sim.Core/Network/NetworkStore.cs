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
    public readonly float[] LanePieceHeading; // курс отрезка k->k+1 полилинии (atan2); производные данные, в хэш не входят (ADR-0010)
    public readonly ZoneData[] Zones;
    public readonly int[] ZoneLanes;         // полосы появления зон подряд; диапазон зоны - ZoneData.FirstSpawnLane/SpawnLaneCount
    public readonly BusStopData[] BusStops;
    public readonly BusRouteData[] BusRoutes;
    public readonly int[] BusRouteStops;
    public readonly uint[] NodeTicket;       // [MaxNodes] следующий билет узла; ++ при каждом допуске (intersections.md §4)
    public readonly int[] LaneGateNode;      // [MaxLanes] (ADR-0016, D5) узел с воротами в конце Normal-полосы / узел gated connector'а, иначе -1; производное, вне хэша
    public readonly float[] LaneLength;      // [MaxLanes] (ADR-0016, D5) горячая копия LaneData.Length; производное, вне хэша
    public readonly float[] OutReserved;     // [MaxLanes] Σ длин машин, допущенных/находящихся в узле с выездом на полосу; пересобирается фазой 4

    // Служебные индексы построения: список инцидентности (по концам сегментов) и сетка прилипания.
    internal readonly int[] IncHead;         // [MaxNodes] -> код конца (2*seg + end) или -1
    internal readonly int[] IncNext;         // [2*MaxSegments]
    internal readonly int[] CellNext;        // [MaxNodes] цепочка узлов в ячейке сетки
    internal readonly FixedHashMap Cells;    // ключ ячейки -> первый узел

    /// <summary>Курс отрезка a->b: тем же выражением, что <see cref="LaneGeometry"/> (нормировка + atan2).</summary>
    internal static float PieceHeading(Vec2 a, Vec2 b)
    {
        Vec2 d = b - a;
        float len = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        Vec2 dir = len > 1e-9f ? d * (1f / len) : new Vec2(1f, 0f);
        return MathF.Atan2(dir.Z, dir.X);
    }

    /// <summary>Заполняет кэш курса отрезков полосы, чьи <paramref name="m"/> точек лежат с индекса <paramref name="first"/>.</summary>
    internal void FillPieceHeadings(int first, int m)
    {
        for (int k = 0; k < m - 1; k++)
        {
            LanePieceHeading[first + k] = PieceHeading(LanePoints[first + k], LanePoints[first + k + 1]);
        }

        LanePieceHeading[first + m - 1] = 0f;
    }

    /// <summary>
    /// Пересчёт <see cref="LaneGateNode"/> для полос узла <paramref name="node"/> (ADR-0016, D5): его connector'ы и их входные полосы получают
    /// номер узла, если у узла есть ворота (<c>Control != None</c>), иначе -1. Зовётся после построения connector'ов узла и при смене режима.
    /// </summary>
    internal void RefreshLaneGateNode(int node)
    {
        ref readonly NodeData nd = ref Nodes[node];
        if (nd.FirstArm < 0)
        {
            return;
        }

        int gate = nd.Control != NodeControl.None ? node : -1;
        for (int p = 0; p < nd.ConnectorCount; p++)
        {
            int c = nd.FirstConnector + p;
            LaneGateNode[c] = gate;
            LaneGateNode[Lanes[c].FromLane] = gate;
        }
    }

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
        LanePieceHeading = new float[pts];
        Zones = new ZoneData[Math.Max(0, caps.MaxZones)];
        ZoneLanes = new int[lanes];
        BusStops = new BusStopData[Math.Max(0, caps.MaxBusStops)];
        BusRoutes = new BusRouteData[Math.Max(0, caps.MaxBusRoutes)];
        BusRouteStops = new int[Math.Max(0, caps.MaxBusRoutes) * Math.Max(0, caps.MaxBusStopsPerRoute)];
        NodeTicket = new uint[nodes];
        OutReserved = new float[lanes];
        LaneGateNode = new int[lanes];
        Array.Fill(LaneGateNode, -1);
        LaneLength = new float[lanes];
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

    /// <summary>
    /// Наибольший индекс узла с построенными рукавами/connector'ами (<c>FirstArm &gt;= 0</c>) или -1. Канонический порядок раскладки
    /// (save-format.md §13 п. 5): connector'ы и конфликты узлов лежат по возрастанию индекса узла, поэтому новый узел можно строить,
    /// только если его индекс больше этого.
    /// </summary>
    public int HighestBuiltNode()
    {
        for (int n = NodeCount - 1; n >= 0; n--)
        {
            if (Nodes[n].FirstArm >= 0)
            {
                return n;
            }
        }

        return -1;
    }

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
