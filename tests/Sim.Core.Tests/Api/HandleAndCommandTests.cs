using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Tests.Api;

public class HandleAndCommandTests
{
    [Fact]
    public void Null_IsDefault_AndIsNull()
    {
        Assert.True(VehicleHandle.Null.IsNull);
        Assert.Equal(default, VehicleHandle.Null);
        Assert.False(new VehicleHandle(0, 1).IsNull);
    }

    [Fact]
    public void Packed_RoundTrips()
    {
        var h = new LaneHandle(123456, 7);
        Assert.Equal(h, LaneHandle.FromPacked(h.Packed));
        var neg = new NodeHandle(-1, int.MaxValue);
        Assert.Equal(neg, NodeHandle.FromPacked(neg.Packed));
        Assert.Equal((7UL << 32) | 5UL, new ZoneHandle(5, 7).Packed);
    }

    [Fact]
    public void Equality_Operators_AndHash()
    {
        var a = new SegmentHandle(3, 5);
        var b = new SegmentHandle(3, 5);
        var c = new SegmentHandle(3, 7);
        Assert.True(a == b);
        Assert.True(a != c);
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals((object)c));
        Assert.False(a.Equals(null));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Default_Command_IsNone_AndZeroed()
    {
        var c = default(Command);
        Assert.Equal(CommandKind.None, c.Kind);
        Assert.Equal(0u, c.Tag);
        Assert.Equal(0, c.SpawnTrip.FromZone);
    }

    [Fact]
    public void Factory_BuildRoad_SetsKindTagAndPayload()
    {
        var p = new BuildRoadCmd { Ax = 1, Az = 2, Bx = 3, Bz = 4, SpeedLimit = 13.9f, LanesAB = 2, LanesBA = 1, Kind = RoadKind.Arterial, Flags = 1 };
        var c = Command.MakeBuildRoad(p, 99);
        Assert.Equal(CommandKind.BuildRoad, c.Kind);
        Assert.Equal(99u, c.Tag);
        Assert.Equal(4f, c.BuildRoad.Bz);
        Assert.Equal(RoadKind.Arterial, c.BuildRoad.Kind);
        Assert.Equal(2, c.BuildRoad.LanesAB);
        Assert.Equal(1, c.BuildRoad.Flags);
    }

    [Fact]
    public void Factory_SelectVehicle_UsesHandle()
    {
        var c = Command.MakeSelectVehicle(new VehicleHandle(42, 9), 5);
        Assert.Equal(CommandKind.SelectVehicle, c.Kind);
        Assert.Equal(42, c.SelectVehicle.Index);
        Assert.Equal(9, c.SelectVehicle.Generation);
        Assert.Equal(5u, c.Tag);
    }

    [Fact]
    public void Factories_SimplePayloads()
    {
        Assert.Equal(CommandKind.ClearSelection, Command.MakeClearSelection().Kind);
        Assert.Equal(1000, Command.MakeSetTargetVehicleCount(1000).SetTargetVehicleCount.Count);
        var n = Command.MakeSetNodeControl(7, NodeControl.Signal);
        Assert.Equal(CommandKind.SetNodeControl, n.Kind);
        Assert.Equal(7, n.SetNodeControl.Node);
        Assert.Equal(NodeControl.Signal, n.SetNodeControl.Control);
        Assert.Equal(3, Command.MakeSpawnBus(3).SpawnBus.Route);
        Assert.Equal(CommandKind.SpawnBus, Command.MakeSpawnBus(3).Kind);

        var t = Command.MakeSpawnTrip(new SpawnTripCmd { FromZone = 1, ToZone = 2, FromLane = -1, Count = 4 });
        Assert.Equal(CommandKind.SpawnTrip, t.Kind);
        Assert.Equal(4, t.SpawnTrip.Count);

        var s = Command.MakeSetSignalPlan(new SetSignalPlanCmd { Node = 2, Mode = SignalMode.Fixed, PhaseCount = 2, Green0 = 400, Green7 = 123, AllRedTicks = 20 });
        Assert.Equal(CommandKind.SetSignalPlan, s.Kind);
        Assert.Equal(123, s.SetSignalPlan.Green7);
        Assert.Equal(20, s.SetSignalPlan.AllRedTicks);

        var r = Command.MakeCreateBusRoute(new CreateBusRouteCmd { HeadwayTicks = 6000, VehicleType = 1 });
        Assert.Equal(CommandKind.CreateBusRoute, r.Kind);
        Assert.Equal(6000, r.CreateBusRoute.HeadwayTicks);

        var a = Command.MakeAppendBusStop(new AppendBusStopCmd { Route = 1, Lane = 2, S = 3.5f, Kind = BusStopKind.Bay });
        Assert.Equal(CommandKind.AppendBusStop, a.Kind);
        Assert.Equal(BusStopKind.Bay, a.AppendBusStop.Kind);
    }

    [Fact]
    public void SetSignalPlan_PayloadFitsLargestMember()
    {
        // Самая большая нагрузка (46 B) не должна затирать Kind/Tag.
        var c = Command.MakeSetSignalPlan(new SetSignalPlanCmd { YellowTicks = ushort.MaxValue, AllRedTicks = ushort.MaxValue }, 77);
        Assert.Equal(CommandKind.SetSignalPlan, c.Kind);
        Assert.Equal(77u, c.Tag);
    }
}
