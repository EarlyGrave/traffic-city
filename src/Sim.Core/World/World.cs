using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Gen;
using Sim.Core.Lod;
using Sim.Core.Metrics;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Save;
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
    public readonly PedDesc PedCfg;            // с применёнными дефолтами (PedNetwork.WithDefaults); Enabled = false - пешеходов нет, ничего не выделено
    public readonly LodDesc LodCfg;            // с применёнными дефолтами (WorldBuilder.WithDefaults); Mode = Off - LOD нет
    public readonly CityDesc Town;             // секции режима жителей (zoning-demand.md §16); нули, если Demand.Model != Residents

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
    public Sim.Core.Peds.PedWorld? Peds;                  // пешеходы, жители, переходы, остановки (ADR-0020); null без секции peds - ни одна ветка пешеходов не исполняется
    public CityWorld? City;                      // участки, здания, жители, деньги, скимы (ADR-0011); null без demand.model = residents - ни одна ветка города не исполняется
    public LodCells? Lod;                        // LOD-ячейки и состояние уровней (ADR-0013); null при Lod.Mode = Off - ни одна ветка LOD не исполняется
    public QueueMeso? Queue;                     // Q-meso (lod.meso_model = "queue", ADR-0019, sim-lod.md §13.9); null - ветки очереди не исполняются
    public readonly SignalMetrics SignalWait;    // ожидание у светофоров по манёврам (2.5, вне хэша)
    public readonly TickMasks Masks;             // маски кандидатов фаз 7-8 из фазы 6 (ADR-0019, A3); производное, вне хэша
    public readonly GateScratch Gate;            // scratch фазы 4 (ворота): кандидаты, затронутые узлы/полосы, коды отказов

    // ---- результаты генератора города (заполняет WorldBuilder; для шагов 3 и 4) ----
    public CityGenResult CityInfo;
    public SignalPlanSeed[] SignalPlanSeeds = System.Array.Empty<SignalPlanSeed>();
    public GenTransit? Transit;

    // ---- сейв (Save/): не входят ни в хэш, ни в состояние мира ----
    public SaveScratch? SaveScratch;                                       // буферы сохранения (растут при первом вызове)
    public KeptBlock[] KeptBlocks = System.Array.Empty<KeptBlock>();       // неизвестные необязательные блоки загруженного сейва: переписываются в следующий
    public HostBlock[] LoadedHostBlocks = System.Array.Empty<HostBlock>(); // блоки хоста загруженного сейва (камера, meta); хост читает и сам решает, что писать дальше
    public byte[] LoadedHostData = System.Array.Empty<byte>();
    public AssetRef[] AssetManifest = System.Array.Empty<AssetRef>();      // манифест ассетов (хост задаёт после Create; пишется в MANI)
    public byte[] AssetIds = System.Array.Empty<byte>();
    public byte[] AssetStates = System.Array.Empty<byte>();                // AssetState по записям манифеста после Load; в сейв не пишется

    // ---- PRNG: по подпотоку на подсистему (core-api 8) ----
    public Pcg32 RngCityGen;
    public Pcg32 RngZones;
    public Pcg32 RngSpawn;
    public Pcg32 RngBehaviour;
    public Pcg32 RngTransit;
    public Pcg32 RngRouting;
    public Pcg32 RngDemand;               // подпоток Demand (zoning-demand.md §18): Create города, события жителей, дневные проходы; в хэше только при residents
    public Pcg32 RngPeds;                 // подпоток Peds (pedestrians.md §13): жители в Create, скорость при запуске поездки, время занятий

    // ---- время, команды, события, метрики ----
    public long Tick;
    public SimCounters Counters;
    public SimWork Work;                  // счётчики работы горячего пути (ADR-0016), вне хэша
    public PhaseProbe? PhaseProbe;        // зонд конца фазы (core-api §3); задаёт хост, ядро время не читает
    public BehaviourJob Behaviour = new(1);  // задание фазы 5 (ADR-0017); счётчики по воркерам
    public IntegrateJob Integrate = new(1);  // задание фазы 6 (ADR-0017); HardClamps по воркерам
    public SnapshotJob SnapshotJob = new(); // задание фазы 10 (ADR-0017)
    public SnapshotClearJob SnapshotClearJob = new(); // обнуление слотов прошлого вида (фаза 10, ADR-0019)
    public readonly RenderViewState RenderView = new(); // вид снапшота (SetRenderView); презентация, вне хэша
    public WorkerPool? Pool;              // воркеры параллельных фаз; null = один поток (SimRuntimeOptions.WorkerThreads <= 1)
    public readonly RingBuffer<Command> Commands;
    public readonly SimEvent[] Events;
    public int EventCount;
    public bool EventsOverflowed;

    // ---- управляющее состояние (команды) ----
    public int TargetVehicles;
    public int TargetPeds;                // цель активных пешеходов (SetPedTarget); при peds = PedCfg.Count
    public VehicleHandle Selected;
    public int[] LaneScratch = System.Array.Empty<int>();   // QualityMetrics: машин по полосам (scratch, всегда обнулён между выборками)
    public int GridlockStreak;            // фаза 9: подряд тиков, когда условие gridlock расходится с флагом
    public bool DebugDisableExitCheck;    // тестовый флаг (intersections.md §11, T6): отключить правило «выезд свободен» у ворот

    // ---- выходные массивы фаз 4, 9, 10 ----
    public readonly SegmentLoad[] SegmentLoads;
    public long SegmentLoadsTick = -1;    // номер тика, по состоянию конца которого посчитан SegmentLoads (ADR-0019, A4); производное, вне хэша
    public readonly uint[] NodeThroughput;
    public readonly BusRouteStats[] BusRouteStats;
    public readonly SignalState[] SignalStates;
    public int SignalCount;
    public readonly byte[] LaneSignals;
    public readonly VehicleRenderData[][] Render;
    public long SnapshotTick;

    // ---- пауза трафика агрегата (zoning-demand.md §9.2); производное, вне сейва и хэша ----
    public int PausedTicks;               // подряд идущих тиков с паузой трафика (после двух снапшоты пусты в обоих буферах)
    public long JumpedTicks;              // тиков, пропущенных прыжком (диагностика тестов)
    public bool DisableJump;              // тест «прыжки = шаги»: прыжки выключены, пустые тики идут по одному
    public readonly int[] SnapshotSlots = new int[2]; // HighWater на момент снапшота в Render[i]; длина публикуемого спана (ADR-0010)

    /// <summary>1 после первой структурной правки сети (<see cref="NetworkStore.ToolsTouched"/>): с этого момента раскладка, free-list'ы и новые поля сети входят в хэш и сейв (player-tools.md §10).</summary>
    public byte ToolsTouched => Net.ToolsTouched;

    /// <summary>
    /// Сверка производных сети с полной перестройкой (player-tools.md §3 п. 4); для тестов и отладки (аллоцирует). <c>null</c> - всё совпало, иначе описание расхождения.
    /// </summary>
    public string? VerifyDerived() => DerivedCheck.Run(this);

    /// <summary>true = <c>Lod.Mode != Off</c>: ячейки построены, массивы уровней машин выделены.</summary>
    public bool LodEnabled => Lod != null;

    /// <summary>
    /// Нагрузка сегментов по состоянию конца последнего завершённого тика (ADR-0019, A4). Фаза 9 считает её только перед пересчётом весов
    /// (<see cref="MetricsPhase.NeedsLoadsNow"/>); остальным читателям (API, команды) недостающее досчитывается здесь по неизменному с конца тика
    /// состоянию машин - то же значение, которое прежде считалось каждый тик. Звать до любых изменений машин в текущем тике.
    /// </summary>
    public SegmentLoad[] EnsureSegmentLoads()
    {
        if (SegmentLoadsTick != Tick - 1)
        {
            MetricsPhase.ComputeSegmentLoads(this);
            SegmentLoadsTick = Tick - 1;
        }

        return SegmentLoads;
    }

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
        LodCfg = WorldBuilder.WithDefaults(desc.Lod);
        Town = desc.Demand.Model == DemandModel.Residents ? desc.Town : default;
        PedCfg = desc.Peds.Enabled ? Sim.Core.Peds.PedNetwork.WithDefaults(desc.Peds, desc.Transit.HeadwayTicks) : default;

        Net = new NetworkStore(c);
        Builder = desc.City.Enabled
            ? new NetworkBuilder(Net, desc.City.LaneWidth, desc.City.ConnectorSamples)
            : new NetworkBuilder(Net);
        Vehicles = new VehicleStore(c.MaxVehicles, LodCfg.Mode != LodMode.Off);
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
        Connectors = new ConnectorStore(maxConnectors, maxConflicts, PedCfg.Enabled);
        Signals = new SignalStore(c.MaxSignalizedNodes, rules.SignalArrowGapTicks);
        Masks = new TickMasks(c.MaxVehicles);
        Gate = new GateScratch(c.MaxNodes, c.MaxVehicles, c.MaxLanes, maxConnectors);
        SignalWait = new SignalMetrics();
        Graph.AttachSignals(Signals, Gate.ConnDelay);

        RngCityGen = Pcg32.ForStream(Seed, RngStream.CityGen);
        RngZones = Pcg32.ForStream(Seed, RngStream.Zones);
        RngSpawn = Pcg32.ForStream(Seed, RngStream.Spawn);
        RngBehaviour = Pcg32.ForStream(Seed, RngStream.Behaviour);
        RngTransit = Pcg32.ForStream(Seed, RngStream.Transit);
        RngRouting = Pcg32.ForStream(Seed, RngStream.Routing);
        RngPeds = Pcg32.ForStream(Seed, RngStream.Peds);
        RngDemand = Pcg32.ForStream(Seed, RngStream.Demand);

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
