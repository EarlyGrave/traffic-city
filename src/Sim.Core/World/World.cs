using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core;

/// <summary>
/// Композиция всех хранилищ и состояния симуляции (core-api 13, 13.1). Создаётся только
/// <see cref="WorldBuilder"/>; все массивы выделены по <see cref="SimCapacities"/>, после этого аллокаций нет.
/// Фазы тика - <c>internal static class XxxPhase { public static void Run(World w) }</c>.
/// Namespace <c>Sim.Core</c> (а не <c>Sim.Core.World</c>): одноимённый namespace затенял бы тип во всех <c>Sim.Core.*</c>.
/// </summary>
internal sealed class World : IDisposable
{
    // ---- параметры (копии из ScenarioDesc) ----
    public readonly SimCapacities Caps;
    public readonly RulesDesc Rules;
    public readonly RoutingDesc Routing;       // с применёнными дефолтами (WorldBuilder.WithDefaults)
    public readonly ulong Seed;
    public readonly VehicleTypeDesc[] Types;
    public readonly DemandDesc Demand;
    public readonly TransitDesc TransitCfg;

    // ---- хранилища ----
    public readonly NetworkStore Net;
    public readonly NetworkBuilder Builder;
    public readonly VehicleStore Vehicles;
    public readonly LaneOccupancy Occupancy;
    public readonly PendingTrips Trips;
    public readonly RouteStore Routes;                 // планы и окна маршрутов (routing.md 9.5)
    public readonly RouteGraph Graph;                  // граф DirSeg и веса (routing.md 1, ADR-0006)
    public Router Router = null!;                     // запросы, окна, дозаполнение; создаётся в WorldBuilder после оверлея
    public readonly RingBuffer<RefillEntry> RefillQueue; // машины, которым пора дозаполнить окно маршрута (routing.md 9.5)
    public Overlay? Overlay;                           // оверлей ячеек (routing.md 9.2); null при routing.levels = 0; строится в Create
    public bool RecalcPending;                         // пересчёт весов по номеру тика наступил, но ждёт конца волны кастомизации
    public readonly RingBuffer<RerouteEntry> Rerouting; // машины, промахнувшиеся мимо полосы (routing.md 3)
    public readonly SpawnerData Spawn;
    public readonly ConnectorStore Connectors;   // connector'ы и конфликтные пары (intersections.md §3, §4); строится в WorldBuilder
    public readonly SignalStore Signals;
    public readonly GateScratch Gate;            // scratch фазы 4 (ворота): кандидаты, затронутые узлы/полосы, коды отказов

    // ---- результаты генератора города (заполняет WorldBuilder; для шагов 3 и 4) ----
    public CityGenResult CityInfo;
    public SignalPlanSeed[] SignalPlanSeeds = System.Array.Empty<SignalPlanSeed>();
    public GenTransit? Transit;

    // ---- PRNG: по подпотоку на подсистему (core-api 8) ----
    public Pcg32 RngCityGen;
    public Pcg32 RngZones;
    public Pcg32 RngSpawn;
    public Pcg32 RngBehaviour;
    public Pcg32 RngTransit;
    public Pcg32 RngRouting;

    // ---- время, команды, события, метрики ----
    public long Tick;
    public SimCounters Counters;
    public SimWork Work;                  // счётчики работы горячего пути (ADR-0016), вне хэша
    public PhaseProbe? PhaseProbe;        // зонд конца фазы (core-api §3); задаёт хост, ядро время не читает
    public BehaviourJob Behaviour = new(1);  // задание фазы 5 (ADR-0017); счётчики по воркерам
    public IntegrateJob Integrate = new(1);  // задание фазы 6 (ADR-0017); HardClamps по воркерам
    public SnapshotJob SnapshotJob = new(); // задание фазы 10 (ADR-0017)
    public WorkerPool? Pool;              // воркеры параллельных фаз; null = один поток (SimRuntimeOptions.WorkerThreads <= 1)
    public readonly RingBuffer<Command> Commands;
    public readonly SimEvent[] Events;
    public int EventCount;
    public bool EventsOverflowed;

    // ---- управляющее состояние (команды) ----
    public int TargetVehicles;
    public VehicleHandle Selected;
    public int GridlockStreak;            // фаза 9: подряд тиков, когда условие gridlock расходится с флагом
    public bool DebugDisableExitCheck;    // тестовый флаг (intersections.md §11, T6): отключить правило «выезд свободен» у ворот

    // ---- выходные массивы фаз 4, 9, 10 ----
    public readonly SegmentLoad[] SegmentLoads;
    public readonly uint[] NodeThroughput;
    public readonly BusRouteStats[] BusRouteStats;
    public readonly SignalState[] SignalStates;
    public int SignalCount;
    public readonly byte[] LaneSignals;
    public readonly VehicleRenderData[][] Render;
    public long SnapshotTick;
    public readonly int[] SnapshotSlots = new int[2]; // HighWater на момент снапшота в Render[i]; длина публикуемого спана (ADR-0010)

    /// <summary>Запускает <paramref name="threads"/> - 1 воркеров (≤ 1: ничего не создаёт). Вызывает <c>Simulation</c> сразу после построения мира.</summary>
    public void StartWorkers(int threads)
    {
        if (threads <= 1)
        {
            return;
        }

        threads = Math.Min(threads, SimRuntimeOptions.MaxWorkerThreads);
        Pool = new WorkerPool(threads);
        Behaviour = new BehaviourJob(threads);
        Integrate = new IntegrateJob(threads);
    }

    public void Dispose()
    {
        Pool?.Dispose();
        Pool = null;
    }

    public World(in ScenarioDesc desc, in RulesDesc rules, in RoutingDesc routing, VehicleTypeDesc[] types)
    {
        SimCapacities c = desc.Capacities;
        if (c.MaxPlanSteps <= 0)
        {
            c.MaxPlanSteps = (int)Math.Min(WorldBuilder.MaxPlanStepsCapacity, (long)c.MaxRoutes * (12 + (8 * Math.Max(1, routing.Levels))));
        }

        Caps = c;
        Rules = rules;
        Routing = routing;
        Seed = desc.Seed;
        Types = types;
        Demand = desc.Demand;
        TransitCfg = desc.Transit;

        Net = new NetworkStore(c);
        Builder = desc.City.Enabled
            ? new NetworkBuilder(Net, desc.City.LaneWidth, desc.City.ConnectorSamples)
            : new NetworkBuilder(Net);
        Vehicles = new VehicleStore(c.MaxVehicles);
        Occupancy = new LaneOccupancy(Vehicles, c.MaxLanes);
        Trips = new PendingTrips(c.MaxPendingTrips);
        Routes = new RouteStore(c.MaxRoutes, c.MaxPlanSteps, routing.WindowSteps, routing.Levels);
        Graph = new RouteGraph(Net);
        Rerouting = new RingBuffer<RerouteEntry>(c.MaxVehicles);
        RefillQueue = new RingBuffer<RefillEntry>(c.MaxVehicles);
        Spawn = new SpawnerData(c.MaxZones, types.Length);

        // 0 = по умолчанию (intersections.md §4): MaxNodes * 16 connector'ов, MaxConnectors * 6 записей конфликтов.
        int maxConnectors = c.MaxConnectors > 0 ? c.MaxConnectors : checked(c.MaxNodes * 16);
        int maxConflicts = c.MaxConflicts > 0 ? c.MaxConflicts : checked(maxConnectors * 6);
        Connectors = new ConnectorStore(maxConnectors, maxConflicts);
        Signals = new SignalStore(c.MaxSignalizedNodes);
        Gate = new GateScratch(c.MaxNodes, c.MaxVehicles, c.MaxLanes);

        RngCityGen = Pcg32.ForStream(Seed, RngStream.CityGen);
        RngZones = Pcg32.ForStream(Seed, RngStream.Zones);
        RngSpawn = Pcg32.ForStream(Seed, RngStream.Spawn);
        RngBehaviour = Pcg32.ForStream(Seed, RngStream.Behaviour);
        RngTransit = Pcg32.ForStream(Seed, RngStream.Transit);
        RngRouting = Pcg32.ForStream(Seed, RngStream.Routing);

        Commands = new RingBuffer<Command>(c.CommandQueueCapacity);
        Events = new SimEvent[c.EventCapacity];

        SegmentLoads = new SegmentLoad[c.MaxSegments];
        for (int i = 0; i < SegmentLoads.Length; i++)
        {
            SegmentLoads[i].SpeedRatio = 1f;
        }

        NodeThroughput = new uint[c.MaxNodes];
        BusRouteStats = new BusRouteStats[c.MaxBusRoutes];
        SignalStates = new SignalState[c.MaxSignalizedNodes];
        LaneSignals = new byte[c.MaxLanes];
        Render = new[] { new VehicleRenderData[c.MaxVehicles], new VehicleRenderData[c.MaxVehicles] };
    }

    /// <summary>
    /// Пишет событие в буфер. Последний слот зарезервирован под <see cref="SimEventKind.EventsOverflow"/>:
    /// при нехватке места остальные события теряются (<c>Counters.EventsDropped++</c>).
    /// </summary>
    public void Emit(SimEventKind kind, uint tag = 0, int a = 0, int b = 0)
    {
        int cap = Events.Length;
        if (EventCount < cap - 1)
        {
            Events[EventCount++] = new SimEvent { Kind = kind, Tag = tag, Tick = Tick, A = a, B = b };
            return;
        }

        Counters.EventsDropped++;
        if (!EventsOverflowed && cap > 0)
        {
            EventsOverflowed = true;
            Events[cap - 1] = new SimEvent { Kind = SimEventKind.EventsOverflow, Tick = Tick };
            EventCount = cap;
        }
    }
}
