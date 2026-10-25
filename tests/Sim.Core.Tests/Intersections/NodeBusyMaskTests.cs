using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Tests.Intersections;

/// <summary>ADR-0016, D3: маска занятых connector'ов <c>NodeBusy</c> (строит фаза 4) - надмножество участников, результат <c>ConflictGap</c> с маской точный.</summary>
public class NodeBusyMaskTests
{
    [Theory]
    [InlineData(NodeControl.RightHandRule)]
    [InlineData(NodeControl.Yield)]
    public void NodeBusy_IsSupersetOfParticipants_AndConflictGapIsExact(NodeControl control)
    {
        ScenarioDesc d = GateFixture.Node(control, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        ConnectorStore cs = w.Connectors;
        for (int a = 0; a < 4; a++)
        {
            int inLane = GateFixture.InLane(w, a);
            GateFixture.Place(sim, inLane, GateFixture.ArmLength - 40f, 6f, GateFixture.ConnectorTo(w, inLane, (a + 2) % 4));
            GateFixture.Place(sim, inLane, GateFixture.ArmLength - 70f, 6f, GateFixture.ConnectorTo(w, inLane, (a + 1) % 4));
        }

        int rejects = 0;
        int nonTrivial = 0;
        for (int tick = 0; tick < 900; tick++)
        {
            SignalPhase.Run(w);
            GatePhase.Run(w);
            ulong[] busy = w.Gate.NodeBusy;

            // Надмножество: каждая машина на connector'е и каждая допущенная (с допуском от своей полосы) отмечена.
            int hw = v.HighWater;
            for (int i = 0; i < hw; i++)
            {
                if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
                {
                    continue;
                }

                int c = -1;
                if (net.Lanes[v.Lane[i]].Kind == LaneKind.Connector)
                {
                    c = v.Lane[i];
                }
                else if (GateRules.IsGranted(v, i))
                {
                    c = v.GrantConnector[i];
                }

                if (c >= 0)
                {
                    int g = net.Lanes[c].ConflictGroup;
                    Assert.True((busy[cs.Node[g]] & (1UL << cs.LocalBit[g])) != 0, $"tick {tick}: машина {i} на/для connector'а {c} не отмечена в NodeBusy");
                }
            }

            // Точность: ObstacleGap с маской == без маски (null) для каждой машины.
            var work = default(SimWork);
            for (int i = 0; i < hw; i++)
            {
                if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
                {
                    continue;
                }

                float with = GateRules.ObstacleGap(net, v, w.Occupancy, cs, busy, ref work, v.S, i, 2f, out bool b1, out float s1);
                float without = GateRules.ObstacleGap(net, v, w.Occupancy, cs, null, ref work, v.S, i, 2f, out bool b2, out float s2);
                Assert.Equal(without, with);
                Assert.Equal(b2, b1);
                Assert.Equal(s2, s1);
                if (with < Idm.FreeGap)
                {
                    nonTrivial++;
                }
            }

            rejects += (int)work.ConflictGapMaskRejects;
            BehaviourPhase.Run(w);
            IntegratePhase.Run(w);
            TransitionPhase.Run(w);
            ArrivalPhase.Run(w);
            w.Tick++;
        }

        Assert.True(nonTrivial > 0, "сценарий не создал ни одного препятствия ворот");
        Assert.True(rejects > 0, "маска ни разу не отсекла вызов");
    }
}
