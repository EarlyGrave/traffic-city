using System;
using System.IO;
using System.Linq;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>YS1 (scenarios-mode.md §2.2-§2.3, ADR-0038): цели сценария - проверка раз в час, грейс, for_hours, исход и событие, сейв посреди отсчёта, хэш по потокам, 0 Б/тик, загрузчик, meta и каталог.</summary>
public sealed class GoalsTests
{
    private const int Day = 72_000;       // сутки s1k-res
    private const int Hour = Day / 24;

    private static string Scenarios => Path.Combine(GenFixture.RepoRoot(), "scenarios");

    private static ScenarioDesc Base()
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(Scenarios, "s1k-res.json")).Desc;
        d.Town.Residents.TripExecution = TripExecution.Aggregate;
        return d;
    }

    private static ScenarioDesc With(int graceHours, GoalCondDesc[] win, GoalCondDesc[] lose)
    {
        ScenarioDesc d = Base();
        var g = new GoalsDesc { GraceHours = graceHours, WinCount = (byte)win.Length, LoseCount = (byte)lose.Length };
        for (int i = 0; i < win.Length; i++)
        {
            g.Set(i, win[i]);
        }

        for (int i = 0; i < lose.Length; i++)
        {
            g.Set(GoalsDesc.MaxConds + i, lose[i]);
        }

        d.Goals = g;
        return d;
    }

    private static GoalCondDesc Cond(GoalKind kind, int value, int holdHours = 0, KpiKind kpi = KpiKind.None) => new() { Kind = kind, Value = value, HoldHours = holdHours, Kpi = kpi };

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static int Outcomes(Simulation sim, out int a, out int b)
    {
        int n = 0;
        a = b = 0;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.ScenarioOutcome)
            {
                n++;
                a = e.A;
                b = e.B;
            }
        }

        return n;
    }

    [Fact]
    public void NoSection_NoGoals()
    {
        ScenarioDesc d = Base();
        Assert.False(d.Goals.Enabled);
        using Simulation sim = Make(in d);
        Assert.False(sim.HasGoals);
        Assert.False(sim.TryGetGoals(out _));
        Assert.Equal(GoalKind.None, sim.GetGoalCond(0).Kind);
        Assert.Null(sim.World.City!.Goals);
    }

    [Fact]
    public void PopulationLt_AboveStart_LosesInFirstHourAfterGrace()
    {
        ScenarioDesc d = With(24, [], [Cond(GoalKind.PopulationLt, 1_000_000)]);
        using Simulation sim = Make(in d);
        long start = sim.World.Tick;
        int events = 0;
        for (int h = 1; h <= 24; h++)
        {
            sim.Step(Hour);
            events += Outcomes(sim, out _, out _);
        }

        Assert.True(sim.TryGetGoals(out GoalsInfo g));
        Assert.Equal(GoalOutcome.Running, g.Outcome);
        Assert.Equal(0, sim.GetGoalCond(GoalsState.LoseBase).HeldHours);   // в грейс поражение не копится
        Assert.Equal(0, events);

        sim.Step(Hour);
        Assert.Equal(1, Outcomes(sim, out int a, out int b));
        Assert.True(sim.TryGetGoals(out g));
        Assert.Equal(GoalOutcome.Lost, g.Outcome);
        Assert.Equal(0, g.LoseIndex);
        Assert.Equal(start + (25 * Hour) - 1, g.OutcomeTick);
        Assert.Equal(GoalOutcome.Lost, a);
        Assert.Equal(0, b);

        sim.Step(3 * Hour);   // после исхода проверок нет
        Assert.Equal(0, Outcomes(sim, out _, out _));
        Assert.True(sim.TryGetGoals(out GoalsInfo after));
        Assert.Equal(g.OutcomeTick, after.OutcomeTick);
        Assert.Equal(25, after.HoursElapsed);
    }

    [Fact]
    public void ForHours_MustHold_ThenFires()
    {
        ScenarioDesc d = With(0, [], [Cond(GoalKind.TreasuryLt, int.MaxValue), Cond(GoalKind.PopulationLt, 1_000_000, holdHours: 3)]);
        d.Goals.L0 = Cond(GoalKind.DayGe, 5);   // не сработает за 3 часа
        using Simulation sim = Make(in d);
        sim.Step(2 * Hour);
        GoalCondInfo c = sim.GetGoalCond(GoalsState.LoseBase + 1);
        Assert.Equal(2, c.HeldHours);
        Assert.Equal(3, c.HoldHours);
        Assert.Equal(1, c.Met);
        Assert.Equal(1, c.IsLose);
        Assert.Equal(sim.CityPopulation, c.Current);
        Assert.True(sim.TryGetGoals(out GoalsInfo g));
        Assert.Equal(GoalOutcome.Running, g.Outcome);
        Assert.Equal(0, sim.GetGoalCond(GoalsState.LoseBase).Met);

        sim.Step(Hour);
        Assert.True(sim.TryGetGoals(out g));
        Assert.Equal(GoalOutcome.Lost, g.Outcome);
        Assert.Equal(1, g.LoseIndex);
    }

    [Fact]
    public void Win_AllConditions_LoseHasPriority()
    {
        ScenarioDesc d = With(0, [Cond(GoalKind.PopulationGe, 1), Cond(GoalKind.DayGe, 0)], []);
        using (Simulation sim = Make(in d))
        {
            sim.Step(Hour);
            Assert.Equal(1, Outcomes(sim, out int a, out int b));
            Assert.Equal(GoalOutcome.Won, a);
            Assert.Equal(-1, b);
            Assert.True(sim.TryGetGoals(out GoalsInfo g));
            Assert.Equal(GoalOutcome.Won, g.Outcome);
            Assert.Equal(-1, g.LoseIndex);
        }

        ScenarioDesc both = With(0, [Cond(GoalKind.PopulationGe, 1)], [Cond(GoalKind.PopulationLt, 1_000_000)]);
        using Simulation s2 = Make(in both);
        s2.Step(Hour);
        Assert.True(s2.TryGetGoals(out GoalsInfo g2));
        Assert.Equal(GoalOutcome.Lost, g2.Outcome);   // в один час поражение важнее
    }

    [Fact]
    public void DayGe_FiresAtDayBoundary()
    {
        ScenarioDesc d = With(0, [Cond(GoalKind.DayGe, 1)], []);
        using Simulation sim = Make(in d);
        sim.Step(23 * Hour);
        Assert.True(sim.TryGetGoals(out GoalsInfo g));
        Assert.Equal(GoalOutcome.Running, g.Outcome);
        sim.Step(Hour);
        Assert.True(sim.TryGetGoals(out g));
        Assert.Equal(GoalOutcome.Won, g.Outcome);
    }

    [Fact]
    public void SaveRoundTrip_MidCountdown_ContinuesIdentically()
    {
        ScenarioDesc d = With(2, [Cond(GoalKind.KpiLe, 100_000, kpi: KpiKind.TravelMin), Cond(GoalKind.PopulationGe, 1_000_000)], [Cond(GoalKind.PopulationLt, 1_000_000, holdHours: 10)]);
        using Simulation a = Make(in d);
        a.Step((6 * Hour) + 123);
        Assert.Equal(4, a.GetGoalCond(GoalsState.LoseBase).HeldHours);
        using Simulation b = ToolsFixture.RoundTrip(a);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.True(b.TryGetGoals(out GoalsInfo gb));
        Assert.Equal(6, gb.HoursElapsed);
        a.Step(8 * Hour);
        b.Step(8 * Hour);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.True(a.TryGetGoals(out GoalsInfo ga));
        Assert.True(b.TryGetGoals(out gb));
        Assert.Equal(GoalOutcome.Lost, ga.Outcome);
        Assert.Equal(ga.OutcomeTick, gb.OutcomeTick);
        Assert.Equal(ga.LoseIndex, gb.LoseIndex);

        using Simulation c = ToolsFixture.RoundTrip(a);   // сейв после исхода
        Assert.Equal(a.ComputeStateHash(), c.ComputeStateHash());
    }

    [Fact]
    public void Hash_DependsOnSection_AndNotOnThreads()
    {
        ulong Run(int threads)
        {
            ScenarioDesc d = With(1, [Cond(GoalKind.KpiGe, 0, kpi: KpiKind.SatisfactionPct)], [Cond(GoalKind.PopulationLt, 1_000_000, holdHours: 30)]);
            d.Town.Residents.TripExecution = TripExecution.Agents;
            using Simulation sim = Make(in d, threads);
            sim.Step(4 * Hour);
            return sim.ComputeStateHash();
        }

        ulong h1 = Run(1);
        Assert.Equal(h1, Run(2));
        Assert.Equal(h1, Run(3));

        ScenarioDesc plain = Base();
        plain.Town.Residents.TripExecution = TripExecution.Agents;
        using Simulation p = Make(in plain);
        p.Step(4 * Hour);
        Assert.NotEqual(h1, p.ComputeStateHash());   // раздел хэша есть только при секции
    }

    [Fact]
    public void Step_AllocatesNothing_WithGoals()
    {
        ScenarioDesc d = With(0, [Cond(GoalKind.KpiLe, 100, kpi: KpiKind.TravelMin), Cond(GoalKind.PopulationGe, 1_000_000)], [Cond(GoalKind.PopulationLt, 1_000_000, holdHours: 1000)]);
        using Simulation sim = Make(in d);
        sim.Step(3 * Hour);
        long bytes = AllocationProbe.Measure(() => sim.Step((2 * Hour) + 200));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Create_RejectsInvalidGoals()
    {
        ScenarioDesc bad = With(0, [Cond(GoalKind.PopulationGe, 1)], []);
        bad.Goals.W3 = Cond(GoalKind.PopulationGe, 1);   // слот вне WinCount не пустой
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in bad, default, out Simulation? s1));
        s1?.Dispose();

        ScenarioDesc kpi = With(0, [Cond(GoalKind.KpiLe, 1)], []);   // KPI без показателя
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in kpi, default, out Simulation? s2));
        s2?.Dispose();
    }

    // ------------------------------------------------------------------ загрузчик, meta, каталог

    private static string Res(string goals) =>
        File.ReadAllText(Path.Combine(Scenarios, "s1k-res.json")).Replace("\"seed\":", goals + ",\n  \"seed\":", StringComparison.Ordinal);

    private static LoadedScenario Parse(string json) => ScenarioLoader.Parse(json, f => File.ReadAllText(Path.Combine(Scenarios, f)), "t");

    [Fact]
    public void Loader_ParsesGoals()
    {
        LoadedScenario sc = Parse(Res("""
            "goals": { "grace_days": 2, "after_win": "end",
              "win": [ { "kind": "population_ge", "value": 50000 }, { "kind": "kpi_le", "kpi": "travel_min", "value": 25.5, "for_hours": 6 } ],
              "lose": [ { "kind": "population_lt", "value": 10000, "for_hours": 24 }, { "kind": "day_ge", "value": 60 }, { "kind": "prestige_ge", "value": 500, "for_days": 2 } ] }
            """));
        GoalsDesc g = sc.Desc.Goals;
        Assert.Equal(48, g.GraceHours);
        Assert.Equal(1, g.AfterWin);
        Assert.Equal(2, g.WinCount);
        Assert.Equal(3, g.LoseCount);
        Assert.Equal(GoalKind.PopulationGe, g.W0.Kind);
        Assert.Equal(50_000, g.W0.Value);
        Assert.Equal(KpiKind.TravelMin, g.W1.Kpi);
        Assert.Equal(255, g.W1.Value);
        Assert.Equal(6, g.W1.HoldHours);
        Assert.Equal(24, g.L0.HoldHours);
        Assert.Equal(GoalKind.DayGe, g.L1.Kind);
        Assert.Equal(GoalKind.PrestigeGe, g.L2.Kind);
        Assert.Equal(48, g.L2.HoldHours);
        Assert.Contains(sc.Warnings, w => w.Contains("prestige_ge", StringComparison.Ordinal));
        Assert.Null(sc.Meta);
        Assert.False(Parse(Res("\"name2\": 1").Replace(",\n  \"name2\": 1", string.Empty, StringComparison.Ordinal)).Desc.Goals.Enabled);
    }

    [Theory]
    [InlineData("\"goals\": { \"win\": [ { \"kind\": \"population_gt\", \"value\": 1 } ] }")]
    [InlineData("\"goals\": { }")]
    [InlineData("\"goals\": { \"win\": [ { \"kind\": \"population_ge\", \"value\": 1, \"for_hours\": 1, \"for_days\": 1 } ] }")]
    [InlineData("\"goals\": { \"win\": [ { \"kind\": \"population_ge\", \"value\": 1, \"extra\": 1 } ] }")]
    [InlineData("\"goals\": { \"lose\": [ { \"kind\": \"insolvent\" } ] }")]
    [InlineData("\"goals\": { \"win\": [ { \"kind\": \"milestone\", \"value\": 0 } ] }")]
    [InlineData("\"goals\": { \"win\": [ { \"kind\": \"kpi_ge\", \"kpi\": \"speed\", \"value\": 1 } ] }")]
    [InlineData("\"goals\": { \"after_win\": \"stop\", \"win\": [ { \"kind\": \"day_ge\", \"value\": 1 } ] }")]
    [InlineData("\"meta\": { \"mode\": \"sandbox\", \"title\": \"x\" }")]
    [InlineData("\"meta\": { \"mode\": \"scenario\", \"title\": \"x\", \"difficulty\": 6 }")]
    public void Loader_RejectsBadGoalsAndMeta(string section)
    {
        Assert.Throws<ScenarioException>(() => Parse(Res(section)));
    }

    [Fact]
    public void Loader_RejectsTooManyConditions_AndNonResidents()
    {
        string nine = string.Join(", ", Enumerable.Repeat("{ \"kind\": \"day_ge\", \"value\": 1 }", 9));
        Assert.Throws<ScenarioException>(() => Parse(Res("\"goals\": { \"lose\": [ " + nine + " ] }")));
        string s1k = File.ReadAllText(Path.Combine(Scenarios, "s1k.json")).Replace("\"seed\":", "\"goals\": { \"win\": [ { \"kind\": \"day_ge\", \"value\": 1 } ] },\n  \"seed\":", StringComparison.Ordinal);
        Assert.Throws<ScenarioException>(() => Parse(s1k));
    }

    [Fact]
    public void GameCity_IsScenarioWithBankruptcyLoss()
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(Scenarios, "game-city.json"));
        Assert.NotNull(sc.Meta);
        Assert.Equal(ScenarioMode.Scenario, sc.Meta!.Mode);
        Assert.Equal(1, sc.Meta.Difficulty);
        GoalsDesc g = sc.Desc.Goals;
        Assert.Equal(0, g.WinCount);
        Assert.Equal(1, g.LoseCount);
        Assert.Equal(GoalKind.Insolvent, g.L0.Kind);
        Assert.Equal(240, g.L0.HoldHours);
        Assert.Equal(48, g.GraceHours);
    }

    [Fact]
    public void Locator_ListsOnlyFilesWithMeta()
    {
        var list = ScenarioLocator.List(Scenarios);
        ScenarioListEntry game = Assert.Single(list, e => e.Name == "game-city");
        Assert.Equal(ScenarioMode.Scenario, game.Meta.Mode);
        ScenarioListEntry yur = Assert.Single(list, e => e.Name == "yuryev-polsky");
        Assert.Equal(ScenarioMode.EmptyMap, yur.Meta.Mode);
        Assert.DoesNotContain(list, e => e.Name is "s1k" or "s1k-res" or "vehicle-types" or "road-profiles");
        for (int i = 1; i < list.Count; i++)
        {
            Assert.True(list[i - 1].Meta.Order <= list[i].Meta.Order);
        }
    }
}
