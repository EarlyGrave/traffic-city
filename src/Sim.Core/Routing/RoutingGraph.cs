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
    private Rules.Intersections.SignalStore? _signals;
    private float[]? _connDelay;

    public readonly int[] ArcFirst;      // [DirSegCapacity + 1]
    public readonly int[] ArcTo;         // [ArcCapacity] целевой DirSeg
    public readonly float[] ArcCost;     // [ArcCapacity] время connector'а + штраф манёвра + штраф узла, с
    public readonly byte[] ArcManeuver;  // [ArcCapacity] манёвр дуги (минимальный по стоимости)
    public readonly float[] SegCost;     // [DirSegCapacity] Length / SpeedLimit, с
    public readonly int[] StartNode;     // [DirSegCapacity]
    public readonly int[] EndNode;       // [DirSegCapacity]
    public readonly Vec2[] EndPos;       // [DirSegCapacity]

    /// <summary>
    /// [DirSegCapacity] классы, которым направленный сегмент закрыт (vehicle-classes.md §2.3, ADR-0039): запрет улицы <c>SegmentData.Deny</c> | классы, которым
    /// в этом направлении не разрешена ни одна полоса (AND масок полос). Производное, пересчитывает <see cref="RecalcDeny"/>.
    /// </summary>
    public readonly byte[] DirSegDeny;

    /// <summary>OR <see cref="DirSegDeny"/> по всей сети: права, у которых есть бит вне неё, проходят везде - поиск идёт прежним кодом без проверок.</summary>
    public byte DenyUnion { get; private set; }

    /// <summary>Растёт при каждом пересчёте масок (<see cref="RecalcDeny"/>).</summary>
    public int AccessVersion { get; private set; }

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
        DirSegDeny = new byte[DirSegCapacity];
        Rebuild();
    }

    public int DirSegCapacity { get; }

    public int ArcCapacity { get; }

    public int DirSegCount { get; private set; }

    public int ArcCount { get; private set; }

    public float VMax { get; private set; } = 1f;

    /// <summary>Версия сети, по которой построен граф.</summary>
    public int BuiltVersion { get; internal set; } = -1;

    /// <summary>Версия весов сегментов: растёт при каждом <see cref="RecalcCosts"/>; оверлей сравнивает с версией своей последней кастомизации.</summary>
    public int WeightsVersion { get; internal set; }

    /// <summary>true, если в сети больше дуг, чем помещается (граф усечён).</summary>
    public bool Truncated { get; private set; }

    public static int DirSegOfLane(NetworkStore net, int lane) => (net.Lanes[lane].Segment * 2) + net.Lanes[lane].Direction;

    /// <summary>
    /// Подключает светофоры (2.5, ADR-0018, intersections.md 6.9): штраф узла <c>Signal</c> берётся из плана слота (Webster), а для режима
    /// <c>Adaptive</c> заменяется измеренной задержкой connector'а <paramref name="connDelay"/> (индекс = <c>LaneData.ConflictGroup</c>).
    /// </summary>
    public void AttachSignals(Rules.Intersections.SignalStore signals, float[] connDelay)
    {
        _signals = signals;
        _connDelay = connDelay;
    }

    /// <summary>
    /// Пересчитывает штрафы дуг, входящих в светофорные узлы (план мог измениться, у Adaptive появилась измеренная задержка); топология дуг
    /// не меняется. Вызывается раз в пересчёт весов перед <see cref="RecalcCosts"/>. Без аллокаций.
    /// </summary>
    public void RefreshSignalCosts()
    {
        if (_signals == null)
        {
            return;
        }

        NetworkStore n = _net;
        for (int u = 0; u < DirSegCount; u++)
        {
            int node = EndNode[u];
            if (node < 0 || n.Nodes[node].Control != NodeControl.Signal || n.Nodes[node].Signal < 0)
            {
                continue;
            }

            int first = ArcFirst[u];
            int cnt = CollectArcs(u);
            if (cnt != ArcFirst[u + 1] - first)
            {
                continue;
            }

            for (int k = 0; k < cnt; k++)
            {
                if (_scratchTo[k] == ArcTo[first + k])
                {
                    ArcCost[first + k] = _scratchCost[k];
                    ArcManeuver[first + k] = _scratchMan[k];
                }
            }
        }
    }

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

    /// <summary>Нижняя граница отношения скорости к лимиту в весе по загрузке (routing.md 9).</summary>
    public const float MinSpeedRatio = 0.2f;

    /// <summary>
    /// Вес по загрузке (ADR-0006): <c>SegCost = Length / (SpeedLimit * clamp(SpeedRatio, 0.2, 1))</c> по <paramref name="loads"/>
    /// (индекс = сегмент). Штрафы манёвров и узлов (<see cref="ArcCost"/>) не меняются. Верхняя граница 1 сохраняет допустимость
    /// эвристики A* (<see cref="VMax"/>). Растит <see cref="WeightsVersion"/>. Без аллокаций.
    /// </summary>
    public void RecalcCosts(SegmentLoad[] loads, int segmentCount)
    {
        int n = Math.Min(segmentCount, Math.Min(_net.SegmentCount, loads.Length));
        for (int s = 0; s < n; s++)
        {
            ref readonly SegmentData sd = ref _net.Segments[s];
            if (!NetworkAlive.IsAlive(sd.Generation))
            {
                SegCost[s * 2] = float.PositiveInfinity;
                SegCost[(s * 2) + 1] = float.PositiveInfinity;
                continue;
            }

            if ((sd.Flags & SegmentFlags.Closing) != 0)
            {
                // Закрыт дренажем (4.3, T4): вес +inf, A* и кастомизация оверлея его не раскрывают.
                SegCost[s * 2] = float.PositiveInfinity;
                SegCost[(s * 2) + 1] = float.PositiveInfinity;
                continue;
            }

            float ratio = Math.Clamp(loads[s].SpeedRatio, MinSpeedRatio, 1f);
            float cost = sd.Length / (sd.SpeedLimit * ratio);
            SegCost[s * 2] = cost;
            SegCost[(s * 2) + 1] = cost;
        }

        WeightsVersion++;
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
            if (!NetworkAlive.IsAlive(sd.Generation))
            {
                // Мёртвый слот сегмента (4.3, T2): вершин и дуг нет, вес +inf.
                for (int d = 0; d < 2; d++)
                {
                    int dead = (s * 2) + d;
                    StartNode[dead] = -1;
                    EndNode[dead] = -1;
                    EndPos[dead] = default;
                    SegCost[dead] = float.PositiveInfinity;
                }

                continue;
            }

            vmax = MathF.Max(vmax, sd.SpeedLimit);
            for (int d = 0; d < 2; d++)
            {
                int u = (s * 2) + d;
                int a = d == 0 ? sd.NodeA : sd.NodeB;
                int b = d == 0 ? sd.NodeB : sd.NodeA;
                StartNode[u] = a;
                EndNode[u] = b;
                EndPos[u] = n.Nodes[b].Position;
                SegCost[u] = (sd.Flags & SegmentFlags.Closing) != 0 ? float.PositiveInfinity : sd.Length / sd.SpeedLimit;
            }
        }

        VMax = vmax;

        int arcs = 0;
        for (int u = 0; u < DirSegCount; u++)
        {
            ArcFirst[u] = arcs;
            int cnt = EndNode[u] < 0 ? 0 : CollectArcs(u);
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
        RecalcDeny();
    }

    /// <summary>
    /// Пересчёт <see cref="DirSegDeny"/> и <see cref="DenyUnion"/> по маскам полос и улиц (после <see cref="Rebuild"/> и правок масок: <c>SetLaneDeny</c>,
    /// <c>SetStreetAccess</c>). O(DirSeg + полос), главный поток, вне параллельных фаз; без аллокаций. Оверлей пересчитывает свои <c>CellDeny</c> сам.
    /// </summary>
    public void RecalcDeny()
    {
        NetworkStore n = _net;
        byte union = 0;
        for (int u = 0; u < DirSegCount; u++)
        {
            ref readonly SegmentData sd = ref n.Segments[u >> 1];
            byte deny = 0;
            if (EndNode[u] >= 0)
            {
                int dir = u & 1;
                int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
                int cnt = dir == 0 ? sd.LanesAB : sd.LanesBA;
                byte all = cnt > 0 ? (byte)0xFF : (byte)0;
                for (int k = 0; k < cnt; k++)
                {
                    all &= n.Lanes[first + k].Deny;
                }

                deny = (byte)(sd.Deny | all);
            }

            DirSegDeny[u] = deny;
            union |= deny;
        }

        DenyUnion = union;
        AccessVersion++;
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
        float nodePenalty = NodePenalty(n, node, u >> 1, in sd);
        int adaptiveSlot = AdaptiveSlot(n, node);
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
                float pen = nodePenalty;
                if (adaptiveSlot >= 0 && _connDelay != null && (uint)cd.ConflictGroup < (uint)_connDelay.Length && _connDelay[cd.ConflictGroup] > 0f)
                {
                    pen = MathF.Min(_connDelay[cd.ConflictGroup], MaxSignalPenalty);   // измеренная задержка заменяет статику (6.9)
                }

                float cost = (cd.Length / MathF.Min(cd.SpeedLimit, outLimit)) + RoutingCosts.OfManeuver(man) + pen;
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
    private float NodePenalty(NetworkStore n, int node, int inSegIndex, in SegmentData inSeg)
    {
        ref readonly NodeData nd = ref n.Nodes[node];
        switch (nd.Control)
        {
            case NodeControl.Signal:
                return SignalPenalty(n, in nd, inSegIndex);
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

    /// <summary>Верхняя граница штрафа светофора, с (routing.md 1.1: клэмп 0…60 с).</summary>
    public const float MaxSignalPenalty = 60f;

    /// <summary>Слот светофора узла в режиме <c>Adaptive</c> или -1 (только такие узлы берут измеренную задержку).</summary>
    private int AdaptiveSlot(NetworkStore n, int node)
    {
        if (_signals == null || n.Nodes[node].Control != NodeControl.Signal)
        {
            return -1;
        }

        int slot = n.Nodes[node].Signal;
        return (uint)slot < (uint)_signals.Count && _signals.Mode[slot] == SignalMode.Adaptive ? slot : -1;
    }

    // Статическая оценка задержки на светофоре: Webster (C - g)^2 / 2C по плану слота (Fixed: цикл и зелёный оси входного рукава; Adaptive:
    // g = (MinGreen + MaxGreen)/2, C = Σ осей); слота нет (явная сеть без плана) - план по умолчанию 56/24 с, как на этапе 1.
    private float SignalPenalty(NetworkStore n, in NodeData nd, int inSegIndex)
    {
        if (_signals != null && (uint)nd.Signal < (uint)_signals.Count)
        {
            int slot = nd.Signal;
            int axis = -1;
            for (int a = 0; a < nd.ArmCount && a < Rules.Intersections.SignalStore.ArmCap; a++)
            {
                if (n.NodeArms[nd.FirstArm + a] == inSegIndex)
                {
                    axis = _signals.ArmAxis[(slot * Rules.Intersections.SignalStore.ArmCap) + a];
                    break;
                }
            }

            if (axis >= 0)
            {
                float d = _signals.StaticDelaySeconds(slot, axis);
                if (d >= 0f)
                {
                    return MathF.Min(d, MaxSignalPenalty);
                }
            }
        }

        return RoutingCosts.SignalDelay(RoutingCosts.DefaultCycle, RoutingCosts.DefaultGreen);
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
