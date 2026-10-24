using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Rules;
using Sim.Core.Tests.Fixtures;

namespace Sim.Core.Tests.Agents;

/// <summary>Контракты отдельных фаз 5-8 (Behaviour/Integrate/Transition/Arrival) на фикстуре.</summary>
public class PhasesTests
{
    [Fact]
    public void IntegrateClamp_HardStopsAtLeaderRearPlusMinGap_AndCountsClamp()
    {
        PhysicsFixture f = PhysicsFixture.Straight();
        int leader = f.Spawn(0, 100f, 0f);
        int follower = f.Spawn(0, 94.5f, 20f); // зазор 1 м на скорости 20 м/с: нужен клэмп
        BehaviourPhase.Run(f.Net, f.V, f.Occ, f.Routes, f.Types, f.P, f.Tick); // зазоры клэмпа пишет фаза 5
        f.V.Accel[leader] = 0f;
        f.V.Accel[follower] = 5f; // принудительно "газ": фаза 6 обязана остановить на S_old[лидер] - L - MinGap
        IntegratePhase.Run(f.Net, f.V, f.Types, f.P, ref f.Counters);
        Assert.Equal(1, f.Counters.HardClamps);
        float expected = 100f - 4.5f - 0.5f;
        Assert.Equal(expected, f.V.S[follower], 4);
        Assert.InRange(f.V.Speed[follower], 0f, 20f);
        Assert.Equal((expected - 94.5f) / PhysicsParams.DefaultDt, f.V.Speed[follower], 3);
    }

    [Fact]
    public void IntegrateClamp_UsesLeaderPositionAtTickStart_RegardlessOfIndexOrder()
    {
        // лидер имеет МЕНЬШИЙ индекс (обрабатывается раньше) и уже сдвинут к моменту обработки ведомого
        PhysicsFixture f = PhysicsFixture.Straight();
        int leader = f.Spawn(0, 100f, 10f);
        int follower = f.Spawn(0, 94f, 10f);
        Assert.True(leader < follower);
        BehaviourPhase.Run(f.Net, f.V, f.Occ, f.Routes, f.Types, f.P, f.Tick);
        f.V.Accel[leader] = 0f;
        f.V.Accel[follower] = 0f;
        IntegratePhase.Run(f.Net, f.V, f.Types, f.P, ref f.Counters);
        Assert.Equal(100.5f, f.V.S[leader], 4);
        Assert.Equal(94.5f, f.V.S[follower], 4); // ограничение 100 - 4.5 - 0.5 = 95 по СТАРОЙ позиции лидера - не сработало
        Assert.Equal(0, f.Counters.HardClamps);
    }

    [Fact]
    public void IntegrateClamp_ConsidersGhostMembershipOfLaneChanger()
    {
        PhysicsFixture f = PhysicsFixture.Straight();
        int changer = f.Spawn(0, 100f, 0f);
        int behindOnSource = f.Spawn(0, 94.5f, 15f);
        f.Occ.FindNeighbours(1, f.V.S[changer], out int ahead, out int behind);
        f.Occ.BeginLaneChange(changer, 1, ahead, behind); // ghost остаётся в полосе 0
        BehaviourPhase.Run(f.Net, f.V, f.Occ, f.Routes, f.Types, f.P, f.Tick);
        f.V.Accel[behindOnSource] = 3f;
        IntegratePhase.Run(f.Net, f.V, f.Types, f.P, ref f.Counters);
        Assert.True(f.V.S[behindOnSource] <= 100f - 4.5f - 0.5f + 1e-4f);
        Assert.Equal(1, f.Counters.HardClamps);
    }

    [Fact]
    public void BehaviourPhase_WritesOnlyAccelAndLaneChangeDir()
    {
        PhysicsFixture f = PhysicsFixture.Ring(24, 300f, 2, 128);
        f.FillRing(0, 20, 8f);
        f.FillRing(1, 20, 8f, offsetIndex: 1);
        f.Run(200);
        ulong before = f.Hash();
        float[] s = (float[])f.V.S.Clone();
        int[] lane = (int[])f.V.Lane.Clone();
        int[] ahead = (int[])f.V.Ahead.Clone();
        float[] speed = (float[])f.V.Speed.Clone();
        BehaviourPhase.Run(f.Net, f.V, f.Occ, f.Routes, f.Types, f.P, f.Tick);
        Assert.Equal(before, f.Hash());
        Assert.Equal(s, f.V.S);
        Assert.Equal(lane, f.V.Lane);
        Assert.Equal(ahead, f.V.Ahead);
        Assert.Equal(speed, f.V.Speed);
    }

    [Fact]
    public void BehaviourPhase_MobilRunsEveryNthTick_StaggeredByIndex()
    {
        PhysicsFixture f = PhysicsFixture.Straight();
        f.Spawn(0, 200f, 6f, PhysicsFixture.Slow);
        int a = f.Spawn(0, 150f, 15f); // желает обгон
        f.V.LaneChangeDir[a] = 0;
        int hits = 0;
        for (long tick = 0; tick < 10; tick++)
        {
            f.V.LaneChangeDir[a] = 0;
            BehaviourPhase.Run(f.Net, f.V, f.Occ, f.Routes, f.Types, f.P, tick);
            if (f.V.LaneChangeDir[a] != 0)
            {
                hits++;
                Assert.Equal(0, (tick + a) % 10);
            }
        }

        Assert.Equal(1, hits);
    }

    [Fact]
    public void Transition_MovesToNextLaneThroughConnector_AndRecomputesDesiredSpeed()
    {
        PhysicsFixture f = PhysicsFixture.TwoSegments(100f, 1, out int a, out int b);
        int car = f.Spawn(a, 99f, 10f);
        f.V.Accel[car] = 0f;
        f.Run(1);
        Assert.Equal(a, f.V.Lane[car]);
        f.Run(30);
        int conn = f.Net.LaneNext[f.Net.Lanes[a].FirstNext];
        Assert.True(f.V.Lane[car] == conn || f.V.Lane[car] == b);
        f.Run(20 * 4);
        Assert.Equal(b, f.V.Lane[car]);
        Assert.True(f.V.S[car] >= 0f && f.V.S[car] < f.Net.Lanes[b].Length);
        Assert.Equal(1u, f.NodeThroughput[f.Net.Lanes[conn].Node]);
        Assert.Equal(1, f.Counters.ConnectorEntries);
        Assert.Equal(PhysicsFixture.Limit, f.V.DesiredSpeed[car], 4);
        Assert.Equal(0, f.V.PointHint[car]);
    }

    [Fact]
    public void Transition_NoContinuation_CarStaysAtLaneEnd()
    {
        PhysicsFixture f = PhysicsFixture.Straight(100f);
        int car = f.Spawn(0, 99.99f, 10f);
        f.V.Accel[car] = 5f;
        for (int n = 0; n < 400; n++)
        {
            f.Step();
        }

        Assert.True(f.V.Pool.IsAlive(car));
        Assert.Equal(0, f.V.Lane[car]);
        Assert.InRange(f.V.S[car], 90f, 100f);
    }

    [Fact]
    public void Arrival_DespawnsOnlyAtDestination_EmitsEvent_AndUnlinks()
    {
        PhysicsFixture f = PhysicsFixture.Straight();
        int car = f.Spawn(0, 0f, 12f);
        int other = f.Spawn(1, 0f, 12f);
        f.V.DestLane[car] = 0;
        f.V.DestS[car] = 300f;
        f.V.SpawnTick[car] = 0;
        uint gen = (uint)f.V.Pool.GenerationOf(car);
        int ticks = 0;
        while (f.V.Pool.IsAlive(car) && ticks < 20 * 120)
        {
            Assert.True(f.V.S[car] < 300f);
            f.Step();
            ticks++;
        }

        Assert.False(f.V.Pool.IsAlive(car));
        Assert.True(f.V.Pool.IsAlive(other)); // без DestLane не исчезает никогда
        Assert.Equal(1, f.Counters.TripsCompleted);
        Assert.Equal(ticks - 1, f.Counters.TripTimeTicksTotal);
        Assert.Equal(-1, f.V.Lane[car]);
        Assert.Equal(1, f.EventCount);
        Assert.Equal(SimEventKind.TripCompleted, f.Events[0].Kind);
        Assert.Equal(car, f.Events[0].A);
        Assert.Equal((int)gen, f.Events[0].B);
        Assert.Null(f.CheckInvariants());
    }

    [Fact]
    public void Arrival_StuckVehicleIsNeverDespawned()
    {
        PhysicsFixture f = PhysicsFixture.Straight(500f);
        f.Types[0].LaneChangeThreshold = 1000f;
        int blocker = f.Spawn(0, 400f, 0f);
        f.Pin(blocker);
        int car = f.Spawn(0, 100f, 10f);
        f.V.DestLane[car] = 0;
        f.V.DestS[car] = 450f;
        f.Run(20 * 600);
        Assert.True(f.V.Pool.IsAlive(car));
        Assert.True(f.V.StillTicks[car] > 1200);
        Assert.Equal(0, f.Counters.TripsCompleted);
    }

    [Fact]
    public void Arrival_FullEventBuffer_ReservesOverflowMarkerAndCountsDrops()
    {
        PhysicsFixture f = PhysicsFixture.Straight();
        var events = new SimEvent[3];
        int count = 0;
        for (int k = 0; k < 5; k++)
        {
            int car = f.Spawn(0, 10f * k, 0f);
            f.V.DestLane[car] = 0;
            f.V.DestS[car] = 0f;
        }

        ArrivalPhase.Run(f.V, f.Occ, 7, events, ref count, ref f.Counters);
        Assert.Equal(3, count);
        Assert.Equal(SimEventKind.TripCompleted, events[0].Kind);
        Assert.Equal(SimEventKind.TripCompleted, events[1].Kind);
        Assert.Equal(SimEventKind.EventsOverflow, events[2].Kind);
        Assert.Equal(5, f.Counters.TripsCompleted);
        Assert.Equal(3, f.Counters.EventsDropped);
    }
}
