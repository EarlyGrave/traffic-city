using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Api;

public class SimulationTests
{
    private static Simulation Make(ulong seed = 1)
    {
        ScenarioDesc d = TestScenarios.Line(seed);
        return Simulation.Create(in d);
    }

    // ---- Create ----

    [Fact]
    public void Create_SmallExplicitNetwork_Ok()
    {
        ScenarioDesc d = TestScenarios.Line();
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? sim));
        Assert.NotNull(sim);
        NetworkView net = sim!.Network;
        Assert.Equal(3, net.Nodes.Length);
        Assert.Equal(2, net.Segments.Length);
        Assert.True(net.Lanes.Length >= 4);
        Assert.Equal(2, net.Zones.Length);
        Assert.Equal(0, sim.Tick);
        Assert.Equal(0, sim.SnapshotTick);
        Assert.Equal(TestScenarios.MaxVehicles, sim.Vehicles.Length);
        Assert.Equal(TestScenarios.MaxVehicles, sim.PreviousVehicles.Length);
        Assert.Equal(0, sim.ActiveVehicleCount);
        Assert.Equal(3, sim.Counters.NodeCount);
        Assert.Equal(2, sim.Counters.ZoneCount);
        Assert.Equal(sim.Network.Lanes.Length, sim.Counters.LaneCount);
    }

    [Fact]
    public void Create_TwoNodesOnly_Ok()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Network.Nodes = new[] { d.Network.Nodes[0], d.Network.Nodes[1] };
        d.Network.Segments = new[] { d.Network.Segments[0] };
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? sim));
        Assert.Equal(2, sim!.Network.Nodes.Length);
    }

    [Fact]
    public void Create_Throws_OnInvalid_TryCreate_DoesNot()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Capacities.MaxVehicles = 0;
        Assert.Throws<ArgumentException>(() => Simulation.Create(in d));
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out Simulation? sim));
        Assert.Null(sim);
    }

    public static TheoryData<int> BadCapacities() => new() { 0, 1, 2, 3, 4, 5, 6, 7 };

    [Theory]
    [MemberData(nameof(BadCapacities))]
    public void Create_InvalidCapacity_ReturnsStatus(int which)
    {
        ScenarioDesc d = TestScenarios.Line();
        switch (which)
        {
            case 0: d.Capacities.MaxVehicles = -5; break;
            case 1: d.Capacities.MaxNodes = 0; break;
            case 2: d.Capacities.MaxSegments = 0; break;
            case 3: d.Capacities.MaxLanes = int.MaxValue; break;
            case 4: d.Capacities.CommandQueueCapacity = 0; break;
            case 5: d.Capacities.EventCapacity = 0; break;
            case 6: d.Capacities.MaxPendingTrips = 0; break;
            default: d.Capacities.MaxZones = -1; break;
        }

        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out Simulation? sim));
        Assert.Null(sim);
    }

    [Fact]
    public void Create_NetworkDoesNotFit_NetworkTooLarge()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Capacities.MaxNodes = 2;
        Assert.Equal(CreateStatus.NetworkTooLarge, Simulation.TryCreate(in d, out _));

        d = TestScenarios.Line();
        d.Capacities.MaxLanes = 3;
        Assert.Equal(CreateStatus.NetworkTooLarge, Simulation.TryCreate(in d, out _));
    }

    [Fact]
    public void Create_BadNetwork_InvalidNetwork()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Network.Segments[0].B = 9;
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in d, out _));

        // Узлы ближе радиуса прилипания слиплись бы и сдвинули индексы.
        d = TestScenarios.Line();
        d.Network.Nodes[1] = new NodeDesc { X = 1, Z = 0 };
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in d, out _));

        // Слишком короткий сегмент (< 2 * отступ).
        d = TestScenarios.Line();
        d.Network.Nodes[1] = new NodeDesc { X = 10, Z = 0 };
        d.Network.Nodes[2] = new NodeDesc { X = 400, Z = 0 };
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in d, out _));
    }

    [Fact]
    public void Create_BadVehicleTypes_InvalidVehicleType()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.VehicleTypes = Array.Empty<VehicleTypeDesc>();
        Assert.Equal(CreateStatus.InvalidVehicleType, Simulation.TryCreate(in d, out _));

        d = TestScenarios.Line();
        d.VehicleTypes = null!;
        Assert.Equal(CreateStatus.InvalidVehicleType, Simulation.TryCreate(in d, out _));

        d = TestScenarios.Line();
        d.VehicleTypes[0].Length = 0f;
        Assert.Equal(CreateStatus.InvalidVehicleType, Simulation.TryCreate(in d, out _));

        d = TestScenarios.Line();
        d.VehicleTypes[0].Accel = float.NaN;
        Assert.Equal(CreateStatus.InvalidVehicleType, Simulation.TryCreate(in d, out _));
    }

    [Fact]
    public void Create_BadDemandAndTransit()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Demand.OdPairs = new[] { new OdPairDesc { FromZone = 0, ToZone = 5, Weight = 1 } };
        Assert.Equal(CreateStatus.InvalidDemand, Simulation.TryCreate(in d, out _));

        d = TestScenarios.Line();
        d.Demand.TargetVehicles = -1;
        Assert.Equal(CreateStatus.InvalidDemand, Simulation.TryCreate(in d, out _));

        d = TestScenarios.Line();
        d.Transit.Routes = 1;
        d.Transit.BusType = 3;
        Assert.Equal(CreateStatus.InvalidTransit, Simulation.TryCreate(in d, out _));
    }

    [Fact]
    public void Create_CityWithEmptyDesc_InvalidNetwork()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.City.Enabled = true;
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in d, out Simulation? sim));
        Assert.Null(sim);
    }

    [Fact]
    public void Create_CopiesDesc_LaterMutationDoesNotLeak()
    {
        ScenarioDesc d = TestScenarios.Line();
        using Simulation sim = Simulation.Create(in d);
        d.VehicleTypes[0].Length = 99f;
        Assert.Equal(4.5f, sim.World.Types[0].Length);
    }

    // ---- Step ----

    [Fact]
    public void Step_1000TicksWithoutVehicles()
    {
        using Simulation sim = Make();
        sim.Step(1000);
        Assert.Equal(1000, sim.Tick);
        Assert.Equal(1000, sim.SnapshotTick);
        Assert.Equal(1000, sim.Counters.Ticks);
        Assert.Equal(0, sim.ActiveVehicleCount);
        Assert.Equal(0, sim.Events.Length);
        sim.Step(0);
        sim.Step(-3);
        Assert.Equal(1000, sim.Tick);
    }

    // ---- команды ----

    [Fact]
    public void Commands_AreFifo_AndAppliedAtStartOfNextStep()
    {
        using Simulation sim = Make();
        Assert.True(sim.Enqueue(Command.MakeSetTargetVehicleCount(5, tag: 11)));
        Assert.True(sim.Enqueue(Command.MakeSetTargetVehicleCount(7, tag: 22)));
        Assert.Equal(2, sim.PendingCommands);
        Assert.Equal(0, sim.World.TargetVehicles); // ещё не применены

        sim.Step(3);

        Assert.Equal(0, sim.PendingCommands);
        Assert.Equal(7, sim.World.TargetVehicles); // порядок: сначала 5, потом 7
        ReadOnlySpan<SimEvent> ev = sim.Events;
        Assert.Equal(2, ev.Length);
        Assert.Equal(SimEventKind.CommandApplied, ev[0].Kind);
        Assert.Equal(11u, ev[0].Tag);
        Assert.Equal(22u, ev[1].Tag);
        Assert.Equal(0, ev[0].Tick); // на первом из трёх тиков
        Assert.Equal(2, sim.Counters.CommandsApplied);

        sim.Step(1); // события предыдущего Step очищены
        Assert.Equal(0, sim.Events.Length);
    }

    [Fact]
    public void Enqueue_Overflow_ReturnsFalse_CountsDropped()
    {
        using Simulation sim = Make(); // CommandQueueCapacity = 8
        for (int i = 0; i < 8; i++)
        {
            Assert.True(sim.Enqueue(Command.MakeClearSelection((uint)i)));
        }

        Assert.False(sim.Enqueue(Command.MakeClearSelection(99)));
        Assert.False(sim.Enqueue(Command.MakeClearSelection(100)));
        Assert.Equal(2, sim.Counters.CommandsDropped);
        Assert.Equal(8, sim.PendingCommands);

        sim.Step(1);
        Assert.Equal(0, sim.PendingCommands);
        Assert.Equal(8, sim.Events.Length);
        Assert.True(sim.Enqueue(Command.MakeClearSelection())); // снова есть место
    }

    [Fact]
    public void SpawnTrip_GoesToPendingTrips()
    {
        using Simulation sim = Make();
        var t = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 0, Count = 3 };
        sim.Enqueue(Command.MakeSpawnTrip(in t, tag: 5));
        sim.Step(1);

        Assert.Equal(3, sim.World.Trips.Count);
        Assert.True(sim.World.Trips.TryPeek(out PendingTrip trip));
        Assert.Equal(0, trip.FromZone);
        Assert.Equal(1, trip.ToZone);
        Assert.Equal(-1, trip.FromLane);
        Assert.Equal(SimEventKind.CommandApplied, sim.Events[0].Kind);
        Assert.Equal(5u, sim.Events[0].Tag);
    }

    [Fact]
    public void SpawnTrip_FromLane_Validated()
    {
        using Simulation sim = Make();
        var ok = new SpawnTripCmd { FromZone = -1, ToZone = 1, FromLane = 0, FromS = 10f, VehicleType = 0, Count = 1 };
        var badLane = new SpawnTripCmd { FromZone = -1, ToZone = 1, FromLane = 9999, VehicleType = 0, Count = 1 };
        var badS = new SpawnTripCmd { FromZone = -1, ToZone = 1, FromLane = 0, FromS = 1e6f, VehicleType = 0, Count = 1 };
        var badType = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 4, Count = 1 };
        var badCount = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 0, Count = 0 };
        sim.Enqueue(Command.MakeSpawnTrip(in ok));
        sim.Enqueue(Command.MakeSpawnTrip(in badLane));
        sim.Enqueue(Command.MakeSpawnTrip(in badS));
        sim.Enqueue(Command.MakeSpawnTrip(in badType));
        sim.Enqueue(Command.MakeSpawnTrip(in badCount));
        sim.Step(1);

        Assert.Equal(1, sim.World.Trips.Count + sim.World.Vehicles.AliveCount); // валидная поездка с полосы уже могла стартовать (фаза 2)
        ReadOnlySpan<SimEvent> ev = sim.Events;
        Assert.Equal(SimEventKind.CommandApplied, ev[0].Kind);
        for (int i = 1; i < 5; i++)
        {
            Assert.Equal(SimEventKind.CommandRejected, ev[i].Kind);
            Assert.Equal((int)RejectReason.InvalidArgument, ev[i].A);
        }

        Assert.Equal(4, sim.Counters.CommandsRejected);
    }

    [Fact]
    public void SpawnTrip_QueueFull_DropsAndCounts()
    {
        using Simulation sim = Make(); // MaxPendingTrips = 16
        var t = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 0, Count = 20 };
        sim.Enqueue(Command.MakeSpawnTrip(in t));
        sim.Step(1);
        Assert.Equal(16, sim.World.Trips.Count);
        Assert.Equal(4, sim.Counters.TripsDropped);
        Assert.Contains(sim.Events.ToArray(), e => e.Kind == SimEventKind.CapacityExhausted && e.A == (int)CapacityKind.PendingTrips && e.B == 4);

        sim.Enqueue(Command.MakeSpawnTrip(in t, tag: 9)); // очередь полна целиком
        sim.Step(1);
        Assert.Equal(24, sim.Counters.TripsDropped);
        Assert.Equal(SimEventKind.CapacityExhausted, sim.Events[0].Kind);
        Assert.Equal(SimEventKind.CommandRejected, sim.Events[1].Kind);
        Assert.Equal((int)RejectReason.Capacity, sim.Events[1].A);
    }

    [Fact]
    public void UnsupportedAndUnknownCommands_AreRejected()
    {
        using Simulation sim = Make();
        sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Stop, tag: 1));
        sim.Enqueue(Command.MakeSpawnBus(0, tag: 2));
        var unknown = default(Command);
        unknown.Kind = (CommandKind)99;
        unknown.Tag = 3;
        sim.Enqueue(in unknown);
        sim.Enqueue(Command.MakeSetTargetVehicleCount(-4, tag: 4));
        sim.Step(1);

        ReadOnlySpan<SimEvent> ev = sim.Events;
        Assert.Equal(4, ev.Length);
        Assert.Equal((int)RejectReason.NotSupported, ev[0].A);
        Assert.Equal((int)RejectReason.NotSupported, ev[1].A);
        Assert.Equal((int)RejectReason.UnknownKind, ev[2].A);
        Assert.Equal((int)RejectReason.InvalidArgument, ev[3].A);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(SimEventKind.CommandRejected, ev[i].Kind);
            Assert.Equal((uint)(i + 1), ev[i].Tag);
        }
    }

    [Fact]
    public void SelectVehicle_ClearSelection_AndInvalidHandle()
    {
        using Simulation sim = Make();
        VehicleHandle a = TestScenarios.PlaceCar(sim, 0, 50f);
        VehicleHandle b = TestScenarios.PlaceCar(sim, 0, 20f);

        sim.Enqueue(Command.MakeSelectVehicle(a));
        sim.Step(1);
        Assert.Equal(a, sim.World.Selected);
        Assert.NotEqual(0, sim.Vehicles[a.Index].Flags & (byte)VehicleRenderFlags.Selected);

        sim.Enqueue(Command.MakeSelectVehicle(b));
        sim.Step(1);
        Assert.Equal(b, sim.World.Selected);
        Assert.Equal(0, sim.Vehicles[a.Index].Flags & (byte)VehicleRenderFlags.Selected);
        Assert.NotEqual(0, sim.Vehicles[b.Index].Flags & (byte)VehicleRenderFlags.Selected);

        sim.Enqueue(Command.MakeSelectVehicle(new VehicleHandle(a.Index, a.Generation + 2), tag: 8));
        sim.Enqueue(Command.MakeSelectVehicle(VehicleHandle.Null, tag: 9));
        sim.Step(1);
        Assert.Equal(b, sim.World.Selected); // не сбилось
        Assert.Equal(2, sim.Counters.InvalidHandleOps);
        Assert.Equal((int)RejectReason.InvalidHandle, sim.Events[0].A);
        Assert.True(sim.SelectedRoute.IsEmpty); // маршрута пока нет

        sim.Enqueue(Command.MakeClearSelection());
        sim.Step(1);
        Assert.True(sim.World.Selected.IsNull);
        Assert.Equal(0, sim.Vehicles[b.Index].Flags & (byte)VehicleRenderFlags.Selected);
    }

    [Fact]
    public void SelectedRoute_ExposesRoutePoolSlice()
    {
        using Simulation sim = Make();
        VehicleHandle h = TestScenarios.PlaceCar(sim, 0, 10f);
        RouteStep[] steps = { new() { Lane = 0, Node = 1 }, new() { Lane = 2, Node = -1 } };
        Assert.True(sim.World.Routes.TryAdd(steps, 0, 1, sim.Network.Version, out RouteHandle rh));
        sim.World.Vehicles.Route[h.Index] = rh.Index;
        sim.World.Vehicles.RouteGen[h.Index] = rh.Generation;
        sim.World.Vehicles.RouteCursor[h.Index] = 1;

        sim.Enqueue(Command.MakeSelectVehicle(h));
        sim.Step(1);
        RouteView rv = sim.SelectedRoute;
        Assert.Equal(h, rv.Vehicle);
        Assert.Equal(2, rv.Steps.Length);
        Assert.Equal(1, rv.Cursor);
        Assert.Equal(2, rv.Steps[1].Lane);
    }

    [Fact]
    public void BuildRoad_NewIsolatedRoad_CreatesNodesSegmentAndEvents()
    {
        using Simulation sim = Make();
        int version = sim.Network.Version;
        var r = new BuildRoadCmd { Ax = 0, Az = 500, Bx = 150, Bz = 500, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        sim.Enqueue(Command.MakeBuildRoad(in r, tag: 4));
        sim.Step(1);

        ReadOnlySpan<SimEvent> ev = sim.Events;
        Assert.Equal(SimEventKind.NodeCreated, ev[0].Kind);
        Assert.Equal(3, ev[0].A);
        Assert.Equal(SimEventKind.NodeCreated, ev[1].Kind);
        Assert.Equal(SimEventKind.SegmentCreated, ev[2].Kind);
        Assert.Equal(2, ev[2].A);
        Assert.Equal(SimEventKind.CommandApplied, ev[3].Kind);
        Assert.Equal(4u, ev[3].Tag);
        Assert.Equal(5, sim.Network.Nodes.Length);
        Assert.Equal(3, sim.Network.Segments.Length);
        Assert.True(sim.Network.Version > version);
        Assert.Equal(5, sim.Counters.NodeCount);
        Assert.True(sim.Network.Nodes[3].FirstArm >= 0); // connector'ы построены
    }

    [Fact]
    public void BuildRoad_Invalid_Rejected_AndBuiltNodeNotSupported()
    {
        using Simulation sim = Make();
        var zeroLanes = new BuildRoadCmd { Ax = 0, Az = 500, Bx = 150, Bz = 500, SpeedLimit = 13.9f };
        var nan = new BuildRoadCmd { Ax = float.NaN, Az = 500, Bx = 150, Bz = 500, SpeedLimit = 13.9f, LanesAB = 1 };
        var tooShort = new BuildRoadCmd { Ax = 0, Az = 500, Bx = 5, Bz = 500, SpeedLimit = 13.9f, LanesAB = 1 };
        var toBuiltNode = new BuildRoadCmd { Ax = 200, Az = 0, Bx = 200, Bz = 300, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1 };
        sim.Enqueue(Command.MakeBuildRoad(in zeroLanes));
        sim.Enqueue(Command.MakeBuildRoad(in nan));
        sim.Enqueue(Command.MakeBuildRoad(in tooShort));
        sim.Enqueue(Command.MakeBuildRoad(in toBuiltNode));
        sim.Step(1);

        int rejected = 0;
        foreach (SimEvent e in sim.Events.ToArray())
        {
            if (e.Kind == SimEventKind.CommandRejected)
            {
                rejected++;
            }
        }

        Assert.Equal(4, rejected);
        Assert.Equal(2, sim.Network.Segments.Length);
    }

    [Fact]
    public void Events_Overflow_LastIsOverflowMarker()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Capacities.EventCapacity = 4;
        d.Capacities.CommandQueueCapacity = 16;
        using Simulation sim = Simulation.Create(in d);
        for (int i = 0; i < 10; i++)
        {
            sim.Enqueue(Command.MakeClearSelection((uint)i));
        }

        sim.Step(1);
        ReadOnlySpan<SimEvent> ev = sim.Events;
        Assert.Equal(4, ev.Length);
        Assert.Equal(SimEventKind.EventsOverflow, ev[3].Kind);
        Assert.Equal(SimEventKind.CommandApplied, ev[2].Kind);
        Assert.Equal(7, sim.Counters.EventsDropped);
        Assert.Equal(10, sim.Counters.CommandsApplied);
    }

    // ---- снапшоты ----

    [Fact]
    public void Snapshot_IsDoubleBuffered_PrevIsPreviousTick()
    {
        using Simulation sim = Make();
        VehicleHandle h = TestScenarios.PlaceCar(sim, 0, 50f, 5f);
        int i = h.Index;

        sim.Step(1);
        Assert.Equal(1, sim.SnapshotTick);
        VehicleRenderData first = sim.Vehicles[i];
        Assert.Equal(TestScenarios.MaxVehicles, sim.Vehicles.Length);
        Assert.NotEqual(0, first.Flags & (byte)VehicleRenderFlags.Active);
        Assert.Equal(0, sim.PreviousVehicles[i].Flags); // до первого Step слот был пуст

        sim.Step(1); // фазы 5-8 подключены: машина едет сама
        Assert.Equal(2, sim.SnapshotTick);
        VehicleRenderData cur = sim.Vehicles[i];
        VehicleRenderData prev = sim.PreviousVehicles[i];
        Assert.Equal(first.X, prev.X);
        Assert.Equal(first.Z, prev.Z);
        Assert.NotEqual(prev.X, cur.X);
        Assert.Equal(prev.Generation, cur.Generation);
        Assert.Equal(i, cur.Index);

        sim.Step(1); // буферы меняются местами: прежний текущий становится предыдущим
        Assert.Equal(cur.X, sim.PreviousVehicles[i].X);
        Assert.NotEqual(sim.PreviousVehicles[i].X, sim.Vehicles[i].X);
    }

    [Fact]
    public void Snapshot_PositionMatchesLaneGeometry_CenterIsBehindFrontBumper()
    {
        using Simulation sim = Make();
        VehicleHandle h = TestScenarios.PlaceCar(sim, 0, 50f, 3f);
        sim.Step(1);
        VehicleRenderData r = sim.Vehicles[h.Index];
        float s = sim.World.Vehicles.S[h.Index]; // машина уже сдвинулась на тик фазами 5-8
        (Sim.Core.Util.Vec2 pos, float heading) = LaneGeometry.Evaluate(sim.Network, 0, s - (4.5f * 0.5f));
        Assert.Equal(pos.X, r.X, 3);
        Assert.Equal(pos.Z, r.Z, 3);
        Assert.Equal(heading, r.Heading, 3);
        Assert.Equal(sim.World.Vehicles.Speed[h.Index], r.Speed);
        Assert.InRange(r.Speed, 1f, 3.5f);
        Assert.Equal(0, r.Type);
        Assert.Equal(h.Index, r.Index);
        Assert.Equal((ushort)h.Generation, r.Generation);
    }

    [Fact]
    public void Snapshot_DeadSlotsAreZero_AfterDespawn()
    {
        using Simulation sim = Make();
        VehicleHandle h = TestScenarios.PlaceCar(sim, 0, 50f);
        sim.Step(2);
        Assert.NotEqual(0, sim.Vehicles[h.Index].Flags);

        sim.World.Occupancy.UnlinkAll(h.Index);
        sim.World.Vehicles.Despawn(h);
        Assert.False(sim.IsAlive(h));
        sim.Step(2); // обновлены оба буфера
        Assert.True(sim.Vehicles[h.Index].Equals(default(VehicleRenderData)));
        Assert.True(sim.PreviousVehicles[h.Index].Equals(default(VehicleRenderData)));
    }

    [Fact]
    public void Queries_PickAndInfo()
    {
        using Simulation sim = Make();
        VehicleHandle a = TestScenarios.PlaceCar(sim, 0, 50f);
        VehicleHandle b = TestScenarios.PlaceCar(sim, 0, 120f, 7f);
        sim.Step(1);

        Assert.True(sim.IsAlive(a));
        Assert.True(sim.TryGetVehicleInfo(b, out VehicleInfo info));
        Assert.Equal(0, info.Lane);
        Assert.Equal(sim.World.Vehicles.S[b.Index], info.S);
        Assert.InRange(info.S, 120f, 121f); // фазы 5-8 сдвинули машину за тик
        Assert.Equal(sim.World.Vehicles.Speed[b.Index], info.Speed);
        Assert.InRange(info.Speed, 6f, 8f);
        Assert.NotEqual(0, info.Flags & (byte)VehicleRenderFlags.Active);
        Assert.False(sim.TryGetVehicleInfo(VehicleHandle.Null, out _));

        VehicleRenderData rb = sim.Vehicles[b.Index];
        Assert.True(sim.TryPickVehicle(rb.X + 0.5f, rb.Z, 2f, out VehicleHandle picked));
        Assert.Equal(b, picked);
        Assert.False(sim.TryPickVehicle(rb.X + 100f, rb.Z + 100f, 2f, out _));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndMakesCallsNoOp()
    {
        Simulation sim = Make();
        sim.Step(5);
        sim.Dispose();
        sim.Dispose();
        sim.Step(5);
        Assert.False(sim.Enqueue(Command.MakeClearSelection()));
        Assert.Equal(0, sim.Vehicles.Length);
        Assert.Equal(0, sim.Events.Length);
        Assert.Equal(0, sim.Network.Nodes.Length);
        Assert.Equal(0UL, sim.ComputeStateHash());
        Assert.False(sim.TryPickVehicle(0, 0, 10, out _));
    }

    [Fact]
    public void GetStats_ZeroSafe_AndComputes()
    {
        using Simulation sim = Make();
        sim.GetStats(out SimStats s0);
        Assert.Equal(0f, s0.AvgSpeedKmh);
        sim.Step(1200); // 60 с
        sim.GetStats(out SimStats s);
        Assert.Equal(0f, s.TripsCompletedPerMin);
    }
}
