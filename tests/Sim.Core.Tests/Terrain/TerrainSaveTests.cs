using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Terrain;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Terrain;

/// <summary>TR1 (terrain.md §8, §9): блок TERR и PARM 241-255, раздел хэша 14, детерминизм по потокам, 0 Б/тик, отказы валидатора, замеры памяти и сейва.</summary>
public sealed class TerrainSaveTests
{
    private readonly ITestOutputHelper _out;

    public TerrainSaveTests(ITestOutputHelper output) => _out = output;

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static byte[] DemoSave(int ticks = 200)
    {
        ScenarioDesc d = TerrainFixtures.Demo();
        using Simulation sim = Make(in d);
        sim.Step(ticks);
        return WorldSaveFixtures.SaveBytes(sim);
    }

    [Fact]
    public void Demo_SaveLoadSave_ByteEqual_HashAndContinuation()
    {
        ScenarioDesc d = TerrainFixtures.Demo();
        using Simulation a = Make(in d);
        a.Step(200);
        byte[] first = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(first);
        Assert.True(b.HasTerrain);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        byte[] second = WorldSaveFixtures.SaveBytes(b);
        Assert.Equal(first.Length, second.Length);
        int diff = first.AsSpan().CommonPrefixLength(second);
        Assert.True(diff == first.Length, diff < first.Length ? WorldSaveFixtures.Locate(first, diff) : "");

        TerrainStore ta = a.World.Terrain!, tb = b.World.Terrain!;
        Assert.Equal(ta.Ground, tb.Ground);
        Assert.Equal(ta.Water, tb.Water);
        Assert.Equal(ta.WaterKind, tb.WaterKind);
        Assert.Equal((ta.OriginX, ta.OriginZ, ta.SamplesX, ta.SamplesZ, ta.BaseCm, ta.CellMm), (tb.OriginX, tb.OriginZ, tb.SamplesX, tb.SamplesZ, tb.BaseCm, tb.CellMm));
        Assert.Equal(0, b.CopyTerrainDirty(new TerrainDirtyRect[4], 0, out bool of));   // кольцо после Load пусто и без переполнения
        Assert.False(of);

        a.Step(300);
        b.Step(300);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void Hash_SectionCoversGroundWaterAndGeometry()
    {
        ScenarioDesc d = TerrainFixtures.Demo();
        using Simulation sim = Make(in d);
        TerrainStore t = sim.World.Terrain!;
        ulong h0 = sim.ComputeStateHash();
        t.Ground[123]++;
        ulong h1 = sim.ComputeStateHash();
        t.Ground[123]--;
        Assert.NotEqual(h0, h1);
        Assert.Equal(h0, sim.ComputeStateHash());
        t.WaterKind[7] ^= 1;
        Assert.NotEqual(h0, sim.ComputeStateHash());
        t.WaterKind[7] ^= 1;
        t.Version++;
        Assert.NotEqual(h0, sim.ComputeStateHash());
        t.Version--;

        // Тот же город без рельефа - другой хэш (раздел 14 есть только с рельефом).
        ScenarioDesc plain = WorldSaveFixtures.Small();
        using Simulation p = Make(in plain);
        Assert.NotEqual(p.ComputeStateHash(), h0);
    }

    [Fact]
    public void Demo_Hash_SameFor1And2And3Threads()
    {
        ScenarioDesc d = TerrainFixtures.Demo();
        ulong h1 = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Make(in d, threads);
            sim.Step(600);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                h1 = h;
            }

            Assert.Equal(h1, h);
        }
    }

    [Fact]
    public void Demo_ZeroBytesPerTick()
    {
        ScenarioDesc d = TerrainFixtures.Demo();
        using Simulation sim = Make(in d, 2);
        sim.Step(400);
        Assert.Equal(0, AllocationProbe.MeasureSimThreads(sim, () => sim.Step(100)));
        var buf = new TerrainDirtyRect[8];
        Assert.Equal(0, AllocationProbe.Measure(() =>
        {
            _ = sim.GroundAt(10f, 20f);
            _ = sim.CopyTerrainDirty(buf, 0, out _);
            TerrainView v = sim.Terrain;
            _ = v.Ground.Length;
        }));
    }

    // ---- валидатор ----

    [Fact]
    public void Validator_Rejects_BadTerrain()
    {
        byte[] ok = DemoSave(50);
        using (Simulation s = WorldSaveFixtures.Load(ok))
        {
            Assert.True(s.HasTerrain);
        }

        AssertRejected(Patch<byte>(ok, "TERR", 10, a => a[0] = 3));                                     // WaterKind 3 - резерв
        AssertRejected(Patch<byte>(ok, "TERR", 10, a => a[a.IndexOfAnyExcept((byte)0)] = 0));          // вода без вида
        AssertRejected(Patch<byte>(ok, "TERR", 10, a => a[a.IndexOf((byte)0)] = 1));                   // вид без воды
        AssertRejected(Patch<float>(ok, "TERR", 2, a => a[0] += 0.5f));                                // начало не на мировой сетке
        AssertRejected(Patch<int>(ok, "TERR", 7, a => a[0] = 20));                                     // HeightUnitMm != 10
        AssertRejected(Patch<int>(ok, "TERR", 1, a => a[0] = 12000));                                  // шаг вне {4, 8, 16} м
        AssertRejected(Patch<int>(ok, "TERR", 6, a => a[0] = 2_000_000));                              // база вне диапазона
        AssertRejected(Patch<int>(ok, "TERR", 11, a => a[0] = -1));
        AssertRejected(Patch<int>(ok, "PARM", 250, a => a[0] = 97));                                   // кисть > 12 · 8 м
        AssertRejected(Patch<int>(ok, "PARM", 242, a => a[0] = 0));                                    // уклон вне диапазона

        // Без рельефа параметры обязаны быть дефолтами (каноничный сейв).
        ScenarioDesc plain = WorldSaveFixtures.Small();
        using Simulation p = Make(in plain);
        byte[] none = WorldSaveFixtures.SaveBytes(p);
        AssertRejected(Patch<int>(none, "PARM", 242, a => a[0] = 81));
        AssertRejected(Patch<byte>(none, "PARM", 249, a => a[0] = 0));
    }

    [Fact]
    public void Save_PresenceFollowsFlag()
    {
        ScenarioDesc plain = WorldSaveFixtures.Small();
        using Simulation p = Make(in plain);
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(WorldSaveFixtures.SaveBytes(p), null, out SaveTables? t, out _));
        Assert.True(t!.FindBlock(SaveFormat.FourCc("TERR"u8)) < 0);
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(DemoSave(10), null, out t, out _));
        Assert.True(t!.FindBlock(SaveFormat.FourCc("TERR"u8)) >= 0);

        // Флаг выключен, а блок есть - файл не принимается.
        byte[] bad = Patch<byte>(DemoSave(10), "PARM", 241, a => a[0] = 0);
        Assert.NotEqual(LoadStatus.Ok, Simulation.Load(bad, default, default, out Simulation? s, out _));
        s?.Dispose();
    }

    private static byte[] Patch<T>(byte[] file, string block, uint columnId, SpanEdit<T> edit)
        where T : unmanaged
    {
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(file, null, out SaveTables? t, out _));
        int b = t!.FindBlock(SaveFormat.FourCc(System.Text.Encoding.ASCII.GetBytes(block)));
        Assert.True(b >= 0);
        Assert.True(t.TryGetColumn(b, columnId, out ReadOnlySpan<T> col));
        edit(MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(col), col.Length));
        SaveHeaderInfo h = t.Header;
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref t, out long len));
        byte[] buf = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, buf, out _));
        return buf;
    }

    private delegate void SpanEdit<T>(Span<T> a);

    private static void AssertRejected(byte[] bad)
    {
        LoadStatus st = LoadStatus.Ok;
        Exception? ex = Record.Exception(() => st = Simulation.Load(bad, default, default, out _, out _));
        Assert.Null(ex);
        Assert.Equal(LoadStatus.InvalidValue, st);
    }

    // ---- замеры (terrain.md §2.3, §8): память TerrainStore и прирост сейва по классам размера ----

    /// <summary>
    /// Классы: game-city ±1 720 м, s1k ±2 544 м, s10k ±4 064 м (шаг 8 м) всегда; s100k ±8 624 м (8 м) и s1m ±25 344 м (16 м) - только при <c>TC_TERRAIN_MEASURE=1</c>.
    /// Сейв - малый город поверх гладкого рельефа <see cref="TerrainFixtures.Smooth"/>; сжатие - deflate Fastest, как у конверта хоста.
    /// </summary>
    [Fact]
    public void Measure_MemoryAndSaveSize()
    {
        bool all = Environment.GetEnvironmentVariable("TC_TERRAIN_MEASURE") == "1";
        ScenarioDesc plain = WorldSaveFixtures.Small();
        long baseRaw, baseZ;
        using (Simulation p = Make(in plain))
        {
            byte[] b = WorldSaveFixtures.SaveBytes(p);
            baseRaw = b.Length;
            baseZ = Deflate(b);
        }

        _out.WriteLine($"no terrain: raw {baseRaw} B, deflate {baseZ} B");
        Row("game-city", 1720f, 8000);
        Row("s1k", 2544f, 8000);
        Row("s10k", 4064f, 8000);
        if (all)
        {
            Row("s100k", 8624f, 8000);
            Row("s1m", 25344f, 16000);
        }

        void Row(string name, float half, int cellMm)
        {
            int n = (int)(2f * half * 1000f / cellMm) + 1;
            long mem = GC.GetAllocatedBytesForCurrentThread();
            var store = new TerrainStore(cellMm, -half, -half, n, n, 0);
            mem = GC.GetAllocatedBytesForCurrentThread() - mem;
            Assert.Equal((long)n * n, store.Count);

            ScenarioDesc d = WorldSaveFixtures.Small();
            d.Terrain = TerrainFixtures.Smooth(half, cellMm);
            using Simulation sim = Make(in d);
            Assert.Equal(n, sim.World.Terrain!.SamplesX);
            byte[] save = WorldSaveFixtures.SaveBytes(sim);
            long z = Deflate(save);
            _out.WriteLine($"{name}: {n}x{n} = {(long)n * n} samples @ {cellMm / 1000} m; TerrainStore {mem} B ({mem / (1024.0 * 1024.0):F2} MiB); " +
                $"save +{save.Length - baseRaw} B raw ({(save.Length - baseRaw) / (1024.0 * 1024.0):F2} MiB), +{z - baseZ} B deflate ({(z - baseZ) / (1024.0 * 1024.0):F2} MiB)");
            Assert.True(mem < 7L * n * n + (1L << 20));
        }
    }

    private static long Deflate(byte[] b)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(b, 0, b.Length);
        }

        return ms.Length;
    }
}
