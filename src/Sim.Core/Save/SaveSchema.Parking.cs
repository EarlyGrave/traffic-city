using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Parking;
using Sim.Core.Util;

namespace Sim.Core.Save;

// world_version 10 (parking.md §13, задача K1): парковка как ресурс и землепользование в сейве.
//   PARM 219 (число полигонов землепользования), 220-239 (секция parking и ёмкость); блок LUSE (есть <=> PARM.LandUseCount > 0) - полигоны парков и воды (visual-style.md §8, ADR-0027);
//   блок PARK (есть <=> PARM.ParkingEnabled = 1) - объекты стоянки; RESD 41-49, VEHC 42-44, TRIP 12-13, BTYP 10-12, CNTR 157-172 - столбцы с дефолтами (MigrationV10).
internal static partial class SaveSchema
{
    private static readonly Func<World, HandlePool> _parkPool = static w => w.City!.Parking!.Pool;
    private static readonly Func<World, int> _parkHw = static w => w.City!.Parking!.Pool.HighWater;

    private static Func<World, bool> ParkingPresent => static w => w.City?.Parking != null;

    // ---- PARM, w10 ----
    private static ColumnDef[] ParkingParams() => new[]
    {
        Col.Scalar<int>(219, "LandUseCount", w => w.LandUse.Length, (c, v) => c.LandUseCount = v),
        Col.Flag(220, "ParkingEnabled", w => w.ParkCfg.Enabled, (c, v) => c.Desc.Parking.Enabled = v),
        Col.Flag(221, "ParkingAccessWalkAgents", w => w.ParkCfg.AccessWalkAgents, (c, v) => c.Desc.Parking.AccessWalkAgents = v),
        Col.Scalar<int>(222, "ParkingWalkMaxMm", w => w.ParkCfg.WalkMaxMm, (c, v) => c.Desc.Parking.WalkMaxMm = v),
        Col.Scalar<int>(223, "ParkingHomeWalkMaxMm", w => w.ParkCfg.HomeWalkMaxMm, (c, v) => c.Desc.Parking.HomeWalkMaxMm = v),
        Col.Scalar<int>(224, "ParkingMaxCandidates", w => w.ParkCfg.MaxCandidates, (c, v) => c.Desc.Parking.MaxCandidates = v),
        Col.Scalar<int>(225, "ParkingWalkSpeedMmS", w => w.ParkCfg.WalkSpeedMmS, (c, v) => c.Desc.Parking.WalkSpeedMmS = v),
        Col.Scalar<int>(226, "ParkingFeeDsPerMoney", w => w.ParkCfg.FeeDsPerMoney, (c, v) => c.Desc.Parking.FeeDsPerMoney = v),
        Col.Scalar<int>(227, "ParkingRetryTicks", w => w.ParkCfg.RetryTicks, (c, v) => c.Desc.Parking.RetryTicks = v),
        Col.Scalar<int>(228, "ParkingSearchRetryTicks", w => w.ParkCfg.SearchRetryTicks, (c, v) => c.Desc.Parking.SearchRetryTicks = v),
        Col.Scalar<int>(229, "ParkingSearchMaxTicks", w => w.ParkCfg.SearchMaxTicks, (c, v) => c.Desc.Parking.SearchMaxTicks = v),
        Col.Scalar<int>(230, "ParkingHomeRetryPerDayQ16", w => w.ParkCfg.HomeRetryPerDayQ16, (c, v) => c.Desc.Parking.HomeRetryPerDayQ16 = v),
        Col.Scalar<int>(231, "ParkingCurbSlotMm", w => w.ParkCfg.CurbSlotMm, (c, v) => c.Desc.Parking.CurbSlotMm = v),
        Col.Scalar<int>(232, "ParkingCurbStopClearMm", w => w.ParkCfg.CurbStopClearMm, (c, v) => c.Desc.Parking.CurbStopClearMm = v),
        Col.Scalar<byte>(233, "ParkingCurbStreet", w => (byte)w.ParkCfg.CurbStreet, (c, v) => c.Desc.Parking.CurbStreet = (CurbMode)v),
        Col.Scalar<byte>(234, "ParkingCurbArterial", w => (byte)w.ParkCfg.CurbArterial, (c, v) => c.Desc.Parking.CurbArterial = (CurbMode)v),
        Col.Scalar<int>(235, "ParkingCurbFee", w => w.ParkCfg.CurbFee, (c, v) => c.Desc.Parking.CurbFee = v),
        Col.Flag(236, "ParkingCurbPermits", w => w.ParkCfg.CurbPermits, (c, v) => c.Desc.Parking.CurbPermits = v),
        Col.Scalar<int>(237, "ParkingGarageFee", w => w.ParkCfg.GarageFee, (c, v) => c.Desc.Parking.GarageFee = v),
        Col.Flag(238, "ParkingGaragePermits", w => w.ParkCfg.GaragePermits, (c, v) => c.Desc.Parking.GaragePermits = v),
        Col.Scalar<int>(239, "MaxParking", w => w.Caps.MaxParking, (c, v) => c.Desc.Capacities.MaxParking = v),
    };

    // ---- BTYP, w10: парковка зданий ----
    private static ColumnDef[] ParkingBuildingTypeColumns() => new[]
    {
        PreField<BuildingTypeDesc, int>(10, "ParkingCapacity", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.ParkingCapacity, static (ref BuildingTypeDesc t, int v) => t.ParkingCapacity = v),
        PreField<BuildingTypeDesc, byte>(11, "ParkingAccess", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => (byte)t.ParkingAccess, static (ref BuildingTypeDesc t, byte v) => t.ParkingAccess = (ParkingAccess)v),
        PreField<BuildingTypeDesc, int>(12, "ParkingFee", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.ParkingFee, static (ref BuildingTypeDesc t, int v) => t.ParkingFee = v),
    };

    // ---- RESD 41-49, VEHC 42-44, TRIP 12-13 ----
    private static ColumnDef[] ParkingResidentColumns() => new[]
    {
        Col.Array<int>(41, "HomeFac", w => w.City!.Residents.HomeFac, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(42, "HomeFacGen", w => w.City!.Residents.HomeFacGen, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(43, "CarFac", w => w.City!.Residents.CarFac, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(44, "CarFacGen", w => w.City!.Residents.CarFacGen, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(45, "CarLot", w => w.City!.Residents.CarLot, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(46, "CarWalkMm", w => w.City!.Residents.CarWalkMm, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<byte>(47, "LegStage", w => w.City!.Residents.LegStage, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<long>(48, "LegWaitFrom", w => w.City!.Residents.LegWaitFrom, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(49, "LegParkWait", w => w.City!.Residents.LegParkWait, _resdHw, "HighWater", "0", _resdPool),
    };

    private static ColumnDef[] ParkingVehicleColumns() => new[]
    {
        Col.Array<int>(42, "ParkFac", w => w.Vehicles.ParkFac, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(43, "ParkFacGen", w => w.Vehicles.ParkFacGen, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<long>(44, "ParkSearchTick", w => w.Vehicles.ParkSearchTick, _vehicleHw, "HighWater", "-1", _vehPool, -1L),
    };

    private static ColumnDef[] ParkingTripColumns() => new[]
    {
        Col.RingField<PendingTrip>(12, "ParkRef", w => w.Trips.Ring, "Count", nameof(PendingTrip.ParkRef)),
        Col.RingField<PendingTrip>(13, "ParkGen", w => w.Trips.Ring, "Count", nameof(PendingTrip.ParkGen)),
    };

    // ---- CNTR, w10: счётчики парковки (parking.md §11) ----
    private static ColumnDef[] ParkingCounters() => new[]
    {
        Col.Scalar<long>(157, "ParkSearchTimeSum", w => w.Counters.ParkSearchTimeSum, (c, v) => c.W.Counters.ParkSearchTimeSum = v),
        Col.Scalar<long>(158, "ParkSearchTimeCount", w => w.Counters.ParkSearchTimeCount, (c, v) => c.W.Counters.ParkSearchTimeCount = v),
        Col.Scalar<long>(159, "ParkSearchHist0", w => w.Counters.ParkSearchHist0, (c, v) => c.W.Counters.ParkSearchHist0 = v),
        Col.Scalar<long>(160, "ParkSearchHist1", w => w.Counters.ParkSearchHist1, (c, v) => c.W.Counters.ParkSearchHist1 = v),
        Col.Scalar<long>(161, "ParkSearchHist2", w => w.Counters.ParkSearchHist2, (c, v) => c.W.Counters.ParkSearchHist2 = v),
        Col.Scalar<long>(162, "ParkSearchHist3", w => w.Counters.ParkSearchHist3, (c, v) => c.W.Counters.ParkSearchHist3 = v),
        Col.Scalar<long>(163, "ParkSearchHist4", w => w.Counters.ParkSearchHist4, (c, v) => c.W.Counters.ParkSearchHist4 = v),
        Col.Scalar<long>(164, "ParkSearchHist5", w => w.Counters.ParkSearchHist5, (c, v) => c.W.Counters.ParkSearchHist5 = v),
        Col.Scalar<long>(165, "ParkNoSpotWaits", w => w.Counters.ParkNoSpotWaits, (c, v) => c.W.Counters.ParkNoSpotWaits = v),
        Col.Scalar<long>(166, "ParkModeSwitch", w => w.Counters.ParkModeSwitch, (c, v) => c.W.Counters.ParkModeSwitch = v),
        Col.Scalar<long>(167, "ParkToursDropped", w => w.Counters.ParkToursDropped, (c, v) => c.W.Counters.ParkToursDropped = v),
        Col.Scalar<long>(168, "ParkOverflow", w => w.Counters.ParkOverflow, (c, v) => c.W.Counters.ParkOverflow = v),
        Col.Scalar<long>(169, "ParkTowed", w => w.Counters.ParkTowed, (c, v) => c.W.Counters.ParkTowed = v),
        Col.Scalar<long>(170, "ParkCarShed", w => w.Counters.ParkCarShed, (c, v) => c.W.Counters.ParkCarShed = v),
        Col.Scalar<long>(171, "ParkSearching", w => w.Counters.ParkSearching, (c, v) => c.W.Counters.ParkSearching = v),
        Col.Scalar<long>(172, "ParkWalkTicks", w => w.Counters.ParkWalkTicks, (c, v) => c.W.Counters.ParkWalkTicks = v),
    };

    // ---- PARK: объекты стоянки (есть <=> PARM.ParkingEnabled = 1); производные (положение, окрестности, WalkMm, HomeAway, обратные ссылки) не сохраняются ----
    private static BlockDef ParkingBlock() => new("PARK", "объекты стоянки: пул (поколения, free-list), вид, флаги, участок, здание, ёмкость, счётчики мест, плата (parking.md §2.1, §13)", new[]
    {
        PoolHighWater(1, _parkPool),
        PoolFreeCount(2, _parkPool),
        Col.Array<int>(3, "FreeList", w => w.City!.Parking!.Pool.FreeListArray, w => w.City!.Parking!.Pool.FreeCount, "FreeCount"),
        Col.Array<int>(4, "Generation", w => w.City!.Parking!.Pool.GenerationArray, _parkHw, "HighWater"),
        Col.Array<byte>(5, "Kind", w => w.City!.Parking!.Kind, _parkHw, "HighWater", "0", _parkPool),
        Col.Array<byte>(6, "Flags", w => w.City!.Parking!.Flags, _parkHw, "HighWater", "0", _parkPool),
        Col.Array<int>(7, "Lot", w => w.City!.Parking!.Lot, _parkHw, "HighWater", "-1", _parkPool, -1),
        Col.Array<int>(8, "Building", w => w.City!.Parking!.Building, _parkHw, "HighWater", "-1", _parkPool, -1),
        Col.Array<int>(9, "BuildingGen", w => w.City!.Parking!.BuildingGen, _parkHw, "HighWater", "0", _parkPool),
        Col.Array<int>(10, "Capacity", w => w.City!.Parking!.Capacity, _parkHw, "HighWater", "0", _parkPool),
        Col.Array<int>(11, "Assigned", w => w.City!.Parking!.Assigned, _parkHw, "HighWater", "0", _parkPool),
        Col.Array<int>(12, "Occupied", w => w.City!.Parking!.Occupied, _parkHw, "HighWater", "0", _parkPool),
        Col.Array<int>(13, "Reserved", w => w.City!.Parking!.Reserved, _parkHw, "HighWater", "0", _parkPool),
        Col.Array<int>(14, "Fee", w => w.City!.Parking!.Fee, _parkHw, "HighWater", "0", _parkPool),
    })
    {
        After = c =>
        {
            ParkingStore p = c.W.City!.Parking!;
            p.Pool.RecountAlive();
            p.RebuildLookups();
            return LoadStatus.Ok;
        },
        PresentText = "PARM.ParkingEnabled = 1",
        Present = ParkingPresent,
    };

    // ---- LUSE: землепользование генератора (есть <=> PARM.LandUseCount > 0) ----
    private static BlockDef LandUseBlock() => new("LUSE", "землепользование: полигоны парков и воды генератора, 4 вершины, вид, флаги (visual-style.md §8, ADR-0027)", new[]
    {
        Col.Scalar<int>(1, "Count", w => w.LandUse.Length, (c, v) =>
        {
            if (v != c.LandUseCount || v < 1)
            {
                return LoadStatus.InvalidValue;
            }

            c.W.LandUse = new LandUsePoly[v];
            c.W.LandUseVersion = 1;
            return LoadStatus.Ok;
        }),
        LandUseCoord(2, "P0X", nameof(LandUsePoly.P0), 0),
        LandUseCoord(3, "P0Z", nameof(LandUsePoly.P0), 4),
        LandUseCoord(4, "P1X", nameof(LandUsePoly.P1), 0),
        LandUseCoord(5, "P1Z", nameof(LandUsePoly.P1), 4),
        LandUseCoord(6, "P2X", nameof(LandUsePoly.P2), 0),
        LandUseCoord(7, "P2Z", nameof(LandUsePoly.P2), 4),
        LandUseCoord(8, "P3X", nameof(LandUsePoly.P3), 0),
        LandUseCoord(9, "P3Z", nameof(LandUsePoly.P3), 4),
        Col.Field<LandUsePoly>(10, "Kind", w => w.LandUse, _landN, "Count", nameof(LandUsePoly.Kind)),
        Col.Field<LandUsePoly>(11, "Flags", w => w.LandUse, _landN, "Count", nameof(LandUsePoly.Flags)),
    })
    {
        After = c => SaveValidator.ValidateLandUse(c),
        PresentText = "PARM.LandUseCount > 0",
        Present = static w => w.LandUse.Length > 0,
    };

    private static readonly Func<World, int> _landN = static w => w.LandUse.Length;

    private static ColumnDef LandUseCoord(uint id, string name, string field, int sub) =>
        Col.Field<LandUsePoly>(id, name, w => w.LandUse, _landN, "Count", field, sub, ColumnType.F32);
}
