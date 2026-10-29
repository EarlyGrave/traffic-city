using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Tools;

namespace Sim.Core.Tests.Intersections;

/// <summary>Приоритет спецмашины на вызове (vehicle-classes.md §2.4, VC3): красный с замедлением, Yield на красном, RevokeOnRed, очередь, детерминизм.</summary>
public class GateOnCallTests
{
    private const byte SpecialType = 1;

    /// <summary>Крест со светофором Fixed; тип 1 - спецмашина (копия легковой).</summary>
    private static Simulation SignalCross()
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        VehicleTypeDesc car = d.VehicleTypes[0];
        VehicleTypeDesc sp = car;
        sp.Category = VehicleCategory.Special;
        d.VehicleTypes = new[] { car, sp };
        d.Demand.TypeMix = new[] { 1f, 0f };
        Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        SignalFixture.EnqueueFixedDefault(sim, 0, 2);
        sim.Step(1);
        Assert.Equal(NodeControl.Signal, sim.World.Net.Nodes[0].Control);
        return sim;
    }

    private static void StepUntil(Simulation sim, int connector, LaneSignal want)
    {
        for (int t = 0; t < 4000 && sim.World.LaneSignals[connector] != (byte)want; t++)
        {
            sim.Step(1);
        }

        Assert.Equal((byte)want, sim.World.LaneSignals[connector]);
    }

    /// <summary>Машина-спецмашина (тип 1), на вызове или нет.</summary>
    private static int PlaceSpecial(Simulation sim, int lane, float s, float speed, int connector, bool onCall)
    {
        int i = GateFixture.Place(sim, lane, s, speed, connector);
        sim.World.Vehicles.Type[i] = SpecialType;
        if (onCall)
        {
            sim.World.Vehicles.Flags[i] |= (byte)VehicleFlags.OnCall;
        }

        return i;
    }

    // Красный: машина на вызове тормозит к линии, получает допуск при v <= порога (15 км/ч) на красном и проезжает; не на вызове - стоит.
    [Fact]
    public void Red_OnCall_SlowsAndIsGranted_NotOnCall_Waits()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool onCall = pass == 0;
            using Simulation sim = SignalCross();
            World w = sim.World;
            int lane = GateFixture.InLane(w, 0);
            int conn = GateFixture.ConnectorTo(w, lane, 2);
            float len = w.Net.LaneLength[lane];
            StepUntil(sim, conn, LaneSignal.Red);
            int x = PlaceSpecial(sim, lane, len - 100f, 12f, conn, onCall);
            int grantedAt = -1;
            float speedAtGrant = -1f;
            for (int t = 0; t < 300 && w.LaneSignals[conn] == (byte)LaneSignal.Red; t++)
            {
                sim.Step(1);
                if (GateRules.IsGranted(w.Vehicles, x))
                {
                    grantedAt = t;
                    speedAtGrant = w.Vehicles.Speed[x];
                    Assert.Equal((byte)LaneSignal.Red, w.LaneSignals[conn]);
                    break;
                }
            }

            if (onCall)
            {
                Assert.True(grantedAt > 0, "спецмашина на вызове не получила допуск на красный");
                Assert.True(speedAtGrant <= w.Rules.EmergencyRedSpeed + 0.5f, $"допуск на скорости {speedAtGrant}");
                Assert.True(speedAtGrant < 12f, "машина должна была сбросить скорость");
                Assert.Equal(1, sim.Counters.OnCallRedGrants);
            }
            else
            {
                Assert.Equal(-1, grantedAt);
                Assert.Equal(0, sim.Counters.OnCallRedGrants);
            }
        }
    }

    // Допуск, выданный на красном, не снимается (RevokeOnRed); обычная допущенная в том же положении снимается.
    [Fact]
    public void Red_RevokeSkipsOnCall()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool onCall = pass == 0;
            using Simulation sim = SignalCross();
            World w = sim.World;
            int lane = GateFixture.InLane(w, 0);
            int conn = GateFixture.ConnectorTo(w, lane, 2);
            StepUntil(sim, conn, LaneSignal.Red);
            int x = PlaceSpecial(sim, lane, 150f, 5f, conn, onCall);
            w.Vehicles.GateState[x] = GateRules.Granted;
            w.Vehicles.GrantConnector[x] = conn;
            w.Vehicles.Ticket[x] = w.Net.NodeTicket[0]++;
            sim.Step(1);
            Assert.Equal(onCall, GateRules.IsGranted(w.Vehicles, x));
            Assert.Equal(onCall ? 0 : 1, sim.Counters.GrantsRevoked);
        }
    }

    // Пересекающийся поток на зелёном не тормозит из-за спецмашины: его скорость и ускорение равны прогону без неё, пока она не допущена.
    [Fact]
    public void Red_CrossingGreenFlow_UnaffectedUntilGrant()
    {
        var accel = new float[2][];
        var speed = new float[2][];
        int grantTick = -1;
        for (int pass = 0; pass < 2; pass++)
        {
            using Simulation sim = SignalCross();
            World w = sim.World;
            int laneA = GateFixture.InLane(w, 0);
            int connA = GateFixture.ConnectorTo(w, laneA, 2);
            int laneB = GateFixture.InLane(w, 1);
            int connB = GateFixture.ConnectorTo(w, laneB, 3);
            StepUntil(sim, connB, LaneSignal.Green);
            Assert.Equal((byte)LaneSignal.Red, w.LaneSignals[connA]);
            int b = GateFixture.Place(sim, laneB, w.Net.LaneLength[laneB] - 70f, 12f, connB);
            int a = -1;
            if (pass == 0)
            {
                a = PlaceSpecial(sim, laneA, w.Net.LaneLength[laneA] - 60f, 12f, connA, onCall: true);
            }

            accel[pass] = new float[120];
            speed[pass] = new float[120];
            for (int t = 0; t < 120; t++)
            {
                sim.Step(1);
                accel[pass][t] = w.Vehicles.Accel[b];
                speed[pass][t] = w.Vehicles.Speed[b];
                if (pass == 0 && grantTick < 0 && GateRules.IsGranted(w.Vehicles, a))
                {
                    grantTick = t;
                }
            }

            // Спецмашина проезжает узел, не дожидаясь смены сигнала не обязана, но окно ищет сама: к концу прогона допуск уже мог быть выдан.
        }

        int compared = grantTick < 0 ? 120 : grantTick;
        Assert.True(compared >= 30, $"слишком короткое окно сравнения: {compared}");
        for (int t = 0; t < compared; t++)
        {
            Assert.Equal(accel[1][t], accel[0][t]);
            Assert.Equal(speed[1][t], speed[0][t]);
        }
    }

    // Пробка: спецмашина на вызове не в голове очереди ждёт наравне с легковой той же позиции (допуск в тот же тик ±1).
    [Fact]
    public void Queue_OnCallInsideQueue_WaitsLikeCar()
    {
        int[] grantedAt = new int[2];
        for (int pass = 0; pass < 2; pass++)
        {
            using Simulation sim = SignalCross();
            World w = sim.World;
            int lane = GateFixture.InLane(w, 0);
            int conn = GateFixture.ConnectorTo(w, lane, 2);
            float len = w.Net.LaneLength[lane];
            StepUntil(sim, conn, LaneSignal.Red);
            int q0 = GateFixture.Place(sim, lane, len - 2f, 0f, conn);
            int q1 = GateFixture.Place(sim, lane, len - 9f, 0f, conn);
            int q2 = PlaceSpecial(sim, lane, len - 16f, 0f, conn, onCall: pass == 0);
            Assert.True(q0 != q1 && q1 != q2);
            grantedAt[pass] = -1;
            for (int t = 0; t < 3000 && grantedAt[pass] < 0; t++)
            {
                sim.Step(1);
                if (GateRules.IsGranted(w.Vehicles, q2))
                {
                    grantedAt[pass] = t;
                }
            }

            Assert.True(grantedAt[pass] > 0);
        }

        Assert.InRange(grantedAt[0] - grantedAt[1], -1, 1);
    }

    private static ScenarioDesc OnCallWorld()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        VehicleTypeDesc car = d.VehicleTypes[0];
        VehicleTypeDesc bus = d.VehicleTypes[1];
        VehicleTypeDesc sp = car;
        sp.Category = VehicleCategory.Special;
        d.VehicleTypes = new[] { car, bus, sp };
        d.Demand.TypeMix = new[] { 0.7f, 0f, 0.3f };
        d.Demand.SpecialOnCall = true;
        return d;
    }

    // Детерминизм: хэш не зависит от числа потоков (1/2/3); спецмашины на вызове реально проезжают красный.
    [Fact]
    public void OnCallWorld_Deterministic_AcrossThreadCounts()
    {
        ulong[] hashes = new ulong[3];
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = Simulation.Create(OnCallWorld(), new SimRuntimeOptions { WorkerThreads = t });
            sim.Step(1500);
            hashes[t - 1] = sim.ComputeStateHash();
            if (t == 1)
            {
                Assert.True(sim.Counters.OnCallRedGrants > 0, "в прогоне нет ни одного проезда на красный");
            }
        }

        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(hashes[0], hashes[2]);
    }

    // Сейв: после загрузки мир идёт так же (счётчики и флаг OnCall в файле).
    [Fact]
    public void OnCallWorld_SaveRoundTrip_ContinuesIdentically()
    {
        using Simulation a = Simulation.Create(OnCallWorld(), new SimRuntimeOptions { WorkerThreads = 1 });
        a.Step(700);
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(bytes);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.Counters.OnCallRedGrants, b.Counters.OnCallRedGrants);
        Assert.Equal(a.World.Rules.EmergencyRedSpeed, b.World.Rules.EmergencyRedSpeed);
        a.Step(500);
        b.Step(500);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.Counters.OnCallRedGrants, b.Counters.OnCallRedGrants);
    }

    // 0 B/тик в стационаре со спецмашинами на вызове.
    [Fact]
    public void OnCallWorld_ZeroAllocationsPerTick()
    {
        using Simulation sim = Simulation.Create(OnCallWorld(), new SimRuntimeOptions { WorkerThreads = 1 });
        sim.Step(900);
        Assert.Equal(0, AllocationProbe.Measure(() => sim.Step(100)));
    }

    // Команда SetOnCall: только у спецмашины.
    [Fact]
    public void SetOnCall_OnlySpecial()
    {
        using Simulation sim = SignalCross();
        World w = sim.World;
        int lane = GateFixture.InLane(w, 0);
        int conn = GateFixture.ConnectorTo(w, lane, 2);
        int car = GateFixture.Place(sim, lane, 100f, 5f, conn);
        int sp = PlaceSpecial(sim, lane, 60f, 5f, conn, onCall: false);
        VehicleHandle hc = w.Vehicles.HandleOf(car);
        VehicleHandle hs = w.Vehicles.HandleOf(sp);
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetOnCall(hc, true, 1)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetOnCall(hs, true, 2)));
        Assert.NotEqual(0, w.Vehicles.Flags[sp] & (byte)VehicleFlags.OnCall);
        Assert.Equal(VehicleAccess.Emergency, w.Vehicles.Access(sp) & VehicleAccess.Emergency);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetOnCall(hs, false, 3)));
        Assert.Equal(0, w.Vehicles.Flags[sp] & (byte)VehicleFlags.OnCall);
    }
}
