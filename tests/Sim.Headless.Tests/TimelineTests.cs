using System;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

namespace Sim.Headless.Tests;

/// <summary>Таймлайн команд в сценарии (M9, modding.md §3.5): разбор JSON, подача по тикам, хэш равен ручному <c>Enqueue</c>.</summary>
public class TimelineTests
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

    // крест: центр n0 (светофор заводится командой set_node_control) и четыре рукава, двусторонние сегменты в одну полосу
    private const string Plus = """
        "city": { "enabled": false },
        "network": {
          "nodes": [
            { "id": "c", "x": 500, "z": 500, "control": "none" },
            { "id": "n", "x": 500, "z": 0, "control": "none" }, { "id": "e", "x": 1000, "z": 500, "control": "none" },
            { "id": "s", "x": 500, "z": 1000, "control": "none" }, { "id": "w", "x": 0, "z": 500, "control": "none" }
          ],
          "segments": [
            { "a": "c", "b": "n", "lanes_ab": 1, "lanes_ba": 1, "speed_kmh": 50, "kind": "street" },
            { "a": "c", "b": "e", "lanes_ab": 1, "lanes_ba": 1, "speed_kmh": 50, "kind": "street" },
            { "a": "c", "b": "s", "lanes_ab": 1, "lanes_ba": 1, "speed_kmh": 50, "kind": "street" },
            { "a": "c", "b": "w", "lanes_ab": 1, "lanes_ba": 1, "speed_kmh": 50, "kind": "street" }
          ]
        }
        """;

    private const string Caps = """
        "capacities": { "max_vehicles": 128, "max_nodes": 32, "max_segments": 32, "max_lanes": 256, "max_lane_points": 2048,
          "max_zones": 16, "max_bus_routes": 1, "max_bus_stops": 1, "max_routes": 256,
          "max_plan_steps": 8192, "max_pending_trips": 128, "max_signalized_nodes": 4 }
        """;

    private const string SignalCmd = """
        { "kind": "set_signal_plan", "node": 0, "mode": "fixed", "green_ticks": [200, 200], "yellow_ticks": 40, "all_red_ticks": 20 }
        """;

    private const string NodeSignalCmd = """
        { "kind": "set_node_control", "node": 0, "control": "signal" }
        """;

    private const string BuildCmd = """
        { "kind": "build_road", "a": [2000, 2000], "b": [2000, 2400], "speed_mps": 13.5, "lanes_ab": 1, "lanes_ba": 1 }
        """;

    private static string Doc(string extra = "", string version = "2") =>
        "{ \"format_version\": " + version + ", \"seed\": 11, \"duration_s\": 60, \"vehicles\": { \"count\": 40, \"spawn_ramp_s\": 5 }, "
        + Plus + ", " + Caps + ", \"demand\": { \"zone_m\": 500, \"model\": \"uniform\", \"external_share\": 0, \"min_trip_m\": 0 }"
        + ", \"routing\": { \"levels\": 1, \"cell_m\": 250, \"pathfind_budget_per_tick\": 8 }"
        + (extra.Length > 0 ? ", " + extra : string.Empty) + " }";

    private static string TimelineDoc() =>
        Doc("\"api_version\": 1, \"timeline\": [ { \"tick\": 50, \"cmd\": " + NodeSignalCmd + " }, { \"tick\": 100, \"cmd\": " + SignalCmd + " }, { \"tick\": 300, \"cmd\": " + BuildCmd + " } ]");

    private static LoadedScenario Parse(string json) => ScenarioLoader.Parse(json, Types);

    private static ScenarioException Fails(string json) => Assert.Throws<ScenarioException>(() => Parse(json));

    private static ulong RunTimeline(string json, int threads, int ticks, out TimelineRunner runner)
    {
        LoadedScenario sc = Parse(json);
        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        runner = new TimelineRunner(sc.Timeline);
        runner.Step(sim, ticks);
        return sim.ComputeStateHash();
    }

    private static ulong RunManual(int threads, int ticks)
    {
        LoadedScenario sc = Parse(Doc());
        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(50);
        sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal));
        sim.Step(50);
        sim.Enqueue(Command.MakeSetSignalPlan(new SetSignalPlanCmd
        {
            Node = 0,
            Mode = SignalMode.Fixed,
            PhaseCount = 2,
            Green0 = 200,
            Green1 = 200,
            YellowTicks = 40,
            AllRedTicks = 20,
        }));
        sim.Step(200);
        sim.Enqueue(Command.MakeBuildRoad(new BuildRoadCmd
        {
            Ax = 2000,
            Az = 2000,
            Bx = 2000,
            Bz = 2400,
            SpeedLimit = 13.5f,
            LanesAB = 1,
            LanesBA = 1,
            Kind = RoadKind.Street,
        }));
        sim.Step(ticks - 300);
        return sim.ComputeStateHash();
    }

    [Fact]
    public void ApiVersion_IsOne() => Assert.Equal(1, CoreInfo.ApiVersion);

    [Fact]
    public void NoTimeline_EmptyAndSameSimulationAsWithApiVersionOnly()
    {
        LoadedScenario a = Parse(Doc());
        LoadedScenario b = Parse(Doc("\"api_version\": 1"));
        Assert.Empty(a.Timeline);
        Assert.Empty(b.Timeline);
        Assert.Equal(RunTimeline(Doc(), 1, 400, out _), RunTimeline(Doc("\"api_version\": \"1.0.0\""), 1, 400, out _));
    }

    [Fact]
    public void Timeline_ParsesToCommandsInOrder()
    {
        LoadedScenario sc = Parse(TimelineDoc());
        Assert.Equal(3, sc.Timeline.Count);
        Assert.Equal(CommandKind.SetNodeControl, sc.Timeline[0].Cmd.Kind);
        Assert.Equal(100, sc.Timeline[1].Tick);
        Assert.Equal(CommandKind.SetSignalPlan, sc.Timeline[1].Cmd.Kind);
        Assert.Equal(2, sc.Timeline[1].Cmd.SetSignalPlan.PhaseCount);
        Assert.Equal(200, sc.Timeline[1].Cmd.SetSignalPlan.Green1);
        Assert.Equal(CommandKind.BuildRoad, sc.Timeline[2].Cmd.Kind);
        Assert.Equal(2400f, sc.Timeline[2].Cmd.BuildRoad.Bz);
    }

    [Fact]
    public void Timeline_EqualsManualEnqueue_AndIsThreadIndependent()
    {
        ulong manual = RunManual(1, 700);
        foreach (int threads in new[] { 1, 2, 3 })
        {
            ulong viaTimeline = RunTimeline(TimelineDoc(), threads, 700, out TimelineRunner r);
            Assert.Equal(manual, viaTimeline);
            Assert.Equal(3, r.Enqueued);
            Assert.True(r.Rejects.Count == 0, string.Join(";", r.Rejects));
            Assert.Equal(0, r.Dropped);
        }

        // хэш действительно зависит от команд (тест не вырожденный)
        Assert.NotEqual(manual, RunTimeline(Doc("\"api_version\": 1"), 1, 700, out _));
    }

    [Fact]
    public void Timeline_StepChunking_DoesNotChangeHash()
    {
        LoadedScenario sc = Parse(TimelineDoc());
        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = 1 });
        var runner = new TimelineRunner(sc.Timeline);
        for (int i = 0; i < 70; i++)
        {
            runner.Step(sim, 10);
        }

        Assert.Equal(RunTimeline(TimelineDoc(), 1, 700, out _), sim.ComputeStateHash());
    }

    [Fact]
    public void Timeline_ToolWithoutImplementation_IsRejectedNotSupported_NoThrow()
    {
        string json = Doc("\"api_version\": 1, \"timeline\": [ { \"tick\": 20, \"cmd\": { \"kind\": \"remove_road\", \"seg\": { \"index\": 0, \"generation\": 1 } } } ]");
        ulong h = RunTimeline(json, 1, 60, out TimelineRunner r);
        Assert.NotEqual(0UL, h);
        TimelineReject rej = Assert.Single(r.Rejects);
        Assert.Equal(CommandKind.RemoveRoad, rej.Kind);
        Assert.Equal(RejectReason.NotSupported, rej.Reason);
        Assert.Equal(20, rej.Tick);
        var w = new StringWriter();
        r.WriteSummary(w);
        Assert.Contains("1 rejected", w.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_SameTick_KeepsFileOrder_TickZeroAllowed()
    {
        string json = Doc("\"api_version\": 1, \"timeline\": [ { \"tick\": 0, \"cmd\": { \"kind\": \"set_target_vehicle_count\", \"count\": 10 } }, "
            + "{ \"tick\": 0, \"cmd\": { \"kind\": \"set_target_vehicle_count\", \"count\": 20, \"tag\": 5 } } ]");
        LoadedScenario sc = Parse(json);
        Assert.Equal(10, sc.Timeline[0].Cmd.SetTargetVehicleCount.Count);
        Assert.Equal(5u, sc.Timeline[1].Cmd.Tag);
        using Simulation sim = Simulation.Create(sc.Desc);
        var runner = new TimelineRunner(sc.Timeline);
        runner.Step(sim, 1);
        Assert.Equal(2, runner.Enqueued);
    }

    [Fact]
    public void Timeline_Unsorted_Fails()
    {
        string json = Doc("\"api_version\": 1, \"timeline\": [ { \"tick\": 50, \"cmd\": { \"kind\": \"clear_selection\" } }, { \"tick\": 49, \"cmd\": { \"kind\": \"clear_selection\" } } ]");
        ScenarioException e = Fails(json);
        Assert.Equal(ModError.TimelineUnsorted, e.ModCode);
        Assert.Contains("$.timeline[1].tick", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_WithoutApiVersion_Fails()
    {
        ScenarioException e = Fails(Doc("\"timeline\": []"));
        Assert.Equal(ModError.MissingKey, e.ModCode);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("\"2.0.0\"")]
    [InlineData("0")]
    public void ApiVersion_Mismatch_Fails(string value)
    {
        ScenarioException e = Fails(Doc("\"api_version\": " + value));
        Assert.Equal(ModError.ApiVersionMismatch, e.ModCode);
    }

    [Fact]
    public void ApiVersion_BadType_Fails() => Assert.Equal(ModError.BadType, Fails(Doc("\"api_version\": true")).ModCode);

    [Theory]
    [InlineData("{ \"kind\": \"no_such_command\" }", "unknown command kind")]
    [InlineData("{ \"kind\": \"spawn_bus\", \"route\": 0, \"bogus\": 1 }", "$.timeline[0].cmd.bogus: unknown key")]
    [InlineData("{ \"kind\": \"build_road\", \"a\": [0, 0], \"b\": [10, 0] }", "speed_mps")]
    [InlineData("{ \"kind\": \"build_road\", \"a\": [0], \"b\": [10, 0], \"speed_mps\": 5 }", "expected [x, z]")]
    [InlineData("{ \"kind\": \"set_signal_plan\", \"node\": 0, \"green_ticks\": [] }", "1..8 phases")]
    [InlineData("{ \"kind\": \"set_node_control\", \"node\": 0, \"control\": \"nope\" }", "unknown value")]
    [InlineData("{ \"kind\": \"remove_road\" }", "$.timeline[0].cmd.seg")]
    public void BadCommands_ReportJsonPath(string cmd, string fragment)
    {
        ScenarioException e = Fails(Doc("\"api_version\": 1, \"timeline\": [ { \"tick\": 1, \"cmd\": " + cmd + " } ]"));
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_UnknownEntryKey_Fails() =>
        Assert.Contains("unknown key", Fails(Doc("\"api_version\": 1, \"timeline\": [ { \"tick\": 1, \"cmd\": { \"kind\": \"clear_selection\" }, \"x\": 1 } ]")).Message, StringComparison.Ordinal);

    [Fact]
    public void AllCommandKinds_Parse()
    {
        string[] cmds =
        [
            "{ \"kind\": \"spawn_trip\", \"to_zone\": 1, \"vehicle_type\": \"bus\", \"count\": 3 }",
            "{ \"kind\": \"create_bus_route\", \"headway_ticks\": 600, \"vehicle_type\": \"bus\" }",
            "{ \"kind\": \"append_bus_stop\", \"route\": 0, \"lane\": 2, \"s\": 10, \"stop_kind\": \"bay\" }",
            "{ \"kind\": \"spawn_bus\", \"route\": 0 }",
            "{ \"kind\": \"select_vehicle\", \"index\": 3, \"generation\": 1 }",
            "{ \"kind\": \"set_ped_target\", \"count\": 5 }",
            "{ \"kind\": \"set_node_control\", \"node\": 1, \"control\": \"stop\" }",
            "{ \"kind\": \"paint_zone\", \"rect\": [0, 0, 100, 100], \"zone\": \"residential\", \"density\": 1 }",
            "{ \"kind\": \"set_trip_execution\", \"mode\": \"aggregate\" }",
            "{ \"kind\": \"remove_road\", \"seg\": { \"index\": 0 }, \"keep_nodes\": true }",
            "{ \"kind\": \"upgrade_road\", \"seg\": { \"index\": 0 }, \"lanes_ab\": 2 }",
            "{ \"kind\": \"split_road\", \"seg\": { \"index\": 0 }, \"s\": 50 }",
            "{ \"kind\": \"set_arm_control\", \"node\": { \"index\": 0 }, \"arm_slot\": 1, \"control\": 2 }",
            "{ \"kind\": \"set_turn_ban\", \"node\": { \"index\": 0 }, \"from_arm\": 0, \"to_arm\": 1 }",
            "{ \"kind\": \"set_lane_flags\", \"seg\": { \"index\": 0 }, \"dir\": 0, \"lane_no\": 0, \"flags\": 1 }",
            "{ \"kind\": \"insert_bus_stop\", \"route\": { \"index\": 0 }, \"seg\": { \"index\": 0 }, \"dir\": 0, \"lane_no\": 0, \"s\": 5 }",
            "{ \"kind\": \"remove_bus_stop\", \"stop\": { \"index\": 0 } }",
            "{ \"kind\": \"remove_bus_route\", \"route\": { \"index\": 0 } }",
            "{ \"kind\": \"set_bus_route_params\", \"route\": { \"index\": 0 }, \"headway_ticks\": 300 }",
            "{ \"kind\": \"merge_at_node\", \"node\": { \"index\": 0 } }",
        ];
        string items = string.Join(", ", cmds.Select((c, i) => "{ \"tick\": " + (i * 10) + ", \"cmd\": " + c + " }"));
        LoadedScenario sc = Parse(Doc("\"api_version\": 1, \"timeline\": [ " + items + " ]"));
        Assert.Equal(cmds.Length, sc.Timeline.Count);
        Assert.Equal(1, sc.Timeline[0].Cmd.SpawnTrip.VehicleType);
        Assert.Equal(BusStopKind.Bay, sc.Timeline[2].Cmd.AppendBusStop.Kind);
        Assert.Equal(1, sc.Timeline[10].Cmd.UpgradeRoad.Flags);   // KeepKind: road_kind не задан
        Assert.Equal(255, sc.Timeline[10].Cmd.UpgradeRoad.LanesBA);

        // JSON-форма -> payload, а не просто «разобралось»: ссылки на сущности
        Assert.Equal(RemoveRoadFlags.KeepNodes, sc.Timeline[9].Cmd.RemoveRoad.Flags);
        Assert.Equal(0, sc.Timeline[9].Cmd.RemoveRoad.Seg.Index);
    }

    [Fact]
    public void V3_RoadAssetRef_ExpandsToCatalogRoad()
    {
        string scenarios = FindScenariosDir();
        string text = File.ReadAllText(Path.Combine(scenarios, "s1k-v3.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        string tl = "\"api_version\": 1, \"timeline\": [ { \"tick\": 40, \"cmd\": { \"kind\": \"build_road\", \"a\": [0, 0], \"b\": [200, 0], \"road\": \"base:road/arterial_4\" } },"
            + " { \"tick\": 41, \"cmd\": { \"kind\": \"build_road\", \"a\": [0, 0], \"b\": [200, 0], \"road\": \"base:road/street_2\", \"lanes_ab\": 3 } } ]";
        string json = text.TrimEnd().TrimEnd('}') + ", " + tl + " }";
        LoadedScenario sc = ScenarioLoader.Parse(json, _ => string.Empty, "t", ScenarioLoader.DefaultSearchDirs(scenarios));
        BuildRoadCmd a = sc.Timeline[0].Cmd.BuildRoad;
        Assert.Equal(RoadKind.Arterial, a.Kind);
        Assert.True(a.SpeedLimit > 0);
        Assert.Equal(sc.Catalog!.Roads.First(r => r.Id.Name == "arterial_4").LanesAB, a.LanesAB);
        Assert.Equal(3, sc.Timeline[1].Cmd.BuildRoad.LanesAB);   // явное поле перекрывает тип дороги

        string bad = text.TrimEnd().TrimEnd('}') + ", \"api_version\": 1, \"timeline\": [ { \"tick\": 1, \"cmd\": { \"kind\": \"build_road\", \"a\": [0, 0], \"b\": [9, 0], \"road\": \"base:road/nope\" } } ] }";
        ScenarioException e = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(bad, _ => string.Empty, "t", ScenarioLoader.DefaultSearchDirs(scenarios)));
        Assert.Equal(ModError.UnknownAssetRef, e.ModCode);
    }

    [Fact]
    public void HeadlessRunAndHash_FeedTimeline_AndReportRejects()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "vehicle-types.json"), Types);
            string json = TimelineDoc().TrimEnd().TrimEnd('}').TrimEnd().TrimEnd(']')
                + ", { \"tick\": 350, \"cmd\": { \"kind\": \"remove_road\", \"seg\": { \"index\": 0, \"generation\": 1 } } } ] }";
            File.WriteAllText(Path.Combine(dir, "tl.json"), json);
            var output = new StringWriter();
            var error = new StringWriter();
            int code = Sim.Headless.Bench.RunCommand.Run(["run", "--scenario", Path.Combine(dir, "tl.json"), "--ticks", "400"], output, error);
            Assert.True(code == 0, error.ToString());
            Assert.Contains("timeline: 4/4 commands enqueued, 1 rejected, 0 dropped", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("kind=RemoveRoad reason=NotSupported", output.ToString(), StringComparison.Ordinal);

            var hash = new StringWriter();
            Assert.Equal(0, Sim.Headless.Bench.HashCommand.Run(["hash", "--scenario", Path.Combine(dir, "tl.json"), "--ticks", "700", "--threads", "2"], hash, error));
            string line = hash.ToString().Split('\n')[0];
            Assert.Contains("tick=700 hash=", line, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string FindScenariosDir()
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
}
