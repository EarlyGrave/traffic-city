using System;
using Sim.Core.Api;
using Sim.Core.Tests.Save;
using Sim.Core.Util;

namespace Sim.Core.Tests.Terrain;

/// <summary>Сценарии рельефа для тестов TR1 (terrain.md §12): малый город <see cref="WorldSaveFixtures.Small"/> поверх синтетической карты высот.</summary>
internal static class TerrainFixtures
{
    public const int DemoSourceSize = 65;          // 65 × 65 отсчётов по 32 м: −1024..+1024 м
    public const float DemoSourceOrigin = -1024f;

    /// <summary>
    /// <c>terrain-demo</c>: малый город (±600 м), протяжённость - bbox сети + 200 м, сетка 32 м развёрнута на 8 м, база 100 м. Уклон к углу (−X, −Z), яма с озером
    /// у (384, −384), море в нижнем углу, река по диагонали от (700, 700) к (−700, −700).
    /// </summary>
    public static ScenarioDesc Demo()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Terrain = DemoTerrain();
        return d;
    }

    public static TerrainDesc DemoTerrain()
    {
        var t = new TerrainDesc
        {
            Enabled = true,
            CellMm = 8000,
            MarginM = 200f,
            Source = TerrainSource.Heightmap,
            SourceHeights = DemoHeights(),
            SourceSizeX = DemoSourceSize,
            SourceSizeZ = DemoSourceSize,
            SourceCellMm = 32000,
            SourceOriginX = DemoSourceOrigin,
            SourceOriginZ = DemoSourceOrigin,
            BaseCm = 10000,
            SourceUnitMm = 10,
        };
        t.Water.HasSeaLevel = true;
        t.Water.SeaLevelM = 136f;                                              // 3 600 см над базой: угол протяжённости (−800, −800) - 3 455 см
        t.Water.Lakes = new[] { new TerrainLakeDesc { X = 384f, Z = -384f, LevelM = 137.6f } };   // дно ямы 2 260 см, озеро 3 760 см, край ямы >= 4 860 см
        t.Water.Rivers = new[]
        {
            new TerrainRiverDesc { Points = new[] { new Vec2(700f, 700f), new Vec2(0f, 0f), new Vec2(-700f, -700f) }, WidthM = 16f, DepthM = 1.5f, FlowM3s = float.NaN },
        };
        return t;
    }

    /// <summary>Сетка ямы: <c>h = 3000 + 40·ix + 25·iz − max(0, 3000 − 300·(|ix−44| + |iz−20|))</c> см.</summary>
    public static ushort[] DemoHeights()
    {
        var h = new ushort[DemoSourceSize * DemoSourceSize];
        for (int iz = 0; iz < DemoSourceSize; iz++)
        {
            for (int ix = 0; ix < DemoSourceSize; ix++)
            {
                int pit = Math.Max(0, 3000 - (300 * (Math.Abs(ix - 44) + Math.Abs(iz - 20))));
                h[(iz * DemoSourceSize) + ix] = (ushort)(3000 + (40 * ix) + (25 * iz) - pit);
            }
        }

        return h;
    }

    /// <summary>
    /// Гладкий синтетический рельеф для замеров памяти и сжатия: сетка 32 м на квадрате <paramref name="halfM"/>, перепад ≈ 70 м (как у Юрьева, terrain.md §1):
    /// сумма двух «треугольных волн» разного периода плюс общий уклон, всё в целых сантиметрах; река по диагонали.
    /// </summary>
    public static TerrainDesc Smooth(float halfM, int cellMm)
    {
        int n = (int)Math.Ceiling(2f * halfM / 32f) + 1;
        var h = new ushort[n * n];
        for (int iz = 0; iz < n; iz++)
        {
            for (int ix = 0; ix < n; ix++)
            {
                int a = Tri(ix + (2 * iz), 90) * 3000 / 90;      // до 30 м, период 90 · 32 м
                int b = Tri((3 * ix) - iz + 1000, 50) * 1500 / 50; // до 15 м
                h[(iz * n) + ix] = (ushort)(1000 + a + b + (((ix + iz) * 2500) / (2 * n)));
            }
        }

        var t = new TerrainDesc
        {
            Enabled = true,
            CellMm = cellMm,
            HasExtent = true,
            ExtentMinX = -halfM,
            ExtentMinZ = -halfM,
            ExtentMaxX = halfM,
            ExtentMaxZ = halfM,
            Source = TerrainSource.Heightmap,
            SourceHeights = h,
            SourceSizeX = n,
            SourceSizeZ = n,
            SourceCellMm = 32000,
            SourceOriginX = -halfM,
            SourceOriginZ = -halfM,
            BaseCm = 12000,
        };
        t.Water.Rivers = new[] { new TerrainRiverDesc { Points = new[] { new Vec2(-halfM, -halfM), new Vec2(halfM, halfM) }, WidthM = 18f, DepthM = 2f } };
        return t;
    }

    private static int Tri(int v, int period)
    {
        int m = ((v % (2 * period)) + (2 * period)) % (2 * period);
        return m <= period ? m : (2 * period) - m;
    }
}
