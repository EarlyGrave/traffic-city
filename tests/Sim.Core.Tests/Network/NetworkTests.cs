using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tests.Network;

public class NetworkTests
{
    private const float Eps = 1e-3f;

    internal static SimCapacities Caps(int nodes = 64, int segs = 128, int lanes = 1024, int pts = 8192) => new()
    {
        MaxNodes = nodes,
        MaxSegments = segs,
        MaxLanes = lanes,
        MaxLanePoints = pts,
        MaxZones = 4,
        MaxBusRoutes = 2,
        MaxBusStops = 4,
        MaxBusStopsPerRoute = 4,
    };

    /// <summary>Крест: центр (0,0) и 4 рукава длиной 100 м (E, S, W, N), по lanes полос в каждую сторону.</summary>
    internal static NetworkStore Cross(int lanes, out NetworkBuilder b)
    {
        var store = new NetworkStore(Caps());
        b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2(0, 0), NodeControl.Yield, out int c));
        Vec2[] ends = { new(100, 0), new(0, 100), new(-100, 0), new(0, -100) };
        foreach (Vec2 e in ends)
        {
            Assert.Equal(NetworkStatus.Ok, b.AddNode(e, NodeControl.None, out int n));
            Assert.Equal(NetworkStatus.Ok, b.AddSegment(c, n, lanes, lanes, 13.9f, RoadKind.Street, out _));
        }

        Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());
        return store;
    }

    private static float PolylineLength(NetworkStore s, in LaneData l)
    {
        float sum = 0;
        for (int i = 1; i < l.PointCount; i++)
        {
            Vec2 d = s.LanePoints[l.FirstPoint + i] - s.LanePoints[l.FirstPoint + i - 1];
            sum += MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        }

        return sum;
    }

    [Fact]
    public void StraightLane_LengthMatchesSegmentAndPolyline()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 1, 1, 13.9f, RoadKind.Street, out int seg));
        float expected = 100f - (2f * b.Options.NodeSetback);
        Assert.Equal(expected, store.Segments[seg].Length, 4);
        for (int i = 0; i < store.LaneCount; i++)
        {
            Assert.Equal(expected, store.Lanes[i].Length, 4);
            Assert.Equal(expected, PolylineLength(store, store.Lanes[i]), 3);
            Assert.Equal(0f, store.LanePointS[store.Lanes[i].FirstPoint]);
            Assert.Equal(expected, store.LanePointS[store.Lanes[i].FirstPoint + store.Lanes[i].PointCount - 1], 4);
        }
    }

    [Fact]
    public void TwoPlusTwo_FourLanesWithDirectionsAndOffsets()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 2, 2, 13.9f, RoadKind.Arterial, out int seg));
        Assert.Equal(4, store.LaneCount);
        SegmentData sd = store.Segments[seg];
        Assert.Equal(0, sd.FirstLaneAB);
        Assert.Equal(2, sd.FirstLaneBA);

        // Вправо по ходу A->B (+X) это +Z. Полоса 0 = крайняя правая.
        float w = b.Options.LaneWidth;
        float[] expectedZ = { 1.5f * w, 0.5f * w, -1.5f * w, -0.5f * w };
        for (int i = 0; i < 4; i++)
        {
            LaneData l = store.Lanes[i];
            Assert.Equal(i < 2 ? 0 : 1, l.Direction);
            Assert.Equal(i % 2, l.LaneNo);
            Assert.Equal(seg, l.Segment);
            Assert.Equal(13.9f, l.SpeedLimit);
            Vec2 a = store.LanePoints[l.FirstPoint];
            Vec2 z = store.LanePoints[l.FirstPoint + l.PointCount - 1];
            Assert.Equal(expectedZ[i], a.Z, 4);
            Assert.Equal(expectedZ[i], z.Z, 4);

            // AB едет в +X, BA — в -X.
            Assert.True(l.Direction == 0 ? z.X > a.X : z.X < a.X);
        }

        Assert.Equal(-1, store.Lanes[0].Right);
        Assert.Equal(1, store.Lanes[0].Left);
        Assert.Equal(0, store.Lanes[1].Right);
        Assert.Equal(-1, store.Lanes[1].Left);
    }

    [Fact]
    public void OneWay_IsCentredOnAxis()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 2, 0, 10f, RoadKind.Street, out int seg));
        Assert.Equal(2, store.LaneCount);
        Assert.Equal(-1, store.Segments[seg].FirstLaneBA);
        float w = b.Options.LaneWidth;
        Assert.Equal(0.5f * w, store.LanePoints[store.Lanes[0].FirstPoint].Z, 4);
        Assert.Equal(-0.5f * w, store.LanePoints[store.Lanes[1].FirstPoint].Z, 4);
    }

    [Fact]
    public void Evaluate_EndsAndMiddleOnStraightLane()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 1, 1, 13.9f, RoadKind.Street, out _);
        NetworkView view = store.GetView();
        float w = b.Options.LaneWidth;
        float len = view.Lanes[0].Length;

        (Vec2 p0, float h0) = LaneGeometry.Evaluate(view, 0, 0f);
        Assert.Equal(7f, p0.X, 4);
        Assert.Equal(0.5f * w, p0.Z, 4);
        Assert.Equal(0f, h0, 4);

        (Vec2 pm, _) = LaneGeometry.Evaluate(view, 0, len * 0.5f);
        Assert.Equal(50f, pm.X, 3);

        (Vec2 pe, float he) = LaneGeometry.Evaluate(view, 0, len);
        Assert.Equal(93f, pe.X, 4);
        Assert.Equal(0f, he, 4);

        // BA-полоса едет в -X: курс = pi, начало на x = 93.
        (Vec2 q0, float hq) = LaneGeometry.Evaluate(view, 1, 0f);
        Assert.Equal(93f, q0.X, 4);
        Assert.Equal(-0.5f * w, q0.Z, 4);
        Assert.Equal(MathF.PI, MathF.Abs(hq), 4);

        // Зажим за границами.
        (Vec2 pc, _) = LaneGeometry.Evaluate(view, 0, len + 50f);
        Assert.Equal(93f, pc.X, 4);
        (Vec2 pn, _) = LaneGeometry.Evaluate(view, 0, -5f);
        Assert.Equal(7f, pn.X, 4);
    }

    [Fact]
    public void Evaluate_PointHintWalksForwardAndBack()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Vec2[] via = { new(40, 0), new(60, 30) };
        b.AddNode(new Vec2(0, 0), NodeControl.None, out int a);
        b.AddNode(new Vec2(100, 30), NodeControl.None, out int c);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(a, c, via, 1, 0, 10f, RoadKind.Street, out _));
        int hint = 0;
        float len = store.Lanes[0].Length;
        LaneGeometry.Evaluate(store, 0, len - 0.01f, ref hint, out _, out _);
        Assert.Equal(store.Lanes[0].PointCount - 2, hint);
        LaneGeometry.Evaluate(store, 0, 0.01f, ref hint, out _, out _);
        Assert.Equal(0, hint);
        int fresh = 0;
        LaneGeometry.Evaluate(store, 0, len * 0.6f, ref hint, out Vec2 p1, out Vec2 d1);
        LaneGeometry.Evaluate(store, 0, len * 0.6f, ref fresh, out Vec2 p2, out Vec2 d2);
        Assert.Equal(p1.X, p2.X);
        Assert.Equal(p1.Z, p2.Z);
        Assert.Equal(d1.X, d2.X);
        Assert.Equal(d1.Z, d2.Z);
    }

    [Fact]
    public void CurvedSegment_AllLanesShareAxisParameterisation()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Vec2[] via = { new(60, 0), new(60, 60) };
        b.AddNode(new Vec2(0, 0), NodeControl.None, out int a);
        b.AddNode(new Vec2(120, 60), NodeControl.None, out int c);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(a, c, via, 2, 2, 10f, RoadKind.Street, out int seg));
        float len = store.Segments[seg].Length;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(len, store.Lanes[i].Length, 4);
            Assert.Equal(len, store.LanePointS[store.Lanes[i].FirstPoint + store.Lanes[i].PointCount - 1], 4);
        }

        // Соседние полосы на одном s лежат на нормали друг к другу: расстояние между ними ~ ширина полосы на прямом участке.
        NetworkView v = store.GetView();
        (Vec2 p0, _) = LaneGeometry.Evaluate(v, 0, 5f);
        (Vec2 p1, _) = LaneGeometry.Evaluate(v, 1, 5f);
        float dz = p0.Z - p1.Z;
        Assert.Equal(b.Options.LaneWidth, dz, 3);
    }

    [Fact]
    public void FourWay_OnePlusOne_Has12Connectors_EndsMatchLanes()
    {
        NetworkStore store = Cross(1, out _);
        Assert.Equal(8, CountKind(store, LaneKind.Normal));
        Assert.Equal(16, CountKind(store, LaneKind.Connector)); // 12 на перекрёстке + 4 разворота на тупиках
        Assert.Equal(12, store.Nodes[0].ConnectorCount);
        Assert.Equal(4, store.Nodes[0].ArmCount);
        AssertConnectorEnds(store);

        // Каждая входная полоса имеет прямо + направо + налево, в порядке прямо, направо, налево.
        for (int i = 0; i < store.LaneCount; i++)
        {
            LaneData l = store.Lanes[i];
            if (l.Kind != LaneKind.Normal || !IsIncomingTo(store, l, 0))
            {
                continue;
            }

            Assert.Equal(3, l.NextCount);
        }
    }

    [Fact]
    public void FourWay_TwoPlusTwo_Has16Connectors_WithTurnRules()
    {
        NetworkStore store = Cross(2, out _);
        Assert.Equal(16, store.Nodes[0].ConnectorCount);
        AssertConnectorEnds(store);

        // Входная полоса 0 (правая) => прямо + направо (2), полоса 1 (левая) => прямо + налево (2).
        for (int i = 0; i < store.LaneCount; i++)
        {
            LaneData l = store.Lanes[i];
            if (l.Kind == LaneKind.Normal && IsIncomingTo(store, l, 0))
            {
                Assert.Equal(2, l.NextCount);
            }
        }
    }

    [Fact]
    public void FourWay_StraightConnector_GoesAcrossTheNode()
    {
        NetworkStore store = Cross(1, out _);
        // Рукав E: сегмент 0 (A = центр), входящая в центр полоса — BA (индекс 1). Первый connector полосы 1 = «прямо».
        LaneData inLane = store.Lanes[1];
        Assert.Equal(1, inLane.Direction);
        LaneData first = store.Lanes[store.LaneNext[inLane.FirstNext]];
        Vec2 a = store.LanePoints[first.FirstPoint];
        Vec2 z = store.LanePoints[first.FirstPoint + first.PointCount - 1];
        Assert.True(z.X < 0 && a.X > 0, "прямо через центр, из +X в -X");
        Assert.Equal(a.X - z.X, first.Length, 2);   // прямо: от стоп-линии E до начала W (отступы рукавов - ADR-0028)
    }

    [Fact]
    public void Connectors_HeadingIsContinuous()
    {
        NetworkStore store = Cross(2, out _);
        NetworkView v = store.GetView();
        for (int i = 0; i < v.Lanes.Length; i++)
        {
            LaneData l = v.Lanes[i];
            if (l.Kind != LaneKind.Connector)
            {
                continue;
            }

            // Разворот (петля в 8 точек) поворачивает курс на ~0.9 рад за звено полилинии.
            bool uturn = v.Lanes[l.FromLane].Segment == v.Lanes[l.ToLane].Segment;
            float maxJump = uturn ? 1.2f : 0.7f;
            float prev = LaneGeometry.Evaluate(v, i, 0f).Heading;
            for (int k = 1; k <= 100; k++)
            {
                float h = LaneGeometry.Evaluate(v, i, l.Length * k / 100f).Heading;
                float dh = MathF.Abs(MathF.IEEERemainder(h - prev, 2f * MathF.PI));
                Assert.True(dh < maxJump, $"connector {i}: скачок курса {dh} рад");
                prev = h;
            }

            // Курс connector'а в начале/конце близок к курсу входной/выходной полосы (касательная Безье).
            float hIn = LaneGeometry.Evaluate(v, l.FromLane, v.Lanes[l.FromLane].Length).Heading;
            float hOut = LaneGeometry.Evaluate(v, l.ToLane, 0f).Heading;
            Assert.True(MathF.Abs(MathF.IEEERemainder(LaneGeometry.Evaluate(v, i, 0f).Heading - hIn, 2f * MathF.PI)) < 0.7f);
            Assert.True(MathF.Abs(MathF.IEEERemainder(LaneGeometry.Evaluate(v, i, l.Length).Heading - hOut, 2f * MathF.PI)) < 0.7f);
        }
    }

    [Fact]
    public void DeadEnd_GetsUTurnConnectors_NoUTurnsOtherwise()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 2, 2, 10f, RoadKind.Street, out _));
        Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());
        // Два тупика, по 2 входных полосы -> 4 разворота.
        Assert.Equal(4, CountKind(store, LaneKind.Connector));
        AssertConnectorEnds(store);
        foreach (LaneData l in store.Lanes.AsSpan(0, store.LaneCount))
        {
            if (l.Kind == LaneKind.Connector)
            {
                Assert.NotEqual(-1, store.Lanes[l.FromLane].Segment);
                Assert.Equal(store.Lanes[l.FromLane].Segment, store.Lanes[l.ToLane].Segment);
            }
        }
    }

    [Fact]
    public void TJunction_MiddleLanesStillGetConnectors()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        b.AddNode(new Vec2(0, 0), NodeControl.Stop, out int c);
        b.AddNode(new Vec2(100, 0), NodeControl.None, out int e);
        b.AddNode(new Vec2(-100, 0), NodeControl.None, out int w);
        b.AddNode(new Vec2(0, 100), NodeControl.None, out int s);
        b.AddSegment(c, e, 3, 3, 10f, RoadKind.Street, out _);
        b.AddSegment(c, w, 3, 3, 10f, RoadKind.Street, out _);
        b.AddSegment(c, s, 3, 3, 10f, RoadKind.Street, out _);
        Assert.Equal(NetworkStatus.Ok, b.BuildConnectorsEx(c));
        for (int i = 0; i < store.LaneCount; i++)
        {
            LaneData l = store.Lanes[i];
            if (l.Kind == LaneKind.Normal && IsIncomingTo(store, l, c))
            {
                Assert.True(l.NextCount > 0, $"полоса {i} без connector'ов");
            }
        }

        AssertConnectorEnds(store);
        Assert.Equal(NetworkStatus.NodeAlreadyBuilt, b.BuildConnectorsEx(c));
    }

    [Fact]
    public void Snap_MergesNearbyNodesAndKeepsFarOnes()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        b.AddNode(new Vec2(100, 100), NodeControl.Signal, out int n0);
        b.AddNode(new Vec2(103, 104), NodeControl.None, out int n1); // 5.0 м — на границе радиуса
        b.AddNode(new Vec2(102, 101), NodeControl.None, out int n2);
        b.AddNode(new Vec2(106, 100), NodeControl.None, out int n3); // 6 м — дальше
        b.AddNode(new Vec2(94.5f, 100), NodeControl.None, out int n4); // другая ячейка сетки, 5.5 м
        b.AddNode(new Vec2(-0.5f, -0.5f), NodeControl.None, out int n5);
        b.AddNode(new Vec2(0.5f, 0.5f), NodeControl.None, out int n6); // через границу ячейки в 0
        Assert.Equal(n0, n1);
        Assert.Equal(n0, n2);
        Assert.NotEqual(n0, n3);
        Assert.NotEqual(n0, n4);
        Assert.Equal(n5, n6);
        Assert.Equal(4, store.NodeCount);
        Assert.Equal(NodeControl.Signal, store.Nodes[n0].Control);
        Assert.Equal(n0, b.FindNode(new Vec2(101, 99)));
        Assert.Equal(-1, b.FindNode(new Vec2(300, 300)));
    }

    [Fact]
    public void AddSegmentByPosition_SnapsEndpointsToExistingNodes()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 1, 1, 10f, RoadKind.Street, out int s0));
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(100.5f, 1f), new Vec2(100, 100), 1, 1, 10f, RoadKind.Street, out int s1));
        Assert.Equal(3, store.NodeCount);
        Assert.Equal(store.Segments[s0].NodeB, store.Segments[s1].NodeA);
    }

    [Fact]
    public void InvalidArguments_ReturnInvalidStatus()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        b.AddNode(new Vec2(0, 0), NodeControl.None, out int a);
        b.AddNode(new Vec2(100, 0), NodeControl.None, out int c);
        Assert.Equal(NetworkStatus.InvalidArgument, b.AddSegment(a, a, 1, 1, 10f, RoadKind.Street, out _));
        Assert.Equal(NetworkStatus.InvalidArgument, b.AddSegment(a, 99, 1, 1, 10f, RoadKind.Street, out _));
        Assert.Equal(NetworkStatus.InvalidArgument, b.AddSegment(a, c, 0, 0, 10f, RoadKind.Street, out _));
        Assert.Equal(NetworkStatus.InvalidArgument, b.AddSegment(a, c, 1, 1, 0f, RoadKind.Street, out _));
        Assert.Equal(NetworkStatus.InvalidArgument, b.AddSegment(a, c, 9, 1, 10f, RoadKind.Street, out _));
        Assert.Equal(NetworkStatus.InvalidArgument, b.BuildConnectorsEx(77));
        b.AddNode(new Vec2(3, 0), NodeControl.None, out int near);
        Assert.Equal(a, near); // snap
        b.AddNode(new Vec2(12, 0), NodeControl.None, out int d);
        Assert.Equal(NetworkStatus.SegmentTooShort, b.AddSegment(a, d, 1, 1, 10f, RoadKind.Street, out _));
        Assert.Equal(0, store.SegmentCount);
        Assert.Equal(0, store.LaneCount);
    }

    [Fact]
    public void CapacityOverflow_ReturnsStatusAndLeavesStoreUntouched()
    {
        // Узлы.
        var s1 = new NetworkStore(Caps(nodes: 2));
        var b1 = new NetworkBuilder(s1);
        b1.AddNode(new Vec2(0, 0), NodeControl.None, out _);
        b1.AddNode(new Vec2(100, 0), NodeControl.None, out _);
        Assert.Equal(NetworkStatus.NodesFull, b1.AddNode(new Vec2(200, 0), NodeControl.None, out int bad));
        Assert.Equal(-1, bad);
        Assert.Equal(CreateStatus.NetworkTooLarge, NetworkStatus.NodesFull.ToCreateStatus());

        // Сегменты.
        var s2 = new NetworkStore(Caps(segs: 1));
        var b2 = new NetworkBuilder(s2);
        Assert.Equal(NetworkStatus.Ok, b2.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 1, 1, 10f, RoadKind.Street, out _));
        ulong before = s2.Hash();
        b2.AddNode(new Vec2(200, 0), NodeControl.None, out int far);
        before = s2.Hash();
        Assert.Equal(NetworkStatus.SegmentsFull, b2.AddSegment(s2.Segments[0].NodeB, far, 1, 1, 10f, RoadKind.Street, out _));
        Assert.Equal(before, s2.Hash());

        // Полосы.
        var s3 = new NetworkStore(Caps(lanes: 3));
        var b3 = new NetworkBuilder(s3);
        Assert.Equal(NetworkStatus.LanesFull, b3.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 2, 2, 10f, RoadKind.Street, out _));
        Assert.Equal(0, s3.SegmentCount);

        // Точки полилиний.
        var s4 = new NetworkStore(Caps(pts: 3));
        var b4 = new NetworkBuilder(s4);
        Assert.Equal(NetworkStatus.PointsFull, b4.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 1, 1, 10f, RoadKind.Street, out _));
        Assert.True(NetworkStatus.PointsFull.IsCapacity());
        Assert.Equal(0, s4.LaneCount);

        // connector'ы не влезают в полосы: узел остаётся не построенным, store не меняется.
        var s5 = new NetworkStore(Caps(lanes: 9));
        var b5 = new NetworkBuilder(s5);
        b5.AddNode(new Vec2(0, 0), NodeControl.None, out int c);
        foreach (Vec2 e in new[] { new Vec2(100, 0), new Vec2(0, 100), new Vec2(-100, 0), new Vec2(0, -100) })
        {
            b5.AddNode(e, NodeControl.None, out int n);
            Assert.Equal(NetworkStatus.Ok, b5.AddSegment(c, n, 1, 1, 10f, RoadKind.Street, out _));
        }

        ulong h5 = s5.Hash();
        Assert.Equal(NetworkStatus.LanesFull, b5.BuildConnectorsEx(c));
        Assert.Equal(h5, s5.Hash());
        Assert.Equal(-1, s5.Nodes[c].FirstArm);
    }

    [Fact]
    public void View_ExposesStoreArraysAndVersionChanges()
    {
        var store = new NetworkStore(Caps());
        var b = new NetworkBuilder(store);
        int v0 = store.GetView().Version;
        b.AddSegment(new Vec2(0, 0), new Vec2(100, 0), 1, 1, 10f, RoadKind.Street, out _);
        b.BuildAllConnectorsEx();
        NetworkView v = store.GetView();
        Assert.True(v.Version > v0);
        Assert.Equal(2, v.Nodes.Length);
        Assert.Equal(1, v.Segments.Length);
        Assert.Equal(store.LaneCount, v.Lanes.Length);
        Assert.Equal(store.LanePointCount, v.LanePoints.Length);
        Assert.Equal(v.LanePoints.Length, v.LanePointS.Length);
        Assert.Equal(0, v.Zones.Length);
        Assert.Equal(0, v.Lanes[0].FirstPoint);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        ulong h1 = BuildGrid().Hash();
        ulong h2 = BuildGrid().Hash();
        Assert.Equal(h1, h2);
        Assert.NotEqual(Fnv1a64.Offset, h1);
    }

    [Fact]
    public void Build_DifferentInputGivesDifferentHash()
    {
        Assert.NotEqual(BuildGrid().Hash(), BuildGrid(lanes: 2).Hash());
    }

    private static NetworkStore BuildGrid(int lanes = 1)
    {
        var store = new NetworkStore(Caps(nodes: 64, segs: 128, lanes: 4096, pts: 65536));
        var b = new NetworkBuilder(store);
        const int N = 4;
        for (int i = 0; i < N; i++)
        {
            for (int j = 0; j < N; j++)
            {
                if (i + 1 < N)
                {
                    Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(i * 100, j * 100), new Vec2((i + 1) * 100, j * 100), lanes, lanes, 13.9f, RoadKind.Street, out _));
                }

                if (j + 1 < N)
                {
                    Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(i * 100, j * 100), new Vec2(i * 100, (j + 1) * 100), lanes, lanes, 13.9f, RoadKind.Arterial, out _));
                }
            }
        }

        Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());
        return store;
    }

    private static int CountKind(NetworkStore s, LaneKind kind)
    {
        int n = 0;
        for (int i = 0; i < s.LaneCount; i++)
        {
            if (s.Lanes[i].Kind == kind)
            {
                n++;
            }
        }

        return n;
    }

    private static bool IsIncomingTo(NetworkStore s, in LaneData l, int node)
    {
        SegmentData sd = s.Segments[l.Segment];
        return l.Direction == 0 ? sd.NodeB == node : sd.NodeA == node;
    }

    private static void AssertConnectorEnds(NetworkStore s)
    {
        for (int i = 0; i < s.LaneCount; i++)
        {
            LaneData c = s.Lanes[i];
            if (c.Kind != LaneKind.Connector)
            {
                continue;
            }

            LaneData from = s.Lanes[c.FromLane];
            LaneData to = s.Lanes[c.ToLane];
            Vec2 fromEnd = s.LanePoints[from.FirstPoint + from.PointCount - 1];
            Vec2 toStart = s.LanePoints[to.FirstPoint];
            Vec2 cStart = s.LanePoints[c.FirstPoint];
            Vec2 cEnd = s.LanePoints[c.FirstPoint + c.PointCount - 1];
            Assert.True(Near(fromEnd, cStart), $"connector {i}: начало не совпало с концом входной полосы");
            Assert.True(Near(toStart, cEnd), $"connector {i}: конец не совпал с началом выходной полосы");
            Assert.Equal(c.Length, s.LanePointS[c.FirstPoint + c.PointCount - 1], 4);
            Assert.Equal(0f, s.LanePointS[c.FirstPoint]);
            Assert.True(c.Length > 0f);
            Assert.Equal(c.ToLane, s.LaneNext[c.FirstNext]);
            Assert.Equal(1, c.NextCount);
            Assert.Equal(c.Node, from.Direction == 0 ? s.Segments[from.Segment].NodeB : s.Segments[from.Segment].NodeA);
        }

        // Диапазоны LaneNext входных полос указывают на connector'ы, чей FromLane — эта полоса.
        for (int i = 0; i < s.LaneCount; i++)
        {
            LaneData l = s.Lanes[i];
            if (l.Kind != LaneKind.Normal)
            {
                continue;
            }

            for (int k = 0; k < l.NextCount; k++)
            {
                LaneData c = s.Lanes[s.LaneNext[l.FirstNext + k]];
                Assert.Equal(LaneKind.Connector, c.Kind);
                Assert.Equal(i, c.FromLane);
            }
        }
    }

    private static bool Near(Vec2 a, Vec2 b) => MathF.Abs(a.X - b.X) <= Eps && MathF.Abs(a.Z - b.Z) <= Eps;
}
