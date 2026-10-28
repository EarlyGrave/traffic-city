using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

public class ScenarioLoaderTests
{
    private const string Types = """
        {
          "format_version": 1,
          "types": [
            { "name": "car", "category": "car", "length_m": 4.5, "width_m": 1.8, "max_speed_kmh": 130, "speed_factor": 1.05,
              "idm": { "a": 1.0, "b": 1.5, "T": 1.2, "s0": 2.0, "delta": 4 },
              "mobil": { "politeness": 0.3, "threshold": 0.2, "b_safe": 4.0, "right_bias": 0.3, "duration_s": 2.0 } },
            { "name": "bus", "category": "bus", "length_m": 12.0, "width_m": 2.55, "max_speed_kmh": 80, "speed_factor": 0.95,
              "idm": { "a": 0.8, "b": 1.2, "T": 1.6, "s0": 2.5, "delta": 4 },
              "mobil": { "politeness": 0.5, "threshold": 0.3, "b_safe": 3.0, "right_bias": 0.5, "duration_s": 3.0 } }
          ]
        }
        """;

    private const string Caps = """
        "capacities": { "max_vehicles": 64, "max_nodes": 8, "max_segments": 8, "max_lanes": 64, "max_lane_points": 512,
          "max_zones": 4, "max_bus_routes": 1, "max_bus_stops": 1, "max_routes": 64,
          "max_plan_steps": 2048, "max_pending_trips": 64, "max_signalized_nodes": 1 }
        """;

    private static readonly float[] _tourX = { 0f, 100f };
    private static readonly float[] _tourZ = { 0f, 200f };
    private static readonly float[] _onlyCar = { 1f, 0f };
    private static readonly float[] _carThreeBusOne = { 3f, 1f };

    private static string Minimal(string extra = "", string version = "2") =>
        "{ \"format_version\": " + version + ", \"seed\": 7, \"duration_s\": 10, \"vehicles\": { \"count\": 5 }, "
        + Caps + (extra.Length > 0 ? ", " + extra : string.Empty) + " }";

    private static ScenarioDesc Load(string json) => ScenarioLoader.Parse(json, Types).Desc;

    private static ScenarioException Fails(string json) =>
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, Types));

    private static string ScenariosDir()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "traffic-city.sln")))
            {
                return Path.Combine(d.FullName, "scenarios");
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    [Theory]
    [InlineData("s1k")]
    [InlineData("s10k")]
    [InlineData("s100k")]
    [InlineData("s1m")]
    public void RealScenariosLoad(string name)
    {
        string path = ScenarioLocator.Resolve(name, ScenariosDir());
        LoadedScenario s = ScenarioLoader.LoadInfo(path);
        Assert.Equal(name, s.Name);
        Assert.Equal(2, s.Desc.VehicleTypes.Length);
        Assert.True(s.Desc.City.Enabled);
        Assert.True(s.Desc.Demand.TargetVehicles > 0);
        Assert.True(s.Desc.Capacities.MaxVehicles > s.Desc.Demand.TargetVehicles);
        Assert.Equal(1, s.Desc.Transit.BusType);
    }

    [Fact]
    public void S1kValues()
    {
        ScenarioDesc d = ScenarioLoader.Load(Path.Combine(ScenariosDir(), "s1k.json"));
        Assert.Equal(20261024UL, d.Seed);
        Assert.Equal(36000, d.DurationTicks);
        Assert.Equal(1000, d.Demand.TargetVehicles);
        Assert.Equal(2400, d.Demand.SpawnRampTicks);
        Assert.Equal(_onlyCar, d.Demand.TypeMix);
        Assert.Equal((float)(60 / 3.6), d.City.ArterialSpeed);
        Assert.Equal(1280, d.Capacities.MaxVehicles);
        Assert.Equal(6000, d.Transit.HeadwayTicks);
        Assert.Equal(8, d.Transit.StopsPerRouteMin);
        Assert.Equal(14, d.Transit.StopsPerRouteMax);
        Assert.Equal(160, d.Transit.DwellBaseTicks);
        Assert.Equal(30f, d.Transit.DwellPerPassengerTicks);
        Assert.Equal(0.9f, d.Transit.HoldAlpha);
        Assert.Equal(1200, d.Transit.HoldMaxTicks);
        Assert.Equal(1, d.Transit.HoldEveryN);
        Assert.Equal(1200, d.Rules.StuckThresholdTicks);
        Assert.Equal(60, d.Rules.DefaultYellowTicks);
        Assert.Equal(20, d.Rules.DefaultAllRedTicks);
    }

    [Fact]
    public void VehicleTypeUnits()
    {
        VehicleTypeDesc car = Load(Minimal(NetworkOnly())).VehicleTypes[0];
        Assert.Equal((float)(130 / 3.6), car.MaxSpeed);
        Assert.Equal(40, car.LaneChangeDurationTicks);
        Assert.Equal(1.2f, car.HeadwayTime);
        Assert.Equal(VehicleCategory.Car, car.Category);
        VehicleTypeDesc bus = Load(Minimal(NetworkOnly())).VehicleTypes[1];
        Assert.Equal(60, bus.LaneChangeDurationTicks);
        Assert.Equal(VehicleCategory.Bus, bus.Category);
    }

    private static string NetworkOnly() => """
        "city": { "enabled": false },
        "network": {
          "nodes": [ { "id": "n0", "x": 0, "z": 0, "control": "none" }, { "id": "n1", "x": 500, "z": 0, "control": "signal" } ],
          "segments": [ { "a": "n0", "b": "n1", "lanes_ab": 2, "lanes_ba": 1, "speed_kmh": 36, "kind": "arterial" } ]
        }
        """;

    [Fact]
    public void ExplicitNetworkMapsIdsAndUnits()
    {
        ScenarioDesc d = Load(Minimal(NetworkOnly()));
        Assert.False(d.City.Enabled);
        Assert.Equal(2, d.Network.Nodes.Length);
        Assert.Equal(NodeControl.Signal, d.Network.Nodes[1].Control);
        Assert.Equal(500f, d.Network.Nodes[1].X);
        SegmentDesc s = d.Network.Segments[0];
        Assert.Equal(0, s.A);
        Assert.Equal(1, s.B);
        Assert.Equal(2, s.LanesAB);
        Assert.Equal(1, s.LanesBA);
        Assert.Equal(10f, s.SpeedLimit);
        Assert.Equal(RoadKind.Arterial, s.Kind);
    }

    [Fact]
    public void SnapNodes_DefaultOffWithoutProfiles_ExplicitOnNeedsBlockMultipleOfModule()
    {
        Assert.False(Load(Minimal()).City.SnapNodes);
        Assert.False(Load(Minimal("\"city\": { \"block_m\": 150 }")).City.SnapNodes);
        Assert.True(Load(Minimal("\"city\": { \"block_m\": 152, \"size_m\": [3040, 3040], \"snap_nodes\": true }")).City.SnapNodes);
        ScenarioException e = Fails(Minimal("\"city\": { \"block_m\": 150, \"snap_nodes\": true }"));
        Assert.Contains("$.city.block_m", e.Message, StringComparison.Ordinal);
        Assert.Contains("multiple of module_m", e.Message, StringComparison.Ordinal);
        Assert.Contains("$.city.snap_nodes", Fails(Minimal("\"city\": { \"snap_nodes\": 1 }")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsApplied()
    {
        ScenarioDesc d = Load(Minimal());
        Assert.True(d.City.Enabled);
        Assert.Equal(3000f, d.City.SizeX);
        Assert.Equal(1f, d.City.Scale);
        Assert.Equal(150f, d.City.BlockSize);
        Assert.Equal(4, d.City.ArterialEvery);
        Assert.Equal((float)(60 / 3.6), d.City.ArterialSpeed);
        Assert.Equal((float)(40 / 3.6), d.City.StreetSpeed);
        Assert.Equal(12, d.City.IrregularNodes);
        Assert.Equal(CitySignals.ArterialXArterial, d.City.Signals);
        Assert.Equal(MinorControl.Yield, d.City.MinorControl);
        Assert.Equal(2400, d.Demand.SpawnRampTicks);
        Assert.Equal(_onlyCar, d.Demand.TypeMix);
        Assert.Equal(DemandModel.Gravity, d.Demand.Model);
        Assert.Equal(500f, d.Demand.ZoneSize);
        Assert.Equal(600f, d.Demand.MinTripLength);
        Assert.Equal(0, d.Transit.Routes);
        Assert.Equal(6000, d.Transit.HeadwayTicks);
        Assert.Equal(1200, d.Rules.StuckThresholdTicks);
        Assert.Equal(60, d.Rules.DefaultYellowTicks);
        Assert.Equal(20, d.Rules.DefaultAllRedTicks);
        Assert.Equal(10, d.Rules.MobilEveryTicks);
        Assert.Equal(32, d.Capacities.MaxBusStopsPerRoute);
        Assert.Equal(1024, d.Capacities.CommandQueueCapacity);
        Assert.Equal(4096, d.Capacities.EventCapacity);
        Assert.Equal(400, d.Rules.MaxYieldWaitTicks);
        Assert.Equal(8 * 16, d.Capacities.MaxConnectors);
        Assert.Equal(8 * 16 * 6, d.Capacities.MaxConflicts);
    }

    [Fact]
    public void Step3KeysAreRead()
    {
        string j = Minimal("\"rules\": { \"max_yield_wait_s\": 12.5 }")
            .Replace("\"max_nodes\": 8", "\"max_nodes\": 8, \"max_connectors\": 100, \"max_conflicts\": 321", StringComparison.Ordinal);
        ScenarioDesc d = Load(j);
        Assert.Equal(250, d.Rules.MaxYieldWaitTicks);
        Assert.Equal(100, d.Capacities.MaxConnectors);
        Assert.Equal(321, d.Capacities.MaxConflicts);

        d = Load(Minimal().Replace("\"max_nodes\": 8", "\"max_nodes\": 8, \"max_connectors\": 100", StringComparison.Ordinal));
        Assert.Equal(600, d.Capacities.MaxConflicts);
    }

    [Theory]
    [InlineData("s1k", 8192, 81920, 700, 1000)]
    [InlineData("s10k", 32000, 640000, 2800, 10000)]
    [InlineData("s100k", 250000, 7000000, 17000, 100000)]
    [InlineData("s1m", 1300000, 47000000, 170000, 1000000)]
    public void LadderCapacities(string name, int routes, int planSteps, int nodes, int vehicles)
    {
        ScenarioDesc d = ScenarioLoader.Load(Path.Combine(ScenariosDir(), name + ".json"));
        Assert.Equal(routes, d.Capacities.MaxRoutes);
        Assert.Equal(planSteps, d.Capacities.MaxPlanSteps);
        Assert.Equal(nodes, d.Capacities.MaxNodes);
        Assert.Equal(nodes * 16, d.Capacities.MaxConnectors);
        Assert.Equal(nodes * 16 * 6, d.Capacities.MaxConflicts);
        Assert.Equal(400, d.Rules.MaxYieldWaitTicks);
        Assert.Equal(vehicles, d.Demand.TargetVehicles);
    }

    [Theory]
    [InlineData("s1k", 1, 32, 48000, 10000, 600)]
    [InlineData("s10k", 1, 128, 192000, 20000, 600)]
    [InlineData("s100k", 2, 512, 400000, 100000, 600)]
    [InlineData("s1m", 3, 2048, 2000000, 2000000, 2400)]
    public void LadderRouting(string name, int levels, int pathfind, int work, int customize, int recalcTicks)
    {
        ScenarioDesc d = ScenarioLoader.Load(Path.Combine(ScenariosDir(), name + ".json"));
        RoutingDesc r = d.Routing;
        Assert.Equal(levels, r.Levels);
        Assert.Equal(1016f, r.CellSize);
        Assert.Equal(4, r.Fanout);
        Assert.Equal(16, r.WindowSteps);
        Assert.Equal(pathfind, r.PathfindBudgetPerTick);
        Assert.Equal(work, r.WorkBudgetPerTick);
        Assert.Equal(customize, r.CustomizeBudgetPerTick);
        Assert.Equal(0.05f, r.CustomizeEps);
        Assert.Equal(recalcTicks, r.CostRecalcTicks);
        Assert.Equal(0.15f, r.TimedDrift);
        Assert.Equal(400, r.TimedDriftAbsTicks);
    }

    [Fact]
    public void RoutingDefaultsWhenSectionAbsent()
    {
        RoutingDesc r = Load(Minimal()).Routing;
        Assert.Equal(1, r.Levels);
        Assert.Equal(1000f, r.CellSize);
        Assert.Equal(4, r.Fanout);
        Assert.Equal(16, r.WindowSteps);
        Assert.Equal(32, r.PathfindBudgetPerTick);
        Assert.Equal(1500 * 32, r.WorkBudgetPerTick);
        Assert.Equal(10000, r.CustomizeBudgetPerTick);
        Assert.Equal(0.05f, r.CustomizeEps);
        Assert.Equal(600, r.CostRecalcTicks);
        Assert.Equal(0.15f, r.TimedDrift);
        Assert.Equal(400, r.TimedDriftAbsTicks);
    }

    [Fact]
    public void RoutingKeysAreReadWithUnits()
    {
        RoutingDesc r = Load(Minimal(
            "\"routing\": { \"levels\": 3, \"cell_m\": 300, \"fanout\": 2, \"window_steps\": 12, \"pathfind_budget_per_tick\": 5, "
            + "\"customize_budget_per_tick\": 77, \"customize_eps\": 0.1, \"cost_recalc_s\": 15, \"timed_drift\": 0.3, \"timed_drift_abs_s\": 10 }"))
            .Routing;
        Assert.Equal(3, r.Levels);
        Assert.Equal(300f, r.CellSize);
        Assert.Equal(2, r.Fanout);
        Assert.Equal(12, r.WindowSteps);
        Assert.Equal(5, r.PathfindBudgetPerTick);
        Assert.Equal(7500, r.WorkBudgetPerTick); // дефолт 1500 * pathfind_budget_per_tick
        Assert.Equal(77, r.CustomizeBudgetPerTick);
        Assert.Equal(0.1f, r.CustomizeEps);
        Assert.Equal(300, r.CostRecalcTicks);
        Assert.Equal(0.3f, r.TimedDrift);
        Assert.Equal(200, r.TimedDriftAbsTicks);

        ScenarioDesc d = Load(Minimal("\"routing\": { \"levels\": 0, \"work_budget_per_tick\": 9 }"));
        Assert.Equal(0, d.Routing.Levels);
        Assert.Equal(9, d.Routing.WorkBudgetPerTick);
    }

    [Fact]
    public void RoutingErrorsReportPath()
    {
        ScenarioException e = Fails(Minimal("\"routing\": { \"levels\": 5 }"));
        Assert.Contains("$.routing.levels", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"routing\": { \"fanout\": 1 }"));
        Assert.Contains("$.routing.fanout", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"routing\": { \"window_steps\": 40 }"));
        Assert.Contains("$.routing.window_steps", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"routing\": { \"cell_mm\": 1 }"));
        Assert.Contains("$.routing.cell_mm", e.Message, StringComparison.Ordinal);

        // pathfind_budget_per_tick переехал из capacities в routing (format_version 2).
        e = Fails(Minimal().Replace("\"max_nodes\": 8", "\"max_nodes\": 8, \"pathfind_budget_per_tick\": 8", StringComparison.Ordinal));
        Assert.Contains("$.capacities.pathfind_budget_per_tick", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxPlanStepsDefaultsFromRoutesAndLevels()
    {
        string noPlan = Minimal().Replace("\"max_plan_steps\": 2048, ", string.Empty, StringComparison.Ordinal);
        Assert.Equal(64 * (12 + 8), Load(noPlan).Capacities.MaxPlanSteps);
        Assert.Equal(64 * (12 + 24), Load(noPlan.Replace("\"capacities\"", "\"routing\": { \"levels\": 3 }, \"capacities\"", StringComparison.Ordinal)).Capacities.MaxPlanSteps);
        Assert.Equal(2048, Load(Minimal()).Capacities.MaxPlanSteps);
        ScenarioException e = Fails(Minimal().Replace("\"max_plan_steps\": 2048", "\"max_plan_steps\": 300000000", StringComparison.Ordinal));
        Assert.Contains("$.capacities.max_plan_steps", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllScenarioFilesLoad()
    {
        foreach (string f in Directory.GetFiles(ScenariosDir(), "*.json"))
        {
            if (Path.GetFileName(f) is "vehicle-types.json" or "road-profiles.json")
            {
                continue;
            }

            ScenarioLoader.Load(f);
        }
    }

    [Fact]
    public void IrregularNodesDefaultScalesWithScaleSquared()
    {
        Assert.Equal(48, Load(Minimal("\"city\": { \"scale\": 2.0 }")).City.IrregularNodes);
        Assert.Equal(7, Load(Minimal("\"city\": { \"scale\": 2.0, \"irregular_nodes\": 7 }")).City.IrregularNodes);
    }

    [Fact]
    public void NodeJitterDefaultsToZeroAndIsRead()
    {
        Assert.Equal(0f, Load(Minimal("\"city\": { \"scale\": 1.0 }")).City.NodeJitter);
        Assert.Equal(5f, Load(Minimal("\"city\": { \"node_jitter_m\": 5 }")).City.NodeJitter);
    }

    [Fact]
    public void City_SignalModeAndCoordination_DefaultsAndErrors()
    {
        CityGenDesc c = Load(Minimal("\"city\": { \"scale\": 1.0 }")).City;
        Assert.Equal(CitySignalMode.Adaptive, c.SignalMode);   // 2.5 S2: дефолт спеки
        Assert.Equal(CitySignalCoordination.Legacy, c.SignalCoordination);   // 2.5 S3: alternate - опция, дефолт legacy
        c = Load(Minimal("\"city\": { \"signal_mode\": \"fixed\", \"signal_coordination\": \"legacy\" }")).City;
        Assert.Equal(CitySignalMode.Fixed, c.SignalMode);
        Assert.Equal(CitySignalCoordination.Legacy, c.SignalCoordination);
        Assert.Equal(CitySignalCoordination.Alternate, Load(Minimal("\"city\": { \"signal_coordination\": \"alternate\" }")).City.SignalCoordination);
        Assert.Equal(CitySignalCoordination.None, Load(Minimal("\"city\": { \"signal_coordination\": \"none\" }")).City.SignalCoordination);
        c = Load(Minimal("\"city\": { \"signal_mode\": \"adaptive\" }")).City;
        Assert.Equal(CitySignalMode.Adaptive, c.SignalMode);
        Assert.Contains("$.city.signal_coordination", Fails(Minimal("\"city\": { \"signal_coordination\": \"x\" }")).Message, StringComparison.Ordinal);
        Assert.Contains("expected", Fails(Minimal("\"city\": { \"signal_mode\": \"x\" }")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeMixMapsNamesToIndices()
    {
        string j = Minimal().Replace("\"count\": 5", "\"count\": 5, \"type_mix\": { \"bus\": 1, \"car\": 3 }", StringComparison.Ordinal);
        Assert.Equal(_carThreeBusOne, Load(j).Demand.TypeMix);
    }

    [Fact]
    public void UnknownKeyReportsPath()
    {
        ScenarioException e = Fails(Minimal("\"city\": { \"scal\": 2 }"));
        Assert.Contains("$.city.scal", e.Message, StringComparison.Ordinal);
        Assert.Contains("unknown key", e.Message, StringComparison.Ordinal);

        e = Fails(Minimal().Replace("\"max_nodes\": 8", "\"max_nodes\": 8, \"bogus\": 1", StringComparison.Ordinal));
        Assert.Contains("$.capacities.bogus", e.Message, StringComparison.Ordinal);

        e = Fails(Minimal().Replace("\"seed\"", "\"extra\": 1, \"seed\"", StringComparison.Ordinal));
        Assert.Contains("$.extra", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongTypeReportsPath()
    {
        ScenarioException e = Fails(Minimal("\"city\": { \"scale\": \"big\" }"));
        Assert.Equal("$.city.scale: expected number", e.Message);
    }

    [Fact]
    public void MissingRequiredKeyIsError()
    {
        ScenarioException e = Fails(Minimal().Replace("\"seed\": 7, ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("$.seed: required key is missing", e.Message);

        e = Fails(Minimal().Replace("\"max_vehicles\": 64, ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("$.capacities.max_vehicles: required key is missing", e.Message);

        e = Fails(Minimal().Replace("\"vehicles\": { \"count\": 5 }, ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("$.vehicles: required key is missing", e.Message);
    }

    [Fact]
    public void WrongFormatVersionIsError()
    {
        ScenarioException e = Fails(Minimal(version: "1")); // формат 1 больше не принимается (ADR-0009)
        Assert.Contains("$.format_version", e.Message, StringComparison.Ordinal);

        e = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Minimal(), Types.Replace("\"format_version\": 1", "\"format_version\": 9", StringComparison.Ordinal)));
        Assert.Contains("$.format_version", e.Message, StringComparison.Ordinal);

        e = Fails(Minimal().Replace("\"format_version\": 2, ", string.Empty, StringComparison.Ordinal));
        Assert.Contains("$.format_version", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownVehicleTypeReferenceIsError()
    {
        string j = Minimal().Replace("\"count\": 5", "\"count\": 5, \"type_mix\": { \"truck\": 1 }", StringComparison.Ordinal);
        ScenarioException e = Fails(j);
        Assert.Contains("$.vehicles.type_mix.truck", e.Message, StringComparison.Ordinal);

        e = Fails(Minimal("\"transit\": { \"routes\": 1, \"bus_type\": \"tram\" }"));
        Assert.Contains("$.transit.bus_type", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkWithEnabledCityIsError()
    {
        ScenarioException e = Fails(Minimal(NetworkOnly().Replace("\"city\": { \"enabled\": false },", string.Empty, StringComparison.Ordinal)));
        Assert.Contains("$.network", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownNodeReferenceIsError()
    {
        ScenarioException e = Fails(Minimal(NetworkOnly().Replace("\"b\": \"n1\"", "\"b\": \"zz\"", StringComparison.Ordinal)));
        Assert.Contains("$.network.segments[0].b", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonIsScenarioError()
    {
        Fails("{ nope");
    }

    [Fact]
    public void ValidateCommandExitCodes()
    {
        var ok = new StringWriter();
        var err = new StringWriter();
        int code = ValidateCommand.Run(new[] { "validate", "--scenario", "s1k", "--scenarios-dir", ScenariosDir() }, ok, err);
        Assert.Equal(0, code);
        Assert.Contains("s1k", ok.ToString(), StringComparison.Ordinal);

        code = ValidateCommand.Run(new[] { "validate", "--scenario", "nope", "--scenarios-dir", ScenariosDir() }, ok, err);
        Assert.Equal(2, code);
        Assert.Contains("not found", err.ToString(), StringComparison.Ordinal);

        code = ValidateCommand.Run(["validate"], ok, err);
        Assert.Equal(2, code);
    }

    [Fact]
    public void LodDefaultsWhenSectionAbsent_ModeOff()
    {
        LodDesc l = Load(Minimal()).Lod;
        Assert.Equal(LodMode.Off, l.Mode);
        Assert.Equal(1000f, l.CellM);
        Assert.Equal(15000, l.MicroBudget);
        Assert.Equal(100, l.EvalTicks);
        Assert.Equal(600, l.HoldTicks);
        Assert.Equal(1, l.FocusMarginCells);
        Assert.True(l.FollowSelected);
        Assert.True(l.FocusCommands);
        Assert.Equal(0.1f, l.CongestionShare);
        Assert.Equal(10, l.CongestionMin);
        Assert.Equal(0.8f, l.MesoHeadwayScale);
        Assert.Equal(1f, l.MesoAccelScale);
        Assert.Null(l.Focus);
        Assert.Null(l.Pinned);
        Assert.Equal(0, l.Tour.PeriodTicks);

        // Пустая секция - те же дефолты, режим off.
        Assert.Equal(LodMode.Off, Load(Minimal("\"lod\": { }")).Lod.Mode);
    }

    [Fact]
    public void LodKeysAreReadWithUnits()
    {
        LodDesc l = Load(Minimal(
            "\"lod\": { \"mode\": \"auto\", \"cell_m\": 500, \"micro_budget\": 3000, \"eval_s\": 2, \"hold_s\": 10, "
            + "\"focus_margin_cells\": 2, \"follow_selected\": false, \"focus_commands\": false, \"congestion_share\": 0.2, "
            + "\"congestion_min\": 7, \"meso_headway_scale\": 1.1, \"meso_accel_scale\": 0.9, "
            + "\"focus\": [ { \"x\": 10, \"z\": 20, \"radius_m\": 1500 } ], "
            + "\"pinned\": [ { \"x\": 1, \"z\": 2, \"radius_m\": 0 }, { \"x\": 3, \"z\": 4, \"radius_m\": 5 } ], "
            + "\"tour\": { \"period_s\": 10, \"radius_m\": 1500, \"path\": [[0, 0], [100, 200]] } }")).Lod;
        Assert.Equal(LodMode.Auto, l.Mode);
        Assert.Equal(500f, l.CellM);
        Assert.Equal(3000, l.MicroBudget);
        Assert.Equal(40, l.EvalTicks);
        Assert.Equal(200, l.HoldTicks);
        Assert.Equal(2, l.FocusMarginCells);
        Assert.False(l.FollowSelected);
        Assert.False(l.FocusCommands);
        Assert.Equal(0.2f, l.CongestionShare);
        Assert.Equal(7, l.CongestionMin);
        Assert.Equal(1.1f, l.MesoHeadwayScale);
        Assert.Equal(0.9f, l.MesoAccelScale);
        Assert.Single(l.Focus!);
        Assert.Equal(10f, l.Focus![0].X);
        Assert.Equal(20f, l.Focus[0].Z);
        Assert.Equal(1500f, l.Focus[0].RadiusM);
        Assert.Equal(2, l.Pinned!.Length);
        Assert.Equal(4f, l.Pinned[1].Z);
        Assert.Equal(200, l.Tour.PeriodTicks);
        Assert.Equal(1500f, l.Tour.RadiusM);
        Assert.Equal(_tourX, l.Tour.PathX);
        Assert.Equal(_tourZ, l.Tour.PathZ);

        Assert.Equal(LodMode.AllMeso, Load(Minimal("\"lod\": { \"mode\": \"all_meso\" }")).Lod.Mode);
        Assert.Equal(LodMode.Off, Load(Minimal("\"lod\": { \"mode\": \"off\", \"cell_m\": 250 }")).Lod.Mode);
        Assert.Equal(MesoModel.Kinematic, Load(Minimal("\"lod\": { \"mode\": \"all_meso\" }")).Lod.MesoModel);
        LodDesc q = Load(Minimal("\"lod\": { \"mode\": \"all_meso\", \"meso_model\": \"queue\", \"queue_headway_scale\": 0.7 }")).Lod;
        Assert.Equal(MesoModel.Queue, q.MesoModel);
        Assert.Equal(0.7f, q.QueueHeadwayScale);
    }

    [Fact]
    public void LodQueueModel_ErrorsReportPath()
    {
        ScenarioException e = Fails(Minimal("\"lod\": { \"mode\": \"all_meso\", \"meso_model\": \"cells\" }"));
        Assert.Contains("$.lod.meso_model", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"mode\": \"auto\", \"meso_model\": \"queue\" }"));
        Assert.Contains("$.lod.meso_model", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LodErrorsReportPath()
    {
        ScenarioException e = Fails(Minimal("\"lod\": { \"mode\": \"fast\" }"));
        Assert.Contains("$.lod.mode", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"cell_m\": 0 }"));
        Assert.Contains("$.lod.cell_m", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"micro_budget\": -1 }"));
        Assert.Contains("$.lod.micro_budget", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"congestion_share\": 1.5 }"));
        Assert.Contains("$.lod.congestion_share", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"follow_selected\": 1 }"));
        Assert.Contains("$.lod.follow_selected", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"cell_mm\": 1 }"));
        Assert.Contains("$.lod.cell_mm", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"focus\": [ { \"x\": 1, \"z\": 2 } ] }"));
        Assert.Contains("$.lod.focus[0].radius_m", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"focus\": [ { \"x\": 1, \"z\": 2, \"radius_m\": 3, \"y\": 0 } ] }"));
        Assert.Contains("$.lod.focus[0].y", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"pinned\": [ {\"x\":0,\"z\":0,\"radius_m\":1}, {\"x\":0,\"z\":0,\"radius_m\":1}, {\"x\":0,\"z\":0,\"radius_m\":1}, "
            + "{\"x\":0,\"z\":0,\"radius_m\":1}, {\"x\":0,\"z\":0,\"radius_m\":1} ] }"));
        Assert.Contains("$.lod.pinned", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"tour\": { \"period_s\": 5, \"radius_m\": 100, \"path\": [] } }"));
        Assert.Contains("$.lod.tour.path", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"tour\": { \"period_s\": 5, \"radius_m\": 100, \"path\": [[1]] } }"));
        Assert.Contains("$.lod.tour.path[0]", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"lod\": { \"tour\": { \"radius_m\": 100, \"path\": [[1, 2]] } }"));
        Assert.Contains("$.lod.tour.period_s", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("s1k")]
    [InlineData("s10k")]
    public void LadderScenariosWithoutLodSection_AreModeOff(string name)
    {
        // До шага L5 лестница без секции lod: поведение и хэш прежние.
        LoadedScenario sc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null));
        Assert.Equal(LodMode.Off, sc.Desc.Lod.Mode);
    }

    [Fact]
    public void ToolsSection_DefaultsAndOverrides()
    {
        // Нет секции: заглушки таблицы стоимостей и границы по умолчанию (bbox сети + 2 км).
        ScenarioDesc d = Load(Minimal());
        Assert.False(d.Tools.HasWorldBounds);
        Assert.Equal(10L, d.Tools.Costs.RoadPerMLaneStreet);
        Assert.Equal(20L, d.Tools.Costs.RoadPerMLaneArterial);
        Assert.Equal(50, d.Tools.Costs.DemolishRefundPct);

        d = Load(Minimal("\"tools\": { \"costs\": { \"road_per_m_lane\": { \"street\": 7 }, \"turn_ban\": 3, \"demolish_refund_pct\": 80 }, "
            + "\"world_bounds\": { \"min_x\": -100, \"min_z\": -200, \"max_x\": 300, \"max_z\": 400 } }"));
        Assert.Equal(7L, d.Tools.Costs.RoadPerMLaneStreet);
        Assert.Equal(20L, d.Tools.Costs.RoadPerMLaneArterial);
        Assert.Equal(3L, d.Tools.Costs.TurnBan);
        Assert.Equal(80, d.Tools.Costs.DemolishRefundPct);
        Assert.True(d.Tools.HasWorldBounds);
        Assert.Equal(-200f, d.Tools.BoundsMinZ);
        Assert.Equal(300f, d.Tools.BoundsMaxX);

        ScenarioException e = Fails(Minimal("\"tools\": { \"costs\": { \"demolish_refund_pct\": 101 } }"));
        Assert.Contains("$.tools.costs.demolish_refund_pct", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"tools\": { \"world_bounds\": { \"min_x\": 5, \"min_z\": 0, \"max_x\": 1, \"max_z\": 1 } }"));
        Assert.Contains("$.tools.world_bounds", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"tools\": { \"unknown\": 1 }"));
        Assert.Contains("$.tools.unknown", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CityLandUse_ParsesDefaultsAndValues_DisabledByDefault()
    {
        Assert.False(Load(Minimal()).City.LandUse.Enabled);
        Assert.False(Load(Minimal("\"city\": { \"landuse\": { \"enabled\": false, \"park_share\": 0.5 } }")).City.LandUse.Enabled);

        LandUseDesc d = Load(Minimal("\"city\": { \"landuse\": { \"enabled\": true } }")).City.LandUse;
        Assert.True(d.Enabled);
        Assert.Equal(0.08f, d.ParkShare);
        Assert.Equal(0.25f, d.PondShare);
        Assert.Equal(LandUseCoast.None, d.Coast);
        Assert.Equal(400f, d.CoastWidthM);

        d = Load(Minimal("\"city\": { \"landuse\": { \"enabled\": true, \"park_share\": 0.2, \"pond_share\": 0, \"coast\": \"east\", \"coast_width_m\": 240 } }")).City.LandUse;
        Assert.Equal(0.2f, d.ParkShare);
        Assert.Equal(0f, d.PondShare);
        Assert.Equal(LandUseCoast.East, d.Coast);
        Assert.Equal(240f, d.CoastWidthM);
    }

    [Fact]
    public void CityLandUse_RejectsBadValues_AndExplicitNetwork()
    {
        ScenarioException e = Fails(Minimal("\"city\": { \"landuse\": { \"enabled\": true, \"coast\": \"up\" } }"));
        Assert.Contains("$.city.landuse.coast", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"city\": { \"landuse\": { \"enabled\": true, \"park_share\": 1.5 } }"));
        Assert.Contains("$.city.landuse.park_share", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"city\": { \"landuse\": { \"enabled\": true, \"nope\": 1 } }"));
        Assert.Contains("$.city.landuse.nope", e.Message, StringComparison.Ordinal);
        e = Fails(Minimal("\"city\": { \"enabled\": false, \"landuse\": { \"enabled\": true } }, \"network\": { \"nodes\": [], \"segments\": [] }"));
        Assert.Contains("$.city.landuse", e.Message, StringComparison.Ordinal);
    }
}
