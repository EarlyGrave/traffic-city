using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Network;
using Sim.Core.Util;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Routing;

public class AStarTests
{
    private readonly ITestOutputHelper _out;

    public AStarTests(ITestOutputHelper output)
    {
        _out = output;
    }

    /// <summary>Решётка n x n, шаг 120 м, по 1 полосе в каждую сторону; скорости чередуются, чтобы стоимости различались.</summary>
    private static NetworkStore Grid(int n, out NetworkBuilder b)
    {
        var store = new NetworkStore(NetworkTests.Caps(nodes: 64, segs: 128, lanes: 1024, pts: 16384));
        b = new NetworkBuilder(store);
        var ids = new int[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(i * 120, j * 120), NodeControl.None, out ids[i, j]));
            }
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                float v = 8f + (((i * 7) + (j * 3)) % 5);
                if (i + 1 < n)
                {
                    Assert.Equal(NetworkStatus.Ok, b.AddSegment(ids[i, j], ids[i + 1, j], 1, 1, v, RoadKind.Street, out _));
                }

                if (j + 1 < n)
                {
                    Assert.Equal(NetworkStatus.Ok, b.AddSegment(ids[i, j], ids[i, j + 1], 1, 1, v + 2f, RoadKind.Street, out _));
                }
            }
        }

        Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());
        return store;
    }

    private static float BruteForce(RouteGraph g, int cur, int goal, float cost, bool[] used)
    {
        if (cur == goal)
        {
            return cost;
        }

        float best = float.PositiveInfinity;
        used[cur] = true;
        for (int a = g.ArcFirst[cur]; a < g.ArcFirst[cur + 1]; a++)
        {
            int w = g.ArcTo[a];
            if (!used[w])
            {
                best = MathF.Min(best, BruteForce(g, w, goal, cost + g.ArcCost[a] + g.SegCost[w], used));
            }
        }

        used[cur] = false;
        return best;
    }

    [Fact]
    public void SmallGrid_AStarMatchesBruteForceAndDijkstra()
    {
        NetworkStore net = Grid(3, out _);
        var g = new RouteGraph(net);
        var star = new AStarSearcher(g);
        var path = new int[g.DirSegCount + 1];
        var starts = new int[1];
        var sg = new float[1];
        int checkedPairs = 0;
        for (int s = 0; s < g.DirSegCount; s++)
        {
            for (int t = 0; t < g.DirSegCount; t++)
            {
                if (s == t)
                {
                    continue;
                }

                starts[0] = s;
                sg[0] = g.SegCost[s];
                SearchStatus st = star.Search(starts, sg, t, -1, g.EndPos[t], g.DirSegCount, path, out int len);
                float brute = BruteForce(g, s, t, g.SegCost[s], new bool[g.DirSegCount]);
                float dij = RoutingFixture.Dijkstra(g, s, g.SegCost[s], t);
                if (float.IsPositiveInfinity(brute))
                {
                    Assert.Equal(SearchStatus.NoPath, st);
                    continue;
                }

                Assert.Equal(SearchStatus.Found, st);
                Assert.Equal(brute, star.LastCost, 1e-3f);
                Assert.Equal(dij, star.LastCost, 1e-3f);
                Assert.Equal(star.LastCost, RoutingFixture.PathCost(g, new ReadOnlySpan<int>(path, 0, len), g.SegCost[s]), 1e-3f);
                Assert.Equal(s, path[0]);
                Assert.Equal(t, path[len - 1]);
                checkedPairs++;
            }
        }

        Assert.True(checkedPairs > 500, "checked " + checkedPairs);
    }

    [Fact]
    public void GraphCosts_ManeuverAndSignalPenalties()
    {
        // Крест из 1+1 полос; сравниваем дуги по манёврам и влияние узла Signal.
        NetworkStore none = NetworkTests.Cross(1, out _);
        none.Nodes[0].Control = NodeControl.None;
        var g0 = new RouteGraph(none);
        NetworkStore sig = NetworkTests.Cross(1, out _);
        sig.Nodes[0].Control = NodeControl.Signal;
        var g1 = new RouteGraph(sig);
        Assert.Equal(g0.ArcCount, g1.ArcCount);

        int straight = 0, right = 0, left = 0;
        for (int u = 0; u < g0.DirSegCount; u++)
        {
            if (g0.EndNode[u] != 0)
            {
                continue;
            }

            float? straightCost = null;
            for (int a = g0.ArcFirst[u]; a < g0.ArcFirst[u + 1]; a++)
            {
                switch ((Maneuver)g0.ArcManeuver[a])
                {
                    case Maneuver.Straight: straight++; straightCost = g0.ArcCost[a]; break;
                    case Maneuver.Right: right++; break;
                    case Maneuver.Left: left++; break;
                }

                // Светофор добавляет ровно среднюю задержку к каждой дуге въезда.
                Assert.Equal(RoutingCosts.SignalDelay(RoutingCosts.DefaultCycle, RoutingCosts.DefaultGreen), g1.ArcCost[a] - g0.ArcCost[a], 1e-3f);
            }

            Assert.NotNull(straightCost);
            for (int a = g0.ArcFirst[u]; a < g0.ArcFirst[u + 1]; a++)
            {
                if ((Maneuver)g0.ArcManeuver[a] == Maneuver.Left)
                {
                    Assert.True(g0.ArcCost[a] >= straightCost!.Value + RoutingCosts.Left - 0.5f);
                }
                else if ((Maneuver)g0.ArcManeuver[a] == Maneuver.Right)
                {
                    Assert.True(g0.ArcCost[a] >= straightCost!.Value + RoutingCosts.Right - 0.5f);
                }
            }
        }

        Assert.Equal(4, straight);
        Assert.Equal(4, right);
        Assert.Equal(4, left);
    }

    [Fact]
    public void Penalties_SignalDetourChoice()
    {
        // A(0,0) - M(200,0) - B(400,0); объезд через P(200,-40) почти той же длины.
        // С светофором в M (+9.1 с) выбирается объезд, без него - прямой путь.
        for (int variant = 0; variant < 2; variant++)
        {
            var store = new NetworkStore(NetworkTests.Caps());
            var b = new NetworkBuilder(store);
            NodeControl mc = variant == 0 ? NodeControl.Signal : NodeControl.None;
            Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(0, 0), NodeControl.None, out int a));
            Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(200, 0), mc, out int m));
            Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(400, 0), NodeControl.None, out int bb));
            Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(200, -40), NodeControl.None, out int p));
            Assert.Equal(NetworkStatus.Ok, b.AddSegment(a, m, 1, 1, 13.9f, RoadKind.Street, out int sam));
            Assert.Equal(NetworkStatus.Ok, b.AddSegment(m, bb, 1, 1, 13.9f, RoadKind.Street, out _));
            Assert.Equal(NetworkStatus.Ok, b.AddSegment(a, p, 1, 1, 13.9f, RoadKind.Street, out int sap));
            Assert.Equal(NetworkStatus.Ok, b.AddSegment(p, bb, 1, 1, 13.9f, RoadKind.Street, out _));
            Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());

            var g = new RouteGraph(store);
            var star = new AStarSearcher(g);
            int[] starts = { sam * 2, sap * 2 };
            float[] sg = { g.SegCost[sam * 2], g.SegCost[sap * 2] };
            var path = new int[16];
            Assert.Equal(SearchStatus.Found, star.Search(starts, sg, -1, bb, store.Nodes[bb].Position, g.DirSegCount, path, out int len));
            bool viaP = false;
            for (int i = 0; i < len; i++)
            {
                viaP |= g.EndNode[path[i]] == p;
            }

            Assert.Equal(variant == 0, viaP);
        }
    }

    [Fact]
    public void UnreachableGoal_NoPath_ZeroAllocations_HeapNeverOverflows()
    {
        // Односторонние: S1 A->B, S2 B->C, S3 D->C. Цель S3 (D->C) из S1 недостижима.
        var store = new NetworkStore(NetworkTests.Caps());
        var b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(0, 0), NodeControl.None, out int a));
        Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(100, 0), NodeControl.None, out int bn));
        Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(200, 0), NodeControl.None, out int c));
        Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(200, 100), NodeControl.None, out int d));
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(a, bn, 1, 0, 13.9f, RoadKind.Street, out int s1));
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(bn, c, 1, 0, 13.9f, RoadKind.Street, out int s2));
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(d, c, 1, 0, 13.9f, RoadKind.Street, out int s3));
        Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());

        var graph = new RouteGraph(store);
        graph.Rebuild();
        var search = new OverlaySearcher(store, graph, null);
        var steps = new RouteStep[64];
        int startLane = store.Segments[s1].FirstLaneAB;
        int goodLane = store.Segments[s2].FirstLaneAB;
        int badLane = store.Segments[s3].FirstLaneAB;

        Assert.Equal(SearchStatus.Found, search.Route(startLane, 0, goodLane, 10, steps, out int count));
        Assert.Equal(3, count);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(SearchStatus.NoPath, search.Route(startLane, 0, badLane, 10, steps, out _));
        }

        int wrong = 0;
        long allocated = AllocationProbe.Measure(
            () =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    if (search.Route(startLane, 0, badLane, 10, steps, out _) != SearchStatus.NoPath)
                    {
                        wrong++;
                    }
                }
            },
            out _);
        Assert.Equal(0, wrong);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void S1k_AStarEqualsDijkstra_100RandomPairs()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Graph;
        var star = new AStarSearcher(g);
        var path = new int[g.DirSegCount + 1];
        var rng = Pcg32.Create(12345, 7);
        int found = 0;
        long expandedTotal = 0;
        for (int q = 0; q < 100; q++)
        {
            int s = RouteGraph.DirSegOfLane(r.Net, RoutingFixture.RandomLane(r, ref rng));
            int t = RouteGraph.DirSegOfLane(r.Net, RoutingFixture.RandomLane(r, ref rng));
            SearchStatus st = star.Search(new[] { s }, new[] { g.SegCost[s] }, t, -1, g.EndPos[t], g.DirSegCount, path, out int len);
            float dij = RoutingFixture.Dijkstra(g, s, g.SegCost[s], t);
            if (float.IsPositiveInfinity(dij))
            {
                Assert.Equal(SearchStatus.NoPath, st);
                continue;
            }

            Assert.Equal(SearchStatus.Found, st);
            Assert.Equal(dij, star.LastCost, dij * 1e-4f);
            Assert.Equal(star.LastCost, RoutingFixture.PathCost(g, new ReadOnlySpan<int>(path, 0, len), g.SegCost[s]), star.LastCost * 1e-4f);
            found++;
            expandedTotal += star.LastExpanded;
        }

        Assert.True(found >= 90, "found " + found);
        _out.WriteLine($"s1k: dirsegs={g.DirSegCount} arcs={g.ArcCount} vmax={g.VMax:F1} found={found}/100 avgExpanded={expandedTotal / Math.Max(1, found)}");
    }

    [Fact]
    public void Search_IsDeterministic()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Graph;
        var a = new AStarSearcher(g);
        var b = new AStarSearcher(g);
        var pa = new int[g.DirSegCount + 1];
        var pb = new int[g.DirSegCount + 1];
        var rng = Pcg32.Create(99, 3);
        for (int q = 0; q < 50; q++)
        {
            int s = RouteGraph.DirSegOfLane(r.Net, RoutingFixture.RandomLane(r, ref rng));
            int t = RouteGraph.DirSegOfLane(r.Net, RoutingFixture.RandomLane(r, ref rng));
            SearchStatus sa = a.Search(new[] { s }, new[] { 0f }, t, -1, g.EndPos[t], g.DirSegCount, pa, out int la);
            SearchStatus sb = b.Search(new[] { s }, new[] { 0f }, t, -1, g.EndPos[t], g.DirSegCount, pb, out int lb);
            Assert.Equal(sa, sb);
            Assert.Equal(la, lb);
            Assert.True(new ReadOnlySpan<int>(pa, 0, la).SequenceEqual(new ReadOnlySpan<int>(pb, 0, lb)));
        }
    }
}
