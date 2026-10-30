using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Util;

namespace Sim.Core.Save;

// world_version 18 (services.md §5, задача SR1; окно w18 открыто до SR5): службы.
//   PARM 300-313 (секция city.services), блок SVCR (PreWorld, есть <=> PARM.ServicesEnabled = 1) - таблицы записей служб (копия, как BTYP),
//   блок SERV (есть <=> city.lifecycle) - станции, кольца вызовов по записям, бригады (переехали из LIFE 4-17). Миграции нет (режим прототипа): MigrationV18.
//   Отклонение от спеки: SERV есть и без режима служб - бригады ЖКХ BL живут в нём (без режима одна встроенная запись utility).
//   SR2 (окно w18, без смены версии): SERV 29-38 (вид вызова бригады, ползунки, расходы, счётчики вызовов записей, переменная часть станций, RespEwma), CNTR 185-188, LEDG потоки длиной 23.
//   SR4 (окно w18, без смены версии): SERV 42-47 (пул пожаров FireStore), PARM 314-315 (fire_cap, fire_spread_ppm), BTYP 20 (FirePpm), CNTR 195-199; бит здания Burning (LifeFlags 64).
//   SR3 (окно w18, без смены версии): SERV 39-41 (пациент бригады Ref/RefGen, CareEwma), RESD 50 (Ill), CNTR 189-194; флаг жителя CareRefused (64), тур Care (3).
internal static partial class SaveSchema
{
    // ---- PARM 300-315 ----
    private static ColumnDef[] ServicesParams() => new[]
    {
        Col.Flag(300, "ServicesEnabled", w => w.Town.Services.Enabled, (c, v) => c.Desc.Town.Services.Enabled = v),
        Col.Scalar<int>(301, "ServicesMaxServiceSites", w => w.Town.Services.MaxServiceSites, (c, v) => c.Desc.Town.Services.MaxServiceSites = v),
        Col.Scalar<int>(302, "ServicesMaxCalls", w => w.Town.Services.MaxCalls, (c, v) => c.Desc.Town.Services.MaxCalls = v),
        Col.Scalar<int>(303, "ServicesMaxFires", w => w.Town.Services.MaxFires, (c, v) => c.Desc.Town.Services.MaxFires = v),
        Col.Scalar<int>(304, "ServicesDisorderIllPct", w => w.Town.Services.DisorderIllPct, (c, v) => c.Desc.Town.Services.DisorderIllPct = v),
        Col.Scalar<int>(305, "ServicesFireRiskPct", w => w.Town.Services.FireRiskPct, (c, v) => c.Desc.Town.Services.FireRiskPct = v),
        Col.Scalar<int>(306, "ServicesSmolderS", w => w.Town.Services.SmolderS, (c, v) => c.Desc.Town.Services.SmolderS = v),
        Col.Scalar<int>(307, "ServicesAnnexBuyPct", w => w.Town.Services.AnnexBuyPct, (c, v) => c.Desc.Town.Services.AnnexBuyPct = v),
        Col.Scalar<int>(308, "ServicesIllLightPpm", w => w.Town.Services.IllLightPpm, (c, v) => c.Desc.Town.Services.IllLightPpm = v),
        Col.Scalar<int>(309, "ServicesIllInpatientPpm", w => w.Town.Services.IllInpatientPpm, (c, v) => c.Desc.Town.Services.IllInpatientPpm = v),
        Col.Scalar<int>(310, "ServicesIllCriticalPpm", w => w.Town.Services.IllCriticalPpm, (c, v) => c.Desc.Town.Services.IllCriticalPpm = v),
        Col.Scalar<int>(311, "ServicesIllDaysLight", w => w.Town.Services.IllDaysLight, (c, v) => c.Desc.Town.Services.IllDaysLight = v),
        Col.Scalar<int>(312, "ServicesIllDaysInpatient", w => w.Town.Services.IllDaysInpatient, (c, v) => c.Desc.Town.Services.IllDaysInpatient = v),
        Col.Scalar<int>(313, "ServicesIllDaysCritical", w => w.Town.Services.IllDaysCritical, (c, v) => c.Desc.Town.Services.IllDaysCritical = v),
        Col.Scalar<int>(314, "ServicesFireCap", w => w.Town.Services.FireCap, (c, v) => c.Desc.Town.Services.FireCap = v),
        Col.Scalar<int>(315, "ServicesFireSpreadPpm", w => w.Town.Services.FireSpreadPpm, (c, v) => c.Desc.Town.Services.FireSpreadPpm = v),
    };

    // ---- SVCR: записи служб (PreWorld, плоские таблицы services.md §2.2; группы - записи, уровни, апгрейды, вызовы, поводы визита) ----
    private static BlockDef ServiceRecordsBlock() => new("SVCR", "записи служб: таблицы записей, уровней, апгрейдов, вызовов и поводов визита (services.md §2.2); строки - как в сценарии", new[]
    {
        PreField<ServiceRecordDesc, byte>(1, "RecFund", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => (byte)t.Fund, static (ref ServiceRecordDesc t, byte v) => t.Fund = (ServiceFund)v),
        PreField<ServiceRecordDesc, ushort>(2, "RecRev", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => t.Rev, static (ref ServiceRecordDesc t, ushort v) => t.Rev = v),
        PreField<ServiceRecordDesc, int>(3, "RecFirstLevel", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => t.FirstLevel, static (ref ServiceRecordDesc t, int v) => t.FirstLevel = v),
        PreField<ServiceRecordDesc, int>(4, "RecLevelCount", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => t.LevelCount, static (ref ServiceRecordDesc t, int v) => t.LevelCount = v),
        PreField<ServiceRecordDesc, int>(5, "RecFirstCall", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => t.FirstCall, static (ref ServiceRecordDesc t, int v) => t.FirstCall = v),
        PreField<ServiceRecordDesc, int>(6, "RecCallCount", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => t.CallCount, static (ref ServiceRecordDesc t, int v) => t.CallCount = v),
        PreField<ServiceRecordDesc, int>(7, "RecFirstVisit", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => t.FirstVisit, static (ref ServiceRecordDesc t, int v) => t.FirstVisit = v),
        PreField<ServiceRecordDesc, int>(8, "RecVisitCount", 1, "Records.Length", w => w.Town.Services.Records, c => c.Desc.Town.Services.Records ?? Array.Empty<ServiceRecordDesc>(), (c, a) => c.Desc.Town.Services.Records = a, static (in ServiceRecordDesc t) => t.VisitCount, static (ref ServiceRecordDesc t, int v) => t.VisitCount = v),
        PreField<ServiceLevelDesc, int>(9, "LevelType", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.Type, static (ref ServiceLevelDesc t, int v) => t.Type = v),
        PreField<ServiceLevelDesc, byte>(10, "LevelRoles", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => (byte)t.Roles, static (ref ServiceLevelDesc t, byte v) => t.Roles = (ServiceRole)v),
        PreField<ServiceLevelDesc, byte>(11, "LevelLotMinF", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.LotMinF, static (ref ServiceLevelDesc t, byte v) => t.LotMinF = v),
        PreField<ServiceLevelDesc, byte>(12, "LevelLotMinD", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.LotMinD, static (ref ServiceLevelDesc t, byte v) => t.LotMinD = v),
        PreField<ServiceLevelDesc, byte>(13, "LevelMilestone", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.Milestone, static (ref ServiceLevelDesc t, byte v) => t.Milestone = v),
        PreField<ServiceLevelDesc, int>(14, "LevelBuildCost", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.BuildCost, static (ref ServiceLevelDesc t, int v) => t.BuildCost = v),
        PreField<ServiceLevelDesc, int>(15, "LevelUpkeepDay", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.UpkeepDay, static (ref ServiceLevelDesc t, int v) => t.UpkeepDay = v),
        PreField<ServiceLevelDesc, int>(16, "LevelCapacityDay", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.CapacityDay, static (ref ServiceLevelDesc t, int v) => t.CapacityDay = v),
        PreField<ServiceLevelDesc, int>(17, "LevelBeds", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.Beds, static (ref ServiceLevelDesc t, int v) => t.Beds = v),
        PreField<ServiceLevelDesc, int>(18, "LevelIcu", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.Icu, static (ref ServiceLevelDesc t, int v) => t.Icu = v),
        PreField<ServiceLevelDesc, short>(19, "LevelOpenSlot", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.OpenSlot, static (ref ServiceLevelDesc t, short v) => t.OpenSlot = v),
        PreField<ServiceLevelDesc, short>(20, "LevelCloseSlot", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.CloseSlot, static (ref ServiceLevelDesc t, short v) => t.CloseSlot = v),
        PreField<ServiceLevelDesc, short>(21, "LevelStaySlots", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.StaySlots, static (ref ServiceLevelDesc t, short v) => t.StaySlots = v),
        PreField<ServiceLevelDesc, short>(22, "LevelDispatchVehicle", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.DispatchVehicle, static (ref ServiceLevelDesc t, short v) => t.DispatchVehicle = v),
        PreField<ServiceLevelDesc, int>(23, "LevelUnits", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.Units, static (ref ServiceLevelDesc t, int v) => t.Units = v),
        PreField<ServiceLevelDesc, int>(24, "LevelPerUnitDay", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.PerUnitDay, static (ref ServiceLevelDesc t, int v) => t.PerUnitDay = v),
        PreField<ServiceLevelDesc, int>(25, "LevelFirstUpgrade", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.FirstUpgrade, static (ref ServiceLevelDesc t, int v) => t.FirstUpgrade = v),
        PreField<ServiceLevelDesc, int>(26, "LevelUpgradeCount", 2, "Levels.Length", w => w.Town.Services.Levels, c => c.Desc.Town.Services.Levels ?? Array.Empty<ServiceLevelDesc>(), (c, a) => c.Desc.Town.Services.Levels = a, static (in ServiceLevelDesc t) => t.UpgradeCount, static (ref ServiceLevelDesc t, int v) => t.UpgradeCount = v),
        PreField<ServiceUpgradeDesc, int>(27, "UpgradeCost", 3, "Upgrades.Length", w => w.Town.Services.Upgrades, c => c.Desc.Town.Services.Upgrades ?? Array.Empty<ServiceUpgradeDesc>(), (c, a) => c.Desc.Town.Services.Upgrades = a, static (in ServiceUpgradeDesc t) => t.Cost, static (ref ServiceUpgradeDesc t, int v) => t.Cost = v),
        PreField<ServiceUpgradeDesc, int>(28, "UpgradeUpkeepDay", 3, "Upgrades.Length", w => w.Town.Services.Upgrades, c => c.Desc.Town.Services.Upgrades ?? Array.Empty<ServiceUpgradeDesc>(), (c, a) => c.Desc.Town.Services.Upgrades = a, static (in ServiceUpgradeDesc t) => t.UpkeepDay, static (ref ServiceUpgradeDesc t, int v) => t.UpkeepDay = v),
        PreField<ServiceUpgradeDesc, byte>(29, "UpgradeMilestone", 3, "Upgrades.Length", w => w.Town.Services.Upgrades, c => c.Desc.Town.Services.Upgrades ?? Array.Empty<ServiceUpgradeDesc>(), (c, a) => c.Desc.Town.Services.Upgrades = a, static (in ServiceUpgradeDesc t) => t.Milestone, static (ref ServiceUpgradeDesc t, byte v) => t.Milestone = v),
        PreField<ServiceUpgradeDesc, byte>(30, "UpgradeEffect", 3, "Upgrades.Length", w => w.Town.Services.Upgrades, c => c.Desc.Town.Services.Upgrades ?? Array.Empty<ServiceUpgradeDesc>(), (c, a) => c.Desc.Town.Services.Upgrades = a, static (in ServiceUpgradeDesc t) => (byte)t.Effect, static (ref ServiceUpgradeDesc t, byte v) => t.Effect = (ServiceUpgradeEffect)v),
        PreField<ServiceUpgradeDesc, int>(31, "UpgradeValue", 3, "Upgrades.Length", w => w.Town.Services.Upgrades, c => c.Desc.Town.Services.Upgrades ?? Array.Empty<ServiceUpgradeDesc>(), (c, a) => c.Desc.Town.Services.Upgrades = a, static (in ServiceUpgradeDesc t) => t.Value, static (ref ServiceUpgradeDesc t, int v) => t.Value = v),
        PreField<ServiceCallDesc, byte>(32, "CallKind", 4, "Calls.Length", w => w.Town.Services.Calls, c => c.Desc.Town.Services.Calls ?? Array.Empty<ServiceCallDesc>(), (c, a) => c.Desc.Town.Services.Calls = a, static (in ServiceCallDesc t) => (byte)t.Kind, static (ref ServiceCallDesc t, byte v) => t.Kind = (ServiceCallKind)v),
        PreField<ServiceCallDesc, byte>(33, "CallFlags", 4, "Calls.Length", w => w.Town.Services.Calls, c => c.Desc.Town.Services.Calls ?? Array.Empty<ServiceCallDesc>(), (c, a) => c.Desc.Town.Services.Calls = a, static (in ServiceCallDesc t) => t.Flags, static (ref ServiceCallDesc t, byte v) => t.Flags = v),
        PreField<ServiceCallDesc, int>(34, "CallOnsiteS", 4, "Calls.Length", w => w.Town.Services.Calls, c => c.Desc.Town.Services.Calls ?? Array.Empty<ServiceCallDesc>(), (c, a) => c.Desc.Town.Services.Calls = a, static (in ServiceCallDesc t) => t.OnsiteS, static (ref ServiceCallDesc t, int v) => t.OnsiteS = v),
        PreField<ServiceCallDesc, int>(35, "CallDeadlineS", 4, "Calls.Length", w => w.Town.Services.Calls, c => c.Desc.Town.Services.Calls ?? Array.Empty<ServiceCallDesc>(), (c, a) => c.Desc.Town.Services.Calls = a, static (in ServiceCallDesc t) => t.DeadlineS, static (ref ServiceCallDesc t, int v) => t.DeadlineS = v),
        PreField<ServiceCallDesc, int>(36, "CallPerCall", 4, "Calls.Length", w => w.Town.Services.Calls, c => c.Desc.Town.Services.Calls ?? Array.Empty<ServiceCallDesc>(), (c, a) => c.Desc.Town.Services.Calls = a, static (in ServiceCallDesc t) => t.PerCall, static (ref ServiceCallDesc t, int v) => t.PerCall = v),
        PreField<ServiceVisitDesc, byte>(37, "VisitReason", 5, "Visits.Length", w => w.Town.Services.Visits, c => c.Desc.Town.Services.Visits ?? Array.Empty<ServiceVisitDesc>(), (c, a) => c.Desc.Town.Services.Visits = a, static (in ServiceVisitDesc t) => (byte)t.Reason, static (ref ServiceVisitDesc t, byte v) => t.Reason = (ServiceVisitReason)v),
        PreField<ServiceVisitDesc, byte>(38, "VisitMinRole", 5, "Visits.Length", w => w.Town.Services.Visits, c => c.Desc.Town.Services.Visits ?? Array.Empty<ServiceVisitDesc>(), (c, a) => c.Desc.Town.Services.Visits = a, static (in ServiceVisitDesc t) => (byte)t.MinRole, static (ref ServiceVisitDesc t, byte v) => t.MinRole = (ServiceRole)v),
        PreField<ServiceVisitDesc, int>(39, "VisitPerVisit", 5, "Visits.Length", w => w.Town.Services.Visits, c => c.Desc.Town.Services.Visits ?? Array.Empty<ServiceVisitDesc>(), (c, a) => c.Desc.Town.Services.Visits = a, static (in ServiceVisitDesc t) => t.PerVisit, static (ref ServiceVisitDesc t, int v) => t.PerVisit = v),
        PreField<ServiceVisitDesc, int>(40, "VisitMaxCostS", 5, "Visits.Length", w => w.Town.Services.Visits, c => c.Desc.Town.Services.Visits ?? Array.Empty<ServiceVisitDesc>(), (c, a) => c.Desc.Town.Services.Visits = a, static (in ServiceVisitDesc t) => t.MaxCostS, static (ref ServiceVisitDesc t, int v) => t.MaxCostS = v),
    })
    {
        PreWorld = true,
        PresentText = "PARM.ServicesEnabled = 1",
        Present = static w => w.Town.Services.Enabled,
    };

    // ---- SERV: станции, кольца вызовов, бригады (services.md §2.3, §5); есть <=> city.lifecycle ----
    private static BlockDef ServicesBlock() => new("SERV", "службы: станции (здание, запись, уровень, апгрейды, машины, койки), кольца вызовов по записям (обычные, срочные; FIFO, голова при загрузке = 0), бригады (services.md §2.3)", new[]
    {
        Col.Array<int>(1, "SiteBuilding", w => Sv(w).Sites.Building, static w => Sv(w).Sites.Capacity, "max_service_sites", "-1"),
        Col.Array<int>(2, "SiteGen", w => Sv(w).Sites.Gen, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<byte>(3, "SiteSvc", w => Sv(w).Sites.Svc, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<byte>(4, "SiteLevel", w => Sv(w).Sites.Level, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<byte>(5, "SiteUpgradeMask", w => Sv(w).Sites.UpgradeMask, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<byte>(6, "SiteUnitsMax", w => Sv(w).Sites.UnitsMax, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<byte>(7, "SiteDuty", w => Sv(w).Sites.Duty, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<int>(8, "SiteBedsUsed", w => Sv(w).Sites.BedsUsed, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<int>(9, "SiteIcuUsed", w => Sv(w).Sites.IcuUsed, static w => Sv(w).Sites.Capacity, "max_service_sites"),
        Col.Array<int>(10, "SiteBedRelease", w => Sv(w).Sites.BedRelease, static w => Sv(w).Sites.BedRelease.Length, "16 * max_service_sites"),
        Col.Array<int>(11, "SiteIcuRelease", w => Sv(w).Sites.IcuRelease, static w => Sv(w).Sites.IcuRelease.Length, "16 * max_service_sites"),
        CallCounts(12),
        CallField<int>(13, "CallTarget", static (in ServiceCall q) => q.Target, static (ref ServiceCall q, int v) => q.Target = v),
        CallField<int>(14, "CallTargetGen", static (in ServiceCall q) => q.TargetGen, static (ref ServiceCall q, int v) => q.TargetGen = v),
        CallField<int>(15, "CallRef", static (in ServiceCall q) => q.Ref, static (ref ServiceCall q, int v) => q.Ref = v),
        CallField<int>(16, "CallRefGen", static (in ServiceCall q) => q.RefGen, static (ref ServiceCall q, int v) => q.RefGen = v),
        CallField<long>(17, "CallTick", static (in ServiceCall q) => q.Tick, static (ref ServiceCall q, long v) => q.Tick = v),
        CallField<byte>(18, "CallKind", static (in ServiceCall q) => q.Kind, static (ref ServiceCall q, byte v) => q.Kind = v),
        CallField<byte>(19, "CallSvc", static (in ServiceCall q) => q.Svc, static (ref ServiceCall q, byte v) => q.Svc = v),
        Col.Array<int>(20, "CrewSite", w => Sv(w).Crews.Site, static w => Sv(w).Crews.Capacity, "max_service_vehicles", "-1"),
        Col.Array<byte>(21, "CrewState", w => Sv(w).Crews.State, static w => Sv(w).Crews.Capacity, "max_service_vehicles"),
        Col.Array<int>(22, "CrewTarget", w => Sv(w).Crews.Target, static w => Sv(w).Crews.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(23, "CrewTargetGen", w => Sv(w).Crews.TargetGen, static w => Sv(w).Crews.Capacity, "max_service_vehicles"),
        Col.Array<int>(24, "CrewSiteLot", w => Sv(w).Crews.SiteLot, static w => Sv(w).Crews.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(25, "CrewVehicle", w => Sv(w).Crews.Vehicle, static w => Sv(w).Crews.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(26, "CrewVehicleGen", w => Sv(w).Crews.VehicleGen, static w => Sv(w).Crews.Capacity, "max_service_vehicles"),
        Col.Array<long>(27, "CrewDueTick", w => Sv(w).Crews.DueTick, static w => Sv(w).Crews.Capacity, "max_service_vehicles"),
        Col.Array<long>(28, "CrewReqTick", w => Sv(w).Crews.ReqTick, static w => Sv(w).Crews.Capacity, "max_service_vehicles"),
        Col.Array<byte>(29, "CrewKind", w => Sv(w).Crews.Kind, static w => Sv(w).Crews.Capacity, "max_service_vehicles"),
        Col.Array<byte>(30, "SvcPct", w => Sv(w).Pct, static w => Sv(w).Pct.Length, "records", "100"),
        Col.Array<byte>(31, "SvcPctPending", w => Sv(w).PctPending, static w => Sv(w).PctPending.Length, "records", "100"),
        Col.Array<long>(32, "SvcCostDay", w => Sv(w).SvcCostDay, static w => Sv(w).SvcCostDay.Length, "2 * records"),
        Col.Array<long>(33, "SvcCallsDone", w => Sv(w).RecCallsDone, static w => Sv(w).RecCallsDone.Length, "records"),
        Col.Array<long>(34, "SvcRespTicks", w => Sv(w).RecRespTicks, static w => Sv(w).RecRespTicks.Length, "records"),
        Col.Array<long>(35, "SvcCallsLate", w => Sv(w).RecCallsLate, static w => Sv(w).RecCallsLate.Length, "records"),
        Col.Array<long>(36, "SvcCallsQueued", w => Sv(w).RecCallsQueued, static w => Sv(w).RecCallsQueued.Length, "records"),
        Col.Array<long>(37, "SiteVarDay", w => Sv(w).SiteVarDay, static w => Sv(w).SiteVarDay.Length, "2 * max_service_sites"),
        Col.Array<ushort>(38, "SvcRespEwma", w => Sv(w).RespEwma, static w => Sv(w).RespEwma.Length, "records * cells (0 без city.services)"),
        Col.Array<int>(39, "CrewRef", w => Sv(w).Crews.Ref, static w => Sv(w).Crews.Capacity, "max_service_vehicles", "-1"),
        Col.Array<int>(40, "CrewRefGen", w => Sv(w).Crews.RefGen, static w => Sv(w).Crews.Capacity, "max_service_vehicles"),
        Col.Array<ushort>(41, "SvcCareEwma", w => Sv(w).CareEwma, static w => Sv(w).CareEwma.Length, "cells (0 без city.services)"),
        Col.Array<byte>(42, "FireState", w => Sv(w).Fires.State, static w => Sv(w).Fires.Capacity, "max_fires (0 без city.services)"),
        Col.Array<int>(43, "FireBuilding", w => Sv(w).Fires.Building, static w => Sv(w).Fires.Capacity, "max_fires (0 без city.services)", "-1"),
        Col.Array<int>(44, "FireGen", w => Sv(w).Fires.Gen, static w => Sv(w).Fires.Capacity, "max_fires (0 без city.services)"),
        Col.Array<long>(45, "FireStartTick", w => Sv(w).Fires.StartTick, static w => Sv(w).Fires.Capacity, "max_fires (0 без city.services)"),
        Col.Array<ushort>(46, "FireCondStart", w => Sv(w).Fires.CondStart, static w => Sv(w).Fires.Capacity, "max_fires (0 без city.services)"),
        Col.Array<long>(47, "FireRetry", w => Sv(w).Fires.Retry, static w => Sv(w).Fires.Capacity, "max_fires (0 без city.services)"),
    })
    {
        Present = static w => w.City?.Services != null,
        PresentText = "PARM.LifecycleEnabled = 1",
    };

    // ---- RESD 50 (SR3): болезнь жителя (services.md §2.5; 0 - здоров и всегда 0 без city.services) ----
    private static ColumnDef[] ServicesResidentColumns() => new[]
    {
        Col.Array<byte>(50, "Ill", w => w.City!.Residents.Ill, _resdHw, "HighWater", "0", _resdPool),
    };

    private delegate TF CallGet<out TF>(in ServiceCall q);

    private delegate void CallSet<in TF>(ref ServiceCall q, TF v);

    private static CityServices Sv(World w) => w.City!.Services!;

    /// <summary>Длины колец: по записи два числа (обычное, срочное); при загрузке задают размер колец (голова = 0).</summary>
    private static ColumnDef CallCounts(uint id) => Col.Custom(
        id,
        "CallCount",
        ColumnType.I32,
        4,
        "2 * records",
        "0",
        w => 2L * Sv(w).Table.Count,
        (w, first, n, dst) =>
        {
            CityServices sv = Sv(w);
            for (int k = 0; k < n; k++)
            {
                MemoryMarshal.Write(dst.Slice(k * 4, 4), Ring(sv, (int)first + k).Count);
            }
        },
        (c, n, src) =>
        {
            CityServices sv = Sv(c.W);
            if (n != 2L * sv.Table.Count)
            {
                return LoadStatus.InvalidValue;
            }

            for (int i = 0; i < n; i++)
            {
                int v = MemoryMarshal.Read<int>(src.Slice(i * 4, 4));
                RingBuffer<ServiceCall> r = Ring(sv, i);
                if (v < 0 || v > r.Capacity)
                {
                    return LoadStatus.CapacityExceeded;
                }

                r.RestoreCount(v);
            }

            return LoadStatus.Ok;
        });

    /// <summary>Поле вызова: все кольца подряд (запись 0 обычное, запись 0 срочное, запись 1 ...), внутри кольца - от головы.</summary>
    private static ColumnDef CallField<TF>(uint id, string name, CallGet<TF> get, CallSet<TF> set)
        where TF : unmanaged
    {
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<TF>();
        return Col.Custom(
            id,
            name,
            Col.TypeOf(typeof(TF)),
            size,
            "sum(CallCount)",
            "0",
            w => TotalCalls(Sv(w)),
            (w, first, n, dst) =>
            {
                CityServices sv = Sv(w);
                for (int k = 0; k < n; k++)
                {
                    Locate(sv, first + k, out RingBuffer<ServiceCall> r, out int off);
                    TF v = get(in r.At(off));
                    MemoryMarshal.Write(dst.Slice(k * size, size), in v);
                }
            },
            (c, n, src) =>
            {
                CityServices sv = Sv(c.W);
                if (n != TotalCalls(sv))
                {
                    return LoadStatus.InvalidValue;
                }

                int k = 0;
                for (int i = 0; i < 2 * sv.Table.Count; i++)
                {
                    RingBuffer<ServiceCall> r = Ring(sv, i);
                    for (int j = 0; j < r.Count; j++, k++)
                    {
                        set(ref r.Items[j], MemoryMarshal.Read<TF>(src.Slice(k * size, size)));
                    }
                }

                return LoadStatus.Ok;
            });
    }

    private static RingBuffer<ServiceCall> Ring(CityServices sv, int i) => (i & 1) == 0 ? sv.Normal[i >> 1] : sv.Urgent[i >> 1];

    private static long TotalCalls(CityServices sv)
    {
        long n = 0;
        for (int i = 0; i < 2 * sv.Table.Count; i++)
        {
            n += Ring(sv, i).Count;
        }

        return n;
    }

    private static void Locate(CityServices sv, long flat, out RingBuffer<ServiceCall> ring, out int off)
    {
        for (int i = 0; i < 2 * sv.Table.Count; i++)
        {
            RingBuffer<ServiceCall> r = Ring(sv, i);
            if (flat < r.Count)
            {
                ring = r;
                off = (int)flat;
                return;
            }

            flat -= r.Count;
        }

        throw new ArgumentOutOfRangeException(nameof(flat));
    }
}
