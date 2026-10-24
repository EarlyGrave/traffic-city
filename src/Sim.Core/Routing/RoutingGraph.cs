using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>Константы весов маршрутизации (routing.md 1.1), секунды. Одно место для калибровки.</summary>
internal static class RoutingCosts
{
    public const float Right = 3f;
    public const float Left = 6f;
    public const float UTurn = 20f;
    public const float Yield = 6f;
    public const float Stop = 9f;
    public const float RightHandRule = 3f;

    /// <summary>План светофора по умолчанию (цикл 56 с, зелёный моей фазы 24 с) - планы в NetworkStore не хранятся.</summary>
    public const float DefaultCycle = 56f;
    public const float DefaultGreen = 24f;

    /// <summary>cos порога "прямо" (как NetworkBuilder.Connectors).</summary>
    public const float StraightCos = 0.9f;

    /// <summary>Средняя задержка равномерного прибытия на светофоре: (Cycle - Green)^2 / (2 Cycle).</summary>
    public static float SignalDelay(float cycle, float green)
    {
        float red = cycle - green;
        return red * red / (2f * cycle);
    }

    public static float OfManeuver(Maneuver m) => m switch
    {
        Maneuver.Right => Right,
        Maneuver.Left => Left,
        Maneuver.UTurn => UTurn,
        _ => 0f,
    };
}

/// <summary>
/// Граф поиска (routing.md 1): вершина = направленный сегмент <c>u = seg*2 + dir</c>, дуга = существует
/// connector между полосами этих направлений. CSR-смежность, дуги вершины упорядочены по целевой вершине.
/// Все массивы выделяются один раз по ёмкости <see cref="NetworkStore"/>; <see cref="Rebuild"/> без аллокаций.
/// </summary>
internal sealed class RouteGraph
{
    private const int ScratchArcs = 512;

    private readonly NetworkStore _net;
    private readonly int[] _scratchTo = new int[ScratchArcs];
    private readonly float[] _scratchCost = new float[ScratchArcs];
    private readonly byte[] _scratchMan = new byte[ScratchArcs];

    public readonly int[] ArcFirst;      // [DirSegCapacity + 1]
    public readonly int[] ArcTo;         // [ArcCapacity] целевой DirSeg
    public readonly float[] ArcCost;     // [ArcCapacity] время connector'а + штраф манёвра + штраф узла, с
    public readonly byte[] ArcManeuver;  // [ArcCapacity] манёвр дуги (минимальный по стоимости)
    public readonly float[] SegCost;     // [DirSegCapacity] Length / SpeedLimit, с
    public readonly int[] StartNode;     // [DirSegCapacity]
    public readonly int[] EndNode;       // [DirSegCapacity]
    public readonly Vec2[] EndPos;       // [DirSegCapacity]

    public RouteGraph(NetworkStore net)
    {
        _net = net;
        DirSegCapacity = 2 * net.Segments.Length;
        ArcCapacity = Math.Max(1, net.Lanes.Length);
        ArcFirst = new int[DirSegCapacity + 1];
        ArcTo = new int[ArcCapacity];
        ArcCost = new float[ArcCapacity];
        ArcManeuver = new byte[ArcCapacity];
        SegCost = new float[DirSegCapacity];
        StartNode = new int[DirSegCapacity];
        EndNode = new int[DirSegCapacity];
        EndPos = new Vec2[DirSegCapacity];
        Rebuild();
    }

    public int DirSegCapacity { get; }

    public int ArcCapacity { get; }

    public int DirSegCount { get; private set; }

    public int ArcCount { get; private set; }

    public float VMax { get; private set; } = 1f;

    /// <summary>Версия сети, по которой построен граф.</summary>
    public int BuiltVersion { get; private set; } = -1;

    /// <summary>true, если в сети больше дуг, чем помещается (граф усечён).</summary>
    public bool Truncated { get; private set; }

    public static int DirSegOfLane(NetworkStore net, int lane) => (net.Lanes[lane].Segment * 2) + net.Lanes[lane].Direction;

    /// <summary>Индекс дуги <c>u -> w</c> или -1 (двоичный поиск по упорядоченным дугам).</summary>
    public int FindArc(int u, int w)
    {
        int lo = ArcFirst[u];
        int hi = ArcFirst[u + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int t = ArcTo[mid];
            if (t == w)
            {
                return mid;
            }

            if (t < w)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    /// <summary>Полная перестройка из текущего <see cref="NetworkStore"/> (после Create и каждого BuildRoad).</summary>
    public void Rebuild()
    {
        NetworkStore n = _net;
        DirSegCount = 2 * n.SegmentCount;
        Truncated = false;
        float vmax = 1f;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            ref readonly SegmentData sd = ref n.Segments[s];
            vmax = MathF.Max(vmax, sd.SpeedLimit);
            for (int d = 0; d < 2; d++)
            {
                int u = (s * 2) + d;
                int a = d == 0 ? sd.NodeA : sd.NodeB;
                int b = d == 0 ? sd.NodeB : sd.NodeA;
                StartNode[u] = a;
                EndNode[u] = b;
                EndPos[u] = n.Nodes[b].Position;
                SegCost[u] = sd.Length / sd.SpeedLimit;
            }
        }

        VMax = vmax;

        int arcs = 0;
        for (int u = 0; u < DirSegCount; u++)
        {
            ArcFirst[u] = arcs;
            int cnt = CollectArcs(u);
            for (int k = 0; k < cnt; k++)
            {
                if (arcs >= ArcCapacity)
                {
                    Truncated = true;
                    break;
                }

                ArcTo[arcs] = _scratchTo[k];
                ArcCost[arcs] = _scratchCost[k];
                ArcManeuver[arcs] = _scratchMan[k];
                arcs++;
            }
        }

        ArcFirst[DirSegCount] = arcs;
        ArcCount = arcs;
        BuiltVersion = n.Version;
    }

    // Собирает дуги вершины u в scratch, упорядоченные по целевой вершине, дубликаты схлопнуты по минимуму стоимости.
    private int CollectArcs(int u)
    {
        NetworkStore n = _net;
        ref readonly SegmentData sd = ref n.Segments[u >> 1];
        int dir = u & 1;
        int firstLane = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
        int laneCount = dir == 0 ? sd.LanesAB : sd.LanesBA;
        int count = 0;
        int node = EndNode[u];
        float nodePenalty = NodePenalty(n, node, in sd);
        for (int li = 0; li < laneCount; li++)
        {
            int lane = firstLane + li;
            ref readonly LaneData ld = ref n.Lanes[lane];
            for (int k = 0; k < ld.NextCount; k++)
            {
                int conn = n.LaneNext[ld.FirstNext + k];
                ref readonly LaneData cd = ref n.Lanes[conn];
                if (cd.Kind != LaneKind.Connector)
                {
                    continue;
                }

                ref readonly LaneData outLane = ref n.Lanes[cd.ToLane];
                int w = (outLane.Segment * 2) + outLane.Direction;
                Maneuver man = Classify(n, in ld, in outLane, (u >> 1) == outLane.Segment);
                float outLimit = n.Segments[outLane.Segment].SpeedLimit;
                float cost = (cd.Length / MathF.Min(cd.SpeedLimit, outLimit)) + RoutingCosts.OfManeuver(man) + nodePenalty;
                count = Insert(count, w, cost, (byte)man);
            }
        }

        return count;
    }

    private int Insert(int count, int w, float cost, byte man)
    {
        int pos = 0;
        while (pos < count && _scratchTo[pos] < w)
        {
            pos++;
        }

        if (pos < count && _scratchTo[pos] == w)
        {
            if (cost < _scratchCost[pos])
            {
                _scratchCost[pos] = cost;
                _scratchMan[pos] = man;
            }

            return count;
        }

        if (count >= ScratchArcs)
        {
            return count;
        }

        for (int i = count; i > pos; i--)
        {
            _scratchTo[i] = _scratchTo[i - 1];
            _scratchCost[i] = _scratchCost[i - 1];
            _scratchMan[i] = _scratchMan[i - 1];
        }

        _scratchTo[pos] = w;
        _scratchCost[pos] = cost;
        _scratchMan[pos] = man;
        return count + 1;
    }

    // Манёвр по геометрии: вход (последний отрезок входной полосы) против выхода (первый отрезок выходной).
    // Знак векторного произведения как в NetworkBuilder.Classify (cross >= 0 - направо).
    private static Maneuver Classify(NetworkStore n, in LaneData lin, in LaneData lout, bool sameSegment)
    {
        if (sameSegment)
        {
            return Maneuver.UTurn;
        }

        Vec2 a1 = n.LanePoints[lin.FirstPoint + lin.PointCount - 1];
        Vec2 a0 = n.LanePoints[lin.FirstPoint + lin.PointCount - 2];
        Vec2 b0 = n.LanePoints[lout.FirstPoint];
        Vec2 b1 = n.LanePoints[lout.FirstPoint + 1];
        Vec2 u = a1 - a0;
        Vec2 v = b1 - b0;
        float lu = MathF.Sqrt((u.X * u.X) + (u.Z * u.Z));
        float lv = MathF.Sqrt((v.X * v.X) + (v.Z * v.Z));
        if (lu <= 0f || lv <= 0f)
        {
            return Maneuver.Straight;
        }

        float dot = ((u.X * v.X) + (u.Z * v.Z)) / (lu * lv);
        if (dot >= RoutingCosts.StraightCos)
        {
            return Maneuver.Straight;
        }

        float cross = (u.X * v.Z) - (u.Z * v.X);
        return cross >= 0f ? Maneuver.Right : Maneuver.Left;
    }

    // Штраф узла для въезда по сегменту: светофор - средняя задержка; Yield/Stop - только с минорного рукава
    // (въезд по Street в узел, где есть Arterial); RightHandRule - при >= 3 рукавах.
    private static float NodePenalty(NetworkStore n, int node, in SegmentData inSeg)
    {
        ref readonly NodeData nd = ref n.Nodes[node];
        switch (nd.Control)
        {
            case NodeControl.Signal:
                return RoutingCosts.SignalDelay(RoutingCosts.DefaultCycle, RoutingCosts.DefaultGreen);
            case NodeControl.Yield:
            case NodeControl.Stop:
                if (inSeg.Kind == RoadKind.Street && HasArterialArm(n, in nd))
                {
                    return nd.Control == NodeControl.Yield ? RoutingCosts.Yield : RoutingCosts.Stop;
                }

                return 0f;
            case NodeControl.RightHandRule:
                return nd.ArmCount >= 3 ? RoutingCosts.RightHandRule : 0f;
            default:
                return 0f;
        }
    }

    private static bool HasArterialArm(NetworkStore n, in NodeData nd)
    {
        for (int i = 0; i < nd.ArmCount; i++)
        {
            if (n.Segments[n.NodeArms[nd.FirstArm + i]].Kind == RoadKind.Arterial)
            {
                return true;
            }
        }

        return false;
    }
}
