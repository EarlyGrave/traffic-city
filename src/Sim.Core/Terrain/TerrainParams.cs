using Sim.Core.Api;

namespace Sim.Core.Terrain;

/// <summary>
/// Параметры рельефа (terrain.md §8, PARM 242-255): дефолты и допустимые диапазоны. Дефолты применяются всегда; без рельефа конфигурация мира -
/// чистые дефолты (их же пишет <c>MigrationV12</c>), поэтому сейв мира без рельефа каноничен.
/// </summary>
internal static class TerrainParams
{
    public const int DefaultCellMm = 8000;
    public const float DefaultMarginM = 1024f;
    public const int DefaultMaxGradeStreetPm = 80;
    public const int DefaultMaxGradeArterialPm = 60;
    public const int DefaultMaxCutCm = 1000;
    public const int DefaultMaxFillCm = 600;
    public const int DefaultBankSlopePm = 500;
    public const int DefaultBridgeClearanceCm = 200;
    public const int DefaultMaxBridgeM = 300;
    public const int DefaultMaxBrushM = 96;
    public const int DefaultMaxDabCm = 200;
    public const int DefaultMaxSamplesPerTick = 32768;
    public const int DefaultMaxFloodSamples = 65536;
    public const int DefaultLotsMaxSlopePct = 15;
    public const int DefaultGradeMaxBoostPm = 1100;
    public const int MaxBrushPerCell = 12;   // max_brush_m <= 12 · cell_m (terrain.md §11, риск 7: лимит undo)

    /// <summary>Параметры с дефолтами вместо нулей; массивы (сетка, вода) не переносятся - это конфигурация мира, а не исходные данные.</summary>
    public static TerrainDesc WithDefaults(in TerrainDesc d)
    {
        var r = default(TerrainDesc);
        r.Enabled = d.Enabled;
        r.CellMm = d.CellMm == 0 ? DefaultCellMm : d.CellMm;
        r.MarginM = d.MarginM == 0f ? DefaultMarginM : d.MarginM;
        r.MaxGradeStreetPm = Or(d.MaxGradeStreetPm, DefaultMaxGradeStreetPm);
        r.MaxGradeArterialPm = Or(d.MaxGradeArterialPm, DefaultMaxGradeArterialPm);
        r.MaxCutCm = Or(d.MaxCutCm, DefaultMaxCutCm);
        r.MaxFillCm = Or(d.MaxFillCm, DefaultMaxFillCm);
        r.BankSlopePm = Or(d.BankSlopePm, DefaultBankSlopePm);
        r.BridgeClearanceCm = Or(d.BridgeClearanceCm, DefaultBridgeClearanceCm);
        r.MaxBridgeM = Or(d.MaxBridgeM, DefaultMaxBridgeM);
        r.NoAutoBridge = d.NoAutoBridge;
        r.MaxBrushM = Or(d.MaxBrushM, DefaultMaxBrushM);
        r.MaxDabCm = Or(d.MaxDabCm, DefaultMaxDabCm);
        r.MaxSamplesPerTick = Or(d.MaxSamplesPerTick, DefaultMaxSamplesPerTick);
        r.MaxFloodSamples = Or(d.MaxFloodSamples, DefaultMaxFloodSamples);
        r.LotsMaxSlopePct = Or(d.LotsMaxSlopePct, DefaultLotsMaxSlopePct);
        r.GradeMaxBoostPm = Or(d.GradeMaxBoostPm, DefaultGradeMaxBoostPm);
        return r;
    }

    /// <summary>Конфигурация мира: с рельефом - параметры сценария с дефолтами, без рельефа - чистые дефолты.</summary>
    public static TerrainDesc ConfigFor(in TerrainDesc d)
    {
        if (d.Enabled)
        {
            return WithDefaults(in d);
        }

        TerrainDesc none = default;
        return WithDefaults(in none);
    }

    /// <summary>Диапазоны параметров (после дефолтов). <paramref name="cellMm"/> - шаг отсчётов (предел кисти).</summary>
    public static bool Valid(in TerrainDesc p, int cellMm) =>
        In(p.MaxGradeStreetPm, 1, 1000) &&
        In(p.MaxGradeArterialPm, 1, 1000) &&
        In(p.MaxCutCm, 0, ushort.MaxValue) &&
        In(p.MaxFillCm, 0, ushort.MaxValue) &&
        In(p.BankSlopePm, 1, 10000) &&
        In(p.BridgeClearanceCm, 0, ushort.MaxValue) &&
        In(p.MaxBridgeM, 1, 100000) &&
        In(p.MaxBrushM, 1, MaxBrushPerCell * (cellMm / 1000)) &&
        In(p.MaxDabCm, 1, ushort.MaxValue) &&
        In(p.MaxSamplesPerTick, 1, TerrainStore.MaxSamples) &&
        In(p.MaxFloodSamples, 1, TerrainStore.MaxSamples) &&
        In(p.LotsMaxSlopePct, 1, 100) &&
        In(p.GradeMaxBoostPm, 1000, 2000);

    /// <summary>Совпадают ли параметры с дефолтами (каноничный сейв мира без рельефа).</summary>
    public static bool AreDefaults(in TerrainDesc p)
    {
        TerrainDesc none = default;
        TerrainDesc d = WithDefaults(in none);
        return p.MaxGradeStreetPm == d.MaxGradeStreetPm && p.MaxGradeArterialPm == d.MaxGradeArterialPm && p.MaxCutCm == d.MaxCutCm &&
            p.MaxFillCm == d.MaxFillCm && p.BankSlopePm == d.BankSlopePm && p.BridgeClearanceCm == d.BridgeClearanceCm && p.MaxBridgeM == d.MaxBridgeM &&
            p.NoAutoBridge == d.NoAutoBridge && p.MaxBrushM == d.MaxBrushM && p.MaxDabCm == d.MaxDabCm && p.MaxSamplesPerTick == d.MaxSamplesPerTick &&
            p.MaxFloodSamples == d.MaxFloodSamples && p.LotsMaxSlopePct == d.LotsMaxSlopePct && p.GradeMaxBoostPm == d.GradeMaxBoostPm;
    }

    public static bool ValidCell(int cellMm) => cellMm == 4000 || cellMm == 8000 || cellMm == 16000;

    private static int Or(int v, int def) => v == 0 ? def : v;

    private static bool In(int v, int lo, int hi) => v >= lo && v <= hi;
}
