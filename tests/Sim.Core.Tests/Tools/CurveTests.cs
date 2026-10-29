using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Scenarios.Tools;

namespace Sim.Core.Tests.Tools;

/// <summary>T5 (player-tools.md §2): кривые Безье в <c>BuildRoad</c> - сэмплирование, превью = применение, <c>TooSharp</c>, профиль вместе с кривой, undo сноса, сейв.</summary>
public sealed class CurveTests
{
    private static Simulation Quiet()
    {
        ScenarioDesc d = ToolsFixture.SmallDesc(buses: false);
        d.Demand.TargetVehicles = 0;
        Simulation sim = Simulation.Create(d, new SimRuntimeOptions());
        sim.SetToolsConfig(new ToolsDesc
        {
            HasWorldBounds = true,
            BoundsMinX = -1e6f,
            BoundsMinZ = -1e6f,
            BoundsMaxX = 1e6f,
            BoundsMaxZ = 1e6f,
            Costs = new ToolCostsDesc { RoadPerMLaneStreet = 10, RoadPerMLaneArterial = 20, CurveFactorPct = 150 },
        });
        sim.Step(20);
        return sim;
    }

    private static BuildRoadCmd Curve(Vec2 a, Vec2 b, Vec2 c)
    {
        BuildRoadCmd r = ToolsFixture.Road(a, b, 1);
        r.Flags = BuildRoadFlags.Curve;
        r.SetControl(c.X, c.Z);
        return r;
    }

    private static int NewSegment(Simulation sim)
    {
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.SegmentCreated)
            {
                return e.A;
            }
        }

        return -1;
    }

    [Fact]
    public void Curve_BuildsSampledAxis_PreviewMatches_CostHasCurveFactor()
    {
        using Simulation sim = Quiet();
        NetworkStore n = sim.World.Net;
        var a = new Vec2(-3000f, -3000f);
        var b = new Vec2(-2800f, -3000f);
        var c = new Vec2(-2900f, -2900f);
        BuildRoadCmd road = Curve(a, b, c);
        Command cmd = Command.MakeBuildRoad(in road, 1);
        Command copy = cmd;
        var axis = new Vec2[64];
        Assert.Equal(RejectReason.None, sim.Preview(in copy, out PreviewResult pr, new PreviewIssue[8], axis));
        Assert.Equal(32, pr.AxisPointCount);   // ceil((|AC| + |CB|) / 8) = 36 -> предел 32
        Assert.Equal(a.X, axis[0].X, 3);
        Assert.Equal(b.X, axis[31].X, 3);
        Assert.Equal(-2950f, (axis[15].Z + axis[16].Z) * 0.5f, 0);   // вершина параметра 0.5: (A + 2C + B) / 4
        Assert.True(pr.LengthM > 200f);

        BuildRoadCmd straight = ToolsFixture.Road(new Vec2(a.X, a.Z - 500f), new Vec2(b.X, b.Z - 500f), 1);
        Command s2 = Command.MakeBuildRoad(in straight, 2);
        Assert.Equal(RejectReason.None, sim.Preview(in s2, out PreviewResult ps, new PreviewIssue[8]));
        Assert.True(pr.Cost > ps.Cost * 3 / 2 - 1, $"кривая {pr.Cost}, прямая {ps.Cost}");

        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in cmd));
        int seg = NewSegment(sim);
        Assert.True(seg >= 0);
        SegmentData sd = n.Segments[seg];
        Assert.NotEqual(0, sd.Flags & SegmentFlags.Curve);
        Assert.Equal(c.X, sd.Cx, 1);
        Assert.Equal(c.Z, sd.Cz, 1);
        Assert.True(sd.PointCount > 10, $"точек оси {sd.PointCount}");
        Assert.Null(sim.World.VerifyDerived());
        sim.Step(20);
        using Simulation twin = ToolsFixture.RoundTrip(sim);
        Assert.NotEqual(0, twin.World.Net.Segments[seg].Flags & SegmentFlags.Curve);
    }

    [Fact]
    public void Curve_TooSharp_Rejected_AndPreviewAgrees()
    {
        using Simulation sim = Quiet();
        // Контрольная точка далеко за концами: петля с радиусом кривизны < 12 м.
        BuildRoadCmd road = Curve(new Vec2(-3000f, -3000f), new Vec2(-2960f, -3000f), new Vec2(-2980f, -2300f));
        Command cmd = Command.MakeBuildRoad(in road, 3);
        Command copy = cmd;
        Assert.Equal(RejectReason.TooSharp, sim.Preview(in copy, out _, new PreviewIssue[8]));
        Assert.Equal(RejectReason.TooSharp, ToolsFixture.Run(sim, in cmd));
    }

    [Fact]
    public void Curve_RemoveThenUndo_RestoresCurve()
    {
        using Simulation sim = Quiet();
        NetworkStore n = sim.World.Net;
        var stack = new UndoStack();
        BuildRoadCmd road = Curve(new Vec2(-3000f, -3000f), new Vec2(-2800f, -3000f), new Vec2(-2900f, -2920f));
        Assert.True(stack.Execute(sim, new[] { Command.MakeBuildRoad(in road) }).Recorded);
        int seg = NewSegment(sim);
        Assert.True(seg >= 0);
        float cx = n.Segments[seg].Cx;
        float cz = n.Segments[seg].Cz;
        Assert.True(stack.Execute(sim, new[] { Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }) }).Recorded);
        for (int t = 0; t < 400 && sim.World.Ops.Count > 0; t++)
        {
            sim.Step(1);
        }

        Assert.False(n.SegmentAlive(seg) && n.Segments[seg].Flags != 0 && (n.Segments[seg].Flags & SegmentFlags.Closing) == 0);
        Assert.True(stack.Undo(sim).Ok);
        int back = -1;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            back = n.SegmentAlive(s) && (n.Segments[s].Flags & SegmentFlags.Curve) != 0 ? s : back;
        }

        Assert.True(back >= 0, "кривая не вернулась");
        Assert.Equal(cx, n.Segments[back].Cx, 1);
        Assert.Equal(cz, n.Segments[back].Cz, 1);
        Assert.Null(sim.World.VerifyDerived());
    }

    [Fact]
    public void BuildRoadCmd_ControlPoint_RoundTripsInDecimetres()
    {
        var r = new BuildRoadCmd { Ax = 100f, Az = 200f, Bx = 300f, Bz = 200f };
        r.SetControl(212.34f, 251.07f);
        Assert.Equal(212.3f, r.ControlX, 3);
        Assert.Equal(251.1f, r.ControlZ, 3);
        r.Profile = 7;
        Assert.Equal(212.3f, r.ControlX, 3);   // профиль больше не делит байты с контрольной точкой
    }
}
