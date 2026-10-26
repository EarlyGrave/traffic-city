using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Intersections;

namespace Sim.Core.Tests.Network;

/// <summary>ADR-0016, D5: производные массивы <c>LaneGateNode</c>/<c>LaneLength</c> совпадают с прежним расчётом после Create, BuildRoad и SetNodeControl.</summary>
public class LaneDerivedArraysTests
{
    private static int AssertConsistent(NetworkStore net)
    {
        int gated = 0;
        for (int lane = 0; lane < net.LaneCount; lane++)
        {
            ref readonly LaneData ld = ref net.Lanes[lane];
            Assert.Equal(ld.Length, net.LaneLength[lane]);
            if (ld.Kind == LaneKind.Normal)
            {
                Assert.Equal(GateRules.GateNodeSlow(net, lane), net.LaneGateNode[lane]);
            }
            else
            {
                bool slow = GateRules.IsGatedConnectorSlow(net, in ld);
                Assert.Equal(slow, net.LaneGateNode[lane] >= 0);
                if (slow)
                {
                    Assert.Equal(ld.Node, net.LaneGateNode[lane]);
                }
            }

            if (net.LaneGateNode[lane] >= 0)
            {
                gated++;
            }
        }

        return gated;
    }

    [Fact]
    public void AfterCreate_MatchesSlowComputation()
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        using Simulation sim = Simulation.Create(in d);
        Assert.True(AssertConsistent(sim.World.Net) > 0);
    }

    [Fact]
    public void AfterBuildRoad_MatchesSlowComputation()
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        using Simulation sim = Simulation.Create(in d);
        sim.SetToolsConfig(Tools.ToolsFixture.Unbounded);
        int before = AssertConsistent(sim.World.Net);
        int lanesBefore = sim.World.Net.LaneCount;
        var r = new BuildRoadCmd { Ax = 5000, Az = 5000, Bx = 5200, Bz = 5000, SpeedLimit = 13.9f, LanesAB = 2, LanesBA = 1, Kind = RoadKind.Street };
        Assert.True(sim.Enqueue(Command.MakeBuildRoad(in r)));
        sim.Step(1);
        Assert.True(sim.World.Net.LaneCount > lanesBefore);
        AssertConsistent(sim.World.Net);
        Assert.True(AssertConsistent(sim.World.Net) >= before);
    }

    [Theory]
    [InlineData(NodeControl.Signal)]
    [InlineData(NodeControl.Yield)]
    [InlineData(NodeControl.Stop)]
    [InlineData(NodeControl.RightHandRule)]
    public void AfterSetNodeControl_MatchesSlowComputation(NodeControl control)
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        using Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, control, 1)));
        sim.Step(1);
        Assert.True(AssertConsistent(sim.World.Net) > 0);
    }

    [Fact]
    public void BuilderSetNodeControl_TogglesGateNodes()
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.None, Arm.E(1), Arm.W(1));
        using Simulation sim = Simulation.Create(in d);
        NetworkStore net = sim.World.Net;
        Assert.Equal(0, AssertConsistent(net));
        sim.World.Builder.SetNodeControl(0, NodeControl.Yield);
        Assert.True(AssertConsistent(net) > 0);
        sim.World.Builder.SetNodeControl(0, NodeControl.None);
        Assert.Equal(0, AssertConsistent(net));
    }
}
