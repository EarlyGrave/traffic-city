using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Routing;

internal enum BuildStatus : byte
{
    Ok = 0,
    NoPath = 1,    // пути нет (RouteFailed)
    Deferred = 2,  // нет слота маршрута / места в арене плана - повторить позже
    Skipped = 3,   // ничего не строилось (внутреннее значение плановой перемаршрутизации)
}

/// <summary>
/// Построение и обслуживание маршрутов (routing.md 9.4-9.7, ADR-0009): запрос <see cref="OverlaySearcher"/> -> план-стек + первое окно полосовых
/// шагов в <see cref="RouteStore"/>; дозаполнение окна ленивым развёртыванием плана; остаток стоимости для проверки дрейфа. Все буферы
/// выделены в конструкторе, методы не аллоцируют. Метрики пишутся прямо в <c>World.Counters</c> (запросы идут и из фаз 2b/8b - автобусы).
/// </summary>
internal sealed class Router
{
    /// <summary>Машина с не более чем столько DirSeg впереди в окне и непустым планом ставится в очередь дозаполнения (routing.md 9.5).</summary>
    public const int RefillAhead = 3;

    private readonly World _w;
    private readonly NetworkStore _net;
    private readonly RouteStore _store;
    private readonly int _maxDirs;
    private readonly int[] _plan;
    private readonly int[] _stk;
    private readonly int[] _dirs;
    private readonly int[] _ubuf = new int[OverlaySearcher.UnpackCap];
    private readonly RouteStep[] _steps;
    private readonly RouteStep[] _newWin;
    private int _sp;
    private int _nd;

    public Router(World w, RouteGraph graph, Overlay? overlay, int queryWorkCap)
    {
        _w = w;
        _net = w.Net;
        _store = w.Routes;
        Graph = graph;
        Searcher = new OverlaySearcher(_net, graph, overlay);
        if (queryWorkCap > 0)
        {
            Searcher.WorkCap = queryWorkCap;
        }

        int n = graph.DirSegCapacity;
        _maxDirs = Math.Max(1, (_store.WindowSteps + 1) / 2);
        _plan = new int[n + 8];
        _stk = new int[n + (OverlaySearcher.UnpackCap * Overlay.MaxLevels) + 16];
        _dirs = new int[_maxDirs + 2];
        _steps = new RouteStep[(2 * _maxDirs) + 2];
        _newWin = new RouteStep[_store.WindowSteps + 2];
    }

    public RouteGraph Graph { get; }

    public OverlaySearcher Searcher { get; }

    /// <summary>Работа запросов и развёртываний с начала бюджетного отсчёта (релаксации); сбрасывает фаза 3 после дозаполнений.</summary>
    public int WorkThisTick { get; set; }

    /// <summary>Приводит граф к текущей версии сети (после <c>BuildRoad</c> вне оверлея); веса возвращаются к загрузке.</summary>
    public void SyncGraph()
    {
        if (Graph.BuiltVersion != _net.Version)
        {
            Graph.Rebuild();
            Graph.RecalcCosts(_w.SegmentLoads, _net.SegmentCount);
        }
    }

    /// <summary>
    /// Строит маршрут от <paramref name="startLane"/>/<paramref name="startS"/> до <paramref name="destLane"/>/<paramref name="destS"/>:
    /// запрос, развёртывание первых DirSeg, раскладка на полосы -> окно слота; остаток плана - стек. Ok: <paramref name="handle"/> валиден.
    /// </summary>
    public BuildStatus Build(int startLane, float startS, int destLane, float destS, long tick, out RouteHandle handle)
    {
        handle = RouteHandle.Null;
        SyncGraph();
        if (_store.Pool.AliveCount >= _store.Pool.Capacity)
        {
            return BuildStatus.Deferred;
        }

        ref SimCounters c = ref _w.Counters;
        SearchStatus st = Searcher.Query(startLane, startS, destLane, destS, _plan, out int pl);
        c.PathfindQueries++;
        c.PathfindWork += Searcher.LastWork;
        c.PathfindExpandedNodes += Searcher.LastExpanded;
        WorkThisTick += Searcher.LastWork;
        if (st != SearchStatus.Found)
        {
            c.RouteFailed++;
            return BuildStatus.NoPath;
        }

        float cost = Searcher.PlanCost;
        _dirs[0] = _plan[0] & OverlaySearcher.SegMask;
        _nd = 1;
        _sp = 0;
        for (int i = pl - 1; i >= 1; i--)
        {
            _stk[_sp++] = _plan[i];
        }

        if (Materialize(_maxDirs) != SearchStatus.Found)
        {
            c.RouteFailed++;
            return BuildStatus.NoPath;
        }

        int layoutDest = _sp == 0 ? destLane : FirstLane(_dirs[_nd - 1]);
        int n = RouteLayout.LayoutLanes(_net, Graph, new ReadOnlySpan<int>(_dirs, 0, _nd), _nd, startLane, layoutDest, _steps);
        if (n < 0)
        {
            c.RouteFailed++;
            return BuildStatus.NoPath;
        }

        if (!_store.TryAllocate(out handle))
        {
            return BuildStatus.Deferred;
        }

        int slot = handle.Index;
        if (!_store.SetPlan(slot, new ReadOnlySpan<int>(_stk, 0, _sp)))
        {
            _store.Free(handle);
            handle = RouteHandle.Null;
            return BuildStatus.Deferred;
        }

        Array.Copy(_steps, 0, _store.Window, slot * _store.WindowSteps, n);
        _store.WinCount[slot] = (byte)n;
        _store.PlanCost[slot] = cost;
        _store.PlanTick[slot] = tick;
        return BuildStatus.Ok;
    }

    /// <summary>
    /// Дозаполнение окна машины <paramref name="i"/> (routing.md 9.5): шаги <c>[Cursor, Cursor+1]</c> (на connector'е - он сам) сохраняются
    /// со сдвигом в начало, остальное перекладывается заново по DirSeg «оставшиеся в окне + новые из плана» до заполнения слота.
    /// <c>RouteCursor</c> сдвигается на то же число, <c>NextConn</c> не меняется. false = не вышло (окно кончилось, развёртывание
    /// не удалось, нет места под план): вызывающий помечает промах.
    /// </summary>
    public bool Refill(int i)
    {
        VehicleStore v = _w.Vehicles;
        int r = v.Route[i];
        if (r < 0 || !_store.Pool.IsAlive(r, v.RouteGen[i]))
        {
            return true;
        }

        int pc = _store.PlanCount[r];
        if (pc == 0)
        {
            return true;
        }

        int width = _store.WindowSteps;
        int b = r * width;
        int wc = _store.WinCount[r];
        int cur = v.RouteCursor[i];
        RouteStep[] win = _store.Window;
        int prefix;
        int first;
        int tieLane;
        if (cur < 0)
        {
            prefix = 0;
            first = 0;
            tieLane = v.Lane[i];
        }
        else if (cur >= wc)
        {
            return false;
        }
        else if (_net.Lanes[win[b + cur].Lane].Kind == LaneKind.Connector)
        {
            prefix = 1;
            first = cur + 1;
            tieLane = win[b + cur].Lane;
        }
        else
        {
            if (cur + 1 >= wc)
            {
                return false;
            }

            prefix = 2;
            first = cur + 2;
            tieLane = win[b + cur + 1].Lane;
        }

        if ((uint)tieLane < (uint)_net.LaneCount && _net.Lanes[tieLane].Kind == LaneKind.Connector)
        {
            tieLane = _net.Lanes[tieLane].ToLane;
        }

        if ((uint)tieLane >= (uint)_net.LaneCount)
        {
            return false;
        }

        _nd = 0;
        for (int k = first; k < wc && _nd < _maxDirs; k += 2)
        {
            _dirs[_nd++] = RouteGraph.DirSegOfLane(_net, win[b + k].Lane);
        }

        if (_nd == 0)
        {
            return false;
        }

        int capDirs = (width - prefix + 1) / 2;
        if (_nd >= capDirs)
        {
            return true; // окно уже полное
        }

        _sp = pc;
        Array.Copy(_store.PlanArena, _store.PlanFirst[r], _stk, 0, pc);
        if (Materialize(capDirs) != SearchStatus.Found)
        {
            return false;
        }

        int layoutDest = _sp == 0 ? v.DestLane[i] : FirstLane(_dirs[_nd - 1]);
        int n = RouteLayout.LayoutLanes(_net, Graph, new ReadOnlySpan<int>(_dirs, 0, _nd), _nd, tieLane, layoutDest, _steps);
        if (n < 0 || prefix + n > width)
        {
            return false;
        }

        for (int k = 0; k < prefix; k++)
        {
            _newWin[k] = win[b + cur + k];
        }

        Array.Copy(_steps, 0, _newWin, prefix, n);
        if (!_store.SetPlan(r, new ReadOnlySpan<int>(_stk, 0, _sp)))
        {
            return false;
        }

        Array.Copy(_newWin, 0, win, b, prefix + n);
        _store.WinCount[r] = (byte)(prefix + n);
        if (cur > 0)
        {
            v.RouteCursor[i] = 0;
        }

        _w.Counters.WindowRefills++;
        return true;
    }

    /// <summary>
    /// Остаток стоимости маршрута машины <paramref name="i"/> при текущих весах, с (routing.md 9.7): остаток текущего сегмента, затем окно
    /// от курсора и стек плана (элемент уровня q &gt; 0 - клетка матрицы, O(1)). Машина должна стоять на обычной полосе.
    /// </summary>
    public float RemainingCost(int i)
    {
        VehicleStore v = _w.Vehicles;
        int r = v.Route[i];
        int lane = v.Lane[i];
        int curSeg = RouteGraph.DirSegOfLane(_net, lane);
        float len = _net.Segments[_net.Lanes[lane].Segment].Length;
        float frac = len > 0f ? Math.Clamp(v.S[i] / len, 0f, 1f) : 0f;
        float cost = Graph.SegCost[curSeg] * (1f - frac);
        int prev = curSeg;
        int b = r * _store.WindowSteps;
        int wc = _store.WinCount[r];
        for (int k = Math.Max(0, v.RouteCursor[i] + 1); k < wc; k++)
        {
            int ln = _store.Window[b + k].Lane;
            if (_net.Lanes[ln].Kind == LaneKind.Connector)
            {
                continue;
            }

            int u = RouteGraph.DirSegOfLane(_net, ln);
            cost += Graph.SegCost[u] + ArcOf(prev, u);
            prev = u;
        }

        Overlay? ov = Searcher.Overlay;
        int pf = _store.PlanFirst[r];
        for (int j = _store.PlanCount[r] - 1; j >= 0; j--)
        {
            int e = _store.PlanArena[pf + j];
            int u = e & OverlaySearcher.SegMask;
            int q = e >> OverlaySearcher.LevelShift;
            if (q == 0 || ov == null || ov.Disabled)
            {
                cost += Graph.SegCost[u] + ArcOf(prev, u);
            }
            else
            {
                cost += ov.ArcCostAt(q, prev, u);
            }

            prev = u;
        }

        // Закрытый дренажем сегмент (4.3, T4) весит +inf: остаток конечен (сейв требует конечного PlanCost), но очень большой - плановая проверка предпочтёт любой другой путь.
        return float.IsFinite(cost) ? cost : 1e6f;
    }

    private float ArcOf(int a, int b)
    {
        int arc = Graph.FindArc(a, b);
        return arc >= 0 ? Graph.ArcCost[arc] : 0f;
    }

    private int FirstLane(int dirSeg) =>
        (dirSeg & 1) == 0 ? _net.Segments[dirSeg >> 1].FirstLaneAB : _net.Segments[dirSeg >> 1].FirstLaneBA;

    // Снимает элементы стека _stk[0.._sp) в _dirs, пока там меньше target DirSeg: уровень 0 - сразу, выше - ленивое развёртывание
    // (routing.md 9.5: глубина <= Levels, одно развёртывание <= UnpackCap элементов).
    private SearchStatus Materialize(int target)
    {
        ref SimCounters c = ref _w.Counters;
        while (_nd < target && _sp > 0)
        {
            int e = _stk[--_sp];
            int u = e & OverlaySearcher.SegMask;
            int q = e >> OverlaySearcher.LevelShift;
            if (q == 0)
            {
                _dirs[_nd++] = u;
                continue;
            }

            SearchStatus st = Searcher.Unpack(_dirs[_nd - 1], u, q, _ubuf, out int ul);
            c.PlanUnpacks++;
            c.UnpackWork += Searcher.LastUnpackWork;
            WorkThisTick += Searcher.LastUnpackWork;
            if (st != SearchStatus.Found)
            {
                return SearchStatus.NoPath;
            }

            if (_sp + ul > _stk.Length)
            {
                return SearchStatus.PathTooLong;
            }

            for (int k = ul - 1; k >= 0; k--)
            {
                _stk[_sp++] = _ubuf[k];
            }
        }

        return SearchStatus.Found;
    }
}
