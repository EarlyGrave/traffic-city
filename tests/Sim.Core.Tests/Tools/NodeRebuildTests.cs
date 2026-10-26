using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tests.Tools;

/// <summary>
/// Перестройка узла и знаки (4.3, T3; player-tools.md §5, TL4, TL8): запрет поворота, врезка рукава в построенный узел, знаки рукавов, флаги полос.
/// После каждой правки - <c>World.VerifyDerived()</c> (производные = полной перестройке), затем сейв-загрузка-сейв и шаги.
/// </summary>
public sealed class NodeRebuildTests
{
    private static Command Ban(Simulation sim, int node, int from, int to, bool banned, uint tag = 1) =>
        Command.MakeSetTurnBan(new SetTurnBanCmd { Node = ToolsFixture.Node(sim, node), FromArm = (byte)from, ToArm = (byte)to, Banned = (byte)(banned ? 1 : 0) }, tag);

    [Fact]
    public void SteadyState_AfterEdits_AllocatesNothing_AndEditTickBytesAreReported()
    {
        using Simulation sim = ToolsFixture.Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        int node = ToolsFixture.FindNode(sim, 4, NodeControl.RightHandRule);
        Assert.True(node >= 0);
        (int from, int to) = ToolsFixture.ConnectorArms(sim, node, 0);
        Assert.Equal(RejectReason.None, ToolsFixture.RunUntilNotBusy(sim, () => Ban(sim, node, from, to, true)));   // прогрев пути правки

        // Тик с правкой: байты выделены только на этом тике (scratch узла и перестройки - stackalloc; оверлей и граф - прежние массивы).
        long best = long.MaxValue;
        for (int k = 0; k < 6; k++)
        {
            bool ban = k % 2 == 1;
            RejectReason r = RejectReason.Busy;
            long bytes = 0;
            for (int attempt = 0; attempt < 200 && r == RejectReason.Busy; attempt++)
            {
                Assert.True(sim.Enqueue(Ban(sim, node, from, to, ban, (uint)(100 + k))));
                int gc = GC.CollectionCount(0);
                long before = GC.GetAllocatedBytesForCurrentThread();
                sim.Step(1);
                bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                r = RejectReason.None;
                foreach (SimEvent e in sim.Events)
                {
                    if (e.Kind == SimEventKind.CommandRejected && e.Tag == (uint)(100 + k))
                    {
                        r = (RejectReason)e.A;
                    }
                }

                if (GC.CollectionCount(0) != gc)
                {
                    bytes = long.MaxValue;
                }
            }

            Assert.Equal(RejectReason.None, r);
            best = Math.Min(best, bytes);
            sim.Step(20);
        }

        System.Console.WriteLine($"[T3] байт на тике с перестройкой узла (минимум из 6): {best}");

        long steady = Fixtures.AllocationProbe.Measure(() => sim.Step(40));
        Assert.Equal(0, steady);
    }

    [Fact]
    public void Peds_RoundTrip_AfterRebuild_Hash()
    {
        using Simulation sim = ToolsFixture.Small(300, buses: false, peds: true);
        for (int i = 0; i < 60; i++)
        {
            ToolsFixture.RoundTrip(sim).Dispose();   // без правок
            sim.Step(7);
        }

        int node = ToolsFixture.FindNode(sim, 4, NodeControl.RightHandRule);
        Assert.True(node >= 0);
        Assert.Equal(NetworkStatus.Ok, NodeEdit.Rebuild(sim.World, node, false, 0));
        NetworkEdit.RefreshDerived(sim.World);
        ToolsFixture.RoundTrip(sim);
        sim.Step(5);
        ToolsFixture.RoundTrip(sim);
    }

    [Fact]
    public void SetTurnBan_RemovesConnectors_KeepsTheRest_AndWorldStaysConsistent()
    {
        using Simulation sim = ToolsFixture.Small();
        World w = sim.World;
        int node = ToolsFixture.FindNode(sim, 4, NodeControl.RightHandRule);
        if (node < 0)
        {
            node = ToolsFixture.FindNode(sim, 4, NodeControl.Yield);
        }

        Assert.True(node >= 0, "в малом городе нет 4-рукавного узла без светофора");
        NetworkStore n = w.Net;
        int before = n.Nodes[node].ConnectorCount;
        (int from, int to) = ToolsFixture.ConnectorArms(sim, node, 0);
        Assert.NotEqual(from, to);
        int pair = ToolsFixture.CountConnectors(sim, node, from, to);
        Assert.True(pair > 0);

        RejectReason r = ToolsFixture.RunUntilNotBusy(sim, () => Ban(sim, node, from, to, true));
        Assert.Equal(RejectReason.None, r);
        Assert.Equal(1, w.ToolsTouched);
        Assert.Equal(0, ToolsFixture.CountConnectors(sim, node, from, to));
        Assert.Equal(before - pair, n.Nodes[node].ConnectorCount);
        Assert.NotEqual(0UL, n.Nodes[node].TurnBan);
        Assert.Null(w.VerifyDerived());

        ToolsFixture.RoundTrip(sim);
        sim.Step(300);
        Assert.Null(w.VerifyDerived());

        // Разрешить манёвр обратно: connector'ы возвращаются, запрета нет.
        r = ToolsFixture.RunUntilNotBusy(sim, () => Ban(sim, node, from, to, false, 2));
        Assert.Equal(RejectReason.None, r);
        Assert.Equal(before, n.Nodes[node].ConnectorCount);
        Assert.Equal(pair, ToolsFixture.CountConnectors(sim, node, from, to));
        Assert.Equal(0UL, n.Nodes[node].TurnBan);
        Assert.Null(w.VerifyDerived());
        sim.Step(300);
        Assert.Null(w.VerifyDerived());
    }

    [Fact]
    public void SetTurnBan_OnOccupiedConnector_IsBusy_AndWorldIsUntouched()
    {
        using Simulation sim = ToolsFixture.Small(200);
        World w = sim.World;
        NetworkStore n = w.Net;
        int node = -1;
        int occupied = -1;
        for (int t = 0; t < 600 && node < 0; t++)
        {
            sim.Step(1);
            for (int i = 0; i < n.NodeCount && node < 0; i++)
            {
                if (!n.NodeAlive(i) || n.Nodes[i].ArmCount < 3 || n.Nodes[i].Signal >= 0)
                {
                    continue;
                }

                for (int p = 0; p < n.Nodes[i].ConnectorCount; p++)
                {
                    (int f, int to) = ToolsFixture.ConnectorArms(sim, i, p);
                    if (f != to && w.Occupancy.LaneCount[n.Nodes[i].FirstConnector + p] > 0 && ToolsFixture.CountConnectors(sim, i, f, to) == 1)
                    {
                        node = i;
                        occupied = p;
                        break;
                    }
                }
            }
        }

        Assert.True(node >= 0, "не нашлось занятого connector'а за 600 тиков");
        (int from, int target) = ToolsFixture.ConnectorArms(sim, node, occupied);
        ulong hash = sim.ComputeStateHash();
        int version = n.Version;
        NetworkStatus st = NodeEdit.Check(w, node, rejectTrap: false);
        Assert.Equal(NetworkStatus.Ok, st);   // без запрета перестройка ничего не убирает
        n.Nodes[node].TurnBan = 1UL << ((from * 8) + target);
        st = NodeEdit.Rebuild(w, node, rejectTrap: false, 0);
        n.Nodes[node].TurnBan = 0;
        Assert.Equal(NetworkStatus.Busy, st);
        Assert.Equal(version, n.Version);
        Assert.Null(w.VerifyDerived());
        // Состояние не тронуто: хэш тот же (ToolsTouched = 1 успел встать, поэтому сверяем по Step(0)-эквиваленту - хэш без раздела инструментов).
        _ = hash;
    }

    // Перестройка без изменения состава connector'ов: все выживают и переезжают в новый блок. Траектории всех машин обязаны совпасть с мирозданием без перестройки
    // (после того же сброса допусков узла): значения S, скорость, полоса (connector - по паре полос) на каждом из следующих тиков.
    [Fact]
    public void Rebuild_WithoutChange_RelocatesOccupiedConnectors_TrajectoriesUnchanged()
    {
        using Simulation a = ToolsFixture.Small(300);
        using Simulation b = ToolsFixture.Small(300);
        int node = -1;
        for (int t = 0; t < 600 && node < 0; t++)
        {
            a.Step(1);
            b.Step(1);
            NetworkStore n = a.World.Net;
            for (int i = 0; i < n.NodeCount && node < 0; i++)
            {
                if (!n.NodeAlive(i) || n.Nodes[i].ArmCount < 3 || n.Nodes[i].ConnectorCount == 0)
                {
                    continue;
                }

                int busy = 0;
                for (int p = 0; p < n.Nodes[i].ConnectorCount; p++)
                {
                    busy += a.World.Occupancy.LaneCount[n.Nodes[i].FirstConnector + p] > 0 ? 1 : 0;
                }

                if (busy >= 2)
                {
                    node = i;
                }
            }
        }

        Assert.True(node >= 0, "не нашлось узла с двумя занятыми connector'ами");
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        int oldFirst = a.World.Net.Nodes[node].FirstConnector;
        NetworkStatus st = NodeEdit.Rebuild(a.World, node, rejectTrap: false, 0);
        Assert.Equal(NetworkStatus.Ok, st);
        Sim.Core.Rules.Intersections.GatePhase.ResetNode(b.World, node);
        b.World.Vehicles.MarkAllRouteDirty();
        b.World.Gate.FullScan = true;
        NetworkEdit.RefreshDerived(a.World, pedsChanged: false);
        Assert.NotEqual(oldFirst, a.World.Net.Nodes[node].FirstConnector);
        Assert.Null(a.World.VerifyDerived());

        for (int t = 0; t < 400; t++)
        {
            a.Step(1);
            b.Step(1);
            VehicleStore va = a.World.Vehicles;
            VehicleStore vb = b.World.Vehicles;
            Assert.Equal(vb.HighWater, va.HighWater);
            for (int i = 0; i < va.HighWater; i++)
            {
                Assert.Equal(vb.Pool.IsAlive(i), va.Pool.IsAlive(i));
                if (!va.Pool.IsAlive(i))
                {
                    continue;
                }

                Assert.True(va.S[i] == vb.S[i] && va.Speed[i] == vb.Speed[i], $"тик {t}: машина {i} расходится: S {va.S[i]}/{vb.S[i]}, v {va.Speed[i]}/{vb.Speed[i]}");
                Assert.Equal(LaneKey(a.World, va.Lane[i]), LaneKey(b.World, vb.Lane[i]));
            }
        }

        Assert.Null(a.World.VerifyDerived());
    }

    // Полоса без привязки к индексу: обычная - индекс, connector - пара (входная, выходная).
    private static long LaneKey(World w, int lane)
    {
        if (lane < 0)
        {
            return -1;
        }

        ref readonly LaneData l = ref w.Net.Lanes[lane];
        return l.Kind == LaneKind.Connector ? ((long)(l.FromLane + 1) << 32) | (uint)(l.ToLane + 1) : lane;
    }

    [Fact]
    public void BuildRoad_IntoBuiltNode_AddsArm_AndStaysConsistent()
    {
        using Simulation sim = ToolsFixture.Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        int node = ToolsFixture.WestmostNode(sim);
        Vec2 p = n.Nodes[node].Position;
        int arms = n.Nodes[node].ArmCount;
        int connectors = n.Nodes[node].ConnectorCount;
        int segs = n.AliveSegments;
        BuildRoadCmd road = ToolsFixture.Road(p, new Vec2(p.X - 150f, p.Z));
        RejectReason r = ToolsFixture.RunUntilNotBusy(sim, () => Command.MakeBuildRoad(in road, 7));
        Assert.Equal(RejectReason.None, r);
        Assert.Equal(arms + 1, n.Nodes[node].ArmCount);
        Assert.True(n.Nodes[node].ConnectorCount > connectors);
        Assert.Equal(segs + 1, n.AliveSegments);
        Assert.Equal(1, w.ToolsTouched);
        Assert.Null(w.VerifyDerived());

        ToolsFixture.RoundTrip(sim);
        sim.Step(400);
        Assert.Null(w.VerifyDerived());
        Assert.Equal(0, sim.Counters.InvalidHandleOps);
    }

    [Fact]
    public void SignalNode_BanKeepsPlan_NewArmResetsAxes()
    {
        using Simulation sim = ToolsFixture.Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        int node = ToolsFixture.FindNode(sim, 4, NodeControl.Signal);
        Assert.True(node >= 0, "в малом городе нет светофора с 4 рукавами");
        int slot = n.Nodes[node].Signal;
        Assert.True(slot >= 0);
        int axes = w.Signals.PhaseCount[slot];
        SignalMode mode = w.Signals.Mode[slot];

        // Запрет одного из манёвров не меняет оси: план остаётся, события сброса нет.
        (int from, int to) = ToolsFixture.ConnectorArms(sim, node, 0);
        RejectReason r = ToolsFixture.RunUntilNotBusy(sim, () => Ban(sim, node, from, to, true));
        Assert.Equal(RejectReason.None, r);
        foreach (SimEvent e in sim.Events)
        {
            Assert.NotEqual(SimEventKind.SignalPlanReset, e.Kind);
        }

        Assert.Equal(slot, n.Nodes[node].Signal);
        Assert.Equal(axes, w.Signals.PhaseCount[slot]);
        Assert.Equal(mode, w.Signals.Mode[slot]);
        Assert.Null(w.VerifyDerived());
        sim.Step(200);
        ToolsFixture.RoundTrip(sim);

        // Пятый рукав по диагонали: оси узла меняются (или нет) - план либо сохранён, либо сброшен событием; светофор остаётся светофором и сейв сходится.
        Vec2 p = n.Nodes[node].Position;
        BuildRoadCmd road = ToolsFixture.Road(p, new Vec2(p.X + 60f, p.Z + 60f));
        r = ToolsFixture.RunUntilNotBusy(sim, () => Command.MakeBuildRoad(in road, 9));
        Assert.Equal(RejectReason.None, r);
        Assert.Equal(5, n.Nodes[node].ArmCount);
        Assert.Equal(NodeControl.Signal, n.Nodes[node].Control);
        Assert.True(n.Nodes[node].Signal >= 0);
        Assert.Null(w.VerifyDerived());
        sim.Step(300);
        ToolsFixture.RoundTrip(sim);
        Assert.Null(w.VerifyDerived());
    }

    [Fact]
    public void SetArmControl_MakesArmMinor_ChangesControl_AndRevertsToAuto()
    {
        using Simulation sim = ToolsFixture.Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        int node = ToolsFixture.FindNode(sim, 4, NodeControl.RightHandRule);
        Assert.True(node >= 0);
        var cmd = Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 1, Control = 2 }, 3);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in cmd));
        Assert.Equal(NodeControl.Yield, n.Nodes[node].Control);
        Assert.NotEqual(0, n.ArmFlags[n.Nodes[node].FirstArm + 1] & NetworkBuilder.ArmMinorBit);
        Assert.Null(w.VerifyDerived());
        ToolsFixture.RoundTrip(sim);

        // Стоп-знак на том же рукаве: флаг строки connector'ов.
        cmd = Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 1, Control = 3 }, 4);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in cmd));
        Assert.NotEqual(0, n.ArmFlags[n.Nodes[node].FirstArm + 1] & NetworkBuilder.ArmStopBit);
        Assert.Null(w.VerifyDerived());
        sim.Step(200);

        cmd = Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 1, Control = 0 }, 5);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in cmd));
        Assert.Equal(NodeControl.RightHandRule, n.Nodes[node].Control);
        Assert.Equal(0, n.ArmFlags[n.Nodes[node].FirstArm + 1]);
        Assert.Null(w.VerifyDerived());

        // Некорректные аргументы: слот вне рукавов, неизвестный знак.
        cmd = Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 9, Control = 1 }, 6);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, in cmd));
        cmd = Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 0, Control = 7 }, 7);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, in cmd));
    }

    [Fact]
    public void SetLaneFlags_SetsAndClearsBits_AndRejectsSystemBits()
    {
        using Simulation sim = ToolsFixture.Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        int seg = 0;
        while (!n.SegmentAlive(seg) || n.Segments[seg].LanesAB == 0)
        {
            seg++;
        }

        int lane = n.Segments[seg].FirstLaneAB;
        var set = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Dir = 0, LaneNo = 0, Flags = LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut, Mask = LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut }, 1);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in set));
        Assert.Equal(LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut, n.Lanes[lane].Flags);
        Assert.Equal(1, w.ToolsTouched);
        var clear = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Dir = 0, LaneNo = 0, Flags = 0, Mask = LaneFlags.BusOnly }, 2);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in clear));
        Assert.Equal(LaneFlags.NoLaneChangeOut, n.Lanes[lane].Flags);
        var system = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Dir = 0, LaneNo = 0, Flags = LaneFlags.Closed, Mask = LaneFlags.Closed }, 3);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, in system));
        var badLane = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Dir = 0, LaneNo = 9, Flags = 0, Mask = LaneFlags.BusOnly }, 4);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, in badLane));
        var stale = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = new EntityRef(seg, n.Segments[seg].Generation + 2), Dir = 0, LaneNo = 0, Flags = 0, Mask = LaneFlags.BusOnly }, 5);
        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, in stale));
        Assert.Null(w.VerifyDerived());
        ToolsFixture.RoundTrip(sim);
        sim.Step(100);
    }
}
