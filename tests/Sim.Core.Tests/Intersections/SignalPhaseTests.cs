using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Intersections;

public class SignalPhaseTests
{
    private static readonly int[] _g = { 10, 6 };

    private static Simulation MakeWithPlan(SetSignalPlanCmd plan, int lanes = 1)
    {
        ScenarioDesc d = SignalFixture.Crosses(2, lanes, 4);
        Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        plan.Node = 0;
        Assert.True(sim.Enqueue(Command.MakeSetSignalPlan(in plan, 2)));
        sim.Step(1);
        return sim;
    }

    [Fact]
    public void Cycle_FollowsGreenYellowAllRedPerTick()
    {
        // фазы: G0=10, Y=3, A=1, G1=6, Y=3, A=1; цикл 24 тика
        using Simulation sim = MakeWithPlan(SignalFixture.Plan(10, 6, 3, 1));
        World w = sim.World;
        for (int n = 0; n < 24 * 3; n++)
        {
            SignalPhase.Run(w);
            (int ph, SignalInterval iv, int left) = SignalFixture.Expected(_g, 3, 1, n);
            SignalState st = w.SignalStates[0];
            Assert.Equal(0, st.Node);
            Assert.Equal(ph, st.Phase);
            Assert.Equal(iv, st.Interval);
            Assert.Equal(left, st.TicksToChange);
        }
    }

    [Fact]
    public void Cycle_IntervalLengthsAreExact()
    {
        using Simulation sim = MakeWithPlan(SignalFixture.Plan(10, 6, 3, 1));
        World w = sim.World;
        var counts = new int[2, 3];
        for (int n = 0; n < 24; n++)
        {
            SignalPhase.Run(w);
            counts[w.SignalStates[0].Phase, (int)w.SignalStates[0].Interval]++;
        }

        Assert.Equal(10, counts[0, 0]);
        Assert.Equal(3, counts[0, 1]);
        Assert.Equal(1, counts[0, 2]);
        Assert.Equal(6, counts[1, 0]);
        Assert.Equal(3, counts[1, 1]);
        Assert.Equal(1, counts[1, 2]);
    }

    [Fact]
    public void ZeroYellowAndAllRed_AreSkipped()
    {
        using Simulation sim = MakeWithPlan(SignalFixture.Plan(4, 3, 0, 0));
        World w = sim.World;
        int[] g = { 4, 3 };
        for (int n = 0; n < 30; n++)
        {
            SignalPhase.Run(w);
            (int ph, SignalInterval iv, _) = SignalFixture.Expected(g, 0, 0, n);
            Assert.Equal(SignalInterval.Green, iv);
            Assert.Equal(ph, w.SignalStates[0].Phase);
            Assert.Equal(SignalInterval.Green, w.SignalStates[0].Interval);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(100)]
    public void Offset_ShiftsCycle(int offset)
    {
        using Simulation sim = MakeWithPlan(SignalFixture.Plan(10, 6, 3, 1, offset));
        World w = sim.World;
        for (int n = 0; n < 60; n++)
        {
            SignalPhase.Run(w);
            (int ph, SignalInterval iv, int left) = SignalFixture.Expected(_g, 3, 1, offset + n);
            Assert.Equal(ph, w.SignalStates[0].Phase);
            Assert.Equal(iv, w.SignalStates[0].Interval);
            Assert.Equal(left, w.SignalStates[0].TicksToChange);
        }
    }

    [Fact]
    public void LaneSignals_ColorConnectorsOfGreenAxis_AndLeaveOtherLanesNone()
    {
        using Simulation sim = MakeWithPlan(SignalFixture.Plan(10, 6, 3, 1), lanes: 2);
        World w = sim.World;
        NodeData nd = w.Net.Nodes[0];
        for (int n = 0; n < 24; n++)
        {
            SignalPhase.Run(w);
            (int ph, SignalInterval iv, _) = SignalFixture.Expected(_g, 3, 1, n);
            for (int c = 0; c < nd.ConnectorCount; c++)
            {
                int lane = nd.FirstConnector + c;
                // ось соответствует фазе: прямо X <-> фаза 0
                bool xAxisIn = ((w.Signals.PhaseMask[0] >> c) & 1UL) != 0;
                bool inPhase = xAxisIn == (ph == 0);
                LaneSignal expected = !inPhase || iv == SignalInterval.AllRed
                    ? LaneSignal.Red
                    : iv == SignalInterval.Green ? LaneSignal.Green : LaneSignal.Yellow;
                Assert.Equal((byte)expected, w.LaneSignals[lane]);
            }
        }

        for (int i = 0; i < w.Net.LaneCount; i++)
        {
            bool ofSignalNode = i >= nd.FirstConnector && i < nd.FirstConnector + nd.ConnectorCount;
            if (!ofSignalNode)
            {
                Assert.Equal((byte)LaneSignal.None, w.LaneSignals[i]);
            }
        }
    }

    [Fact]
    public void LaneSignals_EveryInputLaneOfSignalNodeHasConnectorsOfOneColor_StraightXvsZNeverBothGreen()
    {
        using Simulation sim = MakeWithPlan(SignalFixture.Plan(10, 6, 3, 1), lanes: 2);
        World w = sim.World;
        NodeData nd = w.Net.Nodes[0];
        for (int n = 0; n < 48; n++)
        {
            SignalPhase.Run(w);
            bool greenX = false, greenZ = false;
            for (int c = 0; c < nd.ConnectorCount; c++)
            {
                ref readonly LaneData l = ref w.Net.Lanes[nd.FirstConnector + c];
                bool green = w.LaneSignals[nd.FirstConnector + c] == (byte)LaneSignal.Green;
                greenX |= green && SignalFixture.IsStraightX(w.Net, in l);
                greenZ |= green && SignalFixture.IsStraightZ(w.Net, in l);
            }

            Assert.False(greenX && greenZ);
        }

        // для каждой входной полосы все её connector'ы одного цвета
        SignalPhase.Run(w);
        for (int c = 0; c < nd.ConnectorCount; c++)
        {
            for (int c2 = c + 1; c2 < nd.ConnectorCount; c2++)
            {
                int a = nd.FirstConnector + c;
                int b = nd.FirstConnector + c2;
                if (w.Net.Lanes[a].FromLane == w.Net.Lanes[b].FromLane)
                {
                    Assert.Equal(w.LaneSignals[a], w.LaneSignals[b]);
                }
            }
        }
    }

    [Fact]
    public void Determinism_TwoWorldsSameCommands_SameSignalOutputs()
    {
        ulong Run()
        {
            using Simulation sim = MakeWithPlan(SignalFixture.Plan(10, 6, 3, 1, 7));
            World w = sim.World;
            ulong h = 1469598103934665603UL;
            for (int n = 0; n < 500; n++)
            {
                SignalPhase.Run(w);
                for (int i = 0; i < w.SignalCount; i++)
                {
                    SignalState s = w.SignalStates[i];
                    h = (h ^ (ulong)(s.Node + (s.Phase << 8) + ((int)s.Interval << 16) + (s.TicksToChange << 20))) * 1099511628211UL;
                }

                for (int i = 0; i < w.Net.LaneCount; i++)
                {
                    h = (h ^ w.LaneSignals[i]) * 1099511628211UL;
                }
            }

            return h;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void S1k_SeedsBecomeSignals_WithCorrectPhasesAndPlans()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int added = w.Signals.InitFromSeeds(w);
        Assert.Equal(25, added);
        Assert.Equal(25, w.SignalCount);
        for (int slot = 0; slot < added; slot++)
        {
            int node = w.Signals.Node[slot];
            Assert.Equal(slot, w.Net.Nodes[node].Signal);
            Assert.Equal(NodeControl.Signal, w.Net.Nodes[node].Control);
            SignalPlanSeed2 seed = SignalPlanSeed2.Of(w, node);
            Assert.Equal(seed.PhaseCount, w.Signals.PhaseCount[slot]);
            Assert.Equal(seed.Green0, w.Signals.GreenTicks[slot * 8]);
            Assert.Equal(seed.Green1, w.Signals.GreenTicks[(slot * 8) + 1]);
            Assert.Equal(seed.Yellow, w.Signals.YellowTicks[slot]);
            Assert.Equal(seed.AllRed, w.Signals.AllRedTicks[slot]);
            Assert.Equal(seed.Offset, w.Signals.OffsetTicks[slot]);
            Assert.Equal(0UL, w.Signals.PhaseMask[slot * 8] & w.Signals.PhaseMask[(slot * 8) + 1]);
        }
    }

    [Fact]
    public void S1k_Run1000Ticks_AllocatesNothing()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        Assert.Equal(25, w.Signals.InitFromSeeds(w));
        for (int i = 0; i < 300; i++)
        {
            SignalPhase.Run(w);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            SignalPhase.Run(w);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private readonly struct SignalPlanSeed2
    {
        public readonly int PhaseCount, Green0, Green1, Yellow, AllRed, Offset;

        private SignalPlanSeed2(int pc, int g0, int g1, int y, int a, int o)
        {
            PhaseCount = pc;
            Green0 = g0;
            Green1 = g1;
            Yellow = y;
            AllRed = a;
            Offset = o;
        }

        public static SignalPlanSeed2 Of(World w, int node)
        {
            foreach (var s in w.SignalPlanSeeds)
            {
                if (s.Node == node)
                {
                    return new SignalPlanSeed2(s.PhaseCount, s.Green0, s.Green1, s.YellowTicks, s.AllRedTicks, s.OffsetTicks);
                }
            }

            throw new InvalidOperationException("seed not found");
        }
    }
}
