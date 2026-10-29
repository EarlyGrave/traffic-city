using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Save;
using Sim.Core.Tests.Save;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// Поверхность прогрессии (PG1, progression.md §4, §8): загрузчик секции <c>progression</c> (значения, единицы ×10, ошибки на опечатках), состояние старта, API, сейв <c>PROG</c>/<c>CKPI</c>
/// (Save -&gt; Load -&gt; Save побайтно, продолжение тем же хэшем), проверка загрузки. Прежние golden и корпус w1-w10 - golden-тесты и <c>SaveCorpusTests</c>.
/// </summary>
public sealed class ProgressionDataTests
{
    private static readonly string[] _ids = { "m1", "m2", "m3" };

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    [Fact]
    public void Loader_ParsesSection_IntoPod()
    {
        LoadedScenario sc = ProgressionFixtures.Econ();
        ProgressionDesc p = sc.Desc.Progression;
        Assert.True(p.Enabled);
        Assert.Equal(_ids, sc.MilestoneIds);
        Assert.Equal(50, p.MinLegs);
        Assert.Equal(2, p.Areas.Length);
        Assert.Equal(-2300f, p.Areas[1].MinX);
        ulong locked = Bit(Feature.ArterialUpgrade) | Bit(Feature.BusLines) | Bit(Feature.SignalsAdaptive) | Bit(Feature.Curves) | Bit(Feature.BusLanes) | Bit(Feature.ParkingFees) | Bit(Feature.Zoning);
        Assert.Equal(locked, p.LockedFeatures);
        Assert.Equal(3, p.Milestones.Length);
        MilestoneDesc m2 = p.Milestones[1];
        Assert.Equal(5000, m2.Population);
        Assert.Equal(KpiKind.IdlePct, m2.G0.Kpi);
        Assert.Equal(0, m2.G0.IsMin);
        Assert.Equal(450, m2.G0.Value);   // проценты ×10
        Assert.Equal(KpiKind.TravelMin, m2.G1.Kpi);
        Assert.Equal(405, m2.G1.Value);   // минуты ×10
        Assert.Equal(MilestoneFlags.GatesAny, m2.Flags);
        Assert.Equal(10, m2.CreditDays);
        Assert.Equal(-1, m2.Area);
        Assert.Equal(Bit(Feature.BusLanes) | Bit(Feature.SignalsAdaptive), m2.Features);
        Assert.Equal(1, m2.ProfileCount);
        Assert.Equal(1, p.Milestones[2].Area);
        Assert.Equal(1, p.Milestones[2].G0.IsMin);
        Assert.Equal(2, p.ProfileIdx.Length);
        Assert.Equal("asym_1_2", sc.Desc.RoadProfiles.Profiles[p.ProfileIdx[m2.ProfileFirst]].Id);
    }

    [Fact]
    public void Loader_NoSectionOrDisabled_IsZero()
    {
        LoadedScenario none = ProgressionFixtures.Parse(ProgressionFixtures.EconJson(null));
        Assert.False(none.Desc.Progression.Enabled);
        Assert.Empty(none.MilestoneIds);
        LoadedScenario off = ProgressionFixtures.Parse(ProgressionFixtures.EconJson("""{ "enabled": false, "milestones": [ { "id": "a", "population": 10 } ] }"""));
        Assert.False(off.Desc.Progression.Enabled);
        Assert.Null(off.Desc.Progression.Milestones);
    }

    [Theory]
    [InlineData("""{ "locked_features": ["bus_line"] }""", "unknown feature")]
    [InlineData("""{ "locked_features": ["roads_basic"] }""", "always open")]
    [InlineData("""{ "locked_profiles": ["arterial_8"] }""", "unknown road profile")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100 }, { "id": "b", "population": 100 } ] }""", "strictly increase")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100 }, { "id": "a", "population": 200 } ] }""", "duplicate milestone id")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100, "unlock": ["zoning"] } ] }""", "not in locked_features")]
    [InlineData("""{ "locked_profiles": ["asym_1_2"], "milestones": [ { "id": "a", "population": 100, "unlock_profiles": ["arterial_6"] } ] }""", "not in locked_profiles")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100, "gates": [ { "kpi": "idle_pc", "max": 45 } ] } ] }""", "unknown kpi")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100, "gates": [ { "kpi": "idle_pct" } ] } ] }""", "exactly one")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100, "gates": [ { "kpi": "idle_pct", "max": 1 }, { "kpi": "idle_pct", "max": 2 }, { "kpi": "idle_pct", "max": 3 } ] } ] }""", "at most 2")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100, "area": 0 } ] }""", "milestones[0].area")]
    [InlineData("""{ "milestones": [ { "id": "a", "population": 100, "granted": 5 } ] }""", "unknown key")]
    [InlineData("""{ "min_leg": 5 }""", "unknown key")]
    [InlineData("""{ "areas": [ { "min_x": 10, "min_z": 0, "max_x": 0, "max_z": 5 } ] }""", "min_x <= max_x")]
    public void Loader_RejectsTypos(string section, string message)
    {
        ScenarioException e = Assert.Throws<ScenarioException>(() => ProgressionFixtures.Parse(ProgressionFixtures.EconJson(section)));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsSectionWithoutResidents()
    {
        string json = ProgressionFixtures.EconJson("""{ "milestones": [] }""").Replace("\"model\":\"residents\"", "\"model\":\"gravity\"", StringComparison.Ordinal);
        Assert.Throws<ScenarioException>(() => ProgressionFixtures.Parse(json));
    }

    [Fact]
    public void Create_ValidatesDescInCore()
    {
        ScenarioDesc d = ProgressionFixtures.Econ().Desc;
        d.Progression.Milestones = (MilestoneDesc[])d.Progression.Milestones.Clone();
        d.Progression.Milestones[1].Population = 10;   // не растёт
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in d, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim));
        Assert.Null(sim);
    }

    [Fact]
    public void Create_StartState_AndApi()
    {
        LoadedScenario sc = ProgressionFixtures.Econ();
        using Simulation sim = Make(sc.Desc);
        Assert.True(sim.HasProgression);
        Assert.True(sim.GetProgression(out ProgressionInfo info));
        Assert.Equal(0, info.Reached);
        Assert.Equal(3, info.MilestoneCount);
        Assert.Equal(4000, info.NextPopulation);
        Assert.Equal(0, info.NextGateMask);   // у m1 ворот нет
        Assert.Equal(ProgressionState.AllFeatures & ~sc.Desc.Progression.LockedFeatures, info.Features);
        Assert.False(sim.IsFeatureUnlocked(Feature.BusLines));
        Assert.True(sim.IsFeatureUnlocked(Feature.SignalsFixed));
        Assert.True(sim.IsFeatureUnlocked(Feature.RoadsBasic));
        int asym = Array.FindIndex(sc.Desc.RoadProfiles.Profiles, p => p.Id == "asym_1_2");
        int street = Array.FindIndex(sc.Desc.RoadProfiles.Profiles, p => p.Id == "street_2");
        Assert.False(sim.IsProfileUnlocked(asym));
        Assert.True(sim.IsProfileUnlocked(street));
        Assert.False(sim.IsProfileUnlocked(99));
        Assert.True(sim.GetMilestone(2, out MilestoneInfo m3));
        Assert.Equal(6000, m3.Desc.Population);
        Assert.Equal(-1, m3.ReachedTick);
        Assert.False(sim.GetMilestone(3, out _));
        Assert.Equal(0, sim.CopyComplaints(new ComplaintInfo[4], 0));

        using Simulation plain = Make(ProgressionFixtures.Parse(ProgressionFixtures.EconJson(null)).Desc);
        Assert.False(plain.HasProgression);
        Assert.False(plain.GetProgression(out _));
        Assert.True(plain.IsFeatureUnlocked(Feature.BusLines));
        Assert.True(plain.IsProfileUnlocked(asym));
        Assert.True(plain.GetCityKpi(out _));   // показатели - всегда при жителях
    }

    [Fact]
    public void Progression_DoesNotChangeBehaviour_OnlyHashSection()
    {
        // Прогрессия в PG1/PG2 на поведение не влияет: без неё и с ней счётчики и казна те же; хэш различается только разделом прогрессии.
        using Simulation with = Make(ProgressionFixtures.Econ(microDay: 28_800).Desc);
        using Simulation without = Make(ProgressionFixtures.Econ(microDay: 28_800, section: """{ "enabled": false }""").Desc);
        with.Step(4000);
        without.Step(4000);
        Assert.Equal(without.Counters.ResLegsDone, with.Counters.ResLegsDone);
        Assert.Equal(without.World.City!.Ledger.City, with.World.City!.Ledger.City);
        Assert.Equal(without.World.Counters.VehicleTicksTotal, with.World.Counters.VehicleTicksTotal);
        Assert.NotEqual(without.ComputeStateHash(), with.ComputeStateHash());
    }

    [Fact]
    public void Save_RoundTrip_ByteEqual_AndContinues()
    {
        using Simulation sim = Make(ProgressionFixtures.Econ(microDay: 28_800).Desc);
        sim.Step(2500);   // посреди второго игрового часа (час = 1 200 тиков)
        ProgressionState p = sim.World.Progression!;
        p.GatedNotified = 2;   // состояние, которое пишет PG3, тоже переживает сейв
        byte[] a = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(a);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.True(a.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(loaded)));
        Assert.True(loaded.HasProgression);
        Assert.Equal(2UL, loaded.World.Progression!.GatedNotified);
        Assert.Equal(p.Defs.Length, loaded.World.Progression.Defs.Length);
        sim.Step(1500);
        loaded.Step(1500);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void Load_RejectsInconsistentProgression()
    {
        using Simulation sim = Make(ProgressionFixtures.Econ(microDay: 28_800).Desc);
        sim.Step(50);
        sim.World.Progression!.Reached = 1;   // веха «взята» без тика взятия и без её признаков
        byte[] bad = WorldSaveFixtures.SaveBytes(sim);
        var opt = new LoadOptions { StrictHash = 0 };
        LoadStatus st = Simulation.Load(bad, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? s, out LoadReport rep);
        s?.Dispose();
        Assert.Equal(LoadStatus.InvalidValue, st);
        Assert.Equal(SaveFormat.FourCc("PROG"u8), rep.FailedBlock);

        sim.World.Progression.Reached = 0;
        sim.World.City!.Kpi.Head = 30;   // голова кольца вне диапазона
        bad = WorldSaveFixtures.SaveBytes(sim);
        st = Simulation.Load(bad, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out s, out rep);
        s?.Dispose();
        Assert.Equal(LoadStatus.InvalidValue, st);
        Assert.Equal(SaveFormat.FourCc("CKPI"u8), rep.FailedBlock);
    }

    [Fact]
    public void Surface_IsFrozen()
    {
        Assert.Equal(8, System.Runtime.InteropServices.Marshal.SizeOf<KpiGate>());
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf<MilestoneDesc>());
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<AreaRect>());
        Assert.Equal(88, System.Runtime.InteropServices.Marshal.SizeOf<KpiHour>());
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<ComplaintInfo>());
        Assert.Equal(16, (int)System.Runtime.InteropServices.Marshal.OffsetOf<ComplaintInfo>(nameof(ComplaintInfo.Place)));
        Assert.Equal(17, (int)RejectReason.Locked);
        Assert.Equal((59, 60, 61, 62), ((int)SimEventKind.MilestoneReached, (int)SimEventKind.MilestoneGated, (int)SimEventKind.BudgetWarning, (int)SimEventKind.Complaint));
        Assert.Equal(10, (int)Feature.ParkingFees);
        Assert.Equal(ProgressionState.FeatureCount, Enum.GetValues<Feature>().Length);
        Assert.Equal(ProgressionState.FeatureCount, ScenarioLoader.FeatureNamesCount);
    }

    private static ulong Bit(Feature f) => 1UL << (int)f;
}
