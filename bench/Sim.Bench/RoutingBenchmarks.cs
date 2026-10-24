using System;
using System.IO;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Util;

namespace Sim.Bench;

/// <summary>
/// Маршрутизация (routing.md): A* на случайной паре DirSeg, запрос по оверлею (H2) и плоский на IndexedHeap4, полный маршрут (запрос + развёртывание + раскладка). Сеть генерируется из scenarios/{s1k,s100k}.json (как GenFixture в тестах).
/// </summary>
[MemoryDiagnoser]
public class RoutingBenchmarks
{
    private const int Pairs = 256;

    private NetworkStore _net = null!;
    private RouteGraph _graph = null!;
    private AStarSearcher _star = null!;
    private OverlaySearcher _overlayQuery = null!;
    private OverlaySearcher _flatQuery = null!;
    private int[] _plan = null!;
    private int[] _path = null!;
    private readonly int[] _starts1 = new int[1];
    private readonly float[] _startG1 = new float[1];
    private readonly int[] _pairStart = new int[Pairs];
    private readonly int[] _pairGoal = new int[Pairs];
    private readonly int[] _pairStartLane = new int[Pairs];
    private readonly int[] _pairGoalLane = new int[Pairs];
    private readonly RouteStep[] _steps = new RouteStep[4096];
    private int _i;

    [Params("s1k", "s10k", "s100k")]
    public string Scenario { get; set; } = "s1k";

    [GlobalSetup]
    public void Setup()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "traffic-city.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "scenarios", Scenario + ".json")));
        JsonElement root = doc.RootElement;
        ulong seed = root.GetProperty("seed").GetUInt64();
        JsonElement c = root.GetProperty("city");
        JsonElement size = c.GetProperty("size_m");
        var city = new CityGenDesc
        {
            Enabled = true,
            SizeX = size[0].GetSingle(),
            SizeZ = size[1].GetSingle(),
            Scale = c.GetProperty("scale").GetSingle(),
            BlockSize = c.GetProperty("block_m").GetSingle(),
            ArterialEvery = c.GetProperty("arterial_every").GetInt32(),
            ArterialSpeed = c.GetProperty("arterial_speed_kmh").GetSingle() / 3.6f,
            StreetSpeed = c.GetProperty("street_speed_kmh").GetSingle() / 3.6f,
            LaneWidth = c.GetProperty("lane_width_m").GetSingle(),
            IrregularNodes = c.GetProperty("irregular_nodes").GetInt32(),
            IrregularJitter = c.GetProperty("irregular_jitter_m").GetSingle(),
            RemovedStreetShare = c.GetProperty("removed_street_share").GetSingle(),
            ConnectorSamples = c.GetProperty("connector_samples").GetInt32(),
            Signals = c.GetProperty("signals").GetString() switch
            {
                "all_arterial" => CitySignals.AllArterial,
                "none" => CitySignals.None,
                _ => CitySignals.ArterialXArterial,
            },
            MinorControl = c.GetProperty("minor_control").GetString() switch
            {
                "stop" => MinorControl.Stop,
                "right_hand" => MinorControl.RightHand,
                _ => MinorControl.Yield,
            },
        };
        JsonElement r = root.GetProperty("rules");
        var rules = new RulesDesc
        {
            DefaultYellowTicks = (int)(r.GetProperty("signal_yellow_s").GetSingle() * 20f),
            DefaultAllRedTicks = (int)(r.GetProperty("signal_all_red_s").GetSingle() * 20f),
        };
        JsonElement k = root.GetProperty("capacities");
        var caps = new SimCapacities
        {
            MaxNodes = k.GetProperty("max_nodes").GetInt32(),
            MaxSegments = k.GetProperty("max_segments").GetInt32(),
            MaxLanes = k.GetProperty("max_lanes").GetInt32(),
            MaxLanePoints = k.GetProperty("max_lane_points").GetInt32(),
            MaxZones = k.GetProperty("max_zones").GetInt32(),
            MaxBusRoutes = k.GetProperty("max_bus_routes").GetInt32(),
            MaxBusStops = k.GetProperty("max_bus_stops").GetInt32(),
            MaxBusStopsPerRoute = k.GetProperty("max_bus_stops_per_route").GetInt32(),
            MaxSignalizedNodes = k.GetProperty("max_signalized_nodes").GetInt32(),
        };
        JsonElement d = root.GetProperty("demand");
        var demand = new DemandDesc
        {
            ZoneSize = d.GetProperty("zone_m").GetSingle(),
            WeightJitter = d.GetProperty("weight_jitter").GetSingle(),
        };

        _net = new NetworkStore(caps);
        var builder = new NetworkBuilder(_net, city.LaneWidth, city.ConnectorSamples);
        Pcg32 rngCity = Pcg32.ForStream(seed, RngStream.CityGen);
        Pcg32 rngZones = Pcg32.ForStream(seed, RngStream.Zones);
        if (CityGenerator.Generate(city, rules, caps, builder, ref rngCity, out CityGenResult cr) != CreateStatus.Ok)
        {
            throw new InvalidOperationException("city generation failed");
        }

        var zones = new GenZones(caps.MaxZones, caps.MaxLanes);
        if (ZoneGenerator.Generate(demand, cr.Width, cr.Height, _net, zones, ref rngZones, out _) != CreateStatus.Ok)
        {
            throw new InvalidOperationException("zone generation failed");
        }

        Array.Copy(zones.Zones, _net.Zones, zones.ZoneCount);
        Array.Copy(zones.ZoneLanes, _net.ZoneLanes, zones.ZoneLaneCount);
        _net.ZoneCount = zones.ZoneCount;
        _net.ZoneLaneCount = zones.ZoneLaneCount;

        _graph = new RouteGraph(_net);
        _graph.Rebuild();
        _star = new AStarSearcher(_graph);
        _path = new int[_graph.DirSegCount + 1];
        _plan = new int[_graph.DirSegCount + 8];
        var routing = new RoutingDesc { Levels = Scenario == "s100k" ? 2 : 1, Fanout = 4, CellSize = 1000f, CustomizeEps = 0.05f };
        if (Overlay.TryCreate(_net, _graph, in routing, out Overlay? overlay) != CreateStatus.Ok)
        {
            throw new InvalidOperationException("overlay creation failed");
        }

        _overlayQuery = new OverlaySearcher(_net, _graph, overlay);
        _flatQuery = new OverlaySearcher(_net, _graph, null);

        var rng = Pcg32.Create(2026, 1);
        for (int i = 0; i < Pairs; i++)
        {
            _pairStartLane[i] = RandomLane(ref rng);
            _pairGoalLane[i] = RandomLane(ref rng);
            _pairStart[i] = RouteGraph.DirSegOfLane(_net, _pairStartLane[i]);
            _pairGoal[i] = RouteGraph.DirSegOfLane(_net, _pairGoalLane[i]);
        }
    }

    [Benchmark]
    public float AStarRandomPair()
    {
        int i = _i++ & (Pairs - 1);
        RouteGraph g = _graph;
        int s = _pairStart[i];
        _starts1[0] = s;
        _startG1[0] = g.SegCost[s];
        int goal = _pairGoal[i];
        _star.Search(_starts1, _startG1, goal, -1, g.EndPos[goal], g.DirSegCount, _path, out _);
        return _star.LastCost;
    }

    /// <summary>Запрос по оверлею (план без развёртывания) на той же паре, что <see cref="AStarRandomPair"/>.</summary>
    [Benchmark]
    public float OverlayQueryRandomPair()
    {
        int i = _i++ & (Pairs - 1);
        _overlayQuery.Query(_pairStartLane[i], 0f, _pairGoalLane[i], 0f, _plan, out _);
        return _overlayQuery.PlanCost;
    }

    /// <summary>Плоский поиск на IndexedHeap4 (эталон H2: тот же код без оверлея).</summary>
    [Benchmark]
    public float FlatIndexedQueryRandomPair()
    {
        int i = _i++ & (Pairs - 1);
        _flatQuery.Query(_pairStartLane[i], 0f, _pairGoalLane[i], 0f, _plan, out _);
        return _flatQuery.PlanCost;
    }

    /// <summary>Полный маршрут по оверлею: запрос, развёртывание плана и раскладка на полосы.</summary>
    [Benchmark]
    public int OverlayRouteRandomPair()
    {
        int i = _i++ & (Pairs - 1);
        _overlayQuery.Route(_pairStartLane[i], 0f, _pairGoalLane[i], 0f, _steps, out int n);
        return n;
    }

    private int RandomLane(ref Pcg32 rng)
    {
        while (true)
        {
            int lane = rng.NextInt(0, _net.LaneCount);
            if (_net.Lanes[lane].Kind == LaneKind.Normal && _net.Lanes[lane].Segment >= 0)
            {
                int u = RouteGraph.DirSegOfLane(_net, lane);
                if (_graph.ArcFirst[u + 1] > _graph.ArcFirst[u])
                {
                    return lane;
                }
            }
        }
    }
}
