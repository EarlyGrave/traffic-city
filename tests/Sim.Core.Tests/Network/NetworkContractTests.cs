using System;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tests.Network;

/// <summary>Контракт генератора (city-generator §7.1) и узлы со степенью до 8.</summary>
public class NetworkContractTests
{
    private static NetworkStore Star(int arms, byte lanes, out NetworkBuilder b)
    {
        var store = new NetworkStore(NetworkTests.Caps(nodes: 32, segs: 32, lanes: 2048, pts: 16384));
        b = new NetworkBuilder(store, 3.5f, 8);
        int c = b.AddNode(new Vec2(0, 0), NodeControl.RightHandRule);
        Assert.Equal(0, c);
        for (int i = 0; i < arms; i++)
        {
            float a = i * 2f * MathF.PI / arms;
            int n = b.AddNode(new Vec2(120f * MathF.Cos(a), 120f * MathF.Sin(a)), NodeControl.None);
            Assert.True(n > 0);
            Assert.True(b.AddSegment(c, n, lanes, lanes, 11f, RoadKind.Street) >= 0);
        }

        return store;
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    [InlineData(6, 2)]
    [InlineData(8, 1)]
    public void HighDegreeNode_BuildsValidConnectors(int arms, int lanes)
    {
        NetworkStore store = Star(arms, (byte)lanes, out NetworkBuilder b);
        Assert.Equal(arms, b.ArmCount(0));
        Assert.True(b.BuildAllConnectors());
        Assert.Equal(arms, store.Nodes[0].ArmCount);
        Assert.True(store.Nodes[0].ConnectorCount >= arms * lanes);

        // Рукава отсортированы по часовой стрелке (по возрастанию угла atan2(z, x)), сегменты созданы в этом же порядке.
        for (int k = 0; k < arms; k++)
        {
            Assert.Equal(k, b.ArmSegment(0, k));
        }

        // Каждая входная полоса центрального узла имеет выход; концы connector'ов совпадают.
        for (int i = 0; i < store.LaneCount; i++)
        {
            LaneData l = store.Lanes[i];
            if (l.Kind == LaneKind.Normal && store.Segments[l.Segment].NodeA == 0 && l.Direction == 1)
            {
                Assert.True(l.NextCount > 0);
            }

            if (l.Kind == LaneKind.Connector)
            {
                LaneData f = store.Lanes[l.FromLane];
                LaneData t = store.Lanes[l.ToLane];
                Vec2 fe = store.LanePoints[f.FirstPoint + f.PointCount - 1];
                Vec2 cs = store.LanePoints[l.FirstPoint];
                Vec2 ts = store.LanePoints[t.FirstPoint];
                Vec2 ce = store.LanePoints[l.FirstPoint + l.PointCount - 1];
                Assert.True(MathF.Abs(fe.X - cs.X) < 1e-3f && MathF.Abs(fe.Z - cs.Z) < 1e-3f);
                Assert.True(MathF.Abs(ts.X - ce.X) < 1e-3f && MathF.Abs(ts.Z - ce.Z) < 1e-3f);
                Assert.True(l.Length > 0f);
            }
        }
    }

    [Fact]
    public void ContractApi_ReturnsMinusOneOnFailure_AndSetNodeControlWorks()
    {
        var store = new NetworkStore(NetworkTests.Caps(nodes: 2, segs: 1));
        var b = new NetworkBuilder(store, 3.5f, 8);
        int a = b.AddNode(new Vec2(0, 0), NodeControl.None);
        int c = b.AddNode(new Vec2(100, 0), NodeControl.None);
        Assert.Equal(-1, b.AddNode(new Vec2(500, 0), NodeControl.None));
        Assert.Equal(-1, b.AddSegment(a, a, 1, 1, 10f, RoadKind.Street));
        Assert.Equal(0, b.AddSegment(a, c, 1, 1, 10f, RoadKind.Street));
        Assert.Equal(-1, b.AddSegment(a, c, 1, 1, 10f, RoadKind.Street)); // MaxSegments
        b.SetNodeControl(a, NodeControl.Signal);
        Assert.Equal(NodeControl.Signal, store.Nodes[a].Control);
        Assert.Equal(new Vec2(100, 0).X, b.NodePosition(c).X);
        Assert.Equal(2, b.NodeCount);
        Assert.Equal(1, b.SegmentCount);
        Assert.Equal(2, b.LaneCount);
        Assert.Equal(4, b.LanePointCount);
        Assert.Equal(1, b.ArmCount(a));
        Assert.Equal(-1, b.ArmSegment(a, 1));
    }
}
