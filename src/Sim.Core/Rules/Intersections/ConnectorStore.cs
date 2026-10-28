using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Rules.Intersections;

/// <summary>Вид конфликта двух connector'ов (intersections.md §3). Diverge (общая входная полоса) конфликтом не считается.</summary>
internal enum ConflictKind : byte
{
    Cross = 0,
    Merge = 1,
}

/// <summary>Статическое отношение приоритета «я относительно другого» (intersections.md §3.1).</summary>
internal enum ConflictRel : byte
{
    Yield = 0,      // я уступаю
    Priority = 1,   // у меня приоритет
    Tie = 2,        // ничья (динамика, §5.4)
}

/// <summary>Запись конфликта на connector'е (8 байт; intersections.md §3).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2, Size = 8)]
internal struct ConflictEntry
{
    public byte OtherLocal;   // LocalBit конфликтующего connector'а (индекс = Node.FirstConnector + OtherLocal)
    public byte Flags;        // бит0..1: Kind (ConflictKind); бит2..3: Rel (ConflictRel)
    public ushort SMineCm;    // s на моём connector'е, см
    public ushort SOtherCm;   // s на чужом, см
    public ushort Reserved;

    public readonly ConflictKind Kind => (ConflictKind)(Flags & 3);

    public readonly ConflictRel Rel => (ConflictRel)((Flags >> 2) & 3);
}

/// <summary>
/// Плотный массив connector'ов и их конфликтных пар (intersections.md §3, §4). Индекс записи равен
/// <c>LaneData.ConflictGroup</c> connector-полосы; connector'ы одного узла лежат подряд. Все массивы выделены
/// в конструкторе; <see cref="BuildAll"/> / <see cref="BuildConflicts"/> вызываются только при построении сети.
/// </summary>
internal sealed class ConnectorStore
{
    public const float ConflictWidth = 2.0f;   // м: Cross, если полилинии ближе
    private const int MaxLocal = NetworkBuilder.MaxConnectorsPerNode;

    public readonly int[] Lane;
    public readonly int[] Node;
    public readonly int[] InLane;
    public readonly int[] OutLane;
    public readonly float[] Length;         // длина connector'а (производное, вне хэша; ADR-0016 D4)
    public readonly float[] InLength;       // длина входной полосы connector'а (производное, вне хэша)
    public readonly byte[] LocalBit;
    public readonly byte[] Maneuver;
    public readonly byte[] FromArm;
    public readonly byte[] Flags;           // бит0 FromMinorArm
    public readonly int[] FirstConflict;
    public readonly int[] ConflictCount;
    public readonly ulong[] ConflictMask;
    public readonly ConflictEntry[] Conflicts;

    /// <summary>
    /// Производные пешеходов (pedestrians.md §2.2, вне хэша): индекс перехода (слот рукава в <c>NodeArms</c>), который connector пересекает
    /// на участке <c>S ∈ [0, w]</c> (<see cref="CrossInX"/>) или <c>[L_c − w, L_c]</c> (<see cref="CrossOutX"/>), иначе -1.
    /// null, если секции <c>peds</c> нет - тогда массивы не выделяются. Заполняет <c>PedNetwork</c>.
    /// </summary>
    public readonly int[]? CrossInX;
    public readonly int[]? CrossOutX;

    /// <summary>Арены строк connector'ов (<see cref="Lane"/> и параллельные массивы) и записей конфликтов (4.3, T2): хвосты - <see cref="Count"/> и <see cref="ConflictsUsed"/>.</summary>
    internal readonly BlockArena RowArena;
    internal readonly BlockArena ConflictArena;

    /// <summary>Занятая часть строк connector'ов (хвост арены строк): живые блоки узлов и дыры.</summary>
    public int Count { get => RowArena.Tail; set => RowArena.Tail = value; }

    public int ConflictsUsed { get => ConflictArena.Tail; set => ConflictArena.Tail = value; }

    // Рабочие буферы BuildConflicts (выделены один раз).
    private readonly byte[] _kind = new byte[MaxLocal * MaxLocal];
    private readonly float[] _sOn = new float[MaxLocal * MaxLocal];   // [i*MaxLocal+j] = s на connector'е i для пары (i, j)
    private readonly float[] _box = new float[MaxLocal * 4];

    public ConnectorStore(int maxConnectors, int maxConflicts, bool peds = false)
    {
        int c = Math.Max(0, maxConnectors);
        Lane = new int[c];
        Node = new int[c];
        InLane = new int[c];
        OutLane = new int[c];
        Length = new float[c];
        InLength = new float[c];
        LocalBit = new byte[c];
        Maneuver = new byte[c];
        FromArm = new byte[c];
        Flags = new byte[c];
        FirstConflict = new int[c];
        ConflictCount = new int[c];
        ConflictMask = new ulong[c];
        Conflicts = new ConflictEntry[Math.Max(0, maxConflicts)];
        RowArena = new BlockArena(c);
        ConflictArena = new BlockArena(Conflicts.Length);
        if (peds)
        {
            CrossInX = new int[c];
            CrossOutX = new int[c];
            Array.Fill(CrossInX, -1);
            Array.Fill(CrossOutX, -1);
        }
    }

    public int MaxConnectors => Lane.Length;

    public int MaxConflicts => Conflicts.Length;

    public void Reset()
    {
        RowArena.ClearState();
        ConflictArena.ClearState();
        Count = 0;
        ConflictsUsed = 0;
    }

    /// <summary>Сброс и построение для всех узлов с connector'ами. Первая ошибка прерывает обход (состояние после неё не определено).</summary>
    public NetworkStatus BuildAll(NetworkStore net)
    {
        Reset();
        for (int n = 0; n < net.NodeCount; n++)
        {
            if (net.Nodes[n].ConnectorCount == 0)
            {
                continue;
            }

            NetworkStatus st = BuildConflicts(net, n);
            if (st != NetworkStatus.Ok)
            {
                return st;
            }
        }

        return NetworkStatus.Ok;
    }

    /// <summary>
    /// Регистрирует connector'ы узла (<c>ConflictGroup</c> полос) и строит конфликтные пары (§3), включая <c>Rel</c> (§3.1).
    /// Для узлов с <see cref="NodeControl.None"/> конфликтов нет (ворот нет). Повторный вызов для узла — NodeAlreadyBuilt.
    /// </summary>
    public NetworkStatus BuildConflicts(NetworkStore net, int node)
    {
        if ((uint)node >= (uint)net.NodeCount)
        {
            return NetworkStatus.InvalidArgument;
        }

        ref readonly NodeData nd = ref net.Nodes[node];
        int k = nd.ConnectorCount;
        if (k == 0)
        {
            return NetworkStatus.Ok;
        }

        if (k > MaxLocal || nd.FirstArm < 0)
        {
            return NetworkStatus.TooManyConnectors;
        }

        if (net.Lanes[nd.FirstConnector].ConflictGroup >= 0)
        {
            return NetworkStatus.NodeAlreadyBuilt;
        }

        if (!net.CanAllocBlock(RowArena, k))
        {
            return NetworkStatus.ConnectorsFull;
        }

        int g0 = net.AllocBlock(RowArena, k);
        MarkSpareRowsDead(g0, k);
        Register(net, node, g0);
        if (nd.Control == NodeControl.None)
        {
            return NetworkStatus.Ok;
        }

        ComputePairs(net, g0, k);
        NetworkStatus st = WriteRows(net, node, g0, k);
        if (st != NetworkStatus.Ok)
        {
            // Строки уже зарегистрированы, места под конфликты нет: узел остаётся без записей (как раньше при ConflictsFull), блок строк не освобождаем.
            return st;
        }

        return NetworkStatus.Ok;
    }

    /// <summary>Запасные строки блока по лестнице (ёмкость сверх <paramref name="k"/>) получают канонический вид мёртвых.</summary>
    private void MarkSpareRowsDead(int g0, int k)
    {
        int cap = RowArena.CapacityOf(g0, k);
        for (int g = g0 + k; g < g0 + cap; g++)
        {
            KillRow(g);
        }
    }

    /// <summary>Строка мёртвая и каноническая (<see cref="KillRow"/>): так её пишет сейв и требует валидатор.</summary>
    public bool DeadRowIsCanonical(int g) =>
        Lane[g] == -1 && Node[g] == -1 && InLane[g] == -1 && OutLane[g] == -1 && Length[g] == 0f && InLength[g] == 0f && LocalBit[g] == 0 && Maneuver[g] == 0 && FromArm[g] == 0 &&
        Flags[g] == 0 && FirstConflict[g] == 0 && ConflictCount[g] == 0 && ConflictMask[g] == 0UL;

    /// <summary>Мёртвая строка: ссылки -1, остальное 0 (канон сейва, TL4); производные пешеходов - -1.</summary>
    private void KillRow(int g)
    {
        Lane[g] = -1;
        Node[g] = -1;
        InLane[g] = -1;
        OutLane[g] = -1;
        Length[g] = 0f;
        InLength[g] = 0f;
        LocalBit[g] = 0;
        Maneuver[g] = 0;
        FromArm[g] = 0;
        Flags[g] = 0;
        FirstConflict[g] = 0;
        ConflictCount[g] = 0;
        ConflictMask[g] = 0;
        if (CrossInX != null && CrossOutX != null)
        {
            CrossInX[g] = -1;
            CrossOutX[g] = -1;
        }
    }

    /// <summary>
    /// Освобождает строки и записи конфликтов узла по старому блоку (<paramref name="g0"/>, <paramref name="rows"/>): строки получают вид мёртвых, блок конфликтов (<paramref name="conflictFirst"/>,
    /// <paramref name="conflictTotal"/> - сумма <c>ConflictCount</c> строк) возвращается в арену, записи зануляются.
    /// </summary>
    public void FreeNodeRows(int g0, int rows, int conflictFirst, int conflictTotal)
    {
        int cap = RowArena.CapacityOf(g0, rows);
        for (int g = g0; g < g0 + cap; g++)
        {
            KillRow(g);
        }

        RowArena.Free(g0, rows);
        if (conflictTotal > 0)
        {
            int ccap = ConflictArena.CapacityOf(conflictFirst, conflictTotal);
            Array.Clear(Conflicts, conflictFirst, ccap);
            ConflictArena.Free(conflictFirst, conflictTotal);
        }
    }

    /// <summary>Флаги строк узла (<c>FromMinorArm</c>, знак «стоп») заново по <c>ArmFlags</c> рукавов (смена знаков рукавов, 4.3, T3); <c>Rel</c> - <see cref="RecomputeRel"/>.</summary>
    public void RefreshFlags(NetworkStore net, int node)
    {
        ref readonly NodeData nd = ref net.Nodes[node];
        if (nd.ConnectorCount == 0)
        {
            return;
        }

        int g0 = net.Lanes[nd.FirstConnector].ConflictGroup;
        if (g0 < 0)
        {
            return;
        }

        for (int i = 0; i < nd.ConnectorCount; i++)
        {
            byte af = net.ArmFlags[nd.FirstArm + FromArm[g0 + i]];
            Flags[g0 + i] = (byte)(((af & NetworkBuilder.ArmMinorBit) != 0 ? 1 : 0) | ((af & NetworkBuilder.ArmStopBit) != 0 ? 2 : 0));
        }
    }

    /// <summary>
    /// Пересчёт <c>Rel</c> всех записей узла (при смене <see cref="NodeControl"/>, §3.1); <paramref name="minorRules"/> —
    /// применять ли шаг 1 (второстепенные рукава: Yield/Stop или Signal в режиме FlashYellow).
    /// </summary>
    public void RecomputeRel(NetworkStore net, int node, bool minorRules)
    {
        ref readonly NodeData nd = ref net.Nodes[node];
        if (nd.ConnectorCount == 0)
        {
            return;
        }

        int g0 = net.Lanes[nd.FirstConnector].ConflictGroup;
        if (g0 < 0)
        {
            return;
        }

        for (int i = 0; i < nd.ConnectorCount; i++)
        {
            int g = g0 + i;
            int end = FirstConflict[g] + ConflictCount[g];
            for (int e = FirstConflict[g]; e < end; e++)
            {
                ref ConflictEntry ce = ref Conflicts[e];
                ConflictRel rel = ComputeRel(net, in nd, g, g0 + ce.OtherLocal, minorRules);
                ce.Flags = (byte)((ce.Flags & 3) | ((int)rel << 2));
            }
        }
    }

    /// <summary>Статическое отношение приоритета connector'а <paramref name="c"/> к <paramref name="other"/> (глобальные индексы, §3.1).</summary>
    public ConflictRel ComputeRel(NetworkStore net, in NodeData nd, int c, int other, bool minorRules)
    {
        int a = FromArm[c];
        int b = FromArm[other];
        if (minorRules)
        {
            bool ma = (Flags[c] & 1) != 0;
            bool mb = (Flags[other] & 1) != 0;
            if (ma && !mb)
            {
                return ConflictRel.Yield;
            }

            if (!ma && mb)
            {
                return ConflictRel.Priority;
            }
        }

        if (a == b)
        {
            return ConflictRel.Tie;
        }

        Vec2 inA = net.ArmDir[nd.FirstArm + a] * -1f;
        Vec2 dirB = net.ArmDir[nd.FirstArm + b];
        float dot = (inA.X * dirB.X) + (inA.Z * dirB.Z);
        if (dot >= NetworkBuilder.StraightCos)
        {
            var mine = (Network.Maneuver)Maneuver[c];
            var theirs = (Network.Maneuver)Maneuver[other];
            bool iTurnLeft = mine == Network.Maneuver.Left || mine == Network.Maneuver.UTurn;
            bool theyTurnLeft = theirs == Network.Maneuver.Left || theirs == Network.Maneuver.UTurn;
            if (iTurnLeft && !theyTurnLeft)
            {
                return ConflictRel.Yield;
            }

            if (!iTurnLeft && theyTurnLeft)
            {
                return ConflictRel.Priority;
            }

            return ConflictRel.Tie;
        }

        float cross = (inA.X * dirB.Z) - (inA.Z * dirB.X);
        return cross >= 0f ? ConflictRel.Yield : ConflictRel.Priority; // b справа — уступаю, слева — приоритет
    }

    /// <summary>FNV-1a 64 по занятой части всех массивов (детерминизм построения).</summary>
    public ulong Hash()
    {
        var h = Fnv1a64.Create();
        int n = Count;
        h.Add(n);
        h.Add(ConflictsUsed);
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(Lane, 0, n)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(Node, 0, n)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(InLane, 0, n)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(OutLane, 0, n)));
        h.Add(new ReadOnlySpan<byte>(LocalBit, 0, n));
        h.Add(new ReadOnlySpan<byte>(Maneuver, 0, n));
        h.Add(new ReadOnlySpan<byte>(FromArm, 0, n));
        h.Add(new ReadOnlySpan<byte>(Flags, 0, n));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(FirstConflict, 0, n)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(ConflictCount, 0, n)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(ConflictMask, 0, n)));
        h.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<ConflictEntry>(Conflicts, 0, ConflictsUsed)));
        return h.Value;
    }

    private void Register(NetworkStore net, int node, int g0)
    {
        ref readonly NodeData nd = ref net.Nodes[node];
        for (int i = 0; i < nd.ConnectorCount; i++)
        {
            int lane = nd.FirstConnector + i;
            ref LaneData ld = ref net.Lanes[lane];
            int g = g0 + i;
            ld.ConflictGroup = g;
            Lane[g] = lane;
            Node[g] = node;
            InLane[g] = ld.FromLane;
            OutLane[g] = ld.ToLane;
            Length[g] = ld.Length;
            InLength[g] = net.Lanes[ld.FromLane].Length;
            LocalBit[g] = (byte)i;
            int a = ArmOf(net, in nd, net.Lanes[ld.FromLane].Segment);
            int b = ArmOf(net, in nd, net.Lanes[ld.ToLane].Segment);
            FromArm[g] = (byte)a;
            Maneuver[g] = (byte)(a == b
                ? Network.Maneuver.UTurn
                : NetworkBuilder.ClassifyTurn(net.ArmDir[nd.FirstArm + a], net.ArmDir[nd.FirstArm + b], nd.ArmCount == 2));
            Flags[g] = (byte)(((net.ArmFlags[nd.FirstArm + a] & NetworkBuilder.ArmMinorBit) != 0 ? 1 : 0) | ((net.ArmFlags[nd.FirstArm + a] & NetworkBuilder.ArmStopBit) != 0 ? 2 : 0));
            FirstConflict[g] = 0;
            ConflictCount[g] = 0;
            ConflictMask[g] = 0;
        }
    }

    private static int ArmOf(NetworkStore net, in NodeData nd, int segment)
    {
        for (int a = 0; a < nd.ArmCount; a++)
        {
            if (net.NodeArms[nd.FirstArm + a] == segment)
            {
                return a;
            }
        }

        return 0;
    }

    /// <summary>Заполняет _kind/_sOn для всех пар i &lt; j узла (симметрично).</summary>
    private void ComputePairs(NetworkStore net, int g0, int k)
    {
        for (int i = 0; i < k; i++)
        {
            ref readonly LaneData ld = ref net.Lanes[Lane[g0 + i]];
            float x0 = float.MaxValue;
            float z0 = float.MaxValue;
            float x1 = float.MinValue;
            float z1 = float.MinValue;
            for (int p = 0; p < ld.PointCount; p++)
            {
                Vec2 v = net.LanePoints[ld.FirstPoint + p];
                x0 = MathF.Min(x0, v.X);
                z0 = MathF.Min(z0, v.Z);
                x1 = MathF.Max(x1, v.X);
                z1 = MathF.Max(z1, v.Z);
            }

            _box[(i * 4) + 0] = x0 - ConflictWidth;
            _box[(i * 4) + 1] = z0 - ConflictWidth;
            _box[(i * 4) + 2] = x1 + ConflictWidth;
            _box[(i * 4) + 3] = z1 + ConflictWidth;
        }

        for (int i = 0; i < k; i++)
        {
            _kind[(i * MaxLocal) + i] = 0;
            for (int j = i + 1; j < k; j++)
            {
                int ij = (i * MaxLocal) + j;
                int ji = (j * MaxLocal) + i;
                _kind[ij] = 0;
                _kind[ji] = 0;
                int gi = g0 + i;
                int gj = g0 + j;
                if (InLane[gi] == InLane[gj])
                {
                    continue; // diverge: конфликта нет
                }

                if (OutLane[gi] == OutLane[gj])
                {
                    _kind[ij] = _kind[ji] = (byte)(1 + (int)ConflictKind.Merge);
                    _sOn[ij] = net.Lanes[Lane[gi]].Length;
                    _sOn[ji] = net.Lanes[Lane[gj]].Length;
                    continue;
                }

                if (_box[(i * 4) + 2] < _box[(j * 4) + 0] || _box[(j * 4) + 2] < _box[(i * 4) + 0] ||
                    _box[(i * 4) + 3] < _box[(j * 4) + 1] || _box[(j * 4) + 3] < _box[(i * 4) + 1])
                {
                    continue;
                }

                float best = ClosestApproach(net, Lane[gi], Lane[gj], out float si, out float sj);
                if (best < ConflictWidth * ConflictWidth)
                {
                    _kind[ij] = _kind[ji] = (byte)(1 + (int)ConflictKind.Cross);
                    _sOn[ij] = si;
                    _sOn[ji] = sj;
                }
            }
        }
    }

    private NetworkStatus WriteRows(NetworkStore net, int node, int g0, int k)
    {
        ref readonly NodeData nd = ref net.Nodes[node];
        bool minorRules = nd.Control == NodeControl.Yield || nd.Control == NodeControl.Stop;

        // Один блок записей на узел: общее число известно до записи, нехватка места - до единственного изменения (T3, player-tools.md §3 п. 2).
        int total = 0;
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                if (_kind[(i * MaxLocal) + j] != 0)
                {
                    total++;
                }
            }
        }

        if (!net.CanAllocBlock(ConflictArena, total))
        {
            return NetworkStatus.ConflictsFull;
        }

        int block = net.AllocBlock(ConflictArena, total);
        int used = 0;
        for (int i = 0; i < k; i++)
        {
            int g = g0 + i;
            int first = block + used;
            int count = 0;
            ulong mask = 0;
            for (int j = 0; j < k; j++)
            {
                int kind = _kind[(i * MaxLocal) + j];
                if (kind == 0)
                {
                    continue;
                }

                var entry = new ConflictEntry
                {
                    OtherLocal = (byte)j,
                    SMineCm = ToCm(_sOn[(i * MaxLocal) + j]),
                    SOtherCm = ToCm(_sOn[(j * MaxLocal) + i]),
                };
                ConflictRel rel = ComputeRel(net, in nd, g, g0 + j, minorRules);
                entry.Flags = (byte)((kind - 1) | ((int)rel << 2));
                mask |= 1UL << j;

                // Вставка с сохранением порядка по SMine (при равенстве — по OtherLocal; j возрастает, поэтому после равных).
                int pos = first + count;
                while (pos > first && Conflicts[pos - 1].SMineCm > entry.SMineCm)
                {
                    Conflicts[pos] = Conflicts[pos - 1];
                    pos--;
                }

                Conflicts[pos] = entry;
                count++;
            }

            FirstConflict[g] = first;
            ConflictCount[g] = count;
            ConflictMask[g] = mask;
            used += count;
        }

        return NetworkStatus.Ok;
    }

    private static ushort ToCm(float s)
    {
        int v = DetMath.ToIntSat((s * 100f) + 0.5f);
        return (ushort)(v < 0 ? 0 : v > ushort.MaxValue ? ushort.MaxValue : v);
    }

    /// <summary>Минимальное расстояние² между полилиниями двух полос и s обеих в точке наибольшего сближения.</summary>
    private static float ClosestApproach(NetworkStore net, int laneA, int laneB, out float sA, out float sB)
    {
        ref readonly LaneData a = ref net.Lanes[laneA];
        ref readonly LaneData b = ref net.Lanes[laneB];
        float best = float.MaxValue;
        sA = 0f;
        sB = 0f;
        for (int i = 0; i < a.PointCount - 1; i++)
        {
            Vec2 p1 = net.LanePoints[a.FirstPoint + i];
            Vec2 q1 = net.LanePoints[a.FirstPoint + i + 1];
            float sa0 = net.LanePointS[a.FirstPoint + i];
            float sa1 = net.LanePointS[a.FirstPoint + i + 1];
            float ax0 = MathF.Min(p1.X, q1.X) - ConflictWidth;
            float ax1 = MathF.Max(p1.X, q1.X) + ConflictWidth;
            float az0 = MathF.Min(p1.Z, q1.Z) - ConflictWidth;
            float az1 = MathF.Max(p1.Z, q1.Z) + ConflictWidth;
            for (int j = 0; j < b.PointCount - 1; j++)
            {
                Vec2 p2 = net.LanePoints[b.FirstPoint + j];
                Vec2 q2 = net.LanePoints[b.FirstPoint + j + 1];

                // Отрезки дальше ConflictWidth друг от друга конфликта не дадут: отсекаем по AABB.
                if (MathF.Max(p2.X, q2.X) < ax0 || MathF.Min(p2.X, q2.X) > ax1 || MathF.Max(p2.Z, q2.Z) < az0 || MathF.Min(p2.Z, q2.Z) > az1)
                {
                    continue;
                }

                float d2 = SegmentDistSq(p1, q1, p2, q2, out float s, out float t);
                if (d2 < best)
                {
                    best = d2;
                    sA = sa0 + (s * (sa1 - sa0));
                    float sb0 = net.LanePointS[b.FirstPoint + j];
                    sB = sb0 + (t * (net.LanePointS[b.FirstPoint + j + 1] - sb0));
                }
            }
        }

        return best;
    }

    /// <summary>Ближайшие точки двух отрезков (Ericson, Real-Time Collision Detection 5.1.9): параметры s, t ∈ [0, 1] и расстояние².</summary>
    private static float SegmentDistSq(Vec2 p1, Vec2 q1, Vec2 p2, Vec2 q2, out float s, out float t)
    {
        const float eps = 1e-9f;
        Vec2 d1 = q1 - p1;
        Vec2 d2 = q2 - p2;
        Vec2 r = p1 - p2;
        float a = Dot(d1, d1);
        float e = Dot(d2, d2);
        float f = Dot(d2, r);
        if (a <= eps && e <= eps)
        {
            s = 0f;
            t = 0f;
        }
        else if (a <= eps)
        {
            s = 0f;
            t = Clamp01(f / e);
        }
        else
        {
            float c = Dot(d1, r);
            if (e <= eps)
            {
                t = 0f;
                s = Clamp01(-c / a);
            }
            else
            {
                float b = Dot(d1, d2);
                float denom = (a * e) - (b * b);
                s = denom > eps ? Clamp01(((b * f) - (c * e)) / denom) : 0f;
                t = ((b * s) + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Clamp01((b - c) / a);
                }
            }
        }

        Vec2 c1 = p1 + (d1 * s);
        Vec2 c2 = p2 + (d2 * t);
        Vec2 diff = c1 - c2;
        return Dot(diff, diff);
    }

    private static float Dot(Vec2 a, Vec2 b) => (a.X * b.X) + (a.Z * b.Z);

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
