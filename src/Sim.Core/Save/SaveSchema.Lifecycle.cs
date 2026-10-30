using Sim.Core.Api;

namespace Sim.Core.Save;

// world_version 17 (building-lifecycle.md §5, задачи BL1-BL2; окно w17 открыто до BL4): жизнь здания.
//   PARM 280-288 (секция city.lifecycle), BLDG 22-27 (Cond, Ceil, BornDay, LifeFlags, Signif, ArrivalsDay; без режима - нули), LIFE 1-2 (поля квартала, BL2), BTYP 13-15 (DecayBpDay, CeilDropBpDay, DemolishCost),
//   CNTR 176-178 (BuildingsCrumbled, BuildingsDemolished, BuildingsResettled), CKPI 38-39 (IncBuildingWorn, SaveSchema.Progression). Миграции нет (режим прототипа): MigrationV17.
internal static partial class SaveSchema
{
    // ---- PARM 280-284 ----
    private static ColumnDef[] LifecycleParams() => new[]
    {
        Col.Flag(280, "LifecycleEnabled", w => w.Town.Lifecycle.Enabled, (c, v) => c.Desc.Town.Lifecycle.Enabled = v),
        Col.Scalar<int>(281, "LifecycleAbandonedDecayPct", w => w.Town.Lifecycle.AbandonedDecayPct, (c, v) => c.Desc.Town.Lifecycle.AbandonedDecayPct = v),
        Col.Scalar<int>(282, "LifecycleLowlandCm", w => w.Town.Lifecycle.LowlandCm, (c, v) => c.Desc.Town.Lifecycle.LowlandCm = v),
        Col.Scalar<int>(283, "LifecycleWornRelocateQ16", w => w.Town.Lifecycle.WornRelocateQ16, (c, v) => c.Desc.Town.Lifecycle.WornRelocateQ16 = v),
        Col.Scalar<int>(284, "LifecycleRubbleCostPct", w => w.Town.Lifecycle.RubbleCostPct, (c, v) => c.Desc.Town.Lifecycle.RubbleCostPct = v),
        Col.Scalar<int>(285, "LifecycleCellM", w => w.Town.Lifecycle.CellM, (c, v) => c.Desc.Town.Lifecycle.CellM = v),
        Col.Scalar<int>(286, "LifecycleSigShift", w => w.Town.Lifecycle.SigShift, (c, v) => c.Desc.Town.Lifecycle.SigShift = v),
        Col.Scalar<int>(287, "LifecycleHeritageLossRadiusM", w => w.Town.Lifecycle.HeritageLossRadiusM, (c, v) => c.Desc.Town.Lifecycle.HeritageLossRadiusM = v),
        Col.Scalar<int>(288, "LifecycleLvBlightPermille", w => w.Town.Lifecycle.LvBlightPermille, (c, v) => c.Desc.Town.Lifecycle.LvBlightPermille = v),
    };

    // ---- BLDG 22-25 ----
    private static ColumnDef[] LifecycleBuildingColumns() => new[]
    {
        Col.Array<ushort>(22, "Cond", w => w.City!.Buildings.Cond, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<ushort>(23, "Ceil", w => w.City!.Buildings.Ceil, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(24, "BornDay", w => w.City!.Buildings.BornDay, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<byte>(25, "LifeFlags", w => w.City!.Buildings.LifeFlags, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(26, "Signif", w => w.City!.Buildings.Signif, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(27, "ArrivalsDay", w => w.City!.Buildings.ArrivalsDay, _bldPairN, "2 * HighWater"),
    };

    // ---- LIFE (BL2): поля квартала, оба буфера (building-lifecycle.md §2.6); есть <=> режим city.lifecycle ----
    private static BlockDef LifeBlock() => new("LIFE", "жизнь зданий: поля квартала Heritage/Blight/Disorder/FireRisk на сетке lifecycle.cell_m, текущий и накопительный буферы (building-lifecycle.md §2.6)", new[]
    {
        Col.Array<int>(1, "FieldsCur", w => w.City!.Fields!.Cur, static w => w.City!.Fields!.Cur.Length, "4 * NC"),
        Col.Array<int>(2, "FieldsNext", w => w.City!.Fields!.Next, static w => w.City!.Fields!.Next.Length, "4 * NC"),
    })
    {
        Present = static w => w.City?.Fields != null,
        PresentText = "PARM.LifecycleEnabled = 1",
    };

    // ---- BTYP 13-15 ----
    private static ColumnDef[] LifecycleBuildingTypeColumns() => new[]
    {
        PreField<BuildingTypeDesc, ushort>(13, "DecayBpDay", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.DecayBpDay, static (ref BuildingTypeDesc t, ushort v) => t.DecayBpDay = v),
        PreField<BuildingTypeDesc, ushort>(14, "CeilDropBpDay", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.CeilDropBpDay, static (ref BuildingTypeDesc t, ushort v) => t.CeilDropBpDay = v),
        PreField<BuildingTypeDesc, int>(15, "DemolishCost", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.DemolishCost, static (ref BuildingTypeDesc t, int v) => t.DemolishCost = v),
    };

    // ---- CNTR 176-178 ----
    private static ColumnDef[] LifecycleCounters() => new[]
    {
        Col.Scalar<long>(176, "BuildingsCrumbled", w => w.Counters.BuildingsCrumbled, (c, v) => c.W.Counters.BuildingsCrumbled = v),
        Col.Scalar<long>(177, "BuildingsDemolished", w => w.Counters.BuildingsDemolished, (c, v) => c.W.Counters.BuildingsDemolished = v),
        Col.Scalar<long>(178, "BuildingsResettled", w => w.Counters.BuildingsResettled, (c, v) => c.W.Counters.BuildingsResettled = v),
    };
}
