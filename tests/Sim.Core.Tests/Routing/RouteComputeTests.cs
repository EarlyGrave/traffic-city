using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Routing;

/// <summary>P1 (routing-parallel.md 3.1, 8): <see cref="RouteCompute.Run"/> - чистая функция от входа, графа и оверлея; scratch на результат не влияет.</summary>
public class RouteComputeTests
{
    private const int Queries = 200;

    private struct Snapshot
    {
        public QueryResult R;
        public RouteStep[] Steps;
        public int[] Stack;
    }

    private static RouteQuery[] MakeQueries(World w)
    {
        var rng = Pcg32.Create(77, 5);
        var list = new RouteQuery[Queries];
        for (int k = 0; k < Queries; k++)
        {
            int a = NormalLane(w, ref rng);
            int b = NormalLane(w, ref rng);
            list[k] = new RouteQuery(a, rng.NextFloat01() * 3f, b, rng.NextFloat01() * 3f);
        }

        return list;
    }

    private static int NormalLane(World w, ref Pcg32 rng)
    {
        while (true)
        {
            int l = (int)rng.NextBelow((uint)w.Net.LaneCount);
            if (w.Net.Lanes[l].Kind != LaneKind.Connector && w.Net.Lanes[l].Segment >= 0)
            {
                return l;
            }
        }
    }

    private static Snapshot Run(World w, RouteScratch s, in RouteQuery q)
    {
        var r = default(QueryResult);
        RouteCompute.Run(in q, w.Net, w.Graph, w.Overlay, s, ref r);
        return new Snapshot
        {
            R = r,
            Steps = s.Steps.AsSpan(0, r.StepCount).ToArray(),
            Stack = s.Stk.AsSpan(0, r.StackLen).ToArray(),
        };
    }

    [Fact]
    public void SameQueries_TwoScratches_DifferentOrder_IdenticalResults()
    {
        ScenarioDesc d = CityWorldTests.Desc("s10k");
        d.Routing = OverlayFixture.Desc(2, 500f, customizeBudget: 3000);
        using Simulation sim = Simulation.Create(in d);
        sim.Step(300);   // оверлей достроен волнами, граф синхронен
        World w = sim.World;
        w.Router.SyncGraph();
        RouteQuery[] qs = MakeQueries(w);

        RouteScratch a = w.Router.Scratch[0];
        var b = new RouteScratch(w.Net, w.Graph, w.Overlay, w.Routes.WindowSteps, w.Routing.QueryWorkCap, planArena: true);

        var first = new Snapshot[Queries];
        for (int k = 0; k < Queries; k++)
        {
            first[k] = Run(w, a, in qs[k]);
        }

        int found = 0;
        int unpacks = 0;
        for (int k = Queries - 1; k >= 0; k--)
        {
            Snapshot x = Run(w, b, in qs[k]);
            Snapshot y = first[k];
            Assert.Equal(y.R.Stage, x.R.Stage);
            Assert.Equal(BitConverter.SingleToInt32Bits(y.R.Cost), BitConverter.SingleToInt32Bits(x.R.Cost));
            Assert.Equal(y.R.QueryWork, x.R.QueryWork);
            Assert.Equal(y.R.Expanded, x.R.Expanded);
            Assert.Equal(y.R.Unpacks, x.R.Unpacks);
            Assert.Equal(y.R.UnpackWork, x.R.UnpackWork);
            Assert.Equal(y.R.StepCount, x.R.StepCount);
            Assert.Equal(y.R.StackLen, x.R.StackLen);
            Assert.Equal(y.Stack, x.Stack);
            Assert.Equal(y.Steps.Length, x.Steps.Length);
            for (int j = 0; j < y.Steps.Length; j++)
            {
                Assert.Equal(y.Steps[j].Lane, x.Steps[j].Lane);
                Assert.Equal(y.Steps[j].Node, x.Steps[j].Node);
            }

            if (x.R.Stage == QueryStage.Found)
            {
                found++;
            }

            unpacks += x.R.Unpacks;
        }

        Assert.True(found > Queries / 2, "found " + found);
        Assert.True(unpacks > 0, "lazy unpacks");
    }
}
