using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Сеть пешеходов (pedestrians.md §2): углы узлов, тротуары по сторонам сегментов, переходы через рукава и на перегонах у остановок.
/// Чистая функция сохранённых <c>Nodes/Segments/NodeArms/ArmDir/SegmentPoints/BusStops</c> и параметров <see cref="PedDesc"/>:
/// вне хэша состояния и вне сейва, в <c>Load</c> и после <c>BuildRoad</c> пересобирается тем же кодом (<see cref="Rebuild"/>).
/// Все массивы выделены в конструкторе по ёмкостям; сборка не аллоцирует и не использует тригонометрию (детерминизм).
/// Этот файл - данные и запросы; построение - <c>PedNetwork.Build.cs</c>.
/// </summary>
internal sealed partial class PedNetwork
{
    /// <summary>Номинальная скорость пешехода для стоимости рёбер, мм/с (1.3 м/с, pedestrians.md §2.3).</summary>
    public const int NominalSpeedMmPerSec = 1300;

    public readonly PedDesc Cfg;                 // с применёнными дефолтами (WithDefaults)
    public readonly float SidewalkWidthM;
    public readonly float CrosswalkWidthM;

    // ---- ёмкости и раскладка узлов графа: углы [0, ArmCap) = слот рукава в NodeArms; средние [ArmCap, ArmCap + 2·MidCap):
    //      ArmCap + 2m + side; углы-близнецы тупиков [DeadBase, DeadBase + ArmCap): DeadBase + слот ----
    public readonly int ArmCap;
    public readonly int MidCap;
    public readonly int DeadBase;
    public readonly int NodeCap;

    // ---- узлы графа ----
    public readonly Vec2[] NodePos;              // [NodeCap]
    public readonly int[] PedComp;               // [NodeCap] компонента связности (BFS по возрастанию индекса узла) или -1, если узел не используется
    public int CompCount;

    // ---- рёбра: тротуары, обходы тупиков, переходы узлов, переходы на перегоне (в таком порядке) ----
    public readonly PedEdgeData[] Edges;
    public readonly Vec2[] Points;
    public int EdgeCount;
    public int SidewalkEdgeCount;                // рёбра [0, SidewalkEdgeCount) - тротуары; далее обходы тупиков
    public int WalkwayCount;                     // тротуары + обходы тупиков = граница, с которой начинаются переходы
    public int PointCount;
    public readonly int[] SideFirstEdge;         // CSR по стороне 2·seg + side, [2·SegCap + 1]; рёбра стороны по возрастанию s
    public int SideCount;                        // 2 · число сегментов на момент сборки

    // ---- смежность узлов графа (CSR; рёбра по возрастанию индекса) ----
    public readonly int[] AdjFirst;              // [NodeCap + 1]
    public readonly int[] AdjEdge;               // [2 · EdgeCap]

    // ---- переходы через рукава (индекс перехода = слот рукава в NodeArms, -1 - перехода нет) ----
    public readonly int[] XEdge;                 // [ArmCap] индекс ребра или -1
    public readonly ulong[] XIn;                 // [ArmCap] маска LocalBit connector'ов узла с FromArm == a: полоса перехода на участке S ∈ [0, w]
    public readonly ulong[] XOut;                // [ArmCap] маска connector'ов, ведущих в выходную полосу рукава a: S ∈ [L_c - w, L_c]
    public int CrossingNodeCount;

    // ---- переходы на перегоне (m - в порядке остановок) ----
    public readonly int[] MidEdge;               // [MidCap] ребро MidCrossing
    public readonly int[] MidSegment;            // [MidCap]
    public readonly float[] MidS;                // [MidCap] s_c по оси сегмента A→B, м
    public readonly int[] StopMid;               // [BusStops.Length] номер перехода остановки или -1
    public readonly int[] MidNextFwd;            // [MidCap] следующий переход вдоль A→B (больший s) на том же сегменте или -1
    public readonly int[] MidNextBwd;            // [MidCap] следующий вдоль B→A (меньший s) или -1
    public readonly int[] LaneMidX;              // [MaxLanes] первый по ходу полосы переход на ней или -1 (только Normal-полосы)
    public int MidCount;

    // ---- производные для машин: connector -> индекс перехода (слот рукава) или -1; выделяются в ConnectorStore при включённых peds ----
    private readonly Rules.Intersections.ConnectorStore _conn;

    // ---- рабочие буферы сборки ----
    private readonly Vec2[] _sp;                 // сдвинутая полилиния стороны
    private readonly float[] _ss;                // её параметр s
    private readonly float[] _bnd;               // границы отрезков стороны
    private readonly int[] _bmid;                // номера переходов границ
    private readonly int[] _midHead;             // [SegCap] последний переход сегмента в цепочке построения или -1
    private readonly int[] _midPrev;             // [MidCap]
    private readonly int[] _stack;               // [NodeCap] стек BFS

    private readonly int _edgeCap;
    private readonly int _pointCap;

    public PedNetwork(in SimCapacities caps, NetworkStore net, Rules.Intersections.ConnectorStore conn, in PedDesc cfg)
    {
        Cfg = cfg;
        SidewalkWidthM = cfg.SidewalkWidthMm * 0.001f;
        CrosswalkWidthM = cfg.CrosswalkWidthMm * 0.001f;
        _conn = conn;

        int segCap = net.Segments.Length;
        ArmCap = net.NodeArms.Length;
        MidCap = cfg.MidblockAtStops ? net.BusStops.Length : 0;
        DeadBase = ArmCap + (2 * MidCap);
        NodeCap = DeadBase + ArmCap;

        // Тротуары: по 2 на сегмент + по одному добавочному отрезку на переход на перегоне и сторону; обходы тупиков - по одному на узел;
        // переходы узлов - по одному на слот; на перегоне - по одному на переход.
        _edgeCap = (2 * segCap) + (2 * MidCap) + net.Nodes.Length + ArmCap + MidCap;
        int sp = net.SegmentPoints.Length;
        _pointCap = (2 * sp) + (4 * segCap) + (4 * MidCap) + (3 * net.Nodes.Length) + (2 * ArmCap) + (2 * MidCap);

        NodePos = new Vec2[NodeCap];
        PedComp = new int[NodeCap];
        Edges = new PedEdgeData[_edgeCap];
        Points = new Vec2[_pointCap];
        SideFirstEdge = new int[(2 * segCap) + 1];
        AdjFirst = new int[NodeCap + 1];
        AdjEdge = new int[2 * _edgeCap];
        XEdge = new int[ArmCap];
        XIn = new ulong[ArmCap];
        XOut = new ulong[ArmCap];
        MidEdge = new int[MidCap];
        MidSegment = new int[MidCap];
        MidS = new float[MidCap];
        StopMid = new int[net.BusStops.Length];
        MidNextFwd = new int[MidCap];
        MidNextBwd = new int[MidCap];
        LaneMidX = new int[net.Lanes.Length];

        PointCum = new float[_pointCap];
        _sp = new Vec2[Math.Max(2, sp)];
        _ss = new float[Math.Max(2, sp)];
        _bnd = new float[MidCap + 2];
        _bmid = new int[MidCap + 2];
        _midHead = new int[segCap];
        _midPrev = new int[MidCap];
        _stack = new int[NodeCap];
    }

    public ReadOnlySpan<PedEdgeData> SidewalkSpan => new(Edges, 0, WalkwayCount);

    public ReadOnlySpan<PedEdgeData> CrossingSpan => new(Edges, WalkwayCount, EdgeCount - WalkwayCount);

    public ReadOnlySpan<Vec2> PointSpan => new(Points, 0, PointCount);

    /// <summary>Углы узлов по слотам рукавов (индексы [0, NodeArmCount)); углы-близнецы тупиков лежат за пределами и видны только через полилинии.</summary>
    public ReadOnlySpan<Vec2> CornerSpan => new(NodePos, 0, _armUsed);

    private int _armUsed;

    /// <summary>Узел графа - угол (слот рукава), «средний» узел перехода на перегоне или угол-близнец тупика.</summary>
    public int MidNode(int m, int side) => ArmCap + (2 * m) + side;

    public int DeadNode(int slot) => DeadBase + slot;

    /// <summary>Рёбра стороны сегмента <paramref name="side"/> (0 - справа от A→B) по возрастанию s: [first, first + count).</summary>
    public void SideEdges(int segment, int side, out int first, out int count)
    {
        int i = (2 * segment) + side;
        first = SideFirstEdge[i];
        count = SideFirstEdge[i + 1] - first;
    }

    /// <summary>Рёбра, инцидентные узлу графа: [first, first + count) в <see cref="AdjEdge"/>.</summary>
    public void Adjacent(int node, out int first, out int count)
    {
        first = AdjFirst[node];
        count = AdjFirst[node + 1] - first;
    }

    /// <summary>Другой конец ребра <paramref name="edge"/> относительно узла <paramref name="node"/>.</summary>
    public int Other(int edge, int node) => Edges[edge].A == node ? Edges[edge].B : Edges[edge].A;

    /// <summary>
    /// Диапазон полосы перехода на перегоне в координатах полосы направления <paramref name="direction"/> (0 - A→B, 1 - B→A):
    /// <c>s_c ± w/2</c>, для B→A отражённый (core-api §6.2: длина полосы == длина сегмента).
    /// </summary>
    public void MidLaneRange(int m, int direction, float segmentLength, out float s0, out float s1)
    {
        float half = CrosswalkWidthM * 0.5f;
        float c = direction == 0 ? MidS[m] : segmentLength - MidS[m];
        s0 = c - half;
        s1 = c + half;
    }

    /// <summary>Узел графа не принадлежит ни одному ребру (слот узла без рукавов, неиспользуемый средний узел).</summary>
    public bool IsUsed(int node) => PedComp[node] >= 0;

    /// <summary>Узлы <paramref name="a"/> и <paramref name="b"/> графа лежат в одной компоненте связности (T6).</summary>
    public bool Connected(int a, int b) => PedComp[a] >= 0 && PedComp[a] == PedComp[b];

    /// <summary>Нулевые поля (desc, собранный вручную) заменяются дефолтами спеки (scenario-format.md §2.4); булевы остаются как заданы.</summary>
    internal static PedDesc WithDefaults(PedDesc p, int transitHeadwayTicks)
    {
        if (p.Residents <= 0)
        {
            p.Residents = 2 * p.Count;
        }

        if (p.SpawnRampTicks <= 0)
        {
            p.SpawnRampTicks = 120 * 20;
        }

        if (p.SpeedMinMmPerTick <= 0)
        {
            p.SpeedMinMmPerTick = 56;   // round(4.0 / 3.6 * 50)
        }

        if (p.SpeedMaxMmPerTick <= 0)
        {
            p.SpeedMaxMmPerTick = 75;   // round(5.4 / 3.6 * 50)
        }

        if (p.ActivityMinTicks <= 0)
        {
            p.ActivityMinTicks = 600 * 20;
        }

        if (p.ActivityMaxTicks <= 0)
        {
            p.ActivityMaxTicks = 1800 * 20;
        }

        if (p.WalkOnlyMm <= 0)
        {
            p.WalkOnlyMm = 800_000;
        }

        if (p.MaxWalkToStopMm <= 0)
        {
            p.MaxWalkToStopMm = 500_000;
        }

        if (p.MaxWalkMm <= 0)
        {
            p.MaxWalkMm = 1_500_000;
        }

        if (p.SidewalkWidthMm <= 0)
        {
            p.SidewalkWidthMm = 3000;
        }

        if (p.CrosswalkWidthMm <= 0)
        {
            p.CrosswalkWidthMm = 3000;
        }

        if (p.WalkTicks <= 0)
        {
            p.WalkTicks = 5 * 20;
        }

        if (p.ClearSpeedMmPerTick <= 0)
        {
            p.ClearSpeedMmPerTick = 60;   // round(4.3 / 3.6 * 50)
        }

        if (p.MaxCloseTicks <= 0)
        {
            p.MaxCloseTicks = 10 * 20;
        }

        if (p.CarWindowTicks <= 0)
        {
            p.CarWindowTicks = 5 * 20;
        }

        if (p.PathfindBudgetPerTick <= 0)
        {
            p.PathfindBudgetPerTick = 8;
        }

        if (p.WorkBudgetPerTick <= 0)
        {
            p.WorkBudgetPerTick = 8000;
        }

        if (p.StuckCrossTicks <= 0)
        {
            p.StuckCrossTicks = 120 * 20;
        }

        if (p.StuckStopTicks <= 0)
        {
            p.StuckStopTicks = Math.Max(3 * transitHeadwayTicks, 900 * 20);
        }

        return p;
    }

    /// <summary>Проверка пределов секции (<c>CreateStatus.InvalidPeds</c>): ёмкости, ширины, скорости; принимает desc после <see cref="WithDefaults"/>.</summary>
    internal static bool IsValid(in PedDesc p, in SimCapacities caps)
    {
        const int maxWidthMm = 20_000;
        if (p.Count < 0 || p.Residents < p.Count || p.SpeedMinMmPerTick > p.SpeedMaxMmPerTick || p.ActivityMinTicks > p.ActivityMaxTicks ||
            p.SidewalkWidthMm < 500 || p.SidewalkWidthMm > maxWidthMm || p.CrosswalkWidthMm < 500 || p.CrosswalkWidthMm > maxWidthMm ||
            p.ArterialCrossings < ArterialCrossings.SignalOnly || p.ArterialCrossings > ArterialCrossings.All)
        {
            return false;
        }

        // max_peds >= count · 1.3 (целочисленно, с округлением вверх), max_residents >= residents.
        long need = ((long)p.Count * 13L + 9L) / 10L;
        return caps.MaxPeds >= need && caps.MaxPeds <= WorldBuilder.MaxCapacity && caps.MaxResidents >= p.Residents &&
               caps.MaxResidents <= WorldBuilder.MaxCapacity && caps.MaxPedPathSteps >= 1 && caps.MaxPedPathSteps <= 4096;
    }

    /// <summary>FNV-1a 64 по занятой части всех массивов (детерминизм сборки, равенство пересборке после Load).</summary>
    public ulong Hash()
    {
        var h = Fnv1a64.Create();
        h.Add(EdgeCount);
        h.Add(SidewalkEdgeCount);
        h.Add(WalkwayCount);
        h.Add(PointCount);
        h.Add(MidCount);
        h.Add(CompCount);
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<PedEdgeData>(Edges, 0, EdgeCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(Points, 0, PointCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<Vec2>(NodePos, 0, NodeCap)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(PedComp, 0, NodeCap)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(SideFirstEdge, 0, SideCount + 1)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(AdjFirst, 0, NodeCap + 1)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(AdjEdge, 0, 2 * EdgeCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(XEdge, 0, ArmCap)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(XIn, 0, ArmCap)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(XOut, 0, ArmCap)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(MidEdge, 0, MidCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(MidSegment, 0, MidCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(MidS, 0, MidCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(StopMid, 0, StopMid.Length)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(MidNextFwd, 0, MidCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(MidNextBwd, 0, MidCount)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(LaneMidX, 0, LaneMidX.Length)));
        if (_conn.CrossInX != null && _conn.CrossOutX != null)
        {
            h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(_conn.CrossInX, 0, _conn.Count)));
            h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(_conn.CrossOutX, 0, _conn.Count)));
        }

        return h.Value;
    }
}
