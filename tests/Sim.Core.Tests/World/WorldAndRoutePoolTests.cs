using Sim.Core.Api;
using Sim.Core.Routing;

namespace Sim.Core.Tests.Worlds;

public class WorldAndRoutePoolTests
{
    [Fact]
    public void World_ExposesStoresSizedByCapacities()
    {
        ScenarioDesc d = TestScenarios.Line(9);
        Assert.Equal(CreateStatus.Ok, WorldBuilder.Create(in d, out World? w));
        Assert.NotNull(w);
        Assert.Equal(TestScenarios.MaxVehicles, w!.Vehicles.Capacity);
        Assert.Equal(d.Capacities.MaxLanes, w.Occupancy.MaxLanes);
        Assert.Equal(d.Capacities.MaxPendingTrips, w.Trips.Capacity);
        Assert.Equal(d.Capacities.CommandQueueCapacity, w.Commands.Capacity);
        Assert.Equal(d.Capacities.EventCapacity, w.Events.Length);
        Assert.Equal(d.Capacities.MaxSegments, w.SegmentLoads.Length);
        Assert.Equal(d.Capacities.MaxNodes, w.NodeThroughput.Length);
        Assert.Equal(d.Capacities.MaxLanes, w.LaneSignals.Length);
        Assert.Equal(2, w.Render.Length);
        Assert.Equal(TestScenarios.MaxVehicles, w.Render[0].Length);
        Assert.Equal(TestScenarios.MaxVehicles, w.Render[1].Length);
        Assert.Equal(d.Capacities.MaxRoutes, w.Routes.Pool.Capacity);
        Assert.Equal(0, w.Tick);
        Assert.Equal(9UL, w.Seed);
    }

    [Fact]
    public void World_RngStreamsAreIndependentAndSeeded()
    {
        ScenarioDesc d = TestScenarios.Line(9);
        WorldBuilder.Create(in d, out World? a);
        WorldBuilder.Create(in d, out World? b);
        Assert.Equal(a!.RngSpawn.NextUInt(), b!.RngSpawn.NextUInt());
        _ = a.RngSpawn.NextUInt(); // продвижение одного потока не двигает другие
        Assert.Equal(a.RngRouting.State, b.RngRouting.State);
        Assert.NotEqual(a.RngSpawn.State, a.RngRouting.State);
    }

    [Fact]
    public void World_RulesDefaultsFilled_WhenZero()
    {
        ScenarioDesc d = TestScenarios.Line();
        WorldBuilder.Create(in d, out World? w);
        Assert.Equal(0.5f, w!.Rules.MinPhysicalGap);
        Assert.Equal(1200, w.Rules.StuckThresholdTicks);
        Assert.Equal(10, w.Rules.MobilEveryTicks);

        d.Rules.MobilEveryTicks = 4;
        WorldBuilder.Create(in d, out w);
        Assert.Equal(4, w!.Rules.MobilEveryTicks);
    }

    [Fact]
    public void BuildNetwork_CityWithEmptyDesc_ReturnsInvalidNetwork()
    {
        ScenarioDesc d = TestScenarios.Line();
        WorldBuilder.Create(in d, out World? w);
        d.City.Enabled = true;
        Assert.Equal(CreateStatus.InvalidNetwork, WorldBuilder.BuildNetwork(w!, in d));
    }

    [Fact]
    public void Emit_RespectsCapacityAndReservesOverflowSlot()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Capacities.EventCapacity = 3;
        WorldBuilder.Create(in d, out World? w);
        w!.Emit(SimEventKind.NodeCreated, 1, 1);
        w.Emit(SimEventKind.NodeCreated, 2, 2);
        Assert.Equal(2, w.EventCount);
        w.Emit(SimEventKind.NodeCreated, 3, 3);
        w.Emit(SimEventKind.NodeCreated, 4, 4);
        Assert.Equal(3, w.EventCount);
        Assert.Equal(SimEventKind.EventsOverflow, w.Events[2].Kind);
        Assert.Equal(2, w.Counters.EventsDropped);
    }

    [Fact]
    public void RoutePool_AddGetFree()
    {
        var pool = new RoutePool(2, 5);
        RouteStep[] a = { new() { Lane = 1, Node = 2 }, new() { Lane = 3, Node = -1 } };
        RouteStep[] b = { new() { Lane = 7, Node = 8 }, new() { Lane = 9, Node = 10 }, new() { Lane = 11, Node = -1 } };

        Assert.True(pool.TryAdd(a, 0, 1, 4, out RouteHandle ha));
        Assert.True(pool.TryAdd(b, 1, 0, 4, out RouteHandle hb));
        Assert.Equal(5, pool.StepsUsed);
        Assert.Equal(2, pool.GetSteps(ha).Length);
        Assert.Equal(3, pool.GetSteps(hb).Length);
        Assert.Equal(9, pool.GetSteps(hb)[1].Lane);
        Assert.Equal(4, pool.NetworkVersion[hb.Index]);
        Assert.Equal(1, pool.FromZone[hb.Index]);

        Assert.False(pool.TryAdd(a, 0, 1, 4, out RouteHandle none)); // слоты кончились
        Assert.True(none.IsNull);

        Assert.True(pool.Free(ha));
        Assert.False(pool.Free(ha));
        Assert.True(pool.GetSteps(ha).IsEmpty);
        Assert.False(pool.TryAdd(a, 0, 1, 4, out _)); // слот есть, арена исчерпана
        Assert.Equal(1, pool.AliveCount);
    }
}
