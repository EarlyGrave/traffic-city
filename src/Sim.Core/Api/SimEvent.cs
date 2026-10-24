using System.Runtime.InteropServices;

namespace Sim.Core.Api;

public enum SimEventKind : int
{
    CommandApplied = 1,
    CommandRejected = 2,
    NodeCreated = 3,
    SegmentCreated = 4,
    BusRouteCreated = 5,
    VehicleSpawned = 10,
    TripCompleted = 11,
    VehicleStuck = 12,
    VehicleUnstuck = 13,
    GridlockOn = 20,
    GridlockOff = 21,
    CapacityExhausted = 30,      // A = (int)CapacityKind, B = сколько отброшено за тик
    EventsOverflow = 31,         // всегда последний элемент, если события не влезли
}

/// <summary>Причина отказа команды: <c>SimEvent.A</c> для <see cref="SimEventKind.CommandRejected"/>.</summary>
public enum RejectReason : int
{
    None = 0,
    Capacity = 1,
    InvalidHandle = 2,
    InvalidArgument = 3,
    UnknownKind = 4,
    NotSupported = 5,
}

/// <summary>Какая ёмкость исчерпана: <c>SimEvent.A</c> для <see cref="SimEventKind.CapacityExhausted"/>.</summary>
public enum CapacityKind : int
{
    Vehicles = 1,
    PendingTrips = 2,
    Nodes = 3,
    Segments = 4,
    Lanes = 5,
    LanePoints = 6,
    Routes = 7,
    RouteSteps = 8,
    CommandQueue = 9,
    Events = 10,
    Zones = 11,
    BusRoutes = 12,
    BusStops = 13,
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
public struct SimEvent
{
    public SimEventKind Kind;
    public uint Tag;             // эхо Command.Tag или 0
    public long Tick;
    public int A, B;             // зависят от Kind: индексы/поколения/коды
}
