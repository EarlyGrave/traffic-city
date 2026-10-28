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
/// шагов в <see cref="RouteStore"/>; дозаполнение окна ленивым развёртыванием плана; остаток стоимости для проверки дрейфа. Построение разделено
/// на чистый счёт (<see cref="RouteCompute.Run"/>, без <c>World</c>, в <see cref="RouteScratch"/>) и последовательную фиксацию
/// (<see cref="Commit"/>: счётчики, слот, окно) - routing-parallel.md 3.1, ADR-0032. Все буферы выделены заранее, методы не аллоцируют.
/// Метрики пишутся прямо в <c>World.Counters</c> (запросы идут и из фаз 2b/8b - автобусы).
/// </summary>
internal sealed class Router
{
    /// <summary>Машина с не более чем столько DirSeg впереди в окне и непустым планом ставится в очередь дозаполнения (routing.md 9.5).</summary>
    public const int RefillAhead = 3;

    private readonly World _w;
    private readonly NetworkStore _net;
    private readonly RouteStore _store;
    private readonly int _maxDirs;
    private readonly RouteScratch _s0;
    private readonly RouteStep[] _newWin;
    private readonly Overlay? _overlay;
    private readonly int _queryWorkCap;

    public Router(World w, RouteGraph graph, Overlay? overlay, int queryWorkCap)
    {
        _w = w;
        _net = w.Net;
        _store = w.Routes;
        Graph = graph;
        _overlay = overlay;
        _queryWorkCap = queryWorkCap;
        _s0 = new RouteScratch(_net, graph, overlay, _store.WindowSteps, queryWorkCap, planArena: false);
        Scratch = [_s0];
        _maxDirs = _s0.MaxDirs;
        _newWin = new RouteStep[_store.WindowSteps + 2];
        Batch = new RouteBatch(this, w);
        EnsureWorkers(w.Pool?.Workers ?? 1);
    }

    public RouteGraph Graph { get; }

    /// <summary>Пакет параллельных запросов фазы 3 (routing-parallel.md 3.2-3.4, ADR-0032).</summary>
    public RouteBatch Batch { get; }

    internal NetworkStore Net => _net;

    internal Overlay? OverlayOrNull => _overlay;

    /// <summary>Поисковик главного потока (scratch 0).</summary>
    public OverlaySearcher Searcher => _s0.Searcher;

    /// <summary>Рабочая память воркеров запросов; [0] - главный поток (<see cref="Build"/>, <see cref="Refill"/>).</summary>
    public RouteScratch[] Scratch { get; private set; }

    /// <summary>Работа запросов и развёртываний с начала бюджетного отсчёта (релаксации); сбрасывает фаза 3 после дозаполнений.</summary>
    public int WorkThisTick { get; set; }

    /// <summary>
    /// Доводит число scratch до <paramref name="workers"/> (при создании и из <c>World.StartWorkers</c>, вне горячего пути);
    /// существующие остаются, [0] - буферы главного потока.
    /// </summary>
    public void EnsureWorkers(int workers)
    {
        if (workers > 1)
        {
            _s0.PlanArena ??= new int[RouteScratch.PlanArenaCapacity];
        }

        int have = Scratch.Length;
        if (workers <= have)
        {
            return;
        }

        var all = new RouteScratch[workers];
        Array.Copy(Scratch, all, have);
        for (int k = have; k < workers; k++)
        {
            all[k] = new RouteScratch(_net, Graph, _overlay, _store.WindowSteps, _queryWorkCap, planArena: true);
        }

        Scratch = all;
    }

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
    /// Счёт на scratch 0 + <see cref="Commit"/>.
    /// </summary>
    public BuildStatus Build(int startLane, float startS, int destLane, float destS, long tick, out RouteHandle handle)
    {
        handle = RouteHandle.Null;
        SyncGraph();
        if (_store.Pool.AliveCount >= _store.Pool.Capacity)
        {
            return BuildStatus.Deferred;   // как раньше: пул полон - запрос не считается и счётчики не растут
        }

        var q = new RouteQuery(startLane, startS, destLane, destS);
        var r = default(QueryResult);
        RouteCompute.Run(in q, _net, Graph, _overlay, _s0, ref r);
        return Commit(in q, in r, _s0, tick, out handle);
    }

    /// <summary>
    /// Последовательная фиксация результата <see cref="RouteCompute.Run"/> (routing-parallel.md 3.1): счётчики и бюджет, слот <see cref="RouteStore"/>,
    /// план, окно. <paramref name="src"/> - scratch, на котором шёл счёт (шаги и стек плана ещё не перезаписаны).
    /// </summary>
    public BuildStatus Commit(in RouteQuery q, in QueryResult r, RouteScratch src, long tick, out RouteHandle handle) =>
        Commit(in r, src.Stk, 0, src.Steps, 0, tick, out handle);

    /// <summary>То же, но стек плана и шаги окна лежат в произвольных массивах (результат пакета: арена воркера и массив шагов кандидатов).</summary>
    public BuildStatus Commit(in QueryResult r, int[] stk, int stkOff, RouteStep[] steps, int stepOff, long tick, out RouteHandle handle)
    {
        handle = RouteHandle.Null;
        SyncGraph();
        if (_store.Pool.AliveCount >= _store.Pool.Capacity)
        {
            return BuildStatus.Deferred;
        }

        ref SimCounters c = ref _w.Counters;
        c.PathfindQueries++;
        c.PathfindWork += r.QueryWork;
        c.PathfindExpandedNodes += r.Expanded;
        c.PlanUnpacks += r.Unpacks;
        c.UnpackWork += r.UnpackWork;
        WorkThisTick += r.QueryWork + r.UnpackWork;
        if (r.Stage != QueryStage.Found)
        {
            c.RouteFailed++;
            return BuildStatus.NoPath;
        }

        if (!_store.TryAllocate(out handle))
        {
            return BuildStatus.Deferred;
        }

        int slot = handle.Index;
        if (!_store.SetPlan(slot, new ReadOnlySpan<int>(stk, stkOff, r.StackLen)))
        {
            _store.Free(handle);
            handle = RouteHandle.Null;
            return BuildStatus.Deferred;
        }

        Array.Copy(steps, stepOff, _store.Window, slot * _store.WindowSteps, r.StepCount);
        _store.WinCount[slot] = (byte)r.StepCount;
        _store.PlanCost[slot] = r.Cost;
        _store.PlanTick[slot] = tick;
        return BuildStatus.Ok;
    }

    /// <summary>
    /// <see cref="Build"/> фазы 3: при активном пакете берёт готовый результат кандидата с тем же ключом (<see cref="RouteBatch.Take"/>), иначе считает
    /// сам. Результат и состояние после вызова те же, что у <see cref="Build"/>.
    /// </summary>
    public BuildStatus BuildBatched(int startLane, float startS, int destLane, float destS, long tick, out RouteHandle handle)
    {
        int w0 = WorkThisTick;
        BuildStatus st = Batch.Active
            ? Batch.Take(new RouteQuery(startLane, startS, destLane, destS), tick, out handle)
            : Build(startLane, startS, destLane, destS, tick, out handle);
        Batch.NoteWork(WorkThisTick - w0);
        return st;
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
        RouteScratch s = _s0;
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

        s.Nd = 0;
        for (int k = first; k < wc && s.Nd < _maxDirs; k += 2)
        {
            s.Dirs[s.Nd++] = RouteGraph.DirSegOfLane(_net, win[b + k].Lane);
        }

        if (s.Nd == 0)
        {
            return false;
        }

        int capDirs = (width - prefix + 1) / 2;
        if (s.Nd >= capDirs)
        {
            return true; // окно уже полное
        }

        s.Sp = pc;
        Array.Copy(_store.PlanArena, _store.PlanFirst[r], s.Stk, 0, pc);
        int unpacks = 0;
        int unpackWork = 0;
        SearchStatus mst = RouteCompute.Materialize(s, capDirs, ref unpacks, ref unpackWork);
        _w.Counters.PlanUnpacks += unpacks;
        _w.Counters.UnpackWork += unpackWork;
        WorkThisTick += unpackWork;
        if (mst != SearchStatus.Found)
        {
            return false;
        }

        int layoutDest = s.Sp == 0 ? v.DestLane[i] : RouteCompute.FirstLane(_net, s.Dirs[s.Nd - 1]);
        int n = RouteLayout.LayoutLanes(_net, Graph, new ReadOnlySpan<int>(s.Dirs, 0, s.Nd), s.Nd, tieLane, layoutDest, s.Steps);
        if (n < 0 || prefix + n > width)
        {
            return false;
        }

        for (int k = 0; k < prefix; k++)
        {
            _newWin[k] = win[b + cur + k];
        }

        Array.Copy(s.Steps, 0, _newWin, prefix, n);
        if (!_store.SetPlan(r, new ReadOnlySpan<int>(s.Stk, 0, s.Sp)))
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

}
