using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Intersections;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Peds;

/// <summary>Сеть пешеходов (pedestrians.md §2, §15 «Сеть», PD1): геометрия, правила переходов, маски connector'ов, связность, детерминизм.</summary>
public sealed class PedNetworkTests
{
    private static ScenarioDesc WithPeds(ScenarioDesc d, ArterialCrossings arterial = ArterialCrossings.SignalOnly, bool mid = true, int count = 100)
    {
        d.Peds = new PedDesc { Enabled = true, Count = count, MidblockAtStops = mid, ArterialCrossings = arterial };
        SimCapacities c = d.Capacities;
        c.MaxPeds = count * 2;
        c.MaxResidents = count * 2;
        c.MaxPedPathSteps = 64;
        d.Capacities = c;
        return d;
    }

    private static Simulation Make(in ScenarioDesc d) => Simulation.Create(in d);

    private static ScenarioDesc Cross4(NodeControl control = NodeControl.Signal, RoadKind kind = RoadKind.Street) =>
        GateFixture.Node(control, Arm.E(1, kind), Arm.W(1, kind), Arm.N(1, kind), Arm.S(1, kind));

    private static int SideEdgeCount(PedNetwork pn, int segment, int side)
    {
        pn.SideEdges(segment, side, out _, out int count);
        return count;
    }

    // ------------------------------------------------------------ PD1: без секции peds ничего нет

    [Fact]
    public void WithoutPedsSection_NothingAllocatedAndViewEmpty()
    {
        ScenarioDesc d = Step2.S1k(buses: true);
        using Simulation sim = Make(in d);
        World w = sim.World;
        Assert.False(w.PedCfg.Enabled);
        Assert.Null(w.Net.PedNet);
        Assert.Null(w.Connectors.CrossInX);
        Assert.Null(w.Connectors.CrossOutX);
        NetworkView v = sim.Network;
        Assert.True(v.Sidewalks.IsEmpty);
        Assert.True(v.Crossings.IsEmpty);
        Assert.True(v.PedEdgePoints.IsEmpty);
        Assert.True(v.PedCorners.IsEmpty);
    }

    [Fact]
    public void PedsEnabled_DoesNotChangeStateHash()
    {
        ScenarioDesc plain = Step2.S1k(buses: true);
        ScenarioDesc withPeds = WithPeds(plain);
        using Simulation a = Make(in plain);
        using Simulation b = Make(in withPeds);
        Assert.NotNull(b.World.Net.PedNet);
        for (int i = 0; i < 6; i++)
        {
            a.Step(50);
            b.Step(50);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }

        Assert.Equal(a.World.Net.Hash(), b.World.Net.Hash());
        Assert.Equal(a.World.Connectors.Hash(), b.World.Connectors.Hash());
    }

    // ------------------------------------------------------------ геометрия и состав

    [Fact]
    public void EverySegmentHasTwoSidewalkSides_EveryNodeHasArmCountCorners()
    {
        ScenarioDesc d = WithPeds(Step2.S1k(buses: true));
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        PedNetwork pn = net.PedNet!;
        Assert.Equal(2 * net.SegmentCount, pn.SideCount);
        for (int s = 0; s < net.SegmentCount; s++)
        {
            Assert.True(SideEdgeCount(pn, s, 0) >= 1, $"segment {s} side 0");
            Assert.True(SideEdgeCount(pn, s, 1) >= 1, $"segment {s} side 1");
        }

        int slots = 0;
        for (int n = 0; n < net.NodeCount; n++)
        {
            ref readonly NodeData nd = ref net.Nodes[n];
            if (nd.FirstArm < 0)
            {
                continue;
            }

            slots += nd.ArmCount;
            for (int k = 0; k < nd.ArmCount; k++)
            {
                Assert.True(pn.IsUsed(nd.FirstArm + k), $"corner {k} of node {n}");
            }

            if (nd.ArmCount == 1)
            {
                Assert.True(pn.IsUsed(pn.DeadNode(nd.FirstArm)), $"dead-end twin corner of node {n}");
            }
        }

        Assert.Equal(slots, net.NodeArmCount);
        Assert.Equal(net.NodeArmCount, sim.Network.PedCorners.Length);
    }

    [Fact]
    public void FourWaySignalNode_CornersSidewalksAndCrossingsHaveExpectedGeometry()
    {
        ScenarioDesc d = WithPeds(Cross4());
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        PedNetwork pn = net.PedNet!;
        ref readonly NodeData centre = ref net.Nodes[0];
        Assert.Equal(4, centre.ArmCount);

        // h = 2 · 3.5 / 2 = 3.5 м, смещение h + sw/2 = 5 м: углы в (±5, ±5).
        for (int k = 0; k < 4; k++)
        {
            Vec2 p = pn.NodePos[centre.FirstArm + k];
            Assert.Equal(5f, MathF.Abs(p.X), 3);
            Assert.Equal(5f, MathF.Abs(p.Z), 3);
        }

        // Сторона 0 = справа по ходу A→B. Рукав 0 = на восток (A = центр): справа это +z.
        for (int s = 0; s < 4; s++)
        {
            Assert.Equal(1, SideEdgeCount(pn, s, 0));
            Assert.Equal(1, SideEdgeCount(pn, s, 1));
        }

        pn.SideEdges(0, 0, out int e0, out _);
        pn.SideEdges(0, 1, out int e1, out _);
        PedEdgeData right = pn.Edges[e0];
        PedEdgeData left = pn.Edges[e1];
        Assert.Equal(PedEdgeKind.Sidewalk, right.Kind);
        Assert.Equal(5f, pn.Points[right.FirstPoint].Z, 3);
        Assert.Equal(-5f, pn.Points[left.FirstPoint].Z, 3);
        Assert.Equal(5f, pn.Points[right.FirstPoint + 1].Z, 3);   // сдвинутая ось: тот же z вдоль прямого сегмента
        Assert.Equal(centre.Position.X + 5f, pn.Points[right.FirstPoint].X, 3);

        // Четыре перехода по числу рукавов, длина 2h + sw = 10 м, светофор помечен.
        Assert.Equal(4, pn.CrossingNodeCount);
        for (int k = 0; k < 4; k++)
        {
            int e = pn.XEdge[centre.FirstArm + k];
            Assert.True(e >= 0);
            PedEdgeData x = pn.Edges[e];
            Assert.Equal(PedEdgeKind.Crossing, x.Kind);
            Assert.Equal(10000, x.LenMm);
            Assert.Equal(1, x.Flags & 1);
            Assert.Equal(centre.FirstArm + k, x.Slot);
            Assert.Equal(0, x.Node);
            Assert.Equal(centre.FirstArm + ((k + 3) % 4), x.A);   // от угла k-1 (слева от рукава) к углу k (справа)
            Assert.Equal(centre.FirstArm + k, x.B);
        }

        // Весь граф - одна компонента; углы смежны ровно с двумя тротуарами и двумя переходами.
        Assert.Equal(1, pn.CompCount);
        for (int k = 0; k < 4; k++)
        {
            pn.Adjacent(centre.FirstArm + k, out _, out int deg);
            Assert.Equal(4, deg);
        }
    }

    [Fact]
    public void Crossings_FollowArterialPolicy()
    {
        // Т-узел: восток и запад - артерия, юг - улица; узел без светофора.
        ScenarioDesc Tee(NodeControl control, ArterialCrossings policy) => WithPeds(
            GateFixture.Node(control, Arm.E(1, RoadKind.Arterial), Arm.W(1, RoadKind.Arterial), Arm.S(1)), policy);

        static int CrossingsOnKind(PedNetwork pn, NetworkStore net, RoadKind kind)
        {
            int count = 0;
            ref readonly NodeData nd = ref net.Nodes[0];
            for (int k = 0; k < nd.ArmCount; k++)
            {
                if (pn.XEdge[nd.FirstArm + k] >= 0 && net.Segments[net.NodeArms[nd.FirstArm + k]].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        ScenarioDesc d1 = Tee(NodeControl.Yield, ArterialCrossings.SignalOnly);
        using (Simulation sim = Make(in d1))
        {
            Assert.Equal(1, CrossingsOnKind(sim.World.Net.PedNet!, sim.World.Net, RoadKind.Street));
            Assert.Equal(0, CrossingsOnKind(sim.World.Net.PedNet!, sim.World.Net, RoadKind.Arterial));
        }

        ScenarioDesc d2 = Tee(NodeControl.Yield, ArterialCrossings.All);
        using (Simulation sim = Make(in d2))
        {
            Assert.Equal(2, CrossingsOnKind(sim.World.Net.PedNet!, sim.World.Net, RoadKind.Arterial));
        }

        ScenarioDesc d3 = Tee(NodeControl.Signal, ArterialCrossings.SignalOnly);
        using (Simulation sim = Make(in d3))
        {
            Assert.Equal(2, CrossingsOnKind(sim.World.Net.PedNet!, sim.World.Net, RoadKind.Arterial));
            Assert.Equal(1, CrossingsOnKind(sim.World.Net.PedNet!, sim.World.Net, RoadKind.Street));
        }
    }

    [Fact]
    public void TwoArmNode_HasNoCrossings_DeadEndHasCapAndTwinCorner()
    {
        // Узел с двумя рукавами - перехода нет (ArmCount < 3): центр одной прямой дороги.
        ScenarioDesc d2 = WithPeds(GateFixture.Node(NodeControl.None, Arm.E(1), Arm.W(1)));
        using (Simulation sim = Make(in d2))
        {
            PedNetwork pn = sim.World.Net.PedNet!;
            Assert.Equal(0, pn.CrossingNodeCount);
            Assert.Equal(2, sim.World.Net.Nodes[0].ArmCount);
        }

        // Один сегмент, оба узла - тупики: 2 тротуара + 2 обхода, одна компонента, обход длиной 2h + sw.
        ScenarioDesc d1 = WithPeds(GateFixture.Node(NodeControl.None, Arm.E(1)));
        using Simulation sim1 = Make(in d1);
        NetworkStore net = sim1.World.Net;
        PedNetwork p1 = net.PedNet!;
        Assert.Equal(2, p1.SidewalkEdgeCount);
        Assert.Equal(4, p1.WalkwayCount);
        Assert.Equal(0, p1.CrossingNodeCount);
        Assert.Equal(1, p1.CompCount);
        PedEdgeData cap = p1.Edges[p1.SidewalkEdgeCount];
        Assert.Equal(PedEdgeKind.EndCap, cap.Kind);
        Assert.Equal(10000, cap.LenMm);
        Assert.Equal(3, cap.PointCount);
        Assert.Equal(300000, p1.Edges[0].LenMm);   // от угла до угла вдоль всего сегмента 300 м
    }

    // ------------------------------------------------------------ связь переходов с machine connector'ами

    [Fact]
    public void CrossingMasks_MatchConnectorFromArmAndOutArm()
    {
        ScenarioDesc d = WithPeds(Cross4());
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedNetwork pn = w.Net.PedNet!;
        ref readonly NodeData nd = ref w.Net.Nodes[0];
        Assert.NotNull(w.Connectors.CrossInX);
        Assert.NotNull(w.Connectors.CrossOutX);

        for (int a = 0; a < nd.ArmCount; a++)
        {
            ulong inMask = 0;
            ulong outMask = 0;
            for (int i = 0; i < nd.ConnectorCount; i++)
            {
                int g = w.Net.Lanes[nd.FirstConnector + i].ConflictGroup;
                if (w.Connectors.FromArm[g] == a)
                {
                    inMask |= 1UL << w.Connectors.LocalBit[g];
                }

                int outSeg = w.Net.Lanes[w.Connectors.OutLane[g]].Segment;
                if (outSeg == w.Net.NodeArms[nd.FirstArm + a])
                {
                    outMask |= 1UL << w.Connectors.LocalBit[g];
                }
            }

            Assert.NotEqual(0UL, inMask);
            Assert.NotEqual(0UL, outMask);
            Assert.Equal(inMask, pn.XIn[nd.FirstArm + a]);
            Assert.Equal(outMask, pn.XOut[nd.FirstArm + a]);
        }

        for (int i = 0; i < nd.ConnectorCount; i++)
        {
            int g = w.Net.Lanes[nd.FirstConnector + i].ConflictGroup;
            Assert.Equal(nd.FirstArm + w.Connectors.FromArm[g], w.Connectors.CrossInX![g]);
            int outSlot = w.Connectors.CrossOutX![g];
            Assert.True(outSlot >= nd.FirstArm && outSlot < nd.FirstArm + nd.ArmCount);
            Assert.Equal(w.Net.Lanes[w.Connectors.OutLane[g]].Segment, w.Net.NodeArms[outSlot]);
        }
    }

    [Fact]
    public void NoCrossing_MeansMinusOneForConnectors()
    {
        // Артерии без светофора при signal_only: перехода нет, у connector'ов -1 и маски пусты.
        ScenarioDesc d = WithPeds(GateFixture.Node(NodeControl.Yield, Arm.E(1, RoadKind.Arterial), Arm.W(1, RoadKind.Arterial), Arm.S(1)));
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedNetwork pn = w.Net.PedNet!;
        ref readonly NodeData nd = ref w.Net.Nodes[0];
        for (int i = 0; i < nd.ConnectorCount; i++)
        {
            int g = w.Net.Lanes[nd.FirstConnector + i].ConflictGroup;
            int fromSlot = nd.FirstArm + w.Connectors.FromArm[g];
            bool crossed = pn.XEdge[fromSlot] >= 0;
            Assert.Equal(crossed ? fromSlot : -1, w.Connectors.CrossInX![g]);
            Assert.Equal(crossed, pn.XIn[fromSlot] != 0UL);
        }
    }

    // ------------------------------------------------------------ переходы на перегоне

    [Fact]
    public void MidblockCrossings_StandBeforeStops_OnStreetsOnly()
    {
        ScenarioDesc d = WithPeds(Step2.S1k(buses: true));
        using Simulation sim = Make(in d);
        World w = sim.World;
        NetworkStore net = w.Net;
        PedNetwork pn = net.PedNet!;
        Assert.True(net.BusStopCount > 0);
        Assert.True(pn.MidCount > 0);

        int mapped = 0;
        for (int i = 0; i < net.BusStopCount; i++)
        {
            ref readonly LaneData ld = ref net.Lanes[net.BusStops[i].Lane];
            ref readonly SegmentData sd = ref net.Segments[ld.Segment];
            int m = pn.StopMid[i];
            if (sd.Kind != RoadKind.Street || ld.Kind != LaneKind.Normal || sd.Length < 30f)
            {
                Assert.Equal(-1, m);
                continue;
            }

            Assert.True(m >= 0, $"stop {i} on street has no crossing");
            mapped++;
            Assert.Equal(ld.Segment, pn.MidSegment[m]);
            float sAxis = ld.Direction == 0 ? net.BusStops[i].S : sd.Length - net.BusStops[i].S;
            float expect = MathF.Min(MathF.Max(sAxis - 20f, 15f), sd.Length - 15f);
            Assert.True(MathF.Abs(pn.MidS[m] - expect) < 6f, $"stop {i}: s_c {pn.MidS[m]} vs {expect}");
            Assert.InRange(pn.MidS[m], 15f, sd.Length - 15f);
        }

        Assert.True(mapped > 0);

        // Каждый переход режет обе стороны сегмента: рёбер на стороне = число переходов сегмента + 1; концы - в одной компоненте.
        for (int m = 0; m < pn.MidCount; m++)
        {
            int seg = pn.MidSegment[m];
            int onSeg = 0;
            for (int k = 0; k < pn.MidCount; k++)
            {
                if (pn.MidSegment[k] == seg)
                {
                    onSeg++;
                }
            }

            Assert.Equal(onSeg + 1, SideEdgeCount(pn, seg, 0));
            Assert.Equal(onSeg + 1, SideEdgeCount(pn, seg, 1));
            PedEdgeData x = pn.Edges[pn.MidEdge[m]];
            Assert.Equal(PedEdgeKind.MidCrossing, x.Kind);
            Assert.Equal(pn.MidNode(m, 0), x.A);
            Assert.Equal(pn.MidNode(m, 1), x.B);
            Assert.True(pn.Connected(x.A, x.B));
            int lanes = net.Segments[seg].LanesAB + net.Segments[seg].LanesBA;
            Assert.Equal((int)MathF.Round(((lanes * 3.5f) + 3f) * 1000f), x.LenMm);
        }
    }

    [Fact]
    public void MidblockDisabled_NoMidCrossingsAndNoMidNodes()
    {
        ScenarioDesc d = WithPeds(Step2.S1k(buses: true), mid: false);
        using Simulation sim = Make(in d);
        PedNetwork pn = sim.World.Net.PedNet!;
        Assert.Equal(0, pn.MidCount);
        Assert.Equal(0, pn.MidCap);
        for (int i = 0; i < pn.StopMid.Length; i++)
        {
            Assert.Equal(-1, pn.StopMid[i]);
        }
    }

    [Fact]
    public void MidblockLaneTables_ChainCrossingsInTravelOrder()
    {
        ScenarioDesc d = WithPeds(Step2.S1k(buses: true));
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        PedNetwork pn = net.PedNet!;
        int checkedLanes = 0;
        for (int lane = 0; lane < net.LaneCount; lane++)
        {
            int first = pn.LaneMidX[lane];
            if (first < 0)
            {
                continue;
            }

            ref readonly LaneData ld = ref net.Lanes[lane];
            Assert.Equal(LaneKind.Normal, ld.Kind);
            checkedLanes++;
            float prev = float.NegativeInfinity;
            for (int m = first; m >= 0; m = ld.Direction == 0 ? pn.MidNextFwd[m] : pn.MidNextBwd[m])
            {
                Assert.Equal(ld.Segment, pn.MidSegment[m]);
                pn.MidLaneRange(m, ld.Direction, ld.Length, out float s0, out float s1);
                Assert.True(s0 > prev, "crossings along a lane must be in increasing lane-s order");
                Assert.Equal(pn.Cfg.CrosswalkWidthMm * 0.001f, s1 - s0, 3);
                prev = s1;
            }
        }

        Assert.True(checkedLanes > 0);
    }

    // ------------------------------------------------------------ связность, детерминизм, пересборка

    [Fact]
    public void GraphIsConnectedWithinEachRoadComponent()
    {
        foreach (ArterialCrossings policy in new[] { ArterialCrossings.SignalOnly, ArterialCrossings.All })
        {
            ScenarioDesc d = WithPeds(Step2.S1k(buses: true), policy);
            using Simulation sim = Make(in d);
            NetworkStore net = sim.World.Net;
            PedNetwork pn = net.PedNet!;

            var parent = new int[net.NodeCount];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            for (int s = 0; s < net.SegmentCount; s++)
            {
                parent[Find(net.Segments[s].NodeA)] = Find(net.Segments[s].NodeB);
            }

            // Каждый узел дорожной компоненты: все его углы лежат в одной компоненте пешеходов, и она одна на всю дорожную компоненту.
            var compOfRoad = new System.Collections.Generic.Dictionary<int, int>();
            for (int n = 0; n < net.NodeCount; n++)
            {
                ref readonly NodeData nd = ref net.Nodes[n];
                if (nd.FirstArm < 0)
                {
                    continue;
                }

                int root = Find(n);
                for (int k = 0; k < nd.ArmCount; k++)
                {
                    int comp = pn.PedComp[nd.FirstArm + k];
                    Assert.True(comp >= 0);
                    if (compOfRoad.TryGetValue(root, out int known))
                    {
                        Assert.True(known == comp, $"policy {policy}: road component {root} splits into pedestrian components {known} and {comp} (node {n} corner {k})");
                    }
                    else
                    {
                        compOfRoad[root] = comp;
                    }
                }
            }
        }
    }

    [Fact]
    public void Build_IsDeterministic_AndRebuildIsIdempotent()
    {
        ScenarioDesc d = WithPeds(Step2.S1k(buses: true));
        using Simulation a = Make(in d);
        using Simulation b = Make(in d);
        PedNetwork pa = a.World.Net.PedNet!;
        ulong h = pa.Hash();
        Assert.Equal(h, b.World.Net.PedNet!.Hash());

        Assert.True(pa.Rebuild(a.World));
        Assert.Equal(h, pa.Hash());

        // Пересборка после изменения состояния (шаги) даёт то же самое: сеть - функция сети, а не движения.
        a.Step(200);
        Assert.True(pa.Rebuild(a.World));
        Assert.Equal(h, pa.Hash());
    }

    [Fact]
    public void Rebuild_DoesNotAllocate()
    {
        ScenarioDesc d = WithPeds(Step2.S1k(buses: true));
        using Simulation sim = Make(in d);
        PedNetwork pn = sim.World.Net.PedNet!;
        World w = sim.World;
        long bytes = AllocationProbe.Measure(() => pn.Rebuild(w));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void BuildRoad_RebuildsNetworkToTheSameResultAsAFreshBuild()
    {
        ScenarioDesc d = WithPeds(GateFixture.Node(NodeControl.None, Arm.E(1)));
        SimCapacities c = d.Capacities;
        c.MaxNodes = 64;
        c.MaxSegments = 64;
        c.MaxLanes = 1024;
        c.MaxLanePoints = 16384;
        d.Capacities = c;
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedNetwork pn = w.Net.PedNet!;
        int edgesBefore = pn.EdgeCount;

        var cmd = new BuildRoadCmd { Ax = 0f, Az = 600f, Bx = 300f, Bz = 600f, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        sim.Enqueue(Command.MakeBuildRoad(in cmd));
        sim.Step(1);
        Assert.Equal(2, w.Net.SegmentCount);
        Assert.Same(pn, w.Net.PedNet);
        Assert.Equal(4, pn.SideCount);
        Assert.True(pn.EdgeCount > edgesBefore);

        var fresh = new PedNetwork(in w.Caps, w.Net, w.Connectors, in w.PedCfg);
        Assert.True(fresh.Rebuild(w));
        Assert.Equal(fresh.Hash(), pn.Hash());
    }

    // ------------------------------------------------------------ проверка секции

    [Fact]
    public void Create_RejectsTooSmallPedCapacitiesAndAgentsWithoutPeds()
    {
        ScenarioDesc ok = WithPeds(Cross4(), count: 100);
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in ok, out Simulation? s0));
        s0?.Dispose();

        ScenarioDesc smallPeds = ok;
        smallPeds.Capacities.MaxPeds = 129;   // < 100 · 1.3
        Assert.Equal(CreateStatus.InvalidPeds, Simulation.TryCreate(in smallPeds, out _));

        ScenarioDesc exactPeds = ok;
        exactPeds.Capacities.MaxPeds = 130;
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in exactPeds, out Simulation? s1));
        s1?.Dispose();

        ScenarioDesc smallResidents = ok;
        smallResidents.Capacities.MaxResidents = 199;   // < 2 · count (дефолт)
        Assert.Equal(CreateStatus.InvalidPeds, Simulation.TryCreate(in smallResidents, out _));

        ScenarioDesc badWidth = ok;
        badWidth.Peds.SidewalkWidthMm = 100;
        Assert.Equal(CreateStatus.InvalidPeds, Simulation.TryCreate(in badWidth, out _));

        ScenarioDesc agentsNoPeds = Cross4();
        agentsNoPeds.Transit.PassengerModel = PassengerModel.Agents;
        Assert.Equal(CreateStatus.InvalidPeds, Simulation.TryCreate(in agentsNoPeds, out _));
    }
}
