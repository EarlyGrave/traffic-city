using System;
using Sim.Core.Util;

namespace Sim.Core.Terrain;

/// <summary>
/// Карта высот и вода (terrain.md §2.1, ADR-0036): плотная сетка отсчётов в углах клеток, строка за строкой по Z (<c>i = iz · SamplesX + ix</c>).
/// Высоты и вода - <c>ushort</c> сантиметров над <see cref="BaseCm"/>; в состоянии нет <c>float</c> (TR-I2). Существует только при <c>terrain.enabled</c>.
/// </summary>
internal sealed class TerrainStore
{
    public const int MaxSamples = 1 << 24;       // 16.8 млн отсчётов, 4096²
    public const int ChunkSamples = 32;          // чанк грязи клиента: 32×32 клетки (256 м при 8 м)
    public const ushort Dry = 0;                 // Water: воды нет
    public const int HeightUnitMm = 10;          // TERR 7: единица высот (1 см)

    public readonly int CellMm;                  // 4000 | 8000 | 16000
    public readonly float CellM;                 // CellMm / 1000f
    public readonly float OriginX, OriginZ;      // мир отсчёта (0, 0); кратно клетке от начала мировой сетки
    public readonly int SamplesX, SamplesZ;      // = клеток + 1, >= 2
    public readonly int BaseCm;                  // абсолютная высота нуля, см
    public readonly int ChunksX, ChunksZ;        // чанков 32×32 клетки по осям

    // ---- состояние (сейв TERR, хэш п. 14) ----
    public readonly ushort[] Ground;
    public readonly ushort[] Water;              // зеркало, те же см; Dry = 0; мокро <=> Water > Ground
    public readonly byte[] WaterKind;            // 0 нет, 1 природная, 2 пруд игрока; 3 - резерв (течение, §3.4)
    public int Version;                          // ++ на каждую команду, изменившую Ground/Water (TR5)
    public int OpSeq;                            // счётчик мазков (TR5)

    // ---- производные (не в сейве, не в хэше) ----
    public readonly byte[] Lock;                 // биты TerrainLock (TR3/TR6); в TR1 - нули
    public readonly TerrainDirtyRing Dirty = new();

    public TerrainStore(int cellMm, float originX, float originZ, int samplesX, int samplesZ, int baseCm)
    {
        CellMm = cellMm;
        CellM = cellMm / 1000f;
        OriginX = originX;
        OriginZ = originZ;
        SamplesX = samplesX;
        SamplesZ = samplesZ;
        BaseCm = baseCm;
        ChunksX = Math.Max(1, (samplesX - 1 + ChunkSamples - 1) / ChunkSamples);
        ChunksZ = Math.Max(1, (samplesZ - 1 + ChunkSamples - 1) / ChunkSamples);
        int n = samplesX * samplesZ;
        Ground = new ushort[n];
        Water = new ushort[n];
        WaterKind = new byte[n];
        Lock = new byte[n];
    }

    public int Count => Ground.Length;

    /// <summary>Байт на отсчёт: состояние (Ground 2 + Water 2 + WaterKind 1) и производное Lock 1.</summary>
    public const int BytesPerSample = 6;

    // ---- запросы (terrain.md §2.2): чистые функции, 0 Б ----

    /// <summary>
    /// Высота грунта, абсолютные см: билинейно по четырём отсчётам в <c>float</c>, фиксированный порядок
    /// <c>(h00·(1−tx) + h10·tx)·(1−tz) + (h01·(1−tx) + h11·tx)·tz</c>, затем <c>ToIntSat(Round)</c> + <see cref="BaseCm"/>. Вне протяжённости - зажим к краю.
    /// </summary>
    public int GroundCmAt(float x, float z)
    {
        Locate((x - OriginX) / CellM, SamplesX, out int ix, out float tx);
        Locate((z - OriginZ) / CellM, SamplesZ, out int iz, out float tz);
        int i = (iz * SamplesX) + ix;
        float h00 = Ground[i];
        float h10 = Ground[i + 1];
        float h01 = Ground[i + SamplesX];
        float h11 = Ground[i + SamplesX + 1];
        float a = (h00 * (1f - tx)) + (h10 * tx);
        float b = (h01 * (1f - tx)) + (h11 * tx);
        float v = (a * (1f - tz)) + (b * tz);
        return BaseCm + DetMath.ToIntSat(MathF.Round(v));
    }

    /// <summary>Высота грунта, м (для снапшотов и клиента).</summary>
    public float GroundAt(float x, float z) => GroundCmAt(x, z) / 100f;

    /// <summary>Мокрый ли ближайший отсчёт.</summary>
    public bool WetAt(float x, float z)
    {
        int i = NearestSample(x, z);
        return Water[i] > Ground[i];
    }

    /// <summary>Индекс ближайшего отсчёта (зажим к краю).</summary>
    public int NearestSample(float x, float z)
    {
        int ix = Clamp(DetMath.ToIntSat(MathF.Round((x - OriginX) / CellM)), 0, SamplesX - 1);
        int iz = Clamp(DetMath.ToIntSat(MathF.Round((z - OriginZ) / CellM)), 0, SamplesZ - 1);
        return (iz * SamplesX) + ix;
    }

    /// <summary>
    /// Перепад грунта на прямоугольнике отсчётов <c>[ix0, ix0 + nx) × [iz0, iz0 + nz)</c> (участки, §5), абсолютные см. Прямоугольник обрезается сеткой;
    /// пустое пересечение - false.
    /// </summary>
    public bool CornerRange(int ix0, int iz0, int nx, int nz, out int minCm, out int maxCm)
    {
        int x0 = Math.Max(ix0, 0);
        int z0 = Math.Max(iz0, 0);
        int x1 = Math.Min(ix0 + nx, SamplesX);
        int z1 = Math.Min(iz0 + nz, SamplesZ);
        minCm = 0;
        maxCm = 0;
        if (x0 >= x1 || z0 >= z1)
        {
            return false;
        }

        int lo = ushort.MaxValue;
        int hi = 0;
        for (int iz = z0; iz < z1; iz++)
        {
            int row = iz * SamplesX;
            for (int ix = x0; ix < x1; ix++)
            {
                int g = Ground[row + ix];
                lo = Math.Min(lo, g);
                hi = Math.Max(hi, g);
            }
        }

        minCm = BaseCm + lo;
        maxCm = BaseCm + hi;
        return true;
    }

    // ---- грязь для клиента ----

    /// <summary>
    /// Отметить изменённые отсчёты <c>[ix0..ix1] × [iz0..iz1]</c> (включительно) текущей <see cref="Version"/>. Отсчёт на границе чанков задевает оба:
    /// чанк c покрывает отсчёты <c>[32c, 32c + 32]</c>.
    /// </summary>
    public void MarkSamples(int ix0, int iz0, int ix1, int iz1)
    {
        int cx0 = Clamp((Math.Max(ix0, 1) - 1) / ChunkSamples, 0, ChunksX - 1);
        int cz0 = Clamp((Math.Max(iz0, 1) - 1) / ChunkSamples, 0, ChunksZ - 1);
        int cx1 = Clamp(ix1 / ChunkSamples, 0, ChunksX - 1);
        int cz1 = Clamp(iz1 / ChunkSamples, 0, ChunksZ - 1);
        Dirty.Add(Version, (ushort)cx0, (ushort)cz0, (ushort)cx1, (ushort)cz1);
    }

    private static void Locate(float f, int n, out int i, out float t)
    {
        if (!(f > 0f))
        {
            i = 0;      // и NaN
            t = 0f;
            return;
        }

        if (f >= n - 1)
        {
            i = n - 2;
            t = 1f;
            return;
        }

        i = DetMath.ToIntSat(f);
        t = f - i;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
