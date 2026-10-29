using System;
using System.Threading;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Host.Tests;

/// <summary>
/// <c>TerrainSnapshot</c> (terrain.md §10.1, TR9): чанки с копированием при записи, полная копия на старте, тот же объект без изменений (0 Б на кадр),
/// null без рельефа. Путь «грязных» чанков - синтетически через <c>TerrainSnapshot.Build</c> (ядро пока не умеет менять рельеф).
/// </summary>
[Collection("HostThreads")]
public class TerrainSnapshotTests
{
    private static ScenarioDesc TerrainDesc()
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve("s1k", null));
        d.Terrain = new TerrainDesc
        {
            Enabled = true,
            CellMm = 8000,
            Source = TerrainSource.Flat,
            FlatM = 100f,
        };
        return d;
    }

    private static SimThreadHost Start(ScenarioDesc d) => new(Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 }));

    private static SimFrame RunTo(SimThreadHost host, long tick)
    {
        host.SetTargetTick(tick);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (host.TryAcquireFrame(out SimFrame f) && f.SnapshotTick == tick)
            {
                return f;
            }

            Assert.True(host.State == HostState.Running, "хост остановился: " + host.Fault);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120), "таймаут");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void WorldWithoutTerrain_FrameCarriesNull()
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve("s1k", null));
        using SimThreadHost host = Start(d);
        Assert.Null(RunTo(host, 40).Terrain);
    }

    [Fact]
    public void FirstFrame_IsFullCopy_MatchingCoreView()
    {
        ScenarioDesc d = TerrainDesc();
        using Simulation direct = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        using SimThreadHost host = Start(d);
        SimFrame f = RunTo(host, 20);
        TerrainSnapshot? s = f.Terrain;
        Assert.NotNull(s);
        TerrainView v = direct.Terrain;
        Assert.True(v.Enabled);
        Assert.True(s!.Full);
        Assert.Equal(-1, s.PreviousVersion);
        Assert.Equal(v.Version, s.Version);
        Assert.Equal(v.SamplesX, s.SamplesX);
        Assert.Equal(v.SamplesZ, s.SamplesZ);
        Assert.Equal(v.BaseCm, s.BaseCm);
        Assert.Equal(s.ChunkCount, s.ChangedChunks.Length);
        for (int cz = 0; cz < s.ChunksZ; cz++)
        {
            for (int cx = 0; cx < s.ChunksX; cx++)
            {
                TerrainChunk c = s.GetChunk(cx, cz);
                for (int lz = 0; lz < c.Height; lz++)
                {
                    for (int lx = 0; lx < c.Width; lx++)
                    {
                        int gi = ((c.Z0 + lz) * v.SamplesX) + c.X0 + lx;
                        Assert.Equal(v.Ground[gi], c.Ground[(lz * c.Width) + lx]);
                        Assert.Equal(v.Water[gi], c.Water[(lz * c.Width) + lx]);
                    }
                }
            }
        }
    }

    [Fact]
    public void Unchanged_SameReference_AndZeroBytes_OnBothThreads()
    {
        ScenarioDesc d = TerrainDesc();
        using SimThreadHost host = Start(d);
        RunTo(host, 2600);
        TerrainSnapshot first = RunTo(host, 2620).Terrain!;
        long next = 2620;
        long simDelta = -1, mainDelta = -1;
        for (int attempt = 0; attempt < 30 && simDelta != 0; attempt++)
        {
            int gc = GC.CollectionCount(0);
            SimFrame f0 = RunTo(host, next += 20);
            Assert.Same(first, f0.Terrain);
            long sim0 = f0.SimThreadAllocatedBytes;
            long main0 = GC.GetAllocatedBytesForCurrentThread();
            long goal = next + 200;
            host.SetTargetTick(goal);
            while (host.Tick < goal)
            {
                if (host.TryAcquireFrame(out SimFrame f))
                {
                    Assert.Same(first, f.Terrain);
                }

                host.TryReadResponse(out _);
            }

            host.TryAcquireFrame(out _);
            long main1 = GC.GetAllocatedBytesForCurrentThread();
            long sim1 = host.SimThreadAllocatedBytes;
            next = goal;
            if (GC.CollectionCount(0) == gc)
            {
                simDelta = sim1 - sim0;
                mainDelta = main1 - main0;
            }
        }

        Assert.Equal(0, mainDelta);
        Assert.Equal(0, simDelta);
    }

    private static TerrainSnapshot Build(TerrainSnapshot? prev, int version, ushort[] g, ReadOnlySpan<TerrainDirtyRect> dirty, bool overflow, int n = 100)
        => TerrainSnapshot.Build(prev, version, 8f, 0f, 0f, n, n, 0, g, new ushort[g.Length], new byte[g.Length], new byte[g.Length], dirty, overflow);

    [Fact]
    public void Dirty_CopiesOnlyDirtyChunks_AndSharesTheRest()
    {
        const int n = 100;   // 99 клеток -> 4×4 чанка, последний 3 клетки
        var g = new ushort[n * n];
        for (int i = 0; i < g.Length; i++)
        {
            g[i] = (ushort)(i % 1000);
        }

        TerrainSnapshot a = Build(null, 1, g, default, false);
        Assert.Equal(16, a.ChunkCount);
        Assert.Equal(33, a.GetChunk(0, 0).Width);
        Assert.Equal(4, a.GetChunk(3, 3).Width);   // отсчёты 96..99

        g[(40 * n) + 40] = 5000;   // чанк (1,1)
        var rect = new TerrainDirtyRect { Version = 2, ChunkX0 = 1, ChunkZ0 = 1, ChunkX1 = 1, ChunkZ1 = 1 };
        TerrainSnapshot b = Build(a, 2, g, new[] { rect }, false);
        Assert.False(b.Full);
        Assert.Equal(1, a.Version);
        Assert.Equal(1, b.PreviousVersion);
        Assert.Equal("5", string.Join(",", b.ChangedChunks.ToArray()));
        for (int i = 0; i < 16; i++)
        {
            if (i == 5)
            {
                Assert.NotSame(a.GetChunk(i), b.GetChunk(i));
                Assert.Equal(2, b.GetChunk(i).Version);
            }
            else
            {
                Assert.Same(a.GetChunk(i), b.GetChunk(i));
                Assert.Equal(1, b.GetChunk(i).Version);
            }
        }

        // старый снимок не тронут (render thread держит его)
        Assert.NotEqual((ushort)5000, a.GetChunk(1, 1).Ground[(8 * 33) + 8]);
        Assert.Equal((ushort)5000, b.GetChunk(1, 1).Ground[(8 * 33) + 8]);
    }

    [Fact]
    public void Dirty_OverlappingRects_CountOnce_AndOverflowForcesFullCopy()
    {
        var g = new ushort[100 * 100];
        TerrainSnapshot a = Build(null, 1, g, default, false);
        TerrainDirtyRect[] r =
        {
            new() { Version = 2, ChunkX0 = 0, ChunkZ0 = 0, ChunkX1 = 1, ChunkZ1 = 1 },
            new() { Version = 3, ChunkX0 = 1, ChunkZ0 = 1, ChunkX1 = 2, ChunkZ1 = 1 },
        };
        TerrainSnapshot b = Build(a, 3, g, r, false);
        Assert.Equal("0,1,4,5,6", string.Join(",", b.ChangedChunks.ToArray()));

        TerrainSnapshot c = Build(b, 4, g, default, true);
        Assert.True(c.Full);
        Assert.Equal(16, c.ChangedChunks.Length);
        Assert.NotSame(b.GetChunk(15), c.GetChunk(15));
    }
}
