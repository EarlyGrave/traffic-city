using System;
using System.Globalization;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Параллельная фаза 4 по узлам (scale.md §4, S1, ADR-0017): хэш не зависит от числа потоков (1/2/3/4) на s10k (<c>off</c>, <c>all_meso</c> обе модели) и
/// s10k-peds; последовательный обход узлов в обратном порядке даёт тот же хэш (задания узлов независимы).
/// </summary>
public sealed class GateParallelTests
{
    private static ScenarioDesc Load(string name, LodMode mode, MesoModel model)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null)).Desc;
        d.Lod.Mode = mode;
        d.Lod.MesoModel = model;
        return d;
    }

    private static string Run(in ScenarioDesc d, int threads, bool reverse, int ticks)
    {
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.World.Gate.ReverseOrder = reverse;
        sim.Step(ticks);
        return sim.ComputeStateHash().ToString("x16", CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("s10k", LodMode.Off, MesoModel.Kinematic, 500)]
    [InlineData("s10k", LodMode.AllMeso, MesoModel.Kinematic, 500)]
    [InlineData("s10k", LodMode.AllMeso, MesoModel.Queue, 500)]
    [InlineData("s10k-peds", LodMode.Off, MesoModel.Kinematic, 300)]
    public void Hash_IsSame_For1To4Threads(string name, LodMode mode, MesoModel model, int ticks)
    {
        ScenarioDesc d = Load(name, mode, model);
        string h1 = Run(in d, 1, false, ticks);
        for (int t = 2; t <= 4; t++)
        {
            Assert.Equal(h1, Run(in d, t, false, ticks));
        }
    }

    [Theory]
    [InlineData("s1k", LodMode.Off, MesoModel.Kinematic, 3000)]
    [InlineData("s10k", LodMode.AllMeso, MesoModel.Queue, 400)]
    [InlineData("s1k", LodMode.AllMeso, MesoModel.Queue, 1500)]
    [InlineData("s10k-peds", LodMode.Off, MesoModel.Kinematic, 300)]
    public void ReverseNodeOrder_GivesSameHash(string name, LodMode mode, MesoModel model, int ticks)
    {
        ScenarioDesc d = Load(name, mode, model);
        Assert.Equal(Run(in d, 1, false, ticks), Run(in d, 1, true, ticks));
    }
}
