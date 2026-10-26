using System;
using System.Runtime.InteropServices;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Core.Util;

namespace Sim.Core.Save;

// Блоки подвижного состояния: VEHC, TRIP, QUES, ROUT, RGRP, SMET.
internal static partial class SaveSchema
{
    private static readonly Func<World, HandlePool> _vehPool = static w => w.Vehicles.Pool;
    private static readonly Func<World, HandlePool> _routePool = static w => w.Routes.Pool;
    private static readonly Func<World, int> _routeHw = static w => w.Routes.Pool.HighWater;

    /// <summary>Задаёт <c>HighWater</c> и/или длину free-list пула; значения из файла обязаны быть в границах пула.</summary>
    private static LoadStatus PoolCounts(HandlePool p, int? highWater, int? freeCount)
    {
        int h = highWater ?? p.HighWater;
        int f = freeCount ?? p.FreeCount;
        if (h < 0 || h > p.Capacity || f < 0 || f > h)
        {
            return LoadStatus.CapacityExceeded;
        }

        p.RestoreCounts(h, f);
        return LoadStatus.Ok;
    }

    private static ColumnDef PoolHighWater(uint id, Func<World, HandlePool> pool) =>
        Col.Scalar<int>(id, "HighWater", w => pool(w).HighWater, (c, v) => PoolCounts(pool(c.W), v, null));

    private static ColumnDef PoolFreeCount(uint id, Func<World, HandlePool> pool) =>
        Col.Scalar<int>(id, "FreeCount", w => pool(w).FreeCount, (c, v) => PoolCounts(pool(c.W), null, v));

    private static BlockDef Vehicles() => new("VEHC", "машины: пул (поколения, free-list в LIFO-порядке) и поля; мёртвые слоты - канон свежего слота", new[]
    {
        PoolHighWater(1, _vehPool),
        PoolFreeCount(2, _vehPool),
        Col.Array<int>(3, "FreeList", w => w.Vehicles.Pool.FreeListArray, w => w.Vehicles.Pool.FreeCount, "FreeCount"),
        Col.Array<int>(4, "Generation", w => w.Vehicles.Pool.GenerationArray, _vehicleHw, "HighWater"),
        Col.Array<int>(5, "Lane", w => w.Vehicles.Lane, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<float>(6, "S", w => w.Vehicles.S, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<float>(7, "Speed", w => w.Vehicles.Speed, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<float>(8, "Accel", w => w.Vehicles.Accel, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<float>(9, "Length", w => w.Vehicles.Length, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<byte>(10, "Type", w => w.Vehicles.Type, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<float>(11, "DesiredSpeed", w => w.Vehicles.DesiredSpeed, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(12, "Ahead", w => w.Vehicles.Ahead, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(13, "Behind", w => w.Vehicles.Behind, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(14, "GhostLane", w => w.Vehicles.GhostLane, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(15, "GhostAhead", w => w.Vehicles.GhostAhead, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(16, "GhostBehind", w => w.Vehicles.GhostBehind, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<sbyte>(17, "LaneChangeDir", w => w.Vehicles.LaneChangeDir, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<ushort>(18, "LaneChangeTicks", w => w.Vehicles.LaneChangeTicks, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<ushort>(19, "NextMobilTick", w => w.Vehicles.NextMobilTick, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(20, "Route", w => w.Vehicles.Route, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(21, "RouteGen", w => w.Vehicles.RouteGen, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(22, "RouteCursor", w => w.Vehicles.RouteCursor, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(23, "DestZone", w => w.Vehicles.DestZone, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(24, "DestLane", w => w.Vehicles.DestLane, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<float>(25, "DestS", w => w.Vehicles.DestS, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(26, "NextConn", w => w.Vehicles.NextConn, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<byte>(27, "GateState", w => w.Vehicles.GateState, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(28, "GrantConnector", w => w.Vehicles.GrantConnector, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<uint>(29, "Ticket", w => w.Vehicles.Ticket, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<ushort>(30, "WaitTicks", w => w.Vehicles.WaitTicks, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(31, "PointHint", w => w.Vehicles.PointHint, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<float>(32, "Lateral", w => w.Vehicles.Lateral, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<long>(33, "SpawnTick", w => w.Vehicles.SpawnTick, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<ushort>(34, "StillTicks", w => w.Vehicles.StillTicks, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<byte>(35, "Flags", w => w.Vehicles.Flags, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(36, "BusRoute", w => w.Vehicles.BusRoute, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(37, "BusNextStop", w => w.Vehicles.BusNextStop, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<ushort>(38, "DwellTicks", w => w.Vehicles.DwellTicks, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<ushort>(39, "Passengers", w => w.Vehicles.Passengers, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<byte>(40, "DenyReason", w => w.Gate.DenyReason, _vehicleHw, "HighWater", "0", _vehPool),   // GateDeny конца прошлой фазы 4: детекторы Adaptive (SignalPhase.Detect) читают его раньше записи
    })
    {
        // Сеть и её динамика загружены: проверки и производные до первого обхода сети (в т.ч. Graph.Rebuild в RGRP).
        Before = SaveValidator.ValidateNetworkAndDerive,
        After = c =>
        {
            c.W.Vehicles.Pool.RecountAlive();
            return SaveValidator.ValidateVehicles(c);
        },
    };

    private static ColumnDef RingCount<T>(uint id, string name, Func<World, RingBuffer<T>> ring)
        where T : unmanaged =>
        Col.Scalar<int>(id, name, w => ring(w).Count, (c, v) =>
        {
            RingBuffer<T> r = ring(c.W);
            if (v < 0 || v > r.Capacity)
            {
                return LoadStatus.CapacityExceeded;
            }

            r.RestoreCount(v);
            return LoadStatus.Ok;
        });

    private static BlockDef Trips() => new("TRIP", "очередь отложенных поездок (FIFO, голова при загрузке = 0)", new[]
    {
        RingCount<PendingTrip>(1, "Count", w => w.Trips.Ring),
        Col.RingField<PendingTrip>(2, "FromZone", w => w.Trips.Ring, "Count", nameof(PendingTrip.FromZone)),
        Col.RingField<PendingTrip>(3, "ToZone", w => w.Trips.Ring, "Count", nameof(PendingTrip.ToZone)),
        Col.RingField<PendingTrip>(4, "FromLane", w => w.Trips.Ring, "Count", nameof(PendingTrip.FromLane)),
        Col.RingField<PendingTrip>(5, "FromS", w => w.Trips.Ring, "Count", nameof(PendingTrip.FromS)),
        Col.RingField<PendingTrip>(6, "Type", w => w.Trips.Ring, "Count", nameof(PendingTrip.Type)),
        Col.RingField<PendingTrip>(7, "DestLane", w => w.Trips.Ring, "Count", nameof(PendingTrip.DestLane)),
        Col.RingField<PendingTrip>(8, "DestS", w => w.Trips.Ring, "Count", nameof(PendingTrip.DestS)),
        Col.RingField<PendingTrip>(9, "Route", w => w.Trips.Ring, "Count", nameof(PendingTrip.Route)),
        Col.RingField<PendingTrip>(10, "RouteGen", w => w.Trips.Ring, "Count", nameof(PendingTrip.RouteGen)),
        Col.RingField<PendingTrip>(11, "ResidentRef", w => w.Trips.Ring, "Count", nameof(PendingTrip.ResidentRef)),
    })
    {
        After = c =>
        {
            c.W.Trips.RecountUnrouted();
            return SaveValidator.ValidateTrips(c);
        },
    };

    private static BlockDef Queues() => new("QUES", "очереди: перемаршрутизация, дозаполнение окон, команды (64 Б, раскладка core-api §4)", new[]
    {
        RingCount<RerouteEntry>(1, "ReroutingCount", w => w.Rerouting),
        RingCount<RefillEntry>(2, "RefillCount", w => w.RefillQueue),
        RingCount<Command>(3, "CommandCount", w => w.Commands),
        Col.RingField<RerouteEntry>(4, "ReroutingIndex", w => w.Rerouting, "ReroutingCount", nameof(RerouteEntry.Index)),
        Col.RingField<RerouteEntry>(5, "ReroutingGeneration", w => w.Rerouting, "ReroutingCount", nameof(RerouteEntry.Generation)),
        Col.RingField<RerouteEntry>(6, "ReroutingReadyTick", w => w.Rerouting, "ReroutingCount", nameof(RerouteEntry.ReadyTick)),
        Col.RingField<RerouteEntry>(7, "ReroutingKind", w => w.Rerouting, "ReroutingCount", nameof(RerouteEntry.Kind)),
        Col.RingField<RefillEntry>(8, "RefillIndex", w => w.RefillQueue, "RefillCount", nameof(RefillEntry.Index)),
        Col.RingField<RefillEntry>(9, "RefillGeneration", w => w.RefillQueue, "RefillCount", nameof(RefillEntry.Generation)),
        Col.RingField<Command>(10, "Commands", w => w.Commands, "CommandCount", string.Empty, rawSize: 64),
    })
    {
        After = SaveValidator.ValidateQueues,
    };

    private static long WindowTotal(World w)
    {
        RouteStore r = w.Routes;
        long total = 0;
        for (int i = 0; i < r.Pool.HighWater; i++)
        {
            if (r.Pool.IsAlive(i))
            {
                total += r.WinCount[i];
            }
        }

        return total;
    }

    private static void CopyWindow(World w, long first, int n, Span<byte> dst, bool lane)
    {
        RouteStore r = w.Routes;
        int ws = r.WindowSteps;
        long pos = 0;
        long end = first + n;
        for (int i = 0; i < r.Pool.HighWater && pos < end; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            int cnt = r.WinCount[i];
            long lo = Math.Max(pos, first);
            long hi = Math.Min(pos + cnt, end);
            for (long k = lo; k < hi; k++)
            {
                RouteStep s = r.Window[(i * ws) + (int)(k - pos)];
                int v = lane ? s.Lane : s.Node;
                MemoryMarshal.Write(dst.Slice((int)(k - first) * 4, 4), in v);
            }

            pos += cnt;
        }
    }

    private static LoadStatus StoreWindow(World w, long n, ReadOnlySpan<byte> src, bool lane)
    {
        RouteStore r = w.Routes;
        int ws = r.WindowSteps;
        long pos = 0;
        for (int i = 0; i < r.Pool.HighWater; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            int cnt = r.WinCount[i];
            if (cnt > ws || pos + cnt > n)
            {
                return LoadStatus.InvalidValue;
            }

            for (int k = 0; k < cnt; k++)
            {
                int v = MemoryMarshal.Read<int>(src.Slice((int)(pos + k) * 4, 4));
                ref RouteStep s = ref r.Window[(i * ws) + k];
                if (lane)
                {
                    s.Lane = v;
                }
                else
                {
                    s.Node = v;
                }
            }

            pos += cnt;
        }

        return pos == n ? LoadStatus.Ok : LoadStatus.InvalidValue;
    }

    private static BlockDef Routes() => new("ROUT", "маршруты: пул, окна живых слотов, планы (арена без мусора)", new[]
    {
        PoolHighWater(1, _routePool),
        PoolFreeCount(2, _routePool),
        Col.Array<int>(3, "FreeList", w => w.Routes.Pool.FreeListArray, w => w.Routes.Pool.FreeCount, "FreeCount"),
        Col.Array<int>(4, "Generation", w => w.Routes.Pool.GenerationArray, _routeHw, "HighWater"),
        Col.Scalar<int>(5, "PlanUsed", w => w.Routes.PlanUsed, (c, v) =>
        {
            if (v < 0 || v > c.W.Routes.PlanArena.Length)
            {
                return LoadStatus.CapacityExceeded;
            }

            c.W.Routes.PlanUsed = v;
            return LoadStatus.Ok;
        }),
        Col.Scalar<int>(6, "Compactions", w => w.Routes.Compactions, (c, v) => c.W.Routes.Compactions = v),
        Col.Scalar<int>(7, "WindowSteps", w => w.Routes.WindowSteps, (c, v) => v == c.W.Routes.WindowSteps ? LoadStatus.Ok : LoadStatus.InvalidValue),
        Col.Array<byte>(8, "WinCount", w => w.Routes.WinCount, _routeHw, "HighWater", "0", _routePool),
        Col.Array<int>(9, "PlanFirst", w => w.Routes.PlanFirst, _routeHw, "HighWater", "0", _routePool),
        Col.Array<int>(10, "PlanCount", w => w.Routes.PlanCount, _routeHw, "HighWater", "0", _routePool),
        Col.Array<int>(11, "PlanCap", w => w.Routes.PlanCap, _routeHw, "HighWater", "0", _routePool),
        Col.Array<float>(12, "PlanCost", w => w.Routes.PlanCost, _routeHw, "HighWater", "0", _routePool),
        Col.Array<long>(13, "PlanTick", w => w.Routes.PlanTick, _routeHw, "HighWater", "0", _routePool),
        Col.Custom(
            14,
            "WinLane",
            ColumnType.I32,
            4,
            "Σ WinCount живых",
            "0",
            WindowTotal,
            (w, first, n, dst) => CopyWindow(w, first, n, dst, lane: true),
            (c, n, src) => StoreWindow(c.W, n, src, lane: true)),
        Col.Custom(
            15,
            "WinNode",
            ColumnType.I32,
            4,
            "Σ WinCount живых",
            "-1",
            WindowTotal,
            (w, first, n, dst) => CopyWindow(w, first, n, dst, lane: false),
            (c, n, src) => StoreWindow(c.W, n, src, lane: false)),
        Col.Custom(
            16,
            "PlanArena",
            ColumnType.I32,
            4,
            "PlanUsed",
            "0",
            w => w.Routes.PlanUsed,
            CopyPlanArena,
            (c, n, src) =>
            {
                int[] a = c.W.Routes.PlanArena;
                if (n > a.Length)
                {
                    return LoadStatus.CapacityExceeded;
                }

                src.CopyTo(MemoryMarshal.AsBytes(new Span<int>(a, 0, (int)n)));
                return LoadStatus.Ok;
            }),
    })
    {
        After = c =>
        {
            c.W.Routes.Pool.RecountAlive();
            return SaveValidator.ValidateRoutes(c);
        },
    };

    /// <summary>Арена планов: блоки живых слотов как есть, всё остальное (мёртвые блоки, хвосты <c>PlanCap &gt; PlanCount</c>) - нули.</summary>
    private static void CopyPlanArena(World w, long first, int n, Span<byte> dst)
    {
        dst.Clear();
        RouteStore r = w.Routes;
        long end = first + n;
        for (int i = 0; i < r.Pool.HighWater; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            long lo = Math.Max(r.PlanFirst[i], first);
            long hi = Math.Min(Math.Min((long)r.PlanFirst[i] + r.PlanCount[i], end), r.PlanArena.Length);
            if (hi > lo)
            {
                MemoryMarshal.AsBytes(new ReadOnlySpan<int>(r.PlanArena, (int)lo, (int)(hi - lo))).CopyTo(dst.Slice((int)(lo - first) * 4));
            }
        }
    }

    private static BlockDef RouteGraphBlock() => new("RGRP", "граф маршрутизации: веса (топология - производное)", new[]
    {
        Col.Scalar<int>(1, "BuiltVersion", w => w.Graph.BuiltVersion, (c, v) => c.GraphBuiltVersion = v, "-1"),
        Col.Scalar<int>(2, "WeightsVersion", w => w.Graph.WeightsVersion, (c, v) => c.GraphWeightsVersion = v),
        Col.Array<float>(3, "SegCost", w => w.Graph.SegCost, w => w.Graph.DirSegCount, "DirSegCount"),
        Col.Array<float>(4, "ArcCost", w => w.Graph.ArcCost, w => w.Graph.ArcCount, "ArcCount"),
    })
    {
        Before = c =>
        {
            c.W.Graph.Rebuild();   // топология и длины столбцов - из загруженной сети; веса вернёт блок
            return LoadStatus.Ok;
        },
        After = c =>
        {
            c.W.Graph.BuiltVersion = c.GraphBuiltVersion;
            c.W.Graph.WeightsVersion = c.GraphWeightsVersion;
            return LoadStatus.Ok;
        },
    };

    private static BlockDef SignalMetricsBlock() => new("SMET", "метрики светофоров (SignalMetrics) и накопители допусков по connector'ам", new[]
    {
        Col.Array<long>(1, "SignalWaitTicks", w => w.SignalWait.WaitTicks, static _ => SignalMetrics.Maneuvers, "4"),
        Col.Array<long>(2, "SignalGrants", w => w.SignalWait.Grants, static _ => SignalMetrics.Maneuvers, "4"),
        Col.Array<long>(3, "SignalHist", w => w.SignalWait.Hist, static _ => SignalMetrics.Maneuvers * SignalMetrics.HistBuckets, "4 * 121"),
        Col.Array<long>(4, "ConnWaitSum", w => w.Gate.ConnWaitSum, _connN, "Count"),
        Col.Array<int>(5, "ConnGrantCount", w => w.Gate.ConnGrantCount, _connN, "Count"),
        Col.Array<float>(6, "ConnDelay", w => w.Gate.ConnDelay, _connN, "Count"),
    });
}
