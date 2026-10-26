using System.Runtime.InteropServices;

namespace Sim.Core.Api;

public enum CreateStatus : int
{
    Ok = 0,
    InvalidCapacity = 1,        // ёмкость <= 0 или > лимита реализации
    NetworkTooLarge = 2,        // генератор/явная сеть не влезли в ёмкости
    InvalidVehicleType = 3,
    InvalidNetwork = 4,         // ссылка на несуществующий узел, нулевая длина сегмента и т.п.
    InvalidTransit = 5,
    InvalidDemand = 6,
    NotSupported = 7,           // запрошено то, чего ядро ещё не умеет (например, генератор города)
    InvalidPeds = 8,            // секция peds: ёмкости max_peds/max_residents меньше требуемых, ширины или скорости вне пределов
}

/// <summary>Монотонные счётчики ядра (core-api §11), кроме полей, помеченных «текущее».</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SimCounters
{
    public long Ticks;
    public long VehiclesSpawned, TripsCompleted, TripsDropped;
    public long TripTimeTicksTotal;       // сумма длительностей завершённых поездок
    public long DistanceTotalMm;          // пройденный путь всеми, мм (round(v*dt*1000))
    public long VehicleTicksTotal;        // сумма живых машин по тикам
    public long MovingVehicleTicksTotal;  // то же, только v > StuckSpeedThreshold
    public int ActiveVehicles;            // текущее
    public int StuckVehicles;             // текущее
    public long StuckEventsTotal;         // переходов в stuck
    public long StuckTicksTotal;          // сумма тиков в состоянии stuck
    public int GridlockFlag;              // текущее: 0/1
    public long GridlockTicksTotal;
    public long LaneChanges, LaneChangesAborted;
    public long HardClamps;               // срабатываний клэмпа I6
    public long PathfindQueries, RouteDeferred;      // запросов Query; сумма по тикам ждущих поездок без маршрута
    public long PathfindExpandedNodes, PathfindWork; // раскрытий и релаксаций (дуга графа / клетка матрицы) запросов (routing.md 9.10)
    public long RouteFailed, Reroutes, PlanCompactions; // маршрутизация (routing.md 8, 9.10)
    public long PlanUnpacks, UnpackWork;  // развёртываний дуг плана и их работа
    public long WindowRefills, RefillLate; // дозаполнений окон; машин, дошедших до конца окна без дозаполнения (цель 0)
    public long TimedChecks, TimedTriggered; // проверок дрейфа плановой перемаршрутизации / из них запросов
    public long CostRecalcs, TimedReroutes; // вес по загрузке и перемаршрутизация по времени (ADR-0006)
    public long CustomizeRows, CustomizeWork; // волны кастомизации оверлея: строки и релаксации (routing.md 9.10)
    public long CustomizeLagTicks;        // тики ожидания нового пересчёта весов из-за незаконченной волны
    public long OverlayRebuilds;          // полные перестройки оверлея после BuildRoad
    public int OverlayDisabled;           // текущее: 1 - оверлей выключен (не влезла перестройка)
    public long ConnectorEntries;         // сумма проездов через connector'ы (= сумма NodeThroughput)
    public long CommandsApplied, CommandsRejected, CommandsDropped, EventsDropped;
    public long SpawnDeferredTicks, InvalidHandleOps;
    public int SignalizedNodes, NodeCount, SegmentCount, LaneCount, ZoneCount; // текущее
    public long BusDispatched, BusStopArrivals;
    public long BusHeadwayDeviationTicksTotal;   // Σ |фактический интервал между автобусами на остановке - плановый|, тики
    public long BusHeadwaySamples;               // число таких измерений (прибытия, у которых на остановке был предыдущий автобус)
    public long BusPassengersBoarded;
    // --- перекрёстки (intersections.md §10) ---
    public long Grants;                   // выданных допусков
    public long GateDeniedExit;           // отказов «выезд занят» (don't-block-the-box)
    public long PriorityInversions;       // допусков по инверсии приоритета (долгое ожидание)
    public long BoxWaitTicks;             // Σ тиков, когда допущенная машина стоит на connector'е (v < StuckSpeedThreshold)
    public long GateWaitTicksTotal;       // Σ WaitTicks на момент допуска (среднее ожидание у ворот)
    // --- светофоры 2.5 (intersections.md §10, ADR-0018); вне хэша, считаются в однопоточной фазе 4. По манёврам - SignalMetrics ---
    public long SignalLeftWaitMax;        // max WaitTicks при допуске левого/разворота на светофоре (тики); окно - Simulation.ResetSignalWindow
    public long ArterialSignalGrants;     // допусков на светофоре с Arterial-полосы
    public long ArterialSignalStops;      // из них машин, останавливавшихся на подходе (GateState.StoppedBeforeSignal)
    public long StarveDenials;            // отказов 4b-bis (GateDeny.Yield по голоданию)
    public long ArrowPhases;              // заготовка S2: включений подфазы «стрелка»
    public long SpillOuts;                // заготовка S3: досрочных концов зелёного из-за забитого выезда
    public long ExitClaims;               // заготовка S4: поставленных заявок на выезд
    public long ExitClaimDenials;         // заготовка S4: отказов младшим по заявке
    public long GrantsRevoked;            // 2.6r R1: снятий допуска на красном (§5.4 п. 6; вне хэша)
    // --- уровни детализации (ADR-0013, sim-lod.md §4, §8); вне хэша, заполняются с L2/L3 ---
    public int MicroVehicles, MesoVehicles;                // текущее
    public long MicroVehicleTicks, MesoVehicleTicks, MicroDistanceMm, MesoDistanceMm;
    public long LodEvals, LodCellFlips, LodBudgetSkips, MesoClamps, MesoLaneChanges;
    // --- метрики качества трафика (2.6c, Metrics/QualityMetrics.cs); вне хэша, выборка раз в 20 тиков ---
    public int StillMaxTicks;             // max непрерывного StillTicks; окно - Simulation.ResetSignalWindow
    public int StillConcurrent60Max;      // max одновременно стоящих >= 60 с; окно - Simulation.ResetSignalWindow
    public long StillEpisodes60, StillEpisodes120;   // эпизоды непрерывного стояния >= 60 с / >= 120 с
    public long LaneShareSamples;         // выборок (направление многополосного сегмента, >= 4 машин)
    public long LaneShareMaxPermilleSum;  // Σ доли самой загруженной полосы направления, ‰
    public long LaneShareH0, LaneShareH1, LaneShareH2, LaneShareH3, LaneShareH4, LaneShareH5, LaneShareH6, LaneShareH7, LaneShareH8, LaneShareH9; // гистограмма той же доли, корзины по 10%
    // --- пешеходы и пассажиры (ADR-0020, pedestrians.md §9); заморожены в P2. Состояние: в хэше и сейве (CNTR); при выключенных peds все нули и в хэш не входят ---
    public int PedActive;                 // текущее: живых пешеходов
    public long PedActiveTicksTotal;      // Σ активных пешеходов по тикам (ped_active_mean)
    public long PedSpawned;               // запущенных поездок
    public long PedTripsCompleted;        // завершённых поездок
    public long PedTripTicksTotal;        // Σ длительностей завершённых поездок
    public long PedCrossWaitTicksTotal;   // Σ ожидания у переходов на момент выпуска (P3); гистограмма 1 с - блок XING
    public long PedCrossReleases;         // выпущенных с ожидания у переходов (P3)
    public long PedStopWaitTicksTotal;    // Σ ожидания на остановках на момент посадки (P4)
    public long PedStopBoardings;         // севших на автобус (P4)
    public long PedStuckEvents;           // ожидание >= stuck_cross_s / stuck_stop_s (цель 0)
    public long CarCrossDelayTicks;       // Σ тиков отказа DenyPed у кандидатов + тиков стоянки с ClampFlags.Cross (P3)
    public long CrossingClosedTicks;      // Σ по переходам с выпусками: тики «не Free» (P3)
    public long CrossingObservedTicks;    // Σ по таким переходам: тики наблюдения (P3)
    public long BusPassengersAlighted;    // вышло из автобусов (P4)
    public long BusPaxOnBoardTicks;       // Σ пассажиров на борту по тикам (P4)
    public long PedPathQueries;           // запросов путей
    public long PedPathWork;              // релаксаций A* пешеходов
    public long PedNoPath;                // запросов без пути (норма 0)
    public long PedModeFallback;          // автобусная нога заменена пешей (P4)
    public long GrantsRevokedPed;         // снятий допуска из-за закрытия перехода (P3)
    public long PedAllocFailed;           // пул пешеходов исчерпан при запуске поездки
    public long PedNetResets;             // сбросов пешеходов при BuildRoad
}

/// <summary>Производные значения, считаются в GetStats за O(1) из <see cref="SimCounters"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SimStats
{
    public float AvgSpeedKmh;             // DistanceTotalMm / 1000 / (VehicleTicksTotal*Dt) * 3.6
    public float AvgTripTimeS;            // TripTimeTicksTotal*Dt / TripsCompleted
    public float TripsCompletedPerMin;    // TripsCompleted / (Ticks*Dt/60)
    public int StuckVehicles;
    public int GridlockFlag;              // 0/1 (в спеке bool; int ради blittable-раскладки)
    public float BusHeadwayDeviationS;    // BusHeadwayDeviationTicksTotal*Dt / BusStopArrivals
    public float HardClampsPerVehicleMin;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BusRouteStats
{
    public int ActiveBuses;
    public long Arrivals;
    public long HeadwayDeviationTicksTotal;
    public long LastDispatchTick;
}
