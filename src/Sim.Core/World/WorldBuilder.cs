using System;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Lod;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core;

/// <summary>
/// Создание <see cref="World"/> (core-api 3, 10): валидация <see cref="ScenarioDesc"/>, выделение всех массивов,
/// построение сети. Единственное место ядра, где допустимы аллокации; исключений нет - только <see cref="CreateStatus"/>.
/// </summary>
internal static class WorldBuilder
{
    /// <summary>Верхний предел любой ёмкости (защита от случайных значений вроде int.MaxValue).</summary>
    public const int MaxCapacity = 1 << 24;

    /// <summary>Предел арены планов маршрутов (int, 4 байта); отдельно от <see cref="MaxCapacity"/>.</summary>
    public const int MaxPlanStepsCapacity = 1 << 28;

    /// <summary>Предел массива окон маршрутов: <c>max_routes · window_steps</c> шагов по 8 байт.</summary>
    public const long MaxWindowCells = 1L << 28;

    public const int MaxVehicleTypes = 256; // VehicleStore.Type - byte

    public static CreateStatus Create(in ScenarioDesc desc, out World? world)
    {
        world = null;
        CreateStatus st = ValidateCapacities(in desc.Capacities);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateRoadProfiles(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateRouting(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateLod(in desc.Lod);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateVehicleTypes(desc.VehicleTypes);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateDemand(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateTransit(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidatePeds(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = City.CityBuild.Validate(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateProgression(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = Terrain.TerrainBuild.Validate(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        var w = new World(in desc, WithDefaults(desc.Rules), WithDefaults(desc.Routing), (VehicleTypeDesc[])desc.VehicleTypes.Clone());
        st = BuildNetwork(w, in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = w.Connectors.BuildAll(w.Net).ToCreateStatus();
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        w.Signals.InitFromSeeds(w); // светофоры генератора (intersections.md §6.4); для явной сети seeds нет

        st = BuildZones(w, in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = BuildOverlay(w);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = BuildLod(w);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = BuildPeds(w);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = Terrain.TerrainBuild.Build(w, in desc);   // рельеф и вода (terrain.md §2.4): после сети, до участков; без terrain - ничего
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = City.CityBuild.Build(w, create: true, default);   // жители: нарезка, зоны, начальное население (zoning-demand.md §2.5); без residents - ничего
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        if (desc.Progression.Enabled)
        {
            w.Progression = City.ProgressionState.FromDesc(in desc.Progression, ProfileCount(in desc));   // вехи (ADR-0034): только при residents (проверено выше)
        }

        w.TargetVehicles = desc.Demand.TargetVehicles;
        SyncStructuralCounters(w);
        world = w;
        return CreateStatus.Ok;
    }

    private static int ProfileCount(in ScenarioDesc desc) => desc.RoadProfiles.Enabled ? desc.RoadProfiles.Profiles.Length : 0;

    /// <summary>Секция progression (progression.md §4.1): только при жителях; вехи, ворота, признаки, профили и области - <see cref="City.ProgressionState.Validate"/>.</summary>
    private static CreateStatus ValidateProgression(in ScenarioDesc desc)
    {
        ref readonly ProgressionDesc p = ref desc.Progression;
        if (!p.Enabled)
        {
            return CreateStatus.Ok;
        }

        if (desc.Demand.Model != DemandModel.Residents)
        {
            return CreateStatus.InvalidCity;
        }

        int profiles = ProfileCount(in desc);
        int[] locked = p.LockedProfiles ?? System.Array.Empty<int>();
        foreach (int lp in locked)
        {
            if ((uint)lp >= (uint)profiles)
            {
                return CreateStatus.InvalidCity;
            }
        }

        bool ok = City.ProgressionState.Validate(p.Milestones, p.ProfileIdx, p.Areas, p.MinLegs, p.LockedFeatures, profiles);
        return ok ? CreateStatus.Ok : CreateStatus.InvalidCity;
    }

    /// <summary>
    /// Каталог профилей дорог (ADR-0030): без каталога ссылок на профили быть не должно (режим legacy); с каталогом он валиден, а каждый сегмент явной сети
    /// и оба класса дорог генератора несут профиль, согласованный с полосами, видом дороги и <c>Mirrored</c>.
    /// </summary>
    private static CreateStatus ValidateRoadProfiles(in ScenarioDesc desc)
    {
        if (!desc.RoadProfiles.Enabled)
        {
            bool refs = desc.City.Enabled ? desc.City.StreetProfile != 0 || desc.City.ArterialProfile != 0 : AnySegmentProfile(desc.Network.Segments);
            return refs ? CreateStatus.InvalidNetwork : CreateStatus.Ok;
        }

        if (RoadProfileValidator.Validate(in desc.RoadProfiles, out _) != RoadProfileError.None)
        {
            return CreateStatus.InvalidNetwork;
        }

        RoadProfileDesc[] ps = desc.RoadProfiles.Profiles;
        if (desc.City.Enabled)
        {
            int sp = desc.City.StreetProfile - 1;
            int ap = desc.City.ArterialProfile - 1;
            bool ok = (uint)sp < (uint)ps.Length && (uint)ap < (uint)ps.Length && ps[sp].Kind == RoadKind.Street && ps[ap].Kind == RoadKind.Arterial;
            return ok ? CreateStatus.Ok : CreateStatus.InvalidNetwork;
        }

        SegmentDesc[]? segs = desc.Network.Segments;
        for (int i = 0; segs != null && i < segs.Length; i++)
        {
            ref readonly SegmentDesc s = ref segs[i];
            int p = s.Profile - 1;
            if ((uint)p >= (uint)ps.Length || ps[p].Kind != s.Kind)
            {
                return CreateStatus.InvalidNetwork;
            }

            int fwd = 0;
            int bwd = 0;
            foreach (RoadElemDesc e in ps[p].Elements)
            {
                if (e.Kind == RoadElemKind.Lane)
                {
                    if (e.Dir == RoadLaneDir.Forward)
                    {
                        fwd++;
                    }
                    else
                    {
                        bwd++;
                    }
                }
            }

            if (s.LanesAB != (s.Mirrored ? bwd : fwd) || s.LanesBA != (s.Mirrored ? fwd : bwd))
            {
                return CreateStatus.InvalidNetwork;
            }
        }

        return CreateStatus.Ok;
    }

    private static bool AnySegmentProfile(SegmentDesc[]? segs)
    {
        for (int i = 0; segs != null && i < segs.Length; i++)
        {
            if (segs[i].Profile != 0 || segs[i].Mirrored)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Строит сеть. Явная сеть (<c>desc.Network</c>) - узлы без прилипания друг к другу, сегменты, connector'ы всех узлов.
    /// </summary>
    public static CreateStatus BuildNetwork(World w, in ScenarioDesc desc)
    {
        if (desc.City.Enabled)
        {
            return BuildCity(w, in desc);
        }

        NodeDesc[]? nodes = desc.Network.Nodes;
        SegmentDesc[]? segments = desc.Network.Segments;
        int nodeCount = nodes?.Length ?? 0;
        int segmentCount = segments?.Length ?? 0;

        for (int i = 0; i < nodeCount; i++)
        {
            NodeDesc n = nodes![i];
            NetworkStatus st = w.Builder.AddNode(new Vec2(n.X, n.Z), n.Control, out int idx);
            if (st != NetworkStatus.Ok)
            {
                return st.ToCreateStatus();
            }

            // Прилипание сдвинуло бы индексы узлов относительно desc: явная сеть обязана быть разреженной (> SnapRadius).
            if (idx != i)
            {
                return CreateStatus.InvalidNetwork;
            }
        }

        // Отступы концов по всем рукавам узла (ADR-0028): явная сеть известна целиком до эмиссии сегментов.
        int[] segA = new int[segmentCount];
        int[] segB = new int[segmentCount];
        int[] segLanes = new int[segmentCount];
        int[] segProfile = new int[segmentCount];
        float[] setA = new float[segmentCount];
        float[] setB = new float[segmentCount];
        for (int i = 0; i < segmentCount; i++)
        {
            segA[i] = segments![i].A;
            segB[i] = segments[i].B;
            segLanes[i] = segments[i].LanesAB + segments[i].LanesBA;
            segProfile[i] = segments[i].Profile - 1;   // desc: индекс + 1, 0 - нет
        }

        w.Builder.PlanSetbacks(segA, segB, segLanes, segProfile, setA, setB);

        for (int i = 0; i < segmentCount; i++)
        {
            SegmentDesc s = segments![i];
            NetworkStatus st = s.Profile > 0
                ? w.Builder.AddProfileSegment(s.A, s.B, ReadOnlySpan<Vec2>.Empty, s.Profile - 1, s.Mirrored, s.SpeedLimit, setA[i], setB[i], out _)
                : w.Builder.AddSegment(s.A, s.B, ReadOnlySpan<Vec2>.Empty, s.LanesAB, s.LanesBA, s.SpeedLimit, s.Kind, setA[i], setB[i], out _);
            if (st != NetworkStatus.Ok)
            {
                return st.ToCreateStatus();
            }
        }

        NetworkStatus built = w.Builder.BuildAllConnectorsEx();
        return built.ToCreateStatus();
    }

    /// <summary>
    /// Генератор города (city-generator 7): сеть, затем зоны (ZoneGenerator -> <c>Net.Zones</c>/<c>Net.ZoneLanes</c>)
    /// и автобусные маршруты (TransitGenerator -> <c>Net.Bus*</c> + <c>w.Transit</c> с путями); планы светофоров - в <c>w.SignalPlanSeeds</c>.
    /// </summary>
    private static CreateStatus BuildCity(World w, in ScenarioDesc desc)
    {
        CreateStatus st = CityGenerator.Generate(in desc.City, in w.Rules, in w.Caps, w.Builder, ref w.RngCityGen, out CityGenResult city, w.Seed);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        w.CityInfo = city;
        w.LandUse = city.LandUse ?? System.Array.Empty<LandUsePoly>();
        w.LandUseVersion = w.LandUse.Length > 0 ? 1 : 0;
        w.SignalPlanSeeds = city.SignalPlans ?? System.Array.Empty<SignalPlanSeed>();
        w.SignalCount = w.SignalPlanSeeds.Length;
        if (w.SignalCount > w.SignalStates.Length)
        {
            return CreateStatus.NetworkTooLarge;
        }

        NetworkStore net = w.Net;
        var zones = new GenZones(w.Caps.MaxZones, w.Caps.MaxLanes);
        st = ZoneGenerator.Generate(in desc.Demand, city.Width, city.Height, net, zones, ref w.RngZones, out _);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        Array.Copy(zones.Zones, net.Zones, zones.ZoneCount);
        Array.Copy(zones.ZoneLanes, net.ZoneLanes, zones.ZoneLaneCount);
        net.ZoneCount = zones.ZoneCount;
        net.ZoneLaneCount = zones.ZoneLaneCount;

        var transit = new GenTransit(w.Caps.MaxBusRoutes, w.Caps.MaxBusStops, w.Caps.MaxBusStopsPerRoute);
        st = TransitGenerator.Generate(in desc.Transit, city.Width, city.Height, net, transit, ref w.RngTransit, out _);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        Array.Copy(transit.Routes, net.BusRoutes, transit.RouteCount);
        Array.Copy(transit.Stops, net.BusStops, transit.StopCount);
        Array.Copy(transit.RouteStops, net.BusRouteStops, transit.StopCount);
        net.AdoptBusRoutes(transit.RouteCount);
        net.AdoptBusStops(transit.StopCount);
        net.BusRouteStopCount = transit.StopCount;
        w.Transit = transit;
        return CreateStatus.Ok;
    }

    private static CreateStatus BuildZones(World w, in ScenarioDesc desc)
    {
        if (desc.City.Enabled)
        {
            return CreateStatus.Ok; // зоны уже построены ZoneGenerator в BuildCity
        }

        ZoneDesc[]? zones = desc.Demand.Zones;
        int n = zones?.Length ?? 0;
        if (n > w.Net.Zones.Length)
        {
            return CreateStatus.NetworkTooLarge;
        }

        for (int i = 0; i < n; i++)
        {
            ZoneDesc z = zones![i];
            w.Net.Zones[i] = new ZoneData
            {
                Center = new Vec2(z.CenterX, z.CenterZ),
                HalfSize = z.HalfSize,
                Weight = z.Weight,
                FirstSpawnLane = 0, // TODO(gen): полосы появления назначает ZoneGenerator
                SpawnLaneCount = 0,
                IsExternal = z.IsExternal ? (byte)1 : (byte)0,
            };
        }

        w.Net.ZoneCount = n;
        return CreateStatus.Ok;
    }

    /// <summary>
    /// Оверлей маршрутизации (routing.md 9): граф актуализируется по готовой сети, ячейки и входы/выходы раскладываются, все матрицы
    /// кастомизируются. <c>routing.levels = 0</c> - оверлея нет. Время этого шага - основной риск шага 2.2 (замер в отчёте H1).
    /// </summary>
    private static CreateStatus BuildOverlay(World w)
    {
        w.Graph.Rebuild();
        CreateStatus st = CreateStatus.Ok;
        if (w.Routing.Levels > 0)
        {
            st = Routing.Overlay.TryCreate(w.Net, w.Graph, in w.Routing, out Routing.Overlay? ov);
            w.Overlay = ov;
        }

        w.Router = new Routing.Router(w, w.Graph, w.Overlay, w.Routing.QueryWorkCap);
        return st;
    }

    /// <summary>
    /// Сеть пешеходов (ADR-0020, pedestrians.md §2): при выключенной секции <c>peds</c> ничего не выделяется и не меняется.
    /// Строится после сети, connector'ов, зон и автобусных остановок; не трогает ГСЧ и состояние.
    /// </summary>
    private static CreateStatus BuildPeds(World w) => BuildPeds(w, create: true);

    /// <summary>
    /// <paramref name="create"/> = false: загрузчик сейва выделяет структуры, а жителей и состояние берёт из блоков файла (<c>PEDS</c> и др.).
    /// </summary>
    internal static CreateStatus BuildPeds(World w, bool create)
    {
        if (!w.PedCfg.Enabled)
        {
            return CreateStatus.Ok;
        }

        var net = new Peds.PedNetwork(in w.Caps, w.Net, w.Connectors, in w.PedCfg);
        w.Net.PedNet = net;
        if (!net.Rebuild(w))
        {
            return CreateStatus.NetworkTooLarge;
        }

        var pw = new Peds.PedWorld(w);
        w.Peds = pw;
        if (create)
        {
            pw.Init(w);
        }
        else
        {
            w.TargetPeds = 0;
        }

        return CreateStatus.Ok;
    }

    /// <summary>Секция <c>peds</c> (pedestrians.md §12): пределы ёмкостей и параметров; <c>passenger_model = agents</c> без <c>peds</c> - отказ.</summary>
    internal static CreateStatus ValidatePeds(in ScenarioDesc desc)
    {
        if (desc.Transit.PassengerModel < PassengerModel.Counter || desc.Transit.PassengerModel > PassengerModel.Agents)
        {
            return CreateStatus.InvalidTransit;
        }

        if (!desc.Peds.Enabled)
        {
            return desc.Transit.PassengerModel == PassengerModel.Agents ? CreateStatus.InvalidPeds : CreateStatus.Ok;
        }

        if (desc.Lod.Mode != LodMode.Off && desc.Lod.MesoModel == MesoModel.Queue)
        {
            return CreateStatus.NotSupported;   // Q-meso + peds: машины очереди не материализованы, обязавшихся нет (ADR-0020, pedestrians.md §5.6)
        }

        PedDesc p = Peds.PedNetwork.WithDefaults(desc.Peds, desc.Transit.HeadwayTicks);
        return Peds.PedNetwork.IsValid(in p, in desc.Capacities) ? CreateStatus.Ok : CreateStatus.InvalidPeds;
    }

    /// <summary>LOD-ячейки (ADR-0013): при <c>Mode = Off</c> ничего не выделяется и не меняется (побитово прежнее ядро).</summary>
    private static CreateStatus BuildLod(World w)
    {
        if (w.LodCfg.Mode == LodMode.Off)
        {
            return CreateStatus.Ok;
        }

        if (w.LodCfg.MesoModel == MesoModel.Queue && w.LodCfg.Mode != LodMode.AllMeso)
        {
            return CreateStatus.NotSupported;   // Q0: очередь полосы только в all_meso (смена уровня Q <-> micro - задача Q1)
        }

        CreateStatus st = LodCells.TryCreate(w.Net, in w.LodCfg, out LodCells? cells);
        AttachLod(w, cells, st == CreateStatus.Ok);
        return st;
    }

    /// <summary>Подключает LOD-ячейки к миру (и модель очереди Q-meso, если она задана); общий код <c>Create</c> и загрузки сейва.</summary>
    internal static void AttachLod(World w, LodCells? cells, bool ok = true)
    {
        w.Lod = cells;
        w.Occupancy.LodCells = cells;
        if (ok && cells != null && w.LodCfg.MesoModel == MesoModel.Queue)
        {
            w.Queue = new QueueMeso(w, cells);
            w.Occupancy.Queue = w.Queue;
            w.Vehicles.QueueBits = w.Queue.QBits;
        }
    }

    internal static CreateStatus ValidateLod(in LodDesc l)
    {
        if (l.Mode < LodMode.Off || l.Mode > LodMode.AllMeso || !NonNegative(l.CellM) || l.MicroBudget < 0 || l.EvalTicks < 0 ||
            l.HoldTicks < 0 || l.FocusMarginCells < 0 || !NonNegative(l.CongestionShare) || l.CongestionMin < 0 ||
            !NonNegative(l.MesoHeadwayScale) || !NonNegative(l.MesoAccelScale) || !NonNegative(l.QueueHeadwayScale) ||
            l.MesoModel < MesoModel.Kinematic || l.MesoModel > MesoModel.Queue ||
            (l.Focus?.Length ?? 0) > LodCells.FocusSlots || (l.Pinned?.Length ?? 0) > LodCells.PinSlots)
        {
            return CreateStatus.InvalidCapacity;
        }

        for (int k = 0; k < 2; k++)
        {
            LodPlaceDesc[]? places = k == 0 ? l.Focus : l.Pinned;
            for (int i = 0; i < (places?.Length ?? 0); i++)
            {
                LodPlaceDesc p = places![i];
                if (!IsFinite(p.X) || !IsFinite(p.Z) || !NonNegative(p.RadiusM))
                {
                    return CreateStatus.InvalidCapacity;
                }
            }
        }

        return CreateStatus.Ok;
    }

    /// <summary>Нулевые поля (desc, собранный вручную) заменяются дефолтами спеки; булевы без дефолта остаются как заданы (загрузчик ставит true).</summary>
    /// <summary>Калибровка эквивалентности коридору (sim-lod.md §9, итог L2): множитель времени интервала T в meso.</summary>
    internal const float DefaultMesoHeadwayScale = 0.8f;

    /// <summary>Калибровка насыщенного интервала очереди Q-meso по <c>SaturationProbe</c> (sim-lod.md §13.9): множитель T в <c>h</c>.</summary>
    internal const float DefaultQueueHeadwayScale = 0.2f;

    internal static LodDesc WithDefaults(LodDesc l)
    {
        if (!(l.CellM > 0f))
        {
            l.CellM = 1000f;
        }

        if (l.MicroBudget <= 0)
        {
            l.MicroBudget = 15000;
        }

        if (l.EvalTicks <= 0)
        {
            l.EvalTicks = 100;
        }

        if (l.HoldTicks <= 0)
        {
            l.HoldTicks = 600;
        }

        if (!(l.CongestionShare > 0f))
        {
            l.CongestionShare = 0.1f;
        }

        if (l.CongestionMin <= 0)
        {
            l.CongestionMin = 10;
        }

        if (!(l.MesoHeadwayScale > 0f))
        {
            l.MesoHeadwayScale = DefaultMesoHeadwayScale;
        }

        if (!(l.MesoAccelScale > 0f))
        {
            l.MesoAccelScale = 1f;
        }

        if (!(l.QueueHeadwayScale > 0f))
        {
            l.QueueHeadwayScale = DefaultQueueHeadwayScale;
        }

        return l;
    }

    internal static CreateStatus ValidateRouting(in ScenarioDesc desc)
    {
        RoutingDesc r = desc.Routing;
        if (r.Levels < 0 || r.Levels > Routing.Overlay.MaxLevels || r.Fanout < 0 || r.Fanout == 1 || r.Fanout > Routing.Overlay.MaxFanout ||
            !NonNegative(r.CellSize) || (r.WindowSteps != 0 && (r.WindowSteps < 8 || r.WindowSteps > 32)) ||
            !InRange(r.PathfindBudgetPerTick, 0) || r.WorkBudgetPerTick < 0 || r.CustomizeBudgetPerTick < 0 ||
            !NonNegative(r.CustomizeEps) || r.CostRecalcTicks < 0 || !NonNegative(r.TimedDrift) || r.TimedDriftAbsTicks < 0 ||
            desc.Capacities.MaxPlanSteps < 0 || desc.Capacities.MaxPlanSteps > MaxPlanStepsCapacity || r.QueryWorkCap < 0 ||
            (long)desc.Capacities.MaxRoutes * (r.WindowSteps == 0 ? 16 : r.WindowSteps) > MaxWindowCells)
        {
            return CreateStatus.InvalidCapacity;
        }

        return CreateStatus.Ok;
    }

    internal static RoutingDesc WithDefaults(RoutingDesc r)
    {
        if (r.Fanout <= 0)
        {
            r.Fanout = 4;
        }

        if (!(r.CellSize > 0f))
        {
            r.CellSize = 1000f;
        }

        if (r.WindowSteps <= 0)
        {
            r.WindowSteps = 16;
        }

        if (r.WorkBudgetPerTick <= 0)
        {
            r.WorkBudgetPerTick = (int)Math.Min(int.MaxValue, 1500L * r.PathfindBudgetPerTick);
        }

        if (r.CustomizeBudgetPerTick <= 0)
        {
            r.CustomizeBudgetPerTick = 10000;
        }

        if (!(r.CustomizeEps > 0f))
        {
            r.CustomizeEps = 0.05f;
        }

        if (r.CostRecalcTicks <= 0)
        {
            r.CostRecalcTicks = Routing.RoutePhase.DefaultCostRecalcTicks;
        }

        if (!(r.TimedDrift > 0f))
        {
            r.TimedDrift = 0.15f;
        }

        if (r.TimedDriftAbsTicks <= 0)
        {
            r.TimedDriftAbsTicks = 400;
        }

        return r;
    }

    public static void SyncStructuralCounters(World w)
    {
        w.Counters.NodeCount = w.Net.NodeCount;
        w.Counters.SegmentCount = w.Net.SegmentCount;
        w.Counters.LaneCount = w.Net.LaneCount;
        w.Counters.ZoneCount = w.Net.ZoneCount;
        w.Counters.SignalizedNodes = w.SignalCount;
        w.Counters.ActiveVehicles = w.Vehicles.AliveCount;
    }

    internal static CreateStatus ValidateCapacities(in SimCapacities c)
    {
        // Обязательные (> 0).
        if (!InRange(c.MaxVehicles, 1) || !InRange(c.MaxNodes, 1) || !InRange(c.MaxSegments, 1) || !InRange(c.MaxLanes, 1) ||
            !InRange(c.MaxLanePoints, 1) || !InRange(c.MaxPendingTrips, 1) || !InRange(c.CommandQueueCapacity, 1) ||
            !InRange(c.EventCapacity, 1))
        {
            return CreateStatus.InvalidCapacity;
        }

        // Необязательные (>= 0).
        if (!InRange(c.MaxZones, 0) || !InRange(c.MaxBusRoutes, 0) || !InRange(c.MaxBusStops, 0) ||
            !InRange(c.MaxBusStopsPerRoute, 0) || !InRange(c.MaxRoutes, 0) || !InRange(c.MaxSignalizedNodes, 0))
        {
            return CreateStatus.InvalidCapacity;
        }

        // Произведение BusRoutes * StopsPerRoute выделяется одним массивом.
        if ((long)c.MaxBusRoutes * c.MaxBusStopsPerRoute > MaxCapacity)
        {
            return CreateStatus.InvalidCapacity;
        }

        return CreateStatus.Ok;
    }

    private static bool InRange(int v, int min) => v >= min && v <= MaxCapacity;

    internal static CreateStatus ValidateVehicleTypes(VehicleTypeDesc[]? types)
    {
        if (types == null || types.Length == 0 || types.Length > MaxVehicleTypes)
        {
            return CreateStatus.InvalidVehicleType;
        }

        for (int i = 0; i < types.Length; i++)
        {
            ref readonly VehicleTypeDesc t = ref types[i];
            if (!Positive(t.Length) || !Positive(t.Width) || !Positive(t.MaxSpeed) || !Positive(t.SpeedFactor) ||
                !Positive(t.Accel) || !Positive(t.ComfortDecel) || !NonNegative(t.HeadwayTime) || !NonNegative(t.MinGap) ||
                !Positive(t.Delta) || t.LaneChangeDurationTicks < 0 ||
                (t.Category != VehicleCategory.Car && t.Category != VehicleCategory.Bus))
            {
                return CreateStatus.InvalidVehicleType;
            }
        }

        return CreateStatus.Ok;
    }

    internal static CreateStatus ValidateDemand(in ScenarioDesc desc)
    {
        DemandDesc d = desc.Demand;
        if (d.TargetVehicles < 0 || d.SpawnRampTicks < 0)
        {
            return CreateStatus.InvalidDemand;
        }

        if (d.TypeMix != null)
        {
            if (d.TypeMix.Length > desc.VehicleTypes.Length)
            {
                return CreateStatus.InvalidDemand;
            }

            for (int i = 0; i < d.TypeMix.Length; i++)
            {
                if (!NonNegative(d.TypeMix[i]))
                {
                    return CreateStatus.InvalidDemand;
                }
            }
        }

        int zones = d.Zones?.Length ?? 0;
        for (int i = 0; i < zones; i++)
        {
            ZoneDesc z = d.Zones![i];
            if (!IsFinite(z.CenterX) || !IsFinite(z.CenterZ) || !Positive(z.HalfSize) || !NonNegative(z.Weight))
            {
                return CreateStatus.InvalidDemand;
            }
        }

        int od = d.OdPairs?.Length ?? 0;
        for (int i = 0; i < od; i++)
        {
            OdPairDesc p = d.OdPairs![i];
            if ((uint)p.FromZone >= (uint)zones || (uint)p.ToZone >= (uint)zones || !NonNegative(p.Weight))
            {
                return CreateStatus.InvalidDemand;
            }
        }

        return CreateStatus.Ok;
    }

    /// <summary>Верхняя граница <c>transit.bay_yield_s</c> (60 с, bus-bays.md §3), тики.</summary>
    internal const int MaxBayYieldTicks = 1200;

    internal static CreateStatus ValidateTransit(in ScenarioDesc desc)
    {
        TransitDesc t = desc.Transit;
        int explicitRoutes = t.RoutesExplicit?.Length ?? 0;
        if (t.Routes < 0 || t.HeadwayTicks < 0)
        {
            return CreateStatus.InvalidTransit;
        }

        if ((t.Routes > 0 || explicitRoutes > 0) && (uint)t.BusType >= (uint)desc.VehicleTypes.Length)
        {
            return CreateStatus.InvalidTransit;
        }

        if ((uint)t.BayModel > (uint)BayModel.Lane || t.BayYieldTicks < 0 || t.BayYieldTicks > MaxBayYieldTicks)
        {
            return CreateStatus.InvalidTransit;
        }

        // TODO(step 4): сами автобусные маршруты и остановки строит Transit; пока они только валидируются.
        return CreateStatus.Ok;
    }

    /// <summary>Нулевые поля правил (desc, собранный вручную в тестах) заменяются значениями по умолчанию из спеки.</summary>
    internal static RulesDesc WithDefaults(RulesDesc r)
    {
        if (!(r.MinPhysicalGap > 0f))
        {
            r.MinPhysicalGap = 0.5f;
        }

        if (r.StuckThresholdTicks <= 0)
        {
            r.StuckThresholdTicks = 1200;
        }

        if (!(r.StuckSpeedThreshold > 0f))
        {
            r.StuckSpeedThreshold = 0.1f;
        }

        if (!(r.GridlockStuckShare > 0f))
        {
            r.GridlockStuckShare = 0.05f;
        }

        if (r.GridlockMinStuck <= 0)
        {
            r.GridlockMinStuck = 10;
        }

        if (r.GridlockHoldTicks <= 0)
        {
            r.GridlockHoldTicks = 1200;
        }

        if (r.MobilEveryTicks <= 0)
        {
            r.MobilEveryTicks = 10;
        }

        if (!(r.YieldGapTime > 0f))
        {
            r.YieldGapTime = Rules.Intersections.GateRules.DefaultYieldGapTime;
        }

        if (r.MaxYieldWaitTicks <= 0)
        {
            r.MaxYieldWaitTicks = Rules.Intersections.GateRules.DefaultMaxYieldWaitTicks;
        }

        if (r.SignalMinGreenTicks <= 0)
        {
            r.SignalMinGreenTicks = Rules.Intersections.SignalStore.DefaultMinGreenTicks;
        }

        if (r.SignalMaxGreenTicks <= 0)
        {
            r.SignalMaxGreenTicks = Rules.Intersections.SignalStore.DefaultMaxGreenTicks;
        }

        if (r.SignalGapTicks <= 0)
        {
            r.SignalGapTicks = Rules.Intersections.SignalStore.DefaultGapTicks;
        }

        if (r.SignalArrowTriggerTicks <= 0)
        {
            r.SignalArrowTriggerTicks = Rules.Intersections.SignalStore.DefaultArrowTriggerTicks;
        }

        if (r.SignalArrowMinTicks <= 0)
        {
            r.SignalArrowMinTicks = Rules.Intersections.SignalStore.DefaultArrowMinTicks;
        }

        if (r.SignalArrowMaxTicks <= 0)
        {
            r.SignalArrowMaxTicks = Rules.Intersections.SignalStore.DefaultArrowMaxTicks;
        }

        if (r.SignalArrowGapTicks <= 0)
        {
            r.SignalArrowGapTicks = Rules.Intersections.SignalStore.DefaultArrowGapTicks;
        }

        return r;
    }

    private static bool IsFinite(float v) => float.IsFinite(v);

    private static bool Positive(float v) => float.IsFinite(v) && v > 0f;

    private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0f;
}
