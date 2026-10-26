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
    BusStopArrived = 14,         // A = индекс маршрута, B = индекс остановки (глобальный)
    GridlockOn = 20,
    GridlockOff = 21,
    CapacityExhausted = 30,      // A = (int)CapacityKind, B = сколько отброшено за тик
    EventsOverflow = 31,         // всегда последний элемент, если события не влезли

    // Инструменты игрока (4.3, player-tools.md §7.2, §8.2). T1: номера закреплены, события выдаются с T3..T9.
    SegmentClosing = 40,         // A = индекс сегмента, B = поколение (началось закрытие/дренаж)
    SegmentRemoved = 41,         // A = индекс сегмента, B = поколение
    NodeRemoved = 42,            // A = индекс узла, B = поколение
    NodeRebuilt = 43,            // A = индекс узла
    SignalPlanReset = 44,        // A = индекс узла (план сброшен перестройкой)
    ToolOpFinished = 45,         // A = id операции (ToolOpView.Id), B = (int)CommandKind
    DrainStalled = 46,           // A = id операции, B = Remaining (раз за период; цель остаётся Closing)
    ZoneAccessLost = 47,         // A = индекс зоны
    BusStopCreated = 48,         // A = индекс остановки, B = поколение
    BusStopRemoved = 49,         // A = индекс остановки, B = поколение
    SegmentRemapped = 50,        // A = старый индекс, B = новый индекс (поколение нового - из handle'ов сегмента)
    NodeRemapped = 51,           // то же для узла
    BusStopRemapped = 52,        // то же для остановки
    BusRouteRemapped = 53,       // то же для маршрута
    ZoneLotsLost = 54,           // A = индекс снесённого сегмента, B = число потерянных участков (режим жителей; zoning-demand.md §14)
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

    // Инструменты игрока (player-tools.md §6). Коды 1..99 совпадают с IssueCode-ошибками превью.
    TooShort = 6,               // сегмент или половина разреза < tools.min_segment_m
    TooSharp = 7,               // угол рукавов < min_arm_angle_deg или радиус Безье < min_curve_radius_m
    Overlap = 8,                // ось пересекает сегмент не в узле или идёт параллельно ближе ширины проезжей части
    NodeLimit = 9,              // рукавов > 6, connector'ов > 64
    Busy = 10,                  // цель уже в операции (снос закрывающегося, апгрейд дренируемого)
    CreatesTrap = 11,           // односторонний сегмент в узел без выездов
    InsufficientFunds = 12,     // экономика (4.2): пока не возникает
    Unreachable = 13,           // нет пути от соседней остановки
    ZoneRule = 14,              // резерв ADR-0011
    OutOfBounds = 15,           // вне tools.world_bounds
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
