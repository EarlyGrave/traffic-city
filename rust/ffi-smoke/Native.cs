using System;
using System.Runtime.InteropServices;

namespace FfiSmoke;

// Зеркальные struct C ABI (rust/crates/tc-ffi/include/tc_sim.h). Размеры сверяются с tc_struct_size.

[StructLayout(LayoutKind.Sequential)]
internal struct TcCapacities
{
    public int MaxVehicles, MaxNodes, MaxSegments, MaxLanes, MaxLanePoints;
    public int MaxZones, MaxBusRoutes, MaxBusStops, MaxBusStopsPerRoute, MaxRoutes;
    public int MaxPlanSteps, MaxPendingTrips, CommandQueueCapacity, EventCapacity;
    public int MaxSignalizedNodes, MaxConnectors, MaxConflicts;
    public int MaxPeds, MaxResidents, MaxPedPathSteps;
    public int MaxLots, MaxBuildings, MaxCityResidents, SkimObsCapacity;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcNodeDesc
{
    public float X, Z;
    public byte Control;
    public byte Pad0, Pad1, Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcSegmentDesc
{
    public int A, B, LanesAB, LanesBA;
    public float SpeedLimit;
    public byte Kind;
    public byte Pad0, Pad1, Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TcScenarioDesc
{
    public int StructSize;
    public int Pad0;
    public ulong Seed;
    public TcCapacities Capacities;
    public TcNodeDesc* Nodes;
    public int NodeCount;
    public int Pad1;
    public TcSegmentDesc* Segments;
    public int SegmentCount;
    public int Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcRuntimeOptions
{
    public int StructSize;
    public int WorkerThreads;
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct TcCommand
{
    public int Kind;
    public uint Tag;
    public int PayloadInt;   // первые 4 байта полезной нагрузки (остальные 52 - нули, Size = 64)
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcVec2
{
    public float X, Z;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcPilotCounters
{
    public long CommandsApplied, CommandsDropped;
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
internal struct TcVehicleRenderData
{
    public float X, Y, Z, Heading, Speed;
    public int Index;
    public ushort Generation;
    public byte Type, Flags;
    public float LateralOffset;
}

/// <summary>Владелец tc_sim*: tc_sim_destroy ровно один раз.</summary>
internal sealed class SimHandle : SafeHandle
{
    public SimHandle()
        : base(IntPtr.Zero, true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        Native.tc_sim_destroy(handle);
        return true;
    }
}

internal static class Native
{
    public const string Lib = "tc_ffi";

    public const int ErrNull = -1;
    public const int ErrArg = -2;
    public const int ErrPanic = -3;
    public const int ErrPoisoned = -4;
    public const int ErrAbi = -5;

    public const int SzScenarioDesc = 0;
    public const int SzNodeDesc = 1;
    public const int SzSegmentDesc = 2;
    public const int SzRuntimeOptions = 3;
    public const int SzCommand = 4;
    public const int SzVehicleRenderData = 5;
    public const int SzVec2 = 6;
    public const int SzPilotCounters = 7;
    public const int SzCapacities = 8;

    [DllImport(Lib)]
    public static extern uint tc_abi_version();

    [DllImport(Lib)]
    public static extern int tc_struct_size(int which);

    [DllImport(Lib)]
    public static extern unsafe int tc_sim_create(TcScenarioDesc* desc, TcRuntimeOptions* opts, out SimHandle sim);

    // Те же функции с «сырыми» указателями: проверка NULL-аргументов.
    [DllImport(Lib, EntryPoint = "tc_sim_create")]
    public static extern int tc_sim_create_raw(IntPtr desc, IntPtr opts, IntPtr sim);

    [DllImport(Lib)]
    public static extern void tc_sim_destroy(IntPtr sim);

    [DllImport(Lib)]
    public static extern int tc_sim_step(SimHandle sim, int ticks);

    [DllImport(Lib, EntryPoint = "tc_sim_step")]
    public static extern int tc_sim_step_raw(IntPtr sim, int ticks);

    [DllImport(Lib)]
    public static extern unsafe int tc_sim_enqueue(SimHandle sim, TcCommand* cmd);

    [DllImport(Lib, EntryPoint = "tc_sim_enqueue")]
    public static extern int tc_sim_enqueue_raw(IntPtr sim, IntPtr cmd);

    [DllImport(Lib)]
    public static extern long tc_sim_tick(SimHandle sim);

    [DllImport(Lib, EntryPoint = "tc_sim_tick")]
    public static extern long tc_sim_tick_raw(IntPtr sim);

    [DllImport(Lib)]
    public static extern unsafe TcVehicleRenderData* tc_sim_vehicles(SimHandle sim, out int len);

    [DllImport(Lib)]
    public static extern unsafe TcVec2* tc_sim_lane_points(SimHandle sim, out int len);

    [DllImport(Lib)]
    public static extern ulong tc_sim_network_hash(SimHandle sim);

    [DllImport(Lib, EntryPoint = "tc_sim_network_hash")]
    public static extern ulong tc_sim_network_hash_raw(IntPtr sim);

    [DllImport(Lib)]
    public static extern void tc_sim_pilot_counters(SimHandle sim, out TcPilotCounters counters);

    // Только в сборке с фичей test-panic.
    [DllImport(Lib)]
    public static extern int tc_sim_test_panic(SimHandle sim);
}
