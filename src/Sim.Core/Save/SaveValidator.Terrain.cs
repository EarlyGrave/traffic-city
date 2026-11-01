using System;
using Sim.Core.Api;
using Sim.Core.Terrain;

namespace Sim.Core.Save;

// Проверки рельефа при загрузке (terrain.md §8): параметры PARM 241-255 - те же диапазоны, что Create; сетка TERR - размеры, шаг, начало на мировой сетке;
// вода - WaterKind <= 2 (3 - резерв течения, §3.4) и WaterKind = 0 <=> Water = 0.
internal static partial class SaveValidator
{
    private const int MaxAbsTerrainBaseCm = 1_000_000;

    /// <summary>PARM 241-255: диапазоны (предел кисти - по наибольшему шагу, точный - в <see cref="ValidateTerrain"/>); без рельефа - ровно дефолты (каноничный сейв).</summary>
    public static LoadStatus ValidateTerrainParams(LoadContext c)
    {
        ref readonly TerrainDesc p = ref c.Desc.Terrain;
        if (!p.Enabled && !TerrainParams.AreDefaults(in p))
        {
            return LoadStatus.InvalidValue;
        }

        return TerrainParams.Valid(in p, 16000) ? LoadStatus.Ok : LoadStatus.InvalidValue;
    }

    /// <summary>TERR 1-7 до выделения: отсчётов <c>&lt;= MaxSamples</c>, база в диапазоне, начало - клетка мировой сетки (тот же расчёт, что в Create, побитово).</summary>
    public static LoadStatus ValidateTerrainGrid(LoadContext c)
    {
        int cell = c.TerrCellMm;
        if ((long)c.TerrSamplesX * c.TerrSamplesZ > TerrainStore.MaxSamples || Math.Abs((long)c.TerrBaseCm) > MaxAbsTerrainBaseCm ||
            !Fin(c.TerrOriginX) || !Fin(c.TerrOriginZ) ||
            !OnGrid(c.TerrOriginX, c.W.WorldGridOriginX, cell) || !OnGrid(c.TerrOriginZ, c.W.WorldGridOriginZ, cell))
        {
            return LoadStatus.InvalidValue;
        }

        return LoadStatus.Ok;
    }

    private static bool OnGrid(float origin, float grid, int cellMm) =>
        BitConverter.SingleToInt32Bits(TerrainBuild.OriginFor(grid, TerrainBuild.CellIndexOf(origin, grid, cellMm), cellMm)) == BitConverter.SingleToInt32Bits(origin);

    /// <summary>Блок TERR после загрузки: вода согласована с видом, предел кисти по шагу сетки.</summary>
    public static LoadStatus ValidateTerrain(LoadContext c)
    {
        TerrainStore t = c.W.Terrain!;
        if (c.W.TerrainCfg.MaxBrushM > TerrainParams.MaxBrushPerCell * (t.CellMm / 1000))
        {
            return Bad(c, 0, 1);
        }

        ushort[] water = t.Water;
        byte[] kind = t.WaterKind;
        for (int i = 0; i < kind.Length; i++)
        {
            if (kind[i] > 2 || (kind[i] == 0) != (water[i] == TerrainStore.Dry))
            {
                return Bad(c, i, 10);
            }
        }

        return LoadStatus.Ok;
    }
}
