using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Tests.Intersections;

public class SignalMaskTests
{
    private static Simulation Make(int lanes, bool five = false)
    {
        ScenarioDesc d = SignalFixture.Crosses(2, lanes, 4, five);
        return Simulation.Create(in d);
    }

    private static void Enable(Simulation sim, int node)
    {
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(node, NodeControl.Signal, 1)));
        sim.Step(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FourWay_TwoPhases_StraightXAndStraightZNeverShareAPhase(int lanes)
    {
        using Simulation sim = Make(lanes);
        Enable(sim, 0);
        World w = sim.World;
        SignalStore s = w.Signals;
        Assert.Equal(1, s.Count);
        Assert.Equal(2, s.PhaseCount[0]);

        NodeData nd = w.Net.Nodes[0];
        ulong m0 = s.PhaseMask[0];
        ulong m1 = s.PhaseMask[1];
        Assert.Equal(0UL, m0 & m1);
        int all = nd.ConnectorCount;
        Assert.Equal((1UL << all) - 1UL, m0 | m1);
        int sx = 0, sz = 0;
        for (int c = 0; c < all; c++)
        {
            ref readonly LaneData l = ref w.Net.Lanes[nd.FirstConnector + c];
            bool in0 = ((m0 >> c) & 1UL) != 0;
            if (SignalFixture.IsStraightX(w.Net, in l))
            {
                Assert.True(in0, "прямо X - фаза 0");
                sx++;
            }

            if (SignalFixture.IsStraightZ(w.Net, in l))
            {
                Assert.False(in0, "прямо Z - фаза 1");
                sz++;
            }
        }

        Assert.Equal(2 * lanes, sx);
        Assert.Equal(2 * lanes, sz);
    }

    [Fact]
    public void EachPhase_ContainsOnlyConnectorsFromOneAxis()
    {
        using Simulation sim = Make(2);
        Enable(sim, 0);
        World w = sim.World;
        NodeData nd = w.Net.Nodes[0];
        for (int ph = 0; ph < 2; ph++)
        {
            ulong m = w.Signals.PhaseMask[ph];
            for (int c = 0; c < nd.ConnectorCount; c++)
            {
                if (((m >> c) & 1UL) == 0)
                {
                    continue;
                }

                ref readonly LaneData l = ref w.Net.Lanes[nd.FirstConnector + c];
                ref readonly LaneData from = ref w.Net.Lanes[l.FromLane];
                ref readonly SegmentData seg = ref w.Net.Segments[from.Segment];
                int other = seg.NodeA == 0 ? seg.NodeB : seg.NodeA;
                bool alongX = w.Net.Nodes[other].Position.Z == 0f;
                Assert.Equal(ph == 0, alongX);
            }
        }
    }

    [Fact]
    public void FiveWay_DiagonalArm_GivesThreePhasesWithDefaultPlan()
    {
        using Simulation sim = Make(1, five: true);
        Enable(sim, 0);
        World w = sim.World;
        SignalStore s = w.Signals;
        Assert.Equal(3, s.PhaseCount[0]);
        ulong union = 0;
        for (int k = 0; k < 3; k++)
        {
            ulong m = s.PhaseMask[k];
            Assert.NotEqual(0UL, m);
            Assert.Equal(0UL, union & m);
            union |= m;
        }

        Assert.Equal((1UL << w.Net.Nodes[0].ConnectorCount) - 1UL, union);
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(320, s.GreenTicks[k]); // 16 с при 3 фазах
        }
    }

    [Fact]
    public void DefaultPlan_TwoPhases_24s_3s_1s()
    {
        using Simulation sim = Make(1);
        Enable(sim, 0);
        SignalStore s = sim.World.Signals;
        Assert.Equal(480, s.GreenTicks[0]);
        Assert.Equal(480, s.GreenTicks[1]);
        Assert.Equal(60, s.YellowTicks[0]);
        Assert.Equal(20, s.AllRedTicks[0]);
        Assert.Equal(0, s.OffsetTicks[0]);
        Assert.Equal(2 * (480 + 60 + 20), s.CycleOf(0));
    }
}
