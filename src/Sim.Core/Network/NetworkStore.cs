using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Хранилище сети (core-api §6.1, §6.2). Все массивы выделяются один раз по <see cref="SimCapacities"/>;
/// после построения аллокаций нет. Данные — AoS-структуры <c>*Data</c> и плоские массивы геометрии.
/// </summary>
internal sealed partial class NetworkStore
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

    // ---- сеть с дырами (4.3, T2; player-tools.md §3, ADR-0023) ----

    /// <summary>Пулы слотов с поколениями и LIFO-free-list (как у машин). Поколение слота = <c>Generation</c> в <c>*Data</c> (нечётное = занят).</summary>
    internal readonly HandlePool NodePool, SegmentPool, BusStopPool, BusRoutePool;

    /// <summary>Арены блоков (диапазоны): <see cref="Lanes"/> (блоки полос сегмента и connector'ов узла), точки, рукава, <see cref="LaneNext"/>, <see cref="ZoneLanes"/>, <see cref="BusRouteStops"/>.</summary>
    internal readonly BlockArena LaneArena, LanePointArena, SegmentPointArena, ArmArena, LaneNextArena, ZoneLaneArena, BusRouteStopArena;

    /// <summary>
    /// 1 после первой структурной правки (освобождение слота или блока): с этого момента раскладка, free-list'ы и новые поля входят в хэш и сейв
    /// (player-tools.md §10). У сети без правок всегда 0 - хэш и поведение прежние (TL1). Поля, не покрытые хэшем, пока 0, меняют только после установки флага.
    /// </summary>
    public byte ToolsTouched { get; internal set; }

    /// <summary>Каталог профилей дорог (ADR-0030); null - режим legacy. Задаётся один раз в <c>World</c> до построения сети; в тике только читается.</summary>
    internal RoadProfileStore? Profiles { get; set; }

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
        NodePool = new HandlePool(nodes);
        SegmentPool = new HandlePool(segs);
        BusStopPool = new HandlePool(BusStops.Length);
        BusRoutePool = new HandlePool(BusRoutes.Length);
        LaneArena = new BlockArena(lanes);
        LanePointArena = new BlockArena(pts);
        SegmentPointArena = new BlockArena(SegmentPoints.Length);
        ArmArena = new BlockArena(NodeArms.Length);
        LaneNextArena = new BlockArena(LaneNext.Length);
        ZoneLaneArena = new BlockArena(ZoneLanes.Length);
        BusRouteStopArena = new BlockArena(BusRouteStops.Length);
    }

    /// <summary>Верхняя граница слотов узлов (<c>HighWater</c> пула): обходы «по всем слотам» идут до неё и пропускают мёртвые (<see cref="NodeAlive"/>).</summary>
    public int NodeCount { get => NodePool.HighWater; internal set => NodePool.RestoreCounts(value, 0); }

    public int SegmentCount { get => SegmentPool.HighWater; internal set => SegmentPool.RestoreCounts(value, 0); }

    /// <summary>Занятая часть массива полос (хвост арены): живые блоки и дыры.</summary>
    public int LaneCount { get => LaneArena.Tail; internal set => LaneArena.Tail = value; }

    public int LanePointCount { get => LanePointArena.Tail; internal set => LanePointArena.Tail = value; }

    public int SegmentPointCount { get => SegmentPointArena.Tail; internal set => SegmentPointArena.Tail = value; }

    public int NodeArmCount { get => ArmArena.Tail; internal set => ArmArena.Tail = value; }

    public int LaneNextCount { get => LaneNextArena.Tail; internal set => LaneNextArena.Tail = value; }

    public int ZoneCount { get; internal set; }

    public int ZoneLaneCount { get => ZoneLaneArena.Tail; internal set => ZoneLaneArena.Tail = value; }

    public int BusStopCount { get => BusStopPool.HighWater; internal set => BusStopPool.RestoreCounts(value, 0); }

    public int BusRouteCount { get => BusRoutePool.HighWater; internal set => BusRoutePool.RestoreCounts(value, 0); }

    public int BusRouteStopCount { get => BusRouteStopArena.Tail; internal set => BusRouteStopArena.Tail = value; }

    /// <summary>Живых узлов / сегментов / остановок / маршрутов (<c>HighWater</c> минус дыры).</summary>
    public int AliveNodes => NodePool.AliveCount;

    public int AliveSegments => SegmentPool.AliveCount;

    public int AliveBusStops => BusStopPool.AliveCount;

    public int AliveBusRoutes => BusRoutePool.AliveCount;

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

    /// <summary>
    /// Загрузка сейва: производные массивы полос (<c>LaneLength</c>, <c>LanePieceHeading</c>, <c>LaneGateNode</c>) по сохранённым полосам, точкам и узлам.
    /// Полосы с диапазоном точек вне <c>LanePointCount</c> пропускаются (их отклоняет проверка загрузчика раньше).
    /// </summary>
    internal void RebuildLaneDerived()
    {
        Array.Fill(LaneGateNode, -1);
        for (int l = 0; l < LaneCount; l++)
        {
            if (!LaneAlive(l))
            {
                LaneLength[l] = 0f;
                continue;
            }

            LaneLength[l] = Lanes[l].Length;
            FillPieceHeadings(Lanes[l].FirstPoint, Lanes[l].PointCount);
        }

        for (int n = 0; n < NodeCount; n++)
        {
            if (Nodes[n].FirstArm >= 0)
            {
                RefreshLaneGateNode(n);
            }
        }
    }

    /// <summary>++ при каждом изменении сети (клиент перестраивает меши по смене).</summary>
    public int Version { get; internal set; }

    /// <summary>Сеть пешеходов (ADR-0020); null - секции peds нет. Производное: строится в <c>WorldBuilder</c>, пересобирается после <c>BuildRoad</c> и загрузки.</summary>
    internal Sim.Core.Peds.PedNetwork? PedNet { get; set; }

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
        new ReadOnlySpan<int>(BusRouteStops, 0, BusRouteStopCount),
        PedNet != null ? PedNet.SidewalkSpan : default,
        PedNet != null ? PedNet.CrossingSpan : default,
        PedNet != null ? PedNet.PointSpan : default,
        PedNet != null ? PedNet.CornerSpan : default,
        PedNet?.SidewalkWidthM ?? 0f,
        PedNet?.CrosswalkWidthM ?? 0f);

    /// <summary>Раздел профилей хэша (только режим профилей, ADR-0030): каталог, <c>Segment.Profile</c> и <c>Lane.Width</c> всех слотов.</summary>
    private void AddProfileStateToHash(ref Fnv1a64 h)
    {
        h.Add(0x46_4F_52_50);   // метка раздела
        Profiles!.AddToHash(ref h);
        for (int i = 0; i < SegmentCount; i++)
        {
            h.Add(Segments[i].Profile);
        }

        for (int i = 0; i < LaneCount; i++)
        {
            h.Add(Lanes[i].Width);
        }
    }

    /// <summary>FNV-1a 64 по занятой части всех массивов сети (детерминизм построения).</summary>
    public ulong Hash()
    {
        var h = Fnv1a64.Create();
        h.Add(NodeCount);
        h.Add(SegmentCount);
        h.Add(LaneCount);
        h.Add(LanePointCount);
        // Узлы и сегменты - полями в раскладке до 4.3 (T2): новые поля (Flags, TurnBan, Cx, Cz, Generation остановок) в этот хэш не входят, пока ToolsTouched = 0.
        for (int i = 0; i < NodeCount; i++)
        {
            ref readonly NodeData n = ref Nodes[i];
            h.Add(n.Position.X);
            h.Add(n.Position.Z);
            h.Add(n.Elevation);
            h.Add(n.Radius);
            h.Add((int)n.Control);
            h.Add(n.FirstArm);
            h.Add(n.ArmCount);
            h.Add(n.FirstConnector);
            h.Add(n.ConnectorCount);
            h.Add(n.Signal);
            h.Add(n.Zone);
            h.Add(n.Generation);
        }

        for (int i = 0; i < SegmentCount; i++)
        {
            ref readonly SegmentData sg = ref Segments[i];
            h.Add(sg.NodeA);
            h.Add(sg.NodeB);
            h.Add(sg.Length);
            h.Add(sg.SpeedLimit);
            h.Add((byte)sg.Kind);
            h.Add(sg.LanesAB);
            h.Add(sg.LanesBA);
            h.Add(sg.Flags);
            h.Add(sg.FirstLaneAB);
            h.Add(sg.FirstLaneBA);
            h.Add(sg.FirstPoint);
            h.Add(sg.PointCount);
            h.Add(sg.Generation);
        }

        // Полосы - полями в раскладке до ADR-0030 (60 байт): новое поле Width в этот хэш не входит, пока нет каталога профилей.
        for (int i = 0; i < LaneCount; i++)
        {
            ref readonly LaneData l = ref Lanes[i];
            h.Add((byte)l.Kind);
            h.Add(l.LaneNo);
            h.Add(l.Direction);
            h.Add(l.Flags);
            h.Add(l.Segment);
            h.Add(l.Node);
            h.Add(l.FromLane);
            h.Add(l.ToLane);
            h.Add(l.Length);
            h.Add(l.SpeedLimit);
            h.Add(l.FirstPoint);
            h.Add(l.PointCount);
            h.Add(l.Left);
            h.Add(l.Right);
            h.Add(l.FirstNext);
            h.Add(l.NextCount);
            h.Add(l.ConflictGroup);
            h.Add(l.Generation);
        }

        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(NodeArms, 0, NodeArmCount)));
        h.Add(new ReadOnlySpan<byte>(ArmFlags, 0, NodeArmCount));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(ArmDir, 0, NodeArmCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(LaneNext, 0, LaneNextCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(SegmentPoints, 0, SegmentPointCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(LanePoints, 0, LanePointCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(LanePointS, 0, LanePointCount)));
        if (ToolsTouched != 0)
        {
            AddToolStateToHash(ref h);
        }

        if (Profiles != null)
        {
            AddProfileStateToHash(ref h);
        }

        return h.Value;
    }
}
