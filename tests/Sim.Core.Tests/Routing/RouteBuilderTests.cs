using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Util;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Routing;

public class RouteBuilderTests
{
    private readonly ITestOutputHelper _out;

    public RouteBuilderTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private readonly struct Trip
    {
        public readonly int From, To, StartLane, DestLane;

        public Trip(int from, int to, int startLane, int destLane)
        {
            From = from;
            To = to;
            StartLane = startLane;
            DestLane = destLane;
        }
    }

    /// <summary>Поездки между различными зонами с различными хабами; отбираются те, для которых маршрут строится.</summary>
    private static Trip[] Trips(RoutingRig r, int count, ulong seed)
    {
        var rng = Pcg32.Create(seed, 11);
        var list = new Trip[count];
        int n = 0;
        int guard = 0;
        while (n < count && guard++ < 100_000)
        {
            int a = rng.NextInt(0, r.ZoneCount);
            int b = rng.NextInt(0, r.ZoneCount);
            if (a == b || r.Net.Zones[a].HubNode < 0 || r.Net.Zones[b].HubNode < 0 || r.Net.Zones[a].HubNode == r.Net.Zones[b].HubNode)
            {
                continue;
            }

            int la = RoutingFixture.ZoneLane(r, a, rng.NextInt(0, 64));
            int lb = RoutingFixture.ZoneLane(r, b, rng.NextInt(0, 64));
            if (la < 0 || lb < 0)
            {
                continue;
            }

            list[n++] = new Trip(a, b, la, lb);
        }

        Assert.Equal(count, n);
        return list;
    }

    private static bool Build(RoutingRig r, in Trip t, long tick, out RouteHandle h)
    {
        r.Builder.BeginTick();
        return r.Builder.Build(t.StartLane, 0f, t.DestLane, 0f, t.From, t.To, tick, out h) == BuildStatus.Ok;
    }

    private static void AssertLaneLevelValid(RoutingRig r, ReadOnlySpan<RouteStep> steps, int startLane, int destLane)
    {
        NetworkStore net = r.Net;
        Assert.True(steps.Length >= 1 && steps.Length % 2 == 1);
        Assert.Equal(RouteGraph.DirSegOfLane(net, startLane), RouteGraph.DirSegOfLane(net, steps[0].Lane));
        Assert.Equal(destLane, steps[^1].Lane);
        Assert.Equal(-1, steps[^1].Node);
        for (int i = 0; i + 2 < steps.Length; i += 2)
        {
            ref readonly LaneData from = ref net.Lanes[steps[i].Lane];
            ref readonly LaneData conn = ref net.Lanes[steps[i + 1].Lane];
            ref readonly LaneData nextLane = ref net.Lanes[steps[i + 2].Lane];
            Assert.Equal(LaneKind.Normal, from.Kind);
            Assert.Equal(LaneKind.Connector, conn.Kind);
            Assert.Equal(steps[i].Lane, conn.FromLane);
            Assert.Equal(conn.Node, steps[i + 1].Node);
            ref readonly LaneData landing = ref net.Lanes[conn.ToLane];
            Assert.Equal((nextLane.Segment * 2) + nextLane.Direction, (landing.Segment * 2) + landing.Direction);
            bool listed = false;
            for (int k = 0; k < from.NextCount; k++)
            {
                listed |= net.LaneNext[from.FirstNext + k] == steps[i + 1].Lane;
            }

            Assert.True(listed, "connector must be reachable from previous lane");
        }
    }

    [Fact]
    public void LaneLevelRoute_IsValid_DirectAndViaTrunk()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        var rng = Pcg32.Create(5, 5);
        int ok = 0;
        for (int i = 0; i < 200; i++)
        {
            int s = RoutingFixture.RandomLane(r, ref rng);
            int t = RoutingFixture.RandomLane(r, ref rng);
            r.Builder.BeginTick();
            if (r.Builder.Build(s, 0f, t, 0f, -1, -1, i, out RouteHandle h) == BuildStatus.Ok)
            {
                AssertLaneLevelValid(r, r.Pool.GetSteps(h), s, t);
                r.Pool.Free(h);
                ok++;
            }
        }

        Assert.True(ok >= 180, "ok " + ok);

        Trip[] trips = Trips(r, 150, 77);
        for (int i = 0; i < trips.Length; i++)
        {
            Assert.True(Build(r, trips[i], 1000 + i, out RouteHandle h));
            AssertLaneLevelValid(r, r.Pool.GetSteps(h), trips[i].StartLane, trips[i].DestLane);
            Assert.Equal(1, r.Pool.RefCount[h.Index]);
            Assert.Equal(-1, r.Pool.FromZone[h.Index]);
            r.Pool.Free(h);
        }

        Assert.True(r.Builder.CacheHits > 0);
    }

    [Fact]
    public void Determinism_SameQueriesSameSteps()
    {
        ulong Run()
        {
            RoutingRig r = RoutingFixture.City("s1k");
            Trip[] trips = Trips(r, 120, 4242);
            var h = Fnv1a64.Create();
            for (int i = 0; i < trips.Length; i++)
            {
                Assert.True(Build(r, trips[i], i, out RouteHandle rh));
                foreach (RouteStep s in r.Pool.GetSteps(rh))
                {
                    h.Add(s.Lane);
                    h.Add(s.Node);
                }

                if (i % 3 == 0)
                {
                    r.Pool.Free(rh);
                }
            }

            h.Add(r.Builder.Cache.Count);
            h.Add((int)r.Builder.CacheHits);
            h.Add((int)r.Builder.ExpandedNodes);
            return h.Value;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Cache_R4_HitsMissesAndLastUsed()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        Trip[] pairs = Trips(r, 10, 31);
        // 10 различных пар зон: дубли отбрасываем простым перебором с другим seed.
        for (int i = 0; i < pairs.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                Assert.False(pairs[i].From == pairs[j].From && pairs[i].To == pairs[j].To);
            }
        }

        for (int i = 0; i < 1000; i++)
        {
            Trip t = pairs[i % 10];
            Assert.True(Build(r, t, 100 + i, out RouteHandle h));
            r.Pool.Free(h);
        }

        Assert.Equal(1000, r.Builder.Queries);
        Assert.Equal(990, r.Builder.CacheHits);
        Assert.Equal(10, r.Builder.Cache.Count);
        Assert.Equal(10, r.Pool.AliveCount);
        for (int i = 0; i < 10; i++)
        {
            int idx = r.Builder.Cache.Lookup(pairs[i].From, pairs[i].To, r.Net.Version, 5000);
            Assert.True(idx >= 0);
            Assert.Equal(0, r.Pool.RefCount[idx]);
            Assert.Equal(5000, r.Pool.LastUsedTick[idx]);
        }

        Assert.Equal(10, r.Builder.Cache.Count);
        _out.WriteLine($"cache: hits={r.Builder.CacheHits} queries={r.Builder.Queries} expanded={r.Builder.ExpandedNodes}");
    }

    [Fact]
    public void Cache_R5_Eviction_PoolNeverOverflows_Deterministic()
    {
        ulong Run()
        {
            RoutingRig r = RoutingFixture.City("s1k", maxRoutes: 16, maxSteps: 4096);
            Trip[] trips = Trips(r, 40, 9);
            for (int i = 0; i < trips.Length; i++)
            {
                Assert.True(Build(r, trips[i], i + 1, out RouteHandle h), "trip " + i);
                r.Pool.Free(h);
                Assert.True(r.Pool.AliveCount <= 16);
                Assert.True(r.Builder.Cache.Count <= 16);
            }

            Assert.Equal(r.Builder.Cache.Count, r.Pool.AliveCount);
            var hash = Fnv1a64.Create();
            for (int i = 0; i < 16; i++)
            {
                hash.Add(r.Pool.Pool.IsAlive(i) ? r.Pool.FromZone[i] * 4096 + r.Pool.ToZone[i] : -1);
                hash.Add(r.Pool.LastUsedTick[i]);
            }

            return hash.Value;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Cache_R6_LazyInvalidationByNetworkVersion()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        Trip t = Trips(r, 1, 3)[0];
        Assert.True(Build(r, t, 1, out RouteHandle h1));
        Assert.True(Build(r, t, 2, out RouteHandle h2));
        Assert.Equal(1, r.Builder.CacheHits);
        int trunk = r.Builder.Cache.Lookup(t.From, t.To, r.Net.Version, 2);
        Assert.True(trunk >= 0);
        int oldGen = r.Pool.Pool.GenerationOf(trunk);
        int[] before = new int[r.Pool.Count[h1.Index]];
        for (int i = 0; i < before.Length; i++)
        {
            before[i] = r.Pool.GetSteps(h1)[i].Lane;
        }

        r.Net.Version++;
        Assert.NotEqual(r.Net.Version, r.Builder.Graph.BuiltVersion);
        Assert.True(Build(r, t, 3, out RouteHandle h3));
        Assert.Equal(r.Net.Version, r.Builder.Graph.BuiltVersion);
        Assert.Equal(1, r.Builder.CacheHits);                 // промах: магистраль устарела
        Assert.Equal(1, r.Builder.Cache.Count);               // старая удалена, новая добавлена
        int fresh = r.Builder.Cache.Lookup(t.From, t.To, r.Net.Version, 3);
        Assert.True(fresh >= 0);
        Assert.Equal(r.Net.Version, r.Pool.NetworkVersion[fresh]);
        Assert.True(fresh != trunk || r.Pool.Pool.GenerationOf(trunk) != oldGen);

        // Маршруты, выданные раньше, не изменились.
        ReadOnlySpan<RouteStep> still = r.Pool.GetSteps(h1);
        Assert.Equal(before.Length, still.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i], still[i].Lane);
        }

        Assert.True(r.Pool.IsAlive(h2));
        Assert.True(r.Pool.IsAlive(h3));
    }

    [Fact]
    public void Compaction_KeepsLiveRoutesIntact()
    {
        RoutingRig r = RoutingFixture.City("s1k", maxRoutes: 4096, maxSteps: 6000);
        Trip[] trips = Trips(r, 200, 21);
        var keep = new System.Collections.Generic.List<(RouteHandle H, int[] Steps)>();
        for (int i = 0; i < trips.Length; i++)
        {
            Assert.True(Build(r, trips[i], i, out RouteHandle h), "trip " + i);
            if (i % 10 == 0)
            {
                ReadOnlySpan<RouteStep> s = r.Pool.GetSteps(h);
                var copy = new int[s.Length * 2];
                for (int k = 0; k < s.Length; k++)
                {
                    copy[2 * k] = s[k].Lane;
                    copy[(2 * k) + 1] = s[k].Node;
                }

                keep.Add((h, copy));
            }
            else
            {
                r.Pool.Free(h);
            }
        }

        Assert.True(r.Pool.Compactions > 0, "compactions " + r.Pool.Compactions);
        foreach ((RouteHandle h, int[] steps) in keep)
        {
            ReadOnlySpan<RouteStep> s = r.Pool.GetSteps(h);
            Assert.Equal(steps.Length / 2, s.Length);
            for (int k = 0; k < s.Length; k++)
            {
                Assert.Equal(steps[2 * k], s[k].Lane);
                Assert.Equal(steps[(2 * k) + 1], s[k].Node);
            }
        }
    }

    [Fact]
    public void ExpansionBudget_DefersWithinTick_ResetsOnBeginTick()
    {
        RoutingRig r = RoutingFixture.City("s1k", budget: 1);   // 256 раскрытий на тик
        Trip[] trips = Trips(r, 20, 8);
        int deferred = 0;
        int ok = 0;
        r.Builder.BeginTick();
        for (int i = 0; i < trips.Length; i++)
        {
            BuildStatus st = r.Builder.Build(trips[i].StartLane, 0f, trips[i].DestLane, 0f, -1, -1, 1, out RouteHandle h);
            if (st == BuildStatus.Deferred)
            {
                deferred++;
            }
            else if (st == BuildStatus.Ok)
            {
                ok++;
                r.Pool.Free(h);
            }
        }

        Assert.True(deferred > 0 && ok > 0, $"ok {ok} deferred {deferred}");
        Assert.Equal(deferred, r.Builder.Deferred);
        r.Builder.BeginTick();
        Assert.Equal(BuildStatus.Ok, r.Builder.Build(trips[0].StartLane, 0f, trips[0].DestLane, 0f, -1, -1, 2, out _));
    }

    [Fact]
    public void ZeroAllocations_1000QueriesAfterWarmup()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        Trip[] trips = Trips(r, 64, 55);
        var rng = Pcg32.Create(1, 1);
        var lanes = new int[128];
        for (int i = 0; i < lanes.Length; i++)
        {
            lanes[i] = RoutingFixture.RandomLane(r, ref rng);
        }

        void One(int i)
        {
            r.Builder.BeginTick();
            RouteHandle h;
            if ((i & 1) == 0)
            {
                Trip t = trips[(i >> 1) % trips.Length];
                r.Builder.Build(t.StartLane, 0f, t.DestLane, 0f, t.From, t.To, i, out h);
            }
            else
            {
                r.Builder.Build(lanes[i % 128], 0f, lanes[(i * 7) % 128], 0f, -1, -1, i, out h);
            }

            r.Pool.Free(h);
        }

        for (int i = 0; i < 400; i++)
        {
            One(i);
        }

        int next = 400;
        long allocated = AllocationProbe.Measure(() =>
        {
            for (int k = 0; k < 1000; k++)
            {
                One(next++);
            }
        });
        Assert.Equal(0, allocated);
        Assert.True(r.Builder.CacheHits > 100);
    }

    [Theory]
    [InlineData("s1k", 300)]
    [InlineData("s100k", 100)]
    public void Report_RealTimesAndExpansions(string scenario, int queries)
    {
        RoutingRig r = RoutingFixture.City(scenario, maxRoutes: 65_000, maxSteps: 4_000_000);
        RouteGraph g = r.Builder.Graph;
        var star = new AStarSearcher(g);
        var path = new int[g.DirSegCount + 1];
        var rng = Pcg32.Create(2026, 1);
        var starts = new int[queries];
        var goals = new int[queries];
        for (int i = 0; i < queries; i++)
        {
            starts[i] = RouteGraph.DirSegOfLane(r.Net, RoutingFixture.RandomLane(r, ref rng));
            goals[i] = RouteGraph.DirSegOfLane(r.Net, RoutingFixture.RandomLane(r, ref rng));
        }

        var s1 = new int[1];
        var g1 = new float[1];
        for (int pass = 0; pass < 2; pass++)
        {
            long total = 0, expanded = 0;
            int found = 0;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < queries; i++)
            {
                s1[0] = starts[i];
                g1[0] = g.SegCost[starts[i]];
                SearchStatus st = star.Search(s1, g1, goals[i], -1, g.EndPos[goals[i]], g.DirSegCount, path, out _);
                if (st == SearchStatus.Found)
                {
                    found++;
                }

                expanded += star.LastExpanded;
            }

            total = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (pass == 1)
            {
                double us = total * 1e6 / System.Diagnostics.Stopwatch.Frequency / queries;
                _out.WriteLine($"{scenario}: dirsegs={g.DirSegCount} arcs={g.ArcCount} A* found={found}/{queries} avg={us:F1} us avgExpanded={expanded / queries}");
            }
        }
    }
}
