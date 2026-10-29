using Sim.Core.Network;

namespace Sim.Core.Api;

// Плоские данные, уже распарсенные хостом из JSON (docs/design/scenario-format.md).
// Строк нет: имена типов ТС/зон хост превращает в индексы. Секунды -> тики, км/ч -> м/с делает загрузчик.
// Массивы null трактуются как пустые.

public struct ScenarioDesc
{
    public ulong Seed;
    public int DurationTicks;              // информативно для хоста; ядро само не останавливается
    public SimCapacities Capacities;
    public RoutingDesc Routing;            // иерархическая маршрутизация (routing.md §9); нули = дефолты, Levels 0 = оверлей выключен
    public CityGenDesc City;               // City.Enabled == false -> сеть берётся из Network
    public NetworkDesc Network;            // явная сеть (тесты, ручные карты); может быть пустой
    public DemandDesc Demand;
    public TransitDesc Transit;
    public RulesDesc Rules;
    public LodDesc Lod;                    // уровни детализации (ADR-0013, sim-lod.md §4); Mode = Off - LOD нет, поведение прежнее
    public PedDesc Peds;                   // пешеходы и жители (ADR-0020, pedestrians.md); Enabled = false (по умолчанию) - ничего не выделяется и не меняется
    public ToolsDesc Tools;                // инструменты игрока: стоимости и границы мира (player-tools.md §11); нули - бесплатно, границы по умолчанию
    public CityDesc Town;                  // режим жителей (ADR-0011, zoning-demand.md §16): читается только при Demand.Model = Residents; иначе нули и ничего не выделяется
    public ParkingDesc Parking;            // парковка как ресурс (ADR-0026, parking.md §14): читается только при Demand.Model = Residents и Parking.Enabled; нулевая структура = режима нет
    public VehicleTypeDesc[] VehicleTypes; // индекс = VehicleType; >= 1 элемент
    public float WorldGridOriginX, WorldGridOriginZ;   // начало мировой сетки 8 м (ADR-0030, road-profiles.md §4.1), м; по умолчанию 0. Хранится в сейве (PARM), в хэш не входит: поведение задаст RP5/RP9
    public RoadProfilesDesc RoadProfiles;  // каталог профилей дорог (ADR-0030, road-profiles.md); Profiles = null - режим legacy: прежняя геометрия, хэши и сейвы побитово прежние
    public ProgressionDesc Progression;    // вехи и разблокировки (ADR-0034, progression.md §4.1): только при Demand.Model = Residents; Enabled = false - всё открыто, хэш и сейв побитово прежние
}

public struct SimCapacities
{
    public int MaxVehicles;                // машины + автобусы
    public int MaxNodes, MaxSegments, MaxLanes;
    public int MaxLanePoints;              // суммарно точек полилиний всех полос
    public int MaxZones, MaxBusRoutes, MaxBusStops;
    public int MaxBusStopsPerRoute;        // по умолчанию 32
    public int MaxRoutes;                  // слоты маршрутов (машины + поездки с построенным маршрутом)
    public int MaxPlanSteps;               // арена планов маршрутов (int, 4 Б), routing.md §9.5; окна: MaxRoutes * routing.window_steps
    public int MaxPendingTrips;            // очередь ожидающих спавна поездок
    public int CommandQueueCapacity;       // по умолчанию 1024
    public int EventCapacity;              // по умолчанию 4096 за Step
    public int MaxSignalizedNodes;
    public int MaxConnectors;              // connector-полос всего; по умолчанию MaxNodes * 16
    public int MaxConflicts;               // записей ConflictEntry; по умолчанию MaxConnectors * 6
    public int MaxPeds;                    // только при Peds.Enabled (pedestrians.md §12): >= Peds.Count * 1.3
    public int MaxResidents;               // только при Peds.Enabled: >= Peds.Residents
    public int MaxPedPathSteps;            // только при Peds.Enabled: шагов пути пешехода (дефолт загрузчика 64)
    public int MaxLots;                    // только при Demand.Model = Residents (zoning-demand.md §2, §16): участков (JSON max_lots)
    public int MaxBuildings;               // зданий (max_buildings)
    public int MaxCityResidents;           // жителей города (JSON max_city_residents: ключ max_residents занят заготовками peds)
    public int SkimObsCapacity;            // записей накопителя наблюдений скимов (skim_obs_capacity)
    public int MaxParking;                 // только при Parking.Enabled (parking.md §14, capacities.max_parking): объектов стоянки; 0 = MaxLots + MaxBuildings + 64 (ParkingStore.CapacityFor)
}

/// <summary>
/// Параметры иерархической маршрутизации (routing.md §9.9, scenario-format.md §2.1 "routing"). Нули в полях, кроме
/// <see cref="Levels"/>, заменяются дефолтами в <c>Create</c>; <c>Levels = 0</c> - оверлей не строится (тестовый режим).
/// </summary>
public struct RoutingDesc
{
    public int Levels;                     // 0..4; дефолт загрузчика 1
    public int Fanout;                     // ячеек уровня ℓ по стороне ячейки ℓ+1; дефолт 4
    public float CellSize;                 // сторона ячейки уровня 1, м; дефолт 1000
    public int WindowSteps;                // слот окна маршрута, шагов 8..32; дефолт 16
    public int PathfindBudgetPerTick;      // запросов на тик (0 = без ограничения)
    public int WorkBudgetPerTick;          // релаксаций запросов на тик; дефолт 1500 * PathfindBudgetPerTick (0 = без ограничения)
    public int QueryWorkCap;               // предел релаксаций одного запроса (query_work_cap); 0 = 4 * DirSegCapacity (routing.md 9.4)
    public int CustomizeBudgetPerTick;     // релаксаций волны кастомизации на тик; дефолт 10000
    public float CustomizeEps;             // порог изменения веса для «грязной» ячейки; дефолт 0.05
    public int CostRecalcTicks;            // период пересчёта весов по загрузке, тиков; дефолт 600 (30 с)
    public float TimedDrift;               // порог дрейфа остатка стоимости; дефолт 0.15
    public int TimedDriftAbsTicks;         // абсолютная добавка к порогу, тики; дефолт 400 (20 с)
}

public enum VehicleCategory : int
{
    Car = 0,
    Bus = 1,
}

public struct VehicleTypeDesc
{
    public VehicleCategory Category;
    public float Length, Width;            // м
    public float MaxSpeed;                 // м/с, физический предел
    public float SpeedFactor;              // желаемая v0 = min(MaxSpeed, SpeedFactor * limit)
    public float Accel;                    // IDM a, м/с²
    public float ComfortDecel;             // IDM b, м/с²
    public float HeadwayTime;              // IDM T, с
    public float MinGap;                   // IDM s0, м
    public float Delta;                    // IDM δ
    public float Politeness;               // MOBIL p
    public float LaneChangeThreshold;      // MOBIL Δa_th, м/с²
    public float SafeDecel;                // MOBIL b_safe, м/с²
    public float RightBias;                // MOBIL Δa_bias, м/с²
    public int LaneChangeDurationTicks;
}

public struct RulesDesc
{
    public float MinPhysicalGap;           // м (0.5)
    public int StuckThresholdTicks;        // 60 с -> 1200
    public float StuckSpeedThreshold;      // м/с (0.1)
    public float GridlockStuckShare;       // доля stuck среди живых (0.05)
    public int GridlockMinStuck;           // абсолютный минимум (10)
    public int GridlockHoldTicks;          // 1200
    public int MobilEveryTicks;            // 10
    public int DefaultYellowTicks, DefaultAllRedTicks; // [шаг 3]
    public float YieldGapTime;             // с [шаг 3]
    public int MaxYieldWaitTicks;          // инверсия приоритета у ворот после такого ожидания, 20 с -> 400
    // 2.5 (ADR-0018, intersections.md §6.5): дефолты Adaptive, тики; 0 -> значение по умолчанию (WorldBuilder.WithDefaults)
    public int SignalMinGreenTicks;        // 7 с -> 140
    public int SignalMaxGreenTicks;        // 24 с -> 480
    public int SignalGapTicks;             // 2.5 с -> 50
    public int SignalArrowTriggerTicks;    // 4 с -> 80
    public int SignalArrowMinTicks;        // 3 с -> 60
    public int SignalArrowMaxTicks;        // 6 с -> 120
    public int SignalArrowGapTicks;        // 2 с -> 40
}

// ---- city (scenario-format.md §2.1 "city") ----

public enum CitySignals : int
{
    ArterialXArterial = 0,
    AllArterial = 1,
    None = 2,
}

public enum MinorControl : int
{
    Yield = 0,
    Stop = 1,
    RightHand = 2,
}

/// <summary>Режим светофоров, который генератор ставит узлам (2.5, ADR-0018); дефолт сценария - <see cref="Adaptive"/>.</summary>
public enum CitySignalMode : int
{
    Fixed = 0,
    Adaptive = 1,
}

/// <summary>Офсеты светофоров генератора (2.5, ADR-0018, intersections.md 6.7): формула этапа 1, чередующаяся волна или без офсетов.</summary>
public enum CitySignalCoordination : int
{
    Legacy = 0,
    Alternate = 1,
    None = 2,
}

public struct CityGenDesc
{
    public bool Enabled;
    public float SizeX, SizeZ;             // size_m, м
    public float Scale;                    // множитель к размеру
    public float BlockSize;                // block_m, м
    public int ArterialEvery;
    public float ArterialSpeed, StreetSpeed; // м/с
    public float LaneWidth;                // м
    public int IrregularNodes;
    public float IrregularJitter;          // м
    public float NodeJitter;               // фоновый джиттер всех внутренних узлов, м (дефолт 0)
    public float RemovedStreetShare;       // 0..0.5
    public int ConnectorSamples;
    public float CornerRadius;             // city.corner_radius_m, м (ADR-0028): 0 = дефолт 6 м, < 0 = выключено (фиксированный отступ 7 м, как до ADR-0028)
    public CitySignals Signals;
    public MinorControl MinorControl;
    public CitySignalMode SignalMode;                  // city.signal_mode; 0 = fixed, 1 = adaptive (дефолт загрузчика сценариев)
    public CitySignalCoordination SignalCoordination;  // city.signal_coordination; 0 = legacy (формула этапа 1, дефолт), 1 = alternate, 2 = none
    public float ProgressionSpeedFactor;               // city.progression_speed_factor: v_p = ArterialSpeed * factor (0 = 1.0), калибровка волны alternate
    public int StreetProfile;                          // city.profiles.street: индекс профиля + 1 (0 - нет; в режиме профилей обязателен); число полос - из профиля
    public int ArterialProfile;                        // city.profiles.arterial: индекс профиля + 1
    public bool SnapNodes;                             // city.snap_nodes (road-profiles.md §5): узлы на мировую сетку module_m, шаг решётки = block_m точно; дефолт загрузчика: true в режиме профилей, иначе false (прежнее поведение)
    public LandUseDesc LandUse;                        // city.landuse (visual-style.md §8, ADR-0027): парки/вода; нулевая структура = выключено
}

// ---- network (явная сеть; ссылки на узлы - индексы в Nodes) ----

public struct NodeDesc
{
    public float X, Z;
    public NodeControl Control;
}

public struct SegmentDesc
{
    public int A, B;                       // индексы в NetworkDesc.Nodes
    public int LanesAB, LanesBA;
    public float SpeedLimit;               // м/с
    public RoadKind Kind;
    public int Profile;                    // индекс профиля + 1 (0 - нет); в режиме профилей обязателен, LanesAB/LanesBA/Kind тогда выводятся из профиля (с учётом Mirrored) и обязаны совпасть
    public bool Mirrored;                  // сегмент A→B построен против направления рисования профиля (SegmentFlags.Mirrored)
}

public struct NetworkDesc
{
    public NodeDesc[] Nodes;
    public SegmentDesc[] Segments;
    public float CornerRadius;             // network.corner_radius_m, м (ADR-0028): 0 = дефолт 6 м, < 0 = выключено (фиксированный отступ 7 м)
}

// ---- demand (вкл. секцию "vehicles" JSON: целевое число машин, разгон, смесь типов) ----

public enum DemandModel : int
{
    Gravity = 0,
    Uniform = 1,
    Residents = 2,                         // ADR-0011: поездки от жителей города (zoning-demand.md); OD-генератор выключен, секции CityDesc обязательны
}

public struct ZoneDesc
{
    public float CenterX, CenterZ;
    public float HalfSize;
    public float Weight;
    public bool IsExternal;
}

public struct OdPairDesc
{
    public int FromZone, ToZone;
    public float Weight;
}

public struct DemandDesc
{
    public int TargetVehicles;             // vehicles.count (без автобусов)
    public int SpawnRampTicks;             // линейный разгон до TargetVehicles
    public float[] TypeMix;                // веса по индексу VehicleTypes; null -> всё тип 0
    public float ZoneSize;                 // zone_m, м
    public DemandModel Model;
    public float GravityExponent;
    public float WeightJitter;
    public float ExternalShare;
    public float MinTripLength;            // м
    public ZoneDesc[] Zones;               // необязательные явные зоны (иначе ZoneGenerator)
    public OdPairDesc[] OdPairs;           // необязательные явные пары вместо модели
}

// ---- transit ----

public enum StopKindMode : int
{
    Mixed = 0,
    Curb = 1,
    Bay = 2,
}

public struct BusStopDesc
{
    public int Lane;
    public float S;
    public BusStopKind Kind;
}

public struct BusRouteDesc
{
    public BusStopDesc[] Stops;
    public int HeadwayTicks;
}

public struct TransitDesc
{
    public int Routes;                     // сколько маршрутов сгенерировать
    public int HeadwayTicks;
    public int StopsPerRouteMin, StopsPerRouteMax;
    public float StopSpacing;              // м
    public StopKindMode StopKind;
    public int BusType;                    // индекс в VehicleTypes
    public int DwellBaseTicks;
    public float DwellPerPassengerTicks;
    public float PassengerRatePerMin;

    /// <summary>Удержание по интервалу (ADR-0008): доля планового интервала между отправлениями; 0 - выключено.</summary>
    public float HoldAlpha;

    /// <summary>Верхний предел удержания, тиков (сверх обычной стоянки).</summary>
    public int HoldMaxTicks;

    /// <summary>Контрольные остановки: каждая N-я остановка маршрута (1 - все, конечная не удерживает).</summary>
    public int HoldEveryN;
    public BusRouteDesc[] RoutesExplicit;  // необязательные явные маршруты

    /// <summary>Модель пассажиров (ADR-0020); дефолт загрузчика: <see cref="PassengerModel.Agents"/> при включённых <c>peds</c>, иначе <see cref="PassengerModel.Counter"/>.</summary>
    public PassengerModel PassengerModel;

    /// <summary>Парный обратный маршрут к каждому (scenario-format.md §2.4, pedestrians.md §17 п. 3). P1: только поле; генератор его пока не читает.</summary>
    public bool Bidirectional;

    /// <summary>Модель кармана остановок <c>Bay</c> (ADR-0029, bus-bays.md); нулевая структура - <see cref="Api.BayModel.Curb"/> (прежнее поведение), дефолт загрузчика сценариев - <see cref="Api.BayModel.Virtual"/>.</summary>
    public BayModel BayModel;

    /// <summary>Ожидание автобуса в кармане до уступки ведомых, тики (BB2; в BB1 только хранится). Дефолт загрузчика 3 с = 60.</summary>
    public int BayYieldTicks;
}

/// <summary>
/// Модель кармана остановок <c>Bay</c> (ADR-0029). <c>Curb</c> - как раньше: автобус стоит на полосе и блокирует её. <c>Virtual</c> - автобус снимается со списка полосы на время стоянки
/// и выезжает по gap acceptance. <c>Lane</c> зарезервировано под настоящую полосу-карман (<see cref="LaneKind.BusBay"/>, вариант B): пока ведёт себя как <c>Curb</c>.
/// </summary>
public enum BayModel : int
{
    Curb = 0,
    Virtual = 1,
    Lane = 2,
}

/// <summary>Модель пассажиров автобусов (ADR-0020): счётчик ADR-0008 или пассажиры-пешеходы.</summary>
public enum PassengerModel : int
{
    Counter = 0,
    Agents = 1,
}

/// <summary>Переходы через артерии вне светофоров (pedestrians.md §2.1, §17 п. 2).</summary>
public enum ArterialCrossings : int
{
    SignalOnly = 0,
    All = 1,
}

/// <summary>
/// Секция сценария <c>peds</c> (scenario-format.md §2.4, pedestrians.md §12). Единицы ядра уже целые: мм, мм/тик (при 20 Гц), тики.
/// Нулевая структура = выключено. Нули в остальных полях (desc, собранный вручную) заменяются дефолтами спеки
/// (<c>Peds.PedNetwork.WithDefaults</c>). P1 использует только геометрию и политику переходов; поведение агентов - P2+.
/// </summary>
public struct PedDesc
{
    public bool Enabled;
    public int Count;                      // цель активных пешеходов
    public int Residents;                  // жителей-источников; дефолт загрузчика 2 * Count
    public int SpawnRampTicks;             // дефолт 120 с
    public int SpeedMinMmPerTick;          // round(км/ч / 3.6 * 50); дефолт 4.0 км/ч
    public int SpeedMaxMmPerTick;          // дефолт 5.4 км/ч
    public int ActivityMinTicks;           // дефолт 600 с
    public int ActivityMaxTicks;           // дефолт 1800 с
    public int WalkOnlyMm;                 // дефолт 800 м
    public int MaxWalkToStopMm;            // дефолт 500 м
    public int MaxWalkMm;                  // дефолт 1500 м
    public int SidewalkWidthMm;            // дефолт 3000
    public int CrosswalkWidthMm;           // дефолт 3000
    public ArterialCrossings ArterialCrossings;   // дефолт SignalOnly
    public bool MidblockAtStops;           // дефолт true (загрузчик); для desc, собранного вручную, задаётся явно
    public int WalkTicks;                  // «Walk» пешеходной фазы; дефолт 5 с
    public int ClearSpeedMmPerTick;        // дефолт 4.3 км/ч
    public int MaxCloseTicks;              // дефолт 10 с
    public int CarWindowTicks;             // дефолт 5 с
    public int PathfindBudgetPerTick;      // дефолт 8
    public int WorkBudgetPerTick;          // дефолт 8000
    public int StuckCrossTicks;            // дефолт 120 с
    public int StuckStopTicks;             // дефолт max(3 * transit.headway_s, 900) с
    public PedSource Source;               // peds.source (zoning-demand.md §4.1): Preform - заготовка pedestrians.md §3 (дефолт), City - жители города (Z2); City требует Demand.Model = Residents
}
