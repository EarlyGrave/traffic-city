using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Scenarios.Tools;

namespace Sim.Core.Tests.Tools;

/// <summary>Undo/redo инструментов (4.3, T8; player-tools.md §8, §14 <c>UndoFuzz_Equivalent</c> TL7): обратные записи ядра, перенумерация, стек хоста.</summary>
public sealed class UndoTests
{
    // Мир без машин: обратные не упираются в Busy (перестройка узла ждёт опустения), эквивалентность проверяется строго.
    private static Simulation Quiet(int threads = 1)
    {
        ScenarioDesc d = ToolsFixture.SmallDesc(buses: false);
        d.Demand.TargetVehicles = 0;
        Simulation sim = Simulation.Create(d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        sim.Step(20);
        return sim;
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void UndoFuzz_Equivalent(int seed, bool settle)
    {
        Simulation sim = Quiet();
        var rng = new Random(seed);
        var stack = new UndoStack();
        string[] c0 = Canon(sim);

        int actions = 0;
        for (int i = 0; i < 60; i++)
        {
            var group = new List<Command>();
            int k = 1 + (rng.Next(4) == 0 ? rng.Next(3) : 0);
            for (int j = 0; j < k; j++)
            {
                Command c = EditFuzzTests.Pick(sim, rng, 0);
                if (c.Kind != 0)
                {
                    group.Add(c);
                }
            }

            if (group.Count > 0)
            {
                UndoResult r = stack.Execute(sim, group.ToArray());
                actions += r.Recorded ? 1 : 0;
            }

            sim.Step(rng.Next(0, 6));
        }

        Assert.True(actions > 10, "фаззер почти ничего не применил: " + actions);
        string[]? cEnd = null;
        if (settle)
        {
            Settle(sim);
            cEnd = Canon(sim);
        }

        int depth = stack.UndoCount;
        while (stack.UndoCount > 0)
        {
            UndoResult r = stack.Undo(sim);
            Assert.True(r.Rejected == 0, $"undo отклонён: {r.FirstReason} {r.FirstKind} (осталось {stack.UndoCount})");
            sim.Step(rng.Next(0, 3));
        }

        Settle(sim);
        Assert.Equal(c0, Canon(sim));
        Assert.Equal(depth, stack.RedoCount);

        if (settle)
        {
            while (stack.RedoCount > 0)
            {
                UndoResult r = stack.Redo(sim);
                Assert.True(r.Rejected == 0, $"redo отклонён: {r.FirstReason} {r.FirstKind} (осталось {stack.RedoCount})");
                sim.Step(rng.Next(0, 3));
            }

            Settle(sim);
            Assert.Equal(cEnd, Canon(sim));
        }

        Assert.Null(sim.World.VerifyDerived());
    }

    [Fact]
    public void Undo_BuildRoad_RemovesSegment_RedoRestores()
    {
        Simulation sim = Quiet();
        var stack = new UndoStack();
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 p = sim.World.Net.Nodes[west].Position;
        int before = sim.World.Net.AliveSegments;
        BuildRoadCmd road = ToolsFixture.Road(p, new Vec2(p.X - 120f, p.Z), 1);
        UndoResult r = stack.Execute(sim, new[] { Command.MakeBuildRoad(in road) });
        Assert.True(r.Ok && r.Recorded);
        Assert.Equal(before + 1, sim.World.Net.AliveSegments);

        Assert.True(stack.Undo(sim).Ok);
        Settle(sim);
        Assert.Equal(before, sim.World.Net.AliveSegments);
        Assert.Equal(1, stack.RedoCount);

        Assert.True(stack.Redo(sim).Ok);
        Settle(sim);
        Assert.Equal(before + 1, sim.World.Net.AliveSegments);
    }

    [Fact]
    public void Depth_IsLimited_NewActionClearsRedo()
    {
        Simulation sim = Quiet();
        var stack = new UndoStack(depth: 3);
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 p = sim.World.Net.Nodes[west].Position;
        for (int i = 0; i < 5; i++)
        {
            BuildRoadCmd road = ToolsFixture.Road(new Vec2(p.X - 400f, p.Z - (i * 60f)), new Vec2(p.X - 520f, p.Z - (i * 60f)), 1);
            Assert.True(stack.Execute(sim, new[] { Command.MakeBuildRoad(in road) }).Recorded);
        }

        Assert.Equal(3, stack.UndoCount);
        Assert.True(stack.Undo(sim).Ok);
        Assert.Equal(1, stack.RedoCount);
        BuildRoadCmd more = ToolsFixture.Road(new Vec2(p.X - 400f, p.Z + 300f), new Vec2(p.X - 520f, p.Z + 300f), 1);
        Assert.True(stack.Execute(sim, new[] { Command.MakeBuildRoad(in more) }).Recorded);
        Assert.Equal(0, stack.RedoCount);
    }

    [Fact]
    public void Remap_OlderEntries_FollowRecreatedSegment()
    {
        Simulation sim = Quiet();
        var stack = new UndoStack();
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 p = sim.World.Net.Nodes[west].Position;
        BuildRoadCmd road = ToolsFixture.Road(p, new Vec2(p.X - 120f, p.Z), 2);
        Assert.True(stack.Execute(sim, new[] { Command.MakeBuildRoad(in road) }).Ok);
        int seg = sim.World.Net.SegmentCount - 1;
        while (!sim.World.Net.SegmentAlive(seg))
        {
            seg--;
        }

        EntityRef segRef = ToolsFixture.Segment(sim, seg);
        var flags = new SetLaneFlagsCmd { Seg = segRef, Dir = 0, LaneNo = 0, Flags = LaneFlags.BusOnly, Mask = LaneFlags.BusOnly };
        Assert.True(stack.Execute(sim, new[] { Command.MakeSetLaneFlags(in flags) }).Ok);
        Assert.True(stack.Execute(sim, new[] { Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = segRef }) }).Ok);
        Settle(sim);
        Assert.False(sim.World.Net.SegmentAlive(seg));

        // Занять освободившийся слот чужим сегментом: восстановленный сегмент получит другой индекс.
        BuildRoadCmd other = ToolsFixture.Road(new Vec2(p.X - 600f, p.Z - 600f), new Vec2(p.X - 720f, p.Z - 600f), 1);
        Assert.True(stack.Execute(sim, new[] { Command.MakeBuildRoad(in other) }).Ok);
        Assert.True(sim.World.Net.SegmentAlive(seg), "слот должен быть занят другим сегментом");

        // Откат по стеку: чужой сегмент -> снос, возврат сегмента (новый слот), затем флаг полосы по переписанному handle.
        Assert.True(stack.Undo(sim).Ok);
        Settle(sim);
        UndoResult restored = stack.Undo(sim);
        Assert.True(restored.Ok, $"{restored.FirstReason}");
        Settle(sim);
        UndoResult lane = stack.Undo(sim);
        Assert.True(lane.Ok, $"флаг полосы по переписанному handle: {lane.FirstReason}");
        Settle(sim);
        Assert.Equal(1, CountSegmentsAt(sim, p, new Vec2(p.X - 120f, p.Z)));
    }

    [Fact]
    public void CommandRefs_Remap_Translates_ByKind_AndSkipsUnset()
    {
        var table = new[]
        {
            new RemapEntry { Kind = EntityKind.Segment, OldIndex = 5, OldGeneration = 3, NewIndex = 9, NewGeneration = 7 },
            new RemapEntry { Kind = EntityKind.Node, OldIndex = 2, OldGeneration = 0, NewIndex = 4, NewGeneration = 11 },
        };
        var t = new RemapTable(table);

        Command c = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = new EntityRef(5, 3) });
        CommandRefs.Remap(ref c, in t);
        Assert.Equal(new EntityRef(9, 7), c.SetLaneFlags.Seg);

        Command other = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = new EntityRef(5, 5) });   // иное поколение: не тронуто
        CommandRefs.Remap(ref other, in t);
        Assert.Equal(new EntityRef(5, 5), other.SetLaneFlags.Seg);

        Command nodeCmd = Command.MakeSetNodeControl(new SetNodeControlCmd { Node = 2, NodeGen = 1, Control = NodeControl.Stop });
        CommandRefs.Remap(ref nodeCmd, in t);
        Assert.Equal(4, nodeCmd.SetNodeControl.Node);
        Assert.Equal(11, nodeCmd.SetNodeControl.NodeGen);

        var b = new BuildRoadCmd { RestoreOf = new EntityRef(5, 3), RestoreNodeA = new EntityRef(2, 1), RestoreNodeB = EntityRef.None };
        Command build = Command.MakeBuildRoad(in b);
        CommandRefs.Remap(ref build, in t);
        Assert.Equal(new EntityRef(9, 7), build.BuildRoad.RestoreOf);
        Assert.Equal(new EntityRef(4, 11), build.BuildRoad.RestoreNodeA);
        Assert.Equal(EntityRef.None, build.BuildRoad.RestoreNodeB);

        Command unset = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = new EntityRef(5, 0) });   // без проверки поколения: не трогается
        CommandRefs.Remap(ref unset, in t);
        Assert.Equal(new EntityRef(5, 0), unset.SetLaneFlags.Seg);
    }

    [Fact]
    public void Inverses_AreClearedEachStep_AndCarryTag()
    {
        Simulation sim = Quiet();
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 p = sim.World.Net.Nodes[west].Position;
        BuildRoadCmd road = ToolsFixture.Road(p, new Vec2(p.X - 120f, p.Z), 1);
        Assert.True(sim.Enqueue(Command.MakeBuildRoad(in road, 77)));
        sim.Step(1);
        Assert.Equal(1, sim.Inverses.Length);
        Assert.Equal(77u, sim.Inverses[0].Tag);
        Assert.True(sim.Inverses[0].Count >= 1);
        Assert.Equal(CommandKind.RemoveRoad, sim.InverseCommands[sim.Inverses[0].First].Kind);
        sim.Step(1);
        Assert.Equal(0, sim.Inverses.Length);
    }

    private static int CountSegmentsAt(Simulation sim, Vec2 a, Vec2 b)
    {
        NetworkStore n = sim.World.Net;
        int c = 0;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            Vec2 pa = n.Nodes[n.Segments[s].NodeA].Position;
            Vec2 pb = n.Nodes[n.Segments[s].NodeB].Position;
            if ((Mm(pa.X) == Mm(a.X) && Mm(pa.Z) == Mm(a.Z) && Mm(pb.X) == Mm(b.X) && Mm(pb.Z) == Mm(b.Z)) || (Mm(pa.X) == Mm(b.X) && Mm(pa.Z) == Mm(b.Z) && Mm(pb.X) == Mm(a.X) && Mm(pb.Z) == Mm(a.Z)))
            {
                c++;
            }
        }

        return c;
    }

    private static void Settle(Simulation sim)
    {
        for (int i = 0; i < 6000 && sim.World.Ops.Count > 0; i++)
        {
            sim.Step(1);
        }

        Assert.Equal(0, sim.World.Ops.Count);
    }

    private static int Mm(float v) => (int)MathF.Round(v * 1000f);

    private static string PosKey(Simulation sim, int node)
    {
        Vec2 p = sim.World.Net.Nodes[node].Position;
        return Mm(p.X) + "," + Mm(p.Z);
    }

    // Каноническое описание сети: сегменты (концы в мм, полосы, скорость, класс, флаги полос) и узлы (режим, план, знаки и запреты рукавов, адресованные дальним концом) - без индексов.
    private static string[] Canon(Simulation sim)
    {
        World w = sim.World;
        NetworkStore n = w.Net;
        var list = new List<string>();
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            SegmentData sd = n.Segments[s];
            string lanes = string.Empty;
            for (int d = 0; d < 2; d++)
            {
                int cnt = d == 0 ? sd.LanesAB : sd.LanesBA;
                int first = d == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
                for (int q = 0; q < cnt; q++)
                {
                    lanes += n.Lanes[first + q].Flags & (LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut | LaneFlags.Closed);
                    lanes += ";";
                }

                lanes += "|";
            }

            list.Add($"S {PosKey(sim, sd.NodeA)}>{PosKey(sim, sd.NodeB)} {sd.LanesAB}/{sd.LanesBA} v{Mm(sd.SpeedLimit)} k{(int)sd.Kind} f{sd.Flags} {lanes}");
        }

        for (int i = 0; i < n.NodeCount; i++)
        {
            if (!n.NodeAlive(i) || n.Nodes[i].FirstArm < 0)
            {
                continue;
            }

            NodeData nd = n.Nodes[i];
            string plan = string.Empty;
            if (nd.Signal >= 0)
            {
                SetSignalPlanCmd p = w.Signals.ReadPlan(nd.Signal);
                plan = $"P{(int)p.Mode},{p.PhaseCount},{p.YellowTicks},{p.AllRedTicks},{p.Green0},{p.Green1},{p.Green2},{p.Green3}";
            }

            var arms = new string[nd.ArmCount];
            for (int a = 0; a < nd.ArmCount; a++)
            {
                SegmentData sd = n.Segments[n.NodeArms[nd.FirstArm + a]];
                int far = sd.NodeA == i ? sd.NodeB : sd.NodeA;
                arms[a] = PosKey(sim, far);
            }

            var parts = new List<string>();
            for (int a = 0; a < nd.ArmCount; a++)
            {
                int ov = w.Builder.ArmOverrideOf(i, a);
                if (ov != 0)
                {
                    parts.Add($"o{arms[a]}={ov}");
                }

                for (int b = 0; b < nd.ArmCount && a < 8; b++)
                {
                    if (b < 8 && (nd.TurnBan & (1UL << ((a * 8) + b))) != 0)
                    {
                        parts.Add($"b{arms[a]}>{arms[b]}");
                    }
                }
            }

            parts.Sort(StringComparer.Ordinal);
            list.Add($"N {PosKey(sim, i)} c{(int)nd.Control} {plan} {string.Join(' ', parts)}");
        }

        list.Sort(StringComparer.Ordinal);
        return list.ToArray();
    }
}
