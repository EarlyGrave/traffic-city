using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Util;

namespace Sim.Core.Tests.Routing;

/// <summary>VC2 (vehicle-classes.md §2.2, §7 п. 3): фильтр запретов классов в оверлее и плоском поиске; эквивалентность эталонной Дейкстре с запретами.</summary>
public class AccessMaskRoutingTests
{
    private const int Pairs = 200;

    // Запрет грузовикам на каждой 5-й улице (обе стороны).
    private static void DenyTrucks(RoutingRig r, Overlay? ov)
    {
        for (int s = 0; s < r.Net.SegmentCount; s += 5)
        {
            r.Net.Segments[s].Deny = VehicleAccess.Truck;
        }

        r.Graph.RecalcDeny();
        ov?.RecalcCellDeny();
    }

    // Эталон: Дейкстра по DirSeg, вершины с запретом для access не входят в путь, кроме цели (старт - корень, не проверяется).
    private static float Reference(RouteGraph g, int start, float startG, int goal, byte access)
    {
        var dist = new Dictionary<int, float> { [start] = startG };
        var pq = new PriorityQueue<int, float>();
        pq.Enqueue(start, startG);
        var done = new HashSet<int>();
        while (pq.TryDequeue(out int v, out float d))
        {
            if (!done.Add(v))
            {
                continue;
            }

            if (v == goal)
            {
                return d;
            }

            for (int a = g.ArcFirst[v]; a < g.ArcFirst[v + 1]; a++)
            {
                int w = g.ArcTo[a];
                if (w != goal && !VehicleAccess.Allowed(access, g.DirSegDeny[w]))
                {
                    continue;
                }

                float nd = d + g.ArcCost[a] + g.SegCost[w];
                if (!dist.TryGetValue(w, out float old) || nd < old)
                {
                    dist[w] = nd;
                    pq.Enqueue(w, nd);
                }
            }
        }

        return float.PositiveInfinity;
    }

    private static float StartCost(RoutingRig r, int lane, float s)
    {
        int u0 = RouteGraph.DirSegOfLane(r.Net, lane);
        float len = r.Net.Segments[u0 >> 1].Length;
        return r.Graph.SegCost[u0] * (1f - Math.Clamp(s / len, 0f, 1f));
    }

    private static void AssertClose(float expected, float actual, string what) =>
        Assert.True(MathF.Abs(expected - actual) <= (1e-3f * expected) + 0.01f, $"{what}: expected {expected}, actual {actual}");

    [Theory]
    [InlineData(0, 1000f)]
    [InlineData(1, 1000f)]
    [InlineData(2, 600f)]
    [InlineData(3, 300f)]
    public void TruckQuery_EqualsFilteredDijkstra_CarUnchanged(int levels, float cell)
    {
        RoutingRig r = RoutingFixture.City("s10k");
        Overlay? ov = levels > 0 ? OverlayFixture.Build(r, OverlayFixture.Desc(levels, cell)) : null;
        var search = new OverlaySearcher(r.Net, r.Graph, ov);
        RouteGraph g = r.Graph;
        var plan = new int[g.DirSegCount + 2];
        var path = new int[g.DirSegCount + 2];
        var rng = Pcg32.Create(2026, 39);
        var start = new int[Pairs];
        var goal = new int[Pairs];
        var ss = new float[Pairs];
        var gs = new float[Pairs];
        var carBefore = new float[Pairs];
        for (int i = 0; i < Pairs; i++)
        {
            start[i] = RoutingFixture.RandomLane(r, ref rng);
            goal[i] = RoutingFixture.RandomLane(r, ref rng);
            ss[i] = rng.NextRange(0f, 1f) * r.Net.Segments[r.Net.Lanes[start[i]].Segment].Length;
            gs[i] = rng.NextRange(0f, 1f) * r.Net.Segments[r.Net.Lanes[goal[i]].Segment].Length;
            carBefore[i] = search.Query(start[i], ss[i], goal[i], gs[i], plan, out _, VehicleAccess.Car) == SearchStatus.Found ? search.PlanCost : float.PositiveInfinity;
        }

        DenyTrucks(r, ov);
        Assert.Equal(VehicleAccess.Truck, g.DenyUnion);
        int found = 0, detours = 0;
        for (int i = 0; i < Pairs; i++)
        {
            int u0 = RouteGraph.DirSegOfLane(r.Net, start[i]);
            int t = RouteGraph.DirSegOfLane(r.Net, goal[i]);
            if (u0 == t)
            {
                continue;
            }

            // Легковая: в сети нет её запретов - прежний путь, та же стоимость.
            SearchStatus cst = search.Query(start[i], ss[i], goal[i], gs[i], plan, out _, VehicleAccess.Car);
            Assert.Equal(float.IsPositiveInfinity(carBefore[i]) ? SearchStatus.NoPath : SearchStatus.Found, cst);
            if (cst == SearchStatus.Found)
            {
                Assert.Equal(carBefore[i], search.PlanCost);
            }

            float want = Reference(g, u0, StartCost(r, start[i], ss[i]), t, VehicleAccess.Truck);
            SearchStatus st = search.Query(start[i], ss[i], goal[i], gs[i], plan, out int pl, VehicleAccess.Truck);
            if (float.IsPositiveInfinity(want))
            {
                Assert.Equal(SearchStatus.NoPath, st);
                continue;
            }

            Assert.Equal(SearchStatus.Found, st);
            found++;
            AssertClose(want, search.PlanCost, $"L{levels} pair {i} truck plan cost");
            detours += search.PlanCost > carBefore[i] + 0.01f ? 1 : 0;

            // Развёрнутый путь не заходит на запрещённые DirSeg (кроме старта и цели).
            Assert.Equal(SearchStatus.Found, search.ExpandPlan(new ReadOnlySpan<int>(plan, 0, pl), path, out int len));
            for (int k = 1; k < len - 1; k++)
            {
                Assert.True(VehicleAccess.Allowed(VehicleAccess.Truck, g.DirSegDeny[path[k]]), $"L{levels} pair {i}: путь проходит через запрещённый DirSeg {path[k]}");
            }

            AssertClose(want, RoutingFixture.PathCost(g, new ReadOnlySpan<int>(path, 0, len), StartCost(r, start[i], ss[i])), $"L{levels} pair {i} expanded");
        }

        Assert.True(found > Pairs / 2, "слишком мало найденных пар");
        Assert.True(detours > 0, "запрет не повлиял ни на один маршрут");
        if (levels > 0)
        {
            Assert.True(search.DenyDescends > 0, "ячейки с запретом должны спускаться на уровень ниже");
        }
    }

    // Единственный путь - через запрещённую улицу: NoPath; старт или цель на запрещённой улице - путь есть.
    [Fact]
    public void SinglePathDenied_NoPath_StartAndGoalExempt()
    {
        RoutingRig r = RoutingFixture.City("s10k");
        RouteGraph g = r.Graph;
        var search = new OverlaySearcher(r.Net, g, null);
        var plan = new int[g.DirSegCount + 2];
        var rng = Pcg32.Create(7, 3);

        // Пара с путём из >= 3 DirSeg: запрет на всех DirSeg, кроме старта и цели, у их сегментов.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int a = RoutingFixture.RandomLane(r, ref rng);
            int b = RoutingFixture.RandomLane(r, ref rng);
            int u0 = RouteGraph.DirSegOfLane(r.Net, a);
            int t = RouteGraph.DirSegOfLane(r.Net, b);
            if ((u0 >> 1) == (t >> 1) || search.Query(a, 0f, b, 1f, plan, out int pl) != SearchStatus.Found || pl < 3)
            {
                continue;
            }

            // Старт и цель - на запрещённых улицах: путь есть (исключение первого и последнего сегмента).
            r.Net.Segments[u0 >> 1].Deny = VehicleAccess.Truck;
            r.Net.Segments[t >> 1].Deny = VehicleAccess.Truck;
            g.RecalcDeny();
            Assert.Equal(SearchStatus.Found, search.Query(a, 0f, b, 1f, plan, out _, VehicleAccess.Truck));

            // Все улицы, кроме старта и цели, запрещены: пути нет; легковой - есть.
            for (int s = 0; s < r.Net.SegmentCount; s++)
            {
                r.Net.Segments[s].Deny = VehicleAccess.Truck;
            }

            g.RecalcDeny();
            Assert.Equal(SearchStatus.NoPath, search.Query(a, 0f, b, 1f, plan, out _, VehicleAccess.Truck));
            Assert.Equal(SearchStatus.Found, search.Query(a, 0f, b, 1f, plan, out _, VehicleAccess.Car));
            return;
        }

        Assert.Fail("не нашлось пары с путём из трёх и более DirSeg");
    }

    // DirSegDeny: улица OR (AND полос направления); одна разрешённая полоса открывает направление.
    [Fact]
    public void DirSegDeny_IsStreetOrAndOfLanes()
    {
        RoutingRig r = RoutingFixture.City("s10k");
        NetworkStore n = r.Net;
        int seg = -1;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (n.Segments[s].LanesAB >= 2 && n.Segments[s].LanesBA >= 1)
            {
                seg = s;
                break;
            }
        }

        Assert.True(seg >= 0);
        ref SegmentData sd = ref n.Segments[seg];
        n.SetLaneDeny(sd.FirstLaneAB, LaneDeny.BusLane);
        r.Graph.RecalcDeny();
        Assert.Equal(0, r.Graph.DirSegDeny[seg * 2]);   // вторая полоса AB открыта
        for (int k = 0; k < sd.LanesAB; k++)
        {
            n.SetLaneDeny(sd.FirstLaneAB + k, LaneDeny.BusLane);
        }

        r.Graph.RecalcDeny();
        Assert.Equal(LaneDeny.BusLane, r.Graph.DirSegDeny[seg * 2]);
        Assert.Equal(0, r.Graph.DirSegDeny[(seg * 2) + 1]);
        sd.Deny = VehicleAccess.Truck;
        r.Graph.RecalcDeny();
        Assert.Equal(LaneDeny.BusLane | VehicleAccess.Truck, r.Graph.DirSegDeny[seg * 2]);
        Assert.Equal(VehicleAccess.Truck, r.Graph.DirSegDeny[(seg * 2) + 1]);
        Assert.Equal((byte)(LaneDeny.BusLane | VehicleAccess.Truck), r.Graph.DenyUnion);
        Assert.True((n.Lanes[sd.FirstLaneAB].Flags & LaneFlags.BusOnly) != 0, "зеркало BusOnly");
        n.SetLaneDeny(sd.FirstLaneAB, 0);
        Assert.Equal(0, n.Lanes[sd.FirstLaneAB].Flags & LaneFlags.BusOnly);
    }
}
