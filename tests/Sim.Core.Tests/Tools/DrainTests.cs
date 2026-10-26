using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Tests.Tools;

/// <summary>
/// Снос дороги дренажем (4.3, T4; player-tools.md §4.3, TL2): закрытие для въезда, ожидание опустения, удаление, отмена, застрявший дренаж не форсируется.
/// </summary>
public sealed class DrainTests
{
    private static Command Remove(Simulation sim, int seg, uint tag = 1, int flags = 0) =>
        Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg), Flags = flags }, tag);

    private static long Balance(Simulation sim) => sim.Counters.VehiclesSpawned - sim.ActiveVehicleCount - sim.Counters.TripsCompleted;

    [Fact]
    public void RemoveBusyRoad_NoVehicleLost_AndOpFinishes()
    {
        using Simulation sim = ToolsFixture.Small(500);
        World w = sim.World;
        NetworkStore n = w.Net;
        int seg = ToolsFixture.BusiestSegment(sim);
        int onIt = ToolOpsPhase.CountOn(w, seg);
        Assert.True(onIt > 0, "на самом загруженном сегменте нет машин");
        long balance = Balance(sim);
        int alive = n.AliveSegments;
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Remove(sim, seg)));
        Console.WriteLine($"[T4] байт на тике с командой RemoveRoad (включая первый вызов кода): {GC.GetAllocatedBytesForCurrentThread() - b0}");
        Assert.Equal(1, w.Ops.Count);
        Assert.NotEqual(0, n.Segments[seg].Flags & SegmentFlags.Closing);
        Assert.Equal(1, sim.ToolOps.Length);
        Assert.Equal(seg, sim.ToolOps[0].Target.Index);

        int ticks = 0;
        bool removed = false;
        long maxStep = 0;
        while (ticks < 4000 && !removed)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Step(1);
            maxStep = Math.Max(maxStep, GC.GetAllocatedBytesForCurrentThread() - before);
            ticks++;
            Assert.Equal(balance, Balance(sim));   // TL2: убыль живых - только прибытием
            foreach (SimEvent e in sim.Events)
            {
                removed |= e.Kind == SimEventKind.SegmentRemoved && e.A == seg;
            }
        }

        Assert.True(removed, $"сегмент не удалён за {ticks} тиков (осталось {w.Ops.Remaining[0]})");
        Console.WriteLine($"[T4] наибольшие байты за тик дренажа и финала: {maxStep}");
        Console.WriteLine($"[T4] снос загруженного сегмента ({onIt} машин на нём): {ticks} тиков до финала");
        Assert.Equal(0, w.Ops.Count);
        Assert.False(n.SegmentAlive(seg));
        Assert.Equal(alive - 1, n.AliveSegments);
        Assert.Null(w.VerifyDerived());
        ToolsFixture.RoundTrip(sim);

        sim.Step(800);
        Assert.Equal(balance, Balance(sim));
        Assert.Null(w.VerifyDerived());
        Assert.True(sim.Counters.StuckVehicles <= 8, $"застрявших после сноса {sim.Counters.StuckVehicles}");
    }

    [Fact]
    public void DrainStall_NotForced_ThenUndoReopens()
    {
        using Simulation sim = ToolsFixture.Small(500);
        World w = sim.World;
        NetworkStore n = w.Net;
        int seg = ToolsFixture.BusiestSegment(sim);
        Assert.True(ToolOpsPhase.CountOn(w, seg) > 0);
        long balance = Balance(sim);
        EntityRef handle = ToolsFixture.Segment(sim, seg);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Remove(sim, seg)));

        // Дорогу «не отпускает» одна машина: желаемая скорость 0 каждый тик (затор, который сам не рассосётся).
        int stalls = 0;
        int firstStall = -1;
        for (int t = 0; t < ToolOps.StallTicks + 400; t++)
        {
            Hold(w, seg);
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.DrainStalled)
                {
                    stalls++;
                    firstStall = firstStall < 0 ? t : firstStall;
                    Assert.Equal(w.Ops.Id[0], e.A);
                    Assert.True(e.B > 0);
                }
            }
        }

        Assert.Equal(1, stalls);   // раз за период
        Assert.True(n.SegmentAlive(seg));
        Assert.NotEqual(0, n.Segments[seg].Flags & SegmentFlags.Closing);
        Assert.Equal(balance, Balance(sim));   // никого не удалили
        Assert.True(w.Work.DrainStalls >= 1);
        Assert.Null(w.VerifyDerived());

        // Отмена: BuildRoad(RestoreOf) на живой закрывающийся сегмент переоткрывает его.
        var undo = Command.MakeBuildRoad(new BuildRoadCmd { RestoreOf = handle, SpeedLimit = 1f, LanesAB = 1 }, 9);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in undo));
        Assert.Equal(0, w.Ops.Count);
        Assert.Equal(0, n.Segments[seg].Flags & SegmentFlags.Closing);
        Assert.Equal(0, n.Lanes[n.Segments[seg].FirstLaneAB].Flags & LaneFlags.Closed);
        Assert.Null(w.VerifyDerived());
        sim.Step(300);
        Assert.Equal(balance, Balance(sim));
        Assert.Null(w.VerifyDerived());
    }

    // Все машины сегмента едут с желаемой скоростью 0: они останавливаются и дренаж стоит.
    private static void Hold(World w, int seg)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore n = w.Net;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i) && v.Lane[i] >= 0 && n.Lanes[v.Lane[i]].Segment == seg)
            {
                v.DesiredSpeed[i] = 0f;
            }
        }
    }

    [Fact]
    public void ActiveDrain_StepsAllocateNothing()
    {
        using Simulation sim = ToolsFixture.Small(500);
        World w = sim.World;
        int seg = ToolsFixture.BusiestSegment(sim);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Remove(sim, seg)));
        long bytes = Fixtures.AllocationProbe.Measure(() =>
        {
            for (int t = 0; t < 30; t++)
            {
                Hold(w, seg);   // дренаж не заканчивается: каждый тик 1а считает Remaining
                sim.Step(1);
            }
        });
        Assert.Equal(1, w.Ops.Count);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void RemoveRoad_Rejections()
    {
        using Simulation sim = ToolsFixture.Small(300);
        World w = sim.World;
        NetworkStore n = w.Net;
        int seg = ToolsFixture.BusiestSegment(sim);

        var stale = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(seg, n.Segments[seg].Generation + 2) }, 1);
        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, in stale));
        var badFlags = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg), Flags = 6 }, 2);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, in badFlags));
        Assert.Equal(0, w.Ops.Count);

        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Remove(sim, seg, 3)));
        Assert.Equal(RejectReason.Busy, ToolsFixture.Run(sim, Remove(sim, seg, 4)));   // цель уже в операции
        Assert.Equal(1, w.Ops.Count);

        // Сегмент с остановкой автобуса: снос остановок - T9.
        int withStop = -1;
        for (int i = 0; i < n.BusStopCount && withStop < 0; i++)
        {
            if (n.BusStopAlive(i))
            {
                withStop = n.Lanes[n.BusStops[i].Lane].Segment;
            }
        }

        Assert.True(withStop >= 0);
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(sim, Remove(sim, withStop, 5)));
    }

    [Fact]
    public void RemoveRoad_Mid_Drain_SaveRoundTrip_KeepsOperation()
    {
        using Simulation sim = ToolsFixture.Small(500);
        World w = sim.World;
        int seg = ToolsFixture.BusiestSegment(sim);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Remove(sim, seg)));
        sim.Step(5);
        Assert.Equal(1, w.Ops.Count);
        using Simulation loaded = ToolsFixture.RoundTrip(sim);
        Assert.Equal(1, loaded.World.Ops.Count);
        Assert.Equal(w.Ops.Id[0], loaded.World.Ops.Id[0]);
        Assert.Equal(1, loaded.ToolOps.Length);
        for (int t = 0; t < 600; t++)
        {
            sim.Step(1);
            loaded.Step(1);
        }

        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Null(loaded.World.VerifyDerived());
    }
}
