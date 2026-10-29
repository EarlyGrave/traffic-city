using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.Api;

/// <summary>Типы инструментов игрока (4.3, T1, player-tools.md §1-2, §6): размеры, номера, заглушки диспетчера, перенумерация.</summary>
public class ToolPayloadSizesTests
{
    private static readonly string[] _commandFields =
    {
        nameof(Command.BuildRoad), nameof(Command.SpawnTrip), nameof(Command.SetSignalPlan), nameof(Command.CreateBusRoute),
        nameof(Command.AppendBusStop), nameof(Command.SpawnBus), nameof(Command.SelectVehicle), nameof(Command.SetTargetVehicleCount),
        nameof(Command.SetNodeControl), nameof(Command.SetPedTarget), nameof(Command.PaintZone), nameof(Command.SetTripExecution),
        nameof(Command.RemoveRoad), nameof(Command.UpgradeRoad), nameof(Command.SplitRoad), nameof(Command.SetArmControl),
        nameof(Command.SetTurnBan), nameof(Command.SetLaneFlags), nameof(Command.InsertBusStop), nameof(Command.RemoveBusStop),
        nameof(Command.RemoveBusRoute), nameof(Command.SetBusRouteParams), nameof(Command.MergeAtNode),
    };

    public static TheoryData<Type, int> ToolSizes => new()
    {
        { typeof(EntityRef), 8 },
        { typeof(BuildRoadCmd), 56 },
        { typeof(RemoveRoadCmd), 12 },
        { typeof(UpgradeRoadCmd), 20 },
        { typeof(SplitRoadCmd), 36 },
        { typeof(MergeAtNodeCmd), 16 },
        { typeof(SetArmControlCmd), 12 },
        { typeof(SetTurnBanCmd), 12 },
        { typeof(SetLaneFlagsCmd), 12 },
        { typeof(InsertBusStopCmd), 36 },
        { typeof(RemoveBusStopCmd), 8 },
        { typeof(RemoveBusRouteCmd), 8 },
        { typeof(SetBusRouteParamsCmd), 16 },
        { typeof(SetSignalPlanCmd), 50 },
        { typeof(SetNodeControlCmd), 12 },
        { typeof(CreateBusRouteCmd), 20 },
        { typeof(PreviewIssue), 24 },
        { typeof(InverseRecord), 16 },
        { typeof(RemapEntry), 20 },
        { typeof(ToolOpView), 28 },
    };

    [Theory]
    [MemberData(nameof(ToolSizes))]
    public void ToolPayloadSizes_MatchSpec(Type type, int size)
    {
        Assert.Equal(size, SizeOfUnsafe(type));
        Assert.Equal(size, Marshal.SizeOf(type));
    }

    [Fact]
    public void Command_Is64Bytes_AndEveryPayloadFits56AtOffset8()
    {
        Assert.Equal(64, Unsafe.SizeOf<Command>());
        Assert.Equal(64, Marshal.SizeOf<Command>());
        foreach (string f in _commandFields)
        {
            Assert.Equal(8, (int)Marshal.OffsetOf<Command>(f));
            FieldInfo fi = typeof(Command).GetField(f)!;
            Assert.True(SizeOfUnsafe(fi.FieldType) <= 56, f);
            Assert.Equal(SizeOfUnsafe(fi.FieldType), Marshal.SizeOf(fi.FieldType));
        }
    }

    [Fact]
    public void BuildRoadCmd_FieldOffsets()
    {
        Assert.Equal(20, (int)Marshal.OffsetOf<BuildRoadCmd>(nameof(BuildRoadCmd.LanesAB)));
        Assert.Equal(23, (int)Marshal.OffsetOf<BuildRoadCmd>(nameof(BuildRoadCmd.Flags)));
        Assert.Equal(24, (int)Marshal.OffsetOf<BuildRoadCmd>(nameof(BuildRoadCmd.Cx)));
        Assert.Equal(28, (int)Marshal.OffsetOf<BuildRoadCmd>(nameof(BuildRoadCmd.Profile)));   // RP7: делит байты с Cz
        Assert.Equal(32, (int)Marshal.OffsetOf<BuildRoadCmd>(nameof(BuildRoadCmd.RestoreOf)));
        Assert.Equal(40, (int)Marshal.OffsetOf<BuildRoadCmd>(nameof(BuildRoadCmd.RestoreNodeA)));
        Assert.Equal(48, (int)Marshal.OffsetOf<BuildRoadCmd>(nameof(BuildRoadCmd.RestoreNodeB)));
        Assert.Equal(46, (int)Marshal.OffsetOf<SetSignalPlanCmd>(nameof(SetSignalPlanCmd.NodeGen)));
        Assert.Equal(8, (int)Marshal.OffsetOf<SetNodeControlCmd>(nameof(SetNodeControlCmd.NodeGen)));
        Assert.Equal(12, (int)Marshal.OffsetOf<CreateBusRouteCmd>(nameof(CreateBusRouteCmd.RestoreOf)));
    }

    [Fact]
    public void CommandKind_Numbers_AreStable()
    {
        Assert.Equal(12, (int)CommandKind.SetPedTarget);
        Assert.Equal(23, (int)CommandKind.PaintZone);
        Assert.Equal(24, (int)CommandKind.SetTripExecution);
        Assert.Equal(25, (int)CommandKind.RemoveRoad);
        Assert.Equal(26, (int)CommandKind.UpgradeRoad);
        Assert.Equal(27, (int)CommandKind.SplitRoad);
        Assert.Equal(28, (int)CommandKind.SetArmControl);
        Assert.Equal(29, (int)CommandKind.SetTurnBan);
        Assert.Equal(30, (int)CommandKind.SetLaneFlags);
        Assert.Equal(31, (int)CommandKind.InsertBusStop);
        Assert.Equal(32, (int)CommandKind.RemoveBusStop);
        Assert.Equal(33, (int)CommandKind.RemoveBusRoute);
        Assert.Equal(34, (int)CommandKind.SetBusRouteParams);
        Assert.Equal(35, (int)CommandKind.MergeAtNode);
    }

    [Fact]
    public void RejectReasonAndEventNumbers_AreStable()
    {
        Assert.Equal(6, (int)RejectReason.TooShort);
        Assert.Equal(11, (int)RejectReason.CreatesTrap);
        Assert.Equal(15, (int)RejectReason.OutOfBounds);
        Assert.Equal(40, (int)SimEventKind.SegmentClosing);
        Assert.Equal(46, (int)SimEventKind.DrainStalled);
        Assert.Equal(53, (int)SimEventKind.BusRouteRemapped);
        Assert.Equal(100, (int)IssueCode.DeadEnd);
    }

    [Fact]
    public void Factories_SetKindTagAndPayload()
    {
        var seg = new EntityRef(3, 2);
        Command c = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = seg, Flags = 1 }, 9);
        Assert.Equal(CommandKind.RemoveRoad, c.Kind);
        Assert.Equal(9u, c.Tag);
        Assert.Equal(3, c.RemoveRoad.Seg.Index);
        Assert.Equal(1, c.RemoveRoad.Flags);
        Assert.Equal(CommandKind.UpgradeRoad, Command.MakeUpgradeRoad(default).Kind);
        Assert.Equal(CommandKind.SplitRoad, Command.MakeSplitRoad(default).Kind);
        Assert.Equal(CommandKind.SetArmControl, Command.MakeSetArmControl(default).Kind);
        Assert.Equal(CommandKind.SetTurnBan, Command.MakeSetTurnBan(default).Kind);
        Assert.Equal(CommandKind.SetLaneFlags, Command.MakeSetLaneFlags(default).Kind);
        Assert.Equal(CommandKind.InsertBusStop, Command.MakeInsertBusStop(default).Kind);
        Assert.Equal(CommandKind.RemoveBusStop, Command.MakeRemoveBusStop(default).Kind);
        Assert.Equal(CommandKind.RemoveBusRoute, Command.MakeRemoveBusRoute(default).Kind);
        Assert.Equal(CommandKind.SetBusRouteParams, Command.MakeSetBusRouteParams(default).Kind);
        Assert.Equal(CommandKind.MergeAtNode, Command.MakeMergeAtNode(default).Kind);
    }

    [Fact]
    public void ZeroInitializedRestoreRefs_AreUnset()
    {
        var b = new BuildRoadCmd { Ax = 1 };
        Assert.True(b.RestoreOf.IsUnset);
        Assert.True(b.RestoreNodeA.IsUnset);
        Assert.True(EntityRef.None.IsUnset);
        Assert.Equal(-1, EntityRef.None.Index);
        Assert.False(new EntityRef(0, 1).IsUnset);
    }

    [Fact]
    public void NotYetImplementedToolCommands_AreNotSupported_AndStateIsUntouched()
    {
        using Simulation sim = Simulation.Create(WorldSaveFixtures.Small(), new SimRuntimeOptions());
        sim.Step(5);
        var seg = new EntityRef(0, 1);
        Command[] cmds =
        {
            Command.MakeInsertBusStop(new InsertBusStopCmd { Route = seg, Seg = seg, Position = -1 }, 1),
            Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = seg }, 2),
            Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = seg }, 3),
            Command.MakeSetBusRouteParams(new SetBusRouteParamsCmd { Route = seg, VehicleType = -1 }, 4),
        };
        foreach (Command c in cmds)
        {
            Assert.True(sim.Enqueue(c));
        }

        sim.Step(1);
        int rejected = 0;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.CommandRejected && e.Tag >= 1 && e.Tag <= cmds.Length)
            {
                rejected++;
                Assert.Equal((int)RejectReason.NotSupported, e.A);
                Assert.Equal((int)cmds[e.Tag - 1].Kind, e.B);
            }
        }

        Assert.Equal(cmds.Length, rejected);

        using Simulation twin = Simulation.Create(WorldSaveFixtures.Small(), new SimRuntimeOptions());
        twin.Step(5);
        for (int i = 0; i < cmds.Length; i++)
        {
            Assert.True(twin.Enqueue(new Command { Kind = (CommandKind)99, Tag = (uint)(i + 1) }));   // тот же счёт отказов, но заведомо без эффекта
        }

        twin.Step(1);
        Assert.Equal(twin.ComputeStateHash(), sim.ComputeStateHash());
    }

    [Theory]
    [InlineData(BuildRoadFlags.Curve)]   // T5: AutoSplit реализован (SplitMergeTests), кривые - ещё нет
    public void BuildRoad_NewFlags_AreNotSupported(byte flags)
    {
        using Simulation sim = Simulation.Create(WorldSaveFixtures.Small(), new SimRuntimeOptions());
        var p = new BuildRoadCmd { Ax = 9000, Az = 9000, Bx = 9200, Bz = 9000, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Flags = flags };
        Assert.True(sim.Enqueue(Command.MakeBuildRoad(p, 5)));
        sim.Step(1);
        bool seen = false;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.CommandRejected && e.Tag == 5)
            {
                seen = true;
                Assert.Equal((int)RejectReason.NotSupported, e.A);
            }
        }

        Assert.True(seen);
    }

    [Fact]
    public void Remap_TranslatesRefs_SkipsGenerationZero()
    {
        RemapEntry[] entries =
        {
            new() { Kind = EntityKind.Segment, OldIndex = 3, OldGeneration = 2, NewIndex = 10, NewGeneration = 1 },
            new() { Kind = EntityKind.Node, OldIndex = 3, OldGeneration = 0, NewIndex = 20, NewGeneration = 4 },
        };
        var table = new RemapTable(entries);

        Command c = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(3, 2) });
        CommandRefs.Remap(ref c, in table);
        Assert.Equal(10, c.RemoveRoad.Seg.Index);
        Assert.Equal(1, c.RemoveRoad.Seg.Generation);

        Command other = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(3, 5) });   // чужое поколение
        CommandRefs.Remap(ref other, in table);
        Assert.Equal(3, other.RemoveRoad.Seg.Index);

        Command raw = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(3, 0) });   // без проверки - не трогаем
        CommandRefs.Remap(ref raw, in table);
        Assert.Equal(3, raw.RemoveRoad.Seg.Index);

        Command build = Command.MakeBuildRoad(new BuildRoadCmd { RestoreOf = new EntityRef(3, 2), RestoreNodeA = new EntityRef(3, 7) });
        CommandRefs.Remap(ref build, in table);
        Assert.Equal(10, build.BuildRoad.RestoreOf.Index);
        Assert.Equal(20, build.BuildRoad.RestoreNodeA.Index);
        Assert.Equal(4, build.BuildRoad.RestoreNodeA.Generation);
        Assert.True(build.BuildRoad.RestoreNodeB.IsUnset);

        Command nc = Command.MakeSetNodeControl(3, NodeControl.Yield);
        nc.SetNodeControl.NodeGen = 1;
        CommandRefs.Remap(ref nc, in table);
        Assert.Equal(20, nc.SetNodeControl.Node);
        Assert.Equal(4, nc.SetNodeControl.NodeGen);
    }

    private static int SizeOfUnsafe(Type t) =>
        (int)typeof(ToolPayloadSizesTests).GetMethod(nameof(SizeOfGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(t).Invoke(null, null)!;

    private static int SizeOfGeneric<T>() => Unsafe.SizeOf<T>();
}
