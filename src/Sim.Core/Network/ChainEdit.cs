using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Routing;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Участок «цепочки» дороги между двумя дальними узлами (T5): сегмент или connector'ы промежуточного узла. <see cref="U0"/>/<see cref="Len"/> - метры вдоль цепочки от её начального узла;
/// <see cref="Forward"/> - ось сегмента A-&gt;B идёт по цепочке.
/// </summary>
internal struct ChainPiece
{
    public int Seg;
    public int Node;
    public bool Forward;
    public float U0;
    public float Len;
}

/// <summary>
/// Разрез сегмента узлом (<c>SplitRoad</c>, <c>BuildRoad</c> с <c>AutoSplit</c>) и слияние двух сегментов в узле степени 2 (<c>MergeAtNode</c>) - T5, player-tools.md §4.4.
/// Без дренажа: новая дорога строится рядом со старой (новые слоты), все машины переносятся по координате вдоль цепочки (номер полосы и направление сохраняются; машины «коробки»
/// разреза встают на connector'ы нового узла, машины на connector'ах сливаемого узла - на полосы общего сегмента), connector'ы дальних узлов переводятся на новые полосы и узлы
/// перестраиваются без исчезновений (знаки, запреты и план светофора - по сегментам рукавов). Старые сегменты и узел освобождаются; handle'ы новые (перенумерация - у диспетчера, по Restore*).
/// Предусловия (валидатор): ни остановок, ни операций, ни Q-meso; число полос по направлениям цепочки одинаково на всех участках.
/// </summary>
internal static class ChainEdit
{
    private const float ViaEps = 0.05f;

    // ---------------------------------------------------------------- геометрия

    public static float SetbackA(NetworkStore n, in SegmentData sd) => Dist(n.Nodes[sd.NodeA].Position, n.SegmentPoints[sd.FirstPoint]);

    public static float SetbackB(NetworkStore n, in SegmentData sd) => Dist(n.Nodes[sd.NodeB].Position, n.SegmentPoints[sd.FirstPoint + sd.PointCount - 1]);

    /// <summary>Точка на полной оси (от узла A через точки оси к узлу B) на расстоянии <paramref name="s"/> от узла A; <paramref name="before"/> - сколько точек оси лежат до неё.</summary>
    public static Vec2 PointOnFull(NetworkStore n, in SegmentData sd, float s, out int before)
    {
        Vec2 prev = n.Nodes[sd.NodeA].Position;
        float acc = 0f;
        int m = sd.PointCount;
        for (int i = 0; i <= m; i++)
        {
            Vec2 next = i < m ? n.SegmentPoints[sd.FirstPoint + i] : n.Nodes[sd.NodeB].Position;
            float d = Dist(prev, next);
            if (acc + d >= s)
            {
                before = i;
                float f = d > 0f ? (s - acc) / d : 0f;
                return prev + ((next - prev) * f);
            }

            acc += d;
            prev = next;
        }

        before = m;
        return n.Nodes[sd.NodeB].Position;
    }

    /// <summary>Ближайшая к <paramref name="p"/> точка полной оси сегмента: расстояние, точка <paramref name="q"/> и её расстояние от узла A по оси <paramref name="s"/>.</summary>
    public static float Project(NetworkStore n, in SegmentData sd, Vec2 p, out float s, out Vec2 q)
    {
        Vec2 prev = n.Nodes[sd.NodeA].Position;
        float acc = 0f;
        float best = float.MaxValue;
        s = 0f;
        q = prev;
        int m = sd.PointCount;
        for (int i = 0; i <= m; i++)
        {
            Vec2 next = i < m ? n.SegmentPoints[sd.FirstPoint + i] : n.Nodes[sd.NodeB].Position;
            float dx = next.X - prev.X;
            float dz = next.Z - prev.Z;
            float len2 = (dx * dx) + (dz * dz);
            if (len2 > 0f)
            {
                float t = Math.Clamp((((p.X - prev.X) * dx) + ((p.Z - prev.Z) * dz)) / len2, 0f, 1f);
                var c = new Vec2(prev.X + (dx * t), prev.Z + (dz * t));
                float d = Dist(p, c);
                if (d < best)
                {
                    best = d;
                    s = acc + (t * MathF.Sqrt(len2));
                    q = c;
                }

                acc += MathF.Sqrt(len2);
            }

            prev = next;
        }

        return best;
    }

    /// <summary>Есть ли машины в «коробке» будущего узла разреза (±отступ узла и длина машины от точки <paramref name="s"/> полной оси): врезка AutoSplit тогда ждёт (<c>Busy</c>).</summary>
    public static bool BoxOccupied(World w, int seg, float s)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        ref readonly SegmentData sd = ref n.Segments[seg];
        float sa = SetbackA(n, in sd);
        float reach = w.Builder.Options.NodeSetback + 8f;
        for (int i = 0; i < v.HighWater; i++)
        {
            for (int r = 0; r < 2; r++)
            {
                int lane = r == 0 ? v.Lane[i] : v.GhostLane[i];
                if (!v.Pool.IsAlive(i) || lane < 0 || n.Lanes[lane].Kind != LaneKind.Normal || n.Lanes[lane].Segment != seg)
                {
                    continue;
                }

                float u = sa + (n.Lanes[lane].Direction == 0 ? v.S[i] : sd.Length - v.S[i]);
                if (MathF.Abs(u - s) <= reach)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Сегмент разреза: длина полной оси половин не меньше минимума построителя, остающиеся полосы не короче <c>MinLaneLength</c>.</summary>
    public static bool SplitFits(NetworkStore n, NetworkOptions o, int seg, float s)
    {
        ref readonly SegmentData sd = ref n.Segments[seg];
        float sa = SetbackA(n, in sd);
        float sb = SetbackB(n, in sd);
        float full = sa + sd.Length + sb;
        float min = (2f * o.NodeSetback) + o.MinLaneLength;
        return s >= min && full - s >= min && s - sa - o.NodeSetback >= o.MinLaneLength && full - s - sb - o.NodeSetback >= o.MinLaneLength;
    }

    // Узел, у которого ровно два конца сегментов (разных). Порядок - направление будущего общего сегмента: первым идёт сегмент, входящий в узел (NodeB == node), если второй из него
    // выходит (сохраняет ориентацию цепочки «X -> N -> Y», в том числе после разреза); иначе - по возрастанию индекса.
    public static bool TwoSegments(NetworkStore n, int node, out int s1, out int s2)
    {
        s1 = -1;
        s2 = -1;
        int count = 0;
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            int seg = code >> 1;
            if (count == 0)
            {
                s1 = seg;
            }
            else if (count == 1)
            {
                s2 = seg;
            }

            count++;
        }

        if (count != 2 || s1 == s2)
        {
            return false;
        }

        if (s2 < s1)
        {
            (s1, s2) = (s2, s1);
        }

        if (n.Segments[s1].NodeA == node && n.Segments[s2].NodeB == node)
        {
            (s1, s2) = (s2, s1);
        }

        return true;
    }

    /// <summary>Дальний от <paramref name="node"/> конец сегмента.</summary>
    public static int Far(NetworkStore n, int seg, int node) => n.Segments[seg].NodeA == node ? n.Segments[seg].NodeB : n.Segments[seg].NodeA;

    /// <summary>Полос в направлении цепочки <paramref name="d"/> (0 - от начала к концу) у сегмента с ориентацией <paramref name="forward"/>.</summary>
    public static int LanesAlong(in SegmentData sd, bool forward, int d) => forward == (d == 0) ? sd.LanesAB : sd.LanesBA;

    /// <summary>Эффективная зеркальность профиля сегмента по направлению цепочки (сегмент против цепочки = зеркальный).</summary>
    public static bool MirroredAlong(in SegmentData sd, bool forward) => ((sd.Flags & SegmentFlags.Mirrored) != 0) == forward;

    // ---------------------------------------------------------------- разрез

    /// <summary>
    /// Разрез сегмента <paramref name="seg"/> в точке полной оси <paramref name="s"/> (м от узла A): новый узел <paramref name="node"/>, половины <paramref name="seg1"/> (A-&gt;N) и
    /// <paramref name="seg2"/> (N-&gt;B). Предусловия проверил валидатор; не-Ok - ёмкость (мир не тронут, если отказ до переноса).
    /// </summary>
    public static NetworkStatus Split(World w, int seg, float s, uint tag, out int node, out int seg1, out int seg2)
    {
        NetworkStore n = w.Net;
        NetworkBuilder b = w.Builder;
        NetworkOptions o = b.Options;
        seg1 = -1;
        seg2 = -1;
        SegmentData sd = n.Segments[seg];
        float sa = SetbackA(n, in sd);
        float sb = SetbackB(n, in sd);
        Vec2 p = PointOnFull(n, in sd, s, out int before);
        int yCm = w.Terrain != null ? RoadTerrain.SegmentYCm(w, seg, s) : 0;   // TR3: узел разреза - на профиле сегмента (уклоны половин сохраняются)
        NetworkStatus st = b.AddNodeExact(p, out node);
        if (st != NetworkStatus.Ok)
        {
            return st;
        }

        if (w.Terrain != null)
        {
            n.Nodes[node].Elevation = RoadTerrain.Meters(yCm);
        }

        // Ось половин: точки старой оси до и после точки разреза (крайние точки старой оси лежат на прямой «узел - соседняя точка» - их не повторяем).
        Span<Vec2> via1 = stackalloc Vec2[NetworkBuilder.MaxVia + 2];
        Span<Vec2> via2 = stackalloc Vec2[NetworkBuilder.MaxVia + 2];
        int c1 = 0;
        int c2 = 0;
        for (int i = 1; i < before; i++)
        {
            Vec2 q = n.SegmentPoints[sd.FirstPoint + i];
            if (Dist(q, p) > ViaEps)
            {
                via1[c1++] = q;
            }
        }

        for (int i = before; i < sd.PointCount - 1; i++)
        {
            Vec2 q = n.SegmentPoints[sd.FirstPoint + i];
            if (Dist(q, p) > ViaEps)
            {
                via2[c2++] = q;
            }
        }

        bool mirrored = (sd.Flags & SegmentFlags.Mirrored) != 0;
        st = Add(b, sd.NodeA, node, via1[..c1], in sd, mirrored, sa, o.NodeSetback, out seg1);
        if (st == NetworkStatus.Ok)
        {
            st = Add(b, node, sd.NodeB, via2[..c2], in sd, mirrored, o.NodeSetback, sb, out seg2);
            if (st != NetworkStatus.Ok)
            {
                NetworkEdit.ReleaseSegment(w, seg1);
            }
        }

        if (st != NetworkStatus.Ok)
        {
            NetworkEdit.ReleaseNode(w, node);   // ёмкость (валидатор её проверяет): мир как был
            node = -1;
            seg1 = -1;
            seg2 = -1;
            return st;
        }

        st = b.BuildConnectorsEx(node);
        if (st == NetworkStatus.Ok)
        {
            st = w.Connectors.BuildConflicts(n, node);
        }

        System.Diagnostics.Debug.Assert(st == NetworkStatus.Ok, "connector'ы узла разреза (ёмкость проверил валидатор)");

        CopyLaneFlags(n, seg, seg1, true, true);
        CopyLaneFlags(n, seg, seg2, true, true);
        if ((sd.Flags & SegmentFlags.Bridge) != 0)
        {
            // TR3: разрез моста - обе половины мосты (без отказа: разрез - обратная команда слияния).
            n.Segments[seg1].Flags |= SegmentFlags.Bridge;
            n.Segments[seg2].Flags |= SegmentFlags.Bridge;
        }

        Span<ChainPiece> old = stackalloc ChainPiece[1];
        old[0] = new ChainPiece { Seg = seg, Node = -1, Forward = true, U0 = sa, Len = sd.Length };
        Span<ChainPiece> neu = stackalloc ChainPiece[3];
        float u2 = s + o.NodeSetback;
        neu[0] = new ChainPiece { Seg = seg1, Node = -1, Forward = true, U0 = sa, Len = n.Segments[seg1].Length };
        neu[1] = new ChainPiece { Seg = -1, Node = node, Forward = true, U0 = sa + n.Segments[seg1].Length, Len = u2 - (sa + n.Segments[seg1].Length) };
        neu[2] = new ChainPiece { Seg = seg2, Node = -1, Forward = true, U0 = u2, Len = n.Segments[seg2].Length };
        Transfer(w, old, neu, sd.NodeA, sd.NodeB, tag);

        w.Emit(SimEventKind.NodeCreated, tag, node);
        w.Emit(SimEventKind.SegmentCreated, tag, seg1);
        w.Emit(SimEventKind.SegmentCreated, tag, seg2);
        w.Emit(SimEventKind.SegmentRemoved, tag, seg, sd.Generation);
        return NetworkStatus.Ok;
    }

    private static NetworkStatus Add(NetworkBuilder b, int a, int z, ReadOnlySpan<Vec2> via, in SegmentData like, bool mirrored, float setA, float setB, out int seg) =>
        like.Profile >= 0
            ? b.AddProfileSegment(a, z, via, like.Profile, mirrored, like.SpeedLimit, setA, setB, out seg)
            : b.AddSegment(a, z, via, like.LanesAB, like.LanesBA, like.SpeedLimit, like.Kind, setA, setB, out seg);

    // Флаги полос игрока (BusOnly, NoLaneChangeOut) старого сегмента - новому по направлению цепочки и номеру полосы.
    private static void CopyLaneFlags(NetworkStore n, int from, int to, bool fromForward, bool toForward)
    {
        ref readonly SegmentData f = ref n.Segments[from];
        ref readonly SegmentData t = ref n.Segments[to];
        for (int d = 0; d < 2; d++)
        {
            int cnt = Math.Min(LanesAlong(in f, fromForward, d), LanesAlong(in t, toForward, d));
            int fl = fromForward == (d == 0) ? f.FirstLaneAB : f.FirstLaneBA;
            int tl = toForward == (d == 0) ? t.FirstLaneAB : t.FirstLaneBA;
            for (int k = 0; k < cnt; k++)
            {
                n.Lanes[tl + k].Flags = (byte)(n.Lanes[fl + k].Flags & (LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut));
            }
        }
    }

    // ---------------------------------------------------------------- слияние

    /// <summary>
    /// Слияние двух сегментов узла степени 2 <paramref name="node"/> в один <paramref name="merged"/> (от дальнего конца сегмента с меньшим индексом к дальнему концу второго); узел освобождается.
    /// <paramref name="splitS"/> - расстояние от начала нового сегмента до бывшего узла по полной оси (для обратной <c>SplitRoad</c>).
    /// </summary>
    public static NetworkStatus Merge(World w, int node, uint tag, out int merged, out float splitS)
    {
        NetworkStore n = w.Net;
        NetworkBuilder b = w.Builder;
        merged = -1;
        splitS = 0f;
        TwoSegments(n, node, out int s1, out int s2);
        SegmentData d1 = n.Segments[s1];
        SegmentData d2 = n.Segments[s2];
        int far1 = Far(n, s1, node);
        int far2 = Far(n, s2, node);
        bool fw1 = d1.NodeA == far1;
        bool fw2 = d2.NodeA == node;
        Vec2 np = n.Nodes[node].Position;
        Span<Vec2> via = stackalloc Vec2[NetworkBuilder.MaxVia + 4];
        int c = 0;
        for (int i = 1; i < d1.PointCount - 1; i++)
        {
            via[c++] = n.SegmentPoints[d1.FirstPoint + (fw1 ? i : d1.PointCount - 1 - i)];
        }

        via[c++] = np;
        for (int i = 1; i < d2.PointCount - 1; i++)
        {
            via[c++] = n.SegmentPoints[d2.FirstPoint + (fw2 ? i : d2.PointCount - 1 - i)];
        }

        float set1 = fw1 ? SetbackA(n, in d1) : SetbackB(n, in d1);
        float set2 = fw2 ? SetbackB(n, in d2) : SetbackA(n, in d2);
        Vec2 end1 = n.SegmentPoints[d1.FirstPoint + (fw1 ? d1.PointCount - 1 : 0)];
        Vec2 start2 = n.SegmentPoints[d2.FirstPoint + (fw2 ? 0 : d2.PointCount - 1)];
        bool mirrored = MirroredAlong(in d1, fw1);
        var like = d1;
        like.LanesAB = (byte)LanesAlong(in d1, fw1, 0);
        like.LanesBA = (byte)LanesAlong(in d1, fw1, 1);
        NetworkStatus st = Add(b, far1, far2, via[..c], in like, mirrored, set1, set2, out merged);
        if (st != NetworkStatus.Ok)
        {
            return st;
        }

        CopyLaneFlags(n, s1, merged, fw1, true);
        if ((d1.Flags & d2.Flags & SegmentFlags.Bridge) != 0)
        {
            n.Segments[merged].Flags |= SegmentFlags.Bridge;   // TR3: слияние двух мостов (мост с дорогой валидатор отклоняет)
        }

        float g1 = Dist(end1, np);
        splitS = set1 + d1.Length + g1;
        Span<ChainPiece> old = stackalloc ChainPiece[3];
        old[0] = new ChainPiece { Seg = s1, Node = -1, Forward = fw1, U0 = set1, Len = d1.Length };
        old[1] = new ChainPiece { Seg = -1, Node = node, Forward = true, U0 = set1 + d1.Length, Len = g1 + Dist(np, start2) };
        old[2] = new ChainPiece { Seg = s2, Node = -1, Forward = fw2, U0 = old[1].U0 + old[1].Len, Len = d2.Length };
        Span<ChainPiece> neu = stackalloc ChainPiece[1];
        neu[0] = new ChainPiece { Seg = merged, Node = -1, Forward = true, U0 = set1, Len = n.Segments[merged].Length };
        Transfer(w, old, neu, far1, far2, tag);

        w.Emit(SimEventKind.SegmentCreated, tag, merged);
        w.Emit(SimEventKind.SegmentRemoved, tag, s1, d1.Generation);
        w.Emit(SimEventKind.SegmentRemoved, tag, s2, d2.Generation);
        w.Emit(SimEventKind.NodeRemoved, tag, node, n.Nodes[node].Generation - 1);
        return NetworkStatus.Ok;
    }

    // ---------------------------------------------------------------- перенос

    /// <summary>
    /// Общая часть: старые участки цепочки <paramref name="old"/> заменяются новыми <paramref name="neu"/> (новые сегменты и узлы уже построены). Машины, назначения, поездки, полосы зон,
    /// участки города и connector'ы дальних узлов <paramref name="farA"/>/<paramref name="farB"/> переводятся на новые полосы; дальние узлы перестраиваются; старые сегменты и узел
    /// (у слияния) освобождаются; производные пересчитываются.
    /// </summary>
    private static void Transfer(World w, ReadOnlySpan<ChainPiece> old, ReadOnlySpan<ChainPiece> neu, int farA, int farB, uint tag)
    {
        NetworkStore n = w.Net;
        n.ToolsTouched = 1;

        // Маршруты через старые участки (окна, планы) и кэш первого хода - освобождаются, как при закрытии.
        SetClosed(n, old, true);
        SegmentClosure.AfterClosing(w);
        ReleasePassedWindows(w);
        SetClosed(n, old, false);

        w.Lod?.OnNetworkGrown();   // ячейки LOD новых полос - до вставки машин (EnterLevel читает ячейку полосы)
        MoveVehicles(w, old, neu);
        MoveDestinations(w, old, neu);
        MoveZoneLanes(n, old, neu);
        if (w.City != null)
        {
            MoveLots(w.City, n, old, neu);
        }

        // Дальние узлы: connector'ы и рукава - на новые полосы и сегменты (перестройка их сохранит: пары полос те же).
        RetargetFar(w, farA, old[0], neu[0], 0);
        RetargetFar(w, farB, old[^1], neu[^1], 1);
        for (int k = 0; k < old.Length; k++)
        {
            if (old[k].Seg >= 0)
            {
                w.Builder.UnlinkSegment(old[k].Seg);
                ClearNext(n, old[k].Seg);
            }
        }

        int lo = Math.Min(farA, farB);
        int hi = Math.Max(farA, farB);
        RebuildIfBuilt(w, lo, tag);
        if (hi != lo)
        {
            RebuildIfBuilt(w, hi, tag);
        }

        // Промежуточный узел слияния: без рукавов (connector'ы пусты - машины перенесены), затем освобождается.
        int midNode = -1;
        for (int k = 0; k < old.Length; k++)
        {
            if (old[k].Seg < 0)
            {
                midNode = old[k].Node;
                RebuildIfBuilt(w, midNode, tag);
            }
        }

        for (int k = 0; k < old.Length; k++)
        {
            if (old[k].Seg >= 0)
            {
                bool released = NetworkEdit.ReleaseSegment(w, old[k].Seg);
                System.Diagnostics.Debug.Assert(released, "освобождение старого сегмента цепочки");
            }
        }

        if (midNode >= 0)
        {
            GatePhase.ResetNode(w, midNode);
            bool freed = NetworkEdit.ReleaseNode(w, midNode);
            System.Diagnostics.Debug.Assert(freed, "освобождение узла слияния");
        }

        for (int k = 0; k < neu.Length; k++)
        {
            if (neu[k].Seg < 0)
            {
                GatePhase.ResetNode(w, neu[k].Node);
            }
        }

        GatePhase.ResetNode(w, farA);
        GatePhase.ResetNode(w, farB);
        w.Gate.FullScan = true;
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            v.NextConn[i] = v.Pool.IsAlive(i) ? -1 : v.NextConn[i];
        }

        v.MarkAllRouteDirty();
        n.Version++;
        NetworkEdit.RefreshDerived(w);
        if (w.City != null)
        {
            int lost = w.City.LotGen.OnRoadsChanged(w, w.City);
            if (lost > 0)
            {
                w.Emit(SimEventKind.ZoneLotsLost, tag, neu[0].Seg, lost);
            }

            w.City.LotGen.RestoreDerived(w);
            w.City.Lots.Version++;
        }
    }

    // Окно маршрута целиком (и пройденные шаги до курсора): шаг на закрытой полосе - маршрут освобождается (AfterClosing смотрит только шаги впереди курсора).
    private static void ReleasePassedWindows(World w)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        RouteStore rs = w.Routes;
        for (int i = 0; i < v.HighWater; i++)
        {
            int r = v.Route[i];
            if (!v.Pool.IsAlive(i) || r < 0 || !rs.IsAlive(new RouteHandle(r, v.RouteGen[i])))
            {
                continue;
            }

            int b = r * rs.WindowSteps;
            for (int k = 0; k < rs.WinCount[r]; k++)
            {
                if ((n.Lanes[rs.Window[b + k].Lane].Flags & LaneFlags.Closed) != 0)
                {
                    RouteNav.ReleaseRoute(w, i);
                    break;
                }
            }
        }
    }

    private static void RebuildIfBuilt(World w, int node, uint tag)
    {
        if (w.Net.Nodes[node].FirstArm < 0)
        {
            return;
        }

        NetworkStatus st = NodeEdit.Rebuild(w, node, rejectTrap: false, tag);
        System.Diagnostics.Debug.Assert(st == NetworkStatus.Ok, "перестройка узла цепочки без исчезающих занятых connector'ов");
    }

    // Временное закрытие старых участков: сегменты Closing, их полосы и connector'ы промежуточного узла Closed (только для скана маршрутов).
    private static void SetClosed(NetworkStore n, ReadOnlySpan<ChainPiece> old, bool on)
    {
        for (int k = 0; k < old.Length; k++)
        {
            if (old[k].Seg >= 0)
            {
                ref SegmentData sd = ref n.Segments[old[k].Seg];
                sd.Flags = on ? (byte)(sd.Flags | SegmentFlags.Closing) : (byte)(sd.Flags & ~SegmentFlags.Closing);
                int total = sd.LanesAB + sd.LanesBA;
                int first = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
                for (int q = 0; q < total; q++)
                {
                    SetLaneClosed(n, first + q, on);
                }
            }
            else
            {
                ref readonly NodeData nd = ref n.Nodes[old[k].Node];
                for (int q = 0; q < nd.ConnectorCount; q++)
                {
                    SetLaneClosed(n, nd.FirstConnector + q, on);
                }
            }
        }
    }

    private static void SetLaneClosed(NetworkStore n, int lane, bool on) =>
        n.Lanes[lane].Flags = on ? (byte)(n.Lanes[lane].Flags | LaneFlags.Closed) : (byte)(n.Lanes[lane].Flags & ~LaneFlags.Closed);

    private static void ClearNext(NetworkStore n, int seg)
    {
        ref readonly SegmentData sd = ref n.Segments[seg];
        int total = sd.LanesAB + sd.LanesBA;
        int first = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
        for (int q = 0; q < total; q++)
        {
            n.Lanes[first + q].FirstNext = 0;
            n.Lanes[first + q].NextCount = 0;
        }
    }

    // Участок цепочки, которому принадлежит полоса (обычная полоса сегмента или connector промежуточного узла), или -1; направление цепочки и номер полосы.
    private static int PieceOf(NetworkStore n, ReadOnlySpan<ChainPiece> pieces, int lane, out int d, out int k)
    {
        d = 0;
        k = 0;
        if (lane < 0)
        {
            return -1;
        }

        ref readonly LaneData ld = ref n.Lanes[lane];
        for (int p = 0; p < pieces.Length; p++)
        {
            if (pieces[p].Seg >= 0)
            {
                if (ld.Kind == LaneKind.Normal && ld.Segment == pieces[p].Seg)
                {
                    d = pieces[p].Forward ? ld.Direction : 1 - ld.Direction;
                    k = ld.LaneNo;
                    return p;
                }
            }
            else if (ld.Kind == LaneKind.Connector && ld.Node == pieces[p].Node)
            {
                int pf = PieceOf(n, pieces, ld.FromLane, out d, out k);
                if (pf < 0)
                {
                    return -1;
                }

                d = pf < p ? 0 : 1;
                return p;
            }
        }

        return -1;
    }

    // Полоса участка p в направлении цепочки d с номером k (-1 - нет).
    private static int LaneOf(NetworkStore n, ReadOnlySpan<ChainPiece> pieces, int p, int d, int k)
    {
        ChainPiece pc = pieces[p];
        if (pc.Seg >= 0)
        {
            ref readonly SegmentData sd = ref n.Segments[pc.Seg];
            bool ab = pc.Forward == (d == 0);
            int cnt = ab ? sd.LanesAB : sd.LanesBA;
            return k < cnt ? (ab ? sd.FirstLaneAB : sd.FirstLaneBA) + k : -1;
        }

        int pf = d == 0 ? p - 1 : p + 1;
        int pt = d == 0 ? p + 1 : p - 1;
        if (pf < 0 || pt < 0 || pf >= pieces.Length || pt >= pieces.Length)
        {
            return -1;
        }

        int from = LaneOf(n, pieces, pf, d, k);
        int to = LaneOf(n, pieces, pt, d, k);
        ref readonly NodeData nd = ref n.Nodes[pc.Node];
        int any = -1;
        for (int q = 0; q < nd.ConnectorCount; q++)
        {
            int c = nd.FirstConnector + q;
            if (n.Lanes[c].FromLane == from)
            {
                if (n.Lanes[c].ToLane == to)
                {
                    return c;
                }

                any = any < 0 ? c : any;
            }
        }

        return any;
    }

    private static float ULength(NetworkStore n, int lane, in ChainPiece pc) => pc.Seg >= 0 ? pc.Len : MathF.Max(n.Lanes[lane].Length, 0.01f);

    // Координата вдоль цепочки точки s полосы участка pc в направлении d.
    private static float ToU(NetworkStore n, in ChainPiece pc, int lane, int d, float s)
    {
        float scale = pc.Len / ULength(n, lane, in pc);
        float along = s * scale;
        return d == 0 ? pc.U0 + along : pc.U0 + pc.Len - along;
    }

    private static float FromU(NetworkStore n, in ChainPiece pc, int lane, int d, float u)
    {
        float lenLane = ULength(n, lane, in pc);
        float along = Math.Clamp(d == 0 ? u - pc.U0 : pc.U0 + pc.Len - u, 0f, pc.Len);
        float s = pc.Len > 0f ? along * (lenLane / pc.Len) : 0f;
        return Math.Clamp(s, 0f, n.Lanes[lane].Length);
    }

    // Новая полоса и S для координаты u (направление d, номер k). segmentsOnly - только сегменты (назначения): точка в «коробке» - на ближайший конец сегмента.
    private static int Target(NetworkStore n, ReadOnlySpan<ChainPiece> neu, int d, int k, float u, bool segmentsOnly, out float s)
    {
        int p = neu.Length - 1;
        for (int q = 0; q < neu.Length; q++)
        {
            if (u < neu[q].U0 + neu[q].Len)
            {
                p = q;
                break;
            }
        }

        int lane = segmentsOnly && neu[p].Seg < 0 ? -1 : LaneOf(n, neu, p, d, k);
        if (lane < 0)
        {
            // Нет connector'а нужной пары (или назначение): ближайший сегмент - предыдущий/следующий по цепочке.
            float mid = neu[p].U0 + (neu[p].Len * 0.5f);
            int alt = u < mid ? Math.Max(0, p - 1) : Math.Min(neu.Length - 1, p + 1);
            if (neu[alt].Seg < 0)
            {
                alt = alt == p - 1 ? Math.Max(0, p - 2) : Math.Min(neu.Length - 1, p + 2);
            }

            p = alt;
            lane = LaneOf(n, neu, p, d, k);
            if (lane < 0)
            {
                lane = LaneOf(n, neu, p, d, 0);
            }
        }

        s = lane >= 0 ? FromU(n, neu[p], lane, d, u) : 0f;
        return lane;
    }

    private static void MoveVehicles(World w, ReadOnlySpan<ChainPiece> old, ReadOnlySpan<ChainPiece> neu)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int lane = v.Lane[i];
            int p = PieceOf(n, old, lane, out int d, out int k);
            bool ghostOld = v.GhostLane[i] >= 0 && PieceOf(n, old, v.GhostLane[i], out _, out _) >= 0;
            if (p < 0 && !ghostOld)
            {
                continue;
            }

            if (v.GhostLane[i] >= 0)
            {
                occ.GhostUnlink(i);   // смена полосы завершается мгновенно: машина остаётся на целевой
                v.LaneChangeDir[i] = 0;
                v.LaneChangeTicks[i] = 0;
                v.Lateral[i] = 0f;
            }

            if (p < 0)
            {
                continue;
            }

            float u = ToU(n, old[p], lane, d, v.S[i]);
            int nl = Target(n, neu, d, k, u, segmentsOnly: false, out float s);
            occ.Unlink(i);
            v.S[i] = s;
            occ.InsertSorted(nl, i);
            v.PointHint[i] = 0;
            v.NextConn[i] = -1;
            v.GateState[i] = 0;
            v.GrantConnector[i] = -1;
            v.Ticket[i] = 0;
            if (v.Route[i] >= 0)
            {
                RouteNav.ReleaseRoute(w, i);
            }
        }
    }

    private static void MoveDestinations(World w, ReadOnlySpan<ChainPiece> old, ReadOnlySpan<ChainPiece> neu)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int p = PieceOf(n, old, v.DestLane[i], out int d, out int k);
            if (p >= 0 && old[p].Seg >= 0)
            {
                float u = ToU(n, old[p], v.DestLane[i], d, v.DestS[i]);
                int nl = Target(n, neu, d, k, u, segmentsOnly: true, out float s);
                v.DestLane[i] = nl;
                v.DestS[i] = s;
            }
        }

        PendingTrips trips = w.Trips;
        for (int q = 0; q < trips.Count; q++)
        {
            PendingTrip t = trips.At(q);
            bool changed = false;
            int p = PieceOf(n, old, t.FromLane, out int d, out int k);
            if (p >= 0 && old[p].Seg >= 0)
            {
                float u = ToU(n, old[p], t.FromLane, d, t.FromS);
                t.FromLane = Target(n, neu, d, k, u, segmentsOnly: true, out t.FromS);
                changed = true;
            }

            p = PieceOf(n, old, t.DestLane, out d, out k);
            if (p >= 0 && old[p].Seg >= 0)
            {
                float u = ToU(n, old[p], t.DestLane, d, t.DestS);
                t.DestLane = Target(n, neu, d, k, u, segmentsOnly: true, out t.DestS);
                changed = true;
            }

            if (changed && t.HasRoute)
            {
                w.Routes.Free(new RouteHandle(t.Route, t.RouteGen));
                t.Route = -1;
                t.RouteGen = 0;
            }

            if (changed)
            {
                trips.Set(q, in t);
            }
        }
    }

    // Полосы зон на старых сегментах - на те же (направление, номер) первого нового сегмента; повторы убираются (слияние сводит две полосы в одну).
    private static void MoveZoneLanes(NetworkStore n, ReadOnlySpan<ChainPiece> old, ReadOnlySpan<ChainPiece> neu)
    {
        int firstSeg = 0;
        while (neu[firstSeg].Seg < 0)
        {
            firstSeg++;
        }

        for (int z = 0; z < n.ZoneCount; z++)
        {
            ref ZoneData zd = ref n.Zones[z];
            int cnt = zd.SpawnLaneCount;
            bool touched = false;
            for (int q = 0; q < cnt; q++)
            {
                int lane = n.ZoneLanes[zd.FirstSpawnLane + q];
                int p = PieceOf(n, old, lane, out int d, out int k);
                if (p >= 0 && old[p].Seg >= 0)
                {
                    int nl = LaneOf(n, neu, firstSeg, d, k);
                    n.ZoneLanes[zd.FirstSpawnLane + q] = nl >= 0 ? nl : LaneOf(n, neu, firstSeg, d, 0);
                    touched = true;
                }
            }

            if (!touched)
            {
                continue;
            }

            int write = 0;
            for (int q = 0; q < cnt; q++)
            {
                int lane = n.ZoneLanes[zd.FirstSpawnLane + q];
                bool dup = lane < 0;
                for (int r = 0; r < write && !dup; r++)
                {
                    dup = n.ZoneLanes[zd.FirstSpawnLane + r] == lane;
                }

                if (!dup)
                {
                    n.ZoneLanes[zd.FirstSpawnLane + write++] = lane;
                }
            }

            for (int q = write; q < cnt; q++)
            {
                n.ZoneLanes[zd.FirstSpawnLane + q] = 0;
            }

            zd.SpawnLaneCount = write;
        }
    }

    // Участки города вдоль старых сегментов - на новый сегмент по середине фасада (сторона и S - в осях нового сегмента). Производные пересчитает RestoreDerived.
    private static void MoveLots(CityWorld c, NetworkStore n, ReadOnlySpan<ChainPiece> old, ReadOnlySpan<ChainPiece> neu)
    {
        LotStore l = c.Lots;
        for (int lot = 0; lot < l.Count; lot++)
        {
            int p = -1;
            for (int q = 0; q < old.Length && p < 0; q++)
            {
                p = old[q].Seg >= 0 && l.Seg[lot] == old[q].Seg && l.SegGen[lot] == n.Segments[old[q].Seg].Generation ? q : -1;
            }

            if (p < 0 || (l.Flags[lot] & LotFlags.Lost) != 0)
            {
                continue;
            }

            ChainPiece o = old[p];
            float t0 = l.S0Mm[lot] * 0.001f;
            float t1 = l.S1Mm[lot] * 0.001f;
            float u0 = o.Forward ? o.U0 + t0 : o.U0 + o.Len - t0;
            float u1 = o.Forward ? o.U0 + t1 : o.U0 + o.Len - t1;
            int sideChain = o.Forward ? l.Side[lot] : 1 - l.Side[lot];
            float um = (u0 + u1) * 0.5f;
            int np = -1;
            float best = float.MaxValue;
            for (int q = 0; q < neu.Length; q++)
            {
                if (neu[q].Seg < 0)
                {
                    continue;
                }

                float dist = um < neu[q].U0 ? neu[q].U0 - um : (um > neu[q].U0 + neu[q].Len ? um - (neu[q].U0 + neu[q].Len) : 0f);
                if (dist < best)
                {
                    best = dist;
                    np = q;
                }
            }

            ChainPiece t = neu[np];
            float a = t.Forward ? u0 - t.U0 : t.U0 + t.Len - u0;
            float b = t.Forward ? u1 - t.U0 : t.U0 + t.Len - u1;
            float lo = Math.Clamp(MathF.Min(a, b), 0f, t.Len);
            float hi = Math.Clamp(MathF.Max(a, b), 0f, t.Len);
            int s0 = DetMath.ToIntSat(MathF.Round(lo * 1000f));
            int s1 = Math.Max(s0 + 1, DetMath.ToIntSat(MathF.Round(hi * 1000f)));
            l.Seg[lot] = t.Seg;
            l.SegGen[lot] = n.Segments[t.Seg].Generation;
            l.Side[lot] = (byte)(t.Forward ? sideChain : 1 - sideChain);
            l.S0Mm[lot] = s0;
            l.S1Mm[lot] = s1;
        }
    }

    // Connector'ы дальнего узла: полосы старого крайнего участка -> полосы нового (то же направление цепочки и номер); рукав - на новый сегмент; сигнал полосы переезжает.
    private static void RetargetFar(World w, int node, in ChainPiece oldEnd, in ChainPiece newEnd, int end)
    {
        NetworkStore n = w.Net;
        ref readonly NodeData nd = ref n.Nodes[node];
        if (nd.FirstArm < 0)
        {
            return;
        }

        Span<ChainPiece> o1 = stackalloc ChainPiece[1];
        Span<ChainPiece> n1 = stackalloc ChainPiece[1];
        o1[0] = oldEnd;
        n1[0] = newEnd;
        for (int a = 0; a < nd.ArmCount; a++)
        {
            if (n.NodeArms[nd.FirstArm + a] == oldEnd.Seg)
            {
                n.NodeArms[nd.FirstArm + a] = newEnd.Seg;
            }
        }

        ConnectorStore cs = w.Connectors;
        for (int q = 0; q < nd.ConnectorCount; q++)
        {
            int c = nd.FirstConnector + q;
            ref LaneData cl = ref n.Lanes[c];
            int from = Map(n, o1, n1, cl.FromLane);
            int to = Map(n, o1, n1, cl.ToLane);
            if (from != cl.FromLane)
            {
                w.LaneSignals[from] = w.LaneSignals[cl.FromLane];
            }

            cl.FromLane = from;
            cl.ToLane = to;
            for (int e = 0; e < cl.NextCount; e++)
            {
                n.LaneNext[cl.FirstNext + e] = Map(n, o1, n1, n.LaneNext[cl.FirstNext + e]);
            }

            int g = cl.ConflictGroup;
            if (g >= 0)
            {
                cs.InLane[g] = Map(n, o1, n1, cs.InLane[g]);
                cs.OutLane[g] = Map(n, o1, n1, cs.OutLane[g]);
            }
        }

        _ = end;
    }

    private static int Map(NetworkStore n, ReadOnlySpan<ChainPiece> o1, ReadOnlySpan<ChainPiece> n1, int lane)
    {
        int p = PieceOf(n, o1, lane, out int d, out int k);
        if (p < 0)
        {
            return lane;
        }

        int nl = LaneOf(n, n1, 0, d, k);
        return nl >= 0 ? nl : lane;
    }

    private static float Dist(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
