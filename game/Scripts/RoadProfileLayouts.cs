using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Сечения дорог для клиента (RP6, road-profiles.md §7): каталог профилей (<c>NetworkView.RoadProfiles/ProfileElems</c>) в метрах, по сечению на пару
/// (профиль, <c>Mirrored</c>) - элементы уже в системе A→B сегмента (<c>+x</c> - вправо), <c>forward</c> = A→B. Ядро отдаёт клиенту только
/// <c>SegmentData.Profile</c> и флаг <c>Mirrored</c>; каталог клиент берёт из дескриптора сценария (<see cref="Set"/>), поэтому дорога
/// без профиля (legacy, дорога игрока до RP7) или без каталога рисуется прежним путём. Метров в коде нет: всё из каталога.
/// </summary>
public sealed class RoadProfileLayouts
{
    /// <summary>Каталог текущего сценария; null - режим legacy.</summary>
    public static RoadProfileLayouts? Current { get; private set; }

    public readonly struct Elem
    {
        public Elem(RoadElemKind kind, bool backward, float x0, float x1)
        {
            Kind = kind;
            Backward = backward;
            X0 = x0;
            X1 = x1;
        }

        public RoadElemKind Kind { get; }

        /// <summary>Полоса встречного направления (B→A) в системе A→B сегмента.</summary>
        public bool Backward { get; }

        /// <summary>Левый и правый край от оси, м.</summary>
        public float X0 { get; }

        public float X1 { get; }

        public float Width => X1 - X0;
    }

    /// <summary>Сечение в системе A→B сегмента (после учёта <c>Mirrored</c>).</summary>
    public sealed class Layout
    {
        public Layout(Elem[] elems, float edge)
        {
            Elems = elems;
            Edge = edge;
            float l = float.MaxValue;
            float r = float.MinValue;
            WFwd = 0f;
            WBwd = 0f;
            CBaseAB = float.NaN;
            CBaseBA = float.NaN;
            BwdLeft = float.MaxValue;
            BwdRight = float.MinValue;
            FwdLeft = float.MaxValue;
            FwdRight = float.MinValue;
            foreach (Elem e in elems)
            {
                if (e.Kind != RoadElemKind.Lane)
                {
                    continue;
                }

                l = MathF.Min(l, e.X0);
                r = MathF.Max(r, e.X1);
                if (e.Backward)
                {
                    // полоса 0 встречного направления - крайняя левая (правая по ходу B→A)
                    if (e.X0 < BwdLeft)
                    {
                        BwdLeft = e.X0;
                        CBaseBA = (e.X0 + e.X1) * 0.5f;
                        WBwd = e.Width;
                    }

                    BwdRight = MathF.Max(BwdRight, e.X1);
                }
                else
                {
                    if (e.X1 > FwdRight)
                    {
                        FwdRight = e.X1;
                        CBaseAB = (e.X0 + e.X1) * 0.5f;
                        WFwd = e.Width;
                    }

                    FwdLeft = MathF.Min(FwdLeft, e.X0);
                }
            }

            CurbL = l == float.MaxValue ? 0f : -l;
            CurbR = r == float.MinValue ? 0f : r;
        }

        public Elem[] Elems { get; }

        /// <summary>Ось → край профиля, м.</summary>
        public float Edge { get; }

        /// <summary>Ось → внешний край крайней левой / правой полосы (кромка проезжей части), м.</summary>
        public float CurbL { get; }

        public float CurbR { get; }

        /// <summary>Центр полосы 0 направления A→B (крайняя правая) и B→A (крайняя левая в системе A→B); NaN - направления нет.</summary>
        public float CBaseAB { get; }

        public float CBaseBA { get; }

        /// <summary>Ширина полосы 0 направления A→B / B→A, м (0 - направления нет).</summary>
        public float WFwd { get; }

        public float WBwd { get; }

        public float FwdLeft { get; }

        public float FwdRight { get; }

        public float BwdLeft { get; }

        public float BwdRight { get; }

        public float Curb(bool left) => left ? CurbL : CurbR;

        /// <summary>Слои стороны от кромки проезжей части наружу, м: бордюр, до начала тротуара, до края профиля.</summary>
        public void SideLayers(bool left, out float curbEnd, out float walkStart, out float edge)
        {
            float curb = 0f;
            float walk = 0f;
            foreach (Elem e in Elems)
            {
                bool onSide = left ? e.X1 <= -CurbL + 1e-3f : e.X0 >= CurbR - 1e-3f;
                if (!onSide)
                {
                    continue;
                }

                if (e.Kind == RoadElemKind.Curb)
                {
                    curb += e.Width;
                }
                else if (e.Kind == RoadElemKind.Sidewalk)
                {
                    walk += e.Width;
                }
            }

            edge = MathF.Max(0f, Edge - Curb(left));
            curbEnd = MathF.Min(curb, edge);
            walkStart = MathF.Max(curbEnd, edge - walk);
        }
    }

    private readonly Layout[] _fwd;
    private readonly Layout[] _mir;

    private RoadProfileLayouts(in NetworkView v)
    {
        int n = v.RoadProfiles.Length;
        _fwd = new Layout[n];
        _mir = new Layout[n];
        for (int p = 0; p < n; p++)
        {
            RoadProfileData pd = v.RoadProfiles[p];
            int ne = pd.ElemCount;
            var f = new Elem[ne];
            var m = new Elem[ne];
            for (int e = 0; e < ne; e++)
            {
                RoadElemData el = v.ProfileElems[pd.FirstElem + e];
                var kind = (RoadElemKind)el.Kind;
                bool bwd = kind == RoadElemKind.Lane && (RoadLaneDir)el.Dir == RoadLaneDir.Backward;
                float x0 = el.OffsetMm * 0.001f;
                float x1 = (el.OffsetMm + el.WidthMm) * 0.001f;
                f[e] = new Elem(kind, bwd, x0, x1);
                m[ne - 1 - e] = new Elem(kind, kind == RoadElemKind.Lane && !bwd, -x1, -x0);
            }

            float edge = pd.WidthMm * 0.0005f;
            _fwd[p] = new Layout(f, edge);
            _mir[p] = new Layout(m, edge);
        }
    }

    /// <summary>Задать каталог из <c>NetworkView</c> (RP10b: верен и после Load без дескриптора); без каталога (<c>ModuleMm = 0</c>) - режим legacy.</summary>
    public static void Set(in NetworkView v) => Current = v.ModuleMm > 0 && v.RoadProfiles.Length > 0 ? new RoadProfileLayouts(in v) : null;

    public int Count => _fwd.Length;

    /// <summary>Сечение сегмента или null (legacy / сегмент без профиля / профиль вне каталога).</summary>
    public static Layout? ForSegment(in SegmentData sd)
    {
        RoadProfileLayouts? c = Current;
        if (c == null || sd.Profile < 0 || sd.Profile >= c.Count)
        {
            return null;
        }

        return (sd.Flags & SegmentFlags.Mirrored) != 0 ? c._mir[sd.Profile] : c._fwd[sd.Profile];
    }

    /// <summary>
    /// Зебра (VS4) идёт по кромкам проезжей части (<c>CurbL..CurbR</c>), а не между центрами тротуаров, как ребро перехода пешеходной сети
    /// (road-profiles.md §10): сколько отрезать у концов <paramref name="a"/> и <paramref name="b"/> ребра. Поперечная координата конца берётся от оси
    /// сегмента (проекция на ближайшую точку базовой полосы), отрез = расстояние от конца до кромки с его стороны.
    /// </summary>
    public static bool ZebraTrim(NetworkView net, int seg, Vec2 a, Vec2 b, out float trimA, out float trimB)
    {
        trimA = 0f;
        trimB = 0f;
        if (!net.IsSegmentAlive(seg))
        {
            return false;
        }

        SegmentData sd = net.Segments[seg];
        Layout? lay = ForSegment(in sd);
        if (lay == null)
        {
            return false;
        }

        bool baseAB = sd.LanesAB > 0;
        int lane = baseAB ? sd.FirstLaneAB : sd.FirstLaneBA;
        float cBase = baseAB ? lay.CBaseAB : lay.CBaseBA;
        if (!net.IsLaneAlive(lane) || net.Lanes[lane].PointCount < 2 || float.IsNaN(cBase))
        {
            return false;
        }

        LaneData ld = net.Lanes[lane];
        ReadOnlySpan<Vec2> pts = net.LanePoints.Slice(ld.FirstPoint, ld.PointCount);
        float mx = (a.X + b.X) * 0.5f;
        float mz = (a.Z + b.Z) * 0.5f;
        int best = 0;
        float bd = float.MaxValue;
        for (int k = 0; k < pts.Length; k++)
        {
            float dx = pts[k].X - mx;
            float dz = pts[k].Z - mz;
            float d2 = (dx * dx) + (dz * dz);
            if (d2 < bd)
            {
                bd = d2;
                best = k;
            }
        }

        MeshBuilder.EdgeNormal(pts, best, out float nx, out float nz);
        if (!baseAB)
        {
            nx = -nx;
            nz = -nz; // полилиния идёт B→A: нормаль в системе A→B противоположна
        }

        float la = (nx * (a.X - pts[best].X)) + (nz * (a.Z - pts[best].Z)) + cBase;
        float lb = (nx * (b.X - pts[best].X)) + (nz * (b.Z - pts[best].Z)) + cBase;
        float len = MathF.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));
        if (la * lb >= 0f || len < 1e-3f)
        {
            return false; // ребро не пересекает проезжую часть
        }

        trimA = len * Math.Clamp((la - (la > 0f ? lay.CurbR : -lay.CurbL)) / (la - lb), 0f, 1f);
        trimB = len * Math.Clamp((lb - (lb > 0f ? lay.CurbR : -lay.CurbL)) / (lb - la), 0f, 1f);
        return true;
    }
}
