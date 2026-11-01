using System;
using Sim.Core.Api;
using Sim.Core.Terrain;
using Sim.Core.Tests.Save;
using Sim.Core.Util;

namespace Sim.Core.Tests.Terrain;

/// <summary>TR1 (terrain.md §2, §3.2): протяжённость, грунт flat/heightmap (развёртка в целых), вода (море, озёра, реки), запросы §2.2, кольцо грязи, отказы Create.</summary>
public sealed class TerrainCreateTests
{
    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    // Рельеф ровно как его строит TR1 (до штампа дорог TR3, terrain.md §4.4): пересборка карты в созданном мире; дороги города не перештамповываются.
    private static TerrainStore Raw(Simulation sim, in ScenarioDesc d)
    {
        Assert.Equal(CreateStatus.Ok, TerrainBuild.Build(sim.World, in d));
        return sim.World.Terrain!;
    }

    private static ScenarioDesc WithTerrain(in TerrainDesc t)
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Terrain = t;
        return d;
    }

    private static TerrainDesc Flat(float m, float minX, float minZ, float maxX, float maxZ, int cellMm = 8000) => new()
    {
        Enabled = true,
        CellMm = cellMm,
        HasExtent = true,
        ExtentMinX = minX,
        ExtentMinZ = minZ,
        ExtentMaxX = maxX,
        ExtentMaxZ = maxZ,
        Source = TerrainSource.Flat,
        FlatM = m,
    };

    /// <summary>Сетка из массива <paramref name="h"/> (см над базой 0) с шагом и началом самой сетки рельефа: развёртки нет, отсчёт = значение.</summary>
    private static TerrainDesc Grid(ushort[] h, int n, int cellMm = 8000) => new()
    {
        Enabled = true,
        CellMm = cellMm,
        HasExtent = true,
        ExtentMinX = 0f,
        ExtentMinZ = 0f,
        ExtentMaxX = (n - 1) * cellMm / 1000f,
        ExtentMaxZ = (n - 1) * cellMm / 1000f,
        Source = TerrainSource.Heightmap,
        SourceHeights = h,
        SourceSizeX = n,
        SourceSizeZ = n,
        SourceCellMm = cellMm,
    };

    [Fact]
    public void NoTerrain_NothingAllocated_ApiEmpty()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        using Simulation sim = Make(in d);
        Assert.False(sim.HasTerrain);
        Assert.Null(sim.World.Terrain);
        Assert.False(sim.Terrain.Enabled);
        Assert.True(sim.Terrain.Ground.IsEmpty);
        Assert.Equal(0f, sim.GroundAt(10f, 10f));
        Assert.Equal(0, sim.CopyTerrainDirty(new TerrainDirtyRect[4], -1, out bool of));
        Assert.False(of);
        TerrainDesc def = default;
        Assert.True(TerrainParams.AreDefaults(sim.World.TerrainCfg));
        Assert.Equal(TerrainParams.WithDefaults(in def).MaxBrushM, sim.World.TerrainCfg.MaxBrushM);
    }

    [Fact]
    public void Flat_ExtentRoundedOutward_FromWorldGridOrigin()
    {
        ScenarioDesc d = WithTerrain(Flat(12.34f, -100.5f, -50f, 101f, 63.9f));
        d.WorldGridOriginX = 4f;
        d.WorldGridOriginZ = 0f;
        using Simulation sim = Make(in d);
        TerrainStore t = sim.World.Terrain!;
        Assert.Equal(-108f, t.OriginX);                // 4 + floor(-104.5 / 8) · 8
        Assert.Equal(-56f, t.OriginZ);
        Assert.Equal(28, t.SamplesX);                  // k −14..13: −108..108 м
        Assert.Equal(16, t.SamplesZ);                  // k −7..8: −56..64 м
        Assert.Equal(0, t.BaseCm);
        Assert.All(t.Ground, g => Assert.Equal(1234, g));
        Assert.All(t.Water, v => Assert.Equal(0, v));
        Assert.Equal(12.34f, sim.GroundAt(3f, 7f));
        Assert.Equal(1234, t.GroundCmAt(1e9f, -1e9f));  // зажим к краю
        Assert.Equal(TerrainStore.ChunkSamples, sim.Terrain.ChunkSamples);
        Assert.Equal(t.Count, sim.Terrain.Lock.Length);
    }

    [Fact]
    public void Extent_FromWorldBounds_ThenNetworkBboxPlusMargin()
    {
        TerrainDesc t = Flat(1f, 0f, 0f, 0f, 0f);
        t.HasExtent = false;
        ScenarioDesc d = WithTerrain(in t);
        d.Tools.HasWorldBounds = true;
        d.Tools.BoundsMinX = -800f;
        d.Tools.BoundsMinZ = -800f;
        d.Tools.BoundsMaxX = 800f;
        d.Tools.BoundsMaxZ = 800f;
        using (Simulation sim = Make(in d))
        {
            Assert.Equal(-800f, sim.World.Terrain!.OriginX);
            Assert.Equal(201, sim.World.Terrain.SamplesX);
        }

        d.Tools.HasWorldBounds = false;
        t.MarginM = 100f;
        d.Terrain = t;
        using (Simulation sim = Make(in d))
        {
            Sim.Core.Network.NetworkStore net = sim.World.Net;
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < net.NodeCount; i++)
            {
                if (!net.NodePool.IsAlive(i))
                {
                    continue;
                }

                minX = MathF.Min(minX, net.Nodes[i].Position.X);
                maxX = MathF.Max(maxX, net.Nodes[i].Position.X);
            }

            TerrainStore s = sim.World.Terrain!;
            Assert.True(s.OriginX <= minX - 100f && s.OriginX > minX - 108f, $"{s.OriginX} vs {minX}");
            float right = s.OriginX + ((s.SamplesX - 1) * s.CellM);
            Assert.True(right >= maxX + 100f && right < maxX + 108f, $"{right} vs {maxX}");
            Assert.Equal(0f, (s.OriginX / 8f) - MathF.Floor(s.OriginX / 8f));
        }
    }

    [Fact]
    public void Heightmap_Upsampling_IsIntegerBilinear_RoundHalfUp()
    {
        // 2×2 по 32 м: h00 = 0, h10 = 100, h01 = 200, h11 = 300 (см) -> отсчёты по 8 м.
        var t = new TerrainDesc
        {
            Enabled = true,
            HasExtent = true,
            ExtentMaxX = 32f,
            ExtentMaxZ = 32f,
            Source = TerrainSource.Heightmap,
            SourceHeights = new ushort[] { 0, 100, 200, 300 },
            SourceSizeX = 2,
            SourceSizeZ = 2,
            SourceCellMm = 32000,
            BaseCm = 5000,
        };
        using (Simulation sim = Make(WithTerrain(in t)))
        {
            TerrainStore s = Raw(sim, WithTerrain(in t));
            Assert.Equal(5, s.SamplesX);
            Assert.Equal(5000, s.BaseCm);
            Assert.Equal(25, s.Ground[1]);                 // fx = 1/4
            Assert.Equal(75, s.Ground[(1 * 5) + 1]);       // 0·9/16 + 100·3/16 + 200·3/16 + 300·1/16
            Assert.Equal(125, s.Ground[(1 * 5) + 3]);      // fx = 3/4, fz = 1/4
            Assert.Equal(300, s.Ground[24]);
            Assert.Equal(5000 + 150, s.GroundCmAt(16f, 16f));
        }

        // Округление к ближайшему (половина - вверх) и единица 1 мм: 1 мм на краю -> 0.25 мм = 0 см, 5 мм -> ½ см -> 1 см.
        t.SourceHeights = new ushort[] { 0, 10, 0, 0 };
        t.SourceUnitMm = 1;
        using (Simulation sim = Make(WithTerrain(in t)))
        {
            TerrainStore s = Raw(sim, WithTerrain(in t));
            Assert.Equal(0, s.Ground[1]);     // 2.5 мм -> 0 см
            Assert.Equal(1, s.Ground[2]);     // 5 мм -> 1 см
            Assert.Equal(1, s.Ground[4]);     // 10 мм
        }

        // Единица 100 мм (дм): значение 7 -> 70 см; вне исходной сетки - зажим к краю.
        t.SourceHeights = new ushort[] { 7, 7, 7, 7 };
        t.SourceUnitMm = 100;
        t.ExtentMinX = -16f;
        using (Simulation sim = Make(WithTerrain(in t)))
        {
            Assert.All(Raw(sim, WithTerrain(in t)).Ground, g => Assert.Equal(70, g));
        }
    }

    [Fact]
    public void Create_Rejects_BadSection()
    {
        Assert.Equal(CreateStatus.InvalidNetwork, Status(Flat(1f, 0f, 0f, 100f, 100f, cellMm: 12000)));
        Assert.Equal(CreateStatus.InvalidNetwork, Status(Flat(-1f, 0f, 0f, 100f, 100f)));
        Assert.Equal(CreateStatus.InvalidNetwork, Status(Flat(700f, 0f, 0f, 100f, 100f)));
        Assert.Equal(CreateStatus.InvalidNetwork, Status(Flat(1f, 0f, 0f, 40000f, 40000f, cellMm: 4000)));   // 10 001² > MaxSamples
        TerrainDesc brush = Flat(1f, 0f, 0f, 100f, 100f, cellMm: 4000);
        brush.MaxBrushM = 49;                                                                     // > 12 · 4 м
        Assert.Equal(CreateStatus.InvalidNetwork, Status(brush));
        TerrainDesc noise = Flat(1f, 0f, 0f, 100f, 100f);
        noise.Source = TerrainSource.Noise;
        Assert.Equal(CreateStatus.NotSupported, Status(noise));
        TerrainDesc hm = Grid(new ushort[] { 1, 2, 3 }, 2);
        Assert.Equal(CreateStatus.InvalidNetwork, Status(hm));                                    // длина != 2·2
        TerrainDesc river = Flat(1f, 0f, 0f, 100f, 100f);
        river.Water.Rivers = new[] { new TerrainRiverDesc { Points = new[] { new Vec2(0f, 0f) }, WidthM = 5f, DepthM = 1f } };
        Assert.Equal(CreateStatus.InvalidNetwork, Status(river));
        river.Water.Rivers[0].Points = new[] { new Vec2(0f, 0f), new Vec2(10f, float.NaN) };
        Assert.Equal(CreateStatus.InvalidNetwork, Status(river));
        TerrainDesc sea = Flat(1f, 0f, 0f, 100f, 100f);
        sea.Water.HasSeaLevel = true;
        sea.Water.SeaLevelM = 900f;                                                               // выше 655 м над базой
        Assert.Equal(CreateStatus.InvalidNetwork, Status(sea));

        // Выключенная секция не читается вовсе.
        TerrainDesc off = noise;
        off.Enabled = false;
        Assert.Equal(CreateStatus.Ok, Status(off));
    }

    private static CreateStatus Status(in TerrainDesc t)
    {
        ScenarioDesc d = WithTerrain(in t);
        CreateStatus st = Simulation.TryCreate(in d, default, out Simulation? sim);
        sim?.Dispose();
        return st;
    }

    [Fact]
    public void Sea_FillsFromEdges_RidgeAndExistingWaterAreWalls()
    {
        // 9×9: всё 500 см; кольцо 1000 см вокруг внутренней ямы 3×3 (500 см). Море 800 см заливает внешнее кольцо и не проходит в яму.
        const int n = 9;
        var h = new ushort[n * n];
        for (int iz = 0; iz < n; iz++)
        {
            for (int ix = 0; ix < n; ix++)
            {
                bool ring = (ix == 2 || ix == 6 || iz == 2 || iz == 6) && ix >= 2 && ix <= 6 && iz >= 2 && iz <= 6;
                h[(iz * n) + ix] = ring ? (ushort)1000 : (ushort)500;
            }
        }

        TerrainDesc t = Grid(h, n);
        t.Water.HasSeaLevel = true;
        t.Water.SeaLevelM = 8f;
        t.Water.Lakes = new[] { new TerrainLakeDesc { X = 32f, Z = 32f, LevelM = 9f }, new TerrainLakeDesc { X = 0f, Z = 0f, LevelM = 9.5f } };
        using Simulation sim = Make(WithTerrain(in t));
        TerrainStore s = sim.World.Terrain!;
        for (int iz = 0; iz < n; iz++)
        {
            for (int ix = 0; ix < n; ix++)
            {
                int i = (iz * n) + ix;
                bool inner = ix >= 3 && ix <= 5 && iz >= 3 && iz <= 5;
                bool ring = !inner && ix >= 2 && ix <= 6 && iz >= 2 && iz <= 6;
                ushort expect = inner ? (ushort)900 : (ring ? (ushort)0 : (ushort)800);   // яма - озеро 9 м; второе озеро в море - вода моря стена, ничего не меняет
                Assert.Equal(expect, s.Water[i]);
                Assert.Equal(expect == 0 ? 0 : 1, s.WaterKind[i]);
            }
        }

        Assert.True(s.WetAt(0f, 0f));
        Assert.False(s.WetAt(16f, 16f));
        Assert.True(sim.Terrain.Water[(4 * n) + 4] > sim.Terrain.Ground[(4 * n) + 4]);
    }

    [Fact]
    public void River_MirrorNeverRises_ChannelCarved_BankSlope()
    {
        using Simulation sim = Make(TerrainFixtures.Demo());
        TerrainStore s = Raw(sim, TerrainFixtures.Demo());
        using Simulation flatSim = Make(WithTerrain(DemoWithoutWater()));
        TerrainStore orig = Raw(flatSim, WithTerrain(DemoWithoutWater()));
        Assert.Equal(orig.Count, s.Count);

        // Вдоль оси (700, 700) -> (−700, −700): зеркало не растёт, мокро, грунт ниже зеркала на глубину (или 0).
        int prevWater = int.MaxValue;
        int wetOnAxis = 0;
        for (int k = 0; k <= 175; k++)
        {
            float p = 700f - (k * 8f);
            int i = s.NearestSample(p, p);
            if (s.WaterKind[i] == 1 && s.Water[i] > s.Ground[i])
            {
                wetOnAxis++;
            }

            if (orig.Water[i] == 0 && s.Water[i] != 0 && s.Water[i] != orig.Water[i] && !InLakeOrSea(s, i))
            {
                Assert.True(s.Water[i] <= prevWater, $"зеркало выросло на шаге {k}: {s.Water[i]} > {prevWater}");
                prevWater = s.Water[i];
                Assert.True(s.Ground[i] <= Math.Max(0, s.Water[i] - 150));
            }
        }

        Assert.True(wetOnAxis > 150, $"мокрых отсчётов на оси {wetOnAxis}");

        // Берег: в полосе hw..2hw от оси грунт не выше W + (d − hw)·100/3 и не выше исходного; дальше 2hw - не тронут (кроме моря и озера, они грунт не меняют).
        int changedFar = 0;
        for (int i = 0; i < s.Count; i++)
        {
            Assert.True(s.Ground[i] <= orig.Ground[i]);
            float x = s.OriginX + ((i % s.SamplesX) * s.CellM);
            float z = s.OriginZ + ((i / s.SamplesX) * s.CellM);
            float d = DistToDiagonal(x, z);
            if (d > 16.01f && s.Ground[i] != orig.Ground[i])
            {
                changedFar++;
            }
        }

        Assert.Equal(0, changedFar);
        for (int i = 0; i < s.Count; i++)
        {
            Assert.True(s.WaterKind[i] <= 1);
            Assert.Equal(s.WaterKind[i] == 0, s.Water[i] == 0);
        }
    }

    private static bool InLakeOrSea(TerrainStore s, int i) => s.Water[i] == 3600 || s.Water[i] == 3760;

    private static float DistToDiagonal(float x, float z)
    {
        // Отрезок (700, 700) - (−700, −700) на диагонали x = z.
        float u = Math.Clamp((x + z) * 0.5f, -700f, 700f);
        float ex = x - u, ez = z - u;
        return MathF.Sqrt((ex * ex) + (ez * ez));
    }

    private static TerrainDesc DemoWithoutWater()
    {
        TerrainDesc t = TerrainFixtures.DemoTerrain();
        t.Water = default;
        return t;
    }

    [Fact]
    public void Demo_LakeInPit_SeaInCorner()
    {
        using Simulation sim = Make(TerrainFixtures.Demo());
        TerrainStore s = sim.World.Terrain!;
        int lake = s.NearestSample(384f, -384f);
        Assert.Equal(3760, s.Water[lake]);
        Assert.True(s.Water[lake] > s.Ground[lake]);
        int sea = 0, lakeN = 0;
        for (int i = 0; i < s.Count; i++)
        {
            sea += s.Water[i] == 3600 ? 1 : 0;
            lakeN += s.Water[i] == 3760 ? 1 : 0;
        }

        Assert.True(lakeN > 10 && lakeN < 2000, $"озеро {lakeN}");
        Assert.True(sea > 0, "море в нижнем углу");
    }

    [Fact]
    public void Queries_BilinearClampNearestCornerRange()
    {
        const int n = 3;
        TerrainDesc t = Grid(new ushort[] { 0, 100, 200, 300, 400, 500, 600, 700, 800 }, n);
        t.BaseCm = -50;
        using Simulation sim = Make(WithTerrain(in t));
        TerrainStore s = Raw(sim, WithTerrain(in t));
        Assert.Equal(-50, s.GroundCmAt(0f, 0f));
        Assert.Equal(-50 + 400, s.GroundCmAt(8f, 8f));
        Assert.Equal(-50 + 50, s.GroundCmAt(4f, 0f));
        Assert.Equal(-50 + 200, s.GroundCmAt(4f, 4f));    // (0 + 100 + 300 + 400) / 4
        Assert.Equal(-50 + 800, s.GroundCmAt(100f, 100f));
        Assert.Equal(-50, s.GroundCmAt(float.NaN, -5f));
        Assert.Equal(7.5f, s.GroundAt(16f, 16f));
        Assert.Equal(4, s.NearestSample(9f, 7f));
        Assert.True(s.CornerRange(1, 1, 5, 5, out int lo, out int hi));
        Assert.Equal(-50 + 400, lo);
        Assert.Equal(-50 + 800, hi);
        Assert.False(s.CornerRange(3, 0, 2, 2, out _, out _));
    }

    [Fact]
    public void DirtyRing_ChunksOverflowAndOrder()
    {
        TerrainDesc t = Flat(1f, 0f, 0f, 1024f, 512f);      // 129 × 65 отсчётов: 4 × 2 чанка
        using Simulation sim = Make(WithTerrain(in t));
        TerrainStore s = sim.World.Terrain!;
        Assert.Equal(4, s.ChunksX);
        Assert.Equal(2, s.ChunksZ);
        var buf = new TerrainDirtyRect[8];
        Assert.Equal(0, sim.CopyTerrainDirty(buf, 0, out bool of));
        Assert.False(of);

        s.Version = 1;
        s.MarkSamples(32, 0, 32, 0);                          // отсчёт на границе чанков 0 и 1
        s.Version = 2;
        s.MarkSamples(128, 64, 128, 64);                      // последний отсчёт - последний чанк
        Assert.Equal(2, sim.CopyTerrainDirty(buf, 0, out of));
        Assert.False(of);
        Assert.Equal((1, 0, 0, 1, 0), (buf[0].Version, buf[0].ChunkX0, buf[0].ChunkZ0, buf[0].ChunkX1, buf[0].ChunkZ1));
        Assert.Equal((2, 3, 1, 3, 1), (buf[1].Version, buf[1].ChunkX0, buf[1].ChunkZ0, buf[1].ChunkX1, buf[1].ChunkZ1));
        Assert.Equal(1, sim.CopyTerrainDirty(buf, 1, out _));
        Assert.Equal(1, sim.CopyTerrainDirty(buf.AsSpan(0, 1), 0, out of));
        Assert.True(of);                                      // не влезло в dst

        for (int v = 3; v <= 3 + TerrainDirtyRing.Capacity; v++)
        {
            s.Version = v;
            s.MarkSamples(0, 0, 0, 0);
        }

        Assert.Equal(0, sim.CopyTerrainDirty(buf, 3 + TerrainDirtyRing.Capacity, out of));
        Assert.False(of);
        sim.CopyTerrainDirty(buf, 1, out of);
        Assert.True(of);                                      // записи 2 и 3 вытеснены
        Assert.Equal(12, System.Runtime.CompilerServices.Unsafe.SizeOf<TerrainDirtyRect>());
    }
}
