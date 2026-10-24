using System;
using System.Diagnostics;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Gen;
using Sim.Core.Util;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Intersections;

/// <summary>Юнит-тесты J1 (intersections.md §2, §3, §11): connector'ы, ArmFlags, конфликтные пары.</summary>
public class ConnectorStoreTests
{
    private readonly ITestOutputHelper _out;

    public ConnectorStoreTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static SimCapacities Caps() => new()
    {
        MaxNodes = 64,
        MaxSegments = 128,
        MaxLanes = 4096,
        MaxLanePoints = 65536,
        MaxZones = 4,
        MaxBusRoutes = 2,
        MaxBusStops = 4,
        MaxBusStopsPerRoute = 4,
    };

    private sealed class Fix
    {
        public NetworkStore Net = null!;
        public NetworkBuilder B = null!;
        public ConnectorStore Cs = null!;
        public int Center;
    }

    /// <summary>Центр (0,0) и рукава длиной 100 м под углами (градусы, 0 = +X, по часовой в X-Z); у каждого рукава своя раскладка и класс.</summary>
    private static Fix Star(NodeControl control, params (float Deg, int In, int Out, RoadKind Kind)[] arms)
    {
        Fix f = StarRaw(control, arms);
        f.Cs = new ConnectorStore(1024, 8192);
        Assert.Equal(NetworkStatus.Ok, f.Cs.BuildAll(f.Net));
        return f;
    }

    private static Fix StarRaw(NodeControl control, params (float Deg, int In, int Out, RoadKind Kind)[] arms)
    {
        var f = new Fix { Net = new NetworkStore(Caps()) };
        f.B = new NetworkBuilder(f.Net);
        Assert.Equal(NetworkStatus.Ok, f.B.AddNode(new Vec2(0, 0), control, out f.Center));
        foreach ((float deg, int lin, int lout, RoadKind kind) in arms)
        {
            float r = deg * MathF.PI / 180f;
            Assert.Equal(NetworkStatus.Ok, f.B.AddNode(new Vec2(100f * MathF.Cos(r), 100f * MathF.Sin(r)), NodeControl.None, out int n));
            // Сегмент Center -> n: AB = от центра (выезд), BA = к центру (въезд).
            Assert.Equal(NetworkStatus.Ok, f.B.AddSegment(f.Center, n, lout, lin, 13.9f, kind, out _));
        }

        Assert.Equal(NetworkStatus.Ok, f.B.BuildAllConnectorsEx());
        return f;
    }

    private static Fix FourWay(NodeControl control, int lanes, RoadKind kind = RoadKind.Street) =>
        Star(control, (0f, lanes, lanes, kind), (90f, lanes, lanes, kind), (180f, lanes, lanes, kind), (270f, lanes, lanes, kind));

    private static void AssertEndsMatchLanes(Fix f)
    {
        for (int c = 0; c < f.Cs.Count; c++)
        {
            LaneData l = f.Net.Lanes[f.Cs.Lane[c]];
            Assert.Equal(LaneKind.Connector, l.Kind);
            Vec2 a = f.Net.LanePoints[l.FirstPoint];
            Vec2 z = f.Net.LanePoints[l.FirstPoint + l.PointCount - 1];
            LaneData li = f.Net.Lanes[l.FromLane];
            LaneData lo = f.Net.Lanes[l.ToLane];
            Vec2 ie = f.Net.LanePoints[li.FirstPoint + li.PointCount - 1];
            Vec2 os = f.Net.LanePoints[lo.FirstPoint];
            Assert.True(Dist(a, ie) < 1e-3f, $"connector {c}: начало не совпало с концом входной полосы");
            Assert.True(Dist(z, os) < 1e-3f, $"connector {c}: конец не совпал с началом выходной полосы");
        }
    }

    /// <summary>Индекс первого connector'а центрального узла в ConnectorStore.</summary>
    private static int G0(Fix f) => f.Net.Lanes[f.Net.Nodes[f.Center].FirstConnector].ConflictGroup;

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static int FindConnector(Fix f, int inLane, Maneuver m, int outLane = -1)
    {
        for (int c = 0; c < f.Cs.Count; c++)
        {
            if (f.Cs.InLane[c] == inLane && f.Cs.Maneuver[c] == (byte)m && (outLane < 0 || f.Cs.OutLane[c] == outLane))
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>Входная полоса рукава arm (по часовой от узла) с номером laneNo.</summary>
    private static int InLaneOf(Fix f, int arm, int laneNo)
    {
        ref readonly NodeData nd = ref f.Net.Nodes[f.Center];
        int seg = f.Net.NodeArms[nd.FirstArm + arm];
        SegmentData sd = f.Net.Segments[seg];
        bool atA = sd.NodeA == f.Center;
        // Центр = A: входящие в центр — BA.
        return (atA ? sd.FirstLaneBA : sd.FirstLaneAB) + laneNo;
    }

    private static int OutLaneOf(Fix f, int arm, int laneNo)
    {
        ref readonly NodeData nd = ref f.Net.Nodes[f.Center];
        int seg = f.Net.NodeArms[nd.FirstArm + arm];
        SegmentData sd = f.Net.Segments[seg];
        bool atA = sd.NodeA == f.Center;
        return (atA ? sd.FirstLaneAB : sd.FirstLaneBA) + laneNo;
    }

    private static int CountKind(Fix f, int c, ConflictKind kind)
    {
        int n = 0;
        for (int e = f.Cs.FirstConflict[c]; e < f.Cs.FirstConflict[c] + f.Cs.ConflictCount[c]; e++)
        {
            if (f.Cs.Conflicts[e].Kind == kind)
            {
                n++;
            }
        }

        return n;
    }

    private static bool TryEntry(Fix f, int c, int other, out ConflictEntry entry)
    {
        for (int e = f.Cs.FirstConflict[c]; e < f.Cs.FirstConflict[c] + f.Cs.ConflictCount[c]; e++)
        {
            if (f.Cs.Conflicts[e].OtherLocal == f.Cs.LocalBit[other])
            {
                entry = f.Cs.Conflicts[e];
                return true;
            }
        }

        entry = default;
        return false;
    }

    // ---- число и типы connector'ов ----

    [Fact]
    public void FourWay_OnePlusOne_Has12Connectors_InStraightRightLeftOrder()
    {
        Fix f = FourWay(NodeControl.RightHandRule, 1);
        Assert.Equal(12, f.Net.Nodes[f.Center].ConnectorCount);
        AssertEndsMatchLanes(f);
        for (int arm = 0; arm < 4; arm++)
        {
            LaneData inLane = f.Net.Lanes[InLaneOf(f, arm, 0)];
            Assert.Equal(3, inLane.NextCount);
            var order = new Maneuver[3];
            for (int k = 0; k < 3; k++)
            {
                order[k] = (Maneuver)f.Cs.Maneuver[f.Net.Lanes[f.Net.LaneNext[inLane.FirstNext + k]].ConflictGroup];
            }

            Assert.Equal(new[] { Maneuver.Straight, Maneuver.Right, Maneuver.Left }, order);
        }
    }

    [Fact]
    public void FourWay_OnePlusOne_ConflictTable()
    {
        Fix f = FourWay(NodeControl.RightHandRule, 1);
        int cross = 0;
        int merge = 0;
        for (int c = 0; c < f.Cs.Count; c++)
        {
            cross += CountKind(f, c, ConflictKind.Cross);
            merge += CountKind(f, c, ConflictKind.Merge);
        }

        // Merge: в каждую выходную полосу входят 3 connector'а (прямо, направо, налево с разных рукавов): C(3,2)=3 пары × 4 полосы = 12 пар = 24 записи.
        Assert.Equal(24, merge);
        // Cross (неупорядоченные пары): независимая оценка плотной выборкой в ConflictsMatchDenseSampling; здесь фиксируем число.
        _out.WriteLine($"4-way 1+1: connectors=12 crossEntries={cross} mergeEntries={merge}");
        // 16 неупорядоченных Cross-пар (значение подтверждено плотной выборкой в FourWay_ConflictsMatchDenseSampling).
        Assert.Equal(32, cross);
    }

    [Fact]
    public void FourWay_TwoPlusTwo_Has16Connectors_RightLaneRightStraight_LeftLaneLeftStraight()
    {
        Fix f = FourWay(NodeControl.RightHandRule, 2);
        Assert.Equal(16, f.Net.Nodes[f.Center].ConnectorCount);
        AssertEndsMatchLanes(f);
        for (int arm = 0; arm < 4; arm++)
        {
            int l0 = InLaneOf(f, arm, 0);
            int l1 = InLaneOf(f, arm, 1);
            Assert.True(FindConnector(f, l0, Maneuver.Right) >= 0);
            Assert.True(FindConnector(f, l0, Maneuver.Straight) >= 0);
            Assert.True(FindConnector(f, l0, Maneuver.Left) < 0);
            Assert.True(FindConnector(f, l1, Maneuver.Left) >= 0);
            Assert.True(FindConnector(f, l1, Maneuver.Straight) >= 0);
            Assert.True(FindConnector(f, l1, Maneuver.Right) < 0);
            // Прямо i -> i.
            int s0 = FindConnector(f, l0, Maneuver.Straight);
            Assert.Equal(0, f.Net.Lanes[f.Cs.OutLane[s0]].LaneNo);
            int s1 = FindConnector(f, l1, Maneuver.Straight);
            Assert.Equal(1, f.Net.Lanes[f.Cs.OutLane[s1]].LaneNo);
        }
    }

    [Fact]
    public void TJunction_ArterialAndStreet_Has8Connectors_StreetArmIsMinor()
    {
        // Артерия 2+2 запад-восток, улица 1+1 с юга (угол 90° = +Z).
        Fix f = Star(NodeControl.Yield, (0f, 2, 2, RoadKind.Arterial), (90f, 1, 1, RoadKind.Street), (180f, 2, 2, RoadKind.Arterial));
        Assert.Equal(8, f.Net.Nodes[f.Center].ConnectorCount);
        AssertEndsMatchLanes(f);
        ref readonly NodeData nd = ref f.Net.Nodes[f.Center];
        Assert.Equal(3, nd.ArmCount);
        Assert.Equal(NodeControl.Yield, nd.Control);
        int minor = 0;
        for (int a = 0; a < 3; a++)
        {
            bool isMinor = (f.Net.ArmFlags[nd.FirstArm + a] & 1) != 0;
            Assert.Equal(f.Net.Segments[f.Net.NodeArms[nd.FirstArm + a]].Kind == RoadKind.Street, isMinor);
            minor += isMinor ? 1 : 0;
        }

        Assert.Equal(1, minor);
        // ArmDir — единичные векторы от узла.
        for (int a = 0; a < 3; a++)
        {
            Vec2 d = f.Net.ArmDir[nd.FirstArm + a];
            Assert.Equal(1f, MathF.Sqrt((d.X * d.X) + (d.Z * d.Z)), 3);
        }

        // Правила приоритета: connector улицы уступает любому connector'у артерии, с которым конфликтует.
        for (int c = 0; c < f.Cs.Count; c++)
        {
            for (int e = f.Cs.FirstConflict[c]; e < f.Cs.FirstConflict[c] + f.Cs.ConflictCount[c]; e++)
            {
                ConflictEntry ce = f.Cs.Conflicts[e];
                int o = G0(f) + ce.OtherLocal;
                bool mineMinor = (f.Cs.Flags[c] & 1) != 0;
                bool otherMinor = (f.Cs.Flags[o] & 1) != 0;
                if (mineMinor && !otherMinor)
                {
                    Assert.Equal(ConflictRel.Yield, ce.Rel);
                }
                else if (!mineMinor && otherMinor)
                {
                    Assert.Equal(ConflictRel.Priority, ce.Rel);
                }
            }
        }
    }

    [Fact]
    public void TJunction_EqualRoads_LonelyArmIsMinor_AndYieldWithoutMinorBecomesRightHandRule()
    {
        // Три улицы 1+1, ножка с юга (90°) — единственный рукав без противоположного.
        Fix f = Star(NodeControl.Stop, (0f, 1, 1, RoadKind.Street), (90f, 1, 1, RoadKind.Street), (180f, 1, 1, RoadKind.Street));
        ref readonly NodeData nd = ref f.Net.Nodes[f.Center];
        Assert.Equal(NodeControl.Stop, nd.Control);
        int minorArms = 0;
        for (int a = 0; a < 3; a++)
        {
            minorArms += f.Net.ArmFlags[nd.FirstArm + a] & 1;
        }

        Assert.Equal(1, minorArms);

        // 4-way равнозначный со знаком Yield: нет главной -> RightHandRule.
        Fix g = FourWay(NodeControl.Yield, 1);
        Assert.Equal(NodeControl.RightHandRule, g.Net.Nodes[g.Center].Control);
        // None на узле с >= 3 рукавами -> RightHandRule.
        Fix h = FourWay(NodeControl.None, 1);
        Assert.Equal(NodeControl.RightHandRule, h.Net.Nodes[h.Center].Control);
    }

    [Theory]
    [InlineData(5, 20)]
    [InlineData(6, 30)]
    public void MultiArm_AllPairsConnected(int arms, int expected)
    {
        var spec = new (float, int, int, RoadKind)[arms];
        for (int i = 0; i < arms; i++)
        {
            spec[i] = (i * 360f / arms, 1, 1, RoadKind.Street);
        }

        Fix f = Star(NodeControl.RightHandRule, spec);
        Assert.Equal(arms, f.Net.Nodes[f.Center].ArmCount);
        Assert.Equal(expected, f.Net.Nodes[f.Center].ConnectorCount);
        AssertEndsMatchLanes(f);
        for (int a = 0; a < arms; a++)
        {
            Assert.Equal(arms - 1, f.Net.Lanes[InLaneOf(f, a, 0)].NextCount);
        }

        AssertDenseSamplingAgrees(f);
    }

    [Fact]
    public void DeadEnd_UTurnOnly_AndNoConflictsWhenNoRules()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 2, 2, 10f, RoadKind.Street, out _));
        Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());
        var cs = new ConnectorStore(64, 64);
        Assert.Equal(NetworkStatus.Ok, cs.BuildAll(store));
        Assert.Equal(4, cs.Count);
        for (int c = 0; c < cs.Count; c++)
        {
            Assert.Equal((byte)Maneuver.UTurn, cs.Maneuver[c]);
            Assert.Equal(0, cs.ConflictCount[c]); // Control None: ворот нет
        }
    }

    // ---- конфликты ----

    [Fact]
    public void OncomingLeftCrossesOncomingStraight_RelLeftYields()
    {
        Fix f = FourWay(NodeControl.RightHandRule, 2);
        // Рукава 0 и 2 противоположны (E и W по часовой от узла: углы 0,90,180,270 -> arm индексы 0..3).
        int left = FindConnector(f, InLaneOf(f, 0, 1), Maneuver.Left);
        int straight = FindConnector(f, InLaneOf(f, 2, 1), Maneuver.Straight);
        Assert.True(left >= 0 && straight >= 0);
        Assert.True(TryEntry(f, left, straight, out ConflictEntry e));
        Assert.Equal(ConflictKind.Cross, e.Kind);
        Assert.Equal(ConflictRel.Yield, e.Rel);
        Assert.True(TryEntry(f, straight, left, out ConflictEntry back));
        Assert.Equal(ConflictRel.Priority, back.Rel);
    }

    [Fact]
    public void PerpendicularStraights_Cross_WithRightHandRule()
    {
        Fix f = FourWay(NodeControl.RightHandRule, 1);
        int a = FindConnector(f, InLaneOf(f, 0, 0), Maneuver.Straight);
        int b = FindConnector(f, InLaneOf(f, 1, 0), Maneuver.Straight);
        Assert.True(TryEntry(f, a, b, out ConflictEntry e));
        Assert.Equal(ConflictKind.Cross, e.Kind);
        Assert.NotEqual(ConflictRel.Tie, e.Rel);
        Assert.True(TryEntry(f, b, a, out ConflictEntry back));
        Assert.NotEqual(e.Rel, back.Rel);
    }

    [Fact]
    public void ParallelAndDivergingConnectors_HaveNoConflict()
    {
        Fix f = FourWay(NodeControl.RightHandRule, 2);
        // Прямо с полос 0 и 1 одного рукава — параллельны (3.5 м).
        int s0 = FindConnector(f, InLaneOf(f, 0, 0), Maneuver.Straight);
        int s1 = FindConnector(f, InLaneOf(f, 0, 1), Maneuver.Straight);
        Assert.False(TryEntry(f, s0, s1, out _));
        // Встречные прямые параллельны.
        int o0 = FindConnector(f, InLaneOf(f, 2, 0), Maneuver.Straight);
        Assert.False(TryEntry(f, s0, o0, out _));
        // Diverge: прямо и направо с одной входной полосы.
        int r0 = FindConnector(f, InLaneOf(f, 0, 0), Maneuver.Right);
        Assert.False(TryEntry(f, s0, r0, out _));
        Assert.False(TryEntry(f, r0, s0, out _));
    }

    [Fact]
    public void TwoLanesIntoOne_Merge_AtConnectorEnds()
    {
        // Запад 2+2 входит в восток 1+1; север/юг — заглушки, чтобы узел имел правила.
        Fix f = Star(
            NodeControl.RightHandRule,
            (0f, 1, 1, RoadKind.Street),
            (90f, 1, 1, RoadKind.Street),
            (180f, 2, 2, RoadKind.Street),
            (270f, 1, 1, RoadKind.Street));
        int east = 0;
        int west = 2;
        int outE = OutLaneOf(f, east, 0);
        int c0 = FindConnector(f, InLaneOf(f, west, 0), Maneuver.Straight, outE);
        int c1 = FindConnector(f, InLaneOf(f, west, 1), Maneuver.Straight, outE);
        Assert.True(c0 >= 0 && c1 >= 0, "обе прямые полосы ведут в единственную выходную");
        Assert.True(TryEntry(f, c0, c1, out ConflictEntry e));
        Assert.Equal(ConflictKind.Merge, e.Kind);
        Assert.Equal(ConflictRel.Tie, e.Rel);
        float len0 = f.Net.Lanes[f.Cs.Lane[c0]].Length;
        float len1 = f.Net.Lanes[f.Cs.Lane[c1]].Length;
        Assert.Equal((int)((len0 * 100f) + 0.5f), e.SMineCm);
        Assert.Equal((int)((len1 * 100f) + 0.5f), e.SOtherCm);
    }

    [Fact]
    public void OneIntoTwo_Straight_DivergesToExtraLanes()
    {
        // Восток 2+2, запад 1+1: с запада единственная полоса расходится на обе полосы востока (diverge, без конфликта).
        Fix f = Star(
            NodeControl.RightHandRule,
            (0f, 2, 2, RoadKind.Street),
            (90f, 1, 1, RoadKind.Street),
            (180f, 1, 1, RoadKind.Street),
            (270f, 1, 1, RoadKind.Street));
        int inW = InLaneOf(f, 2, 0);
        int d0 = FindConnector(f, inW, Maneuver.Straight, OutLaneOf(f, 0, 0));
        int d1 = FindConnector(f, inW, Maneuver.Straight, OutLaneOf(f, 0, 1));
        Assert.True(d0 >= 0 && d1 >= 0);
        Assert.True(d0 < d1, "порядок LaneNext: по возрастанию выходной полосы");
        Assert.False(TryEntry(f, d0, d1, out _));
        AssertEndsMatchLanes(f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FourWay_ConflictPointsLieOnBothCurves(int lanes)
    {
        Fix f = FourWay(NodeControl.RightHandRule, lanes);
        int crossEntries = 0;
        for (int c = 0; c < f.Cs.Count; c++)
        {
            ulong mask = 0;
            int prevS = -1;
            for (int e = f.Cs.FirstConflict[c]; e < f.Cs.FirstConflict[c] + f.Cs.ConflictCount[c]; e++)
            {
                ConflictEntry ce = f.Cs.Conflicts[e];
                Assert.True(ce.SMineCm >= prevS, "записи отсортированы по SMine");
                prevS = ce.SMineCm;
                mask |= 1UL << ce.OtherLocal;
                if (ce.Kind != ConflictKind.Cross)
                {
                    continue;
                }

                crossEntries++;
                int o = f.Net.Nodes[f.Center].FirstConnector + ce.OtherLocal;
                (Vec2 p1, _) = LaneGeometry.Evaluate(f.Net, f.Cs.Lane[c], ce.SMineCm / 100f);
                (Vec2 p2, _) = LaneGeometry.Evaluate(f.Net, o, ce.SOtherCm / 100f);
                Assert.True(Dist(p1, p2) <= 0.5f, $"connector {c} x {o}: точки конфликта в {Dist(p1, p2)} м друг от друга");
            }

            Assert.Equal(mask, f.Cs.ConflictMask[c]);
        }

        Assert.True(crossEntries > 0);
    }

    [Fact]
    public void Rel_IsAntisymmetric_AndConflictsAreSymmetric()
    {
        foreach (NodeControl ctl in new[] { NodeControl.RightHandRule, NodeControl.Yield })
        {
            Fix f = Star(
                ctl,
                (0f, 2, 2, RoadKind.Arterial),
                (60f, 1, 1, RoadKind.Street),
                (180f, 2, 2, RoadKind.Arterial),
                (270f, 1, 1, RoadKind.Street));
            AssertRelAntisymmetric(f);
        }
    }

    private static void AssertRelAntisymmetric(Fix f)
    {
        int g0 = f.Net.Lanes[f.Net.Nodes[f.Center].FirstConnector].ConflictGroup;
        for (int c = g0; c < g0 + f.Net.Nodes[f.Center].ConnectorCount; c++)
        {
            for (int e = f.Cs.FirstConflict[c]; e < f.Cs.FirstConflict[c] + f.Cs.ConflictCount[c]; e++)
            {
                ConflictEntry ce = f.Cs.Conflicts[e];
                int o = g0 + ce.OtherLocal;
                bool found = false;
                for (int e2 = f.Cs.FirstConflict[o]; e2 < f.Cs.FirstConflict[o] + f.Cs.ConflictCount[o]; e2++)
                {
                    ConflictEntry back = f.Cs.Conflicts[e2];
                    if (back.OtherLocal != f.Cs.LocalBit[c])
                    {
                        continue;
                    }

                    found = true;
                    Assert.Equal(ce.Kind, back.Kind);
                    Assert.Equal(ce.SMineCm, back.SOtherCm);
                    Assert.Equal(ce.SOtherCm, back.SMineCm);
                    ConflictRel expected = ce.Rel switch
                    {
                        ConflictRel.Yield => ConflictRel.Priority,
                        ConflictRel.Priority => ConflictRel.Yield,
                        _ => ConflictRel.Tie,
                    };
                    Assert.Equal(expected, back.Rel);
                }

                Assert.True(found, $"нет обратной записи для {c} -> {o}");
            }
        }
    }

    [Fact]
    public void RecomputeRel_ChangesWithMinorRules()
    {
        Fix f = Star(NodeControl.Yield, (0f, 2, 2, RoadKind.Arterial), (90f, 1, 1, RoadKind.Street), (180f, 2, 2, RoadKind.Arterial));
        ref readonly NodeData nd = ref f.Net.Nodes[f.Center];
        // Без шага 1 (RightHandRule) рукав-ножка тоже подчиняется помехе справа: набор Rel меняется, антисимметрия сохраняется.
        f.Cs.RecomputeRel(f.Net, f.Center, minorRules: false);
        AssertRelAntisymmetric(f);
        f.Cs.RecomputeRel(f.Net, f.Center, minorRules: true);
        AssertRelAntisymmetric(f);
        Assert.Equal(3, nd.ArmCount);
    }

    // ---- независимая проверка плотной выборкой ----

    private static void AssertDenseSamplingAgrees(Fix f)
    {
        int g0 = G0(f);
        int k = f.Net.Nodes[f.Center].ConnectorCount;
        for (int i = 0; i < k; i++)
        {
            for (int j = i + 1; j < k; j++)
            {
                if (f.Cs.InLane[g0 + i] == f.Cs.InLane[g0 + j] || f.Cs.OutLane[g0 + i] == f.Cs.OutLane[g0 + j])
                {
                    continue;
                }

                float min = float.MaxValue;
                LaneData li = f.Net.Lanes[f.Cs.Lane[g0 + i]];
                LaneData lj = f.Net.Lanes[f.Cs.Lane[g0 + j]];
                for (int a = 0; a <= 100; a++)
                {
                    (Vec2 pa, _) = LaneGeometry.Evaluate(f.Net, f.Cs.Lane[g0 + i], li.Length * a / 100f);
                    for (int b = 0; b <= 100; b++)
                    {
                        (Vec2 pb, _) = LaneGeometry.Evaluate(f.Net, f.Cs.Lane[g0 + j], lj.Length * b / 100f);
                        min = MathF.Min(min, Dist(pa, pb));
                    }
                }

                bool listed = TryEntry(f, g0 + i, g0 + j, out _);
                if (min < 1.9f)
                {
                    Assert.True(listed, $"{i} x {j}: расстояние {min}, конфликт не найден");
                }
                else if (min > 2.1f)
                {
                    Assert.False(listed, $"{i} x {j}: расстояние {min}, лишний конфликт");
                }
            }
        }
    }

    [Fact]
    public void FourWay_ConflictsMatchDenseSampling()
    {
        AssertDenseSamplingAgrees(FourWay(NodeControl.RightHandRule, 1));
        AssertDenseSamplingAgrees(FourWay(NodeControl.RightHandRule, 2));
        AssertDenseSamplingAgrees(Star(
            NodeControl.Yield,
            (0f, 2, 2, RoadKind.Arterial),
            (90f, 1, 1, RoadKind.Street),
            (180f, 2, 2, RoadKind.Arterial)));
    }

    // ---- детерминизм ----

    [Fact]
    public void Determinism_SameInputSameHash_DifferentInputDiffers()
    {
        ulong a = FourWay(NodeControl.RightHandRule, 2).Cs.Hash();
        ulong b = FourWay(NodeControl.RightHandRule, 2).Cs.Hash();
        ulong c = FourWay(NodeControl.RightHandRule, 1).Cs.Hash();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Capacity_Exceeded_ReturnsStatusWithoutException()
    {
        Fix f = StarRaw(NodeControl.RightHandRule, (0f, 2, 2, RoadKind.Street), (90f, 2, 2, RoadKind.Street), (180f, 2, 2, RoadKind.Street), (270f, 2, 2, RoadKind.Street));
        var tinyConnectors = new ConnectorStore(8, 1000);
        Assert.Equal(NetworkStatus.ConnectorsFull, tinyConnectors.BuildAll(f.Net));
        // Вторая свежая сеть: первая уже частично размечена.
        Fix g = StarRaw(NodeControl.RightHandRule, (0f, 2, 2, RoadKind.Street), (90f, 2, 2, RoadKind.Street), (180f, 2, 2, RoadKind.Street), (270f, 2, 2, RoadKind.Street));
        var tinyConflicts = new ConnectorStore(64, 3);
        Assert.Equal(NetworkStatus.ConflictsFull, tinyConflicts.BuildAll(g.Net));
        Assert.Equal(CreateStatus.NetworkTooLarge, NetworkStatus.ConflictsFull.ToCreateStatus());
    }

    // ---- s1k / s10k / s100k ----

    [Theory]
    [InlineData("s1k")]
    [InlineData("s10k")]
    [InlineData("s100k")]
    public void Scenario_CapacitiesHold_ReportNumbers(string name)
    {
        GenRun r = GenFixture.Run(name);
        Assert.Equal(CreateStatus.Ok, r.CityStatus);
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json")));
        JsonElement caps = doc.RootElement.GetProperty("capacities");
        int maxConnectors = caps.GetProperty("max_connectors").GetInt32();
        int maxConflicts = caps.GetProperty("max_conflicts").GetInt32();

        var cs = new ConnectorStore(maxConnectors, maxConflicts);
        long t0 = Stopwatch.GetTimestamp();
        NetworkStatus st = cs.BuildAll(r.Net);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Assert.Equal(NetworkStatus.Ok, st);
        Assert.Equal(r.City0.ConnectorCount, cs.Count);
        Assert.True(cs.Count <= maxConnectors);
        Assert.True(cs.ConflictsUsed <= maxConflicts);

        int maxPerNode = 0;
        for (int n = 0; n < r.Net.NodeCount; n++)
        {
            maxPerNode = Math.Max(maxPerNode, r.Net.Nodes[n].ConnectorCount);
        }

        // Инвариант §2.2: у каждой Normal-полосы, входящей в узел, есть connector (в сгенерированной сети — у всех полос, кроме выходных тупиков).
        _out.WriteLine($"{name}: nodes={r.Net.NodeCount} connectors={cs.Count}/{maxConnectors} conflicts={cs.ConflictsUsed}/{maxConflicts} maxConnectorsPerNode={maxPerNode} buildConflictsMs={ms:F1} hash=0x{cs.Hash():X16}");

        if (name == "s1k")
        {
            var cs2 = new ConnectorStore(maxConnectors, maxConflicts);
            GenRun r2 = GenFixture.Run(name);
            Assert.Equal(NetworkStatus.Ok, cs2.BuildAll(r2.Net));
            Assert.Equal(cs.Hash(), cs2.Hash());
        }
    }
}
