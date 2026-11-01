using System;
using Sim.Core.Api;
using Sim.Core.Terrain;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>Биты <see cref="TerrainStore.Lock"/> (terrain.md §2.1): Road 1, NodePad 2, Building 4 (TR6), BridgeEnd 8.</summary>
internal static class TerrainLock
{
    public const byte Road = 1;
    public const byte NodePad = 2;
    public const byte Building = 4;
    public const byte BridgeEnd = 8;

    /// <summary>Биты, которые ставит сеть (TR3); <see cref="Building"/> - здания (TR6), пересчёт сети его не трогает.</summary>
    public const byte Network = Road | NodePad | BridgeEnd;
}

/// <summary>Служебное состояние дорог на рельефе (производное, вне хэша и сейва): версия сети последней синхронизации и scratch для сравнения <c>Lock</c>.</summary>
internal sealed class RoadTerrainState
{
    public int NetVersion = int.MinValue;
    public SampleRect Pending;   // штампы команд, ещё не учтённые в Version/грязных чанках (сбрасывает Sync)
    public readonly byte[] LockScratch;

    public RoadTerrainState(int samples) => LockScratch = new byte[samples];
}

/// <summary>Продольный профиль одной дороги по полной оси (от узла A через точки оси к узлу B), terrain.md §4.2: плоские площадки у концов, между ними - линейно.</summary>
internal struct RoadAxis
{
    public int YaCm, YbCm;      // абсолютные см у узлов A и B
    public float SetA, SetB;    // длина плоской площадки у конца по оси, м
    public float Full;          // длина полной оси, м
    public float Hw;            // полуширина коридора, м
    public bool Bridge;

    /// <summary>Высота оси в точке <paramref name="s"/> (м от узла A), абсолютные см.</summary>
    public readonly int YCmAt(float s)
    {
        if (s <= SetA)
        {
            return YaCm;
        }

        if (s >= Full - SetB)
        {
            return YbCm;
        }

        float mid = Full - SetA - SetB;

        float t = (s - SetA) / mid;
        return YaCm + DetMath.ToIntSat(MathF.Round((YbCm - YaCm) * t));
    }
}

/// <summary>Площадка узла для штампа: диск <see cref="R"/> (радиус площадки + наибольшая полуширина рукавов) на высоте узла.</summary>
internal struct RoadPad
{
    public Vec2 C;
    public float R;
    public int YCm;
}

/// <summary>Прямоугольник изменённых отсчётов (включительно).</summary>
internal struct SampleRect
{
    public int X0, Z0, X1, Z1;
    public bool Any;

    public void Add(int ix, int iz)
    {
        if (!Any)
        {
            X0 = X1 = ix;
            Z0 = Z1 = iz;
            Any = true;
            return;
        }

        X0 = Math.Min(X0, ix);
        Z0 = Math.Min(Z0, iz);
        X1 = Math.Max(X1, ix);
        Z1 = Math.Max(Z1, iz);
    }

    public void Union(in SampleRect r)
    {
        if (r.Any)
        {
            Add(r.X0, r.Z0);
            Add(r.X1, r.Z1);
        }
    }
}

/// <summary>
/// Дороги на рельефе (terrain.md §4, TR3): высоты узлов, штамп коридора и площадок (насыпи и выемки), объём земляных работ, <c>Lock</c> по сети и синхронизация
/// производных после правок. Всё только при рельефе (<c>w.Terrain != null</c>); без рельефа ничего не вызывается и мир побитово прежний.
/// Арифметика высот - целые см; проекция на ось - <c>float</c> в фиксированном порядке. 0 Б (стек и заранее выделенные массивы).
/// </summary>
internal static class RoadTerrain
{
    /// <summary>Наибольшая ширина полосы откоса за краем коридора, м (§4.4: <c>bank = min(16 м, |ΔY|/k)</c> - зажим сам гаснет, где перепад мал).</summary>
    public const float BankMaxM = 16f;

    /// <summary>Длина устоя моста от каждого конца, м (§4.5): там штамп как у дороги и бит <see cref="TerrainLock.BridgeEnd"/>.</summary>
    public const float AbutmentM = 16f;

    /// <summary>Добавка к полуширине legacy-коридора (тротуар и обочина), м.</summary>
    public const float LegacyShoulderM = 1.5f;

    /// <summary>Сколько точек полной оси помещается в стековый буфер (<c>MaxVia</c> + концы и запас).</summary>
    public const int MaxAxisPoints = NetworkBuilder.MaxVia + 8;

    public static int Cm(float elevationM) => DetMath.ToIntSat(MathF.Round(elevationM * 100f));

    public static float Meters(int cm) => cm / 100f;

    /// <summary>Полуширина коридора (§4.4): <c>WidthMm / 2</c> профиля, в legacy <c>(LanesAB + LanesBA) · LaneWidth / 2 + 1.5 м</c>.</summary>
    public static float HalfWidth(NetworkStore n, in NetworkOptions o, int profile, int lanes) =>
        profile >= 0 && n.Profiles != null ? n.Profiles.WidthMm[profile] * 0.0005f : (lanes * o.LaneWidth * 0.5f) + LegacyShoulderM;

    public static float HalfWidth(NetworkStore n, in NetworkOptions o, in SegmentData sd) => HalfWidth(n, in o, sd.Profile, sd.LanesAB + sd.LanesBA);

    /// <summary>Предел уклона, ‰ (§4.3): <c>max_grade_pct</c> профиля, иначе <c>terrain.max_grade_pct</c> по виду дороги.</summary>
    public static int MaxGradePm(World w, int profile, RoadKind kind)
    {
        RoadProfileStore? p = w.Net.Profiles;
        if (profile >= 0 && p != null && p.MaxGradePm[profile] > 0)
        {
            return p.MaxGradePm[profile];
        }

        return kind == RoadKind.Arterial ? w.TerrainCfg.MaxGradeArterialPm : w.TerrainCfg.MaxGradeStreetPm;
    }

    /// <summary>Полная ось сегмента: узел A, точки оси, узел B. Возвращает число точек (не больше <paramref name="dst"/>).</summary>
    public static int FullAxis(NetworkStore n, in SegmentData sd, Span<Vec2> dst)
    {
        int m = Math.Min(sd.PointCount, dst.Length - 2);
        dst[0] = n.Nodes[sd.NodeA].Position;
        for (int k = 0; k < m; k++)
        {
            dst[1 + k] = n.SegmentPoints[sd.FirstPoint + k];
        }

        dst[m + 1] = n.Nodes[sd.NodeB].Position;
        return m + 2;
    }

    /// <summary>Профиль существующего сегмента по его текущей геометрии и высотам узлов.</summary>
    public static RoadAxis AxisOf(NetworkStore n, in NetworkOptions o, in SegmentData sd, ReadOnlySpan<Vec2> axis)
    {
        float full = 0f;
        for (int k = 1; k < axis.Length; k++)
        {
            full += Dist(axis[k - 1], axis[k]);
        }

        return new RoadAxis
        {
            YaCm = Cm(n.Nodes[sd.NodeA].Elevation),
            YbCm = Cm(n.Nodes[sd.NodeB].Elevation),
            SetA = axis.Length > 1 ? Dist(axis[0], axis[1]) : 0f,
            SetB = axis.Length > 1 ? Dist(axis[^1], axis[^2]) : 0f,
            Full = full,
            Hw = HalfWidth(n, in o, in sd),
            Bridge = (sd.Flags & SegmentFlags.Bridge) != 0,
        };
    }

    /// <summary>Высота оси сегмента <paramref name="seg"/> в точке полной оси <paramref name="s"/> (м от узла A), абсолютные см: высота узла разреза (§4.1).</summary>
    public static int SegmentYCm(World w, int seg, float s)
    {
        NetworkStore n = w.Net;
        ref readonly SegmentData sd = ref n.Segments[seg];
        Span<Vec2> axis = stackalloc Vec2[MaxAxisPoints];
        int an = FullAxis(n, in sd, axis);
        RoadAxis ax = AxisOf(n, w.Builder.Options, in sd, axis[..an]);
        return ax.YCmAt(s);
    }

    /// <summary>Площадка узла: радиус - наибольший из <c>Radius</c> и отступов рукавов, полуширина - наибольшая по рукавам и <paramref name="extraHw"/>.</summary>
    public static RoadPad PadOf(NetworkStore n, in NetworkOptions o, int node, float extraHw)
    {
        ref readonly NodeData nd = ref n.Nodes[node];
        float r = nd.Radius;
        float hw = extraHw;
        for (int a = 0; a < nd.ArmCount; a++)
        {
            ref readonly SegmentData sd = ref n.Segments[n.NodeArms[nd.FirstArm + a]];
            hw = MathF.Max(hw, HalfWidth(n, in o, in sd));
            Vec2 near = sd.NodeA == node ? n.SegmentPoints[sd.FirstPoint] : n.SegmentPoints[sd.FirstPoint + sd.PointCount - 1];
            r = MathF.Max(r, Dist(nd.Position, near));
        }

        return new RoadPad { C = nd.Position, R = r + hw, YCm = Cm(nd.Elevation) };
    }

    // ---------------------------------------------------------------- штамп (§4.4)

    /// <summary>
    /// Штамп коридора оси <paramref name="axis"/> и площадок <paramref name="pads"/> (по порядку: сначала сегмент, затем площадки) в карту высот. <paramref name="apply"/> = false -
    /// только счёт (превью и валидатор). Мокрые отсчёты не трогаются; при <paramref name="respectLock"/> - и заблокированные (дороги и здания). Возвращает <c>Σ|ΔGround|</c>, см.
    /// </summary>
    public static long Stamp(TerrainStore t, ReadOnlySpan<Vec2> axis, in RoadAxis ax, ReadOnlySpan<RoadPad> pads, int bankSlopePm, bool apply, bool respectLock, ref SampleRect changed)
    {
        float reach = ax.Hw + BankMaxM;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int k = 0; k < axis.Length; k++)
        {
            minX = MathF.Min(minX, axis[k].X - reach);
            minZ = MathF.Min(minZ, axis[k].Z - reach);
            maxX = MathF.Max(maxX, axis[k].X + reach);
            maxZ = MathF.Max(maxZ, axis[k].Z + reach);
        }

        for (int k = 0; k < pads.Length; k++)
        {
            float pr = pads[k].R + BankMaxM;
            minX = MathF.Min(minX, pads[k].C.X - pr);
            minZ = MathF.Min(minZ, pads[k].C.Z - pr);
            maxX = MathF.Max(maxX, pads[k].C.X + pr);
            maxZ = MathF.Max(maxZ, pads[k].C.Z + pr);
        }

        Range(t, minX, maxX, t.OriginX, t.SamplesX, out int ix0, out int ix1);
        Range(t, minZ, maxZ, t.OriginZ, t.SamplesZ, out int iz0, out int iz1);
        float kCm = bankSlopePm / 10f;   // см на метр по горизонтали
        long sum = 0;
        for (int iz = iz0; iz <= iz1; iz++)
        {
            float pz = t.OriginZ + (iz * t.CellM);
            int row = iz * t.SamplesX;
            for (int ix = ix0; ix <= ix1; ix++)
            {
                int i = row + ix;
                if (t.Water[i] > t.Ground[i] || (respectLock && t.Lock[i] != 0))
                {
                    continue;
                }

                var p = new Vec2(t.OriginX + (ix * t.CellM), pz);
                int g0 = t.Ground[i];
                int g = g0;
                Project(axis, p, out float d, out float s);
                if (d <= reach && !(ax.Bridge && s > AbutmentM && s < ax.Full - AbutmentM))
                {
                    g = Rule(g, ax.YCmAt(s) - t.BaseCm, d, ax.Hw, kCm);
                }

                for (int k = 0; k < pads.Length; k++)
                {
                    float dp = Dist(pads[k].C, p);
                    if (dp <= pads[k].R + BankMaxM)
                    {
                        g = Rule(g, pads[k].YCm - t.BaseCm, dp, pads[k].R, kCm);
                    }
                }

                if (g != g0)
                {
                    sum += Math.Abs(g - g0);
                    changed.Add(ix, iz);
                    if (apply)
                    {
                        t.Ground[i] = (ushort)g;
                    }
                }
            }
        }

        return sum;
    }

    // Правило отсчёта: в коридоре (d <= hw) - высота оси, в полосе откоса - зажим в [Y - (d - hw)·k, Y + (d - hw)·k]. Результат в [0, 65535].
    private static int Rule(int g, int y, float d, float hw, float kCm)
    {
        int v;
        if (d <= hw)
        {
            v = y;
        }
        else
        {
            if (d > hw + BankMaxM)
            {
                return g;
            }

            int e = DetMath.ToIntSat(MathF.Round((d - hw) * kCm));
            v = Math.Clamp(g, y - e, y + e);
        }

        return Math.Clamp(v, 0, ushort.MaxValue);
    }

    /// <summary>Объём земляных работ, м³ (вверх): <c>Σ|ΔGround| · cell² / 100</c>.</summary>
    public static long VolumeM3(TerrainStore t, long sumCm)
    {
        long cell2 = (long)t.CellMm * t.CellMm;   // мм²
        long num = sumCm * cell2;                 // см·мм² ; 1 м³ = 100 см · 10^6 мм²
        const long Per = 100L * 1_000_000L;
        return (num + Per - 1) / Per;
    }

    // ---------------------------------------------------------------- Lock (§7.4)

    /// <summary>
    /// Полный пересчёт битов сети в <c>Lock</c> (Road, NodePad, BridgeEnd; Building не трогается). Изменился - прямоугольник изменений в <paramref name="changed"/>; true - что-то изменилось.
    /// </summary>
    public static bool RebuildLock(World w, ref SampleRect changed)
    {
        TerrainStore t = w.Terrain!;
        NetworkStore n = w.Net;
        RoadTerrainState st = n.TerrainState!;
        byte[] lk = t.Lock;
        byte[] old = st.LockScratch;
        Array.Copy(lk, old, lk.Length);
        for (int i = 0; i < lk.Length; i++)
        {
            lk[i] = (byte)(lk[i] & ~TerrainLock.Network);
        }

        NetworkOptions o = w.Builder.Options;
        Span<Vec2> axis = stackalloc Vec2[MaxAxisPoints];
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[s];
            int an = FullAxis(n, in sd, axis);
            RoadAxis ax = AxisOf(n, in o, in sd, axis[..an]);
            MarkCorridor(t, axis[..an], in ax);
        }

        for (int i = 0; i < n.NodeCount; i++)
        {
            if (n.NodeAlive(i) && n.Nodes[i].ArmCount > 0)
            {
                RoadPad pad = PadOf(n, in o, i, 0f);
                MarkDisc(t, pad.C, pad.R);
            }
        }

        bool any = false;
        for (int i = 0; i < lk.Length; i++)
        {
            if (lk[i] != old[i])
            {
                changed.Add(i % t.SamplesX, i / t.SamplesX);
                any = true;
            }
        }

        return any;
    }

    private static void MarkCorridor(TerrainStore t, ReadOnlySpan<Vec2> axis, in RoadAxis ax)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int k = 0; k < axis.Length; k++)
        {
            minX = MathF.Min(minX, axis[k].X - ax.Hw);
            minZ = MathF.Min(minZ, axis[k].Z - ax.Hw);
            maxX = MathF.Max(maxX, axis[k].X + ax.Hw);
            maxZ = MathF.Max(maxZ, axis[k].Z + ax.Hw);
        }

        Range(t, minX, maxX, t.OriginX, t.SamplesX, out int ix0, out int ix1);
        Range(t, minZ, maxZ, t.OriginZ, t.SamplesZ, out int iz0, out int iz1);
        for (int iz = iz0; iz <= iz1; iz++)
        {
            float pz = t.OriginZ + (iz * t.CellM);
            for (int ix = ix0; ix <= ix1; ix++)
            {
                Project(axis, new Vec2(t.OriginX + (ix * t.CellM), pz), out float d, out float s);
                if (d > ax.Hw)
                {
                    continue;
                }

                byte bit = !ax.Bridge ? TerrainLock.Road : s <= AbutmentM || s >= ax.Full - AbutmentM ? TerrainLock.BridgeEnd : (byte)0;
                t.Lock[(iz * t.SamplesX) + ix] |= bit;
            }
        }
    }

    private static void MarkDisc(TerrainStore t, Vec2 c, float r)
    {
        Range(t, c.X - r, c.X + r, t.OriginX, t.SamplesX, out int ix0, out int ix1);
        Range(t, c.Z - r, c.Z + r, t.OriginZ, t.SamplesZ, out int iz0, out int iz1);
        for (int iz = iz0; iz <= iz1; iz++)
        {
            float pz = t.OriginZ + (iz * t.CellM);
            for (int ix = ix0; ix <= ix1; ix++)
            {
                if (Dist(c, new Vec2(t.OriginX + (ix * t.CellM), pz)) <= r)
                {
                    t.Lock[(iz * t.SamplesX) + ix] |= TerrainLock.NodePad;
                }
            }
        }
    }

    // ---------------------------------------------------------------- жизненный цикл

    /// <summary>Выделить производные (массивы высот полос, состояние); только при рельефе.</summary>
    public static void Ensure(World w)
    {
        LaneElevation.Ensure(w.Net);
        w.Net.TerrainState ??= new RoadTerrainState(w.Terrain!.Count);
    }

    /// <summary>
    /// Синхронизация после правки сети (фаза 1): при смене <c>Net.Version</c> - пересчёт высот полос и <c>Lock</c>. Если изменились рельеф (<paramref name="ground"/>, штамп команды)
    /// или <c>Lock</c> - один <c>Version++</c> рельефа и пометка чанков грязными (§7.4, TR9).
    /// </summary>
    public static void Sync(World w, in SampleRect ground)
    {
        TerrainStore? t = w.Terrain;
        if (t == null)
        {
            return;
        }

        NetworkStore n = w.Net;
        RoadTerrainState st = n.TerrainState!;
        var rect = ground;
        rect.Union(in st.Pending);
        st.Pending = default;
        if (st.NetVersion != n.Version)
        {
            st.NetVersion = n.Version;
            LaneElevation.Rebuild(n);
            RebuildLock(w, ref rect);
        }

        if (rect.Any)
        {
            t.Version++;
            t.MarkSamples(rect.X0, rect.Z0, rect.X1, rect.Z1);
        }
    }

    public static void Sync(World w)
    {
        var none = default(SampleRect);
        Sync(w, in none);
    }

    /// <summary>Производные после <c>Load</c> (или <c>Create</c>): высоты полос и <c>Lock</c> без <c>Version++</c> - они функция сохранённого состояния.</summary>
    public static void RestoreDerived(World w)
    {
        if (w.Terrain == null)
        {
            return;
        }

        Ensure(w);
        LaneElevation.Rebuild(w.Net);
        var r = default(SampleRect);
        RebuildLock(w, ref r);
        w.Net.TerrainState!.NetVersion = w.Net.Version;
    }

    /// <summary>
    /// <c>Create</c> с рельефом (§4.1, §4.5): высоты всех узлов по земле (узел в воде - над водой на просвет моста), мост там, где ось уходит в воду или выше <c>max_fill</c>,
    /// затем штамп всех сегментов по индексу и всех площадок по индексу, затем производные. Отказов нет: сеть сценария принимается как есть.
    /// </summary>
    public static void InitOnCreate(World w)
    {
        TerrainStore? t = w.Terrain;
        if (t == null)
        {
            return;
        }

        Ensure(w);
        NetworkStore n = w.Net;
        NetworkOptions o = w.Builder.Options;
        for (int i = 0; i < n.NodeCount; i++)
        {
            if (!n.NodeAlive(i))
            {
                continue;
            }

            Vec2 p = n.Nodes[i].Position;
            int y = t.GroundCmAt(p.X, p.Z);
            int si = t.NearestSample(p.X, p.Z);
            if (t.Water[si] > t.Ground[si])
            {
                y = Math.Max(y, t.BaseCm + t.Water[si] + w.TerrainCfg.BridgeClearanceCm);
            }

            n.Nodes[i].Elevation = Meters(y);
        }

        Span<Vec2> axis = stackalloc Vec2[MaxAxisPoints];
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            ref SegmentData sd = ref n.Segments[s];
            int an = FullAxis(n, in sd, axis);
            RoadAxis ax = AxisOf(n, in o, in sd, axis[..an]);
            if (Scan(w, axis[..an], in ax, out _).NeedBridge)
            {
                sd.Flags |= SegmentFlags.Bridge;
            }
        }

        var changed = default(SampleRect);
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[s];
            int an = FullAxis(n, in sd, axis);
            RoadAxis ax = AxisOf(n, in o, in sd, axis[..an]);
            Stamp(t, axis[..an], in ax, ReadOnlySpan<RoadPad>.Empty, w.TerrainCfg.BankSlopePm, apply: true, respectLock: false, ref changed);
        }

        Span<RoadPad> pad = stackalloc RoadPad[1];
        for (int i = 0; i < n.NodeCount; i++)
        {
            if (n.NodeAlive(i) && n.Nodes[i].ArmCount > 0)
            {
                pad[0] = PadOf(n, in o, i, 0f);
                Stamp(t, ReadOnlySpan<Vec2>.Empty, default, pad, w.TerrainCfg.BankSlopePm, apply: true, respectLock: false, ref changed);
            }
        }

        RestoreDerived(w);
    }

    /// <summary>
    /// Применение <c>BuildRoad</c> (§4.4): штамп нового сегмента по его построенной геометрии и площадок обоих концов (заблокированные отсчёты - другие дороги - не трогаются).
    /// Синхронизация (высоты полос, <c>Lock</c>, <c>Version++</c>) - в следующем <see cref="Sync(World)"/>: после connector'ов узлов, до следующей команды и в конце фазы 1. Мост - только устои.
    /// </summary>
    public static void OnRoadBuilt(World w, int seg)
    {
        TerrainStore t = w.Terrain!;
        NetworkStore n = w.Net;
        NetworkOptions o = w.Builder.Options;
        ref readonly SegmentData sd = ref n.Segments[seg];
        Span<Vec2> axis = stackalloc Vec2[MaxAxisPoints];
        int an = FullAxis(n, in sd, axis);
        RoadAxis ax = AxisOf(n, in o, in sd, axis[..an]);
        Span<RoadPad> pads = stackalloc RoadPad[2];
        pads[0] = PadOf(n, in o, sd.NodeA, ax.Hw);
        pads[1] = PadOf(n, in o, sd.NodeB, ax.Hw);
        RoadTerrainState st = n.TerrainState!;
        Stamp(t, axis[..an], in ax, pads, w.TerrainCfg.BankSlopePm, apply: true, respectLock: true, ref st.Pending);
        st.NetVersion = int.MinValue;   // пересчитать производные при следующем Sync, даже если версия сети больше не изменится
    }

    // ---------------------------------------------------------------- проверки оси (§4.4, §4.5)

    /// <summary>Итог прохода по оси: нужен ли мост, ниже ли ось земли больше max_cut, низко ли палуба над водой; точка первой проблемы.</summary>
    internal struct ScanResult
    {
        public bool NeedBridge, TooDeep, LowDeck;
        public Vec2 BridgeAt, DeepAt, LowAt;
    }

    /// <summary>
    /// Проход по уклонной части оси (между площадками) с шагом клетки: <c>Y &lt; ground − max_cut</c> - TooDeep; <c>Y &gt; ground + max_fill</c> или мокрый отсчёт - нужен мост;
    /// над мокрым отсчётом <c>Y &lt; Water + clearance</c> - палуба низко. <paramref name="steps"/> - число точек.
    /// </summary>
    public static ScanResult Scan(World w, ReadOnlySpan<Vec2> axis, in RoadAxis ax, out int steps)
    {
        TerrainStore t = w.Terrain!;
        ref readonly TerrainDesc cfg = ref w.TerrainCfg;
        var r = default(ScanResult);
        steps = 0;
        float s0 = MathF.Min(ax.SetA, ax.Full);
        float s1 = MathF.Max(s0, ax.Full - ax.SetB);
        int count = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling((s1 - s0) / t.CellM)));
        for (int k = 0; k <= count; k++)
        {
            float s = s0 + ((s1 - s0) * k / count);
            Vec2 p = PointAt(axis, s);
            int y = ax.YCmAt(s);
            int g = t.GroundCmAt(p.X, p.Z);
            int i = t.NearestSample(p.X, p.Z);
            bool wet = t.Water[i] > t.Ground[i];
            steps++;
            if (!r.TooDeep && y < g - cfg.MaxCutCm)
            {
                r.TooDeep = true;
                r.DeepAt = p;
            }

            if (!r.NeedBridge && (wet || y > g + cfg.MaxFillCm))
            {
                r.NeedBridge = true;
                r.BridgeAt = p;
            }

            if (wet && !r.LowDeck && y < t.BaseCm + t.Water[i] + cfg.BridgeClearanceCm)
            {
                r.LowDeck = true;
                r.LowAt = p;
            }
        }

        return r;
    }

    // ---------------------------------------------------------------- геометрия

    /// <summary>Ближайшая точка ломаной: расстояние <paramref name="d"/> и длина по ломаной до проекции <paramref name="s"/> (ничья - первый отрезок).</summary>
    public static void Project(ReadOnlySpan<Vec2> axis, Vec2 p, out float d, out float s)
    {
        float best = float.MaxValue;
        float acc = 0f;
        s = 0f;
        if (axis.Length == 1)
        {
            d = Dist(axis[0], p);
            return;
        }

        for (int k = 1; k < axis.Length; k++)
        {
            Vec2 a = axis[k - 1];
            Vec2 b = axis[k];
            float ex = b.X - a.X;
            float ez = b.Z - a.Z;
            float l2 = (ex * ex) + (ez * ez);
            float len = MathF.Sqrt(l2);
            float u = l2 > 0f ? Math.Clamp((((p.X - a.X) * ex) + ((p.Z - a.Z) * ez)) / l2, 0f, 1f) : 0f;
            float qx = a.X + (ex * u) - p.X;
            float qz = a.Z + (ez * u) - p.Z;
            float d2 = (qx * qx) + (qz * qz);
            if (d2 < best)
            {
                best = d2;
                s = acc + (len * u);
            }

            acc += len;
        }

        d = axis.Length == 0 ? float.MaxValue : MathF.Sqrt(best);
    }

    /// <summary>Точка ломаной на расстоянии <paramref name="s"/> от начала (зажим к концам).</summary>
    public static Vec2 PointAt(ReadOnlySpan<Vec2> axis, float s)
    {
        float acc = 0f;
        for (int k = 1; k < axis.Length; k++)
        {
            float len = Dist(axis[k - 1], axis[k]);
            if (acc + len >= s)
            {
                float f = len > 0f ? Math.Clamp((s - acc) / len, 0f, 1f) : 0f;
                return axis[k - 1] + ((axis[k] - axis[k - 1]) * f);
            }

            acc += len;
        }

        return axis[^1];
    }

    private static void Range(TerrainStore t, float min, float max, float origin, int samples, out int i0, out int i1)
    {
        i0 = Math.Clamp(DetMath.ToIntSat(MathF.Floor((min - origin) / t.CellM)), 0, samples - 1);
        i1 = Math.Clamp(DetMath.ToIntSat(MathF.Ceiling((max - origin) / t.CellM)), 0, samples - 1);
    }

    private static float Dist(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
