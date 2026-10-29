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
    GridlockOn = 20,             // A = застрявших, B = активных; Place = сегмент с наибольшим числом застрявших (EV1) или нет места
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
    DrainStalled = 46,           // A = id операции, B = Remaining (раз за период; цель остаётся Closing); Place = сегмент-цель (EV1)
    ZoneAccessLost = 47,         // A = индекс зоны, B = индекс закрытого сегмента (EV1); Place = зона (центр)
    BusStopCreated = 48,         // A = индекс остановки, B = поколение
    BusStopRemoved = 49,         // A = индекс остановки, B = поколение
    SegmentRemapped = 50,        // A = старый индекс, B = новый индекс (поколение нового - из handle'ов сегмента)
    NodeRemapped = 51,           // то же для узла
    BusStopRemapped = 52,        // то же для остановки
    BusRouteRemapped = 53,       // то же для маршрута
    ZoneLotsLost = 54,           // A = индекс снесённого сегмента, B = число потерянных участков (режим жителей; zoning-demand.md §14)

    // Экономика города (4.2, city-economy.md §10, E1): номера закреплены; BudgetNegative/LimitReached/Recovered выдаёт E2 на границе суток.
    BudgetNegative = 55,         // A = номер суток, B = City (насыщенно в int): первый день City < 0
    BudgetLimitReached = 56,     // A = номер суток, B = City (насыщенно в int): казна ушла за кредитный лимит
    BudgetRecovered = 57,        // A = номер суток, B = City (насыщенно в int): казна вернулась в лимит
    PolicyApplied = 58,          // A = номер суток, B = Fare новой политики: ожидающая политика стала действующей

    // Прогрессия (progression.md §4.5, ADR-0034): номера закреплены в PG1; выдают PG3 (59-60), PG5 (61), PG4 (62).
    MilestoneReached = 59,       // A = индекс вехи, B = население
    MilestoneGated = 60,         // A = индекс вехи, B = маска невыполненных ворот (бит 0 - G0, бит 1 - G1): населения хватает, ворот нет (раз на веху)
    BudgetWarning = 61,          // A = уровень предупреждения (1/2), B = DaysToLimit
    Complaint = 62,              // A = (int)ComplaintKind, B = порядковый номер записи в кольце жалоб (CopyComplaints)

    // События с местом (EV1, core-api.md §5.7): место - в SimEvent.Place (EventPlace). Выдаются с ограничением частоты на место.
    JamAt = 63,                  // A = застрявших машин на сегменте, B = всего застрявших в сети; Place = сегмент (середина оси)
    BusLineProblem = 64,         // A = (int)BusLineIssue, B = величина (см. BusLineIssue), C = индекс остановки или -1; Place = маршрут, X/Z - точка проблемы
    ParkingFull = 65,            // A = отказов в месте за окно, B = индекс участка цели; Place = здание цели (центр участка)

    // Автобусные инструменты (T9, player-tools.md §8.7). BusRouteCreated (5): A = индекс маршрута, B = поколение; BusStopCreated/Removed (48/49) - для остановок.
    BusRouteRemoved = 66,        // A = индекс маршрута, B = поколение снятого (автобусы линии доезжают до своей ближайшей остановки и завершают рейс)
}

/// <summary>Проблема автобусной линии: <c>SimEvent.A</c> для <see cref="SimEventKind.BusLineProblem"/>.</summary>
public enum BusLineIssue : int
{
    None = 0,
    NoBuses = 1,                 // на линии нет автобусов и выпуск просрочен: B = тиков просрочки, X/Z - первая остановка
    BusesStuck = 2,              // автобусы линии стоят (stuck, не на остановке): B = сколько, X/Z - первый по индексу из них
    Overloaded = 3,              // на остановке линии копится очередь: B = ждущих, C = остановка, X/Z - её точка
}

/// <summary>
/// Место события (EV1; общий формат для событий и жалоб PG4, progression.md §6.2): вид и ссылка на объект плюс точка мира, которую считает ядро
/// (узел - ось узла, сегмент - середина осевой полилинии, остановка - точка на полосе, зона - центр, здание - центр участка; у маршрута - точка проблемы).
/// <c>ObjKind == None</c> - места нет (X/Z = 0). У зоны <c>Generation = 0</c>. 20 Б.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 20)]
public struct EventPlace
{
    public EntityKind ObjKind;
    public byte Reserved0;
    public ushort Reserved1;
    public int Index, Generation;
    public float X, Z;

    public EventPlace(EntityKind kind, int index, int generation, float x, float z)
    {
        ObjKind = kind;
        Reserved0 = 0;
        Reserved1 = 0;
        Index = index;
        Generation = generation;
        X = x;
        Z = z;
    }

    public readonly bool HasPlace => ObjKind != EntityKind.None;
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
    InsufficientFunds = 12,     // экономика (4.2): City - цена < -кредитный лимит (применяет E4)
    Unreachable = 13,           // нет пути от соседней остановки
    ZoneRule = 14,              // резерв ADR-0011
    OutOfBounds = 15,           // вне tools.world_bounds
    BudgetRule = 16,            // экономика (4.2): ставки политики за пределами, выключение бюджета, политика без бюджета
    Locked = 17,                // прогрессия (progression.md §4.3): признак или профиль ещё не открыт вехой; в превью Value = веха, которая откроет (-1 - не откроется)

    // Рельеф (terrain.md §4, TR3).
    TooSteep = 18,              // уклон оси > max_grade профиля и подгонка высоты нового конца не помогла; в превью Value = уклон, ‰
    NeedsBridge = 19,           // насыпь выше max_fill, мокрый отсчёт или узел в воде без флага Bridge; в превью Value: -1 мост ниже воды + просвет, -2 мост длиннее max_bridge_m
    TooDeep = 20,               // ось ниже земли больше max_cut
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

/// <summary>
/// Событие ядра (48 Б; до EV1 было 24 Б без <see cref="Place"/> и <see cref="C"/>). <see cref="Place"/> заполнен у событий с местом
/// (<see cref="SimEventKind.JamAt"/>, <see cref="SimEventKind.BusLineProblem"/>, <see cref="SimEventKind.ParkingFull"/>, <see cref="SimEventKind.GridlockOn"/>,
/// <see cref="SimEventKind.DrainStalled"/>, <see cref="SimEventKind.ZoneAccessLost"/>), у остальных - нули.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct SimEvent
{
    public SimEventKind Kind;
    public uint Tag;             // эхо Command.Tag или 0
    public long Tick;
    public int A, B;             // зависят от Kind: индексы/поколения/коды
    public EventPlace Place;     // место события (EV1) или нули
    public int C;                // третий параметр (зависит от Kind) или 0
}
