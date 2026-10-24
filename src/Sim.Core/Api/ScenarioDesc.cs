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
    public CityGenDesc City;               // City.Enabled == false -> сеть берётся из Network
    public NetworkDesc Network;            // явная сеть (тесты, ручные карты); может быть пустой
    public DemandDesc Demand;
    public TransitDesc Transit;
    public RulesDesc Rules;
    public VehicleTypeDesc[] VehicleTypes; // индекс = VehicleType; >= 1 элемент
}

public struct SimCapacities
{
    public int MaxVehicles;                // машины + автобусы
    public int MaxNodes, MaxSegments, MaxLanes;
    public int MaxLanePoints;              // суммарно точек полилиний всех полос
    public int MaxZones, MaxBusRoutes, MaxBusStops;
    public int MaxBusStopsPerRoute;        // по умолчанию 32
    public int MaxRoutes, MaxRouteSteps;   // кэш маршрутов: число маршрутов и суммарно шагов
    public int MaxPendingTrips;            // очередь ожидающих спавна поездок
    public int CommandQueueCapacity;       // по умолчанию 1024
    public int EventCapacity;              // по умолчанию 4096 за Step
    public int PathfindBudgetPerTick;      // запросов A* на тик [шаг 3]
    public int MaxSignalizedNodes;
    public int MaxConnectors;              // connector-полос всего; по умолчанию MaxNodes * 16
    public int MaxConflicts;               // записей ConflictEntry; по умолчанию MaxConnectors * 6
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
    public float RemovedStreetShare;       // 0..0.5
    public int ConnectorSamples;
    public CitySignals Signals;
    public MinorControl MinorControl;
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
}

public struct NetworkDesc
{
    public NodeDesc[] Nodes;
    public SegmentDesc[] Segments;
}

// ---- demand (вкл. секцию "vehicles" JSON: целевое число машин, разгон, смесь типов) ----

public enum DemandModel : int
{
    Gravity = 0,
    Uniform = 1,
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
    public BusRouteDesc[] RoutesExplicit;  // необязательные явные маршруты
}
