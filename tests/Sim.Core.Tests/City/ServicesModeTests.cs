using System;
using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

namespace Sim.Core.Tests.City;

/// <summary>
/// Режим <c>city.services</c> (services.md §2.1, §5; SR1): запись <c>utility</c> из секции ведёт себя ровно как ЖКХ BL (те же ремонты, задержки, фонд), хэш при 1/2/3 потоках
/// один, сейв посреди выездов продолжается тем же хэшем, 0 Б/тик; валидатор <c>Create</c> отвергает режим без <c>city.lifecycle</c> и битые таблицы.
/// </summary>
public sealed class ServicesModeTests
{
    private const string Scenario = "s1k-res-bl";
    private const int Depots = 3;

    private static (long Repairs, long Delay, long Timeouts, long NoDepot, long Fund) Outcome(Simulation sim) =>
        (sim.World.Counters.RepairsDone, sim.World.Counters.RepairDelayTicksTotal, sim.World.Counters.CrewTimeouts, sim.World.Counters.RepairNoDepot, sim.World.City!.Ledger.Utility);

    [Theory]
    [InlineData(TripExecution.Agents)]
    [InlineData(TripExecution.Aggregate)]
    public void UtilityRecord_SameOutcomeAsBl(TripExecution exec)
    {
        using Simulation bl = ServicesRefactorTests.Make(Scenario, exec, Depots);
        using Simulation sr = ServicesRefactorTests.Make(Scenario, exec, Depots, mode: true);
        Assert.False(bl.World.City!.Services!.Table.Explicit);
        Assert.True(sr.World.City!.Services!.Table.Explicit);
        int day = 2 * bl.World.City!.DayTicks;   // первые ремонты - на вторые сутки
        bl.Step(day);
        sr.Step(day);
        Assert.True(bl.World.Counters.RepairsDone > 0);
        Assert.Equal(Outcome(bl), Outcome(sr));
        Assert.NotEqual(bl.ComputeStateHash(), sr.ComputeStateHash());   // в режиме хэш пишет станции и все кольца (блок SERV)
    }

    [Fact]
    public void Mode_Hash_SameAt_1_2_3_Threads()
    {
        ulong? first = null;
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = ServicesRefactorTests.Make(Scenario, TripExecution.Agents, Depots, mode: true, threads: t);
            sim.Step(sim.World.City!.DayTicks / 4);
            Assert.True(sim.World.City!.Services!.Crews.Crews() > 0);
            ulong h = sim.ComputeStateHash();
            first ??= h;
            Assert.Equal(first, h);
        }
    }

    [Fact]
    public void Mode_Save_Continues_SameHash()
    {
        using Simulation sim = ServicesRefactorTests.Make(Scenario, TripExecution.Agents, Depots, mode: true);
        sim.Step(sim.World.City!.DayTicks / 4);
        CityServices sv = sim.World.City!.Services!;
        Assert.True(sv.Crews.Crews() > 0 && sv.Sites.Used() == Depots);
        byte[] raw = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(raw);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.True(loaded.World.City!.Services!.Table.Explicit);
        Assert.Equal(sv.Table.Records, loaded.World.City!.Services!.Table.Records);
        sim.Step(4_000);
        loaded.Step(4_000);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void Mode_ZeroAllocations_PerTick()
    {
        using Simulation sim = ServicesRefactorTests.Make(Scenario, TripExecution.Agents, Depots, mode: true);
        sim.Step(sim.World.City!.DayTicks / 8);
        Assert.True(sim.World.City!.Services!.Crews.Crews() > 0);
        long bytes = AllocationProbe.Measure(() => sim.Step(400));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Create_RejectsBadServices()
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(Scenario);
        ServicesRefactorTests.EnableServices(ref d);
        ScenarioDesc noLife = d;
        noLife.Town.Lifecycle = default;
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in noLife));

        ScenarioDesc onCall = d;
        onCall.Town.Services.Calls = new[] { new ServiceCallDesc { Kind = ServiceCallKind.Repair, Flags = ServiceCallFlags.OnCall, OnsiteS = 150 } };
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in onCall));

        ScenarioDesc range = d;
        range.Town.Services.Records = new[] { new ServiceRecordDesc { Fund = ServiceFund.Utility, FirstLevel = 0, LevelCount = 2 } };
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in range));

        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(in d));
    }

    private static LoadedScenario Parse(string name, Action<JsonNode> edit, bool packages = false)
    {
        string dir = Path.Combine(GenFixture.RepoRoot(), "scenarios");
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")))!;
        edit(root);
        PackageSearchDir[]? search = packages ? new[] { new PackageSearchDir(Path.Combine(GenFixture.RepoRoot(), "content"), true) } : null;
        return ScenarioLoader.Parse(root.ToJsonString(), p => File.ReadAllText(Path.Combine(dir, p)), name, search, null, p => File.ReadAllBytes(Path.Combine(dir, p)));
    }

    [Fact]
    public void Loader_V2_BuiltInUtilityRecord_AndKeys()
    {
        LoadedScenario sc = Parse(Scenario, r => r["city"]!["services"] = new JsonObject
        {
            ["max_service_sites"] = 64,
            ["max_calls"] = 512,
            ["fire_risk_pct"] = 20,
            ["incidence_ppm"] = new JsonObject { ["adult"] = new JsonArray(1000, 200, 100) },
            ["ill_days"] = new JsonArray(2, 4, 6),
        });
        CityServicesDesc s = sc.Desc.Town.Services;
        Assert.True(s.Enabled);
        Assert.Equal((64, 512, 20, 1000, 200, 100, 2, 4, 6), (s.MaxServiceSites, s.MaxCalls, s.FireRiskPct, s.IllLightPpm, s.IllInpatientPpm, s.IllCriticalPpm, s.IllDaysLight, s.IllDaysInpatient, s.IllDaysCritical));
        ServiceRecordDesc r = Assert.Single(s.Records);
        Assert.Equal(ServiceFund.Utility, r.Fund);
        Assert.Equal(ServiceCallKind.Repair, Assert.Single(s.Calls).Kind);
        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(sc.Desc));
    }

    [Fact]
    public void Loader_RequiresLifecycle_AndRejectsUnknownKeys()
    {
        Assert.Throws<ScenarioException>(() => Parse(Scenario, r =>
        {
            r["city"]!.AsObject().Remove("lifecycle");
            r["city"]!["services"] = new JsonObject();
        }));
        Assert.Throws<ScenarioException>(() => Parse(Scenario, r => r["city"]!["services"] = new JsonObject { ["nope"] = 1 }));
        Assert.Throws<ScenarioException>(() => Parse(Scenario, r => r["city"]!["services"] = new JsonObject { ["services_off"] = new JsonArray("base:service/utility") }));   // v2: каталога нет
        LoadedScenario off = Parse(Scenario, r => r["city"]!["services"] = new JsonObject { ["enabled"] = false });
        Assert.False(off.Desc.Town.Services.Enabled);
    }

    [Fact]
    public void Loader_V3_ServicesOff_ChecksCatalog()
    {
        // v3: services_off сверяется с каталогом пакетов; сами таблицы из каталога - ServiceContentTests (Flatten)
        ScenarioException known = Assert.Throws<ScenarioException>(() => Parse("s1k-v3", r => r["city"]!["services"] = new JsonObject { ["services_off"] = new JsonArray("base:service/utility") }, packages: true));
        Assert.Contains("requires city.lifecycle", known.Message, StringComparison.Ordinal);
        ScenarioException unknown = Assert.Throws<ScenarioException>(() => Parse("s1k-v3", r => r["city"]!["services"] = new JsonObject { ["services_off"] = new JsonArray("base:service/nope") }, packages: true));
        Assert.Contains("is not loaded", unknown.Message, StringComparison.Ordinal);
    }
}
