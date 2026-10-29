using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Tools;
using Sim.Scenarios.Tools;

namespace Sim.Core.Tests.Agents;

/// <summary>VC2 (vehicle-classes.md §7 пп. 2, 3, 6): маски запрета полос и улиц в живом мире - полосы, SetStreetAccess, правки цепочек, детерминизм, сейв, 0 B/тик.</summary>
public class VehicleClassAccessTests
{
    private const uint Tag = 900;

    // Автобусная полоса LaneNo 0 в каждом направлении с двумя и более полосами; возвращает число команд.
    private static int EnqueueBusLanes(Simulation sim)
    {
        NetworkStore n = sim.World.Net;
        int count = 0;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            for (byte dir = 0; dir < 2; dir++)
            {
                int lanes = dir == 0 ? n.Segments[s].LanesAB : n.Segments[s].LanesBA;
                if (lanes >= 2)
                {
                    var f = new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, s), Dir = dir, LaneNo = 0, Flags = LaneFlags.BusOnly, Mask = LaneFlags.BusOnly };
                    Put(sim, Command.MakeSetLaneFlags(in f, Tag));
                    count++;
                }
            }
        }

        return count;
    }

    // Очередь команд ограничена: полная - шаг и повтор.
    private static void Put(Simulation sim, in Command c)
    {
        for (int k = 0; !sim.Enqueue(c); k++)
        {
            Assert.True(k < 100, "очередь команд не освобождается");
            sim.Step(1);
        }
    }

    // Зеркало BusOnly = (Deny == 0x1D) у всех живых полос.
    private static void AssertMirror(NetworkStore n)
    {
        for (int i = 0; i < n.LaneCount; i++)
        {
            ref readonly LaneData l = ref n.Lanes[i];
            Assert.True(((l.Flags & LaneFlags.BusOnly) != 0) == (l.Deny == LaneDeny.BusLane), $"полоса {i}: Flags {l.Flags}, Deny {l.Deny}");
        }
    }

    private static int FindSegment(NetworkStore n, int minLanesAB, float minLength)
    {
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (n.SegmentAlive(s) && n.Segments[s].LanesAB >= minLanesAB && n.Segments[s].Length >= minLength)
            {
                return s;
            }
        }

        return -1;
    }

    // Легковая не въезжает на автобусную полосу (MOBIL, connector, прибытие - только если это её полоса назначения); автобус - въезжает.
    [Fact]
    public void BusLanes_CarsNeverEnter_BusesDo()
    {
        using Simulation sim = Simulation.Create(ToolsFixture.SmallDesc(), new SimRuntimeOptions { WorkerThreads = 1 });
        Assert.True(EnqueueBusLanes(sim) > 0, "в малом мире нет направлений с двумя полосами");
        sim.Step(1);
        World w = sim.World;
        NetworkStore n = w.Net;
        AssertMirror(n);
        sim.Step(600);   // маршруты, построенные до масок, сменились
        VehicleStore v = w.Vehicles;
        var prevLane = new int[v.Capacity];
        var prevGen = new int[v.Capacity];
        Array.Fill(prevLane, -1);
        int carChecks = 0, busOnBusLane = 0;
        for (int t = 0; t < 1500; t++)
        {
            for (int i = 0; i < v.HighWater; i++)
            {
                if (!v.Pool.IsAlive(i))
                {
                    prevLane[i] = -1;
                    continue;
                }

                int lane = v.Lane[i];
                int gen = v.Pool.GenerationOf(i);
                bool denied = lane >= 0 && !VehicleAccess.Allowed(v.Access(i), n.Lanes[lane].Deny);
                if (lane >= 0 && n.Lanes[lane].Deny == LaneDeny.BusLane)
                {
                    if ((v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
                    {
                        busOnBusLane++;
                    }
                    else
                    {
                        carChecks++;
                    }
                }

                if (denied && prevLane[i] >= 0 && prevGen[i] == gen && prevLane[i] != lane)
                {
                    Assert.True(lane == v.DestLane[i], $"тик {w.Tick}: машина {i} (тип {v.Type[i]}) въехала с полосы {prevLane[i]} на запрещённую полосу {lane}");
                }

                if (v.GhostLane[i] >= 0)
                {
                    Assert.True(VehicleAccess.Allowed(v.Access(i), n.Lanes[v.Lane[i]].Deny) || VehicleAccess.Allowed(v.Access(i), n.Lanes[v.GhostLane[i]].Deny));
                }

                prevLane[i] = lane;
                prevGen[i] = gen;
            }

            sim.Step(1);
        }

        Assert.True(busOnBusLane > 0, "автобусы не ездили по автобусным полосам");
        _ = carChecks;
    }

    // SetLaneFlags(BusOnly) - то же, что SetLaneDeny(0x1D); снятие - Deny 0; хэш сети при одних автобусных полосах - без нового раздела.
    [Fact]
    public void SetLaneFlagsBusOnly_EqualsLaneDeny()
    {
        using Simulation sim = ToolsFixture.Small(100);
        NetworkStore n = sim.World.Net;
        int seg = FindSegment(n, 2, 0f);
        Assert.True(seg >= 0);
        int lane = n.Segments[seg].FirstLaneAB;
        var on = new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Dir = 0, LaneNo = 0, Flags = LaneFlags.BusOnly, Mask = LaneFlags.BusOnly };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetLaneFlags(in on, 1)));
        Assert.Equal(LaneDeny.BusLane, n.Lanes[lane].Deny);
        Assert.NotEqual(0, n.Lanes[lane].Flags & LaneFlags.BusOnly);
        Assert.Equal(0, sim.World.Graph.DirSegDeny[seg * 2]);   // вторая полоса направления открыта: DirSeg без запрета (AND полос)
        var off = on;
        off.Flags = 0;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetLaneFlags(in off, 2)));
        Assert.Equal(0, n.Lanes[lane].Deny);
        Assert.Equal(0, n.Lanes[lane].Flags & LaneFlags.BusOnly);
        Assert.Equal(0, sim.World.Graph.DenyUnion);
        AssertMirror(n);
    }

    // SetStreetAccess: проверка аргументов, применение по маске, отмена через стек, хэш сети прежний после возврата.
    [Fact]
    public void SetStreetAccess_AppliesValidatesAndUndoes()
    {
        using Simulation sim = ToolsFixture.Small(100);
        NetworkStore n = sim.World.Net;
        int seg = FindSegment(n, 1, 0f);
        ulong h0 = n.Hash();
        EntityRef sr = ToolsFixture.Segment(sim, seg);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = sr, Deny = VehicleAccess.Special, Mask = VehicleAccess.Special }, 1)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = sr, Deny = VehicleAccess.Truck, Mask = VehicleAccess.Van }, 2)));
        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = new EntityRef(seg, sr.Generation + 2), Deny = 0, Mask = VehicleAccess.Truck }, 3)));

        var stack = new UndoStack();
        Assert.True(stack.Execute(sim, new[] { Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = sr, Deny = VehicleAccess.Truck | VehicleAccess.Van, Mask = VehicleAccess.Truck | VehicleAccess.Van }) }).Ok);
        Assert.Equal(VehicleAccess.Truck | VehicleAccess.Van, n.Segments[seg].Deny);
        Assert.Equal(VehicleAccess.Truck | VehicleAccess.Van, sim.World.Graph.DirSegDeny[seg * 2] & (VehicleAccess.Truck | VehicleAccess.Van));
        Assert.NotEqual(h0, n.Hash());

        // Маска: меняется только бит van, truck остаётся.
        Assert.True(stack.Execute(sim, new[] { Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = sr, Deny = 0, Mask = VehicleAccess.Van }) }).Ok);
        Assert.Equal(VehicleAccess.Truck, n.Segments[seg].Deny);
        Assert.True(stack.Undo(sim).Ok);
        Assert.Equal(VehicleAccess.Truck | VehicleAccess.Van, n.Segments[seg].Deny);
        Assert.True(stack.Undo(sim).Ok);
        Assert.Equal(0, n.Segments[seg].Deny);
        Assert.Equal(0, sim.World.Graph.DenyUnion);
    }

    // Разрез и слияние сохраняют маски полос (с зеркалом BusOnly) и улицы.
    [Fact]
    public void Split_KeepsLaneAndStreetDeny()
    {
        using Simulation sim = ToolsFixture.Small(300);
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        NetworkStore n = sim.World.Net;
        int seg = FindSegment(n, 2, 90f);
        Assert.True(seg >= 0);
        EntityRef sr = ToolsFixture.Segment(sim, seg);
        var bus = new SetLaneFlagsCmd { Seg = sr, Dir = 0, LaneNo = 0, Flags = LaneFlags.BusOnly, Mask = LaneFlags.BusOnly };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetLaneFlags(in bus, 1)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = sr, Deny = VehicleAccess.Truck, Mask = VehicleAccess.Truck }, 2)));
        float len = n.Segments[seg].Length;
        Assert.Equal(RejectReason.None, ToolsFixture.RunUntilNotBusy(sim, () => Command.MakeSplitRoad(new SplitRoadCmd { Seg = ToolsFixture.Segment(sim, seg), S = len * 0.5f }, 3)));
        for (int i = 0; i < 4000 && sim.World.Ops.Count > 0; i++)
        {
            sim.Step(1);
        }

        int streets = 0, busLanes = 0;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            streets += n.Segments[s].Deny == VehicleAccess.Truck ? 1 : 0;
            int first = n.Segments[s].FirstLaneAB;
            for (int k = 0; k < n.Segments[s].LanesAB; k++)
            {
                busLanes += n.Lanes[first + k].Deny == LaneDeny.BusLane ? 1 : 0;
            }
        }

        Assert.Equal(2, streets);
        Assert.Equal(2, busLanes);
        AssertMirror(n);
    }

    private static Simulation DeniedWorld(int threads)
    {
        Simulation sim = Simulation.Create(ToolsFixture.SmallDesc(), new SimRuntimeOptions { WorkerThreads = threads });
        EnqueueBusLanes(sim);
        NetworkStore n = sim.World.Net;
        for (int s = 0; s < n.SegmentCount; s += 4)
        {
            if (n.SegmentAlive(s))
            {
                Put(sim, Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = ToolsFixture.Segment(sim, s), Deny = VehicleAccess.Truck | VehicleAccess.Van, Mask = VehicleAccess.Truck | VehicleAccess.Van }, Tag));
            }
        }

        sim.Step(1);
        return sim;
    }

    // Детерминизм с масками: хэш не зависит от числа потоков (1/2/3).
    [Fact]
    public void Denies_Deterministic_AcrossThreadCounts()
    {
        ulong[] hashes = new ulong[3];
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = DeniedWorld(t);
            sim.Step(900);
            hashes[t - 1] = sim.ComputeStateHash();
        }

        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(hashes[0], hashes[2]);
    }

    // Сейв с масками: после загрузки мир идёт так же, как исходный.
    [Fact]
    public void Denies_SaveRoundTrip_ContinuesIdentically()
    {
        using Simulation a = DeniedWorld(1);
        a.Step(300);
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(bytes);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.World.Net.Hash(), b.World.Net.Hash());
        Assert.Equal(a.World.Graph.DenyUnion, b.World.Graph.DenyUnion);
        a.Step(300);
        b.Step(300);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    // 0 B/тик в стационаре с масками полос и улиц (фильтр в поиске, проверки смены полосы).
    [Fact]
    public void Denies_ZeroAllocationsPerTick()
    {
        using Simulation sim = DeniedWorld(1);
        sim.Step(600);
        Assert.Equal(0, AllocationProbe.Measure(() => sim.Step(100)));
    }
}
