using System.Runtime.InteropServices;
using Sim.Core.Network;

namespace Sim.Core.Api;

public enum CommandKind : int
{
    None = 0,
    BuildRoad = 1,
    SpawnTrip = 2,
    SetSignalPlan = 3,
    CreateBusRoute = 4,
    AppendBusStop = 5,
    SpawnBus = 6,
    SelectVehicle = 7,
    ClearSelection = 8,
    SetTargetVehicleCount = 9,
    SetNodeControl = 10,
}

public enum SignalMode : int
{
    Off = 0,
    Fixed = 1,
    FlashYellow = 2,
}

public enum BusStopKind : int
{
    Curb = 0,
    Bay = 1,
}

/// <summary>Плоский tagged-union фиксированного размера 64 байта (core-api §4.1). Payload — с offset 8.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct Command
{
    [FieldOffset(0)]
    public CommandKind Kind;

    /// <summary>Произвольная метка клиента, эхо в <see cref="SimEvent.Tag"/>.</summary>
    [FieldOffset(4)]
    public uint Tag;

    [FieldOffset(8)]
    public BuildRoadCmd BuildRoad;

    [FieldOffset(8)]
    public SpawnTripCmd SpawnTrip;

    [FieldOffset(8)]
    public SetSignalPlanCmd SetSignalPlan;

    [FieldOffset(8)]
    public CreateBusRouteCmd CreateBusRoute;

    [FieldOffset(8)]
    public AppendBusStopCmd AppendBusStop;

    [FieldOffset(8)]
    public SpawnBusCmd SpawnBus;

    [FieldOffset(8)]
    public SelectVehicleCmd SelectVehicle;

    [FieldOffset(8)]
    public SetTargetVehicleCountCmd SetTargetVehicleCount;

    [FieldOffset(8)]
    public SetNodeControlCmd SetNodeControl;

    // Фабрики называются Make*: имя Command.BuildRoad занято полем-payload (core-api §4.1).
    public static Command MakeBuildRoad(in BuildRoadCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.BuildRoad;
        c.Tag = tag;
        c.BuildRoad = payload;
        return c;
    }

    public static Command MakeSpawnTrip(in SpawnTripCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SpawnTrip;
        c.Tag = tag;
        c.SpawnTrip = payload;
        return c;
    }

    public static Command MakeSetSignalPlan(in SetSignalPlanCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetSignalPlan;
        c.Tag = tag;
        c.SetSignalPlan = payload;
        return c;
    }

    public static Command MakeCreateBusRoute(in CreateBusRouteCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.CreateBusRoute;
        c.Tag = tag;
        c.CreateBusRoute = payload;
        return c;
    }

    public static Command MakeAppendBusStop(in AppendBusStopCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.AppendBusStop;
        c.Tag = tag;
        c.AppendBusStop = payload;
        return c;
    }

    public static Command MakeSpawnBus(int route, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SpawnBus;
        c.Tag = tag;
        c.SpawnBus = new SpawnBusCmd { Route = route };
        return c;
    }

    public static Command MakeSelectVehicle(VehicleHandle h, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SelectVehicle;
        c.Tag = tag;
        c.SelectVehicle = new SelectVehicleCmd { Index = h.Index, Generation = h.Generation };
        return c;
    }

    public static Command MakeClearSelection(uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.ClearSelection;
        c.Tag = tag;
        return c;
    }

    public static Command MakeSetTargetVehicleCount(int count, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetTargetVehicleCount;
        c.Tag = tag;
        c.SetTargetVehicleCount = new SetTargetVehicleCountCmd { Count = count };
        return c;
    }

    public static Command MakeSetNodeControl(int node, NodeControl control, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetNodeControl;
        c.Tag = tag;
        c.SetNodeControl = new SetNodeControlCmd { Node = node, Control = control };
        return c;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BuildRoadCmd
{
    public float Ax, Az, Bx, Bz;      // концы оси, м; концы прилипают к узлу в радиусе SnapRadius (5 м)
    public float SpeedLimit;          // м/с
    public byte LanesAB, LanesBA;     // полос A→B и B→A (0 допустимо для одностороннего)
    public RoadKind Kind;             // Street = 0, Arterial = 1
    public byte Flags;                // бит0: не создавать connector'ы автоматически (тесты)
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SpawnTripCmd
{
    public int FromZone;              // индекс зоны или -1, если задан FromLane
    public int ToZone;                // индекс зоны (обязателен)
    public int FromLane;              // индекс полосы или -1
    public float FromS;               // м вдоль FromLane
    public int VehicleType;           // индекс в ScenarioDesc.VehicleTypes
    public int Count;                 // сколько машин поставить в очередь поездок (1..N)
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct SetSignalPlanCmd
{
    public int Node;
    public SignalMode Mode;
    public ushort OffsetTicks;        // сдвиг цикла
    public byte PhaseCount;           // 1..8
    public byte Reserved;
    public ushort Green0, Green1, Green2, Green3, Green4, Green5, Green6, Green7; // зелёный по фазам, тики
    public ushort YellowTicks, AllRedTicks;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CreateBusRouteCmd
{
    public int HeadwayTicks;
    public int VehicleType;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AppendBusStopCmd
{
    public int Route;
    public int Lane;
    public float S;
    public BusStopKind Kind;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SpawnBusCmd
{
    public int Route;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SelectVehicleCmd
{
    public int Index;
    public int Generation;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetTargetVehicleCountCmd
{
    public int Count;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetNodeControlCmd
{
    public int Node;
    public NodeControl Control;
}
