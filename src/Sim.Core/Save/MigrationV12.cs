using Sim.Core.Api;
using Sim.Core.Terrain;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 11 -> 12 (save-format.md §4, §12; ADR-0036, terrain.md §8, задача TR1): рельеф и вода. Чистая функция над <see cref="SaveTables"/>.
/// Мир из файла v11 - без рельефа: <c>PARM</c> 241 = 0, 242-255 - дефолты (<see cref="TerrainParams"/>), блок <c>TERR</c> не создаётся. Раздел хэша рельефа пишется
/// только при рельефе, поэтому мир из файла w1-w11 продолжается тем же хэшем (TR-I1).
/// </summary>
internal static class MigrationV12
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        if (parm < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        TerrainDesc none = default;
        TerrainDesc d = TerrainParams.WithDefaults(in none);
        bool ok = t.SetColumn(parm, 241, new byte[1]);
        ok &= t.SetColumn(parm, 242, new[] { d.MaxGradeStreetPm });
        ok &= t.SetColumn(parm, 243, new[] { d.MaxGradeArterialPm });
        ok &= t.SetColumn(parm, 244, new[] { d.MaxCutCm });
        ok &= t.SetColumn(parm, 245, new[] { d.MaxFillCm });
        ok &= t.SetColumn(parm, 246, new[] { d.BankSlopePm });
        ok &= t.SetColumn(parm, 247, new[] { d.BridgeClearanceCm });
        ok &= t.SetColumn(parm, 248, new[] { d.MaxBridgeM });
        ok &= t.SetColumn(parm, 249, new byte[] { 1 });
        ok &= t.SetColumn(parm, 250, new[] { d.MaxBrushM });
        ok &= t.SetColumn(parm, 251, new[] { d.MaxDabCm });
        ok &= t.SetColumn(parm, 252, new[] { d.MaxSamplesPerTick });
        ok &= t.SetColumn(parm, 253, new[] { d.MaxFloodSamples });
        ok &= t.SetColumn(parm, 254, new[] { d.LotsMaxSlopePct });
        ok &= t.SetColumn(parm, 255, new[] { d.GradeMaxBoostPm });
        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
