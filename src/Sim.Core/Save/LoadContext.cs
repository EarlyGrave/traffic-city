using System;
using Sim.Core.Api;
using Sim.Core.Routing;

namespace Sim.Core.Save;

/// <summary>
/// Состояние загрузки одного сейва: параметры, собранные блоками до создания мира (<c>PARM</c>, <c>DEMD</c>, <c>VTYP</c>), мир, который заполняют остальные блоки,
/// и промежуточные скаляры, которым нужны соседи по блоку. Живёт только внутри <see cref="WorldLoader"/>.
/// </summary>
internal sealed class LoadContext
{
    public LoadContext(in LoadOptions options, in SimRuntimeOptions runtime)
    {
        Options = options;
        Runtime = runtime;
        Array.Fill(Group, -1L);
    }

    public LoadOptions Options;
    public SimRuntimeOptions Runtime;

    /// <summary>Мир; null, пока не прочитаны блоки параметров.</summary>
    public World W = null!;

    // ---- параметры (блоки до создания мира) ----
    public ScenarioDesc Desc;
    public RulesDesc Rules;
    public RoutingDesc Routing;
    public LodDesc Lod;
    public VehicleTypeDesc[] Types = Array.Empty<VehicleTypeDesc>();
    public ZoneDesc[] Zones = Array.Empty<ZoneDesc>();
    public OdPairDesc[] OdPairs = Array.Empty<OdPairDesc>();
    public float[] TypeMix = Array.Empty<float>();
    public float NetLaneWidth = 3.5f;
    public float NetCornerRadius;   // w6 (ADR-0028); 0 - фиксированный отступ (сейвы до w6)
    public int NetConnectorSamples = 8;
    public int RoadProfileMode;     // w8 (ADR-0030): 0 - legacy, 1 - режим профилей (блок RPRF обязателен)
    public int ProgMilestones;      // w11 (progression.md §4.4): счётчики PROG до выделения состояния
    public int ProgProfileIdx;
    public int ProgAreas;
    public int LandUseCount;        // w10 (visual-style.md §8): число полигонов землепользования (блок LUSE обязателен при > 0)

    // каталог профилей (RPRF, PreWorld): столбцы по группам, собираются в RoadProfilesDesc в After блока
    public int RprfModuleMm;
    public byte[] RprfKind = Array.Empty<byte>();
    public int[] RprfWidthMm = Array.Empty<int>();
    public byte[] RprfElemCount = Array.Empty<byte>();
    public ushort[] RprfIdLength = Array.Empty<ushort>();
    public byte[] RprfElemKind = Array.Empty<byte>();
    public byte[] RprfElemDir = Array.Empty<byte>();
    public int[] RprfElemWidthMm = Array.Empty<int>();
    public byte[] RprfIdBlob = Array.Empty<byte>();

    /// <summary>Длины групп свободной длины (-1 - ещё не задана); см. <see cref="ColumnDef.Group"/>.</summary>
    public readonly long[] Group = new long[8];

    /// <summary>Место поломки, найденное проверками стадии (<see cref="SaveValidator"/>): строка и номер проверки (не id столбца).</summary>
    public long FailRow;
    public uint FailBlock;
    public uint FailColumn;

    // ---- промежуточные значения ----
    public int SelectedIndex;
    public int SelectedGeneration;
    public readonly ulong[] RngState = new ulong[6];
    public ulong PedRngState;
    public ulong DemandRngState;

    // город (zoning-demand.md §15): сетка ячеек (PARM), таблица типов зданий (BTYP), накопитель наблюдений скимов (SKIM) до вставки в таблицу
    public float CityGridOriginX;
    public float CityGridOriginZ;
    public int CityGridCellsX;
    public int CityGridCellsZ;
    public Sim.Core.Api.BuildingTypeDesc[] BuildingTypes = Array.Empty<Sim.Core.Api.BuildingTypeDesc>();
    public int[]? ObsKeys;
    public long[]? ObsSums;
    public int[]? ObsCounts;

    public float LodOriginX;
    public float LodOriginZ;
    public int LodCellsX;
    public int LodCellsZ;

    public float OvOriginX;
    public float OvOriginZ;
    public int OvNx1;
    public int OvNz1;
    public int OvEntriesLen;
    public int OvExitsLen;
    public int OvMatrixLen;
    public int OvBuiltNodes;
    public int OvBuiltDirSegs;
    public bool OvDisabled;
    public long OvRebuilds;
    public int OvWeightsVersion;
    public bool OvWaveActive;
    public int OvWaveCell;
    public int OvWaveRow;
    public long OvCustomizeRows;
    public long OvCustomizeWork;
    public long OvFullRows;
    public long OvFullWork;
    public Overlay? Ov;

    public long QPendingDistMm;
    public long QEvents;
    public long QWakes;

    // манифест ассетов (MANI)
    public AssetRef[]? ManiRefs;
    public byte[]? ManiIds;
    public int AssetsMissing;
    public int AssetsChanged;

    public int GraphDirSegCount;
    public int GraphArcCount;
    public int GraphBuiltVersion;
    public int GraphWeightsVersion;
}
