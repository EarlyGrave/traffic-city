using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Tests.Intersections;

public class SignalCommandTests
{
    private static readonly int[] _greens = { 10, 6 };

    private static Simulation Make(int maxSignals = 4, int count = 2)
    {
        ScenarioDesc d = SignalFixture.Crosses(count, 1, maxSignals);
        return Simulation.Create(in d);
    }

    private static (bool Applied, int Reason) Last(Simulation sim, uint tag)
    {
        for (int i = 0; i < sim.Events.Length; i++)
        {
            SimEvent e = sim.Events[i];
            if (e.Tag == tag && e.Kind == SimEventKind.CommandApplied)
            {
                return (true, 0);
            }

            if (e.Tag == tag && e.Kind == SimEventKind.CommandRejected)
            {
                return (false, e.A);
            }
        }

        throw new Xunit.Sdk.XunitException("no result event for tag " + tag);
    }

    private static void Send(Simulation sim, in Command c)
    {
        Assert.True(sim.Enqueue(in c));
        sim.Step(1);
    }

    private static Command PlanCmd(int node, SetSignalPlanCmd p, uint tag)
    {
        p.Node = node;
        return Command.MakeSetSignalPlan(in p, tag);
    }

    [Fact]
    public void SetNodeControl_Signal_CreatesSlotWithDefaultPlan()
    {
        using Simulation sim = Make();
        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Signal, 7));
        Assert.True(Last(sim, 7).Applied);
        World w = sim.World;
        Assert.Equal(NodeControl.Signal, w.Net.Nodes[0].Control);
        Assert.Equal(0, w.Net.Nodes[0].Signal);
        Assert.Equal(1, w.SignalCount);
        Assert.Equal(1, sim.Counters.SignalizedNodes);
        Assert.Equal(0, w.Signals.Node[0]);
        Assert.Equal(SignalMode.Fixed, w.Signals.Mode[0]);

        // повтор - no-op, слот не дублируется
        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Signal, 8));
        Assert.True(Last(sim, 8).Applied);
        Assert.Equal(1, w.SignalCount);
    }

    [Fact]
    public void SetNodeControl_Rejections()
    {
        using Simulation sim = Make(maxSignals: 1);
        Send(sim, Command.MakeSetNodeControl(99, NodeControl.Signal, 1));
        Assert.Equal((false, (int)RejectReason.InvalidArgument), Last(sim, 1));
        Send(sim, Command.MakeSetNodeControl(0, (NodeControl)42, 2));
        Assert.Equal((false, (int)RejectReason.InvalidArgument), Last(sim, 2));
        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Signal, 3));
        Assert.True(Last(sim, 3).Applied);
        // Узел 5 - 4-рукавный крест: сборка (connectors, J1) сама ставит RightHandRule, поэтому сравниваем с исходным значением.
        NodeControl before5 = sim.World.Net.Nodes[5].Control;
        Send(sim, Command.MakeSetNodeControl(5, NodeControl.Signal, 4)); // второй крест, ёмкость 1
        Assert.Equal((false, (int)RejectReason.Capacity), Last(sim, 4));
        Assert.Equal(before5, sim.World.Net.Nodes[5].Control);
        Assert.Equal(-1, sim.World.Net.Nodes[5].Signal);
    }

    [Fact]
    public void SetNodeControl_AwayFromSignal_SwapRemovesSlotAndClearsLanes()
    {
        using Simulation sim = Make();
        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Signal, 1));
        Send(sim, Command.MakeSetNodeControl(5, NodeControl.Signal, 2));
        World w = sim.World;
        Assert.Equal(2, w.SignalCount);
        Assert.Equal(1, w.Net.Nodes[5].Signal);
        SignalPhase.Run(w);
        NodeData n0 = w.Net.Nodes[0];
        bool anyColored = false;
        for (int c = 0; c < n0.ConnectorCount; c++)
        {
            anyColored |= w.LaneSignals[n0.FirstConnector + c] != (byte)LaneSignal.None;
        }

        Assert.True(anyColored);

        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Yield, 3));
        Assert.True(Last(sim, 3).Applied);
        Assert.Equal(NodeControl.Yield, w.Net.Nodes[0].Control);
        Assert.Equal(-1, w.Net.Nodes[0].Signal);
        Assert.Equal(1, w.SignalCount);
        Assert.Equal(0, w.Net.Nodes[5].Signal); // переставленный узел
        Assert.Equal(5, w.Signals.Node[0]);
        for (int c = 0; c < n0.ConnectorCount; c++)
        {
            Assert.Equal((byte)LaneSignal.None, w.LaneSignals[n0.FirstConnector + c]);
        }

        // оставшийся узел продолжает работать
        SignalPhase.Run(w);
        Assert.Equal(5, w.SignalStates[0].Node);
    }

    [Fact]
    public void SetSignalPlan_AppliedOnNextTick_AndStateFollowsOffset()
    {
        using Simulation sim = Make();
        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Signal, 1));
        World w = sim.World;
        Command cmd = PlanCmd(0, SignalFixture.Plan(10, 6, 3, 1, offset: 12), 5);
        Assert.True(sim.Enqueue(in cmd));
        Assert.Equal(480, w.Signals.GreenTicks[0]); // до Step план прежний
        sim.Step(1);
        Assert.True(Last(sim, 5).Applied);
        Assert.Equal(10, w.Signals.GreenTicks[0]);
        Assert.Equal(6, w.Signals.GreenTicks[1]);
        (int ph, SignalInterval iv, int left) = SignalFixture.Expected(_greens, 3, 1, 12);
        Assert.Equal(SignalInterval.Yellow, iv); // позиция 12 в цикле [10,3,1,6,3,1]: жёлтый фазы 0, остаток 1
        Assert.Equal(ph, w.Signals.Phase[0]);
        Assert.Equal(iv, w.Signals.Interval[0]);
        Assert.Equal(left, w.Signals.TicksLeft[0]);
    }

    [Fact]
    public void SetSignalPlan_Rejections_LeavePlanUnchanged()
    {
        using Simulation sim = Make();
        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Signal, 1));
        World w = sim.World;

        SetSignalPlanCmd bad = SignalFixture.Plan(10, 6, 3, 1);
        bad.PhaseCount = 3; // у креста 2 оси
        Send(sim, PlanCmd(0, bad, 2));
        Assert.Equal((false, (int)RejectReason.InvalidArgument), Last(sim, 2));
        Assert.Equal(480, w.Signals.GreenTicks[0]);

        Send(sim, PlanCmd(0, SignalFixture.Plan(0, 6, 3, 1), 3));
        Assert.Equal((false, (int)RejectReason.InvalidArgument), Last(sim, 3));

        Send(sim, PlanCmd(0, SignalFixture.Plan(40000, 40000, 3, 1), 4));
        Assert.Equal((false, (int)RejectReason.InvalidArgument), Last(sim, 4));

        Send(sim, PlanCmd(5, SignalFixture.Plan(10, 6, 3, 1), 5)); // узел 5 - не светофор
        Assert.Equal((false, (int)RejectReason.InvalidArgument), Last(sim, 5));

        Send(sim, PlanCmd(1234, SignalFixture.Plan(10, 6, 3, 1), 6));
        Assert.Equal((false, (int)RejectReason.InvalidArgument), Last(sim, 6));
        Assert.Equal(480, w.Signals.GreenTicks[0]);
        Assert.Equal(5, sim.Counters.CommandsRejected);
    }

    [Fact]
    public void SetSignalPlan_OffAndFlashYellow_PaintLanes_AndBackToFixed()
    {
        using Simulation sim = Make();
        Send(sim, Command.MakeSetNodeControl(0, NodeControl.Signal, 1));
        World w = sim.World;
        NodeData nd = w.Net.Nodes[0];

        Send(sim, PlanCmd(0, new SetSignalPlanCmd { Mode = SignalMode.FlashYellow }, 2));
        Assert.True(Last(sim, 2).Applied);
        SignalPhase.Run(w);
        Assert.Equal(SignalInterval.Yellow, w.SignalStates[0].Interval);
        for (int c = 0; c < nd.ConnectorCount; c++)
        {
            Assert.Equal((byte)LaneSignal.Yellow, w.LaneSignals[nd.FirstConnector + c]);
        }

        Send(sim, PlanCmd(0, new SetSignalPlanCmd { Mode = SignalMode.Off }, 3));
        SignalPhase.Run(w);
        Assert.Equal(SignalInterval.Off, w.SignalStates[0].Interval);
        for (int c = 0; c < nd.ConnectorCount; c++)
        {
            Assert.Equal((byte)LaneSignal.None, w.LaneSignals[nd.FirstConnector + c]);
        }

        Send(sim, PlanCmd(0, SignalFixture.Plan(10, 6, 3, 1), 4));
        Assert.True(Last(sim, 4).Applied);
        SignalPhase.Run(w);
        Assert.Equal(SignalInterval.Green, w.SignalStates[0].Interval);
    }
}
