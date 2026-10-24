using System;
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
    public void RouteStore_SlotsWindowsAndPlanArena()
    {
        var store = new RouteStore(maxRoutes: 3, maxPlanSteps: 40, windowSteps: 8, levels: 1); // запас блока = 8
        Assert.True(store.TryAllocate(out RouteHandle ha));
        Assert.True(store.TryAllocate(out RouteHandle hb));
        Assert.True(store.TryAllocate(out RouteHandle hc));
        Assert.False(store.TryAllocate(out RouteHandle none)); // слоты кончились
        Assert.True(none.IsNull);

        // Окно слота лежит по адресу slot * WindowSteps и не двигается.
        store.Window[(hb.Index * 8) + 1] = new RouteStep { Lane = 9, Node = 10 };
        store.WinCount[hb.Index] = 2;
        Assert.Equal(9, store.GetWindow(hb)[1].Lane);
        Assert.Equal(2, store.GetWindow(hb).Length);

        // План: блок = длина + запас; рост сверх запаса - переезд в хвост; арена кончилась - уплотнение; всё равно мало - false.
        int[] four = { 1, 2, 3, 4 };
        Assert.True(store.SetPlan(ha.Index, four));
        Assert.Equal(12, store.PlanUsed);
        Assert.True(store.GetPlan(ha.Index).SequenceEqual(four));
        int[] two = { 7, 8 };
        Assert.True(store.SetPlan(hb.Index, two));
        Assert.Equal(22, store.PlanUsed);
        var twelve = new int[12];
        for (int i = 0; i < twelve.Length; i++)
        {
            twelve[i] = 100 + i;
        }

        Assert.True(store.SetPlan(ha.Index, twelve)); // помещается в блок (12) - без переезда
        Assert.Equal(22, store.PlanUsed);
        var thirteen = new int[13];
        Assert.True(store.SetPlan(ha.Index, thirteen)); // переезд в хвост: 22 + 21 > 40, уплотнение освобождает старый блок
        Assert.Equal(1, store.Compactions);
        Assert.True(store.GetPlan(hb.Index).SequenceEqual(two)); // чужой блок цел после уплотнения
        Assert.False(store.SetPlan(hc.Index, new int[40])); // больше арены
        Assert.Equal(0, store.PlanCount[hc.Index]);

        Assert.True(store.Free(ha));
        Assert.False(store.Free(ha));
        Assert.True(store.GetWindow(ha).IsEmpty);
        Assert.Equal(2, store.AliveCount);
    }
}
