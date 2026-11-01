using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Terrain;

/// <summary>
/// Создание рельефа в <c>WorldBuilder.Create</c> (terrain.md §2.4, §3.2): протяжённость, грунт (flat / heightmap с развёрткой в целых), вода (море, озёра, реки).
/// Всё - функция сценария, без <c>float</c> в результате, кроме фиксированных выражений над отсчётами (интерполяция, расстояние до оси реки).
/// </summary>
internal static class TerrainBuild
{
    public const int RiverFreeboardCm = 50;     // зеркало реки ниже грунта оси (terrain.md §3.2)
    public const int RiverBankRun = 3;          // берег реки: откос 1:3 к исходному грунту
    private const int MaxSourceCellMm = 256_000;
    private const int MaxSourceUnitMm = 1000;
    private const int MaxAbsBaseCm = 1_000_000;

    /// <summary>Проверка секции до выделения (terrain.md §2.1, §9.1). Без рельефа - Ok, остальное не читается.</summary>
    public static CreateStatus Validate(in ScenarioDesc desc)
    {
        ref readonly TerrainDesc t = ref desc.Terrain;
        if (!t.Enabled)
        {
            return CreateStatus.Ok;
        }

        TerrainDesc p = TerrainParams.WithDefaults(in t);
        if (!TerrainParams.ValidCell(p.CellMm) || !(p.MarginM >= 0f) || !float.IsFinite(p.MarginM) || !TerrainParams.Valid(in p, p.CellMm))
        {
            return CreateStatus.InvalidNetwork;
        }

        if (t.HasExtent && !(Fin(t.ExtentMinX) && Fin(t.ExtentMinZ) && Fin(t.ExtentMaxX) && Fin(t.ExtentMaxZ) && t.ExtentMaxX > t.ExtentMinX && t.ExtentMaxZ > t.ExtentMinZ))
        {
            return CreateStatus.InvalidNetwork;
        }

        switch (t.Source)
        {
            case TerrainSource.Flat:
                long flat = Cm(t.FlatM);
                if (!Fin(t.FlatM) || flat < 0 || flat > ushort.MaxValue)
                {
                    return CreateStatus.InvalidNetwork;
                }

                break;
            case TerrainSource.Heightmap:
                if (t.SourceHeights == null || t.SourceSizeX < 2 || t.SourceSizeZ < 2 || (long)t.SourceSizeX * t.SourceSizeZ > TerrainStore.MaxSamples ||
                    t.SourceHeights.Length != t.SourceSizeX * t.SourceSizeZ || t.SourceCellMm < 1 || t.SourceCellMm > MaxSourceCellMm ||
                    t.SourceUnitMm < 0 || t.SourceUnitMm > MaxSourceUnitMm || !Fin(t.SourceOriginX) || !Fin(t.SourceOriginZ) || Math.Abs(t.BaseCm) > MaxAbsBaseCm)
                {
                    return CreateStatus.InvalidNetwork;
                }

                break;
            case TerrainSource.Noise:
                return CreateStatus.NotSupported;   // генератор шума - TR7
            default:
                return CreateStatus.InvalidNetwork;
        }

        return ValidateWater(in t.Water);
    }

    private static CreateStatus ValidateWater(in TerrainWaterDesc w)
    {
        if (w.HasSeaLevel && !Fin(w.SeaLevelM))
        {
            return CreateStatus.InvalidNetwork;
        }

        TerrainLakeDesc[] lakes = w.Lakes ?? Array.Empty<TerrainLakeDesc>();
        for (int i = 0; i < lakes.Length; i++)
        {
            if (!Fin(lakes[i].X) || !Fin(lakes[i].Z) || !Fin(lakes[i].LevelM))
            {
                return CreateStatus.InvalidNetwork;
            }
        }

        TerrainRiverDesc[] rivers = w.Rivers ?? Array.Empty<TerrainRiverDesc>();
        for (int i = 0; i < rivers.Length; i++)
        {
            ref readonly TerrainRiverDesc r = ref rivers[i];
            if (r.Points == null || r.Points.Length < 2 || !Fin(r.WidthM) || !(r.WidthM > 0f) || !Fin(r.DepthM) || !(r.DepthM > 0f) || Cm(r.DepthM) > ushort.MaxValue)
            {
                return CreateStatus.InvalidNetwork;
            }

            for (int k = 0; k < r.Points.Length; k++)
            {
                if (!Fin(r.Points[k].X) || !Fin(r.Points[k].Z))
                {
                    return CreateStatus.InvalidNetwork;
                }
            }
        }

        TerrainWaterSourceDesc[] sources = w.Sources ?? Array.Empty<TerrainWaterSourceDesc>();
        for (int i = 0; i < sources.Length; i++)
        {
            if ((uint)sources[i].Kind > (uint)TerrainWaterSourceKind.Sink)
            {
                return CreateStatus.InvalidNetwork;   // резерв §3.4: остальное валидирует загрузчик, ядро не исполняет
            }
        }

        return CreateStatus.Ok;
    }

    /// <summary>Строит рельеф мира по проверенной секции; без рельефа - ничего. Порядок: протяжённость, грунт, море, озёра, реки (terrain.md §2.4).</summary>
    public static CreateStatus Build(World w, in ScenarioDesc desc)
    {
        ref readonly TerrainDesc t = ref desc.Terrain;
        if (!t.Enabled)
        {
            return CreateStatus.Ok;
        }

        int cellMm = w.TerrainCfg.CellMm;
        Extent(w, in desc, out double minX, out double minZ, out double maxX, out double maxZ);
        if (!Axis(minX, maxX, w.WorldGridOriginX, cellMm, out float originX, out int sx) ||
            !Axis(minZ, maxZ, w.WorldGridOriginZ, cellMm, out float originZ, out int sz) ||
            (long)sx * sz > TerrainStore.MaxSamples)
        {
            return CreateStatus.InvalidNetwork;   // отсчётов больше MaxSamples: увеличьте cell_m (загрузчик сценария - понятная ошибка)
        }

        var s = new TerrainStore(cellMm, originX, originZ, sx, sz, t.Source == TerrainSource.Heightmap ? t.BaseCm : 0);
        if (t.Source == TerrainSource.Heightmap)
        {
            if (!Upsample(s, in t))
            {
                return CreateStatus.InvalidNetwork;   // значение сетки выше 655.35 м над base
            }
        }
        else
        {
            Array.Fill(s.Ground, (ushort)Cm(t.FlatM));
        }

        if (!BuildWater(s, in t.Water))
        {
            return CreateStatus.InvalidNetwork;
        }

        s.Dirty.Reset(s.Version);
        w.Terrain = s;
        return CreateStatus.Ok;
    }

    /// <summary>Протяжённость (terrain.md §2.1): terrain.extent, иначе tools.world_bounds, иначе bbox живых узлов сети ± margin.</summary>
    private static void Extent(World w, in ScenarioDesc desc, out double minX, out double minZ, out double maxX, out double maxZ)
    {
        ref readonly TerrainDesc t = ref desc.Terrain;
        if (t.HasExtent)
        {
            minX = t.ExtentMinX;
            minZ = t.ExtentMinZ;
            maxX = t.ExtentMaxX;
            maxZ = t.ExtentMaxZ;
            return;
        }

        if (desc.Tools.HasWorldBounds)
        {
            minX = desc.Tools.BoundsMinX;
            minZ = desc.Tools.BoundsMinZ;
            maxX = desc.Tools.BoundsMaxX;
            maxZ = desc.Tools.BoundsMaxZ;
            return;
        }

        double m = w.TerrainCfg.MarginM;
        double x0 = 0, z0 = 0, x1 = 0, z1 = 0;
        bool any = false;
        Network.NetworkStore net = w.Net;
        for (int i = 0; i < net.NodeCount; i++)
        {
            if (!net.NodePool.IsAlive(i))
            {
                continue;
            }

            Vec2 p = net.Nodes[i].Position;
            if (!any)
            {
                x0 = x1 = p.X;
                z0 = z1 = p.Z;
                any = true;
            }
            else
            {
                x0 = Math.Min(x0, p.X);
                x1 = Math.Max(x1, p.X);
                z0 = Math.Min(z0, p.Z);
                z1 = Math.Max(z1, p.Z);
            }
        }

        minX = x0 - m;
        minZ = z0 - m;
        maxX = x1 + m;
        maxZ = z1 + m;
    }

    /// <summary>Ось: границы наружу до кратного клетки от начала мировой сетки; <c>origin = grid + k0·cell</c> (тот же расчёт - в валидаторе сейва).</summary>
    private static bool Axis(double min, double max, float gridOrigin, int cellMm, out float origin, out int samples)
    {
        double cell = cellMm / 1000.0;
        double k0 = Math.Floor((min - gridOrigin) / cell);
        double k1 = Math.Ceiling((max - gridOrigin) / cell);
        origin = OriginFor(gridOrigin, DetMath.ToLongSat(k0), cellMm);
        double n = k1 - k0 + 1;
        samples = n >= 2 && n <= TerrainStore.MaxSamples ? DetMath.ToIntSat(n) : 0;
        return samples >= 2 && float.IsFinite(origin);
    }

    /// <summary>Начало отсчётов по номеру клетки от мировой сетки (float из double, детерминированно).</summary>
    public static float OriginFor(float gridOrigin, long k, int cellMm) => (float)(gridOrigin + (k * (cellMm / 1000.0)));

    /// <summary>Номер клетки начала отсчётов от мировой сетки (обратное к <see cref="OriginFor"/>).</summary>
    public static long CellIndexOf(float origin, float gridOrigin, int cellMm) => DetMath.ToLongSat(Math.Round((origin - (double)gridOrigin) / (cellMm / 1000.0)));

    /// <summary>
    /// Развёртка исходной сетки на шаг отсчётов (terrain.md §9.1): билинейно в целых по мм-смещениям,
    /// <c>h = (h00·(S−fx)(S−fz) + h10·fx(S−fz) + h01·(S−fx)fz + h11·fx·fz) / S²</c>, перевод единиц и округление к ближайшему. Вне исходной сетки - зажим к краю.
    /// </summary>
    private static bool Upsample(TerrainStore s, in TerrainDesc t)
    {
        long S = t.SourceCellMm;
        long unit = t.SourceUnitMm == 0 ? TerrainStore.HeightUnitMm : t.SourceUnitMm;
        long den = S * S * TerrainStore.HeightUnitMm;
        long ox = Mm(s.OriginX) - Mm(t.SourceOriginX);
        long oz = Mm(s.OriginZ) - Mm(t.SourceOriginZ);
        int ssx = t.SourceSizeX;
        ushort[] src = t.SourceHeights;
        for (int iz = 0; iz < s.SamplesZ; iz++)
        {
            SrcAxis(oz + ((long)iz * s.CellMm), S, t.SourceSizeZ, out int jz, out long fz);
            for (int ix = 0; ix < s.SamplesX; ix++)
            {
                SrcAxis(ox + ((long)ix * s.CellMm), S, ssx, out int jx, out long fx);
                int j = (jz * ssx) + jx;
                long num = (src[j] * (S - fx) * (S - fz)) + (src[j + 1] * fx * (S - fz)) + (src[j + ssx] * (S - fx) * fz) + (src[j + ssx + 1] * fx * fz);
                long cm = ((num * unit) + (den / 2)) / den;
                if (cm > ushort.MaxValue)
                {
                    return false;
                }

                s.Ground[(iz * s.SamplesX) + ix] = (ushort)cm;
            }
        }

        return true;
    }

    private static void SrcAxis(long p, long S, int n, out int j, out long f)
    {
        long max = (n - 1) * S;
        if (p <= 0)
        {
            j = 0;
            f = 0;
        }
        else if (p >= max)
        {
            j = n - 2;
            f = S;
        }
        else
        {
            j = (int)(p / S);
            f = p - (j * S);
        }
    }

    // ---- вода (terrain.md §3.2) ----

    private static bool BuildWater(TerrainStore s, in TerrainWaterDesc w)
    {
        int[]? queue = null;
        if (w.HasSeaLevel)
        {
            long sea = Cm(w.SeaLevelM) - s.BaseCm;
            if (sea > ushort.MaxValue)
            {
                return false;
            }

            if (sea > 0)
            {
                queue = new int[s.Count];
                Sea(s, (ushort)sea, queue);
            }
        }

        TerrainLakeDesc[] lakes = w.Lakes ?? Array.Empty<TerrainLakeDesc>();
        for (int i = 0; i < lakes.Length; i++)
        {
            long level = Cm(lakes[i].LevelM) - s.BaseCm;
            if (level > ushort.MaxValue)
            {
                return false;
            }

            if (level > 0)
            {
                queue ??= new int[s.Count];
                int tail = 0;
                Push(s, s.NearestSample(lakes[i].X, lakes[i].Z), (ushort)level, queue, ref tail);
                Flood(s, (ushort)level, queue, tail);
            }
        }

        TerrainRiverDesc[] rivers = w.Rivers ?? Array.Empty<TerrainRiverDesc>();
        if (rivers.Length > 0)
        {
            var river = new byte[s.Count];   // 1 - вода этого этапа (реки): зеркало = минимум назначений; вода моря и озёр остаётся
            for (int r = 0; r < rivers.Length; r++)
            {
                River(s, in rivers[r], river);
            }
        }

        return true;
    }

    /// <summary>Море: BFS по 4 соседям от краевых отсчётов с Ground &lt; уровня, краевые - по возрастанию индекса, соседи E, W, S (+Z), N (−Z).</summary>
    private static void Sea(TerrainStore s, ushort level, int[] queue)
    {
        int sx = s.SamplesX;
        int sz = s.SamplesZ;
        int tail = 0;
        for (int iz = 0; iz < sz; iz++)
        {
            int row = iz * sx;
            if (iz == 0 || iz == sz - 1)
            {
                for (int ix = 0; ix < sx; ix++)
                {
                    Push(s, row + ix, level, queue, ref tail);
                }
            }
            else
            {
                Push(s, row, level, queue, ref tail);
                Push(s, row + sx - 1, level, queue, ref tail);
            }
        }

        Flood(s, level, queue, tail);
    }

    /// <summary>Заливка из очереди (FIFO): сухой отсчёт с Ground &lt; уровня становится водой уровня; существующая вода - стена.</summary>
    private static void Flood(TerrainStore s, ushort level, int[] queue, int tail)
    {
        int sx = s.SamplesX;
        int n = s.Count;
        int head = 0;
        while (head < tail)
        {
            int i = queue[head++];
            int ix = i % sx;
            if (ix + 1 < sx)
            {
                Push(s, i + 1, level, queue, ref tail);
            }

            if (ix > 0)
            {
                Push(s, i - 1, level, queue, ref tail);
            }

            if (i + sx < n)
            {
                Push(s, i + sx, level, queue, ref tail);
            }

            if (i - sx >= 0)
            {
                Push(s, i - sx, level, queue, ref tail);
            }
        }
    }

    private static void Push(TerrainStore s, int i, ushort level, int[] queue, ref int tail)
    {
        if (s.Water[i] != TerrainStore.Dry || s.Ground[i] >= level)
        {
            return;
        }

        s.Water[i] = level;
        s.WaterKind[i] = 1;
        queue[tail++] = i;
    }

    /// <summary>
    /// Река (terrain.md §3.2): зеркало вдоль полилинии с шагом клетки <c>W(s) = min(W(s−1), GroundCmAt(точка) − 50)</c> по грунту до врезки; отсчёты в полуширине
    /// получают <c>Water = W</c> ближайшей точки оси и <c>Ground = min(Ground, W − depth)</c>, полоса ещё полуширины - откос 1:3 к исходному грунту.
    /// Ближайшая точка оси - проекция на отрезки (первый при равенстве); зеркало - шаг <c>floor(s / cell)</c> по длине дуги.
    /// </summary>
    private static void River(TerrainStore s, in TerrainRiverDesc r, byte[] river)
    {
        Vec2[] pts = r.Points;
        float cell = s.CellM;
        int segs = pts.Length - 1;
        var cum = new float[pts.Length];
        for (int k = 0; k < segs; k++)
        {
            float dx = pts[k + 1].X - pts[k].X;
            float dz = pts[k + 1].Z - pts[k].Z;
            cum[k + 1] = cum[k] + MathF.Sqrt((dx * dx) + (dz * dz));
        }

        float length = cum[segs];
        int steps = DetMath.ToIntSat(MathF.Floor(length / cell)) + 1;
        var mirror = new int[steps];   // абсолютные см
        int seg = 0;
        int prev = int.MaxValue;
        for (int k = 0; k < steps; k++)
        {
            float at = k * cell;
            while (seg < segs - 1 && cum[seg + 1] < at)
            {
                seg++;
            }

            float len = cum[seg + 1] - cum[seg];
            float u = len > 0f ? Math.Clamp((at - cum[seg]) / len, 0f, 1f) : 0f;
            float px = pts[seg].X + ((pts[seg + 1].X - pts[seg].X) * u);
            float pz = pts[seg].Z + ((pts[seg + 1].Z - pts[seg].Z) * u);
            prev = Math.Min(prev, s.GroundCmAt(px, pz) - RiverFreeboardCm);
            mirror[k] = prev;
        }

        float hw = r.WidthM * 0.5f;
        float reach = 2f * hw;
        int depthCm = (int)Cm(r.DepthM);
        float bx0 = float.MaxValue, bz0 = float.MaxValue, bx1 = float.MinValue, bz1 = float.MinValue;
        for (int k = 0; k < pts.Length; k++)
        {
            bx0 = MathF.Min(bx0, pts[k].X);
            bz0 = MathF.Min(bz0, pts[k].Z);
            bx1 = MathF.Max(bx1, pts[k].X);
            bz1 = MathF.Max(bz1, pts[k].Z);
        }

        int ix0 = Math.Max(0, DetMath.ToIntSat(MathF.Floor((bx0 - reach - s.OriginX) / cell)));
        int iz0 = Math.Max(0, DetMath.ToIntSat(MathF.Floor((bz0 - reach - s.OriginZ) / cell)));
        int ix1 = Math.Min(s.SamplesX - 1, DetMath.ToIntSat(MathF.Ceiling((bx1 + reach - s.OriginX) / cell)));
        int iz1 = Math.Min(s.SamplesZ - 1, DetMath.ToIntSat(MathF.Ceiling((bz1 + reach - s.OriginZ) / cell)));
        for (int iz = iz0; iz <= iz1; iz++)
        {
            float z = s.OriginZ + (iz * cell);
            for (int ix = ix0; ix <= ix1; ix++)
            {
                float x = s.OriginX + (ix * cell);
                float best = float.MaxValue;
                float bestAt = 0f;
                for (int k = 0; k < segs; k++)
                {
                    float ax = pts[k].X, az = pts[k].Z;
                    float dx = pts[k + 1].X - ax, dz = pts[k + 1].Z - az;
                    float l2 = (dx * dx) + (dz * dz);
                    float u = l2 > 0f ? Math.Clamp((((x - ax) * dx) + ((z - az) * dz)) / l2, 0f, 1f) : 0f;
                    float ex = x - (ax + (dx * u));
                    float ez = z - (az + (dz * u));
                    float d2 = (ex * ex) + (ez * ez);
                    if (d2 < best)
                    {
                        best = d2;
                        bestAt = cum[k] + ((cum[k + 1] - cum[k]) * u);
                    }
                }

                float d = MathF.Sqrt(best);
                if (!(d <= reach))
                {
                    continue;
                }

                int step = Math.Clamp(DetMath.ToIntSat(MathF.Floor(bestAt / cell)), 0, steps - 1);
                int wRel = Math.Clamp(mirror[step] - s.BaseCm, 1, ushort.MaxValue);
                int i = (iz * s.SamplesX) + ix;
                if (d <= hw)
                {
                    s.Ground[i] = (ushort)Math.Min(s.Ground[i], Math.Max(0, wRel - depthCm));
                    if (river[i] != 0)
                    {
                        s.Water[i] = (ushort)Math.Min(s.Water[i], wRel);
                    }
                    else if (s.Water[i] == TerrainStore.Dry)
                    {
                        s.Water[i] = (ushort)wRel;
                        s.WaterKind[i] = 1;
                        river[i] = 1;
                    }
                }
                else
                {
                    int bank = wRel + DetMath.ToIntSat(MathF.Round((d - hw) * 100f / RiverBankRun));
                    s.Ground[i] = (ushort)Math.Clamp(Math.Min(s.Ground[i], bank), 0, ushort.MaxValue);
                }
            }
        }
    }

    private static bool Fin(float v) => float.IsFinite(v);

    private static long Cm(float m) => DetMath.ToLongSat(Math.Round(m * 100.0));

    private static long Mm(float m) => DetMath.ToLongSat(Math.Round(m * 1000.0));
}
