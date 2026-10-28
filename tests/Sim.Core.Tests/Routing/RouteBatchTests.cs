using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Routing;

/// <summary>Пакет параллельных запросов фазы 3 (routing-parallel.md, ADR-0032, P2): пул маршрутов (<c>Deferred</c>), 0 B на тик.</summary>
public class RouteBatchTests
{
    private static Simulation Make(in ScenarioDesc d, int threads, int maxCandidates)
    {
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.World.Router.Batch.MaxCandidates = maxCandidates;
        sim.World.Router.Batch.SingleThread = threads == 1;
        return sim;
    }

    [Fact]
    public void PoolFull_Deferred_BatchOnEqualsOff()
    {
        ScenarioDesc d = Step3.S1k();
        d.Capacities.MaxRoutes = 48;   // мал: пул слотов упирается, фаза 3 откладывает запросы (BuildStatus.Deferred)
        ulong off;
        long deferredOff;
        using (Simulation a = Make(in d, 1, 0))
        {
            a.Step(1500);
            off = a.ComputeStateHash();
            deferredOff = a.Counters.RouteDeferred;
        }

        Assert.True(deferredOff > 0, "очередь упирается в пул маршрутов");
        foreach (int threads in new[] { 1, 4 })
        {
            using Simulation b = Make(in d, threads, RouteBatch.Capacity);
            b.Step(1500);
            Assert.Equal(off, b.ComputeStateHash());
            Assert.Equal(deferredOff, b.Counters.RouteDeferred);
            Assert.Equal(0, b.World.Router.Batch.Mismatch);
            Assert.True(b.World.Router.Batch.Committed > 0);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Step_ZeroAllocations_WithBatch(int threads)
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Make(in d, threads, RouteBatch.Capacity);
        sim.Step(2500);   // рампа, плановые запросы дрейфа и пересчёты весов (волны оверлея) уже идут
        long q0 = sim.Counters.PathfindQueries;
        long c0 = sim.World.Router.Batch.Committed;
        Assert.Equal(0, AllocationProbe.MeasureSimThreads(sim, () => sim.Step(100)));
        Assert.True(sim.Counters.PathfindQueries > q0, "в окне замера идут запросы");
        Assert.True(sim.World.Router.Batch.Committed > c0, "запросы идут через пакет");
    }
}
