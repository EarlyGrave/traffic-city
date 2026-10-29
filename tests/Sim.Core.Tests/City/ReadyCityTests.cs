using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Scenarios;
using Sim.Scenarios.Game;

namespace Sim.Core.Tests.City;

/// <summary>«Новая игра - готовый город» (PG6, progression.md §7.3): сценарий <c>game-city</c> с прогрессией и казной и подготовка <see cref="ReadyCity"/>.</summary>
public sealed class ReadyCityTests
{
    private static LoadedScenario Load(string name) => ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json"));

    [Fact]
    public void GameCity_Scenario_HasProgressionBudgetAndAreas()
    {
        ScenarioDesc d = Load("game-city").Desc;
        Assert.True(d.Progression.Enabled);
        Assert.Equal(6, d.Progression.Milestones.Length);
        Assert.Equal(4, d.Progression.Areas.Length);
        Assert.True(d.Town.Economy.CityBudget);
        Assert.Equal(20, d.Town.Economy.CreditDays);
        Assert.True(d.Tools.HasWorldBounds);
    }

    [Fact]
    public void TargetTick_IsSixAmOfDayOne()
    {
        Assert.Equal(90_000, ReadyCity.TargetTick(72_000));
        Assert.Equal(72_000, ReadyCity.TargetTick(72_000, 0));
        Assert.Equal(1_000 + 250, ReadyCity.TargetTick(1_000));
    }

    [Fact]
    public void Advance_RejectsScenarioWithoutCity()
    {
        ScenarioDesc d = Load("s1k").Desc;
        using Simulation sim = Simulation.Create(in d);
        Assert.Throws<ArgumentException>(() => ReadyCity.Advance(sim, ReadyCityOptions.Default));
    }

    /// <summary>Полная подготовка (минуты в Release, <c>TC_SLOW=1</c>): стоп ровно в 06:00 суток 1, прогресс идёт до цели, хэш не зависит от числа потоков.</summary>
    [SlowFact]
    public void Prepare_StopsAtSixAm_AndIsThreadIndependent()
    {
        ScenarioDesc d = Load("game-city").Desc;
        long last = 0;
        var opt = new ReadyCityOptions { StopHour = 6, Progress = (done, total) => { last = done; Assert.Equal(90_000, total); } };
        Assert.Equal(CreateStatus.Ok, ReadyCity.TryPrepare(in d, new SimRuntimeOptions { WorkerThreads = 1 }, opt, out Simulation? a));
        using Simulation sa = a!;
        Assert.Equal(90_000, sa.Tick);
        Assert.Equal(90_000, last);
        Assert.True(sa.GetProgression(out ProgressionInfo p));
        Assert.Equal(0, p.Reached);
        Assert.Equal(CreateStatus.Ok, ReadyCity.TryPrepare(in d, new SimRuntimeOptions { WorkerThreads = 3 }, ReadyCityOptions.Default, out Simulation? b));
        using Simulation sb = b!;
        Assert.Equal(sa.ComputeStateHash(), sb.ComputeStateHash());
    }
}
