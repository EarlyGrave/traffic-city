using Sim.Core.Api;

namespace Sim.Core.Save;

// world_version 17 (building-lifecycle.md §5, задачи BL1-BL2; окно w17 открыто до BL4): жизнь здания.
//   PARM 280-299 (секция city.lifecycle; 289-298 - ЖКХ, BL3; 299 - restore_per_unit, BL4), BLDG 22-27 (Cond, Ceil, BornDay, LifeFlags, Signif, ArrivalsDay; без режима - нули), LIFE 1-2 (поля квартала, BL2), LIFE 3-17 (фонд Utility, очередь заявок, бригады, BL3),
//   BTYP 13-19 (DecayBpDay, CeilDropBpDay, DemolishCost, BuildCost, ServiceKind, Vehicles, VehicleType),
//   CNTR 176-184 (BuildingsCrumbled, BuildingsDemolished, BuildingsResettled, RepairsDone, RepairDelayTicksTotal, RepairNoDepot, CrewTimeouts, TariffLeaves, Restorations); LEDG потоки длиной 22; CKPI 38-39 (IncBuildingWorn, SaveSchema.Progression). Миграции нет (режим прототипа): MigrationV17.
internal static partial class SaveSchema
{
    // ---- PARM 280-299 ----
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
        Col.Scalar<int>(289, "LifecycleRepairRequestBp", w => w.Town.Lifecycle.RepairRequestBp, (c, v) => c.Desc.Town.Lifecycle.RepairRequestBp = v),
        Col.Scalar<int>(290, "LifecycleRepairPerUnit", w => w.Town.Lifecycle.RepairPerUnit, (c, v) => c.Desc.Town.Lifecycle.RepairPerUnit = v),
        Col.Scalar<int>(291, "LifecycleRepairMinutes", w => w.Town.Lifecycle.RepairMinutes, (c, v) => c.Desc.Town.Lifecycle.RepairMinutes = v),
        Col.Scalar<int>(292, "LifecycleCrewDayCost", w => w.Town.Lifecycle.CrewDayCost, (c, v) => c.Desc.Town.Lifecycle.CrewDayCost = v),
        Col.Scalar<int>(293, "LifecycleMaxServiceVehicles", w => w.Town.Lifecycle.MaxServiceVehicles, (c, v) => c.Desc.Town.Lifecycle.MaxServiceVehicles = v),
        Col.Scalar<int>(294, "LifecycleMaxDispatchPerTick", w => w.Town.Lifecycle.MaxDispatchPerTick, (c, v) => c.Desc.Town.Lifecycle.MaxDispatchPerTick = v),
        Col.Scalar<int>(295, "LifecycleTariffBurdenPct", w => w.Town.Lifecycle.TariffBurdenPct, (c, v) => c.Desc.Town.Lifecycle.TariffBurdenPct = v),
        Col.Scalar<int>(296, "LifecycleTariffLeaveQ16", w => w.Town.Lifecycle.TariffLeaveQ16, (c, v) => c.Desc.Town.Lifecycle.TariffLeaveQ16 = v),
        Col.Scalar<int>(297, "LifecycleMaxUtilityTariff", w => w.Town.Lifecycle.MaxUtilityTariff, (c, v) => c.Desc.Town.Lifecycle.MaxUtilityTariff = v),
        Col.Scalar<int>(298, "LifecycleUtilityTariff", w => w.Town.Lifecycle.UtilityTariff, (c, v) => c.Desc.Town.Lifecycle.UtilityTariff = v),
        Col.Scalar<int>(299, "LifecycleRestorePerUnit", w => w.Town.Lifecycle.RestorePerUnit, (c, v) => c.Desc.Town.Lifecycle.RestorePerUnit = v),
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
    private static BlockDef LifeBlock() => new("LIFE", "жизнь зданий: поля квартала Heritage/Blight/Disorder/FireRisk на сетке lifecycle.cell_m, текущий и накопительный буферы (building-lifecycle.md §2.6); ЖКХ: фонд Utility, очередь заявок, бригады (§2.10)", new[]
    {
        Col.Array<int>(1, "FieldsCur", w => w.City!.Fields!.Cur, static w => w.City!.Fields!.Cur.Length, "4 * NC"),
        Col.Array<int>(2, "FieldsNext", w => w.City!.Fields!.Next, static w => w.City!.Fields!.Next.Length, "4 * NC"),

        // ЖКХ (BL3, §2.10): фонд, очередь заявок (FIFO, голова при загрузке = 0), бригады; кольцо CrewArrivals пусто на границе тиков и не хранится
        Col.Scalar<long>(3, "Utility", w => w.City!.Ledger.Utility, (c, v) => c.W.City!.Ledger.Utility = v),
        RingCount<City.RepairRequest>(4, "QueueCount", w => w.City!.Utility!.Queue),
        Col.RingField<City.RepairRequest>(5, "QueueBuilding", w => w.City!.Utility!.Queue, "QueueCount", nameof(City.RepairRequest.Building)),
        Col.RingField<City.RepairRequest>(6, "QueueGen", w => w.City!.Utility!.Queue, "QueueCount", nameof(City.RepairRequest.Gen)),
        Col.RingField<City.RepairRequest>(7, "QueueTick", w => w.City!.Utility!.Queue, "QueueCount", nameof(City.RepairRequest.Tick)),
        Col.Array<int>(8, "CrewDepot", w => w.City!.Utility!.Depot, static w => w.City!.Utility!.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(9, "CrewDepotGen", w => w.City!.Utility!.DepotGen, static w => w.City!.Utility!.Capacity, "max_service_vehicles"),
        Col.Array<byte>(10, "CrewState", w => w.City!.Utility!.State, static w => w.City!.Utility!.Capacity, "max_service_vehicles"),
        Col.Array<int>(11, "CrewTarget", w => w.City!.Utility!.Target, static w => w.City!.Utility!.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(12, "CrewTargetGen", w => w.City!.Utility!.TargetGen, static w => w.City!.Utility!.Capacity, "max_service_vehicles"),
        Col.Array<int>(13, "CrewSiteLot", w => w.City!.Utility!.SiteLot, static w => w.City!.Utility!.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(14, "CrewVehicle", w => w.City!.Utility!.Vehicle, static w => w.City!.Utility!.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(15, "CrewVehicleGen", w => w.City!.Utility!.VehicleGen, static w => w.City!.Utility!.Capacity, "max_service_vehicles"),
        Col.Array<long>(16, "CrewDueTick", w => w.City!.Utility!.DueTick, static w => w.City!.Utility!.Capacity, "max_service_vehicles"),
        Col.Array<long>(17, "CrewReqTick", w => w.City!.Utility!.ReqTick, static w => w.City!.Utility!.Capacity, "max_service_vehicles"),
    })
    {
        Present = static w => w.City?.Fields != null,
        PresentText = "PARM.LifecycleEnabled = 1",
    };

    // ---- BTYP 13-19 ----
    private static ColumnDef[] LifecycleBuildingTypeColumns() => new[]
    {
        PreField<BuildingTypeDesc, ushort>(13, "DecayBpDay", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.DecayBpDay, static (ref BuildingTypeDesc t, ushort v) => t.DecayBpDay = v),
        PreField<BuildingTypeDesc, ushort>(14, "CeilDropBpDay", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.CeilDropBpDay, static (ref BuildingTypeDesc t, ushort v) => t.CeilDropBpDay = v),
        PreField<BuildingTypeDesc, int>(15, "DemolishCost", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.DemolishCost, static (ref BuildingTypeDesc t, int v) => t.DemolishCost = v),
        PreField<BuildingTypeDesc, int>(16, "BuildCost", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.BuildCost, static (ref BuildingTypeDesc t, int v) => t.BuildCost = v),
        PreField<BuildingTypeDesc, byte>(17, "ServiceKind", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => (byte)t.ServiceKind, static (ref BuildingTypeDesc t, byte v) => t.ServiceKind = (ServiceKind)v),
        PreField<BuildingTypeDesc, byte>(18, "Vehicles", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.Vehicles, static (ref BuildingTypeDesc t, byte v) => t.Vehicles = v),
        PreField<BuildingTypeDesc, byte>(19, "VehicleType", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.VehicleType, static (ref BuildingTypeDesc t, byte v) => t.VehicleType = v),
    };

    // ---- CNTR 176-184 ----
    private static ColumnDef[] LifecycleCounters() => new[]
    {
        Col.Scalar<long>(176, "BuildingsCrumbled", w => w.Counters.BuildingsCrumbled, (c, v) => c.W.Counters.BuildingsCrumbled = v),
        Col.Scalar<long>(177, "BuildingsDemolished", w => w.Counters.BuildingsDemolished, (c, v) => c.W.Counters.BuildingsDemolished = v),
        Col.Scalar<long>(178, "BuildingsResettled", w => w.Counters.BuildingsResettled, (c, v) => c.W.Counters.BuildingsResettled = v),
        Col.Scalar<long>(179, "RepairsDone", w => w.Counters.RepairsDone, (c, v) => c.W.Counters.RepairsDone = v),
        Col.Scalar<long>(180, "RepairDelayTicksTotal", w => w.Counters.RepairDelayTicksTotal, (c, v) => c.W.Counters.RepairDelayTicksTotal = v),
        Col.Scalar<long>(181, "RepairNoDepot", w => w.Counters.RepairNoDepot, (c, v) => c.W.Counters.RepairNoDepot = v),
        Col.Scalar<long>(182, "CrewTimeouts", w => w.Counters.CrewTimeouts, (c, v) => c.W.Counters.CrewTimeouts = v),
        Col.Scalar<long>(183, "TariffLeaves", w => w.Counters.TariffLeaves, (c, v) => c.W.Counters.TariffLeaves = v),
        Col.Scalar<long>(184, "Restorations", w => w.Counters.Restorations, (c, v) => c.W.Counters.Restorations = v),
    };
}
