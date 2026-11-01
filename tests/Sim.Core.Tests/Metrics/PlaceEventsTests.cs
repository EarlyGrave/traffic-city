using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Metrics;
using Sim.Core.Network;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.Metrics;

/// <summary>
/// События с местом (EV1, core-api.md §5.7): <c>JamAt</c>, <c>BusLineProblem</c>, <c>ParkingFull</c>, место у <c>GridlockOn</c>/<c>DrainStalled</c>/<c>ZoneAccessLost</c>;
/// ограничение частоты по месту, 0 Б/тик, размер <c>SimEvent</c> 48 Б.
/// </summary>
public sealed class PlaceEventsTests
{
    [Fact]
    public void Layout_SimEvent48_EventPlace20()
    {
        Assert.Equal(48, Marshal.SizeOf<SimEvent>());
        Assert.Equal(20, Marshal.SizeOf<EventPlace>());
        Assert.Equal(24, (int)Marshal.OffsetOf<SimEvent>(nameof(SimEvent.Place)));
        Assert.Equal(44, (int)Marshal.OffsetOf<SimEvent>(nameof(SimEvent.C)));
        Assert.Equal(63, (int)SimEventKind.JamAt);
        Assert.Equal(64, (int)SimEventKind.BusLineProblem);
        Assert.Equal(65, (int)SimEventKind.ParkingFull);
        Assert.Equal(6, (int)EntityKind.Building);
    }

    // Все машины на сегменте стоят (желаемая скорость 0), очередь растёт и за ним.
    private static void Hold(Sim.Core.World w, int seg)
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

    private static void AssertSegmentPlace(Sim.Core.World w, in EventPlace p)
    {
        Assert.Equal(EntityKind.Segment, p.ObjKind);
        Assert.True(w.Net.SegmentAlive(p.Index));
        Assert.Equal(w.Net.Segments[p.Index].Generation, p.Generation);
        EventPlace want = PlaceEvents.SegmentPlace(w.Net, p.Index);
        Assert.Equal(want.X, p.X);
        Assert.Equal(want.Z, p.Z);
        Assert.True(p.X != 0f || p.Z != 0f);
    }

    [Fact]
    public void JamAt_FiresOnHeldSegment_WithPlace_RateLimited_ZeroAlloc()
    {
        using Simulation sim = ToolsFixture.Small(500);
        var w = sim.World;
        int seg = ToolsFixture.BusiestSegment(sim);
        var jams = new List<SimEvent>();
        for (int t = 0; t < 4000; t++)
        {
            Hold(w, seg);
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.JamAt)
                {
                    jams.Add(e);
                    Assert.Equal(0, e.Tick % PlaceEvents.ScanPeriodTicks);
                    Assert.True(e.A >= PlaceEvents.JamMinStuck);
                    Assert.True(e.B >= e.A);
                    AssertSegmentPlace(w, in e.Place);
                }
            }
        }

        Assert.NotEmpty(jams);
        var last = new Dictionary<int, long>();
        foreach (SimEvent e in jams)
        {
            if (last.TryGetValue(e.Place.Index, out long prev))
            {
                Assert.True(e.Tick - prev >= PlaceEvents.JamCooldownTicks, $"сегмент {e.Place.Index}: JamAt через {e.Tick - prev} тиков");
            }

            last[e.Place.Index] = e.Tick;
        }

        // Место gridlock - сегмент с наибольшим числом застрявших (тот же обход).
        EventPlace g = w.Places.GridlockPlace(w);
        AssertSegmentPlace(w, in g);

        long bytes = Fixtures.AllocationProbe.Measure(() =>
        {
            for (int t = 0; t < 300; t++)
            {
                Hold(w, seg);
                sim.Step(1);
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void BusLine_NoBuses_FiresOnce_PerCooldown_AtFirstStop()
    {
        using Simulation sim = Simulation.Create(ToolsFixture.SmallDesc(buses: true));
        var w = sim.World;
        NetworkStore n = w.Net;
        Assert.True(n.BusRouteCount > 0);
        int r = 0;
        n.BusRoutes[r].VehicleType = 9999;   // выпуск невозможен: линия без автобусов
        var got = new List<SimEvent>();
        int total = n.BusRoutes[r].HeadwayTicks + PlaceEvents.NoBusGraceTicks + PlaceEvents.BusCooldownTicks + 400;
        for (int t = 0; t < total; t++)
        {
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.BusLineProblem && e.A == (int)BusLineIssue.NoBuses && e.Place.Index == r)
                {
                    got.Add(e);
                }
            }
        }

        Assert.Equal(2, got.Count);   // первый раз и ещё раз после остывания
        Assert.True(got[1].Tick - got[0].Tick >= PlaceEvents.BusCooldownTicks);
        SimEvent ev = got[0];
        Assert.Equal(EntityKind.BusRoute, ev.Place.ObjKind);
        Assert.Equal(n.BusRoutes[r].Generation, ev.Place.Generation);
        Assert.True(ev.B >= PlaceEvents.NoBusGraceTicks);
        int stop = n.BusRoutes[r].FirstStop;
        Assert.Equal(stop, ev.C);
        (Sim.Core.Util.Vec2 pos, _) = LaneGeometry.Evaluate(n, n.BusStops[stop].Lane, n.BusStops[stop].S);
        Assert.Equal(pos.X, ev.Place.X, 3);
        Assert.Equal(pos.Z, ev.Place.Z, 3);
    }

    [Fact]
    public void BusLine_Overloaded_AtWorstStop()
    {
        using Simulation sim = ToolsFixture.Small(200);
        var w = sim.World;
        NetworkStore n = w.Net;
        int r = 0;
        int stop = n.BusRoutes[r].FirstStop + (n.BusRoutes[r].StopCount - 1);
        SimEvent? hit = null;
        for (int t = 0; t < 400 && hit == null; t++)
        {
            n.BusStops[stop].Waiting = 190;
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.BusLineProblem && e.A == (int)BusLineIssue.Overloaded && e.Place.Index == r)
                {
                    hit = e;
                }
            }
        }

        Assert.NotNull(hit);
        SimEvent ev = hit!.Value;
        Assert.Equal(stop, ev.C);
        Assert.True(ev.B >= PlaceEvents.OverloadWaiting);
        Assert.Equal(EntityKind.BusRoute, ev.Place.ObjKind);
        (Sim.Core.Util.Vec2 pos, _) = LaneGeometry.Evaluate(n, n.BusStops[stop].Lane, n.BusStops[stop].S);
        Assert.Equal(pos.X, ev.Place.X, 3);
        Assert.Equal(pos.Z, ev.Place.Z, 3);
    }

    [Fact]
    public void BusLine_BusesStuck_WhenBusHeld()
    {
        using Simulation sim = ToolsFixture.Small(600);
        var w = sim.World;
        VehicleStore v = w.Vehicles;
        SimEvent? hit = null;
        for (int t = 0; t < 6000 && hit == null; t++)
        {
            for (int i = 0; i < v.HighWater; i++)
            {
                if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
                {
                    v.DesiredSpeed[i] = 0f;
                }
            }

            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.BusLineProblem && e.A == (int)BusLineIssue.BusesStuck)
                {
                    hit = e;
                }
            }
        }

        string dbg = $"routes {w.Net.BusRouteCount} stuckThr {w.Rules.StuckThresholdTicks}: ";
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
            {
                dbg += $"[{i} f={v.Flags[i]} still={v.StillTicks[i]} sp={v.Speed[i]} lane={v.Lane[i]}] ";
            }
        }

        Assert.True(hit != null, dbg);
        SimEvent ev = hit!.Value;
        Assert.Equal(EntityKind.BusRoute, ev.Place.ObjKind);
        Assert.True(ev.B >= 1);
        Assert.Equal(-1, ev.C);
        Assert.True(ev.Place.X != 0f || ev.Place.Z != 0f);
    }

    [Fact]
    public void DrainStalled_CarriesTargetSegment()
    {
        using Simulation sim = ToolsFixture.Small(500);
        var w = sim.World;
        int seg = ToolsFixture.BusiestSegment(sim);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 1)));
        SimEvent? hit = null;
        for (int t = 0; t < ToolOps.StallTicks + 400 && hit == null; t++)
        {
            Hold(w, seg);
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.DrainStalled)
                {
                    hit = e;
                }
            }
        }

        Assert.NotNull(hit);
        Assert.Equal(seg, hit!.Value.Place.Index);
        AssertSegmentPlace(w, hit.Value.Place);
    }

    [Fact]
    public void ZoneAccessLost_CarriesZoneCenter_AndSegment()
    {
        using Simulation sim = Simulation.Create(ToolsFixture.SmallDesc(buses: false));
        var w = sim.World;
        NetworkStore n = w.Net;
        int zone = -1;
        for (int z = 0; z < n.ZoneCount && zone < 0; z++)
        {
            zone = n.Zones[z].SpawnLaneCount > 0 ? z : -1;
        }

        Assert.True(zone >= 0);

        // Закрываем сегменты полос появления зоны по очереди (как дренаж сноса): событие - на закрытии последнего, B = он, место - центр зоны.
        ref readonly ZoneData zd = ref n.Zones[zone];
        var segs = new List<int>();
        for (int k = 0; k < zd.SpawnLaneCount; k++)
        {
            int s0 = n.Lanes[n.ZoneLanes[zd.FirstSpawnLane + k]].Segment;
            if (!segs.Contains(s0))
            {
                segs.Add(s0);
            }
        }

        int seen = 0;
        foreach (int seg in segs)
        {
            int from = w.EventCount;
            SegmentClosure.Close(w, seg, 3);
            for (int k = from; k < w.EventCount; k++)
            {
                SimEvent e = w.Events[k];
                if (e.Kind == SimEventKind.ZoneAccessLost && e.A == zone)
                {
                    seen++;
                    Assert.Equal(segs[^1], seg);
                    Assert.Equal(seg, e.B);
                    Assert.Equal(3u, e.Tag);
                    Assert.Equal(EntityKind.Zone, e.Place.ObjKind);
                    Assert.Equal(zone, e.Place.Index);
                    Assert.Equal(0, e.Place.Generation);
                    Assert.Equal(n.Zones[zone].Center.X, e.Place.X);
                    Assert.Equal(n.Zones[zone].Center.Z, e.Place.Z);
                }
            }
        }

        Assert.Equal(1, seen);
    }

    [Fact]
    public void ParkingFull_AtDestinationBuilding_RateLimited()
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res-park.json")).Desc;
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        var c = sim.World.City!;
        Sim.Core.Parking.ParkingStore p = c.Parking!;
        for (int f = 1; f < p.HighWater; f++)
        {
            if (p.IsAlive(f))
            {
                p.Capacity[f] = p.Assigned[f];   // у гостей мест нет нигде
            }
        }

        int window = Math.Max(c.DayTicks / 24, PlaceEvents.ScanPeriodTicks);
        var got = new List<SimEvent>();
        for (int t = 0; t < 200; t++)
        {
            sim.Step(100);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.ParkingFull)
                {
                    got.Add(e);
                }
            }
        }

        // Дальше - по тику: остывание проверяется на подряд идущих тиках.
        for (int t = 0; t < 6000; t++)
        {
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.ParkingFull)
                {
                    got.Add(e);
                }
            }
        }

        Assert.True(sim.Counters.ParkNoSpotWaits > 0);
        Assert.NotEmpty(got);
        var last = new Dictionary<(int, int), long>();
        foreach (SimEvent e in got)
        {
            Assert.Equal(EntityKind.Building, e.Place.ObjKind);
            Assert.True(e.A >= PlaceEvents.ParkingMinRefusals);
            Assert.Equal(c.Buildings.Lot[e.Place.Index], e.B);
            Assert.Equal(c.Lots.X[e.B], e.Place.X);
            Assert.Equal(c.Lots.Z[e.B], e.Place.Z);
            var key = (e.Place.Index, e.Place.Generation);
            if (last.TryGetValue(key, out long prev))
            {
                Assert.True(e.Tick - prev >= 2L * window, $"здание {e.Place.Index}: ParkingFull через {e.Tick - prev} тиков");
            }

            last[key] = e.Tick;
        }
    }
}
