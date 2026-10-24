using System;
using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Agents;

/// <summary>Фазы 5-8 на кольце: I6, стационарный поток, детерминизм, 0 B/тик, порядок списков.</summary>
public class RingPhaseTests
{
    private readonly ITestOutputHelper _out;

    public RingPhaseTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static PhysicsFixture MixedRing(int carsPerLane, bool slow)
    {
        PhysicsFixture f = PhysicsFixture.Ring(24, 300f, 2, 512);
        f.FillRing(0, carsPerLane, 8f);
        f.FillRing(1, carsPerLane, 8f, offsetIndex: 1);
        if (slow)
        {
            // каждая 7-я машина - медленный грузовик: провоцирует обгоны и смены полосы
            for (int i = 0; i < f.V.HighWater; i += 7)
            {
                f.V.Type[i] = PhysicsFixture.Slow;
                f.V.Length[i] = f.Types[PhysicsFixture.Slow].Length;
                f.V.DesiredSpeed[i] = Sim.Core.Rules.Idm.DesiredSpeed(f.Types[PhysicsFixture.Slow], PhysicsFixture.Limit);
            }
        }

        return f;
    }

    [Fact]
    public void Ring_ConnectorsKeepLaneIndex_AndFormClosedLoop()
    {
        PhysicsFixture f = PhysicsFixture.Ring(24, 300f, 2, 8);
        for (int seg = 0; seg < 24; seg++)
        {
            for (int k = 0; k < 2; k++)
            {
                int lane = f.RingSegmentLane(seg, k);
                Assert.Equal(1, f.Net.Lanes[lane].NextCount);
                int conn = f.Net.LaneNext[f.Net.Lanes[lane].FirstNext];
                Assert.Equal(LaneKind.Connector, f.Net.Lanes[conn].Kind);
                Assert.Equal(f.RingSegmentLane((seg + 1) % 24, k), f.Net.Lanes[conn].ToLane);
            }
        }
    }

    [Fact]
    public void SteadyFlow_NeverNegativeGap_AndMeanSpeedStabilises()
    {
        PhysicsFixture f = MixedRing(carsPerLane: 28, slow: true);
        int ticks10Min = 20 * 60 * 10;
        double sumA = 0;
        double sumB = 0;
        int nA = 0;
        int nB = 0;
        for (int n = 0; n < ticks10Min + (20 * 240); n++)
        {
            f.Step();
            string? err = f.CheckInvariants();
            Assert.True(err is null, err);
            if (n >= ticks10Min - (20 * 240) && n < ticks10Min)
            {
                sumA += f.MeanSpeed();
                nA++;
            }
            else if (n >= ticks10Min)
            {
                sumB += f.MeanSpeed();
                nB++;
            }
        }

        double a = sumA / nA;
        double b = sumB / nB;
        _out.WriteLine($"steady: mean speed {a:F3} -> {b:F3} m/s, lane changes {f.Counters.LaneChanges}, aborted {f.Counters.LaneChangesAborted}, hard clamps {f.Counters.HardClamps}");
        Assert.InRange(a, 3.0, 15.0);
        Assert.True(Math.Abs(a - b) < 0.05 * a, $"mean speed drifts: {a:F3} -> {b:F3}");
        Assert.True(f.Counters.LaneChanges > 0, "ожидались смены полосы");
        Assert.Equal(56, f.V.AliveCount);
    }

    [Fact]
    public void DenseFlow_NeverNegativeGap_NoVehicleLost()
    {
        PhysicsFixture f = MixedRing(carsPerLane: 80, slow: true);
        for (int n = 0; n < 20 * 300; n++)
        {
            f.Step();
            string? err = f.CheckInvariants();
            Assert.True(err is null, err);
        }

        _out.WriteLine($"dense: mean speed {f.MeanSpeed():F3} m/s, lane changes {f.Counters.LaneChanges}, hard clamps {f.Counters.HardClamps}");
        Assert.Equal(160, f.V.AliveCount);
    }

    [Fact]
    public void TwoRuns_ProduceIdenticalStateHash()
    {
        ulong Run()
        {
            PhysicsFixture f = MixedRing(carsPerLane: 40, slow: true);
            f.Run(20 * 120);
            return f.Hash();
        }

        ulong h1 = Run();
        ulong h2 = Run();
        Assert.Equal(h1, h2);
        Assert.NotEqual(0UL, h1);
    }

    [Fact]
    public void AllPhases_Allocate_ZeroBytesPerTick()
    {
        PhysicsFixture f = MixedRing(carsPerLane: 40, slow: true);
        f.Run(400); // прогрев JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        f.Run(1000);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.True(f.Counters.LaneChanges > 0);
    }

    [Fact]
    public void ListOrder_StaysSortedByS_AfterIntegration_IncludingGhosts()
    {
        PhysicsFixture f = MixedRing(carsPerLane: 50, slow: true);
        bool sawGhost = false;
        for (int n = 0; n < 20 * 180; n++)
        {
            f.Step();
            for (int i = 0; i < f.V.HighWater; i++)
            {
                if (f.V.Pool.IsAlive(i) && f.V.GhostLane[i] != -1)
                {
                    sawGhost = true;
                }
            }

            string? err = f.CheckInvariants(); // обход каждого списка: S не убывает к голове
            Assert.True(err is null, err);
        }

        Assert.True(sawGhost);
    }

    [Fact]
    public void Throughput_CountsConnectorExits()
    {
        PhysicsFixture f = MixedRing(carsPerLane: 10, slow: false);
        f.Run(20 * 120);
        long total = 0;
        foreach (uint t in f.NodeThroughput)
        {
            total += t;
        }

        Assert.True(total > 0);
        Assert.Equal(f.Counters.ConnectorEntries, total);
    }
}
