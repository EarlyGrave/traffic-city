using System;
using Sim.Core.Rules;
using Sim.Core.Tests.Fixtures;

namespace Sim.Core.Tests.Rules;

public class IdmTests
{
    private static readonly Sim.Core.Api.VehicleTypeDesc _car = PhysicsFixture.CarType();

    [Fact]
    public void FreeRoad_AtRest_AcceleratesWithAmax()
    {
        Assert.Equal(_car.Accel, Idm.Accel(_car, 0f, 15f, Idm.FreeGap, 0f), 5);
    }

    [Fact]
    public void FreeRoad_AtDesiredSpeed_ZeroAcceleration()
    {
        Assert.Equal(0f, Idm.Accel(_car, 15f, 15f, Idm.FreeGap, 0f), 5);
    }

    [Fact]
    public void FreeRoad_AboveDesiredSpeed_Decelerates()
    {
        Assert.True(Idm.Accel(_car, 20f, 15f, Idm.FreeGap, 0f) < -1f);
    }

    [Fact]
    public void ClosingOnSlowLeader_BrakesHarderWithSmallerGap()
    {
        float far = Idm.Accel(_car, 15f, 15f, 60f, 10f);
        float near = Idm.Accel(_car, 15f, 15f, 20f, 10f);
        Assert.True(far < 0f || far > -1f);
        Assert.True(near < far);
        Assert.True(near < -1.5f);
    }

    [Fact]
    public void Equilibrium_FollowingAtSameSpeed_GapNearDesired()
    {
        // s_eq = (s0 + vT) / sqrt(1 - (v/v0)^4): при v = 10, v0 = 15 равновесный зазор ~ 14..16 м, a == 0
        float v = 10f;
        float sEq = (_car.MinGap + (v * _car.HeadwayTime)) / MathF.Sqrt(1f - MathF.Pow(v / 15f, 4f));
        Assert.Equal(0f, Idm.Accel(_car, v, 15f, sEq, 0f), 3);
    }

    [Fact]
    public void Accel_ClampedAtMaxBrake()
    {
        Assert.Equal(-Idm.MaxBrake, Idm.Accel(_car, 20f, 15f, 0.5f, 20f), 5);
    }

    [Fact]
    public void DesiredSpeed_IsMinOfLimitAndMax()
    {
        var t = _car;
        t.SpeedFactor = 1.05f;
        Assert.Equal(1.05f * 10f, Idm.DesiredSpeed(t, 10f), 5);
        Assert.Equal(t.MaxSpeed, Idm.DesiredSpeed(t, 1000f), 5);
    }

    [Fact]
    public void SingleCar_ReachesDesiredSpeedAndNeverExceedsIt()
    {
        PhysicsFixture f = PhysicsFixture.Straight();
        int car = f.Spawn(0, 0f, 0f);
        float maxV = 0f;
        for (int n = 0; n < 20 * 40; n++)
        {
            f.Step();
            maxV = MathF.Max(maxV, f.V.Speed[car]);
            Assert.Null(f.CheckInvariants());
        }

        Assert.InRange(f.V.Speed[car], 14.8f, 15.001f);
        Assert.True(maxV <= 15.001f);
        Assert.Equal(0, f.Counters.HardClamps);
    }

    [Fact]
    public void StoppedLeader_FollowerBrakesWithoutCollisionOrClamp()
    {
        PhysicsFixture f = PhysicsFixture.Straight();
        f.Types[0].LaneChangeThreshold = 1000f; // без манёвров: чистый IDM
        int leader = f.Spawn(0, 1000f, 0f);
        f.Pin(leader);
        int follower = f.Spawn(0, 0f, 15f);
        for (int n = 0; n < 20 * 90; n++)
        {
            f.Step();
            Assert.Null(f.CheckInvariants());
        }

        float gap = f.V.S[leader] - f.V.Length[leader] - f.V.S[follower];
        Assert.True(f.V.Speed[follower] < 0.05f, $"v={f.V.Speed[follower]}");
        Assert.InRange(gap, 1.0f, 3.5f); // около s0 = 2 м
        Assert.Equal(0, f.Counters.HardClamps);
    }

    [Fact]
    public void DeadEnd_CarStopsBeforeLaneEndAndStaysAlive()
    {
        PhysicsFixture f = PhysicsFixture.Straight(500f);
        f.Types[0].LaneChangeThreshold = 1000f;
        int car = f.Spawn(0, 300f, 15f);
        f.Run(20 * 60);
        Assert.True(f.V.Pool.IsAlive(car));
        Assert.True(f.V.S[car] <= 500f);
        Assert.True(f.V.S[car] > 495f, $"s={f.V.S[car]}");
        Assert.True(f.V.Speed[car] < 0.05f);
        Assert.Equal(0, f.Counters.HardClamps);
        Assert.Equal(0, f.Counters.TripsCompleted);
    }
}
