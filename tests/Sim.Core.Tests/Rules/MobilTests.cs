using Sim.Core.Api;
using Sim.Core.Tests.Fixtures;

namespace Sim.Core.Tests.Rules;

public class MobilTests
{
    [Fact]
    public void CarBehindSlowLeader_OvertakesOnTwoLaneRoad()
    {
        PhysicsFixture f = PhysicsFixture.Straight(2000f);
        int slow = f.Spawn(0, 200f, 6f, PhysicsFixture.Slow);
        int fast = f.Spawn(0, 120f, 15f);
        bool wentLeft = false;
        bool passed = false;
        for (int n = 0; n < 20 * 120; n++)
        {
            f.Step();
            Assert.Null(f.CheckInvariants());
            if (f.V.Lane[fast] == 1)
            {
                wentLeft = true;
            }

            if (f.V.S[fast] - f.V.Length[fast] > f.V.S[slow])
            {
                passed = true;
                break;
            }
        }

        Assert.True(wentLeft, "обгоняющий должен занять левую полосу");
        Assert.True(passed, "обгоняющий должен оказаться впереди медленного");
        Assert.Equal(0, f.Counters.HardClamps);
    }

    [Fact]
    public void AfterOvertaking_RightBiasReturnsCarToRightLane()
    {
        PhysicsFixture f = PhysicsFixture.Straight(2000f);
        f.Spawn(0, 200f, 6f, PhysicsFixture.Slow);
        int fast = f.Spawn(0, 120f, 15f);
        f.Run(20 * 150);
        Assert.Equal(0, f.V.Lane[fast]);
        Assert.Equal(-1, f.V.GhostLane[fast]);
        Assert.Equal(0f, f.V.Lateral[fast]);
    }

    [Fact]
    public void LaneChange_UsesGhostForConfiguredDurationAndAnimatesLateral()
    {
        PhysicsFixture f = PhysicsFixture.Straight(2000f);
        f.Spawn(0, 200f, 6f, PhysicsFixture.Slow);
        int fast = f.Spawn(0, 120f, 15f);
        int ticksWithGhost = 0;
        bool started = false;
        float firstLateral = 0f;
        for (int n = 0; n < 20 * 60; n++)
        {
            f.Step();
            if (f.V.GhostLane[fast] != -1)
            {
                if (!started)
                {
                    started = true;
                    firstLateral = f.V.Lateral[fast];
                }

                ticksWithGhost++;
            }
            else if (started)
            {
                break;
            }
        }

        Assert.True(started);
        Assert.Equal(f.Types[0].LaneChangeDurationTicks, ticksWithGhost);
        Assert.Equal(-PhysicsParamsLaneWidth(), firstLateral, 3); // стартует на оси правой полосы (справа от целевой)
        Assert.Equal(0f, f.V.Lateral[fast]);
    }

    [Fact]
    public void UnsafeGapToFastFollower_BlocksLaneChange()
    {
        PhysicsFixture f = PhysicsFixture.Straight(2000f);
        f.Spawn(0, 200f, 4f, PhysicsFixture.Slow);
        int car = f.Spawn(0, 150f, 12f);
        int chaser = f.Spawn(1, 138f, 15f); // на левой полосе, почти вплотную сзади и быстрее
        f.Types[PhysicsFixture.Car].SafeDecel = 1f;
        for (int n = 0; n < 8; n++)
        {
            f.Step();
            Assert.Equal(0, f.V.Lane[car]);
            Assert.Equal(-1, f.V.GhostLane[car]);
        }

        Assert.True(f.V.Pool.IsAlive(chaser));
    }

    // intersections.md 7.1: вставка перед допущенной машиной целевой полосы запрещена (иначе лидер получит билет позже ведомого -
    // взаимная блокировка «физический лидер ждёт ведомого по билету»). Контроль: тот же сценарий без допуска - смена полосы идёт.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GrantedFollowerOnTargetLane_BlocksInsertionAhead(bool followerGranted, bool expectChange)
    {
        PhysicsFixture f = PhysicsFixture.Straight(2000f);
        f.Spawn(0, 200f, 6f, PhysicsFixture.Slow);
        int car = f.Spawn(0, 120f, 15f);
        int follower = f.Spawn(1, 40f, 5f); // на левой полосе далеко позади: по зазорам вставка безопасна
        if (followerGranted)
        {
            f.V.GateState[follower] = Sim.Core.Rules.Intersections.GateRules.Granted;
        }

        bool changed = false;
        for (int n = 0; n < 200; n++)
        {
            f.Step();
            changed |= f.V.GhostLane[car] != -1 || f.V.Lane[car] != 0;
        }

        Assert.Equal(expectChange, changed);
    }

    [Fact]
    public void RouteNeedsOtherLane_CarChangesLaneBeforeTurnAndFollowsRoute()
    {
        PhysicsFixture f = PhysicsFixture.TwoSegments(400f, 2, out int a, out int b);
        // connector'ы: полоса k первого сегмента -> полоса k второго; маршрут требует левую (k = 1)
        int conn1 = f.Net.LaneNext[f.Net.Lanes[a + 1].FirstNext];
        Assert.Equal(b + 1, f.Net.Lanes[conn1].ToLane);
        var steps = new RouteStep[] { new() { Lane = a + 1, Node = 1 }, new() { Lane = conn1, Node = 1 }, new() { Lane = b + 1, Node = -1 } };
        f.SetSingleRoute(steps);
        int car = f.Spawn(a, 50f, 10f);
        f.V.Route[car] = 0;
        f.V.RouteCursor[car] = 0;

        bool changed = false;
        for (int n = 0; n < 20 * 60; n++)
        {
            f.Step();
            Assert.Null(f.CheckInvariants());
            if (f.V.Lane[car] == a + 1 && f.V.RouteCursor[car] == 0)
            {
                changed = true;
            }

            if (f.V.Lane[car] == b + 1)
            {
                break;
            }
        }

        Assert.True(changed, "перед поворотом машина должна занять нужную полосу");
        Assert.Equal(b + 1, f.V.Lane[car]);
        Assert.Equal(2, f.V.RouteCursor[car]);
    }

    [Fact]
    public void RouteNeedsOtherLane_BlockedTarget_CarDoesNotWait_TakesConnectorToSameNextSegment()
    {
        PhysicsFixture f = PhysicsFixture.TwoSegments(200f, 2, out int a, out int b);
        int conn1 = f.Net.LaneNext[f.Net.Lanes[a + 1].FirstNext];
        var steps = new RouteStep[] { new() { Lane = a + 1, Node = 1 }, new() { Lane = conn1, Node = 1 }, new() { Lane = b + 1, Node = -1 } };
        f.SetSingleRoute(steps);
        // левая полоса забита неподвижной колонной по всей длине: перестроиться некуда
        for (int k = 0; k < 39; k++)
        {
            f.Pin(f.Spawn(a + 1, 10f + (k * 4.9f), 0f));
        }

        f.Types[0].LaneChangeThreshold = 1000f;
        int car = f.Spawn(a, 5f, 10f);
        f.V.Route[car] = 0;
        f.Run(20 * 30);
        // routing.md 6.2: машина не ждёт перестроения стоя - на конце полосы берёт connector в тот же следующий сегмент
        // (маршрут продолжается, на следующем сегменте MOBIL доведёт до нужной полосы).
        Assert.NotEqual(a, f.V.Lane[car]);
        Assert.True(f.V.RouteCursor[car] >= 2);
        Assert.Equal(0, f.V.Flags[car] & (byte)Sim.Core.Agents.VehicleFlags.RouteMissed);
    }

    private static float PhysicsParamsLaneWidth() => Sim.Core.Rules.PhysicsParams.DefaultLaneWidth;
}
