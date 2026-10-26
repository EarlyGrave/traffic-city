using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Запрос маршрута по многоуровневому оверлею (routing.md 9.4, ADR-0009): однонаправленный A* с эвристикой плоского поиска
/// (<c>|Δpos| / VMax · 0.95</c>), правило уровня CRP (вершина раскрывается матрицей старшего уровня, чья ячейка не содержит ни
/// старт, ни цель), индексная 4-арная куча с decrease-key, ничьи по <c>(f, DirSeg)</c>. Без оверлея (<c>levels = 0</c>, <c>null</c>
/// или <see cref="Overlay.Disabled"/>) все вершины раскрываются по дугам графа - это плоский A* (эталон тестов).
/// Результат - <b>план</b>: массив <c>DirSeg | level &lt;&lt; 28</c> от старта к цели; уровень элемента - уровень дуги, которой в него
/// входят (у первого 0). План разворачивается лениво (<see cref="Unpack"/>: дуга уровня q -> цепочка дуг уровня q-1 внутри одной
/// ячейки) либо целиком (<see cref="ExpandPlan"/>, <see cref="Route"/>). Все буферы выделены в конструкторе, запросы не аллоцируют.
/// </summary>
internal sealed class OverlaySearcher
{
    public const int LevelShift = 28;
    public const int SegMask = (1 << LevelShift) - 1;

    /// <summary>Ёмкость буфера одного развёртывания дуги (элементов).</summary>
    public const int UnpackCap = 256;

    private const float HeuristicScale = 0.95f;

    private readonly NetworkStore _net;
    private readonly RouteGraph _g;
    private readonly Overlay? _ov;
    private readonly float[] _gs;
    private readonly int[] _parent;
    private readonly byte[] _parentLevel;
    private readonly uint[] _seen;
    private readonly uint[] _closed;
    private readonly IndexedHeap4 _heap;
    private readonly int[] _sCell = new int[Overlay.MaxLevels + 2];
    private readonly int[] _tCell = new int[Overlay.MaxLevels + 2];
    private readonly int[] _plan;
    private readonly int[] _path;
    private readonly int[] _stack;
    private readonly int[] _ubuf = new int[UnpackCap];
    private uint _searchId;

    public OverlaySearcher(NetworkStore net, RouteGraph graph, Overlay? overlay)
    {
        _net = net;
        _g = graph;
        _ov = overlay;
        int n = graph.DirSegCapacity;
        _gs = new float[n];
        _parent = new int[n];
        _parentLevel = new byte[n];
        _seen = new uint[n];
        _closed = new uint[n];
        _heap = new IndexedHeap4(n);
        _plan = new int[n + 8];
        _path = new int[n + 8];
        _stack = new int[n + (UnpackCap * Overlay.MaxLevels) + 16];
        WorkCap = Math.Max(1, 4 * n);
    }

    /// <summary>Предел релаксаций одного запроса (<c>query_work_cap</c>, дефолт <c>4 · DirSegCapacity</c>); превышение - <see cref="SearchStatus.ExpansionCap"/>.</summary>
    public int WorkCap { get; set; }

    /// <summary>Стоимость плана последнего запроса (G цели), с.</summary>
    public float PlanCost { get; private set; }

    /// <summary>Раскрытых вершин и релаксаций (дуга графа или элемент матрицы) последнего запроса.</summary>
    public int LastExpanded { get; private set; }

    public int LastWork { get; private set; }

    /// <summary>Стоимость и работа последнего <see cref="Unpack"/>.</summary>
    public float UnpackCost { get; private set; }

    public int LastUnpackWork { get; private set; }

    // ---- накопительные счётчики (PathfindQueries, PathfindWork, PathfindExpandedNodes, PlanUnpacks, UnpackWork) ----
    public long Queries { get; private set; }

    public long TotalWork { get; private set; }

    public long TotalExpanded { get; private set; }

    public long Unpacks { get; private set; }

    public long UnpackWork { get; private set; }

    /// <summary>Оверлей запросов (null = плоский поиск).</summary>
    public Overlay? Overlay => _ov;

    private bool Active => _ov != null && !_ov.Disabled;

    /// <summary>
    /// Запрос от <paramref name="startLane"/>/<paramref name="startS"/> до <paramref name="destLane"/>/<paramref name="destS"/> (как
    /// <see cref="Router.Build"/>). Граф должен быть актуален. Возвращает <see cref="SearchStatus.Found"/> и план в <paramref name="plan"/>.
    /// </summary>
    public SearchStatus Query(int startLane, float startS, int destLane, float destS, Span<int> plan, out int planLen)
    {
        planLen = 0;
        PlanCost = 0f;
        LastExpanded = 0;
        LastWork = 0;
        if (!IsNormalLane(startLane) || !IsNormalLane(destLane))
        {
            return SearchStatus.NoPath;
        }

        int u0 = RouteGraph.DirSegOfLane(_net, startLane);
        int goal = RouteGraph.DirSegOfLane(_net, destLane);
        float segLen = _net.Segments[u0 >> 1].Length;
        float frac = segLen > 0f ? Math.Clamp(startS / segLen, 0f, 1f) : 0f;
        float c0 = _g.SegCost[u0];
        if (!float.IsFinite(c0))
        {
            c0 = 0f;   // стартовый сегмент закрыт дренажем (4.3, T4): машина уже на нём, цена остатка не должна делать все пути бесконечными
        }

        if (u0 == goal)
        {
            if (startS > destS || plan.Length < 1)
            {
                return SearchStatus.NoPath;
            }

            plan[0] = u0;
            planLen = 1;
            PlanCost = c0 * Math.Max(0f, (destS - startS) / Math.Max(segLen, 1e-3f));
            Queries++;
            return SearchStatus.Found;
        }

        return Search(u0, c0 * (1f - frac), goal, plan, out planLen);
    }

    /// <summary>
    /// Поиск от DirSeg <paramref name="u0"/> (начальная стоимость <paramref name="startG"/>) до DirSeg <paramref name="goal"/>.
    /// </summary>
    public SearchStatus Search(int u0, float startG, int goal, Span<int> plan, out int planLen)
    {
        planLen = 0;
        PlanCost = 0f;
        LastExpanded = 0;
        LastWork = 0;
        Queries++;
        uint id = NextId();
        _heap.Clear();
        int levels = Active ? _ov!.Levels : 0;
        int endU0 = _g.EndNode[u0];
        int startG0 = _g.StartNode[goal];
        for (int l = 1; l <= levels; l++)
        {
            _sCell[l] = _ov!.CellOfNode(l, endU0);
            _tCell[l] = _ov.CellOfNode(l, startG0);
        }

        Vec2 goalPos = _g.EndPos[goal];
        float invV = HeuristicScale / _g.VMax;
        _seen[u0] = id;
        _gs[u0] = startG;
        _parent[u0] = -1;
        _parentLevel[u0] = 0;
        _heap.PushOrDecrease(u0, startG + (H(u0, goalPos) * invV));

        int expanded = 0;
        int work = 0;
        SearchStatus status = SearchStatus.NoPath;
        while (_heap.Count > 0)
        {
            int v = _heap.Pop(out _);
            _closed[v] = id;
            if (v == goal)
            {
                PlanCost = _gs[v];
                status = Reconstruct(v, plan, out planLen);
                break;
            }

            if (work >= WorkCap)
            {
                status = SearchStatus.ExpansionCap;
                break;
            }

            expanded++;
            float gv = _gs[v];
            int q = LevelOf(v, levels);
            if (q > 0)
            {
                int r = _ov!.EntryIdx[((q - 1) * _g.DirSegCapacity) + v];
                if (r >= 0)
                {
                    work += ExpandMatrix(v, gv, q, r, id, goalPos, invV);
                    continue;
                }
            }

            work += ExpandArcs(v, gv, id, goalPos, invV);
        }

        LastExpanded = expanded;
        LastWork = work;
        TotalExpanded += expanded;
        TotalWork += work;
        return status;
    }

    /// <summary>
    /// Развёртывание дуги <paramref name="a"/> -> <paramref name="b"/> уровня <paramref name="q"/>: поиск внутри ячейки
    /// <c>cell_q(EndNode a)</c> по дугам уровня q-1. В <paramref name="output"/> - вершины после <paramref name="a"/> до
    /// <paramref name="b"/> включительно в прямом порядке, каждая как <c>DirSeg | (q-1) &lt;&lt; 28</c>.
    /// </summary>
    public SearchStatus Unpack(int a, int b, int q, Span<int> output, out int len)
    {
        len = 0;
        UnpackCost = 0f;
        LastUnpackWork = 0;
        if (!Active || q < 1 || q > _ov!.Levels)
        {
            return SearchStatus.NoPath;
        }

        Unpacks++;
        uint id = NextId();
        _heap.Clear();
        Vec2 goalPos = _g.EndPos[b];
        float invV = HeuristicScale / _g.VMax;
        int lq = q - 1;
        _seen[a] = id;
        _gs[a] = 0f;
        _parent[a] = -1;
        _parentLevel[a] = 0;
        _heap.PushOrDecrease(a, H(a, goalPos) * invV);
        int work = 0;
        SearchStatus status = SearchStatus.NoPath;
        int dirCap = _g.DirSegCapacity;
        while (_heap.Count > 0)
        {
            int v = _heap.Pop(out _);
            _closed[v] = id;
            float gv = _gs[v];
            if (v == b)
            {
                UnpackCost = gv;
                status = ReconstructForward(v, (byte)lq, output, out len);
                break;
            }

            if (lq == 0)
            {
                int end = _g.ArcFirst[v + 1];
                for (int k = _g.ArcFirst[v]; k < end; k++)
                {
                    work++;
                    int w = _g.ArcTo[k];
                    if (w == b || _ov.CutLevel[w] < q)
                    {
                        Relax(w, gv + _g.ArcCost[k] + _g.SegCost[w], v, 0, id, goalPos, invV);
                    }
                }

                continue;
            }

            int s = _ov.CellOfNode(lq, _g.EndNode[v]);
            int r = _ov.EntryIdx[((lq - 1) * dirCap) + v];
            if (r < 0)
            {
                continue; // по построению не бывает: v - вход подъячейки
            }

            int mb = _ov.MatrixFirst[s] + (r * _ov.ExitCap[s]);
            int xf = _ov.ExitFirst[s];
            int xn = _ov.ExitCount[s];
            for (int c = 0; c < xn; c++)
            {
                work++;
                float cost = _ov.Matrix[mb + c];
                if (float.IsPositiveInfinity(cost))
                {
                    continue;
                }

                int x = _ov.Exits[xf + c];
                if (x == b || _ov.CutLevel[x] < q)
                {
                    Relax(x, gv + cost, v, (byte)lq, id, goalPos, invV);
                }
            }
        }

        LastUnpackWork = work;
        UnpackWork += work;
        return status;
    }

    /// <summary>
    /// Полное развёртывание плана в цепочку DirSeg <paramref name="path"/> (первая и последняя вершины совпадают с планом). Эквивалент
    /// ленивой материализации из стека (routing.md 9.5): элементы уровня q &gt; 0 разворачиваются <see cref="Unpack"/> до уровня 0.
    /// </summary>
    public SearchStatus ExpandPlan(ReadOnlySpan<int> plan, Span<int> path, out int len)
    {
        len = 0;
        if (plan.Length < 1 || path.Length < 1)
        {
            return SearchStatus.NoPath;
        }

        int sp = 0;
        for (int i = plan.Length - 1; i >= 1; i--)
        {
            _stack[sp++] = plan[i];
        }

        int n = 0;
        path[n++] = plan[0] & SegMask;
        while (sp > 0)
        {
            int e = _stack[--sp];
            int v = e & SegMask;
            int q = e >> LevelShift;
            if (q == 0)
            {
                if (n >= path.Length)
                {
                    return SearchStatus.PathTooLong;
                }

                path[n++] = v;
                continue;
            }

            SearchStatus st = Unpack(path[n - 1], v, q, _ubuf, out int ul);
            if (st != SearchStatus.Found)
            {
                return st;
            }

            if (sp + ul > _stack.Length)
            {
                return SearchStatus.PathTooLong;
            }

            for (int i = ul - 1; i >= 0; i--)
            {
                _stack[sp++] = _ubuf[i];
            }
        }

        len = n;
        return SearchStatus.Found;
    }

    /// <summary>Запрос + полное развёртывание + раскладка на полосы (<see cref="RouteLayout.LayoutLanes"/>).</summary>
    public SearchStatus Route(int startLane, float startS, int destLane, float destS, Span<RouteStep> steps, out int stepCount)
    {
        stepCount = 0;
        SearchStatus st = Query(startLane, startS, destLane, destS, _plan, out int pl);
        if (st != SearchStatus.Found)
        {
            return st;
        }

        st = ExpandPlan(new ReadOnlySpan<int>(_plan, 0, pl), _path, out int len);
        if (st != SearchStatus.Found)
        {
            return st;
        }

        int count = RouteLayout.LayoutLanes(_net, _g, new ReadOnlySpan<int>(_path, 0, len), len, startLane, destLane, steps);
        if (count < 0)
        {
            return SearchStatus.NoPath;
        }

        stepCount = count;
        return SearchStatus.Found;
    }

    // ---- ядро ----

    // Уровень раскрытия вершины (routing.md 9.4): старший ℓ <= CutLevel(v), при котором ячейка конца v не содержит старт и цель; 0 - дуги графа.
    private int LevelOf(int v, int levels)
    {
        if (levels == 0)
        {
            return 0;
        }

        int top = Math.Min(levels, _ov!.CutLevel[v]);
        if (top == 0)
        {
            return 0;
        }

        int e = _g.EndNode[v];
        for (int l = top; l >= 1; l--)
        {
            int cell = _ov.CellOfNode(l, e);
            if (cell != _sCell[l] && cell != _tCell[l])
            {
                return l;
            }
        }

        return 0;
    }

    private int ExpandArcs(int v, float gv, uint id, Vec2 goalPos, float invV)
    {
        int first = _g.ArcFirst[v];
        int end = _g.ArcFirst[v + 1];
        for (int k = first; k < end; k++)
        {
            int w = _g.ArcTo[k];
            Relax(w, gv + _g.ArcCost[k] + _g.SegCost[w], v, 0, id, goalPos, invV);
        }

        return end - first;
    }

    private int ExpandMatrix(int v, float gv, int q, int r, uint id, Vec2 goalPos, float invV)
    {
        int c0 = _ov!.CellOfNode(q, _g.EndNode[v]);
        int mb = _ov.MatrixFirst[c0] + (r * _ov.ExitCap[c0]);
        int xf = _ov.ExitFirst[c0];
        int xn = _ov.ExitCount[c0];
        for (int c = 0; c < xn; c++)
        {
            float cost = _ov.Matrix[mb + c];
            if (float.IsPositiveInfinity(cost))
            {
                continue;
            }

            Relax(_ov.Exits[xf + c], gv + cost, v, (byte)q, id, goalPos, invV);
        }

        return xn;
    }

    private void Relax(int w, float ng, int parent, byte level, uint id, Vec2 goalPos, float invV)
    {
        if (_closed[w] == id || float.IsPositiveInfinity(ng))
        {
            return;   // закрытые дренажем сегменты (SegCost +inf, 4.3 T4) и пути через них не раскрываются
        }

        if (_seen[w] == id && ng >= _gs[w])
        {
            return;
        }

        _seen[w] = id;
        _gs[w] = ng;
        _parent[w] = parent;
        _parentLevel[w] = level;
        _heap.PushOrDecrease(w, ng + (H(w, goalPos) * invV));
    }

    private float H(int v, Vec2 goal)
    {
        float dx = _g.EndPos[v].X - goal.X;
        float dz = _g.EndPos[v].Z - goal.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private uint NextId()
    {
        if (++_searchId == 0)
        {
            Array.Clear(_seen);
            Array.Clear(_closed);
            _searchId = 1;
        }

        return _searchId;
    }

    private SearchStatus Reconstruct(int goal, Span<int> plan, out int length)
    {
        int len = 0;
        for (int v = goal; v >= 0; v = _parent[v])
        {
            len++;
        }

        length = len;
        if (len > plan.Length)
        {
            length = 0;
            return SearchStatus.PathTooLong;
        }

        int k = len;
        for (int v = goal; v >= 0; v = _parent[v])
        {
            plan[--k] = v | (_parentLevel[v] << LevelShift);
        }

        return SearchStatus.Found;
    }

    // Цепочка от вершины после корня поиска до goal; уровень каждого элемента задан явно (развёртывание: все дуги уровня q-1).
    private SearchStatus ReconstructForward(int goal, byte level, Span<int> output, out int length)
    {
        int len = 0;
        for (int v = goal; _parent[v] >= 0; v = _parent[v])
        {
            len++;
        }

        length = len;
        if (len > output.Length)
        {
            length = 0;
            return SearchStatus.PathTooLong;
        }

        int k = len;
        for (int v = goal; _parent[v] >= 0; v = _parent[v])
        {
            output[--k] = v | (level << LevelShift);
        }

        return SearchStatus.Found;
    }

    private bool IsNormalLane(int lane) =>
        (uint)lane < (uint)_net.LaneCount && _net.Lanes[lane].Kind != LaneKind.Connector && _net.Lanes[lane].Segment >= 0;
}
