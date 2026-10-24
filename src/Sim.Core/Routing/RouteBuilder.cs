using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

internal enum BuildStatus : byte
{
    Ok = 0,
    NoPath = 1,    // пути нет (RouteFailed)
    Deferred = 2,  // бюджет раскрытий тика исчерпан либо в пуле/арене нет места - повторить позже
}

/// <summary>
/// Сборка маршрута машины (routing.md 4.2, 5): префикс + магистраль из кэша по паре зон + суффикс, раскладка
/// на полосы, запись индивидуального маршрута в <see cref="RoutePool"/>. Не зависит от World: принимает
/// <see cref="NetworkStore"/>, <see cref="RoutePool"/> и ёмкости. Не подключён к фазам (задача 3-R).
/// Все буферы выделяются в конструкторе; <see cref="Build"/> не аллоцирует.
/// </summary>
internal sealed class RouteBuilder
{
    /// <summary>Предел раскрытий для локальных поисков префикса/суффикса (routing.md 2).</summary>
    public const int LocalExpansionCap = 512;

    /// <summary>Оценка длины на одно перестроение, м (routing.md 5; в этой версии не используется при выборе connector'а).</summary>
    public const float LaneChangeDistance = 60f;

    private const int ExpansionsPerQuery = 256;
    private const int MaxStarts = 64;

    private readonly NetworkStore _net;
    private readonly RoutePool _pool;
    private readonly RouteCache _cache;
    private readonly int[] _trunk;
    private readonly int[] _prefix;
    private readonly int[] _suffix;
    private readonly int[] _combined;
    private readonly RouteStep[] _steps;
    private readonly int[] _starts = new int[MaxStarts];
    private readonly float[] _startG = new float[MaxStarts];

    public RouteBuilder(NetworkStore net, RoutePool pool, in SimCapacities caps)
        : this(net, pool, caps.MaxZones, caps.PathfindBudgetPerTick)
    {
    }

    public RouteBuilder(NetworkStore net, RoutePool pool, int maxZones, int pathfindBudgetPerTick)
    {
        _net = net;
        _pool = pool;
        Graph = new RouteGraph(net);
        Searcher = new AStarSearcher(Graph);
        _cache = new RouteCache(pool, maxZones);
        ExpansionBudget = pathfindBudgetPerTick > 0 ? pathfindBudgetPerTick * ExpansionsPerQuery : 0;
        int dirSegs = Graph.DirSegCapacity;
        _trunk = new int[dirSegs + 2];
        _prefix = new int[LocalExpansionCap + 2];
        _suffix = new int[LocalExpansionCap + 2];
        _combined = new int[dirSegs + (2 * LocalExpansionCap) + 8];
        _steps = new RouteStep[(2 * _combined.Length) + 1];
    }

    public RouteGraph Graph { get; }

    public AStarSearcher Searcher { get; }

    public RouteCache Cache => _cache;

    /// <summary>Раскрытий на тик (0 = без ограничения). Проверяется между поисками.</summary>
    public int ExpansionBudget { get; set; }

    public int ExpansionsThisTick { get; private set; }

    // ---- счётчики (routing.md 8) ----
    public long Queries { get; private set; }

    public long CacheHits { get; private set; }

    public long ExpandedNodes { get; private set; }

    public long Failed { get; private set; }

    public long Deferred { get; private set; }

    public long CacheSkips { get; private set; }

    public int Compactions => _pool.Compactions;

    /// <summary>Начало тика: сброс счётчика раскрытий бюджета.</summary>
    public void BeginTick() => ExpansionsThisTick = 0;

    /// <summary>
    /// Строит маршрут от <paramref name="startLane"/>/<paramref name="startS"/> до <paramref name="destLane"/>/<paramref name="destS"/>.
    /// Зоны &gt;= 0 включают кэш магистралей; иначе - прямой поиск. Маршрут - индивидуальная запись пула с RefCount = 1.
    /// </summary>
    public BuildStatus Build(int startLane, float startS, int destLane, float destS, int fromZone, int toZone, long tick, out RouteHandle handle)
    {
        handle = RouteHandle.Null;
        if (Graph.BuiltVersion != _net.Version)
        {
            Graph.Rebuild();
        }

        if (ExpansionBudget > 0 && ExpansionsThisTick >= ExpansionBudget)
        {
            Deferred++;
            return BuildStatus.Deferred;
        }

        Queries++;
        if (!IsNormalLane(startLane) || !IsNormalLane(destLane))
        {
            Failed++;
            return BuildStatus.NoPath;
        }

        int u0 = RouteGraph.DirSegOfLane(_net, startLane);
        int goal = RouteGraph.DirSegOfLane(_net, destLane);
        float segLen = _net.Segments[u0 >> 1].Length;
        float frac = segLen > 0f ? Math.Clamp(startS / segLen, 0f, 1f) : 0f;
        float startCost = Graph.SegCost[u0] * (1f - frac);

        int len = -1;
        if (u0 == goal)
        {
            if (startS <= destS)
            {
                _combined[0] = u0;
                len = 1;
            }
            else
            {
                Failed++;
                return BuildStatus.NoPath;
            }
        }

        if (len < 0 && fromZone >= 0 && toZone >= 0 && fromZone != toZone && fromZone < _net.ZoneCount && toZone < _net.ZoneCount)
        {
            int hubA = _net.Zones[fromZone].HubNode;
            int hubB = _net.Zones[toZone].HubNode;
            if (hubA >= 0 && hubB >= 0 && hubA != hubB)
            {
                int t = GetTrunk(fromZone, toZone, hubA, hubB, tick);
                if (t > 0)
                {
                    len = ComposeViaTrunk(u0, startCost, goal, t);
                }
            }
        }

        if (len < 0)
        {
            _starts[0] = u0;
            _startG[0] = startCost;
            SearchStatus st = Search(1, goal, -1, Graph.EndPos[goal], Graph.DirSegCount, _combined, out len);
            if (st != SearchStatus.Found)
            {
                Failed++;
                return BuildStatus.NoPath;
            }
        }

        int stepCount = LayoutLanes(_combined, len, startLane, destLane, _steps);
        if (stepCount < 0)
        {
            Failed++;
            return BuildStatus.NoPath;
        }

        if (!AddRoute(new ReadOnlySpan<RouteStep>(_steps, 0, stepCount), -1, -1, tick, out handle))
        {
            Deferred++;
            return BuildStatus.Deferred;
        }

        _pool.RefCount[handle.Index] = 1;
        return BuildStatus.Ok;
    }

    /// <summary>
    /// Раскладка DirSeg-пути на полосы (routing.md 5): <c>[L0, C0, L1, ..., Lk]</c>, 2k+1 шагов. Возвращает число шагов или -1.
    /// Выбор connector'а: минимум |LaneNo(ToLane) - LaneNo(L_{j+1})|; для первого сегмента ничья - ближе к стартовой полосе,
    /// далее - меньший индекс connector'а.
    /// </summary>
    public int LayoutLanes(ReadOnlySpan<int> path, int len, int startLane, int destLane, Span<RouteStep> output)
    {
        if (len < 1 || output.Length < (2 * len) - 1)
        {
            return -1;
        }

        int k = len - 1;
        int lane = destLane;
        output[2 * k] = new RouteStep { Lane = lane, Node = -1 };
        int startNo = _net.Lanes[startLane].LaneNo;
        for (int j = k - 1; j >= 0; j--)
        {
            int u = path[j];
            int w = path[j + 1];
            int firstLane = (u & 1) == 0 ? _net.Segments[u >> 1].FirstLaneAB : _net.Segments[u >> 1].FirstLaneBA;
            int laneCount = (u & 1) == 0 ? _net.Segments[u >> 1].LanesAB : _net.Segments[u >> 1].LanesBA;
            int targetNo = _net.Lanes[lane].LaneNo;
            int bestConn = -1;
            int bestDist = int.MaxValue;
            int bestTie = int.MaxValue;
            for (int li = 0; li < laneCount; li++)
            {
                ref readonly LaneData ld = ref _net.Lanes[firstLane + li];
                for (int c = 0; c < ld.NextCount; c++)
                {
                    int conn = _net.LaneNext[ld.FirstNext + c];
                    ref readonly LaneData cd = ref _net.Lanes[conn];
                    if (cd.Kind != LaneKind.Connector)
                    {
                        continue;
                    }

                    ref readonly LaneData to = ref _net.Lanes[cd.ToLane];
                    if (((to.Segment * 2) + to.Direction) != w)
                    {
                        continue;
                    }

                    int dist = Math.Abs(to.LaneNo - targetNo);
                    int tie = j == 0 ? Math.Abs(ld.LaneNo - startNo) : 0;
                    if (dist < bestDist || (dist == bestDist && (tie < bestTie || (tie == bestTie && conn < bestConn))))
                    {
                        bestDist = dist;
                        bestTie = tie;
                        bestConn = conn;
                    }
                }
            }

            if (bestConn < 0)
            {
                return -1;
            }

            ref readonly LaneData best = ref _net.Lanes[bestConn];
            output[(2 * j) + 1] = new RouteStep { Lane = bestConn, Node = best.Node };
            lane = best.FromLane;
            output[2 * j] = new RouteStep { Lane = lane, Node = Graph.EndNode[u] };
        }

        return (2 * len) - 1;
    }

    private bool IsNormalLane(int lane) =>
        (uint)lane < (uint)_net.LaneCount && _net.Lanes[lane].Kind != LaneKind.Connector && _net.Lanes[lane].Segment >= 0;

    private SearchStatus Search(int startCount, int goalSeg, int goalNode, Vec2 goalPos, int cap, Span<int> path, out int len)
    {
        SearchStatus st = Searcher.Search(
            new ReadOnlySpan<int>(_starts, 0, startCount),
            new ReadOnlySpan<float>(_startG, 0, startCount),
            goalSeg,
            goalNode,
            goalPos,
            cap,
            path,
            out len);
        ExpandedNodes += Searcher.LastExpanded;
        ExpansionsThisTick += Searcher.LastExpanded;
        return st;
    }

    // Магистраль зонной пары: из кэша или полным поиском с вставкой в кэш. Результат - в _trunk, возвращает длину (0 = нет).
    private int GetTrunk(int fromZone, int toZone, int hubA, int hubB, long tick)
    {
        int idx = _cache.Lookup(fromZone, toZone, _net.Version, tick);
        if (idx >= 0)
        {
            CacheHits++;
            int n = _pool.Count[idx];
            int first = _pool.First[idx];
            for (int i = 0; i < n; i++)
            {
                _trunk[i] = _pool.Steps[first + i].Lane;
            }

            return n;
        }

        int starts = 0;
        ref readonly NodeData nd = ref _net.Nodes[hubA];
        for (int i = 0; i < nd.ArmCount && starts < MaxStarts; i++)
        {
            int seg = _net.NodeArms[nd.FirstArm + i];
            ref readonly SegmentData sd = ref _net.Segments[seg];
            if (sd.NodeA == hubA && sd.LanesAB > 0)
            {
                _starts[starts] = seg * 2;
                _startG[starts++] = Graph.SegCost[seg * 2];
            }

            if (sd.NodeB == hubA && sd.LanesBA > 0)
            {
                _starts[starts] = (seg * 2) + 1;
                _startG[starts++] = Graph.SegCost[(seg * 2) + 1];
            }
        }

        if (starts == 0)
        {
            return 0;
        }

        SearchStatus st = Search(starts, -1, hubB, _net.Nodes[hubB].Position, Graph.DirSegCount, _trunk, out int len);
        if (st != SearchStatus.Found)
        {
            return 0;
        }

        for (int i = 0; i < len; i++)
        {
            _steps[i] = new RouteStep { Lane = _trunk[i], Node = Graph.EndNode[_trunk[i]] };
        }

        if (!AddRoute(new ReadOnlySpan<RouteStep>(_steps, 0, len), fromZone, toZone, tick, out RouteHandle h) ||
            !_cache.Insert(fromZone, toZone, h, tick))
        {
            if (!h.IsNull)
            {
                _pool.Free(h);
            }

            CacheSkips++;
        }

        return len;
    }

    // префикс (старт -> Trunk[0]) + Trunk[1..] + суффикс (Trunk[last] -> цель)[1..]; -1 = упёрлись в cap / нет пути.
    private int ComposeViaTrunk(int u0, float startCost, int goal, int trunkLen)
    {
        _starts[0] = u0;
        _startG[0] = startCost;
        if (Search(1, _trunk[0], -1, Graph.EndPos[_trunk[0]], LocalExpansionCap, _prefix, out int pl) != SearchStatus.Found)
        {
            return -1;
        }

        _starts[0] = _trunk[trunkLen - 1];
        _startG[0] = 0f;
        if (Search(1, goal, -1, Graph.EndPos[goal], LocalExpansionCap, _suffix, out int sl) != SearchStatus.Found)
        {
            return -1;
        }

        int n = 0;
        for (int i = 0; i < pl; i++)
        {
            _combined[n++] = _prefix[i];
        }

        for (int i = 1; i < trunkLen; i++)
        {
            _combined[n++] = _trunk[i];
        }

        for (int i = 1; i < sl; i++)
        {
            _combined[n++] = _suffix[i];
        }

        return n;
    }

    // Запись в пул: при нехватке слота/арены - компактификация и вытеснение магистрали (routing.md 4.3).
    private bool AddRoute(ReadOnlySpan<RouteStep> steps, int fromZone, int toZone, long tick, out RouteHandle handle)
    {
        bool compacted = false;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            if (steps.Length > _pool.Steps.Length)
            {
                break;
            }

            if (_pool.TryAdd(steps, fromZone, toZone, _net.Version, out handle))
            {
                _pool.LastUsedTick[handle.Index] = tick;
                return true;
            }

            bool slotsFull = _pool.Pool.AliveCount >= _pool.Pool.Capacity;
            if (!slotsFull && !compacted)
            {
                _pool.Compact();
                compacted = true;
                continue;
            }

            if (!_cache.EvictOne(Queries + attempt))
            {
                break;
            }

            compacted = false;
        }

        handle = RouteHandle.Null;
        return false;
    }
}
