using Sim.Core.Api;

namespace Sim.Core.Save;

// world_version 17 (building-lifecycle.md §5, задача BL1; окно w17 открыто до BL4): жизнь здания.
//   PARM 280-284 (секция city.lifecycle), BLDG 22-25 (Cond, Ceil, BornDay, LifeFlags; без режима - нули), BTYP 13-15 (DecayBpDay, CeilDropBpDay, DemolishCost),
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
    };

    // ---- BLDG 22-25 ----
    private static ColumnDef[] LifecycleBuildingColumns() => new[]
    {
        Col.Array<ushort>(22, "Cond", w => w.City!.Buildings.Cond, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<ushort>(23, "Ceil", w => w.City!.Buildings.Ceil, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(24, "BornDay", w => w.City!.Buildings.BornDay, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<byte>(25, "LifeFlags", w => w.City!.Buildings.LifeFlags, _bldHw, "HighWater", "0", _bldPool),
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
