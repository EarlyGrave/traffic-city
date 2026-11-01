using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tools;

// Профили дорог в инструментах (RP7, ADR-0030, road-profiles.md §6): разбор Profile/Mirrored в BuildRoad, проверка UpgradeRoad, наложение дорог (Overlap).
internal static partial class ToolValidator
{
    /// <summary>Допуск наложения, м: дороги, вплотную лежащие друг к другу (зазор 0), не считаются наложенными.</summary>
    private const float OverlapEps = 0.01f;

    // ---------------------------------------------------------------- BuildRoad: профиль

    /// <summary>
    /// <c>BuildRoadCmd.Profile</c> / бит <c>Mirrored</c>: профиль обязан быть в каталоге, полосы и тип - совпадать с профилем (с учётом <c>Mirrored</c>); <c>Mirrored</c> без профиля - отказ.
    /// Без профиля (<c>Profile = 0</c>) команда ведёт себя как раньше.
    /// </summary>
    private static RejectReason CheckBuildProfile(World w, in BuildRoadCmd b, ref ToolCheck c)
    {
        bool mirrored = (b.Flags & BuildRoadFlags.Mirrored) != 0;
        if (b.Profile == 0)
        {
            return mirrored ? RejectReason.InvalidArgument : RejectReason.None;
        }

        RoadProfileStore? ps = w.Net.Profiles;
        int p = b.Profile - 1;
        if (ps == null || p >= ps.Count || b.LanesAB != ps.LanesAB(p, mirrored) || b.LanesBA != ps.LanesBA(p, mirrored) || b.Kind != ps.Kind[p])
        {
            return RejectReason.InvalidArgument;
        }

        c.Profile = p;
        c.Mirrored = mirrored;
        return RejectReason.None;
    }

    // ---------------------------------------------------------------- UpgradeRoad

    /// <summary>
    /// <c>UpgradeRoad</c> (player-tools.md §4.5, RP7): скорость, тип, полосы и профиль сегмента. Ось сегмента не меняется. Правка полос (другое число полос или смена типа) требует пустого сегмента
    /// и пустых connector'ов его концов - иначе <c>Busy</c> (дренаж <c>ShrinkLanes</c> - T5); смена только геометрии профиля при тех же полосах идёт на месте и занятости не требует.
    /// Сегмент с профилем меняет полосы и тип только профилем (инвариант RP2: полосы, тип и ширина полос согласованы с каталогом).
    /// </summary>
    private static RejectReason CheckUpgradeRoad(World w, in Command cmd, ref ToolCheck c)
    {
        UpgradeRoadCmd u = cmd.UpgradeRoad;
        NetworkStore n = w.Net;
        if ((u.Flags & ~UpgradeRoadFlags.Supported) != 0)
        {
            return RejectReason.InvalidArgument;
        }

        if (!n.ResolveSegment(in u.Seg))
        {
            return RejectReason.InvalidHandle;
        }

        int seg = u.Seg.Index;
        c.Seg = seg;
        ref readonly SegmentData sd = ref n.Segments[seg];
        Vec2 mid = n.SegmentPoints[sd.FirstPoint + (sd.PointCount / 2)];
        Mark(ref c, EntityKind.Segment, seg, sd.Generation, mid);
        int shrinkOp = w.Ops.FindShrink(seg);
        if ((sd.Flags & SegmentFlags.Closing) != 0 || (shrinkOp < 0 && w.Ops.FindAny(seg) >= 0))
        {
            return RejectReason.Busy;
        }

        if (!float.IsFinite(u.SpeedLimit) || u.SpeedLimit < 0f)
        {
            return RejectReason.InvalidArgument;
        }

        if (w.Queue != null)
        {
            return RejectReason.NotSupported;   // Q-meso не умеет перестраивать построенный узел
        }

        float speed = u.SpeedLimit == 0f ? sd.SpeedLimit : u.SpeedLimit;
        bool keepKind = (u.Flags & UpgradeRoadFlags.KeepKind) != 0;
        bool mirrored = (u.Flags & UpgradeRoadFlags.Mirrored) != 0;
        int profile = sd.Profile;
        bool curMirrored = (sd.Flags & SegmentFlags.Mirrored) != 0;
        int lanesAB = sd.LanesAB;
        int lanesBA = sd.LanesBA;
        RoadKind kind = keepKind ? sd.Kind : u.Kind;
        if (u.Profile != 0)
        {
            RoadProfileStore? ps = n.Profiles;
            int p = u.Profile - 1;
            if (ps == null || p >= ps.Count)
            {
                return RejectReason.InvalidArgument;
            }

            lanesAB = ps.LanesAB(p, mirrored);
            lanesBA = ps.LanesBA(p, mirrored);
            if ((u.LanesAB != 255 && u.LanesAB != lanesAB) || (u.LanesBA != 255 && u.LanesBA != lanesBA) || (!keepKind && u.Kind != ps.Kind[p]))
            {
                return RejectReason.InvalidArgument;
            }

            kind = ps.Kind[p];
            profile = p;
            curMirrored = mirrored;
        }
        else
        {
            bool noProfile = (u.Flags & UpgradeRoadFlags.NoProfile) != 0;
            if (mirrored)
            {
                return RejectReason.InvalidArgument;
            }

            if (noProfile)
            {
                profile = -1;   // снять профиль: полосы и тип - из команды (обратная к «без профиля -> профиль»)
                curMirrored = false;
            }

            if (u.LanesAB != 255)
            {
                lanesAB = u.LanesAB;
            }

            if (u.LanesBA != 255)
            {
                lanesBA = u.LanesBA;
            }

            if (lanesAB + lanesBA == 0 || lanesAB > NetworkBuilder.MaxLanesPerDirection || lanesBA > NetworkBuilder.MaxLanesPerDirection)
            {
                return RejectReason.InvalidArgument;
            }

            if (!noProfile && sd.Profile >= 0 && (lanesAB != sd.LanesAB || lanesBA != sd.LanesBA || kind != sd.Kind))
            {
                return RejectReason.InvalidArgument;   // полосы и тип сегмента с профилем - только через профиль
            }
        }

        NetworkBuilder bld = w.Builder;
        ReshapeMode mode = bld.PlanReshape(seg, profile, curMirrored, lanesAB, lanesBA, speed, kind);
        if (shrinkOp >= 0)
        {
            // Сегмент в дренаже полос (T5): разрешено только вернуть прежние полосы (отмена операции, undo) и сменить скорость; остальное - после финала.
            if (mode != ReshapeMode.None && mode != ReshapeMode.Speed)
            {
                return RejectReason.Busy;
            }

            c.CancelShrink = true;
            c.Seg = seg;
            c.Profile = profile;
            c.Mirrored = curMirrored;
            c.LanesAB = (byte)lanesAB;
            c.LanesBA = (byte)lanesBA;
            c.Kind = kind;
            c.Speed = speed;
            c.Reshape = mode;
            c.LengthM = bld.FullAxisLength(seg);
            return RejectReason.None;
        }

        c.Profile = profile;
        c.Mirrored = curMirrored;
        c.LanesAB = (byte)lanesAB;
        c.LanesBA = (byte)lanesBA;
        c.Kind = kind;
        c.Speed = speed;
        c.Reshape = mode;
        c.LengthM = bld.FullAxisLength(seg);
        c.Mechanism = (byte)(mode == ReshapeMode.Swap ? 1 : 0);
        if (mode == ReshapeMode.None)
        {
            return RejectReason.None;   // ничего не меняется: применится без последствий
        }

        // Полосы или тип: connector'ы концов перестраиваются (другая пара полос). Занятый сегмент (T5): уцелевшие полосы переносятся с машинами, убираемые и обречённые занятые
        // connector'ы сначала пустеют - операция ShrinkLanes (дренаж), правка - в её финале.
        bool layout = mode == ReshapeMode.Swap || kind != sd.Kind;
        if (layout && mode == ReshapeMode.Swap && HasBusStop(n, seg))
        {
            return RejectReason.NotSupported;   // остановка на сегменте - до T9
        }

        // Односторонний результат не должен оставлять въезд без выезда.
        if (mode != ReshapeMode.Speed && (lanesAB == 0) != (lanesBA == 0))
        {
            int into = lanesAB > 0 ? sd.NodeB : sd.NodeA;
            if (!HasExit(n, into, seg))
            {
                Mark(ref c, EntityKind.Node, into, n.Nodes[into].Generation, n.Nodes[into].Position);
                return RejectReason.CreatesTrap;
            }
        }

        if (mode == ReshapeMode.Speed)
        {
            c.Cost = 0;
            return RejectReason.None;
        }

        // Ёмкости: новый блок полос и точек (для Swap), затем перестройка узлов концов (план по новым полосам - пробным планированием).
        int total = lanesAB + lanesBA;
        if (mode == ReshapeMode.Swap)
        {
            if (!n.CanAllocBlock(n.LaneArena, total))
            {
                c.CapKind = CapacityKind.Lanes;
                return RejectReason.Capacity;
            }

            if (!n.CanAllocBlock(n.LanePointArena, total * sd.PointCount))
            {
                c.CapKind = CapacityKind.LanePoints;
                return RejectReason.Capacity;
            }
        }

        RejectReason rb = CheckRebuildEnds(w, seg, lanesAB, lanesBA, kind, ref c);
        if (rb != RejectReason.None)
        {
            return rb;
        }

        if (layout)
        {
            c.Drain = bld.VanishingOccupancy(w, seg, lanesAB, lanesBA);
            NetworkStatus ends = bld.PlanReshapeEnds(w, seg, lanesAB, lanesBA, kind, close: false, out int doomedBusy);
            if (ends != NetworkStatus.Ok)
            {
                c.CapKind = CapacityKind.Lanes;
                return RejectOf(ends);
            }

            c.Drain += doomedBusy;
            if (c.Drain > 0)
            {
                if (w.Ops.Count >= w.Ops.Capacity)
                {
                    return RejectReason.Capacity;
                }

                c.Mechanism = 2;
            }
        }

        // Геометрия профиля: наложение на соседние дороги (только для профиля, вышедшего за прежний край).
        if (profile >= 0 && n.Profiles != null)
        {
            float edge = n.Profiles.EdgeMm[profile] * 0.001f;
            float oldEdge = sd.Profile >= 0 ? n.Profiles.EdgeMm[sd.Profile] * 0.001f : 0f;
            if (edge > oldEdge)
            {
                Span<Vec2> axis = stackalloc Vec2[NetworkBuilder.MaxVia + 2];
                axis = axis[..sd.PointCount];
                for (int i = 0; i < axis.Length; i++)
                {
                    axis[i] = n.SegmentPoints[sd.FirstPoint + i];
                }

                RejectReason ov = CheckFootprint(w, axis, edge, sd.NodeA, sd.NodeB, seg, ref c);
                if (ov != RejectReason.None)
                {
                    return ov;
                }
            }
        }

        // Стоимость: новая дорога минус возврат за заменяемую.
        bool bridge = (sd.Flags & SegmentFlags.Bridge) != 0;   // TR3: мост остаётся мостом
        long add = RoadCost(in w.ToolsCfg.Costs, kind, c.LengthM, total, (sd.Flags & SegmentFlags.Curve) != 0, bridge);
        long old = RoadCost(in w.ToolsCfg.Costs, sd.Kind, c.LengthM, sd.LanesAB + sd.LanesBA, (sd.Flags & SegmentFlags.Curve) != 0, bridge);
        c.Cost = add - (old * w.ToolsCfg.Costs.UpgradeRefundPct / 100);
        return RejectReason.None;
    }

    // Пробное планирование перестройки обоих концов при новых полосах и типе сегмента (SegmentData правится на время пробы и возвращается): лимит connector'ов узла и ёмкости арен.
    private static RejectReason CheckRebuildEnds(World w, int seg, int lanesAB, int lanesBA, RoadKind kind, ref ToolCheck c)
    {
        NetworkStore n = w.Net;
        ref SegmentData sd = ref n.Segments[seg];
        byte oldAB = sd.LanesAB;
        byte oldBA = sd.LanesBA;
        RoadKind oldKind = sd.Kind;
        sd.LanesAB = (byte)lanesAB;
        sd.LanesBA = (byte)lanesBA;
        sd.Kind = kind;
        RejectReason result = RejectReason.None;
        for (int e = 0; e < 2 && result == RejectReason.None; e++)
        {
            int node = e == 0 ? sd.NodeA : sd.NodeB;
            if (n.Nodes[node].FirstArm < 0)
            {
                continue;
            }

            NetworkStatus st = w.Builder.PlanRebuild(w, node, rejectTrap: false, out RebuildPlan rp);
            if (st == NetworkStatus.Ok && !RebuildFits(w, in rp))
            {
                c.CapKind = CapacityKind.Lanes;
                result = RejectReason.Capacity;
            }
            else if (st != NetworkStatus.Ok && st != NetworkStatus.Busy)
            {
                // Busy при пробе не причина: обречённые connector'ы сегмента пусты (проверено выше), остальные переживают перестройку.
                result = RejectOf(st);
            }
        }

        sd.LanesAB = oldAB;
        sd.LanesBA = oldBA;
        sd.Kind = oldKind;
        return result;
    }

    // Те же условия ёмкости, что в начале CommitRebuild.
    private static bool RebuildFits(World w, in RebuildPlan rp)
    {
        NetworkStore s = w.Net;
        int samples = w.Builder.Options.ConnectorSamples;
        return s.CanAllocBlock(s.ArmArena, rp.Arms) && s.CanAllocBlock(s.LaneArena, rp.Plan) && s.CanAllocBlock(s.LanePointArena, rp.Plan * samples) &&
            s.CanAllocBlock(s.LaneNextArena, 2 * rp.Plan) && (rp.Plan == 0 || s.CanAllocBlock(w.Connectors.RowArena, rp.Plan));
    }

    // Есть ли у узла выезд по живому незакрывающемуся сегменту, кроме skipSeg.
    private static bool HasExit(NetworkStore n, int node, int skipSeg)
    {
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            int seg = code >> 1;
            if (seg == skipSeg)
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[seg];
            if ((sd.Flags & SegmentFlags.Closing) != 0)
            {
                continue;
            }

            if (((code & 1) == 0 ? sd.LanesAB : sd.LanesBA) > 0)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- Overlap

    /// <summary>
    /// Наложение (road-profiles.md §6): ось с краем <paramref name="edge"/> (половина ширины профиля, м) ближе суммы краёв к оси соседнего сегмента. У общих узлов осевая линия обрезается на
    /// радиус «диска узла»: полосы рукавов под углом θ перестают налегать на расстоянии <c>(En + Ee) / (2 sin(θ/2))</c> от узла, но не дальше <c>2 (En + Ee)</c> - более острые стыки (~29 градусов и меньше) отклоняются. Параллельные дороги вплотную друг к другу допустимы.
    /// <paramref name="axis"/> - ось нового (или правимого) сегмента между отступами его концов; <paramref name="self"/> - правимый сегмент (его пропустить) или -1.
    /// </summary>
    private static RejectReason CheckFootprint(World w, ReadOnlySpan<Vec2> axis, float edge, int nodeA, int nodeB, int self, ref ToolCheck c)
    {
        NetworkStore n = w.Net;
        int m = axis.Length;
        Vec2 uA = Unit(axis[0], axis[1]);
        Vec2 uB = Unit(axis[m - 1], axis[m - 2]);
        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxZ = float.MinValue;
        for (int i = 0; i < m; i++)
        {
            minX = MathF.Min(minX, axis[i].X);
            maxX = MathF.Max(maxX, axis[i].X);
            minZ = MathF.Min(minZ, axis[i].Z);
            maxZ = MathF.Max(maxZ, axis[i].Z);
        }

        Span<Vec2> mine = stackalloc Vec2[NetworkBuilder.MaxVia + 2];
        Span<Vec2> theirs = stackalloc Vec2[NetworkBuilder.MaxVia + 2];
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (s == self || !n.SegmentAlive(s))
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[s];
            float ee = LotBuilder.FrontOffsetM(w, in sd);
            float reach = edge + ee;
            int p0 = sd.FirstPoint;
            int tm = sd.PointCount;
            float sMinX = float.MaxValue;
            float sMinZ = float.MaxValue;
            float sMaxX = float.MinValue;
            float sMaxZ = float.MinValue;
            for (int i = 0; i < tm; i++)
            {
                Vec2 q = n.SegmentPoints[p0 + i];
                sMinX = MathF.Min(sMinX, q.X);
                sMaxX = MathF.Max(sMaxX, q.X);
                sMinZ = MathF.Min(sMinZ, q.Z);
                sMaxZ = MathF.Max(sMaxZ, q.Z);
            }

            if (sMinX > maxX + reach || sMaxX < minX - reach || sMinZ > maxZ + reach || sMaxZ < minZ - reach)
            {
                continue;
            }

            for (int i = 0; i < m; i++)
            {
                mine[i] = axis[i];
            }

            for (int i = 0; i < tm; i++)
            {
                theirs[i] = n.SegmentPoints[p0 + i];
            }

            int mStart = 0;
            int mEnd = m;
            int tStart = 0;
            int tEnd = tm;

            // Общие узлы: обе оси начинаются не ближе радиуса диска пары.
            for (int e = 0; e < 2; e++)
            {
                int mine0 = e == 0 ? nodeA : nodeB;
                if (mine0 < 0)
                {
                    continue;
                }

                for (int f = 0; f < 2; f++)
                {
                    int their0 = f == 0 ? sd.NodeA : sd.NodeB;
                    if (their0 != mine0)
                    {
                        continue;
                    }

                    Vec2 x = n.Nodes[mine0].Position;
                    Vec2 vm = e == 0 ? uA : uB;
                    Vec2 vt = f == 0 ? Unit(theirs[0], theirs[1]) : Unit(theirs[tm - 1], theirs[tm - 2]);
                    float cos = (vm.X * vt.X) + (vm.Z * vt.Z);
                    float sinHalf = MathF.Sqrt(MathF.Max(0f, (1f - cos) * 0.5f));
                    float r = sinHalf > 1e-3f ? MathF.Min(reach / (2f * sinHalf), 2f * reach) : 2f * reach;   // острее ~29 градусов диск не растёт: наложение остаётся
                    if (e == 0)
                    {
                        mStart = TrimFrom(mine, m, true, x, r, mStart);
                    }
                    else
                    {
                        mEnd = TrimFrom(mine, m, false, x, r, mEnd);
                    }

                    if (f == 0)
                    {
                        tStart = TrimFrom(theirs, tm, true, x, r, tStart);
                    }
                    else
                    {
                        tEnd = TrimFrom(theirs, tm, false, x, r, tEnd);
                    }
                }
            }

            if (mEnd - mStart < 2 || tEnd - tStart < 2)
            {
                continue;   // диск узла съел всю ось
            }

            float best = float.MaxValue;
            for (int i = mStart; i + 1 < mEnd; i++)
            {
                for (int j = tStart; j + 1 < tEnd; j++)
                {
                    best = MathF.Min(best, SegmentDistance(mine[i], mine[i + 1], theirs[j], theirs[j + 1]));
                }
            }

            if (best < reach - OverlapEps)
            {
                Mark(ref c, EntityKind.Segment, s, sd.Generation, n.SegmentPoints[p0 + (tm / 2)]);
                return RejectReason.Overlap;
            }
        }

        return RejectReason.None;
    }

    // Обрезка начала (fromStart) или конца ломаной на расстояние r от узла x: точка пересчитывается на ближайшую допустимую вершину; возвращает новую границу диапазона вершин.
    // Вершины, оказавшиеся внутри диска, отбрасываются; граничная точка сдвигается вдоль звена ровно на диск.
    private static int TrimFrom(Span<Vec2> pts, int count, bool fromStart, Vec2 x, float r, int bound)
    {
        if (fromStart)
        {
            int i = bound;
            while (i + 1 < count && Dist(pts[i], x) < r)
            {
                Vec2 q = pts[i + 1];
                if (Dist(q, x) >= r)
                {
                    pts[i] = Advance(pts[i], q, x, r);
                    return i;
                }

                i++;
            }

            return i;
        }

        int j = bound - 1;
        while (j - 1 >= 0 && Dist(pts[j], x) < r)
        {
            Vec2 q = pts[j - 1];
            if (Dist(q, x) >= r)
            {
                pts[j] = Advance(pts[j], q, x, r);
                return j + 1;
            }

            j--;
        }

        return j + 1;
    }

    // Точка на звене p -> q, где расстояние до x впервые достигает r (бисекция по 20 шагов: детерминированна и дёшева, без квадратного уравнения).
    private static Vec2 Advance(Vec2 p, Vec2 q, Vec2 x, float r)
    {
        float lo = 0f;
        float hi = 1f;
        for (int k = 0; k < 20; k++)
        {
            float mid = (lo + hi) * 0.5f;
            Vec2 t = p + ((q - p) * mid);
            if (Dist(t, x) < r)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return p + ((q - p) * hi);
    }

    private static Vec2 Unit(Vec2 from, Vec2 to)
    {
        float dx = to.X - from.X;
        float dz = to.Z - from.Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        return len > 0f ? new Vec2(dx / len, dz / len) : default;
    }

    private static float SegmentDistance(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1)
    {
        if (SegmentsCross(a0, a1, b0, b1))
        {
            return 0f;
        }

        return MathF.Min(MathF.Min(PointSegment(a0, b0, b1), PointSegment(a1, b0, b1)), MathF.Min(PointSegment(b0, a0, a1), PointSegment(b1, a0, a1)));
    }

    private static bool SegmentsCross(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1)
    {
        float d1 = Cross(b0, b1, a0);
        float d2 = Cross(b0, b1, a1);
        float d3 = Cross(a0, a1, b0);
        float d4 = Cross(a0, a1, b1);
        return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
    }

    private static float Cross(Vec2 o, Vec2 a, Vec2 b) => ((a.X - o.X) * (b.Z - o.Z)) - ((a.Z - o.Z) * (b.X - o.X));

    private static float PointSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float len2 = (dx * dx) + (dz * dz);
        float t = len2 > 1e-12f ? Math.Clamp((((p.X - a.X) * dx) + ((p.Z - a.Z) * dz)) / len2, 0f, 1f) : 0f;
        return Dist(p, new Vec2(a.X + (dx * t), a.Z + (dz * t)));
    }
}
