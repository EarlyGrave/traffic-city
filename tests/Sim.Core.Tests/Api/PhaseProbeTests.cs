using System;
using Sim.Core.Api;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Api;

/// <summary>Зонд конца фазы <see cref="Simulation.OnPhaseEnd"/> и счётчики работы <see cref="Simulation.Work"/> (ADR-0016, hotpath.md D1).</summary>
public class PhaseProbeTests
{
    private static Simulation Make()
    {
        ScenarioDesc d = TestScenarios.Line();
        Simulation sim = Simulation.Create(in d);
        for (int k = 0; k < 6; k++)
        {
            TestScenarios.PlaceCar(sim, k % 4, 20f + (k * 9f), k);
        }

        return sim;
    }

    private sealed class Recorder
    {
        public readonly int[] Seen = new int[10 * 64];
        public int Count;

        public void OnPhase(int phase)
        {
            if (Count < Seen.Length)
            {
                Seen[Count] = phase;
            }

            Count++;
        }
    }

    [Fact]
    public void Probe_IsNullByDefault_AndCalledTenTimesPerTickInOrder()
    {
        using Simulation sim = Make();
        Assert.Null(sim.OnPhaseEnd);
        var rec = new Recorder();
        sim.OnPhaseEnd = rec.OnPhase;
        sim.Step(5);
        Assert.Equal(50, rec.Count);
        for (int k = 0; k < rec.Count; k++)
        {
            Assert.Equal((k % 10) + 1, rec.Seen[k]);
        }

        sim.OnPhaseEnd = null;
        sim.Step(3);
        Assert.Equal(50, rec.Count);
    }

    [Fact]
    public void Probe_DoesNotChangeStateHash_AndWork()
    {
        using Simulation a = Make();
        using Simulation b = Make();
        var rec = new Recorder();
        b.OnPhaseEnd = rec.OnPhase;
        a.Step(600);
        b.Step(600);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.Work.BehaviourVehicles, b.Work.BehaviourVehicles);
        Assert.Equal(a.Work.ObstacleGapCalls, b.Work.ObstacleGapCalls);
        Assert.Equal(a.Work.LeaderLookaheadHops, b.Work.LeaderLookaheadHops);
    }

    [Fact]
    public void Work_IsDeterministic_AndMonotonic()
    {
        using Simulation a = Make();
        using Simulation b = Make();
        a.Step(300);
        long beforeVehicles = a.Work.BehaviourVehicles;
        long beforeSlots = a.Work.SnapshotSlots;
        a.Step(300);
        b.Step(600);
        Assert.True(a.Work.BehaviourVehicles > beforeVehicles);
        Assert.True(a.Work.SnapshotSlots > beforeSlots);
        Assert.Equal(a.Work.BehaviourVehicles, b.Work.BehaviourVehicles);
        Assert.Equal(a.Work.LeaderLookaheadHops, b.Work.LeaderLookaheadHops);
        Assert.Equal(a.Work.ObstacleGapCalls, b.Work.ObstacleGapCalls);
        Assert.Equal(a.Work.ConflictGapCallsBehaviour, b.Work.ConflictGapCallsBehaviour);
        Assert.Equal(a.Work.ConflictGapCallsIntegrate, b.Work.ConflictGapCallsIntegrate);
        Assert.Equal(a.Work.ConflictEntriesScanned, b.Work.ConflictEntriesScanned);
        Assert.Equal(a.Work.ConflictOccupantsScanned, b.Work.ConflictOccupantsScanned);
        Assert.Equal(a.Work.GateCandidates, b.Work.GateCandidates);
        Assert.Equal(a.Work.GateTryGrantCalls, b.Work.GateTryGrantCalls);
        Assert.Equal(a.Work.GateTryGrantEntries, b.Work.GateTryGrantEntries);
        Assert.Equal(a.Work.RouteRefillChecks, b.Work.RouteRefillChecks);
        Assert.Equal(a.Work.NextConnRecomputes, b.Work.NextConnRecomputes);
    }

    [Fact]
    public void Step_WithProbeAndWork_AllocatesNothing()
    {
        using Simulation sim = Make();
        var rec = new Recorder();
        sim.OnPhaseEnd = rec.OnPhase;
        sim.Step(400);
        long bytes = AllocationProbe.Measure(() => sim.Step(50));
        Assert.Equal(0, bytes);
    }
}
