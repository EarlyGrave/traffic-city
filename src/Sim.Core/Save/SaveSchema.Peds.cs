using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Peds;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Save;

// Блоки пешеходов (world_version 2, pedestrians.md §11): PEDS, PPTH, RESI, XING, PQUE. Есть ⇔ peds.enabled; все critical.
// Столбцы заморожены в P2 для P3 (переходы) и P4 (пассажиры): логика меняется, схема - нет.
internal static partial class SaveSchema
{
    private static readonly Func<World, HandlePool> _pedPool = static w => w.Peds!.P.Pool;
    private static readonly Func<World, int> _pedHw = static w => w.Peds!.P.Pool.HighWater;
    private static readonly Func<World, int> _resN = static w => w.Peds!.R.Count;
    private static readonly Func<World, int> _xN = static w => PedWorld.XCount(w);

    private static BlockDef PedsBlock() => new("PEDS", "пешеходы: пул (поколения, free-list в LIFO-порядке) и поля; мёртвые слоты - канон (ссылки -1, остальное 0)", new[]
    {
        PoolHighWater(1, _pedPool),
        PoolFreeCount(2, _pedPool),
        Col.Array<int>(3, "FreeList", w => w.Peds!.P.Pool.FreeListArray, w => w.Peds!.P.Pool.FreeCount, "FreeCount"),
        Col.Array<int>(4, "Generation", w => w.Peds!.P.Pool.GenerationArray, _pedHw, "HighWater"),
        Col.Array<byte>(5, "State", w => w.Peds!.P.State, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<byte>(6, "Leg", w => w.Peds!.P.Leg, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<int>(7, "Resident", w => w.Peds!.P.Resident, _pedHw, "HighWater", "-1", _pedPool, -1),
        Col.Array<int>(8, "Edge", w => w.Peds!.P.Edge, _pedHw, "HighWater", "-1", _pedPool, -1),
        Col.Array<long>(9, "T0", w => w.Peds!.P.T0, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<int>(10, "S0Mm", w => w.Peds!.P.S0Mm, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<int>(11, "VMmT", w => w.Peds!.P.VMmT, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<long>(12, "EvTick", w => w.Peds!.P.EvTick, _pedHw, "HighWater", "-1", _pedPool, -1L),
        Col.Array<int>(13, "PathCursor", w => w.Peds!.P.PathCursor, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<int>(14, "PathCount", w => w.Peds!.P.PathCount, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<int>(15, "GoalEdge", w => w.Peds!.P.GoalEdge, _pedHw, "HighWater", "-1", _pedPool, -1),
        Col.Array<int>(16, "GoalSMm", w => w.Peds!.P.GoalSMm, _pedHw, "HighWater", "0", _pedPool),
        Col.Array<int>(17, "Next", w => w.Peds!.P.Next, _pedHw, "HighWater", "-1", _pedPool, -1),
        Col.Array<int>(18, "Bus", w => w.Peds!.P.Bus, _pedHw, "HighWater", "-1", _pedPool, -1),
        Col.Array<int>(19, "AlightStop", w => w.Peds!.P.AlightStop, _pedHw, "HighWater", "-1", _pedPool, -1),
        Col.Array<long>(20, "TripTick", w => w.Peds!.P.TripTick, _pedHw, "HighWater", "0", _pedPool),
    })
    {
        After = c =>
        {
            c.W.Peds!.P.Pool.RecountAlive();
            return LoadStatus.Ok;
        },
        PresentText = "peds.enabled",
        Present = static w => w.Peds != null,
    };

    private static BlockDef PedPaths() => new("PPTH", "арена путей пешеходов: блок max_ped_path_steps шагов на слот (мёртвые слоты - нули)", new[]
    {
        Col.Scalar<int>(1, "Steps", w => w.Peds!.P.MaxSteps, (c, v) => v == c.W.Peds!.P.MaxSteps ? LoadStatus.Ok : LoadStatus.InvalidValue),
        Col.Custom(
            2,
            "Path",
            ColumnType.I32,
            4,
            "HighWater * Steps",
            "0",
            w => (long)w.Peds!.P.Pool.HighWater * w.Peds.P.MaxSteps,
            (w, first, n, dst) =>
            {
                PedStore ps = w.Peds!.P;
                ReadOnlySpan<int> src = new(ps.Path, (int)first, n);
                MemoryMarshal.AsBytes(src).CopyTo(dst);
                int steps = ps.MaxSteps;
                for (int k = 0; k < n; k++)
                {
                    if (!ps.Pool.IsAlive((int)((first + k) / steps)))
                    {
                        MemoryMarshal.Write(dst.Slice(k * 4, 4), 0);
                    }
                }
            },
            (c, n, src) =>
            {
                int[] a = c.W.Peds!.P.Path;
                if (n > a.Length)
                {
                    return LoadStatus.CapacityExceeded;
                }

                src.CopyTo(MemoryMarshal.AsBytes(new Span<int>(a, 0, (int)n)));
                return LoadStatus.Ok;
            }),
    })
    {
        PresentText = "peds.enabled",
        Present = static w => w.Peds != null,
    };

    private static BlockDef ResidentsBlock() => new("RESI", "жители-заготовки: дом, работа, способ, время готовности, очередь готовых (голова/хвост)", new[]
    {
        Col.Scalar<int>(1, "Count", w => w.Peds!.R.Count, (c, v) =>
        {
            if (v < 0 || v > c.W.Peds!.R.Capacity)
            {
                return LoadStatus.CapacityExceeded;
            }

            c.W.Peds!.R.Count = v;
            return LoadStatus.Ok;
        }),
        Col.Scalar<int>(2, "ReadyHead", w => w.Peds!.R.ReadyHead, (c, v) => c.W.Peds!.R.ReadyHead = v, "-1"),
        Col.Scalar<int>(3, "ReadyTail", w => w.Peds!.R.ReadyTail, (c, v) => c.W.Peds!.R.ReadyTail = v, "-1"),
        Col.Array<int>(4, "HomeEdge", w => w.Peds!.R.HomeEdge, _resN, "Count", "-1"),
        Col.Array<int>(5, "HomeSMm", w => w.Peds!.R.HomeSMm, _resN, "Count"),
        Col.Array<int>(6, "WorkEdge", w => w.Peds!.R.WorkEdge, _resN, "Count", "-1"),
        Col.Array<int>(7, "WorkSMm", w => w.Peds!.R.WorkSMm, _resN, "Count"),
        Col.Array<int>(8, "HomeZone", w => w.Peds!.R.HomeZone, _resN, "Count", "-1"),
        Col.Array<int>(9, "WorkZone", w => w.Peds!.R.WorkZone, _resN, "Count", "-1"),
        Col.Array<byte>(10, "ModeOut", w => w.Peds!.R.ModeOut, _resN, "Count"),
        Col.Array<byte>(11, "ModeBack", w => w.Peds!.R.ModeBack, _resN, "Count"),
        Col.Array<int>(12, "RouteOut", w => w.Peds!.R.RouteOut, _resN, "Count", "-1"),
        Col.Array<int>(13, "StopAOut", w => w.Peds!.R.StopAOut, _resN, "Count", "-1"),
        Col.Array<int>(14, "StopBOut", w => w.Peds!.R.StopBOut, _resN, "Count", "-1"),
        Col.Array<int>(15, "RouteBack", w => w.Peds!.R.RouteBack, _resN, "Count", "-1"),
        Col.Array<int>(16, "StopABack", w => w.Peds!.R.StopABack, _resN, "Count", "-1"),
        Col.Array<int>(17, "StopBBack", w => w.Peds!.R.StopBBack, _resN, "Count", "-1"),
        Col.Array<byte>(18, "Where", w => w.Peds!.R.Where, _resN, "Count"),
        Col.Array<long>(19, "ReadyTick", w => w.Peds!.R.ReadyTick, _resN, "Count"),
        Col.Array<int>(20, "Ped", w => w.Peds!.R.Ped, _resN, "Count", "-1"),
        Col.Array<int>(21, "ReadyNext", w => w.Peds!.R.ReadyNext, _resN, "Count", "-2"),
    })
    {
        PresentText = "peds.enabled",
        Present = static w => w.Peds != null,
    };

    private static BlockDef CrossingsBlock() => new("XING", "переходы (по индексу: слоты рукавов, затем переходы на перегоне), очереди остановок, пассажиры автобусов, гистограммы ожидания", new[]
    {
        Col.Array<byte>(1, "XState", w => w.Peds!.X.XState, _xN, "XCount"),
        Col.Array<int>(2, "QHead", w => w.Peds!.X.QHead, _xN, "XCount", "-1"),
        Col.Array<int>(3, "QTail", w => w.Peds!.X.QTail, _xN, "XCount", "-1"),
        Col.Array<int>(4, "QCount", w => w.Peds!.X.QCount, _xN, "XCount"),
        Col.Array<int>(5, "OnCross", w => w.Peds!.X.OnCross, _xN, "XCount"),
        Col.Array<long>(6, "BusyUntil", w => w.Peds!.X.BusyUntil, _xN, "XCount"),
        Col.Array<long>(7, "ClosedSince", w => w.Peds!.X.ClosedSince, _xN, "XCount"),
        Col.Array<long>(8, "CarWindowUntil", w => w.Peds!.X.CarWindowUntil, _xN, "XCount"),
        Col.Array<byte>(9, "CarsWaiting", w => w.Peds!.X.CarsWaiting, _xN, "XCount"),
        Col.Array<long>(10, "WaitSum", w => w.Peds!.X.WaitSum, _xN, "XCount"),
        Col.Array<int>(11, "Releases", w => w.Peds!.X.Releases, _xN, "XCount"),
        Col.Array<int>(12, "MeasuredWait", w => w.Peds!.X.MeasuredWait, _xN, "XCount"),
        Col.Array<int>(13, "StopQHead", w => w.Peds!.StopQHead, _stopN, "BusStopCount", "-1"),
        Col.Array<int>(14, "StopQTail", w => w.Peds!.StopQTail, _stopN, "BusStopCount", "-1"),
        Col.Array<int>(15, "StopQCount", w => w.Peds!.StopQCount, _stopN, "BusStopCount"),
        Col.Array<int>(16, "PaxHead", w => w.Peds!.PaxHead, _vehicleHw, "Vehicles.HighWater", "-1", _vehPool, -1),
        Col.Array<long>(17, "CrossWaitHist", w => w.Peds!.CrossWaitHist, static _ => PedWorld.HistBuckets, "256"),
        Col.Array<long>(18, "StopWaitHist", w => w.Peds!.StopWaitHist, static _ => PedWorld.HistBuckets, "256"),
    })
    {
        PresentText = "peds.enabled",
        Present = static w => w.Peds != null,
    };

    private static BlockDef PedPathQueue() => new("PQUE", "очередь запросов путей пешеходов (FIFO, голова при загрузке = 0)", new[]
    {
        RingCount<PathRequest>(1, "Count", w => w.Peds!.PathQueue),
        Col.RingField<PathRequest>(2, "Index", w => w.Peds!.PathQueue, "Count", nameof(PathRequest.Index)),
        Col.RingField<PathRequest>(3, "Generation", w => w.Peds!.PathQueue, "Count", nameof(PathRequest.Generation)),
    })
    {
        PresentText = "peds.enabled",
        Present = static w => w.Peds != null,
    };

    // ---- PARM: секция peds и модель пассажиров (world_version 2); ids продолжают 85 ----

    private static ColumnDef[] PedParams() => new[]
    {
        Col.Flag(86, "PedsEnabled", w => w.PedCfg.Enabled, (c, v) => c.Desc.Peds.Enabled = v),
        Col.Scalar<int>(87, "TransitPassengerModel", w => (int)w.TransitCfg.PassengerModel, (c, v) => c.Desc.Transit.PassengerModel = (PassengerModel)v),
        Col.Scalar<int>(88, "MaxPeds", w => w.Caps.MaxPeds, (c, v) => c.Desc.Capacities.MaxPeds = v),
        Col.Scalar<int>(89, "MaxResidents", w => w.Caps.MaxResidents, (c, v) => c.Desc.Capacities.MaxResidents = v),
        Col.Scalar<int>(90, "MaxPedPathSteps", w => w.Caps.MaxPedPathSteps, (c, v) => c.Desc.Capacities.MaxPedPathSteps = v),
        Col.Scalar<int>(91, "PedCount", w => w.PedCfg.Count, (c, v) => c.Desc.Peds.Count = v),
        Col.Scalar<int>(92, "PedResidents", w => w.PedCfg.Residents, (c, v) => c.Desc.Peds.Residents = v),
        Col.Scalar<int>(93, "PedSpawnRampTicks", w => w.PedCfg.SpawnRampTicks, (c, v) => c.Desc.Peds.SpawnRampTicks = v),
        Col.Scalar<int>(94, "PedSpeedMinMmPerTick", w => w.PedCfg.SpeedMinMmPerTick, (c, v) => c.Desc.Peds.SpeedMinMmPerTick = v),
        Col.Scalar<int>(95, "PedSpeedMaxMmPerTick", w => w.PedCfg.SpeedMaxMmPerTick, (c, v) => c.Desc.Peds.SpeedMaxMmPerTick = v),
        Col.Scalar<int>(96, "PedActivityMinTicks", w => w.PedCfg.ActivityMinTicks, (c, v) => c.Desc.Peds.ActivityMinTicks = v),
        Col.Scalar<int>(97, "PedActivityMaxTicks", w => w.PedCfg.ActivityMaxTicks, (c, v) => c.Desc.Peds.ActivityMaxTicks = v),
        Col.Scalar<int>(98, "PedWalkOnlyMm", w => w.PedCfg.WalkOnlyMm, (c, v) => c.Desc.Peds.WalkOnlyMm = v),
        Col.Scalar<int>(99, "PedMaxWalkToStopMm", w => w.PedCfg.MaxWalkToStopMm, (c, v) => c.Desc.Peds.MaxWalkToStopMm = v),
        Col.Scalar<int>(100, "PedMaxWalkMm", w => w.PedCfg.MaxWalkMm, (c, v) => c.Desc.Peds.MaxWalkMm = v),
        Col.Scalar<int>(101, "PedSidewalkWidthMm", w => w.PedCfg.SidewalkWidthMm, (c, v) => c.Desc.Peds.SidewalkWidthMm = v),
        Col.Scalar<int>(102, "PedCrosswalkWidthMm", w => w.PedCfg.CrosswalkWidthMm, (c, v) => c.Desc.Peds.CrosswalkWidthMm = v),
        Col.Scalar<int>(103, "PedArterialCrossings", w => (int)w.PedCfg.ArterialCrossings, (c, v) => c.Desc.Peds.ArterialCrossings = (ArterialCrossings)v),
        Col.Flag(104, "PedMidblockAtStops", w => w.PedCfg.MidblockAtStops, (c, v) => c.Desc.Peds.MidblockAtStops = v),
        Col.Scalar<int>(105, "PedWalkTicks", w => w.PedCfg.WalkTicks, (c, v) => c.Desc.Peds.WalkTicks = v),
        Col.Scalar<int>(106, "PedClearSpeedMmPerTick", w => w.PedCfg.ClearSpeedMmPerTick, (c, v) => c.Desc.Peds.ClearSpeedMmPerTick = v),
        Col.Scalar<int>(107, "PedMaxCloseTicks", w => w.PedCfg.MaxCloseTicks, (c, v) => c.Desc.Peds.MaxCloseTicks = v),
        Col.Scalar<int>(108, "PedCarWindowTicks", w => w.PedCfg.CarWindowTicks, (c, v) => c.Desc.Peds.CarWindowTicks = v),
        Col.Scalar<int>(109, "PedPathfindBudgetPerTick", w => w.PedCfg.PathfindBudgetPerTick, (c, v) => c.Desc.Peds.PathfindBudgetPerTick = v),
        Col.Scalar<int>(110, "PedWorkBudgetPerTick", w => w.PedCfg.WorkBudgetPerTick, (c, v) => c.Desc.Peds.WorkBudgetPerTick = v),
        Col.Scalar<int>(111, "PedStuckCrossTicks", w => w.PedCfg.StuckCrossTicks, (c, v) => c.Desc.Peds.StuckCrossTicks = v),
        Col.Scalar<int>(112, "PedStuckStopTicks", w => w.PedCfg.StuckStopTicks, (c, v) => c.Desc.Peds.StuckStopTicks = v),
    };

    // ---- WRLD: поток Peds и цель пешеходов ----

    private static ColumnDef[] PedWorldState() => new[]
    {
        Col.Scalar<ulong>(11, "PedRngState", w => w.RngPeds.State, (c, v) => c.PedRngState = v),
        Col.Scalar<ulong>(12, "PedRngInc", w => w.RngPeds.Inc, (c, v) =>
        {
            if (v == 0UL && c.PedRngState == 0UL)
            {
                return LoadStatus.Ok;   // сейв без пешеходов (миграция 1 -> 2): поток остаётся свежим
            }

            if ((v & 1UL) == 0)
            {
                return LoadStatus.InvalidValue;
            }

            c.W.RngPeds = Pcg32.FromState(c.PedRngState, v);
            return LoadStatus.Ok;
        }),
        Col.Scalar<int>(13, "TargetPeds", w => w.TargetPeds, (c, v) => c.W.TargetPeds = v),
    };

    // ---- CNTR: счётчики пешеходов (ids продолжают 95) ----

    private static ColumnDef[] PedCounters() => new[]
    {
        Col.Scalar<int>(96, "PedActive", w => w.Counters.PedActive, (c, v) => c.W.Counters.PedActive = v),
        Col.Scalar<long>(97, "PedActiveTicksTotal", w => w.Counters.PedActiveTicksTotal, (c, v) => c.W.Counters.PedActiveTicksTotal = v),
        Col.Scalar<long>(98, "PedSpawned", w => w.Counters.PedSpawned, (c, v) => c.W.Counters.PedSpawned = v),
        Col.Scalar<long>(99, "PedTripsCompleted", w => w.Counters.PedTripsCompleted, (c, v) => c.W.Counters.PedTripsCompleted = v),
        Col.Scalar<long>(100, "PedTripTicksTotal", w => w.Counters.PedTripTicksTotal, (c, v) => c.W.Counters.PedTripTicksTotal = v),
        Col.Scalar<long>(101, "PedCrossWaitTicksTotal", w => w.Counters.PedCrossWaitTicksTotal, (c, v) => c.W.Counters.PedCrossWaitTicksTotal = v),
        Col.Scalar<long>(102, "PedCrossReleases", w => w.Counters.PedCrossReleases, (c, v) => c.W.Counters.PedCrossReleases = v),
        Col.Scalar<long>(103, "PedStopWaitTicksTotal", w => w.Counters.PedStopWaitTicksTotal, (c, v) => c.W.Counters.PedStopWaitTicksTotal = v),
        Col.Scalar<long>(104, "PedStopBoardings", w => w.Counters.PedStopBoardings, (c, v) => c.W.Counters.PedStopBoardings = v),
        Col.Scalar<long>(105, "PedStuckEvents", w => w.Counters.PedStuckEvents, (c, v) => c.W.Counters.PedStuckEvents = v),
        Col.Scalar<long>(106, "CarCrossDelayTicks", w => w.Counters.CarCrossDelayTicks, (c, v) => c.W.Counters.CarCrossDelayTicks = v),
        Col.Scalar<long>(107, "CrossingClosedTicks", w => w.Counters.CrossingClosedTicks, (c, v) => c.W.Counters.CrossingClosedTicks = v),
        Col.Scalar<long>(108, "CrossingObservedTicks", w => w.Counters.CrossingObservedTicks, (c, v) => c.W.Counters.CrossingObservedTicks = v),
        Col.Scalar<long>(109, "BusPassengersAlighted", w => w.Counters.BusPassengersAlighted, (c, v) => c.W.Counters.BusPassengersAlighted = v),
        Col.Scalar<long>(110, "BusPaxOnBoardTicks", w => w.Counters.BusPaxOnBoardTicks, (c, v) => c.W.Counters.BusPaxOnBoardTicks = v),
        Col.Scalar<long>(111, "PedPathQueries", w => w.Counters.PedPathQueries, (c, v) => c.W.Counters.PedPathQueries = v),
        Col.Scalar<long>(112, "PedPathWork", w => w.Counters.PedPathWork, (c, v) => c.W.Counters.PedPathWork = v),
        Col.Scalar<long>(113, "PedNoPath", w => w.Counters.PedNoPath, (c, v) => c.W.Counters.PedNoPath = v),
        Col.Scalar<long>(114, "PedModeFallback", w => w.Counters.PedModeFallback, (c, v) => c.W.Counters.PedModeFallback = v),
        Col.Scalar<long>(115, "GrantsRevokedPed", w => w.Counters.GrantsRevokedPed, (c, v) => c.W.Counters.GrantsRevokedPed = v),
        Col.Scalar<long>(116, "PedAllocFailed", w => w.Counters.PedAllocFailed, (c, v) => c.W.Counters.PedAllocFailed = v),
        Col.Scalar<long>(117, "PedNetResets", w => w.Counters.PedNetResets, (c, v) => c.W.Counters.PedNetResets = v),
    };

    // ---- SIGS: пешеходная фаза светофора (P3), ids продолжают 38 ----

    private static ColumnDef[] PedSignalColumns() => new[]
    {
        Col.Array<ushort>(39, "XWalkLeft", w => w.Signals.XWalkLeft, w => w.Signals.Count * SignalStore.XSlots, "Count * 8"),
        Col.Array<ushort>(40, "XClearLeft", w => w.Signals.XClearLeft, w => w.Signals.Count * SignalStore.XSlots, "Count * 8"),
        Col.Array<byte>(41, "XServed", w => w.Signals.XServed, _signalN, "Count"),
    };

    /// <summary>Блок с добавленными в конец столбцами (новые id в старых блоках: PARM, WRLD, CNTR, SIGS).</summary>
    private static BlockDef Extend(BlockDef b, params ColumnDef[][] extras)
    {
        int total = b.Columns.Length;
        foreach (ColumnDef[] extra in extras)
        {
            total += extra.Length;
        }

        var all = new ColumnDef[total];
        Array.Copy(b.Columns, all, b.Columns.Length);
        int at = b.Columns.Length;
        foreach (ColumnDef[] extra in extras)
        {
            Array.Copy(extra, 0, all, at, extra.Length);
            at += extra.Length;
        }

        return new BlockDef(b.Name, b.Title, all)
        {
            Schema = b.Schema,
            Present = b.Present,
            PresentText = b.PresentText,
            PreWorld = b.PreWorld,
            After = b.After,
            Before = b.Before,
        };
    }
}
