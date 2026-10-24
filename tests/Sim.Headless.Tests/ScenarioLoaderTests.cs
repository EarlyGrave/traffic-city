using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Headless.Scenarios;

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
          "max_zones": 4, "max_bus_routes": 1, "max_bus_stops": 1, "max_routes": 64, "max_route_steps": 4096,
          "max_pending_trips": 64, "pathfind_budget_per_tick": 8, "max_signalized_nodes": 1 }
        """;

    private static readonly float[] _onlyCar = { 1f, 0f };
    private static readonly float[] _carThreeBusOne = { 3f, 1f };

    private static string Minimal(string extra = "", string version = "1") =>
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
    }

    [Fact]
    public void IrregularNodesDefaultScalesWithScaleSquared()
    {
        Assert.Equal(48, Load(Minimal("\"city\": { \"scale\": 2.0 }")).City.IrregularNodes);
        Assert.Equal(7, Load(Minimal("\"city\": { \"scale\": 2.0, \"irregular_nodes\": 7 }")).City.IrregularNodes);
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
        ScenarioException e = Fails(Minimal(version: "2"));
        Assert.Contains("$.format_version", e.Message, StringComparison.Ordinal);

        e = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Minimal(), Types.Replace("\"format_version\": 1", "\"format_version\": 9", StringComparison.Ordinal)));
        Assert.Contains("$.format_version", e.Message, StringComparison.Ordinal);

        e = Fails(Minimal().Replace("\"format_version\": 1, ", string.Empty, StringComparison.Ordinal));
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
}
