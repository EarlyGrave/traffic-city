using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

// Сеть с дырами (4.3, T2; player-tools.md §3, ADR-0023): живость слотов, проверка handle'ов, канонические значения мёртвых слотов,
// выделение блоков арен и состояние инструментов в хэше. Структурные операции (освобождение, перенос) - NetworkEdit и задачи T3+.
internal sealed partial class NetworkStore
{
    // ---- живость ----

    public bool NodeAlive(int i) => (uint)i < (uint)NodeCount && (Nodes[i].Generation & 1) == 1;

    public bool SegmentAlive(int i) => (uint)i < (uint)SegmentCount && (Segments[i].Generation & 1) == 1;

    /// <summary>Полоса живая: блок полос не освобождён (у мёртвой и запасной полосы <c>Generation == 0</c>, см. <see cref="DeadLane"/>).</summary>
    public bool LaneAlive(int i) => (uint)i < (uint)LaneCount && (Lanes[i].Generation & 1) == 1;

    public bool BusStopAlive(int i) => (uint)i < (uint)BusStopCount && (BusStops[i].Generation & 1) == 1;

    public bool BusRouteAlive(int i) => (uint)i < (uint)BusRouteCount && (BusRoutes[i].Generation & 1) == 1;

    // ---- handle'ы: индекс + поколение (0 - без проверки поколения) ----

    /// <summary>Узел <paramref name="index"/> жив и (при <paramref name="generation"/> != 0) поколение совпало.</summary>
    public bool ResolveNode(int index, int generation) => NodeAlive(index) && (generation == 0 || Nodes[index].Generation == generation);

    public bool ResolveSegment(int index, int generation) => SegmentAlive(index) && (generation == 0 || Segments[index].Generation == generation);

    public bool ResolveBusStop(int index, int generation) => BusStopAlive(index) && (generation == 0 || BusStops[index].Generation == generation);

    public bool ResolveBusRoute(int index, int generation) => BusRouteAlive(index) && (generation == 0 || BusRoutes[index].Generation == generation);

    public bool ResolveNode(in EntityRef r) => ResolveNode(r.Index, r.Generation);

    public bool ResolveSegment(in EntityRef r) => ResolveSegment(r.Index, r.Generation);

    public bool ResolveBusStop(in EntityRef r) => ResolveBusStop(r.Index, r.Generation);

    public bool ResolveBusRoute(in EntityRef r) => ResolveBusRoute(r.Index, r.Generation);

    // ---- канонические значения мёртвых слотов (ссылки -1, остальное 0, поколение чётное; сейв пишет их как есть) ----

    internal static NodeData DeadNode(int generation) => new() { FirstArm = -1, Signal = -1, Zone = -1, Generation = generation };

    internal static SegmentData DeadSegment(int generation) => new() { NodeA = -1, NodeB = -1, FirstLaneAB = -1, FirstLaneBA = -1, Generation = generation };

    /// <summary>Мёртвая и запасная полоса блока: ссылки -1, <c>Generation == 0</c> (живая - 1).</summary>
    internal static LaneData DeadLane() => new() { Segment = -1, Node = -1, FromLane = -1, ToLane = -1, Left = -1, Right = -1, ConflictGroup = -1 };

    internal static BusStopData DeadBusStop(int generation) => new() { Lane = -1, Route = -1, LastArrivalTick = -1, LastDepartTick = -1, Generation = generation };

    internal static BusRouteData DeadBusRoute(int generation) => new() { LastDispatchTick = -1, Generation = generation };

    internal static bool IsDeadLane(in LaneData l) =>
        l.Generation == 0 && l.Segment == -1 && l.Node == -1 && l.FromLane == -1 && l.ToLane == -1 && l.Left == -1 && l.Right == -1 && l.ConflictGroup == -1 &&
        l.Kind == LaneKind.Normal && l.LaneNo == 0 && l.Direction == 0 && l.Flags == 0 && l.Length == 0f && l.SpeedLimit == 0f && l.FirstPoint == 0 &&
        l.PointCount == 0 && l.FirstNext == 0 && l.NextCount == 0;

    // ---- блоки арен ----

    /// <summary>
    /// Выделение блока: у сети без правок (<see cref="ToolsTouched"/> = 0) - подряд в хвосте без запаса (раскладка генератора и <c>Create</c>, TL1);
    /// после первой правки - по лестнице (LIFO-дыра класса либо хвост с запасом до <see cref="BlockArena.CapFor"/>).
    /// </summary>
    internal bool CanAllocBlock(BlockArena a, int n) => ToolsTouched == 0 ? a.CanAllocExact(n) : a.CanAlloc(n);

    internal int AllocBlock(BlockArena a, int n) => ToolsTouched == 0 ? a.AllocExact(n) : a.Alloc(n);

    /// <summary>Блок полос; запас хвоста (ёмкость сверх <paramref name="n"/>) получает канонический вид мёртвых полос.</summary>
    internal int AllocLaneBlock(int n)
    {
        int first = AllocBlock(LaneArena, n);
        if (first >= 0)
        {
            int cap = LaneArena.CapacityOf(first, n);
            for (int i = first + n; i < first + cap; i++)
            {
                Lanes[i] = DeadLane();
            }
        }

        return first;
    }

    /// <summary>Освобождает блок полос: элементы получают вид <see cref="DeadLane"/>, производные массивы полос обнуляются.</summary>
    internal void FreeLaneBlock(int first, int n)
    {
        int cap = LaneArena.CapacityOf(first, n);
        for (int i = first; i < first + cap; i++)
        {
            Lanes[i] = DeadLane();
            LaneLength[i] = 0f;
            LaneGateNode[i] = -1;
            OutReserved[i] = 0f;
        }

        LaneArena.Free(first, n);
    }

    internal void FreePointBlock(int first, int n)
    {
        int cap = LanePointArena.CapacityOf(first, n);
        Array.Clear(LanePoints, first, cap);
        Array.Clear(LanePointS, first, cap);
        Array.Clear(LanePieceHeading, first, cap);
        LanePointArena.Free(first, n);
    }

    internal void FreeSegmentPointBlock(int first, int n)
    {
        Array.Clear(SegmentPoints, first, SegmentPointArena.CapacityOf(first, n));
        SegmentPointArena.Free(first, n);
    }

    internal void FreeArmBlock(int first, int n)
    {
        int cap = ArmArena.CapacityOf(first, n);
        Array.Clear(NodeArms, first, cap);
        Array.Clear(ArmFlags, first, cap);
        Array.Clear(ArmDir, first, cap);
        ArmArena.Free(first, n);
    }

    internal void FreeLaneNextBlock(int first, int n)
    {
        Array.Clear(LaneNext, first, LaneNextArena.CapacityOf(first, n));
        LaneNextArena.Free(first, n);
    }

    internal void FreeZoneLaneBlock(int first, int n)
    {
        Array.Clear(ZoneLanes, first, ZoneLaneArena.CapacityOf(first, n));
        ZoneLaneArena.Free(first, n);
    }

    internal void FreeBusRouteStopBlock(int first, int n)
    {
        Array.Clear(BusRouteStops, first, BusRouteStopArena.CapacityOf(first, n));
        BusRouteStopArena.Free(first, n);
    }

    // ---- пулы после плотной записи данных (генератор, загрузка) ----

    /// <summary>Генератор транзита записал <paramref name="count"/> остановок подряд: слоты заняты, поколение 1 (free-list пуст).</summary>
    internal void AdoptBusStops(int count)
    {
        BusStopPool.AdoptDense(count);
        for (int i = 0; i < count; i++)
        {
            BusStops[i].Generation = 1;
        }
    }

    internal void AdoptBusRoutes(int count)
    {
        BusRoutePool.AdoptDense(count);
        for (int i = 0; i < count; i++)
        {
            BusRoutes[i].Generation = 1;
        }
    }

    /// <summary>Узлы и сегменты, построенные подряд (<c>AddNode</c>/<c>AddSegment</c> без пула) или загруженные из сейва: поколения пулов берутся из <c>*Data.Generation</c>.</summary>
    internal void SyncPoolGenerations()
    {
        CopyGenerations(NodePool, NodeCount, i => Nodes[i].Generation);
        CopyGenerations(SegmentPool, SegmentCount, i => Segments[i].Generation);
        CopyGenerations(BusStopPool, BusStopCount, i => BusStops[i].Generation);
        CopyGenerations(BusRoutePool, BusRouteCount, i => BusRoutes[i].Generation);
    }

    private static void CopyGenerations(HandlePool pool, int count, Func<int, int> gen)
    {
        int[] g = pool.GenerationArray;
        for (int i = 0; i < count; i++)
        {
            g[i] = gen(i);
        }

        pool.RecountAlive();
    }

    // ---- хэш состояния инструментов (только при ToolsTouched = 1; player-tools.md §10) ----

    /// <summary>Пулы (free-list'ы), арены, новые поля узлов/сегментов/остановок. Порядок фиксирован; вызывает <c>StateHash</c> и <see cref="Hash"/>.</summary>
    internal void AddToolStateToHash(ref Fnv1a64 h)
    {
        AddPool(ref h, NodePool);
        AddPool(ref h, SegmentPool);
        AddPool(ref h, BusStopPool);
        AddPool(ref h, BusRoutePool);
        LaneArena.AddToHash(ref h);
        LanePointArena.AddToHash(ref h);
        SegmentPointArena.AddToHash(ref h);
        ArmArena.AddToHash(ref h);
        LaneNextArena.AddToHash(ref h);
        ZoneLaneArena.AddToHash(ref h);
        BusRouteStopArena.AddToHash(ref h);
        int nodes = NodeCount;
        for (int i = 0; i < nodes; i++)
        {
            h.Add(Nodes[i].Generation);
            h.Add(Nodes[i].Flags);
            h.Add(Nodes[i].TurnBan);
        }

        int segs = SegmentCount;
        for (int i = 0; i < segs; i++)
        {
            h.Add(Segments[i].Generation);
            h.Add(Segments[i].Flags);
            h.Add(Segments[i].Cx);
            h.Add(Segments[i].Cz);
        }

        int stops = BusStopCount;
        for (int i = 0; i < stops; i++)
        {
            h.Add(BusStops[i].Generation);
        }

        int routes = BusRouteCount;
        for (int i = 0; i < routes; i++)
        {
            h.Add(BusRoutes[i].Generation);
        }

        int lanes = LaneCount;
        for (int i = 0; i < lanes; i++)
        {
            h.Add(Lanes[i].Flags);
        }
    }

    private static void AddPool(ref Fnv1a64 h, HandlePool p)
    {
        h.Add(p.HighWater);
        h.Add(p.FreeCount);
        for (int k = 0; k < p.FreeCount; k++)
        {
            h.Add(p.FreeListArray[k]);
        }
    }
}
