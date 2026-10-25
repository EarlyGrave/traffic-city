using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Save;

// Блоки параметров и управляющего состояния: PARM, DEMD, VTYP, WRLD, CNTR.
internal static partial class SaveSchema
{
    private static BlockDef Params() => new("PARM", "параметры мира (ScenarioDesc без генератора)", new[]
    {
        Col.Scalar<ulong>(1, "Seed", w => w.Seed, (c, v) => c.Desc.Seed = v),

        // SimCapacities
        Col.Scalar<int>(2, "MaxVehicles", w => w.Caps.MaxVehicles, (c, v) => c.Desc.Capacities.MaxVehicles = v),
        Col.Scalar<int>(3, "MaxNodes", w => w.Caps.MaxNodes, (c, v) => c.Desc.Capacities.MaxNodes = v),
        Col.Scalar<int>(4, "MaxSegments", w => w.Caps.MaxSegments, (c, v) => c.Desc.Capacities.MaxSegments = v),
        Col.Scalar<int>(5, "MaxLanes", w => w.Caps.MaxLanes, (c, v) => c.Desc.Capacities.MaxLanes = v),
        Col.Scalar<int>(6, "MaxLanePoints", w => w.Caps.MaxLanePoints, (c, v) => c.Desc.Capacities.MaxLanePoints = v),
        Col.Scalar<int>(7, "MaxZones", w => w.Caps.MaxZones, (c, v) => c.Desc.Capacities.MaxZones = v),
        Col.Scalar<int>(8, "MaxBusRoutes", w => w.Caps.MaxBusRoutes, (c, v) => c.Desc.Capacities.MaxBusRoutes = v),
        Col.Scalar<int>(9, "MaxBusStops", w => w.Caps.MaxBusStops, (c, v) => c.Desc.Capacities.MaxBusStops = v),
        Col.Scalar<int>(10, "MaxBusStopsPerRoute", w => w.Caps.MaxBusStopsPerRoute, (c, v) => c.Desc.Capacities.MaxBusStopsPerRoute = v),
        Col.Scalar<int>(11, "MaxRoutes", w => w.Caps.MaxRoutes, (c, v) => c.Desc.Capacities.MaxRoutes = v),
        Col.Scalar<int>(12, "MaxPlanSteps", w => w.Caps.MaxPlanSteps, (c, v) => c.Desc.Capacities.MaxPlanSteps = v),
        Col.Scalar<int>(13, "MaxPendingTrips", w => w.Caps.MaxPendingTrips, (c, v) => c.Desc.Capacities.MaxPendingTrips = v),
        Col.Scalar<int>(14, "CommandQueueCapacity", w => w.Caps.CommandQueueCapacity, (c, v) => c.Desc.Capacities.CommandQueueCapacity = v),
        Col.Scalar<int>(15, "EventCapacity", w => w.Caps.EventCapacity, (c, v) => c.Desc.Capacities.EventCapacity = v),
        Col.Scalar<int>(16, "MaxSignalizedNodes", w => w.Caps.MaxSignalizedNodes, (c, v) => c.Desc.Capacities.MaxSignalizedNodes = v),
        Col.Scalar<int>(17, "MaxConnectors", w => w.Caps.MaxConnectors, (c, v) => c.Desc.Capacities.MaxConnectors = v),
        Col.Scalar<int>(18, "MaxConflicts", w => w.Caps.MaxConflicts, (c, v) => c.Desc.Capacities.MaxConflicts = v),

        // RulesDesc
        Col.Scalar<float>(19, "RulesMinPhysicalGap", w => w.Rules.MinPhysicalGap, (c, v) => c.Rules.MinPhysicalGap = v),
        Col.Scalar<int>(20, "RulesStuckThresholdTicks", w => w.Rules.StuckThresholdTicks, (c, v) => c.Rules.StuckThresholdTicks = v),
        Col.Scalar<float>(21, "RulesStuckSpeedThreshold", w => w.Rules.StuckSpeedThreshold, (c, v) => c.Rules.StuckSpeedThreshold = v),
        Col.Scalar<float>(22, "RulesGridlockStuckShare", w => w.Rules.GridlockStuckShare, (c, v) => c.Rules.GridlockStuckShare = v),
        Col.Scalar<int>(23, "RulesGridlockMinStuck", w => w.Rules.GridlockMinStuck, (c, v) => c.Rules.GridlockMinStuck = v),
        Col.Scalar<int>(24, "RulesGridlockHoldTicks", w => w.Rules.GridlockHoldTicks, (c, v) => c.Rules.GridlockHoldTicks = v),
        Col.Scalar<int>(25, "RulesMobilEveryTicks", w => w.Rules.MobilEveryTicks, (c, v) => c.Rules.MobilEveryTicks = v),
        Col.Scalar<int>(26, "RulesDefaultYellowTicks", w => w.Rules.DefaultYellowTicks, (c, v) => c.Rules.DefaultYellowTicks = v),
        Col.Scalar<int>(27, "RulesDefaultAllRedTicks", w => w.Rules.DefaultAllRedTicks, (c, v) => c.Rules.DefaultAllRedTicks = v),
        Col.Scalar<float>(28, "RulesYieldGapTime", w => w.Rules.YieldGapTime, (c, v) => c.Rules.YieldGapTime = v),
        Col.Scalar<int>(29, "RulesMaxYieldWaitTicks", w => w.Rules.MaxYieldWaitTicks, (c, v) => c.Rules.MaxYieldWaitTicks = v),

        // RoutingDesc (с применёнными дефолтами)
        Col.Scalar<int>(30, "RoutingLevels", w => w.Routing.Levels, (c, v) => c.Routing.Levels = v),
        Col.Scalar<int>(31, "RoutingFanout", w => w.Routing.Fanout, (c, v) => c.Routing.Fanout = v),
        Col.Scalar<float>(32, "RoutingCellSize", w => w.Routing.CellSize, (c, v) => c.Routing.CellSize = v),
        Col.Scalar<int>(33, "RoutingWindowSteps", w => w.Routing.WindowSteps, (c, v) => c.Routing.WindowSteps = v),
        Col.Scalar<int>(34, "RoutingPathfindBudgetPerTick", w => w.Routing.PathfindBudgetPerTick, (c, v) => c.Routing.PathfindBudgetPerTick = v),
        Col.Scalar<int>(35, "RoutingWorkBudgetPerTick", w => w.Routing.WorkBudgetPerTick, (c, v) => c.Routing.WorkBudgetPerTick = v),
        Col.Scalar<int>(36, "RoutingQueryWorkCap", w => w.Routing.QueryWorkCap, (c, v) => c.Routing.QueryWorkCap = v),
        Col.Scalar<int>(37, "RoutingCustomizeBudgetPerTick", w => w.Routing.CustomizeBudgetPerTick, (c, v) => c.Routing.CustomizeBudgetPerTick = v),
        Col.Scalar<float>(38, "RoutingCustomizeEps", w => w.Routing.CustomizeEps, (c, v) => c.Routing.CustomizeEps = v),
        Col.Scalar<int>(39, "RoutingCostRecalcTicks", w => w.Routing.CostRecalcTicks, (c, v) => c.Routing.CostRecalcTicks = v),
        Col.Scalar<float>(40, "RoutingTimedDrift", w => w.Routing.TimedDrift, (c, v) => c.Routing.TimedDrift = v),
        Col.Scalar<int>(41, "RoutingTimedDriftAbsTicks", w => w.Routing.TimedDriftAbsTicks, (c, v) => c.Routing.TimedDriftAbsTicks = v),

        // LodDesc (с дефолтами; фокус/закрепления - в блоке LODS, обход фокуса - только хост)
        Col.Scalar<int>(42, "LodMode", w => (int)w.LodCfg.Mode, (c, v) => c.Lod.Mode = (LodMode)v),
        Col.Scalar<float>(43, "LodCellM", w => w.LodCfg.CellM, (c, v) => c.Lod.CellM = v),
        Col.Scalar<int>(44, "LodMicroBudget", w => w.LodCfg.MicroBudget, (c, v) => c.Lod.MicroBudget = v),
        Col.Scalar<int>(45, "LodEvalTicks", w => w.LodCfg.EvalTicks, (c, v) => c.Lod.EvalTicks = v),
        Col.Scalar<int>(46, "LodHoldTicks", w => w.LodCfg.HoldTicks, (c, v) => c.Lod.HoldTicks = v),
        Col.Scalar<int>(47, "LodFocusMarginCells", w => w.LodCfg.FocusMarginCells, (c, v) => c.Lod.FocusMarginCells = v),
        Col.Flag(48, "LodFollowSelected", w => w.LodCfg.FollowSelected, (c, v) => c.Lod.FollowSelected = v),
        Col.Flag(49, "LodFocusCommands", w => w.LodCfg.FocusCommands, (c, v) => c.Lod.FocusCommands = v),
        Col.Scalar<float>(50, "LodCongestionShare", w => w.LodCfg.CongestionShare, (c, v) => c.Lod.CongestionShare = v),
        Col.Scalar<int>(51, "LodCongestionMin", w => w.LodCfg.CongestionMin, (c, v) => c.Lod.CongestionMin = v),
        Col.Scalar<float>(52, "LodMesoHeadwayScale", w => w.LodCfg.MesoHeadwayScale, (c, v) => c.Lod.MesoHeadwayScale = v),
        Col.Scalar<float>(53, "LodMesoAccelScale", w => w.LodCfg.MesoAccelScale, (c, v) => c.Lod.MesoAccelScale = v),
        Col.Scalar<int>(54, "LodMesoModel", w => (int)w.LodCfg.MesoModel, (c, v) => c.Lod.MesoModel = (MesoModel)v),
        Col.Scalar<float>(55, "LodQueueHeadwayScale", w => w.LodCfg.QueueHeadwayScale, (c, v) => c.Lod.QueueHeadwayScale = v),

        // DemandDesc (скаляры)
        Col.Scalar<int>(56, "DemandTargetVehicles", w => w.Demand.TargetVehicles, (c, v) => c.Desc.Demand.TargetVehicles = v),
        Col.Scalar<int>(57, "DemandSpawnRampTicks", w => w.Demand.SpawnRampTicks, (c, v) => c.Desc.Demand.SpawnRampTicks = v),
        Col.Scalar<float>(58, "DemandZoneSize", w => w.Demand.ZoneSize, (c, v) => c.Desc.Demand.ZoneSize = v),
        Col.Scalar<int>(59, "DemandModel", w => (int)w.Demand.Model, (c, v) => c.Desc.Demand.Model = (DemandModel)v),
        Col.Scalar<float>(60, "DemandGravityExponent", w => w.Demand.GravityExponent, (c, v) => c.Desc.Demand.GravityExponent = v),
        Col.Scalar<float>(61, "DemandWeightJitter", w => w.Demand.WeightJitter, (c, v) => c.Desc.Demand.WeightJitter = v),
        Col.Scalar<float>(62, "DemandExternalShare", w => w.Demand.ExternalShare, (c, v) => c.Desc.Demand.ExternalShare = v),
        Col.Scalar<float>(63, "DemandMinTripLength", w => w.Demand.MinTripLength, (c, v) => c.Desc.Demand.MinTripLength = v),

        // TransitDesc (скаляры)
        Col.Scalar<int>(64, "TransitRoutes", w => w.TransitCfg.Routes, (c, v) => c.Desc.Transit.Routes = v),
        Col.Scalar<int>(65, "TransitHeadwayTicks", w => w.TransitCfg.HeadwayTicks, (c, v) => c.Desc.Transit.HeadwayTicks = v),
        Col.Scalar<int>(66, "TransitStopsPerRouteMin", w => w.TransitCfg.StopsPerRouteMin, (c, v) => c.Desc.Transit.StopsPerRouteMin = v),
        Col.Scalar<int>(67, "TransitStopsPerRouteMax", w => w.TransitCfg.StopsPerRouteMax, (c, v) => c.Desc.Transit.StopsPerRouteMax = v),
        Col.Scalar<float>(68, "TransitStopSpacing", w => w.TransitCfg.StopSpacing, (c, v) => c.Desc.Transit.StopSpacing = v),
        Col.Scalar<int>(69, "TransitStopKind", w => (int)w.TransitCfg.StopKind, (c, v) => c.Desc.Transit.StopKind = (StopKindMode)v),
        Col.Scalar<int>(70, "TransitBusType", w => w.TransitCfg.BusType, (c, v) => c.Desc.Transit.BusType = v),
        Col.Scalar<int>(71, "TransitDwellBaseTicks", w => w.TransitCfg.DwellBaseTicks, (c, v) => c.Desc.Transit.DwellBaseTicks = v),
        Col.Scalar<float>(72, "TransitDwellPerPassengerTicks", w => w.TransitCfg.DwellPerPassengerTicks, (c, v) => c.Desc.Transit.DwellPerPassengerTicks = v),
        Col.Scalar<float>(73, "TransitPassengerRatePerMin", w => w.TransitCfg.PassengerRatePerMin, (c, v) => c.Desc.Transit.PassengerRatePerMin = v),

        // Геометрия построителя сети (нужна BuildRoad после загрузки): остальные параметры NetworkOptions - константы.
        Col.Scalar<float>(74, "NetLaneWidth", w => w.Builder.Options.LaneWidth, (c, v) => c.NetLaneWidth = v),
        Col.Scalar<int>(75, "NetConnectorSamples", w => w.Builder.Options.ConnectorSamples, (c, v) => c.NetConnectorSamples = v),

        // RulesDesc, добавлено в 2.5 S2 (дефолты Adaptive; значения с применёнными дефолтами WorldBuilder)
        Col.Scalar<int>(76, "RulesSignalMinGreenTicks", w => w.Rules.SignalMinGreenTicks, (c, v) => c.Rules.SignalMinGreenTicks = v, "140"),
        Col.Scalar<int>(77, "RulesSignalMaxGreenTicks", w => w.Rules.SignalMaxGreenTicks, (c, v) => c.Rules.SignalMaxGreenTicks = v, "480"),
        Col.Scalar<int>(78, "RulesSignalGapTicks", w => w.Rules.SignalGapTicks, (c, v) => c.Rules.SignalGapTicks = v, "50"),
        Col.Scalar<int>(79, "RulesSignalArrowTriggerTicks", w => w.Rules.SignalArrowTriggerTicks, (c, v) => c.Rules.SignalArrowTriggerTicks = v, "80"),
        Col.Scalar<int>(80, "RulesSignalArrowMinTicks", w => w.Rules.SignalArrowMinTicks, (c, v) => c.Rules.SignalArrowMinTicks = v, "60"),
        Col.Scalar<int>(81, "RulesSignalArrowMaxTicks", w => w.Rules.SignalArrowMaxTicks, (c, v) => c.Rules.SignalArrowMaxTicks = v, "120"),
        Col.Scalar<int>(82, "RulesSignalArrowGapTicks", w => w.Rules.SignalArrowGapTicks, (c, v) => c.Rules.SignalArrowGapTicks = v, "40"),

        // Удержание автобусов (ADR-0008), добавлено в 2.5
        Col.Scalar<float>(83, "TransitHoldAlpha", w => w.TransitCfg.HoldAlpha, (c, v) => c.Desc.Transit.HoldAlpha = v),
        Col.Scalar<int>(84, "TransitHoldMaxTicks", w => w.TransitCfg.HoldMaxTicks, (c, v) => c.Desc.Transit.HoldMaxTicks = v),
        Col.Scalar<int>(85, "TransitHoldEveryN", w => w.TransitCfg.HoldEveryN, (c, v) => c.Desc.Transit.HoldEveryN = v),
    })
    {
        PreWorld = true,
    };

    private static BlockDef Demand() => new("DEMD", "спрос: смесь типов, зоны и OD-пары", new[]
    {
        Col.Custom(
            1,
            "TypeMix",
            ColumnType.F32,
            4,
            "TypeMix.Length",
            "0",
            w => w.Demand.TypeMix?.Length ?? 0,
            (w, first, n, dst) => MemoryMarshal.AsBytes(new ReadOnlySpan<float>(w.Demand.TypeMix, (int)first, n)).CopyTo(dst),
            (c, n, src) =>
            {
                c.TypeMix = new float[n];
                src.CopyTo(MemoryMarshal.AsBytes(c.TypeMix.AsSpan()));
                return LoadStatus.Ok;
            },
            group: 4),
        PreField<ZoneDesc, float>(2, "ZoneCenterX", 1, "Zones.Length", w => w.Demand.Zones, c => c.Zones, (c, a) => c.Zones = a, static (in ZoneDesc z) => z.CenterX, static (ref ZoneDesc z, float v) => z.CenterX = v),
        PreField<ZoneDesc, float>(3, "ZoneCenterZ", 1, "Zones.Length", w => w.Demand.Zones, c => c.Zones, (c, a) => c.Zones = a, static (in ZoneDesc z) => z.CenterZ, static (ref ZoneDesc z, float v) => z.CenterZ = v),
        PreField<ZoneDesc, float>(4, "ZoneHalfSize", 1, "Zones.Length", w => w.Demand.Zones, c => c.Zones, (c, a) => c.Zones = a, static (in ZoneDesc z) => z.HalfSize, static (ref ZoneDesc z, float v) => z.HalfSize = v),
        PreField<ZoneDesc, float>(5, "ZoneWeight", 1, "Zones.Length", w => w.Demand.Zones, c => c.Zones, (c, a) => c.Zones = a, static (in ZoneDesc z) => z.Weight, static (ref ZoneDesc z, float v) => z.Weight = v),
        PreField<ZoneDesc, byte>(6, "ZoneIsExternal", 1, "Zones.Length", w => w.Demand.Zones, c => c.Zones, (c, a) => c.Zones = a, static (in ZoneDesc z) => z.IsExternal ? (byte)1 : (byte)0, static (ref ZoneDesc z, byte v) => z.IsExternal = v != 0),
        PreField<OdPairDesc, int>(7, "OdFromZone", 2, "OdPairs.Length", w => w.Demand.OdPairs, c => c.OdPairs, (c, a) => c.OdPairs = a, static (in OdPairDesc p) => p.FromZone, static (ref OdPairDesc p, int v) => p.FromZone = v),
        PreField<OdPairDesc, int>(8, "OdToZone", 2, "OdPairs.Length", w => w.Demand.OdPairs, c => c.OdPairs, (c, a) => c.OdPairs = a, static (in OdPairDesc p) => p.ToZone, static (ref OdPairDesc p, int v) => p.ToZone = v),
        PreField<OdPairDesc, float>(9, "OdWeight", 2, "OdPairs.Length", w => w.Demand.OdPairs, c => c.OdPairs, (c, a) => c.OdPairs = a, static (in OdPairDesc p) => p.Weight, static (ref OdPairDesc p, float v) => p.Weight = v),
    })
    {
        PreWorld = true,
    };

    private static BlockDef VehicleTypes() => new("VTYP", "типы ТС: симуляционные параметры (ассеты, save-format.md §5)", new[]
    {
        PreField<VehicleTypeDesc, int>(1, "Category", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => (int)t.Category, static (ref VehicleTypeDesc t, int v) => t.Category = (VehicleCategory)v),
        PreField<VehicleTypeDesc, float>(2, "Length", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.Length, static (ref VehicleTypeDesc t, float v) => t.Length = v),
        PreField<VehicleTypeDesc, float>(3, "Width", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.Width, static (ref VehicleTypeDesc t, float v) => t.Width = v),
        PreField<VehicleTypeDesc, float>(4, "MaxSpeed", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.MaxSpeed, static (ref VehicleTypeDesc t, float v) => t.MaxSpeed = v),
        PreField<VehicleTypeDesc, float>(5, "SpeedFactor", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.SpeedFactor, static (ref VehicleTypeDesc t, float v) => t.SpeedFactor = v),
        PreField<VehicleTypeDesc, float>(6, "Accel", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.Accel, static (ref VehicleTypeDesc t, float v) => t.Accel = v),
        PreField<VehicleTypeDesc, float>(7, "ComfortDecel", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.ComfortDecel, static (ref VehicleTypeDesc t, float v) => t.ComfortDecel = v),
        PreField<VehicleTypeDesc, float>(8, "HeadwayTime", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.HeadwayTime, static (ref VehicleTypeDesc t, float v) => t.HeadwayTime = v),
        PreField<VehicleTypeDesc, float>(9, "MinGap", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.MinGap, static (ref VehicleTypeDesc t, float v) => t.MinGap = v),
        PreField<VehicleTypeDesc, float>(10, "Delta", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.Delta, static (ref VehicleTypeDesc t, float v) => t.Delta = v),
        PreField<VehicleTypeDesc, float>(11, "Politeness", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.Politeness, static (ref VehicleTypeDesc t, float v) => t.Politeness = v),
        PreField<VehicleTypeDesc, float>(12, "LaneChangeThreshold", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.LaneChangeThreshold, static (ref VehicleTypeDesc t, float v) => t.LaneChangeThreshold = v),
        PreField<VehicleTypeDesc, float>(13, "SafeDecel", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.SafeDecel, static (ref VehicleTypeDesc t, float v) => t.SafeDecel = v),
        PreField<VehicleTypeDesc, float>(14, "RightBias", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.RightBias, static (ref VehicleTypeDesc t, float v) => t.RightBias = v),
        PreField<VehicleTypeDesc, int>(15, "LaneChangeDurationTicks", 3, "Types.Length", w => w.Types, c => c.Types, (c, a) => c.Types = a, static (in VehicleTypeDesc t) => t.LaneChangeDurationTicks, static (ref VehicleTypeDesc t, int v) => t.LaneChangeDurationTicks = v),
    })
    {
        PreWorld = true,
    };

    private delegate TF FieldGet<TS, out TF>(in TS s);

    private delegate void FieldSet<TS, in TF>(ref TS s, TF v);

    /// <summary>Поле элемента массива параметров (зоны, OD-пары, типы ТС): число элементов задаёт файл, столбцы одной группы обязаны совпасть по длине.</summary>
    private static ColumnDef PreField<TS, TF>(
        uint id,
        string name,
        int group,
        string length,
        Func<World, TS[]?> saved,
        Func<LoadContext, TS[]> current,
        Action<LoadContext, TS[]> assign,
        FieldGet<TS, TF> get,
        FieldSet<TS, TF> set)
        where TS : struct
        where TF : unmanaged
    {
        ColumnType type = Col.TypeOf(typeof(TF));
        int size = Unsafe.SizeOf<TF>();
        return new ColumnDef(
            id,
            name,
            type,
            size,
            length,
            "0",
            w => saved(w)?.Length ?? 0,
            (w, first, n, dst) =>
            {
                TS[] a = saved(w)!;
                for (int k = 0; k < n; k++)
                {
                    TF v = get(in a[(int)first + k]);
                    MemoryMarshal.Write(dst.Slice(k * size, size), in v);
                }
            },
            (c, n, src) =>
            {
                TS[] a = current(c);
                if (a.Length != n)
                {
                    a = new TS[n];
                    assign(c, a);
                }

                for (int k = 0; k < n; k++)
                {
                    TF v = MemoryMarshal.Read<TF>(src.Slice(k * size, size));
                    set(ref a[k], v);
                }

                return LoadStatus.Ok;
            })
        {
            Group = group,
        };
    }

    private static BlockDef WorldState() => new("WRLD", "время, PRNG, управление", new[]
    {
        Col.Scalar<long>(1, "Tick", w => w.Tick, (c, v) => c.W.Tick = v),
        Col.Scalar<int>(2, "NetVersion", w => w.Net.Version, (c, v) => c.W.Net.Version = v),
        Col.Scalar<int>(3, "TargetVehicles", w => w.TargetVehicles, (c, v) => c.W.TargetVehicles = v),
        Col.Scalar<int>(4, "SelectedIndex", w => w.Selected.Index, (c, v) => c.SelectedIndex = v),
        Col.Scalar<int>(5, "SelectedGeneration", w => w.Selected.Generation, (c, v) => c.W.Selected = new VehicleHandle(c.SelectedIndex, v)),
        Col.Scalar<int>(6, "GridlockStreak", w => w.GridlockStreak, (c, v) => c.W.GridlockStreak = v),
        Col.Flag(7, "RecalcPending", w => w.RecalcPending, (c, v) => c.W.RecalcPending = v),
        Col.Scalar<long>(8, "SegmentLoadsTick", w => w.SegmentLoadsTick, (c, v) => c.W.SegmentLoadsTick = v, "-1"),
        Col.Custom(
            9,
            "RngState",
            ColumnType.U64,
            8,
            "6",
            "0",
            _ => 6,
            (w, first, n, dst) =>
            {
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice(k * 8, 8), RngOf(w, (int)first + k).State);
                }
            },
            (c, n, src) =>
            {
                for (int k = 0; k < 6; k++)
                {
                    c.RngState[k] = MemoryMarshal.Read<ulong>(src.Slice(k * 8, 8));
                }

                return LoadStatus.Ok;
            }),
        Col.Custom(
            10,
            "RngInc",
            ColumnType.U64,
            8,
            "6",
            "0",
            _ => 6,
            (w, first, n, dst) =>
            {
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice(k * 8, 8), RngOf(w, (int)first + k).Inc);
                }
            },
            (c, n, src) =>
            {
                for (int k = 0; k < 6; k++)
                {
                    ulong inc = MemoryMarshal.Read<ulong>(src.Slice(k * 8, 8));
                    if ((inc & 1UL) == 0)
                    {
                        return LoadStatus.InvalidValue;
                    }

                    Pcg32 r = Pcg32.FromState(c.RngState[k], inc);
                    switch (k)
                    {
                        case 0:
                            c.W.RngCityGen = r;
                            break;
                        case 1:
                            c.W.RngZones = r;
                            break;
                        case 2:
                            c.W.RngSpawn = r;
                            break;
                        case 3:
                            c.W.RngBehaviour = r;
                            break;
                        case 4:
                            c.W.RngTransit = r;
                            break;
                        default:
                            c.W.RngRouting = r;
                            break;
                    }
                }

                return LoadStatus.Ok;
            }),
    });

    /// <summary>Подпоток PRNG по порядку <see cref="RngStream"/>.</summary>
    private static Pcg32 RngOf(World w, int k) => k switch
    {
        0 => w.RngCityGen,
        1 => w.RngZones,
        2 => w.RngSpawn,
        3 => w.RngBehaviour,
        4 => w.RngTransit,
        _ => w.RngRouting,
    };

    private static BlockDef Counters() => new("CNTR", "SimCounters по полям (новый счётчик - новый столбец, дефолт 0)", new[]
    {
        Col.Scalar<long>(1, "Ticks", w => w.Counters.Ticks, (c, v) => c.W.Counters.Ticks = v),
        Col.Scalar<long>(2, "VehiclesSpawned", w => w.Counters.VehiclesSpawned, (c, v) => c.W.Counters.VehiclesSpawned = v),
        Col.Scalar<long>(3, "TripsCompleted", w => w.Counters.TripsCompleted, (c, v) => c.W.Counters.TripsCompleted = v),
        Col.Scalar<long>(4, "TripsDropped", w => w.Counters.TripsDropped, (c, v) => c.W.Counters.TripsDropped = v),
        Col.Scalar<long>(5, "TripTimeTicksTotal", w => w.Counters.TripTimeTicksTotal, (c, v) => c.W.Counters.TripTimeTicksTotal = v),
        Col.Scalar<long>(6, "DistanceTotalMm", w => w.Counters.DistanceTotalMm, (c, v) => c.W.Counters.DistanceTotalMm = v),
        Col.Scalar<long>(7, "VehicleTicksTotal", w => w.Counters.VehicleTicksTotal, (c, v) => c.W.Counters.VehicleTicksTotal = v),
        Col.Scalar<long>(8, "MovingVehicleTicksTotal", w => w.Counters.MovingVehicleTicksTotal, (c, v) => c.W.Counters.MovingVehicleTicksTotal = v),
        Col.Scalar<int>(9, "ActiveVehicles", w => w.Counters.ActiveVehicles, (c, v) => c.W.Counters.ActiveVehicles = v),
        Col.Scalar<int>(10, "StuckVehicles", w => w.Counters.StuckVehicles, (c, v) => c.W.Counters.StuckVehicles = v),
        Col.Scalar<long>(11, "StuckEventsTotal", w => w.Counters.StuckEventsTotal, (c, v) => c.W.Counters.StuckEventsTotal = v),
        Col.Scalar<long>(12, "StuckTicksTotal", w => w.Counters.StuckTicksTotal, (c, v) => c.W.Counters.StuckTicksTotal = v),
        Col.Scalar<int>(13, "GridlockFlag", w => w.Counters.GridlockFlag, (c, v) => c.W.Counters.GridlockFlag = v),
        Col.Scalar<long>(14, "GridlockTicksTotal", w => w.Counters.GridlockTicksTotal, (c, v) => c.W.Counters.GridlockTicksTotal = v),
        Col.Scalar<long>(15, "LaneChanges", w => w.Counters.LaneChanges, (c, v) => c.W.Counters.LaneChanges = v),
        Col.Scalar<long>(16, "LaneChangesAborted", w => w.Counters.LaneChangesAborted, (c, v) => c.W.Counters.LaneChangesAborted = v),
        Col.Scalar<long>(17, "HardClamps", w => w.Counters.HardClamps, (c, v) => c.W.Counters.HardClamps = v),
        Col.Scalar<long>(18, "PathfindQueries", w => w.Counters.PathfindQueries, (c, v) => c.W.Counters.PathfindQueries = v),
        Col.Scalar<long>(19, "RouteDeferred", w => w.Counters.RouteDeferred, (c, v) => c.W.Counters.RouteDeferred = v),
        Col.Scalar<long>(20, "PathfindExpandedNodes", w => w.Counters.PathfindExpandedNodes, (c, v) => c.W.Counters.PathfindExpandedNodes = v),
        Col.Scalar<long>(21, "PathfindWork", w => w.Counters.PathfindWork, (c, v) => c.W.Counters.PathfindWork = v),
        Col.Scalar<long>(22, "RouteFailed", w => w.Counters.RouteFailed, (c, v) => c.W.Counters.RouteFailed = v),
        Col.Scalar<long>(23, "Reroutes", w => w.Counters.Reroutes, (c, v) => c.W.Counters.Reroutes = v),
        Col.Scalar<long>(24, "PlanCompactions", w => w.Counters.PlanCompactions, (c, v) => c.W.Counters.PlanCompactions = v),
        Col.Scalar<long>(25, "PlanUnpacks", w => w.Counters.PlanUnpacks, (c, v) => c.W.Counters.PlanUnpacks = v),
        Col.Scalar<long>(26, "UnpackWork", w => w.Counters.UnpackWork, (c, v) => c.W.Counters.UnpackWork = v),
        Col.Scalar<long>(27, "WindowRefills", w => w.Counters.WindowRefills, (c, v) => c.W.Counters.WindowRefills = v),
        Col.Scalar<long>(28, "RefillLate", w => w.Counters.RefillLate, (c, v) => c.W.Counters.RefillLate = v),
        Col.Scalar<long>(29, "TimedChecks", w => w.Counters.TimedChecks, (c, v) => c.W.Counters.TimedChecks = v),
        Col.Scalar<long>(30, "TimedTriggered", w => w.Counters.TimedTriggered, (c, v) => c.W.Counters.TimedTriggered = v),
        Col.Scalar<long>(31, "CostRecalcs", w => w.Counters.CostRecalcs, (c, v) => c.W.Counters.CostRecalcs = v),
        Col.Scalar<long>(32, "TimedReroutes", w => w.Counters.TimedReroutes, (c, v) => c.W.Counters.TimedReroutes = v),
        Col.Scalar<long>(33, "CustomizeRows", w => w.Counters.CustomizeRows, (c, v) => c.W.Counters.CustomizeRows = v),
        Col.Scalar<long>(34, "CustomizeWork", w => w.Counters.CustomizeWork, (c, v) => c.W.Counters.CustomizeWork = v),
        Col.Scalar<long>(35, "CustomizeLagTicks", w => w.Counters.CustomizeLagTicks, (c, v) => c.W.Counters.CustomizeLagTicks = v),
        Col.Scalar<long>(36, "OverlayRebuilds", w => w.Counters.OverlayRebuilds, (c, v) => c.W.Counters.OverlayRebuilds = v),
        Col.Scalar<int>(37, "OverlayDisabled", w => w.Counters.OverlayDisabled, (c, v) => c.W.Counters.OverlayDisabled = v),
        Col.Scalar<long>(38, "ConnectorEntries", w => w.Counters.ConnectorEntries, (c, v) => c.W.Counters.ConnectorEntries = v),
        Col.Scalar<long>(39, "CommandsApplied", w => w.Counters.CommandsApplied, (c, v) => c.W.Counters.CommandsApplied = v),
        Col.Scalar<long>(40, "CommandsRejected", w => w.Counters.CommandsRejected, (c, v) => c.W.Counters.CommandsRejected = v),
        Col.Scalar<long>(41, "CommandsDropped", w => w.Counters.CommandsDropped, (c, v) => c.W.Counters.CommandsDropped = v),
        Col.Scalar<long>(42, "EventsDropped", w => w.Counters.EventsDropped, (c, v) => c.W.Counters.EventsDropped = v),
        Col.Scalar<long>(43, "SpawnDeferredTicks", w => w.Counters.SpawnDeferredTicks, (c, v) => c.W.Counters.SpawnDeferredTicks = v),
        Col.Scalar<long>(44, "InvalidHandleOps", w => w.Counters.InvalidHandleOps, (c, v) => c.W.Counters.InvalidHandleOps = v),
        Col.Scalar<int>(45, "SignalizedNodes", w => w.Counters.SignalizedNodes, (c, v) => c.W.Counters.SignalizedNodes = v),
        Col.Scalar<int>(46, "NodeCount", w => w.Counters.NodeCount, (c, v) => c.W.Counters.NodeCount = v),
        Col.Scalar<int>(47, "SegmentCount", w => w.Counters.SegmentCount, (c, v) => c.W.Counters.SegmentCount = v),
        Col.Scalar<int>(48, "LaneCount", w => w.Counters.LaneCount, (c, v) => c.W.Counters.LaneCount = v),
        Col.Scalar<int>(49, "ZoneCount", w => w.Counters.ZoneCount, (c, v) => c.W.Counters.ZoneCount = v),
        Col.Scalar<long>(50, "BusDispatched", w => w.Counters.BusDispatched, (c, v) => c.W.Counters.BusDispatched = v),
        Col.Scalar<long>(51, "BusStopArrivals", w => w.Counters.BusStopArrivals, (c, v) => c.W.Counters.BusStopArrivals = v),
        Col.Scalar<long>(52, "BusHeadwayDeviationTicksTotal", w => w.Counters.BusHeadwayDeviationTicksTotal, (c, v) => c.W.Counters.BusHeadwayDeviationTicksTotal = v),
        Col.Scalar<long>(53, "BusHeadwaySamples", w => w.Counters.BusHeadwaySamples, (c, v) => c.W.Counters.BusHeadwaySamples = v),
        Col.Scalar<long>(54, "BusPassengersBoarded", w => w.Counters.BusPassengersBoarded, (c, v) => c.W.Counters.BusPassengersBoarded = v),
        Col.Scalar<long>(55, "Grants", w => w.Counters.Grants, (c, v) => c.W.Counters.Grants = v),
        Col.Scalar<long>(56, "GateDeniedExit", w => w.Counters.GateDeniedExit, (c, v) => c.W.Counters.GateDeniedExit = v),
        Col.Scalar<long>(57, "PriorityInversions", w => w.Counters.PriorityInversions, (c, v) => c.W.Counters.PriorityInversions = v),
        Col.Scalar<long>(58, "BoxWaitTicks", w => w.Counters.BoxWaitTicks, (c, v) => c.W.Counters.BoxWaitTicks = v),
        Col.Scalar<long>(59, "GateWaitTicksTotal", w => w.Counters.GateWaitTicksTotal, (c, v) => c.W.Counters.GateWaitTicksTotal = v),
        Col.Scalar<long>(60, "SignalLeftWaitMax", w => w.Counters.SignalLeftWaitMax, (c, v) => c.W.Counters.SignalLeftWaitMax = v),
        Col.Scalar<long>(61, "ArterialSignalGrants", w => w.Counters.ArterialSignalGrants, (c, v) => c.W.Counters.ArterialSignalGrants = v),
        Col.Scalar<long>(62, "ArterialSignalStops", w => w.Counters.ArterialSignalStops, (c, v) => c.W.Counters.ArterialSignalStops = v),
        Col.Scalar<long>(63, "StarveDenials", w => w.Counters.StarveDenials, (c, v) => c.W.Counters.StarveDenials = v),
        Col.Scalar<long>(64, "ArrowPhases", w => w.Counters.ArrowPhases, (c, v) => c.W.Counters.ArrowPhases = v),
        Col.Scalar<long>(65, "SpillOuts", w => w.Counters.SpillOuts, (c, v) => c.W.Counters.SpillOuts = v),
        Col.Scalar<long>(66, "ExitClaims", w => w.Counters.ExitClaims, (c, v) => c.W.Counters.ExitClaims = v),
        Col.Scalar<long>(67, "ExitClaimDenials", w => w.Counters.ExitClaimDenials, (c, v) => c.W.Counters.ExitClaimDenials = v),
        Col.Scalar<long>(68, "GrantsRevoked", w => w.Counters.GrantsRevoked, (c, v) => c.W.Counters.GrantsRevoked = v),
        Col.Scalar<int>(69, "MicroVehicles", w => w.Counters.MicroVehicles, (c, v) => c.W.Counters.MicroVehicles = v),
        Col.Scalar<int>(70, "MesoVehicles", w => w.Counters.MesoVehicles, (c, v) => c.W.Counters.MesoVehicles = v),
        Col.Scalar<long>(71, "MicroVehicleTicks", w => w.Counters.MicroVehicleTicks, (c, v) => c.W.Counters.MicroVehicleTicks = v),
        Col.Scalar<long>(72, "MesoVehicleTicks", w => w.Counters.MesoVehicleTicks, (c, v) => c.W.Counters.MesoVehicleTicks = v),
        Col.Scalar<long>(73, "MicroDistanceMm", w => w.Counters.MicroDistanceMm, (c, v) => c.W.Counters.MicroDistanceMm = v),
        Col.Scalar<long>(74, "MesoDistanceMm", w => w.Counters.MesoDistanceMm, (c, v) => c.W.Counters.MesoDistanceMm = v),
        Col.Scalar<long>(75, "LodEvals", w => w.Counters.LodEvals, (c, v) => c.W.Counters.LodEvals = v),
        Col.Scalar<long>(76, "LodCellFlips", w => w.Counters.LodCellFlips, (c, v) => c.W.Counters.LodCellFlips = v),
        Col.Scalar<long>(77, "LodBudgetSkips", w => w.Counters.LodBudgetSkips, (c, v) => c.W.Counters.LodBudgetSkips = v),
        Col.Scalar<long>(78, "MesoClamps", w => w.Counters.MesoClamps, (c, v) => c.W.Counters.MesoClamps = v),
        Col.Scalar<long>(79, "MesoLaneChanges", w => w.Counters.MesoLaneChanges, (c, v) => c.W.Counters.MesoLaneChanges = v),
        Col.Scalar<int>(80, "StillMaxTicks", w => w.Counters.StillMaxTicks, (c, v) => c.W.Counters.StillMaxTicks = v),
        Col.Scalar<int>(81, "StillConcurrent60Max", w => w.Counters.StillConcurrent60Max, (c, v) => c.W.Counters.StillConcurrent60Max = v),
        Col.Scalar<long>(82, "StillEpisodes60", w => w.Counters.StillEpisodes60, (c, v) => c.W.Counters.StillEpisodes60 = v),
        Col.Scalar<long>(83, "StillEpisodes120", w => w.Counters.StillEpisodes120, (c, v) => c.W.Counters.StillEpisodes120 = v),
        Col.Scalar<long>(84, "LaneShareSamples", w => w.Counters.LaneShareSamples, (c, v) => c.W.Counters.LaneShareSamples = v),
        Col.Scalar<long>(85, "LaneShareMaxPermilleSum", w => w.Counters.LaneShareMaxPermilleSum, (c, v) => c.W.Counters.LaneShareMaxPermilleSum = v),
        Col.Scalar<long>(86, "LaneShareH0", w => w.Counters.LaneShareH0, (c, v) => c.W.Counters.LaneShareH0 = v),
        Col.Scalar<long>(87, "LaneShareH1", w => w.Counters.LaneShareH1, (c, v) => c.W.Counters.LaneShareH1 = v),
        Col.Scalar<long>(88, "LaneShareH2", w => w.Counters.LaneShareH2, (c, v) => c.W.Counters.LaneShareH2 = v),
        Col.Scalar<long>(89, "LaneShareH3", w => w.Counters.LaneShareH3, (c, v) => c.W.Counters.LaneShareH3 = v),
        Col.Scalar<long>(90, "LaneShareH4", w => w.Counters.LaneShareH4, (c, v) => c.W.Counters.LaneShareH4 = v),
        Col.Scalar<long>(91, "LaneShareH5", w => w.Counters.LaneShareH5, (c, v) => c.W.Counters.LaneShareH5 = v),
        Col.Scalar<long>(92, "LaneShareH6", w => w.Counters.LaneShareH6, (c, v) => c.W.Counters.LaneShareH6 = v),
        Col.Scalar<long>(93, "LaneShareH7", w => w.Counters.LaneShareH7, (c, v) => c.W.Counters.LaneShareH7 = v),
        Col.Scalar<long>(94, "LaneShareH8", w => w.Counters.LaneShareH8, (c, v) => c.W.Counters.LaneShareH8 = v),
        Col.Scalar<long>(95, "LaneShareH9", w => w.Counters.LaneShareH9, (c, v) => c.W.Counters.LaneShareH9 = v),
    });
}
