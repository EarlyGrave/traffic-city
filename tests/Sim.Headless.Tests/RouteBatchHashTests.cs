using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Пакет параллельных запросов фазы 3 (routing-parallel.md, ADR-0032, P2): хэш мира при выключенном пакете (<c>MaxCandidates = 0</c> - прежний код)
/// равен хэшу пакета при 1 (тестовый хук без пула) / 2 / 3 / 4 потоках и не зависит от размера пакета; расхождений фильтров сбора и повтора нет.
/// </summary>
public sealed class RouteBatchHashTests
{
    private static ScenarioDesc Load(string name) => ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null)).Desc;

    private sealed class Outcome
    {
        public readonly List<ulong> Hashes = [];
        public long Batches, Committed, Inline, Mismatch;
    }

    private static Outcome Run(in ScenarioDesc d, int threads, int maxCandidates, int ticks, int every)
    {
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        RouteBatch b = sim.World.Router.Batch;
        b.MaxCandidates = maxCandidates;
        b.SingleThread = threads == 1;
        var o = new Outcome();
        for (int t = 0; t < ticks; t += every)
        {
            sim.Step(every);
            o.Hashes.Add(sim.ComputeStateHash());
        }

        o.Batches = b.Batches;
        o.Committed = b.Committed;
        o.Inline = b.Inline;
        o.Mismatch = b.Mismatch;
        return o;
    }

    private static void CheckScenario(string name, int ticks, int every, int[] threads)
    {
        ScenarioDesc d = Load(name);
        Outcome off = Run(in d, 1, 0, ticks, every);
        Assert.Equal(0, off.Batches);
        foreach (int th in threads)
        {
            Outcome on = Run(in d, th, RouteBatch.Capacity, ticks, every);
            Assert.True(on.Batches > 0 && on.Committed > 0, $"{name}/{th}: пакет не использовался");
            Assert.Equal(0, on.Mismatch);
            Assert.Equal(off.Hashes, on.Hashes);
        }
    }

    [Fact]
    public void S1k_BatchOff_Equals_BatchOn_1To4Threads() => CheckScenario("s1k", 2000, 100, [1, 2, 3, 4]);

    [Fact]
    public void S10k_BatchOff_Equals_BatchOn_1To4Threads() => CheckScenario("s10k", 2000, 100, [1, 2, 3, 4]);

    [Fact]
    public void S100k_RampBatchOff_Equals_BatchOn_1To4Threads() => CheckScenario("s100k", 600, 100, [1, 2, 3, 4]);

    [Theory]
    [InlineData("s1k", 1500, 4)]
    [InlineData("s10k", 800, 3)]
    public void BatchSize_DoesNotChangeHash(string name, int ticks, int threads)
    {
        ScenarioDesc d = Load(name);
        Outcome big = Run(in d, threads, 256, ticks, 100);
        Assert.Equal(0, big.Mismatch);
        foreach (int size in new[] { 1, 3 })
        {
            Outcome o = Run(in d, threads, size, ticks, 100);
            Assert.True(o.Committed > 0);
            Assert.Equal(big.Hashes, o.Hashes);
        }
    }
}
