using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Пакет параллельных запросов фазы 3 (routing-parallel.md 3.2-3.4, ADR-0032): «спекулятивный пакет + последовательный повтор».
/// (A) <see cref="Prepare"/> собирает кандидатов в порядке очередей (те же фильтры, что в цикле фазы, состояние не меняется),
/// (B) считает их <see cref="RouteCompute.Run"/> на воркерах пула (чанк 1, порядок раздачи - по убыванию оценки цены),
/// (C) нынешний последовательный цикл фазы вызывает <see cref="Take"/> вместо <see cref="Router.Build"/>: результат кандидата с тем же ключом
/// запроса фиксируется <see cref="Router.Commit(in QueryResult, int[], int, RouteStep[], int, long, out RouteHandle)"/>, иначе запрос считается на главном потоке.
/// Результат запроса - функция только от ключа, графа и оверлея, поэтому хэш мира не зависит ни от числа потоков, ни от размера пакета.
/// Всё состояние - производное (вне хэша и сейва); память выделена в конструкторе.
/// </summary>
internal sealed class RouteBatch : IRangeJob
{
    /// <summary>Жёсткая ёмкость пакета (массивы выделены под неё); <see cref="MaxCandidates"/> ограничивает сверху.</summary>
    public const int Capacity = 256;

    private const float WorkSlack = 1.25f;
    private const float AvgGain = 1f / 16f;

    private readonly Router _rt;
    private readonly World _w;
    private readonly RouteQuery[] _keys = new RouteQuery[Capacity];
    private readonly QueryResult[] _res = new QueryResult[Capacity];
    private readonly RouteStep[] _steps;
    private readonly int[] _order = new int[Capacity];
    private readonly float[] _est = new float[Capacity];
    private readonly int _stride;
    private RouteScratch[] _scratch = [];
    private int _count;
    private int _cursor;
    private bool _truncated;
    private long _totalWork;
    private long _usedWork;
    private int _usedCount;
    private float _avgWork;
    private int _maxCandidates = Capacity;

    public RouteBatch(Router rt, World w)
    {
        _rt = rt;
        _w = w;
        _stride = (2 * ((w.Routes.WindowSteps + 1) / 2)) + 2;
        _steps = new RouteStep[Capacity * _stride];
    }

    /// <summary>Пакет собран на этот тик и (C) может брать из него результаты.</summary>
    public bool Active { get; private set; }

    /// <summary>Предел числа кандидатов тика, 0 = пакет выключен (каждый запрос считается в цикле фазы - прежний код). Тестовый хук; на результат не влияет.</summary>
    public int MaxCandidates
    {
        get => _maxCandidates;
        set => _maxCandidates = Math.Clamp(value, 0, Capacity);
    }

    /// <summary>Тестовый хук: собирать пакет и без пула (счёт на главном потоке). Без хука пакет при <c>Pool == null</c> выключен.</summary>
    public bool SingleThread { get; set; }

    // ---- диагностика (вне хэша и сейва; bench JSON routing.batch_*) ----
    public long Batches { get; private set; }
    public long Candidates { get; private set; }
    public long Committed { get; private set; }
    public long Discarded { get; private set; }
    public long Inline { get; private set; }
    public long Mismatch { get; private set; }
    public long ComputedWork { get; private set; }
    public long DiscardedWork { get; private set; }
    public long InlineWork { get; private set; }

    /// <summary>Скользящее среднее работы зафиксированного запроса (релаксации), 0 пока нет данных.</summary>
    public float AvgWork => _avgWork;

    /// <summary>Учёт работы запроса фазы 3 для предела <c>Kwork</c> (вызывает <see cref="Router.BuildBatched"/>).</summary>
    public void NoteWork(int work)
    {
        if (work > 0)
        {
            _avgWork = _avgWork <= 0f ? work : _avgWork + ((work - _avgWork) * AvgGain);
        }
    }

    /// <summary>
    /// (A)+(B): собирает и считает пакет. false = пакета нет (выключен, пустой, граф отстаёт от сети) - фаза идёт прежним кодом.
    /// Вызывать после дозаполнений окон и <c>EnqueueTimed</c>, до циклов (C).
    /// </summary>
    public bool Prepare(int maxQueries, int maxWork)
    {
        Active = false;
        WorkerPool? pool = _w.Pool;
        if (_maxCandidates <= 0 || (pool == null && !SingleThread))
        {
            return false;
        }

        // Граф отстаёт от сети (правка в этом тике): первый Build синхронизирует его уже после RemainingCost плановых запросов, этот порядок
        // сохраняем: такой тик идёт без пакета (редкий).
        if (_rt.Graph.BuiltVersion != _w.Net.Version)
        {
            return false;
        }

        int workers = pool?.Workers ?? 1;
        int limit = Limit(maxQueries, maxWork, workers);
        _count = 0;
        _cursor = 0;
        _truncated = false;
        _totalWork = 0;
        _usedWork = 0;
        _usedCount = 0;
        if (limit > 0)
        {
            CollectRerouting(maxQueries, maxWork, limit);
            CollectTrips(limit);
        }

        if (_count == 0)
        {
            return false;
        }

        Order();
        _rt.Scratch[0].PlanArena ??= new int[RouteScratch.PlanArenaCapacity];   // только тестовый хук SingleThread: с пулом арена выделена в EnsureWorkers
        RouteScratch[] sc = _rt.Scratch;
        for (int k = 0; k < Math.Min(workers, sc.Length); k++)
        {
            sc[k].ArenaUsed = 0;
        }

        _scratch = sc;
        if (pool != null)
        {
            pool.Run(this, _count, 1);
        }
        else
        {
            RunRange(0, 0, _count);
        }

        for (int j = 0; j < _count; j++)
        {
            _totalWork += _res[j].QueryWork + _res[j].UnpackWork;
        }

        Batches++;
        Candidates += _count;
        ComputedWork += _totalWork;
        Active = true;
        return true;
    }

    /// <summary>
    /// (C): готовый результат кандидата с тем же ключом (первый подходящий после курсора; пропущенные - отброшены) либо счёт на главном потоке.
    /// Эквивалент <see cref="Router.Build"/> для того же запроса в этой точке тика.
    /// </summary>
    public BuildStatus Take(in RouteQuery q, long tick, out RouteHandle handle)
    {
        int j = _cursor;
        while (j < _count && !SameKey(in _keys[j], in q))
        {
            j++;
        }

        if (j < _count && _res[j].Stage != QueryStage.Overflow)
        {
            _cursor = j + 1;
            _usedCount++;
            ref QueryResult r = ref _res[j];
            _usedWork += r.QueryWork + r.UnpackWork;
            RouteScratch src = _scratch[r.Worker];
            return _rt.Commit(in r, src.PlanArena ?? Array.Empty<int>(), r.PlanOff, _steps, j * _stride, tick, out handle);
        }

        if (j >= _count)
        {
            if (_cursor < _count && !_truncated)
            {
                Mismatch++;   // пакет собран до конца очередей, а запроса в нём нет: фильтры (A) разошлись с (C)
            }
        }
        else
        {
            _cursor = j + 1;
        }

        Inline++;
        int w0 = _rt.WorkThisTick;
        BuildStatus st = _rt.Build(q.StartLane, q.StartS, q.DestLane, q.DestS, tick, out handle);
        InlineWork += _rt.WorkThisTick - w0;
        return st;
    }

    /// <summary>Конец фазы: итоги пакета (отброшенное) и закрытие.</summary>
    public void Finish()
    {
        if (!Active)
        {
            return;
        }

        Active = false;
        Committed += _usedCount;
        Discarded += _count - _usedCount;
        DiscardedWork += _totalWork - _usedWork;
    }

    // (B): элемент = позиция в порядке раздачи; результат и шаги пишутся в ячейки кандидата, стек - в арену воркера.
    public void RunRange(int worker, int begin, int end)
    {
        RouteScratch s = _scratch[worker];
        NetworkStore net = _w.Net;
        RouteGraph g = _rt.Graph;
        Overlay? ov = _rt.OverlayOrNull;
        for (int p = begin; p < end; p++)
        {
            int j = _order[p];
            ref QueryResult r = ref _res[j];
            RouteCompute.Run(in _keys[j], net, g, ov, s, ref r);
            if (r.Stage != QueryStage.Found)
            {
                continue;
            }

            int[]? arena = s.PlanArena;
            if (arena == null || s.ArenaUsed + r.StackLen > arena.Length)
            {
                r.Stage = QueryStage.Overflow;
                continue;
            }

            Array.Copy(s.Steps, 0, _steps, j * _stride, r.StepCount);
            Array.Copy(s.Stk, 0, arena, s.ArenaUsed, r.StackLen);
            r.Worker = worker;
            r.PlanOff = s.ArenaUsed;
            s.ArenaUsed += r.StackLen;
        }
    }

    private static bool SameKey(in RouteQuery a, in RouteQuery b) =>
        a.StartLane == b.StartLane && a.DestLane == b.DestLane &&
        BitConverter.SingleToInt32Bits(a.StartS) == BitConverter.SingleToInt32Bits(b.StartS) &&
        BitConverter.SingleToInt32Bits(a.DestS) == BitConverter.SingleToInt32Bits(b.DestS);

    // K = min(MaxCandidates, MaxQueries, Kwork); Kwork = ceil(1.25 * MaxWork / avgWork) + workers. Влияет только на скорость.
    private int Limit(int maxQueries, int maxWork, int workers)
    {
        int k = _maxCandidates;
        if (maxQueries != int.MaxValue)
        {
            k = Math.Min(k, maxQueries);
        }

        if (maxWork != int.MaxValue && _avgWork > 0f)
        {
            float kw = MathF.Ceiling(WorkSlack * maxWork / _avgWork) + workers;
            if (kw < k)
            {
                k = (int)kw;
            }
        }

        return k;
    }

    private bool Add(int lane, float s, int destLane, float destS)
    {
        if (_count >= Math.Min(_maxCandidates, Capacity))
        {
            _truncated = true;
            return false;
        }

        _keys[_count] = new RouteQuery(lane, s, destLane, destS);
        _count++;
        return true;
    }

    // (A) Rerouting: фильтры RoutePhase.RerouteVehicles до вызова Build; Timed - не больше timedCap.
    private void CollectRerouting(int maxQueries, int maxWork, int limit)
    {
        World w = _w;
        RingBuffer<RerouteEntry> q = w.Rerouting;
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int timedCap = maxQueries == int.MaxValue ? int.MaxValue : Math.Max(1, maxQueries / RoutePhase.TimedBudgetDivisor);
        // Доля работы плановых (timedWorkCap) при сборе неизвестна: оцениваем число плановых запросов по средней работе, иначе очередь плановых
        // (они стоят перед поездками) забила бы пакет, а повтор отложил бы их в хвост по работе - счёт впустую и поездки считаются без пакета.
        int timedEst = int.MaxValue;
        if (maxWork != int.MaxValue && _avgWork > 0f)
        {
            int timedWorkCap = Math.Max(1, maxWork / RoutePhase.TimedBudgetDivisor);
            timedEst = (int)MathF.Ceiling(WorkSlack * timedWorkCap / _avgWork) + 1;
        }

        int timed = 0;
        int n = q.Count;
        for (int k = 0; k < n; k++)
        {
            ref readonly RerouteEntry e = ref q.At(k);
            int i = e.Index;
            if (e.Kind == RouteNav.KindTimed)
            {
                if (!v.Pool.IsAlive(i, e.Generation) || v.Route[i] < 0 || v.Lane[i] < 0 ||
                    net.Lanes[v.Lane[i]].Kind != LaneKind.Normal || (v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0 ||
                    w.Tick - e.ReadyTick > RoutePhase.TimedExpiryTicks)
                {
                    continue;
                }

                if (timed >= timedCap)
                {
                    continue;
                }

                if (timed >= timedEst)
                {
                    _truncated = true;   // оценка по работе: пакет неполный, запрос вне пакета - не расхождение фильтров
                    continue;
                }

                int lane = v.Lane[i];
                if (net.Segments[net.Lanes[lane].Segment].Length - v.S[i] < RoutePhase.TimedMinSegmentRemaining)
                {
                    continue;
                }

                int cursor = v.RouteCursor[i];
                if (cursor < 0 || cursor >= w.Routes.WinCount[v.Route[i]])
                {
                    continue;
                }

                if (_count >= limit || !Add(lane, v.S[i], v.DestLane[i], v.DestS[i]))
                {
                    _truncated = true;
                    return;
                }

                timed++;
                _est[_count - 1] = Estimate(lane, v.DestLane[i]);
                continue;
            }

            if (!v.Pool.IsAlive(i, e.Generation) || v.Route[i] >= 0 || v.Lane[i] < 0 || e.ReadyTick > w.Tick)
            {
                continue;
            }

            int ml = v.Lane[i];
            float ms = v.S[i];
            if (net.Lanes[ml].Kind == LaneKind.Connector)
            {
                ml = net.Lanes[ml].ToLane;
                ms = 0f;
            }

            if (ml < 0)
            {
                continue;
            }

            if (_count >= limit || !Add(ml, ms, v.DestLane[i], v.DestS[i]))
            {
                _truncated = true;
                return;
            }

            _est[_count - 1] = Estimate(ml, v.DestLane[i]);
        }
    }

    // (A) PendingTrips: фильтры RoutePhase.RouteTrips; точка прибытия - на копии RngRouting (реальный двигает только (C)).
    private void CollectTrips(int limit)
    {
        World w = _w;
        PendingTrips trips = w.Trips;
        NetworkStore net = w.Net;
        Pcg32 rng = w.RngRouting;
        int k = 0;
        int seen = 0;
        int unrouted = trips.Unrouted;
        while (seen < unrouted && k < trips.Count)
        {
            PendingTrip t = trips.At(k);
            k++;
            if (t.HasRoute)
            {
                continue;
            }

            seen++;
            if (t.DestLane < 0 && !Destinations.Pick(net, t.ToZone, ref rng, out t.DestLane, out t.DestS))
            {
                continue;
            }

            if (t.FromLane < 0)
            {
                int fromZone = t.FromZone;
                if ((uint)fromZone >= (uint)net.ZoneCount || net.OpenZoneLaneCount(fromZone) <= 0)
                {
                    continue;
                }

                t.FromLane = net.OpenZoneLane(fromZone, (int)((w.Tick + t.ToZone) % net.OpenZoneLaneCount(fromZone)));
            }

            if (_count >= limit || !Add(t.FromLane, t.FromS, t.DestLane, t.DestS))
            {
                _truncated = true;
                return;
            }

            _est[_count - 1] = Estimate(t.FromLane, t.DestLane);
        }
    }

    // Оценка цены запроса для порядка раздачи: квадрат расстояния между концами DirSeg старта и цели.
    private float Estimate(int startLane, int destLane)
    {
        NetworkStore net = _w.Net;
        if ((uint)startLane >= (uint)net.LaneCount || (uint)destLane >= (uint)net.LaneCount ||
            net.Lanes[startLane].Segment < 0 || net.Lanes[destLane].Segment < 0)
        {
            return 0f;
        }

        RouteGraph g = _rt.Graph;
        Vec2 a = g.EndPos[RouteGraph.DirSegOfLane(net, startLane)];
        Vec2 b = g.EndPos[RouteGraph.DirSegOfLane(net, destLane)];
        float dx = a.X - b.X;
        float dy = a.Z - b.Z;
        return (dx * dx) + (dy * dy);
    }

    // LPT: по убыванию оценки, ничьи - по номеру кандидата (вставками: пакет мал). Порядок раздачи на результат не влияет.
    private void Order()
    {
        for (int j = 0; j < _count; j++)
        {
            int p = j;
            while (p > 0 && _est[_order[p - 1]] < _est[j])
            {
                _order[p] = _order[p - 1];
                p--;
            }

            _order[p] = j;
        }
    }
}
