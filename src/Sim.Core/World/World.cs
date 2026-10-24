using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core;

/// <summary>
/// Композиция всех хранилищ и состояния симуляции (core-api 13, 13.1). Создаётся только
/// <see cref="WorldBuilder"/>; все массивы выделены по <see cref="SimCapacities"/>, после этого аллокаций нет.
/// Фазы тика - <c>internal static class XxxPhase { public static void Run(World w) }</c>.
/// Namespace <c>Sim.Core</c> (а не <c>Sim.Core.World</c>): одноимённый namespace затенял бы тип во всех <c>Sim.Core.*</c>.
/// </summary>
internal sealed class World
{
    // ---- параметры (копии из ScenarioDesc) ----
    public readonly SimCapacities Caps;
    public readonly RulesDesc Rules;
    public readonly ulong Seed;
    public readonly VehicleTypeDesc[] Types;
    public readonly DemandDesc Demand;

    // ---- хранилища ----
    public readonly NetworkStore Net;
    public readonly NetworkBuilder Builder;
    public readonly VehicleStore Vehicles;
    public readonly LaneOccupancy Occupancy;
    public readonly PendingTrips Trips;
    public readonly RoutePool Routes;
    public readonly SpawnerData Spawn;
    public readonly ConnectorStore Connectors;   // connector'ы и конфликтные пары (intersections.md §3, §4); строится в WorldBuilder
    public readonly SignalStore Signals;

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
    public readonly RingBuffer<Command> Commands;
    public readonly SimEvent[] Events;
    public int EventCount;
    public bool EventsOverflowed;

    // ---- управляющее состояние (команды) ----
    public int TargetVehicles;
    public VehicleHandle Selected;
    public int GridlockStreak;            // фаза 9: подряд тиков, когда условие gridlock расходится с флагом

    // ---- выходные массивы фаз 4, 9, 10 ----
    public readonly SegmentLoad[] SegmentLoads;
    public readonly uint[] NodeThroughput;
    public readonly BusRouteStats[] BusRouteStats;
    public readonly SignalState[] SignalStates;
    public int SignalCount;
    public readonly byte[] LaneSignals;
    public readonly VehicleRenderData[][] Render;
    public long SnapshotTick;

    public World(in ScenarioDesc desc, in RulesDesc rules, VehicleTypeDesc[] types)
    {
        SimCapacities c = desc.Capacities;
        Caps = c;
        Rules = rules;
        Seed = desc.Seed;
        Types = types;
        Demand = desc.Demand;

        Net = new NetworkStore(c);
        Builder = desc.City.Enabled
            ? new NetworkBuilder(Net, desc.City.LaneWidth, desc.City.ConnectorSamples)
            : new NetworkBuilder(Net);
        Vehicles = new VehicleStore(c.MaxVehicles);
        Occupancy = new LaneOccupancy(Vehicles, c.MaxLanes);
        Trips = new PendingTrips(c.MaxPendingTrips);
        Routes = new RoutePool(c.MaxRoutes, c.MaxRouteSteps);
        Spawn = new SpawnerData(c.MaxZones, c.MaxLanes, types.Length);

        // 0 = по умолчанию (intersections.md §4): MaxNodes * 16 connector'ов, MaxConnectors * 6 записей конфликтов.
        int maxConnectors = c.MaxConnectors > 0 ? c.MaxConnectors : checked(c.MaxNodes * 16);
        int maxConflicts = c.MaxConflicts > 0 ? c.MaxConflicts : checked(maxConnectors * 6);
        Connectors = new ConnectorStore(maxConnectors, maxConflicts);
        Signals = new SignalStore(c.MaxSignalizedNodes);

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
