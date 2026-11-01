using System.Runtime.InteropServices;
using Sim.Core.Util;

namespace Sim.Core.Api;

/// <summary>Источник карты высот (terrain.md §2.4, §9.1).</summary>
public enum TerrainSource : int
{
    Flat = 0,          // все отсчёты на FlatM, BaseCm = 0
    Heightmap = 1,     // сетка SourceHeights, билинейно в целых на CellMm (§9.1)
    Noise = 2,         // генератор (TR7); в TR1 - CreateStatus.NotSupported
}

/// <summary>
/// Секция <c>terrain</c> сценария (terrain.md §9.1, ADR-0036), уже разобранная загрузчиком (TR2): ядро файлов не читает. <see cref="Enabled"/> = false
/// (нулевая структура) - рельефа нет: массивы не выделяются, состояние, хэш, сейв и поведение побитово прежние (TR-I1).
/// Нули в параметрах = дефолты §9.1 (<see cref="Terrain.TerrainParams.WithDefaults"/>). Высоты - целые сантиметры над <see cref="BaseCm"/>.
/// </summary>
public struct TerrainDesc
{
    public bool Enabled;
    public int CellMm;                      // шаг отсчётов: 4000 | 8000 | 16000; 0 = 8000
    public float MarginM;                   // поле вокруг bbox сети, если нет extent и tools.world_bounds; 0 = 1024
    public bool HasExtent;                  // terrain.extent задан
    public float ExtentMinX, ExtentMinZ, ExtentMaxX, ExtentMaxZ;   // м, мир

    public TerrainSource Source;
    public float FlatM;                     // source = flat: высота всех отсчётов, м (0..655.35)

    // ---- source = heightmap (TR2 разбирает .r16 / data_b64) ----
    public ushort[] SourceHeights;          // [SourceSizeX * SourceSizeZ], строка за строкой по Z; значение = (h - base) / unit
    public int SourceSizeX, SourceSizeZ;    // >= 2
    public int SourceCellMm;                // шаг исходной сетки, мм (32 м = 32000); кратен CellMm
    public float SourceOriginX, SourceOriginZ;   // мир отсчёта (0, 0) исходной сетки, м
    public int BaseCm;                      // абсолютная высота нуля, см (base_m · 100)
    public int SourceUnitMm;                // единица исходных значений, мм (unit_m · 1000); 0 = 10 (1 см)

    public TerrainWaterDesc Water;

    // ---- параметры (PARM 242-255, terrain.md §8); 0 = дефолт ----
    public int MaxGradeStreetPm;            // ‰; 80 (8%)
    public int MaxGradeArterialPm;          // ‰; 60
    public int MaxCutCm;                    // 1000
    public int MaxFillCm;                   // 600
    public int BankSlopePm;                 // откос насыпи, ‰ (высота/заложение); 500 = 1:2
    public int BridgeClearanceCm;           // 200
    public int MaxBridgeM;                  // 300
    public bool NoAutoBridge;               // auto_bridge = false (дефолт true)
    public int MaxBrushM;                   // 96; <= 12 · cell_m (terrain.md §11, риск 7)
    public int MaxDabCm;                    // 200
    public int MaxSamplesPerTick;           // 32768
    public int MaxFloodSamples;             // 65536
    public int LotsMaxSlopePct;             // lots.max_slope_pct; 15
    public int GradeMaxBoostPm;             // потолок множителя скорости под гору, ‰; 1100
}

/// <summary>Вода сценария (terrain.md §3.2, §3.4). Вся вода статичная.</summary>
public struct TerrainWaterDesc
{
    public bool HasSeaLevel;                // water.sea_level_m не null
    public float SeaLevelM;                 // абсолютная, м
    public TerrainLakeDesc[] Lakes;
    public TerrainRiverDesc[] Rivers;
    public TerrainWaterSourceDesc[] Sources;   // резерв §3.4: ядро MVP не исполняет, не сохраняет и не хэширует
}

/// <summary>Озеро: заливка от ближайшего к (X, Z) отсчёта до уровня <see cref="LevelM"/> (абсолютный, м).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TerrainLakeDesc
{
    public float X, Z, LevelM;
}

/// <summary>Река: полилиния от истока, зеркало вниз по течению не растёт (terrain.md §3.2).</summary>
public struct TerrainRiverDesc
{
    public Vec2[] Points;                   // >= 2, мир, м
    public float WidthM, DepthM;            // > 0
    public float FlowM3s;                   // резерв §3.4 (rivers[].flow_m3s): не исполняется; NaN = null
}

/// <summary>Вид источника воды (резерв §3.4).</summary>
public enum TerrainWaterSourceKind : int
{
    Spring = 0,
    Inflow = 1,
    Sink = 2,
}

/// <summary>Источник воды (резерв §3.4): валидирует загрузчик (TR2), ядро MVP только передаёт дальше.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TerrainWaterSourceDesc
{
    public int Id;
    public TerrainWaterSourceKind Kind;
    public float X, Z, FlowM3s, TargetLevelM, RadiusM;   // NaN = null
}
