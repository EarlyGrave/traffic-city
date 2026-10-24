using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Util;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Routing;

/// <summary>H2 (routing.md 9.12): запрос по оверлею, правило уровня, развёртывание плана; эквивалентность плоскому A*.</summary>
public class OverlaySearcherTests
{
    private const int Pairs = 1000;
    private readonly ITestOutputHelper _out;

    public OverlaySearcherTests(ITestOutputHelper output) => _out = output;

    private sealed class Setup
    {
        public RoutingRig Rig = null!;
        public Overlay Ov = null!;
        public OverlaySearcher Search = null!;
        public int[] StartLane = null!;
        public int[] GoalLane = null!;
        public float[] StartS = null!;
        public float[] GoalS = null!;
    }

    private static Setup Make(string scenario, int levels, float cell, int fanout = 4)
    {
        RoutingRig r = RoutingFixture.City(scenario);
        RoutingDesc d = OverlayFixture.Desc(levels, cell, fanout);
        Overlay ov = OverlayFixture.Build(r, in d);
        var s = new Setup
        {
            Rig = r,
            Ov = ov,
            Search = new OverlaySearcher(r.Net, r.Builder.Graph, ov),
            StartLane = new int[Pairs],
            GoalLane = new int[Pairs],
            StartS = new float[Pairs],
            GoalS = new float[Pairs],
        };
        var rng = Pcg32.Create(2026, 7);
        for (int i = 0; i < Pairs; i++)
        {
            s.StartLane[i] = RoutingFixture.RandomLane(r, ref rng);
            s.GoalLane[i] = RoutingFixture.RandomLane(r, ref rng);
            s.StartS[i] = rng.NextRange(0f, 1f) * r.Net.Segments[r.Net.Lanes[s.StartLane[i]].Segment].Length;
            s.GoalS[i] = rng.NextRange(0f, 1f) * r.Net.Segments[r.Net.Lanes[s.GoalLane[i]].Segment].Length;
        }

        return s;
    }

    // Новые случайные веса: RecalcCosts + волна до конца (матрицы согласованы с весами).
    private static void Reweigh(Setup s, ulong seed)
    {
        RouteGraph g = s.Rig.Builder.Graph;
        var loads = new SegmentLoad[s.Rig.Net.SegmentCount];
        var rng = Pcg32.Create(seed, 9);
        for (int i = 0; i < loads.Length; i++)
        {
            loads[i].SpeedRatio = rng.NextRange(0.05f, 1.3f); // ниже 0.2 и выше 1 обрезаются RecalcCosts
        }

        g.RecalcCosts(loads, loads.Length);
        s.Ov.AfterRecalc(g.WeightsVersion);
        s.Ov.RunWave(0);
        Assert.False(s.Ov.WaveActive);
    }

    private static float StartCost(Setup s, int i)
    {
        RouteGraph g = s.Rig.Builder.Graph;
        int u0 = RouteGraph.DirSegOfLane(s.Rig.Net, s.StartLane[i]);
        float len = s.Rig.Net.Segments[u0 >> 1].Length;
        return g.SegCost[u0] * (1f - Math.Clamp(s.StartS[i] / len, 0f, 1f));
    }

    private static void AssertClose(float expected, float actual, string what)
    {
        Assert.True(MathF.Abs(expected - actual) <= (1e-3f * expected) + 0.01f, $"{what}: expected {expected}, actual {actual}");
    }

    // Плоский эталон: независимый AStarSearcher этапа 1 (двоичная куча) по тем же весам.
    private static float FlatCost(Setup s, int i, AStarSearcher star, int[] buf, out int expanded)
    {
        RouteGraph g = s.Rig.Builder.Graph;
        int u0 = RouteGraph.DirSegOfLane(s.Rig.Net, s.StartLane[i]);
        int goal = RouteGraph.DirSegOfLane(s.Rig.Net, s.GoalLane[i]);
        var starts = new[] { u0 };
        var startG = new[] { StartCost(s, i) };
        SearchStatus st = star.Search(starts, startG, goal, -1, g.EndPos[goal], g.DirSegCount, buf, out _);
        expanded = star.LastExpanded;
        return st == SearchStatus.Found ? star.LastCost : float.PositiveInfinity;
    }

    private void CheckEquivalence(Setup s, string label)
    {
        RouteGraph g = s.Rig.Builder.Graph;
        var star = new AStarSearcher(g);
        var flatBuf = new int[g.DirSegCount + 2];
        var plan = new int[g.DirSegCount + 2];
        var path = new int[g.DirSegCount + 2];
        int found = 0;
        int usedMatrix = 0;
        long ovWork = 0, flatExp = 0, ovExp = 0, unpacks = 0;
        for (int i = 0; i < Pairs; i++)
        {
            int u0 = RouteGraph.DirSegOfLane(s.Rig.Net, s.StartLane[i]);
            int goal = RouteGraph.DirSegOfLane(s.Rig.Net, s.GoalLane[i]);
            float want = FlatCost(s, i, star, flatBuf, out int fexp);
            SearchStatus st = s.Search.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out int pl);
            if (u0 == goal)
            {
                continue; // тот же сегмент: особый случай, без поиска
            }

            if (float.IsPositiveInfinity(want))
            {
                Assert.Equal(SearchStatus.NoPath, st);
                continue;
            }

            Assert.Equal(SearchStatus.Found, st);
            found++;
            flatExp += fexp;
            ovExp += s.Search.LastExpanded;
            ovWork += s.Search.LastWork;
            AssertClose(want, s.Search.PlanCost, $"{label} pair {i} plan cost");
            Assert.Equal(u0, plan[0] & OverlaySearcher.SegMask);
            Assert.Equal(0, plan[0] >> OverlaySearcher.LevelShift);
            Assert.Equal(goal, plan[pl - 1] & OverlaySearcher.SegMask);
            for (int k = 0; k < pl; k++)
            {
                if ((plan[k] >> OverlaySearcher.LevelShift) > 0)
                {
                    usedMatrix++;
                }
            }

            long before = s.Search.Unpacks;
            Assert.Equal(SearchStatus.Found, s.Search.ExpandPlan(new ReadOnlySpan<int>(plan, 0, pl), path, out int len));
            unpacks += s.Search.Unpacks - before;
            Assert.Equal(u0, path[0]);
            Assert.Equal(goal, path[len - 1]);
            float pathCost = RoutingFixture.PathCost(g, new ReadOnlySpan<int>(path, 0, len), StartCost(s, i));
            Assert.False(float.IsNaN(pathCost), $"{label} pair {i}: развёрнутый путь содержит несуществующую дугу");
            AssertClose(want, pathCost, $"{label} pair {i} expanded path cost");
            AssertClose(s.Search.PlanCost, pathCost, $"{label} pair {i} plan vs expanded");
        }

        Assert.True(found > Pairs / 2, "слишком мало найденных пар");
        _out.WriteLine(
            $"{label}: pairs found {found}, plan elements with level>0 {usedMatrix}, unpacks {unpacks}, "
            + $"flat expansions/query {(double)flatExp / found:F0}, overlay expansions/query {(double)ovExp / found:F0}, overlay relaxations/query {(double)ovWork / found:F0}");
    }

    // H-T4: 1 000 пар, статические веса и после RecalcCosts со случайными SpeedRatio.
    [Theory]
    [InlineData("s10k", 1, 1000f)]
    [InlineData("s10k", 3, 300f)]
    [InlineData("s100k", 2, 1000f)]
    public void Query_EqualsFlatAStar_StaticAndAfterRecalc(string scenario, int levels, float cell)
    {
        Setup s = Make(scenario, levels, cell);
        CheckEquivalence(s, $"{scenario} L{levels} static");
        Reweigh(s, 11);
        CheckEquivalence(s, $"{scenario} L{levels} reweighed");
    }

    // levels = 0 (нет оверлея) и выключенный оверлей - плоский A* на тех же структурах.
    [Fact]
    public void WithoutOverlay_IsFlatAStar_AndDisabledOverlayToo()
    {
        Setup s = Make("s10k", 2, 1000f);
        RouteGraph g = s.Rig.Builder.Graph;
        var flat = new OverlaySearcher(s.Rig.Net, g, null);
        var star = new AStarSearcher(g);
        var buf = new int[g.DirSegCount + 2];
        var plan = new int[g.DirSegCount + 2];
        for (int i = 0; i < 200; i++)
        {
            int u0 = RouteGraph.DirSegOfLane(s.Rig.Net, s.StartLane[i]);
            int goal = RouteGraph.DirSegOfLane(s.Rig.Net, s.GoalLane[i]);
            if (u0 == goal)
            {
                continue;
            }

            float want = FlatCost(s, i, star, buf, out int exp);
            SearchStatus st = flat.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out int pl);
            Assert.Equal(float.IsPositiveInfinity(want) ? SearchStatus.NoPath : SearchStatus.Found, st);
            if (st == SearchStatus.Found)
            {
                AssertClose(want, flat.PlanCost, "flat plan cost");
                Assert.Equal(exp, flat.LastExpanded); // тот же алгоритм и тай-брейк: то же число раскрытий
                for (int k = 0; k < pl; k++)
                {
                    Assert.Equal(0, plan[k] >> OverlaySearcher.LevelShift);
                }
            }
        }

        s.Ov.Disabled = true;
        var disabled = new OverlaySearcher(s.Rig.Net, g, s.Ov);
        for (int i = 0; i < 50; i++)
        {
            if (RouteGraph.DirSegOfLane(s.Rig.Net, s.StartLane[i]) == RouteGraph.DirSegOfLane(s.Rig.Net, s.GoalLane[i]))
            {
                continue;
            }

            SearchStatus a = flat.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out int pa);
            SearchStatus b = disabled.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out int pb);
            Assert.Equal(a, b);
            Assert.Equal(flat.PlanCost, disabled.PlanCost);
            Assert.Equal(pa, pb);
        }
    }

    // H-T5: ленивое развёртывание (по одному элементу из стека, как будет в RouteStore) = полное; стоимость каждой развёрнутой дуги = клетка матрицы.
    [Fact]
    public void LazyUnpack_EqualsFullExpansion_AndArcCostEqualsMatrixCell()
    {
        Setup s = Make("s10k", 3, 300f);
        RouteGraph g = s.Rig.Builder.Graph;
        var plan = new int[g.DirSegCount + 2];
        var full = new int[g.DirSegCount + 2];
        var lazy = new List<int>();
        var stack = new List<int>();
        var tmp = new int[OverlaySearcher.UnpackCap];
        int checkedArcs = 0;
        for (int i = 0; i < 300; i++)
        {
            if (s.Search.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out int pl) != SearchStatus.Found || pl < 2)
            {
                continue;
            }

            Assert.Equal(SearchStatus.Found, s.Search.ExpandPlan(new ReadOnlySpan<int>(plan, 0, pl), full, out int fl));
            lazy.Clear();
            stack.Clear();
            lazy.Add(plan[0] & OverlaySearcher.SegMask);
            for (int k = pl - 1; k >= 1; k--)
            {
                stack.Add(plan[k]);
            }

            while (stack.Count > 0)
            {
                int e = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                int v = e & OverlaySearcher.SegMask;
                int q = e >> OverlaySearcher.LevelShift;
                if (q == 0)
                {
                    lazy.Add(v);
                    continue;
                }

                int a = lazy[^1];
                Assert.Equal(SearchStatus.Found, s.Search.Unpack(a, v, q, tmp, out int ul));
                float unpackCost = s.Search.UnpackCost;
                int c0 = s.Ov.CellOfNode(q, g.EndNode[a]);
                int r = s.Ov.EntryIdx[((q - 1) * g.DirSegCapacity) + a];
                int col = s.Ov.ExitIdx[((q - 1) * g.DirSegCapacity) + v];
                Assert.True(r >= 0 && col >= 0);
                float cell = s.Ov.Matrix[s.Ov.MatrixFirst[c0] + (r * s.Ov.ExitCap[c0]) + col];
                AssertClose(cell, unpackCost, $"pair {i}: unpack({a}->{v}, level {q}) vs matrix cell");
                checkedArcs++;
                Assert.Equal(v, tmp[ul - 1] & OverlaySearcher.SegMask);
                for (int k = 0; k < ul; k++)
                {
                    Assert.Equal(q - 1, tmp[k] >> OverlaySearcher.LevelShift);
                }

                for (int k = ul - 1; k >= 0; k--)
                {
                    stack.Add(tmp[k]);
                }
            }

            Assert.Equal(fl, lazy.Count);
            for (int k = 0; k < fl; k++)
            {
                Assert.Equal(full[k], lazy[k]);
            }
        }

        _out.WriteLine($"checked {checkedArcs} unpacked arcs against matrix cells");
        Assert.True(checkedArcs > 100);
    }

    // Развёртывание в полосовые шаги: чередование полоса - connector - полоса, connector'ы ведут по цепочке DirSeg.
    [Fact]
    public void Route_ProducesConnectedLaneSteps()
    {
        Setup s = Make("s10k", 3, 300f);
        RoutingRig r = s.Rig;
        var steps = new RouteStep[2 * (r.Builder.Graph.DirSegCount + 8)];
        int ok = 0;
        for (int i = 0; i < 300; i++)
        {
            int u0 = RouteGraph.DirSegOfLane(r.Net, s.StartLane[i]);
            int goal = RouteGraph.DirSegOfLane(r.Net, s.GoalLane[i]);
            if (u0 == goal)
            {
                continue;
            }

            SearchStatus st = s.Search.Route(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], steps, out int n);
            if (st != SearchStatus.Found)
            {
                continue;
            }

            ok++;
            Assert.True(n >= 3 && (n & 1) == 1);
            Assert.Equal(r.Net.Lanes[steps[0].Lane].Segment * 2 + r.Net.Lanes[steps[0].Lane].Direction, u0);
            Assert.Equal(s.GoalLane[i], steps[n - 1].Lane);
            for (int k = 0; k + 1 < n; k++)
            {
                ref readonly LaneData ld = ref r.Net.Lanes[steps[k].Lane];
                if (ld.Kind == LaneKind.Connector)
                {
                    // после connector'а - полоса того же направленного сегмента (перестроение внутри сегмента допускается, routing.md 5)
                    Assert.Equal(RouteGraph.DirSegOfLane(r.Net, ld.ToLane), RouteGraph.DirSegOfLane(r.Net, steps[k + 1].Lane));
                }
                else
                {
                    Assert.Equal(LaneKind.Connector, r.Net.Lanes[steps[k + 1].Lane].Kind);
                    Assert.Equal(steps[k].Lane, r.Net.Lanes[steps[k + 1].Lane].FromLane);
                }
            }
        }

        Assert.True(ok > 100);
    }

    [Fact]
    public void Query_SameSegment_AndBadLanes_FollowFlatRules()
    {
        Setup s = Make("s10k", 1, 1000f);
        var plan = new int[s.Rig.Builder.Graph.DirSegCount + 2];
        int lane = s.StartLane[0];
        Assert.Equal(SearchStatus.Found, s.Search.Query(lane, 5f, lane, 20f, plan, out int pl));
        Assert.Equal(1, pl);
        Assert.Equal(SearchStatus.NoPath, s.Search.Query(lane, 20f, lane, 5f, plan, out pl));
        Assert.Equal(SearchStatus.NoPath, s.Search.Query(-1, 0f, lane, 0f, plan, out _));
        Assert.Equal(SearchStatus.PathTooLong, s.Search.Query(s.StartLane[1], 0f, s.GoalLane[1], 0f, new int[1], out _));
    }

    // Работа-предел: упёрлись -> ExpansionCap, не зависание.
    [Fact]
    public void Query_WorkCap_StopsSearch()
    {
        Setup s = Make("s10k", 1, 1000f);
        var plan = new int[s.Rig.Builder.Graph.DirSegCount + 2];
        s.Search.WorkCap = 5;
        int capped = 0;
        for (int i = 0; i < 50; i++)
        {
            if (s.Search.Query(s.StartLane[i], 0f, s.GoalLane[i], 0f, plan, out _) == SearchStatus.ExpansionCap)
            {
                capped++;
            }
        }

        Assert.True(capped > 0);
    }

    // Детерминизм: два независимых прогона и повтор на том же объекте (штампы) дают побитово те же планы и стоимости.
    [Fact]
    public void Query_IsDeterministic_BitIdentical()
    {
        Setup a = Make("s10k", 3, 300f);
        Setup b = Make("s10k", 3, 300f);
        Reweigh(a, 5);
        Reweigh(b, 5);
        var pa = new int[a.Rig.Builder.Graph.DirSegCount + 2];
        var pb = new int[pa.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < 300; i++)
            {
                SearchStatus sa = a.Search.Query(a.StartLane[i], a.StartS[i], a.GoalLane[i], a.GoalS[i], pa, out int la);
                SearchStatus sb = b.Search.Query(b.StartLane[i], b.StartS[i], b.GoalLane[i], b.GoalS[i], pb, out int lb);
                Assert.Equal(sa, sb);
                Assert.Equal(la, lb);
                Assert.Equal(BitConverter.SingleToInt32Bits(a.Search.PlanCost), BitConverter.SingleToInt32Bits(b.Search.PlanCost));
                Assert.Equal(a.Search.LastWork, b.Search.LastWork);
                for (int k = 0; k < la; k++)
                {
                    Assert.Equal(pa[k], pb[k]);
                }
            }
        }
    }

    // 0 B на запрос: запрос + полное развёртывание + раскладка на полосы.
    [Fact]
    public void Query_Expand_Route_DoNotAllocate()
    {
        Setup s = Make("s10k", 3, 300f);
        RouteGraph g = s.Rig.Builder.Graph;
        var plan = new int[g.DirSegCount + 2];
        var path = new int[g.DirSegCount + 2];
        var steps = new RouteStep[2 * (g.DirSegCount + 8)];
        long bytes = AllocationProbe.Measure(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                if (s.Search.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out int pl) == SearchStatus.Found)
                {
                    s.Search.ExpandPlan(new ReadOnlySpan<int>(plan, 0, pl), path, out _);
                }

                s.Search.Route(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], steps, out _);
            }
        });
        Assert.Equal(0, bytes);
    }

    // Ключевая цифра шага: время запроса по оверлею и плоского A* на тех же 1 000 парах. Только по явной просьбе
    // (TC_QUERY_TIMING=1, Release): в обычном прогоне - без замеров времени.
    [Theory]
    [InlineData("s10k", 1, 1000f)]
    [InlineData("s100k", 2, 1000f)]
    public void Timing_OverlayVsFlat(string scenario, int levels, float cell)
    {
        if (Environment.GetEnvironmentVariable("TC_QUERY_TIMING") != "1")
        {
            return;
        }

        Setup s = Make(scenario, levels, cell);
        RouteGraph g = s.Rig.Builder.Graph;
        var star = new AStarSearcher(g);
        var flatSearch = new OverlaySearcher(s.Rig.Net, g, null);
        var buf = new int[g.DirSegCount + 2];
        var plan = new int[g.DirSegCount + 2];
        var path = new int[g.DirSegCount + 2];
        var tOv = new double[Pairs];
        var tOvFull = new double[Pairs];
        var tFlat = new double[Pairs];
        var tFlat2 = new double[Pairs];
        var wOv = new long[Pairs];
        var wFlat = new long[Pairs];
        var eOv = new long[Pairs];
        var eFlat = new long[Pairs];
        double toMs = 1000.0 / Stopwatch.Frequency;
        for (int round = 0; round < 3; round++) // раунды 0-1 - прогрев JIT, замер - раунд 2
        {
            for (int i = 0; i < Pairs; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                SearchStatus st = s.Search.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out int pl);
                long t1 = Stopwatch.GetTimestamp();
                if (st == SearchStatus.Found)
                {
                    s.Search.ExpandPlan(new ReadOnlySpan<int>(plan, 0, pl), path, out _);
                }

                long t2 = Stopwatch.GetTimestamp();
                wOv[i] = s.Search.LastWork;
                eOv[i] = s.Search.LastExpanded;
                flatSearch.Query(s.StartLane[i], s.StartS[i], s.GoalLane[i], s.GoalS[i], plan, out _);
                long t3 = Stopwatch.GetTimestamp();
                FlatCost(s, i, star, buf, out int exp);
                long t4 = Stopwatch.GetTimestamp();
                wFlat[i] = flatSearch.LastWork;
                eFlat[i] = flatSearch.LastExpanded;
                _ = exp;
                tOv[i] = (t1 - t0) * toMs;
                tOvFull[i] = (t2 - t0) * toMs;
                tFlat[i] = (t3 - t2) * toMs;
                tFlat2[i] = (t4 - t3) * toMs;
            }
        }

        _out.WriteLine(
            $"{scenario} (levels {levels}, cell {cell} m, DirSeg {g.DirSegCount}), {Pairs} pairs, ms:\n"
            + $"  overlay query         median {Pct(tOv, 0.5):F3}  p95 {Pct(tOv, 0.95):F3}  mean {Mean(tOv):F3}\n"
            + $"  overlay query+expand  median {Pct(tOvFull, 0.5):F3}  p95 {Pct(tOvFull, 0.95):F3}  mean {Mean(tOvFull):F3}\n"
            + $"  flat (IndexedHeap4)   median {Pct(tFlat, 0.5):F3}  p95 {Pct(tFlat, 0.95):F3}  mean {Mean(tFlat):F3}\n"
            + $"  flat (stage-1 A*)     median {Pct(tFlat2, 0.5):F3}  p95 {Pct(tFlat2, 0.95):F3}  mean {Mean(tFlat2):F3}\n"
            + $"  relaxations: overlay median {PctL(wOv, 0.5)} p95 {PctL(wOv, 0.95)}; flat median {PctL(wFlat, 0.5)} p95 {PctL(wFlat, 0.95)}\n"
            + $"  expansions:  overlay median {PctL(eOv, 0.5)} p95 {PctL(eOv, 0.95)}; flat median {PctL(eFlat, 0.5)} p95 {PctL(eFlat, 0.95)}");
    }

    private static double Pct(double[] v, double p)
    {
        double[] c = (double[])v.Clone();
        Array.Sort(c);
        return c[(int)Math.Min(c.Length - 1, p * c.Length)];
    }

    private static long PctL(long[] v, double p)
    {
        long[] c = (long[])v.Clone();
        Array.Sort(c);
        return c[(int)Math.Min(c.Length - 1, p * c.Length)];
    }

    private static double Mean(double[] v)
    {
        double sum = 0;
        foreach (double x in v)
        {
            sum += x;
        }

        return sum / v.Length;
    }
}
