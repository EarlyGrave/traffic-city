using System;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>Секция сценария <c>peds</c> и ключи <c>transit.passenger_model</c>/<c>bidirectional</c> (scenario-format.md §2.4, pedestrians.md §12).</summary>
public class PedsScenarioTests
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

    private static string Doc(string extra = "", string capsExtra = "") =>
        "{ \"format_version\": 2, \"seed\": 7, \"duration_s\": 10, \"vehicles\": { \"count\": 5 }, "
        + "\"capacities\": { \"max_vehicles\": 64, \"max_nodes\": 8, \"max_segments\": 8, \"max_lanes\": 64, \"max_lane_points\": 512, "
        + "\"max_zones\": 4, \"max_bus_routes\": 1, \"max_bus_stops\": 1, \"max_routes\": 64, "
        + "\"max_plan_steps\": 2048, \"max_pending_trips\": 64, \"max_signalized_nodes\": 1" + (capsExtra.Length > 0 ? ", " + capsExtra : string.Empty) + " }"
        + (extra.Length > 0 ? ", " + extra : string.Empty) + " }";

    private static ScenarioDesc Load(string json) => ScenarioLoader.Parse(json, Types).Desc;

    private static ScenarioException Fails(string json) => Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, Types));

    private const string PedCaps = "\"max_peds\": 130, \"max_residents\": 200";

    [Fact]
    public void NoPedsSection_IsDisabled_CounterModel_AndZeroPedCapacities()
    {
        ScenarioDesc d = Load(Doc());
        Assert.False(d.Peds.Enabled);
        Assert.Equal(default, d.Peds);
        Assert.Equal(PassengerModel.Counter, d.Transit.PassengerModel);
        Assert.False(d.Transit.Bidirectional);
        Assert.Equal(0, d.Capacities.MaxPeds);
        Assert.Equal(0, d.Capacities.MaxResidents);
        Assert.Equal(0, d.Capacities.MaxPedPathSteps);
    }

    [Fact]
    public void PedsSection_AppliesDefaultsAndConvertsUnits()
    {
        ScenarioDesc d = Load(Doc("\"peds\": { \"count\": 100 }", PedCaps));
        PedDesc p = d.Peds;
        Assert.True(p.Enabled);
        Assert.Equal(100, p.Count);
        Assert.Equal(200, p.Residents);                  // 2 · count
        Assert.Equal(120 * 20, p.SpawnRampTicks);
        Assert.Equal(56, p.SpeedMinMmPerTick);           // round(4.0 / 3.6 · 50)
        Assert.Equal(75, p.SpeedMaxMmPerTick);           // round(5.4 / 3.6 · 50)
        Assert.Equal(600 * 20, p.ActivityMinTicks);
        Assert.Equal(1800 * 20, p.ActivityMaxTicks);
        Assert.Equal(800_000, p.WalkOnlyMm);
        Assert.Equal(500_000, p.MaxWalkToStopMm);
        Assert.Equal(1_500_000, p.MaxWalkMm);
        Assert.Equal(3000, p.SidewalkWidthMm);
        Assert.Equal(3000, p.CrosswalkWidthMm);
        Assert.Equal(ArterialCrossings.SignalOnly, p.ArterialCrossings);
        Assert.True(p.MidblockAtStops);
        Assert.Equal(5 * 20, p.WalkTicks);
        Assert.Equal(60, p.ClearSpeedMmPerTick);         // round(4.3 / 3.6 · 50)
        Assert.Equal(10 * 20, p.MaxCloseTicks);
        Assert.Equal(5 * 20, p.CarWindowTicks);
        Assert.Equal(8, p.PathfindBudgetPerTick);
        Assert.Equal(8000, p.WorkBudgetPerTick);
        Assert.Equal(120 * 20, p.StuckCrossTicks);
        Assert.Equal(900 * 20, p.StuckStopTicks);        // max(3 · 300 с, 900 с) при headway 300 с
        Assert.Equal(PassengerModel.Agents, d.Transit.PassengerModel);
        Assert.Equal(130, d.Capacities.MaxPeds);
        Assert.Equal(200, d.Capacities.MaxResidents);
        Assert.Equal(64, d.Capacities.MaxPedPathSteps);
    }

    [Fact]
    public void PedsSection_ExplicitValues_AreParsed()
    {
        string peds = "\"peds\": { \"count\": 10, \"residents\": 15, \"speed_kmh\": [3.6, 7.2], \"activity_s\": [10, 20], \"walk_only_m\": 400, "
            + "\"sidewalk_width_m\": 2.5, \"crosswalk_width_m\": 4, \"arterial_crossings\": \"all\", \"midblock_at_stops\": false, \"walk_s\": 7, "
            + "\"max_close_s\": 12, \"car_window_s\": 3, \"stuck_cross_s\": 90, \"stuck_stop_s\": 1500 }";
        ScenarioDesc d = Load(Doc(peds, "\"max_peds\": 13, \"max_residents\": 15, \"max_ped_path_steps\": 32"));
        PedDesc p = d.Peds;
        Assert.Equal(15, p.Residents);
        Assert.Equal(50, p.SpeedMinMmPerTick);           // 1 м/с · 50
        Assert.Equal(100, p.SpeedMaxMmPerTick);
        Assert.Equal(200, p.ActivityMinTicks);
        Assert.Equal(400, p.ActivityMaxTicks);
        Assert.Equal(400_000, p.WalkOnlyMm);
        Assert.Equal(2500, p.SidewalkWidthMm);
        Assert.Equal(4000, p.CrosswalkWidthMm);
        Assert.Equal(ArterialCrossings.All, p.ArterialCrossings);
        Assert.False(p.MidblockAtStops);
        Assert.Equal(140, p.WalkTicks);
        Assert.Equal(240, p.MaxCloseTicks);
        Assert.Equal(60, p.CarWindowTicks);
        Assert.Equal(1800, p.StuckCrossTicks);
        Assert.Equal(30_000, p.StuckStopTicks);
        Assert.Equal(32, d.Capacities.MaxPedPathSteps);
    }

    [Fact]
    public void StuckStopDefault_FollowsTransitHeadway()
    {
        ScenarioDesc d = Load(Doc("\"peds\": { \"count\": 1 }, \"transit\": { \"headway_s\": 600 }", "\"max_peds\": 2, \"max_residents\": 2"));
        Assert.Equal(1800 * 20, d.Peds.StuckStopTicks);   // 3 · 600 > 900
    }

    [Fact]
    public void DisabledPedsSection_KeepsEverythingOff()
    {
        ScenarioDesc d = Load(Doc("\"peds\": { \"enabled\": false, \"count\": 5 }"));
        Assert.False(d.Peds.Enabled);
        Assert.Equal(PassengerModel.Counter, d.Transit.PassengerModel);
        Assert.Equal(0, d.Capacities.MaxPeds);
    }

    [Fact]
    public void PassengerModel_AgentsWithoutPeds_IsRejected()
    {
        ScenarioException e = Fails(Doc("\"transit\": { \"passenger_model\": \"agents\" }"));
        Assert.Contains("$.transit.passenger_model", e.Message, StringComparison.Ordinal);
        e = Fails(Doc("\"peds\": { \"enabled\": false, \"count\": 1 }, \"transit\": { \"passenger_model\": \"agents\" }"));
        Assert.Contains("$.transit.passenger_model", e.Message, StringComparison.Ordinal);
        e = Fails(Doc("\"transit\": { \"passenger_model\": \"crowd\" }"));
        Assert.Contains("$.transit.passenger_model", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassengerModel_CounterWithPeds_AndBidirectional_AreParsed()
    {
        ScenarioDesc d = Load(Doc("\"peds\": { \"count\": 10 }, \"transit\": { \"passenger_model\": \"counter\", \"bidirectional\": true }", "\"max_peds\": 13, \"max_residents\": 20"));
        Assert.Equal(PassengerModel.Counter, d.Transit.PassengerModel);
        Assert.True(d.Transit.Bidirectional);
    }

    [Fact]
    public void UnknownAndInvalidPedKeys_AreRejected()
    {
        ScenarioException e = Fails(Doc("\"peds\": { \"count\": 1, \"speeed_kmh\": [4, 5] }", "\"max_peds\": 2, \"max_residents\": 2"));
        Assert.Contains("$.peds", e.Message, StringComparison.Ordinal);
        Assert.Contains("speeed_kmh", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { \"count\": 1, \"arterial_crossings\": \"never\" }", "\"max_peds\": 2, \"max_residents\": 2"));
        Assert.Contains("$.peds.arterial_crossings", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { \"count\": 1, \"speed_kmh\": [5, 4] }", "\"max_peds\": 2, \"max_residents\": 2"));
        Assert.Contains("$.peds.speed_kmh", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { \"count\": 1, \"speed_kmh\": [4] }", "\"max_peds\": 2, \"max_residents\": 2"));
        Assert.Contains("$.peds.speed_kmh", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { }", PedCaps));
        Assert.Contains("$.peds.count", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { \"count\": 1, \"sidewalk_width_m\": 0.1 }", "\"max_peds\": 2, \"max_residents\": 2"));
        Assert.Contains("$.peds.sidewalk_width_m", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { \"count\": 1, \"residents\": 0 }", "\"max_peds\": 2, \"max_residents\": 2"));
        Assert.Contains("$.peds.residents", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PedCapacities_AreRequiredWithPeds_ChecksOneAndThreeTenths_AndForbiddenWithout()
    {
        ScenarioException e = Fails(Doc("\"peds\": { \"count\": 100 }"));
        Assert.Contains("$.capacities.max_peds", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { \"count\": 100 }", "\"max_peds\": 129, \"max_residents\": 200"));
        Assert.Contains("$.capacities.max_peds", e.Message, StringComparison.Ordinal);

        e = Fails(Doc("\"peds\": { \"count\": 100 }", "\"max_peds\": 130, \"max_residents\": 199"));
        Assert.Contains("$.capacities.max_residents", e.Message, StringComparison.Ordinal);

        e = Fails(Doc(string.Empty, "\"max_peds\": 10"));
        Assert.Contains("$.capacities.max_peds", e.Message, StringComparison.Ordinal);
        Assert.Contains("$.peds", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedScenarios_HaveNoPedsSection()
    {
        // PD1: s1k/s10k не меняются - секции peds нет, модель пассажиров прежняя.
        foreach (string name in new[] { "s1k", "s10k" })
        {
            ScenarioDesc d = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null)).Desc;
            Assert.False(d.Peds.Enabled);
            Assert.Equal(PassengerModel.Counter, d.Transit.PassengerModel);
            Assert.False(d.Transit.Bidirectional);
        }
    }

    [Fact]
    public void LoadedPedsDesc_CreatesWorldWithNetwork()
    {
        // Сквозная проверка: загрузчик -> ядро. Мини-сценарий с городом создаётся с включённой сетью пешеходов.
        string json = Doc("\"city\": { \"size_m\": [600, 600], \"block_m\": 150, \"arterial_every\": 2 }, \"peds\": { \"count\": 10 }",
            "\"max_peds\": 13, \"max_residents\": 20");
        json = json.Replace("\"max_nodes\": 8", "\"max_nodes\": 64", StringComparison.Ordinal)
            .Replace("\"max_segments\": 8", "\"max_segments\": 128", StringComparison.Ordinal)
            .Replace("\"max_lanes\": 64", "\"max_lanes\": 2048", StringComparison.Ordinal)
            .Replace("\"max_lane_points\": 512", "\"max_lane_points\": 32768", StringComparison.Ordinal)
            .Replace("\"max_zones\": 4", "\"max_zones\": 64", StringComparison.Ordinal)
            .Replace("\"max_signalized_nodes\": 1", "\"max_signalized_nodes\": 16", StringComparison.Ordinal);
        ScenarioDesc d = Load(json);
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? sim));
        using (sim)
        {
            Assert.True(sim!.Network.Sidewalks.Length > 0);
            Assert.True(sim.Network.Crossings.Length > 0);
            Assert.True(sim.Network.PedEdgePoints.Length > 0);
            Assert.Equal(3f, sim.Network.SidewalkWidthM, 3);
            Assert.Equal(3f, sim.Network.CrosswalkWidthM, 3);
        }
    }

}
