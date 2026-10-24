using System;
using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Routing;

/// <summary>ADR-0006: вес сегментов по загрузке, плановая перемаршрутизация, детерминизм и 0 B/тик.</summary>
public class LoadAwareRoutingTests
{
    private static SegmentLoad[] FreeFlow(RoutingRig r)
    {
        var loads = new SegmentLoad[r.Net.SegmentCount];
        for (int i = 0; i < loads.Length; i++)
        {
            loads[i].SpeedRatio = 1f;
        }

        return loads;
    }

    [Fact]
    public void RecalcCosts_FollowsSpeedRatio_ClampsAndBumpsVersion()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Builder.Graph;
        SegmentLoad[] loads = FreeFlow(r);
        float free0 = g.SegCost[0];
        int v0 = g.WeightsVersion;
        loads[0].SpeedRatio = 0.5f;
        loads[1].SpeedRatio = 0f;
        loads[2].SpeedRatio = 3f;
        g.RecalcCosts(loads, r.Net.SegmentCount);
        Assert.Equal(v0 + 1, g.WeightsVersion);
        Assert.Equal(free0 * 2f, g.SegCost[0], 3);
        Assert.Equal(g.SegCost[0], g.SegCost[1]);
        Assert.Equal(r.Net.Segments[1].Length / (r.Net.Segments[1].SpeedLimit * RouteGraph.MinSpeedRatio), g.SegCost[2], 3);
        Assert.Equal(r.Net.Segments[2].Length / r.Net.Segments[2].SpeedLimit, g.SegCost[4], 3);
    }

    [Fact]
    public void NewRoute_AvoidsArtificialJam_AndStaleTrunkIsDropped()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Builder.Graph;
        var rng = Pcg32.Create(77, 3);
        int avoided = 0;
        for (int k = 0; k < 60; k++)
        {
            g.RecalcCosts(FreeFlow(r), r.Net.SegmentCount);
            int s = RoutingFixture.RandomLane(r, ref rng);
            int t = RoutingFixture.RandomLane(r, ref rng);
            r.Builder.BeginTick();
            if (r.Builder.Build(s, 0f, t, 0f, -1, -1, k, out RouteHandle h) != BuildStatus.Ok)
            {
                continue;
            }

            ReadOnlySpan<RouteStep> steps = r.Pool.GetSteps(h);
            if (steps.Length < 7)
            {
                r.Pool.Free(h);
                continue;
            }

            int jam = RouteGraph.DirSegOfLane(r.Net, steps[(steps.Length / 4) * 2].Lane);
            r.Pool.Free(h);
            if (jam == RouteGraph.DirSegOfLane(r.Net, s) || jam == RouteGraph.DirSegOfLane(r.Net, t))
            {
                continue;
            }

            SegmentLoad[] loads = FreeFlow(r);
            loads[jam >> 1].SpeedRatio = 0.0f;
            g.RecalcCosts(loads, r.Net.SegmentCount);
            r.Builder.BeginTick();
            Assert.Equal(BuildStatus.Ok, r.Builder.Build(s, 0f, t, 0f, -1, -1, k, out RouteHandle h2));
            ReadOnlySpan<RouteStep> steps2 = r.Pool.GetSteps(h2);
            bool uses = false;
            for (int j = 0; j < steps2.Length; j += 2)
            {
                uses |= (RouteGraph.DirSegOfLane(r.Net, steps2[j].Lane) >> 1) == (jam >> 1);
            }

            if (!uses)
            {
                avoided++;
            }

            r.Pool.Free(h2);
        }

        Assert.True(avoided >= 5, $"only {avoided} routes avoided the jam");
    }

    [Fact]
    public void StaleTrunk_IsRebuiltAfterWeightsChange()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Builder.Graph;
        var rng = Pcg32.Create(9, 9);
        int a = -1;
        int b = -1;
        int la = -1;
        int lb = -1;
        for (int guard = 0; guard < 10_000 && la < 0; guard++)
        {
            a = rng.NextInt(0, r.ZoneCount);
            b = rng.NextInt(0, r.ZoneCount);
            if (a == b || r.Net.Zones[a].HubNode < 0 || r.Net.Zones[b].HubNode < 0 || r.Net.Zones[a].HubNode == r.Net.Zones[b].HubNode)
            {
                continue;
            }

            la = RoutingFixture.ZoneLane(r, a, 0);
            lb = RoutingFixture.ZoneLane(r, b, 0);
            if (lb < 0)
            {
                la = -1;
            }
        }

        Assert.True(la >= 0);
        r.Builder.BeginTick();
        Assert.Equal(BuildStatus.Ok, r.Builder.Build(la, 0f, lb, 0f, a, b, 1, out RouteHandle h1));
        r.Builder.BeginTick();
        Assert.Equal(BuildStatus.Ok, r.Builder.Build(la, 0f, lb, 0f, a, b, 2, out RouteHandle h2));
        Assert.Equal(1, r.Builder.CacheHits);
        g.RecalcCosts(FreeFlow(r), r.Net.SegmentCount);
        r.Builder.BeginTick();
        Assert.Equal(BuildStatus.Ok, r.Builder.Build(la, 0f, lb, 0f, a, b, 3, out RouteHandle h3));
        Assert.Equal(1, r.Builder.CacheHits); // магистраль прежней версии весов не использована
        r.Builder.BeginTick();
        Assert.Equal(BuildStatus.Ok, r.Builder.Build(la, 0f, lb, 0f, a, b, 4, out _));
        Assert.Equal(2, r.Builder.CacheHits); // новая магистраль уже в кэше
        r.Pool.Free(h1);
        r.Pool.Free(h2);
        r.Pool.Free(h3);
    }

    [Fact]
    public void S1k_WithRecalcAndTimedReroutes_IsDeterministic()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        for (int t = 0; t < 3000; t += 100)
        {
            a.Step(100);
            b.Step(100);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }

        Assert.Equal(4, a.Counters.CostRecalcs);
        Assert.True(a.Counters.TimedReroutes > 0);
        Assert.Equal(a.Counters.TimedReroutes, b.Counters.TimedReroutes);
    }

    [Fact]
    public void S1k_RecalcAndTimedReroutes_AllocateZeroBytes()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(2500);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long recalcs = sim.Counters.CostRecalcs;
        long timed = sim.Counters.TimedReroutes;
        sim.Step(1000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sim.Counters.CostRecalcs > recalcs);
        Assert.True(sim.Counters.TimedReroutes > timed);
    }
}
