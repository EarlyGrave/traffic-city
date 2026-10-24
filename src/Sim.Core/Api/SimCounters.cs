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
    public long PathfindQueries, PathfindCacheHits, RouteDeferred;
    public long PathfindExpandedNodes;
    public long ConnectorEntries;         // сумма проездов через connector'ы (= сумма NodeThroughput)
    public long CommandsApplied, CommandsRejected, CommandsDropped, EventsDropped;
    public long SpawnDeferredTicks, InvalidHandleOps;
    public int SignalizedNodes, NodeCount, SegmentCount, LaneCount, ZoneCount; // текущее
    public long BusDispatched, BusStopArrivals;
    public long BusHeadwayDeviationTicksTotal;   // [шаг 4]
    public long BusPassengersBoarded;
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
    public float PathfindCacheHitRate;
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
