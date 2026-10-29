using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Util;
using Sim.Scenarios.Tools;

namespace Sim.Core.Tests.Tools;

/// <summary>
/// T5 (player-tools.md §4.4, §4.5, §14 <c>Upgrade_Split_Merge_Relocation</c>): разрез, слияние и правка полос дороги с машинами - перенос без потерь (позиция в мире сохраняется),
/// производные равны перестроенным, сейв-загрузка, undo/redo, 0 Б/тик.
/// </summary>
public sealed class SplitMergeTests
{
    // Сегмент без остановок с наибольшим числом машин и полной осью не короче minFull.
    private static int BusiestSegment(Simulation sim, float minFull)
    {
        World w = sim.World;
        NetworkStore n = w.Net;
        int best = -1;
        int bestCount = -1;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s) || Sim.Core.Tools.ToolValidator.HasBusStop(n, s) || w.Builder.FullAxisLength(s) < minFull)
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[s];
            int cnt = 0;
            for (int k = 0; k < sd.LanesAB + sd.LanesBA; k++)
            {
                cnt += w.Occupancy.LaneCount[(sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA) + k];
            }

            if (cnt > bestCount)
            {
                bestCount = cnt;
                best = s;
            }
        }

        return best;
    }

    // Мировая точка полосы на расстоянии s от её начала (по точкам полосы).
    internal static Vec2 PointOf(NetworkStore n, int lane, float s)
    {
        ref readonly LaneData ld = ref n.Lanes[lane];
        for (int i = 1; i < ld.PointCount; i++)
        {
            float s0 = n.LanePointS[ld.FirstPoint + i - 1];
            float s1 = n.LanePointS[ld.FirstPoint + i];
            if (s <= s1 || i == ld.PointCount - 1)
            {
                Vec2 a = n.LanePoints[ld.FirstPoint + i - 1];
                Vec2 b = n.LanePoints[ld.FirstPoint + i];
                float f = s1 > s0 ? Math.Clamp((s - s0) / (s1 - s0), 0f, 1f) : 0f;
                return a + ((b - a) * f);
            }
        }

        return n.LanePoints[ld.FirstPoint];
    }

    private static Dictionary<int, Vec2> Positions(Simulation sim)
    {
        World w = sim.World;
        var map = new Dictionary<int, Vec2>();
        for (int i = 0; i < w.Vehicles.HighWater; i++)
        {
            if (w.Vehicles.Pool.IsAlive(i) && w.Vehicles.Lane[i] >= 0 && w.Vehicles.GhostLane[i] < 0)
            {
                map[i] = PointOf(w.Net, w.Vehicles.Lane[i], w.Vehicles.S[i]);
            }
        }

        return map;
    }

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    // Машины, бывшие на сегментах цепочки, стоят там же в мире: на полосах сегментов - до 1 см, на connector'ах нового узла - до 0.5 м (кривая вместо прямой).
    private static void AssertSamePlaces(Simulation sim, Dictionary<int, Vec2> before, HashSet<int> moved)
    {
        World w = sim.World;
        int checkedCars = 0;
        foreach (int i in moved)
        {
            Assert.True(w.Vehicles.Pool.IsAlive(i), $"машина {i} пропала");
            int lane = w.Vehicles.Lane[i];
            float tol = w.Net.Lanes[lane].Kind == LaneKind.Connector ? 0.5f : 0.01f;
            float d = Dist(before[i], PointOf(w.Net, lane, w.Vehicles.S[i]));
            Assert.True(d <= tol, $"машина {i}: сдвиг {d} м (полоса {lane}, {w.Net.Lanes[lane].Kind})");
            checkedCars++;
        }

        Assert.True(checkedCars > 0, "на правленой дороге не было машин");
    }

    private static HashSet<int> OnSegments(Simulation sim, params int[] segs)
    {
        World w = sim.World;
        var set = new HashSet<int>();
        for (int i = 0; i < w.Vehicles.HighWater; i++)
        {
            int lane = w.Vehicles.Lane[i];
            if (w.Vehicles.Pool.IsAlive(i) && lane >= 0 && w.Vehicles.GhostLane[i] < 0 && Array.IndexOf(segs, w.Net.Lanes[lane].Segment) >= 0)
            {
                set.Add(i);
            }
        }

        return set;
    }

    private static Command Split(Simulation sim, int seg, float s, uint tag) =>
        Command.MakeSplitRoad(new SplitRoadCmd { Seg = ToolsFixture.Segment(sim, seg), S = s }, tag);

    private static Command Merge(Simulation sim, int node, uint tag) =>
        Command.MakeMergeAtNode(new MergeAtNodeCmd { Node = ToolsFixture.Node(sim, node) }, tag);

    [Fact]
    public void Upgrade_Split_Merge_Relocation()
    {
        using Simulation sim = ToolsFixture.Small(600);
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        World w = sim.World;
        NetworkStore n = w.Net;
        int seg = BusiestSegment(sim, 90f);
        Assert.True(seg >= 0);
        int alive0 = w.Vehicles.Pool.AliveCount;
        int segs0 = n.AliveSegments;
        int nodes0 = n.AliveNodes;
        SegmentData sd = n.Segments[seg];
        int a = sd.NodeA;
        int b = sd.NodeB;
        float full = w.Builder.FullAxisLength(seg);
        Dictionary<int, Vec2> before = Positions(sim);
        HashSet<int> moved = OnSegments(sim, seg);

        // Разрез посередине: только фаза 1 (команды), без движения - позиции сверяются точно.
        Command split = Split(sim, seg, full * 0.5f, 7001);
        Command copy = split;
        Assert.Equal(RejectReason.None, sim.Preview(in copy, out PreviewResult pr, new PreviewIssue[8]));
        Assert.Equal(1, pr.Mechanism);
        Assert.True(sim.Enqueue(split));
        RunPhaseOneOnly(sim);
        Assert.Equal(segs0 + 1, n.AliveSegments);
        Assert.Equal(nodes0 + 1, n.AliveNodes);
        Assert.False(n.SegmentAlive(seg) && n.Segments[seg].Generation == sd.Generation);
        AssertSamePlaces(sim, before, moved);
        Assert.Equal(alive0, w.Vehicles.Pool.AliveCount);
        Assert.Null(w.VerifyDerived());

        // Новый узел - между A и B, степени 2.
        int mid = -1;
        for (int i = 0; i < n.NodeCount && mid < 0; i++)
        {
            mid = n.NodeAlive(i) && i != a && i != b && n.Nodes[i].ArmCount == 2 && ChainEdit.TwoSegments(n, i, out int x1, out int x2) &&
                ((ChainEdit.Far(n, x1, i) == a && ChainEdit.Far(n, x2, i) == b) || (ChainEdit.Far(n, x1, i) == b && ChainEdit.Far(n, x2, i) == a)) ? i : -1;
        }

        Assert.True(mid >= 0, "узел разреза не найден");
        sim.Step(200);
        Assert.Null(w.VerifyDerived());

        // Слияние обратно.
        ChainEdit.TwoSegments(n, mid, out int h1, out int h2);
        before = Positions(sim);
        moved = OnSegments(sim, h1, h2);
        Assert.True(sim.Enqueue(Merge(sim, mid, 7002)));
        RunPhaseOneOnly(sim);
        Assert.Equal(segs0, n.AliveSegments);
        Assert.Equal(nodes0, n.AliveNodes);
        AssertSamePlaces(sim, before, moved);
        Assert.Null(w.VerifyDerived());

        // Больше полос на занятом сегменте: машины сохраняют номер полосы, направление и S.
        int merged = -1;
        for (int s = 0; s < n.SegmentCount && merged < 0; s++)
        {
            merged = n.SegmentAlive(s) && ((n.Segments[s].NodeA == a && n.Segments[s].NodeB == b) || (n.Segments[s].NodeA == b && n.Segments[s].NodeB == a)) ? s : -1;
        }

        Assert.True(merged >= 0);
        moved = OnSegments(sim, merged);
        var lanes = new Dictionary<int, (int Dir, int No, float S)>();
        foreach (int i in moved)
        {
            LaneData ld = n.Lanes[w.Vehicles.Lane[i]];
            lanes[i] = (ld.Direction, ld.LaneNo, w.Vehicles.S[i]);
        }

        SegmentData m = n.Segments[merged];
        var up = new UpgradeRoadCmd { Seg = ToolsFixture.Segment(sim, merged), LanesAB = (byte)(m.LanesAB + 1), LanesBA = (byte)(m.LanesBA + 1), Flags = UpgradeRoadFlags.KeepKind };
        Assert.True(sim.Enqueue(Command.MakeUpgradeRoad(in up, 7003)));
        RunPhaseOneOnly(sim);
        if (w.Ops.FindShrink(merged) < 0)
        {
            foreach (int i in moved)
            {
                LaneData ld = n.Lanes[w.Vehicles.Lane[i]];
                Assert.Equal(merged, ld.Segment);
                Assert.Equal(lanes[i].Dir, ld.Direction);
                Assert.Equal(lanes[i].No, ld.LaneNo);
                Assert.Equal(lanes[i].S, w.Vehicles.S[i]);
            }
        }

        sim.Step(600);
        Assert.Null(w.VerifyDerived());
        Assert.True(w.Vehicles.Pool.AliveCount > alive0 / 2, "город не должен опустеть");
        using Simulation twin = ToolsFixture.RoundTrip(sim);
        twin.Step(40);
        sim.Step(40);
        Assert.Equal(sim.ComputeStateHash(), twin.ComputeStateHash());
    }

    // Только фаза 1 тика (финалы операций и очередь команд), без движения машин: позиции до и после правки сравнимы.
    private static void RunPhaseOneOnly(Simulation sim) => CommandDispatcher.Run(sim.World);

    [Fact]
    public void Split_Validation_TooShort_NearNode_Busy()
    {
        using Simulation sim = ToolsFixture.Small(100);
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        int seg = BusiestSegment(sim, 90f);
        Assert.Equal(RejectReason.TooShort, ToolsFixture.Run(sim, Split(sim, seg, 3f, 1)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Split(sim, seg, float.NaN, 2)));
        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, Command.MakeSplitRoad(new SplitRoadCmd { Seg = new EntityRef(seg, 2), S = 40f }, 3)));
        int node = ToolsFixture.FindNode(sim, 4, NodeControl.Signal);
        if (node < 0)
        {
            node = ToolsFixture.FindNode(sim, 3, NodeControl.RightHandRule);
        }

        Assert.True(node >= 0);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Merge(sim, node, 4)));   // не степень 2
    }

    [Fact]
    public void Split_Undo_Redo_RoundTrip_WithTraffic()
    {
        using Simulation sim = ToolsFixture.Small(500);
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        World w = sim.World;
        NetworkStore n = w.Net;
        int seg = BusiestSegment(sim, 90f);
        int segs0 = n.AliveSegments;
        int nodes0 = n.AliveNodes;
        var stack = new UndoStack();
        UndoResult r = stack.Execute(sim, new[] { Split(sim, seg, w.Builder.FullAxisLength(seg) * 0.4f, 0) });
        Assert.True(r.Recorded && r.Rejected == 0);
        Assert.Equal(segs0 + 1, n.AliveSegments);
        sim.Step(100);
        Assert.True(stack.Undo(sim).Ok);
        Assert.Equal(segs0, n.AliveSegments);
        Assert.Equal(nodes0, n.AliveNodes);
        Assert.Null(w.VerifyDerived());
        sim.Step(100);
        Assert.True(stack.Redo(sim).Ok);
        Assert.Equal(segs0 + 1, n.AliveSegments);
        Assert.Null(w.VerifyDerived());
        sim.Step(100);
        Assert.True(stack.Undo(sim).Ok);
        Assert.Equal(segs0, n.AliveSegments);
        using Simulation twin = ToolsFixture.RoundTrip(sim);
        twin.Step(30);
        sim.Step(30);
        Assert.Equal(sim.ComputeStateHash(), twin.ComputeStateHash());
    }

    [Fact]
    public void Step_AfterSplitAndMerge_AllocatesNothing()
    {
        using Simulation sim = ToolsFixture.Small(300);
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        World w = sim.World;
        int seg = BusiestSegment(sim, 90f);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Split(sim, seg, w.Builder.FullAxisLength(seg) * 0.5f, 1)));
        sim.Step(200);
        long bytes = AllocationProbe.Measure(() => sim.Step(200));
        Assert.Equal(0, bytes);
    }
}
