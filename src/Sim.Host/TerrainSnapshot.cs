using System;
using Sim.Core.Api;

namespace Sim.Host;

/// <summary>
/// Неизменяемый чанк рельефа для клиента (terrain.md §10.1): отсчёты <c>[X0, X0 + Width) × [Z0, Z0 + Height)</c>, где <c>X0 = CX · 32</c>.
/// Чанк покрывает 32×32 клетки, а значит до 33×33 отсчётов: общий край с соседом продублирован, меш чанка строится без чтения соседних.
/// Массивы после публикации не меняются (поток симуляции при изменении делает новый чанк), поэтому главный поток читает их без блокировок.
/// </summary>
public sealed class TerrainChunk
{
    private readonly ushort[] _ground;
    private readonly ushort[] _water;
    private readonly byte[] _kind;
    private readonly byte[] _lock;

    internal TerrainChunk(int cx, int cz, int x0, int z0, int width, int height, int version, ushort[] ground, ushort[] water, byte[] kind, byte[] lockBits)
    {
        CX = cx;
        CZ = cz;
        X0 = x0;
        Z0 = z0;
        Width = width;
        Height = height;
        Version = version;
        _ground = ground;
        _water = water;
        _kind = kind;
        _lock = lockBits;
    }

    public int CX { get; }

    public int CZ { get; }

    /// <summary>Индекс первого отсчёта чанка в глобальной сетке (колонка и строка).</summary>
    public int X0 { get; }

    public int Z0 { get; }

    /// <summary>Отсчётов по X и Z (33, у последних чанков меньше).</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Версия рельефа (<c>TerrainView.Version</c>), на которой скопирован чанк. Клиент хранит её на каждый свой меш: не равна - перестроить.</summary>
    public int Version { get; }

    /// <summary>Высоты, см над <see cref="TerrainSnapshot.BaseCm"/>; индекс <c>lz · Width + lx</c>.</summary>
    public ReadOnlySpan<ushort> Ground => _ground;

    /// <summary>Уровень воды, см над базой; мокро <c>⇔ Water &gt; Ground</c>.</summary>
    public ReadOnlySpan<ushort> Water => _water;

    public ReadOnlySpan<byte> WaterKind => _kind;

    /// <summary>Биты <c>TerrainLock</c>; актуальны на <see cref="Version"/> чанка (обновляются вместе с ним).</summary>
    public ReadOnlySpan<byte> Lock => _lock;

    /// <summary>Высота отсчёта (локальные координаты), м: <c>(BaseCm + Ground) / 100</c>.</summary>
    public float HeightM(int lx, int lz, int baseCm) => (baseCm + _ground[(lz * Width) + lx]) / 100f;
}

/// <summary>
/// Копия рельефа для главного потока (terrain.md §10.1, ADR-0024 п. 3): сетка чанков 32×32 клетки, каждый чанк - отдельные неизменяемые массивы.
/// Поток симуляции при смене <c>Terrain.Version</c> копирует только грязные чанки (<see cref="Sim.Core.Api.Simulation.CopyTerrainDirty"/>), чистые
/// переходят в новый снимок по ссылке; полная копия - на старте и при переполнении кольца. Кадр несёт ссылку (<see cref="SimFrame.Terrain"/>);
/// пока рельеф не менялся, это тот же объект - 0 Б на кадр. Без рельефа в мире кадр несёт <c>null</c>.
/// </summary>
public sealed class TerrainSnapshot
{
    /// <summary>Размер чанка в клетках (= <c>TerrainView.ChunkSamples</c>).</summary>
    public const int ChunkCells = 32;

    private readonly TerrainChunk[] _chunks;
    private readonly int[] _changed;

    private TerrainSnapshot(TerrainChunk[] chunks, int[] changed, bool full, TerrainSnapshot? prev, int version, float cellM, float originX, float originZ, int samplesX, int samplesZ, int baseCm, int chunksX, int chunksZ)
    {
        _chunks = chunks;
        _changed = changed;
        Full = full;
        PreviousVersion = prev?.Version ?? -1;
        Version = version;
        CellM = cellM;
        OriginX = originX;
        OriginZ = originZ;
        SamplesX = samplesX;
        SamplesZ = samplesZ;
        BaseCm = baseCm;
        ChunksX = chunksX;
        ChunksZ = chunksZ;
    }

    /// <summary><c>TerrainView.Version</c> на момент копии.</summary>
    public int Version { get; }

    /// <summary>Версия предыдущего снимка хоста (-1 у первого). Список <see cref="ChangedChunks"/> верен для клиента, видевшего именно её.</summary>
    public int PreviousVersion { get; }

    /// <summary>Снимок собран полной копией (старт, переполнение кольца грязи, смена размеров): <see cref="ChangedChunks"/> = все чанки.</summary>
    public bool Full { get; }

    public float CellM { get; }

    public float OriginX { get; }

    public float OriginZ { get; }

    public int SamplesX { get; }

    public int SamplesZ { get; }

    public int BaseCm { get; }

    public int ChunksX { get; }

    public int ChunksZ { get; }

    public int ChunkCount => _chunks.Length;

    /// <summary>Индексы чанков (<c>cz · ChunksX + cx</c>), заменённых относительно предыдущего снимка, по возрастанию. Клиент, пропустивший кадры, сравнивает <see cref="TerrainChunk.Version"/>.</summary>
    public ReadOnlySpan<int> ChangedChunks => _changed;

    public TerrainChunk GetChunk(int cx, int cz) => _chunks[(cz * ChunksX) + cx];

    public TerrainChunk GetChunk(int index) => _chunks[index];

    /// <summary>
    /// Строит снимок. <paramref name="prev"/> = null или <paramref name="overflow"/> или иные размеры - полная копия; иначе копируются только чанки из
    /// <paramref name="dirty"/>, остальные берутся у <paramref name="prev"/>. Спаны - массивы ядра целиком (<c>SamplesX · SamplesZ</c>).
    /// </summary>
    internal static TerrainSnapshot Build(
        TerrainSnapshot? prev,
        int version,
        float cellM,
        float originX,
        float originZ,
        int samplesX,
        int samplesZ,
        int baseCm,
        ReadOnlySpan<ushort> ground,
        ReadOnlySpan<ushort> water,
        ReadOnlySpan<byte> kind,
        ReadOnlySpan<byte> lockBits,
        ReadOnlySpan<TerrainDirtyRect> dirty,
        bool overflow)
    {
        int chunksX = Math.Max(1, (samplesX - 1 + ChunkCells - 1) / ChunkCells);
        int chunksZ = Math.Max(1, (samplesZ - 1 + ChunkCells - 1) / ChunkCells);
        int n = chunksX * chunksZ;
        bool full = prev == null || overflow || prev.SamplesX != samplesX || prev.SamplesZ != samplesZ || prev.ChunksX != chunksX || prev.ChunksZ != chunksZ;
        var chunks = new TerrainChunk[n];
        int[] changed;
        if (full)
        {
            changed = new int[n];
            for (int i = 0; i < n; i++)
            {
                chunks[i] = CopyChunk(i % chunksX, i / chunksX, version, samplesX, samplesZ, ground, water, kind, lockBits);
                changed[i] = i;
            }
        }
        else
        {
            Array.Copy(prev!._chunks, chunks, n);
            var mask = new bool[n];
            int count = 0;
            foreach (TerrainDirtyRect r in dirty)
            {
                int x1 = Math.Min((int)r.ChunkX1, chunksX - 1);
                int z1 = Math.Min((int)r.ChunkZ1, chunksZ - 1);
                for (int cz = r.ChunkZ0; cz <= z1; cz++)
                {
                    for (int cx = r.ChunkX0; cx <= x1; cx++)
                    {
                        int i = (cz * chunksX) + cx;
                        if (!mask[i])
                        {
                            mask[i] = true;
                            count++;
                        }
                    }
                }
            }

            changed = new int[count];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask[i])
                {
                    chunks[i] = CopyChunk(i % chunksX, i / chunksX, version, samplesX, samplesZ, ground, water, kind, lockBits);
                    changed[k++] = i;
                }
            }
        }

        return new TerrainSnapshot(chunks, changed, full, prev, version, cellM, originX, originZ, samplesX, samplesZ, baseCm, chunksX, chunksZ);
    }

    private static TerrainChunk CopyChunk(int cx, int cz, int version, int samplesX, int samplesZ, ReadOnlySpan<ushort> ground, ReadOnlySpan<ushort> water, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> lockBits)
    {
        int x0 = cx * ChunkCells;
        int z0 = cz * ChunkCells;
        int w = Math.Min(ChunkCells + 1, samplesX - x0);
        int h = Math.Min(ChunkCells + 1, samplesZ - z0);
        var g = new ushort[w * h];
        var wa = new ushort[w * h];
        var kd = new byte[w * h];
        var lk = new byte[w * h];
        for (int lz = 0; lz < h; lz++)
        {
            int src = ((z0 + lz) * samplesX) + x0;
            int dst = lz * w;
            ground.Slice(src, w).CopyTo(g.AsSpan(dst, w));
            water.Slice(src, w).CopyTo(wa.AsSpan(dst, w));
            kind.Slice(src, w).CopyTo(kd.AsSpan(dst, w));
            lockBits.Slice(src, w).CopyTo(lk.AsSpan(dst, w));
        }

        return new TerrainChunk(cx, cz, x0, z0, w, h, version, g, wa, kd, lk);
    }
}
