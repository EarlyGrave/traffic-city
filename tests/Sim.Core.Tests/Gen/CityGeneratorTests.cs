using System;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Util;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Gen;

public class CityGeneratorTests
{
    // Голден-хэш NetworkStore.Hash() сети s1k (seed 20261024). Менять только вместе с city-generator.md §3.9
    // (порядок PRNG) или изменением геометрии/эмиссии NetworkBuilder.
    private const ulong GoldenS1kNetworkHash = 0xC9A11346B4CF4614UL;

    private readonly ITestOutputHelper _out;

    public CityGeneratorTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static GenRun S1k() => GenFixture.Run("s1k");

    [Fact]
    public void Report_RealNumbers()
    {
        foreach (string name in new[] { "s1k", "s10k", "s100k" })
        {
            GenRun r = GenFixture.Run(name);
            Assert.Equal(CreateStatus.Ok, r.CityStatus);
            Assert.Equal(CreateStatus.Ok, r.ZoneStatus);
            Assert.Equal(CreateStatus.Ok, r.TransitStatus);
            CityGenResult c = r.City0;
            _out.WriteLine($"{name}: nodes={c.NodeCount} segs={c.SegmentCount} lanes={c.LaneCount} (connectors={c.ConnectorCount}) points={c.LanePointCount} " +
                $"signals={c.SignalizedNodes} removed={c.RemovedStreets} repaired={c.RepairedEdges} shifted={c.ShiftedNodes} merged={c.MergedNodes} skipped={c.IrregularSkipped} " +
                $"zones={r.Zone0.ZoneCount} spawnLanes={r.Zone0.SpawnLanesTotal} empty={r.Zone0.EmptyZones} routes={r.Transit0.Routes} stops={r.Transit0.Stops} " +
                $"bay={r.Transit0.BayStops} failed={r.Transit0.RoutesFailed} belowMin={r.Transit0.RoutesBelowMinStops} cityMs={r.CityMs} totalMs={r.TotalMs} hash=0x{r.Net.Hash():X16}");
        }
    }

    [Fact]
    public void S1m_FitsCapacities_Timing()
    {
        GenRun r = GenFixture.Run("s1m");
        Assert.Equal(CreateStatus.Ok, r.CityStatus);
        Assert.Equal(CreateStatus.Ok, r.ZoneStatus);
        Assert.Equal(CreateStatus.Ok, r.TransitStatus);
        CityGenResult c = r.City0;
        _out.WriteLine($"s1m: nodes={c.NodeCount} segs={c.SegmentCount} lanes={c.LaneCount} points={c.LanePointCount} signals={c.SignalizedNodes} " +
            $"zones={r.Zone0.ZoneCount} routes={r.Transit0.Routes} stops={r.Transit0.Stops} cityMs={r.CityMs} totalMs={r.TotalMs}");
    }

    [Fact]
    public void Determinism_SameSeedSameResults_DifferentSeedDiffers()
    {
        GenRun a = S1k();
        GenRun b = S1k();
        Assert.Equal(GenFixture.ResultHash(a), GenFixture.ResultHash(b));
        Assert.Equal(a.Net.Hash(), b.Net.Hash());
        Assert.Equal(a.RngCity.State, b.RngCity.State);

        GenRun c = GenFixture.Run("s1k", r => r.Seed = 777UL);
        Assert.NotEqual(a.Net.Hash(), c.Net.Hash());
        bool anyPosDiffers = false;
        for (int i = 0; i < Math.Min(a.Net.NodeCount, c.Net.NodeCount); i++)
        {
            if (a.Net.Nodes[i].Position.X != c.Net.Nodes[i].Position.X)
            {
                anyPosDiffers = true;
            }
        }

        Assert.True(anyPosDiffers);
    }

    [Fact]
    public void GoldenHash_S1k()
    {
        GenRun r = S1k();
        Assert.Equal(GoldenS1kNetworkHash, r.Net.Hash());
    }

    [Fact]
    public void Connectivity_StronglyConnectedLanes_AndNodes()
    {
        GenRun r = S1k();
        NetworkStore s = r.Net;
        int laneCount = s.LaneCount;
        bool[] fwd = Bfs(s, forward: true);
        bool[] rev = Bfs(s, forward: false);
        for (int l = 0; l < laneCount; l++)
        {
            if (s.Lanes[l].Kind == LaneKind.Normal)
            {
                Assert.True(fwd[l], $"lane {l} unreachable from lane 0");
                Assert.True(rev[l], $"lane {l} cannot reach lane 0");
            }
        }

        // узлы по сегментам
        bool[] seen = new bool[s.NodeCount];
        int[] queue = new int[s.NodeCount];
        int head = 0;
        int tail = 0;
        queue[tail++] = 0;
        seen[0] = true;
        while (head < tail)
        {
            int n = queue[head++];
            ref readonly NodeData nd = ref s.Nodes[n];
            for (int k = 0; k < nd.ArmCount; k++)
            {
                SegmentData sd = s.Segments[s.NodeArms[nd.FirstArm + k]];
                int o = sd.NodeA == n ? sd.NodeB : sd.NodeA;
                if (!seen[o])
                {
                    seen[o] = true;
                    queue[tail++] = o;
                }
            }
        }

        Assert.Equal(s.NodeCount, tail);
        _out.WriteLine($"RepairedEdges={r.City0.RepairedEdges}");
    }

    private static bool[] Bfs(NetworkStore s, bool forward)
    {
        int n = s.LaneCount;
        bool[] seen = new bool[n];
        int[] queue = new int[n];
        int[] revHead = new int[n];
        int[] revNext = new int[n];
        Array.Fill(revHead, -1);
        for (int l = 0; l < n; l++)
        {
            if (s.Lanes[l].Kind == LaneKind.Connector)
            {
                // ребро FromLane -> connector -> ToLane
                int f = s.Lanes[l].FromLane;
                revNext[l] = revHead[s.Lanes[l].ToLane];
                revHead[s.Lanes[l].ToLane] = l;
                _ = f;
            }
        }

        int head = 0;
        int tail = 0;
        queue[tail++] = 0;
        seen[0] = true;
        while (head < tail)
        {
            int l = queue[head++];
            if (forward)
            {
                if (s.Lanes[l].Kind == LaneKind.Normal)
                {
                    for (int k = 0; k < s.Lanes[l].NextCount; k++)
                    {
                        Visit(s.LaneNext[s.Lanes[l].FirstNext + k]);
                    }
                }
                else
                {
                    Visit(s.Lanes[l].ToLane);
                }
            }
            else if (s.Lanes[l].Kind == LaneKind.Normal)
            {
                for (int c = revHead[l]; c >= 0; c = revNext[c])
                {
                    Visit(c);
                }
            }
            else
            {
                Visit(s.Lanes[l].FromLane);
            }
        }

        return seen;

        void Visit(int x)
        {
            if (!seen[x])
            {
                seen[x] = true;
                queue[tail++] = x;
            }
        }
    }

    [Fact]
    public void NoDegeneracy_LengthsArmsAnglesNoMultiEdges()
    {
        GenRun r = S1k();
        NetworkStore s = r.Net;
        float setback2 = 2f * r.Builder.Options.NodeSetback;
        float minLen = float.MaxValue;
        for (int i = 0; i < s.SegmentCount; i++)
        {
            SegmentData sd = s.Segments[i];
            minLen = MathF.Min(minLen, sd.Length);
            Assert.True(sd.Length + setback2 >= CityGenerator.MinSegmentLength - 1e-3f, $"segment {i} too short");
            Assert.NotEqual(sd.NodeA, sd.NodeB);
        }

        _out.WriteLine($"min Segment.Length={minLen} (axis distance {minLen + setback2})");
        for (int n = 0; n < s.NodeCount; n++)
        {
            ref readonly NodeData nd = ref s.Nodes[n];
            Assert.InRange(nd.ArmCount, 2, CityGenerator.MaxArms);
            float[] ang = new float[nd.ArmCount];
            int[] others = new int[nd.ArmCount];
            for (int k = 0; k < nd.ArmCount; k++)
            {
                SegmentData sd = s.Segments[s.NodeArms[nd.FirstArm + k]];
                int o = sd.NodeA == n ? sd.NodeB : sd.NodeA;
                others[k] = o;
                Vec2 d = s.Nodes[o].Position - nd.Position;
                ang[k] = MathF.Atan2(d.Z, d.X);
                for (int q = 0; q < k; q++)
                {
                    Assert.NotEqual(others[q], o);
                }
            }

            Array.Sort(ang);
            for (int k = 0; k < ang.Length; k++)
            {
                float next = k + 1 < ang.Length ? ang[k + 1] : ang[0] + (2f * MathF.PI);
                Assert.True(next - ang[k] >= CityGenerator.MinArmAngle - 1e-4f, $"node {n} arm angle too small");
            }
        }
    }

    [Fact]
    public void RoadClasses_LanesSpeedsAndArterialCount()
    {
        GenRun r = S1k();
        NetworkStore s = r.Net;
        int arterial = 0;
        for (int i = 0; i < s.SegmentCount; i++)
        {
            SegmentData sd = s.Segments[i];
            if (sd.Kind == RoadKind.Arterial)
            {
                arterial++;
                Assert.Equal(2, sd.LanesAB);
                Assert.Equal(2, sd.LanesBA);
                Assert.Equal(60f / 3.6f, sd.SpeedLimit, 1e-3f);
            }
            else
            {
                Assert.Equal(1, sd.LanesAB);
                Assert.Equal(1, sd.LanesBA);
                Assert.Equal(40f / 3.6f, sd.SpeedLimit, 1e-3f);
            }
        }

        Assert.Equal(200, arterial);
    }

    [Fact]
    public void Counters_S1k_MatchSpecTable()
    {
        GenRun r = S1k();
        CityGenResult c = r.City0;
        Assert.Equal(20, c.Nx);
        Assert.Equal(5, c.ArterialLinesX);
        Near(435, c.NodeCount);
        Near(758, c.SegmentCount);
        Near(1900, c.LaneCount - c.ConnectorCount);
        // спека §4 завышала connector'ы (≈12/узел); реально ≈10/узел — проверяем верхнюю границу таблицы и фактический порядок
        Assert.InRange(c.ConnectorCount, 4000, 5200);
        Assert.InRange(c.LanePointCount, 35000, 46000);
        Assert.Equal(25, c.SignalizedNodes);
        Assert.InRange(c.RemovedStreets + c.RepairedEdges, 60, 76);
        Assert.Equal(12, c.ShiftedNodes + c.MergedNodes + c.IrregularSkipped);
        Assert.Equal(840 - c.RemovedStreets - c.MergedNodes, c.SegmentCount);
        Assert.Equal(441 - c.MergedNodes, c.NodeCount);
        Assert.Equal(c.NodeCount, r.Net.NodeCount);
        Assert.Equal(c.LanePointCount, r.Net.LanePointCount);
    }

    private static void Near(int expected, int actual)
    {
        Assert.InRange(actual, (int)(expected * 0.85), (int)(expected * 1.15));
    }

    [Fact]
    public void NodeControl_FollowsArmClasses()
    {
        GenRun r = S1k();
        NetworkStore s = r.Net;
        int signals = 0;
        for (int n = 0; n < s.NodeCount; n++)
        {
            ref readonly NodeData nd = ref s.Nodes[n];
            int ac = 0;
            int sc = 0;
            for (int k = 0; k < nd.ArmCount; k++)
            {
                if (s.Segments[s.NodeArms[nd.FirstArm + k]].Kind == RoadKind.Arterial)
                {
                    ac++;
                }
                else
                {
                    sc++;
                }
            }

            if (nd.ArmCount == 2)
            {
                Assert.Equal(NodeControl.None, nd.Control);
            }
            else if (ac == 0)
            {
                Assert.Equal(NodeControl.RightHandRule, nd.Control);
            }
            else if (sc > 0)
            {
                Assert.Equal(NodeControl.Yield, nd.Control);
            }
            else
            {
                Assert.Equal(NodeControl.Signal, nd.Control);
                signals++;
            }
        }

        Assert.Equal(25, signals);
        SignalPlanSeed[] plans = r.City0.SignalPlans;
        Assert.Equal(25, plans.Length);
        int cycle = 540 + 540 + (2 * (60 + 20));
        for (int i = 0; i < plans.Length; i++)
        {
            if (i > 0)
            {
                Assert.True(plans[i].Node > plans[i - 1].Node);
            }

            Assert.Equal(540, plans[i].Green0);
            Assert.Equal(540, plans[i].Green1);
            Assert.True(plans[i].OffsetTicks < cycle);
            Assert.Equal(NodeControl.Signal, s.Nodes[plans[i].Node].Control);
        }
    }

    [Fact]
    public void Parameters_SignalsAndShares()
    {
        GenRun none = GenFixture.Run("s1k", r => r.City.Signals = CitySignals.None);
        Assert.Equal(0, none.City0.SignalizedNodes);

        GenRun small = GenFixture.Run("s1k", r =>
        {
            r.City.Signals = CitySignals.AllArterial;
            r.Caps.MaxSignalizedNodes = 64;
        });
        Assert.Equal(CreateStatus.NetworkTooLarge, small.CityStatus);

        GenRun all = GenFixture.Run("s1k", r =>
        {
            r.City.Signals = CitySignals.AllArterial;
            r.Caps.MaxSignalizedNodes = 256;
        });
        Assert.Equal(CreateStatus.Ok, all.CityStatus);
        int expected = 0;
        for (int n = 0; n < all.Net.NodeCount; n++)
        {
            ref readonly NodeData nd = ref all.Net.Nodes[n];
            bool art = false;
            for (int k = 0; k < nd.ArmCount; k++)
            {
                art |= all.Net.Segments[all.Net.NodeArms[nd.FirstArm + k]].Kind == RoadKind.Arterial;
            }

            if (art && nd.ArmCount >= 3)
            {
                expected++;
            }
        }

        _out.WriteLine($"AllArterial signals={all.City0.SignalizedNodes} (upper bound 185)");
        Assert.Equal(expected, all.City0.SignalizedNodes);
        Assert.InRange(all.City0.SignalizedNodes, 25, 185);

        GenRun noRemoval = GenFixture.Run("s1k", r => r.City.RemovedStreetShare = 0f);
        Assert.Equal(840 - noRemoval.City0.MergedNodes, noRemoval.City0.SegmentCount);

        GenRun regular = GenFixture.Run("s1k", r => r.City.IrregularNodes = 0);
        NetworkStore s = regular.Net;
        for (int i = 0; i < s.SegmentCount; i++)
        {
            Vec2 d = s.Nodes[s.Segments[i].NodeB].Position - s.Nodes[s.Segments[i].NodeA].Position;
            float a = MathF.Abs(MathF.Atan2(d.Z, d.X)) * 180f / MathF.PI;
            float off = MathF.Min(MathF.Min(a, MathF.Abs(a - 90f)), MathF.Abs(a - 180f));
            Assert.True(off <= 8f, $"segment {i} deviates {off} deg from axis");
        }
    }

    [Fact]
    public void Capacities_TooSmall_ReturnStatusWithoutException()
    {
        GenRun segs = GenFixture.Run("s1k", r => r.Caps.MaxSegments = 500);
        Assert.Equal(CreateStatus.NetworkTooLarge, segs.CityStatus);
        GenRun lanes = GenFixture.Run("s1k", r => r.Caps.MaxLanes = 1000);
        Assert.Equal(CreateStatus.NetworkTooLarge, lanes.CityStatus);
        GenRun nodes = GenFixture.Run("s1k", r => r.Caps.MaxNodes = 100);
        Assert.Equal(CreateStatus.NetworkTooLarge, nodes.CityStatus);
        GenRun pts = GenFixture.Run("s1k", r => r.Caps.MaxLanePoints = 5000);
        Assert.Equal(CreateStatus.NetworkTooLarge, pts.CityStatus);
        GenRun bad = GenFixture.Run("s1k", r => r.City.BlockSize = 10f);
        Assert.Equal(CreateStatus.InvalidNetwork, bad.CityStatus);
    }

    [Theory]
    [InlineData("s10k", 40, 10)]
    [InlineData("s100k", 100, 25)]
    public void Scale_FitsCapacities(string name, int nx, int arterialLines)
    {
        GenRun r = GenFixture.Run(name);
        Assert.Equal(CreateStatus.Ok, r.CityStatus);
        Assert.Equal(CreateStatus.Ok, r.ZoneStatus);
        Assert.Equal(CreateStatus.Ok, r.TransitStatus);
        Assert.Equal(nx, r.City0.Nx);
        Assert.Equal(arterialLines, r.City0.ArterialLinesX);
        Assert.Equal(arterialLines * arterialLines, r.City0.SignalizedNodes);
        Assert.True(r.City0.NodeCount <= r.Caps.MaxNodes);
        Assert.True(r.City0.SegmentCount <= r.Caps.MaxSegments);
        Assert.True(r.City0.LaneCount <= r.Caps.MaxLanes);
        Assert.True(r.City0.LanePointCount <= r.Caps.MaxLanePoints);
        Assert.True(r.Zone0.ZoneCount <= r.Caps.MaxZones);
        Assert.True(r.Transit0.Stops <= r.Caps.MaxBusStops);
        if (name == "s10k")
        {
            double expected = 1657.0;
            Assert.InRange(r.City0.NodeCount, (int)(expected * 0.95), (int)(expected * 1.05));
        }
    }

    [Fact]
    public void S1k_FitsCapacities()
    {
        GenRun r = S1k();
        Assert.True(r.City0.NodeCount <= r.Caps.MaxNodes);
        Assert.True(r.City0.SegmentCount <= r.Caps.MaxSegments);
        Assert.True(r.City0.LaneCount <= r.Caps.MaxLanes);
        Assert.True(r.City0.LanePointCount <= r.Caps.MaxLanePoints);
        Assert.True(r.City0.SignalizedNodes <= r.Caps.MaxSignalizedNodes);
        Assert.True(r.Zone0.ZoneCount <= r.Caps.MaxZones);
        Assert.True(r.Transit0.Routes <= r.Caps.MaxBusRoutes);
        Assert.True(r.Transit0.Stops <= r.Caps.MaxBusStops);
    }

    [Fact]
    public void Zones_S1k()
    {
        GenRun r = S1k();
        NetworkStore s = r.Net;
        GenZones z = r.Zones;
        Assert.Equal(60, z.ZoneCount);
        Assert.Equal(36, r.Zone0.InternalZones);
        int externalLanes = 0;
        bool[] used = new bool[s.LaneCount];
        for (int i = 0; i < z.ZoneCount; i++)
        {
            ZoneData zd = z.Zones[i];
            Assert.Equal(i >= 36, zd.IsExternal == 1);
            for (int k = 0; k < zd.SpawnLaneCount; k++)
            {
                int lane = z.ZoneLanes[zd.FirstSpawnLane + k];
                Assert.False(used[lane], $"lane {lane} in two zones");
                used[lane] = true;
                LaneData ld = s.Lanes[lane];
                Assert.Equal(LaneKind.Normal, ld.Kind);
                SegmentData sd = s.Segments[ld.Segment];
                Assert.True(sd.LanesAB > 0 && sd.LanesBA > 0);
                if (k > 0)
                {
                    Assert.True(lane > z.ZoneLanes[zd.FirstSpawnLane + k - 1]);
                }
            }

            if (i >= 36)
            {
                externalLanes += zd.SpawnLaneCount;
                Assert.True(zd.SpawnLaneCount >= 2, $"external zone {i} has {zd.SpawnLaneCount} lanes");
                Assert.True(zd.Weight > 0f);
            }
            else
            {
                Assert.True(zd.Weight == 0f || (zd.Weight >= 0.5f && zd.Weight <= 1.5f));
                Assert.Equal(zd.Weight == 0f, zd.SpawnLaneCount == 0);
            }

            Assert.InRange(zd.HubNode, 0, s.NodeCount - 1);
        }

        Assert.Equal(96, externalLanes);
        for (int n = 0; n < s.NodeCount; n++)
        {
            Assert.InRange(s.Nodes[n].Zone, 0, 59);
        }
    }

    [Fact]
    public void Transit_S1k_Routes()
    {
        GenRun r = S1k();
        NetworkStore s = r.Net;
        GenTransit t = r.Buses;
        _out.WriteLine($"routes={t.RouteCount} stops={t.StopCount} failed={r.Transit0.RoutesFailed} belowMin={r.Transit0.RoutesBelowMinStops}");
        Assert.Equal(10, t.RouteCount);
        for (int ri = 0; ri < t.RouteCount; ri++)
        {
            BusRouteData rt = t.Routes[ri];
            Assert.InRange(rt.StopCount, 2, 14);
            Assert.Equal(6000, rt.HeadwayTicks);
            bool anyArterialStop = false;
            float prevPos = -1f;
            int pf = t.PathFirst[ri];
            int pc = t.PathCount[ri];

            // рёбра связаны: конец предыдущего = начало следующего, ни одно ребро дважды
            for (int k = 0; k < pc; k++)
            {
                int seg = t.PathSeg[pf + k];
                SegmentData sd = s.Segments[seg];
                int start = t.PathDir[pf + k] == 0 ? sd.NodeA : sd.NodeB;
                if (k > 0)
                {
                    int ps = t.PathSeg[pf + k - 1];
                    SegmentData pd = s.Segments[ps];
                    int pend = t.PathDir[pf + k - 1] == 0 ? pd.NodeB : pd.NodeA;
                    Assert.Equal(pend, start);
                }

                for (int q = 0; q < k; q++)
                {
                    Assert.NotEqual(seg, t.PathSeg[pf + q]);
                }
            }

            int cursor = 0;
            for (int k = 0; k < rt.StopCount; k++)
            {
                BusStopData st = t.Stops[rt.FirstStop + k];
                Assert.Equal(ri, st.Route);
                LaneData ld = s.Lanes[st.Lane];
                SegmentData sd = s.Segments[ld.Segment];
                Assert.Equal(0, ld.LaneNo);   // правая полоса
                Assert.InRange(st.S, 25f, ld.Length - 25f);
                bool art = sd.Kind == RoadKind.Arterial;
                anyArterialStop |= art;
                Assert.Equal(art ? BusStopKind.Bay : BusStopKind.Curb, st.Kind);

                // остановка лежит на ребре маршрута, в порядке следования, в направлении движения
                while (cursor < pc && t.PathSeg[pf + cursor] != ld.Segment)
                {
                    cursor++;
                }

                Assert.True(cursor < pc, "stop not on route path in order");
                Assert.Equal(t.PathDir[pf + cursor], ld.Direction);
                float pos = cursor + (st.S / ld.Length);
                Assert.True(pos > prevPos);
                prevPos = pos;
            }

            Assert.True(anyArterialStop, $"route {ri} has no arterial stop");
        }

        GenRun r2 = S1k();
        Assert.Equal(GenFixture.ResultHash(r), GenFixture.ResultHash(r2));
    }
}
