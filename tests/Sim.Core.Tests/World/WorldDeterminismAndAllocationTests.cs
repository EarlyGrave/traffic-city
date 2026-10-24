using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;

namespace Sim.Core.Tests.Worlds;

public class WorldDeterminismAndAllocationTests
{
    private static Simulation Setup(ulong seed)
    {
        ScenarioDesc d = TestScenarios.Line(seed);
        Simulation sim = Simulation.Create(in d);
        for (int k = 0; k < 10; k++)
        {
            TestScenarios.PlaceCar(sim, k % 4, 20f + (k * 9f), k);
        }

        return sim;
    }

    /// <summary>Одинаковый «внешний» сценарий: движение вместо неподключённых фаз и командный поток по тикам.</summary>
    private static void Drive(Simulation sim, int tick)
    {
        World w = sim.World;
        for (int i = 0; i < w.Vehicles.HighWater; i++)
        {
            if (w.Vehicles.Pool.IsAlive(i))
            {
                w.Vehicles.S[i] += 0.01f * ((i % 3) + 1);
            }
        }

        if (tick % 50 == 0)
        {
            sim.Enqueue(Command.MakeSetTargetVehicleCount(tick / 50, (uint)tick));
        }

        if (tick % 70 == 0)
        {
            var t = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 0, Count = 2 };
            sim.Enqueue(Command.MakeSpawnTrip(in t, (uint)tick));
        }

        if (tick % 90 == 0)
        {
            sim.Enqueue(Command.MakeSelectVehicle(w.Vehicles.HandleOf(tick / 90 % 5)));
        }
    }

    [Fact]
    public void SameDescAndCommands_SameHashEvery100Ticks()
    {
        using Simulation a = Setup(42);
        using Simulation b = Setup(42);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        for (int tick = 0; tick < 2000; tick++)
        {
            Drive(a, tick);
            Drive(b, tick);
            a.Step(1);
            b.Step(1);
            if ((tick + 1) % 100 == 0)
            {
                Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
            }
        }

        Assert.Equal(2000, a.Tick);
    }

    [Fact]
    public void DifferentSeed_DifferentHash()
    {
        using Simulation a = Setup(1);
        using Simulation b = Setup(2);
        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
        for (int tick = 0; tick < 300; tick++)
        {
            Drive(a, tick);
            Drive(b, tick);
            a.Step(1);
            b.Step(1);
        }

        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void Hash_ReactsToStateAndCommands()
    {
        using Simulation a = Setup(7);
        using Simulation b = Setup(7);
        a.Step(10);
        b.Step(10);
        ulong h0 = a.ComputeStateHash();
        Assert.Equal(h0, b.ComputeStateHash());
        Assert.Equal(h0, a.ComputeStateHash()); // чистая функция состояния

        float saved = b.World.Vehicles.S[0];
        b.World.Vehicles.S[0] = saved + 0.001f;
        Assert.NotEqual(h0, b.ComputeStateHash());
        b.World.Vehicles.S[0] = saved;
        Assert.Equal(h0, b.ComputeStateHash());

        var t = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 0, Count = 1 };
        b.Enqueue(Command.MakeSpawnTrip(in t));
        b.Step(1);
        a.Step(1);
        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void Step_Allocates_ZeroBytes_InSteadyState()
    {
        using Simulation sim = Setup(5);
        var spawn = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 0, Count = 1 };
        int n = 0;

        void RunTicks(int ticks)
        {
            for (int t = 0; t < ticks; t++, n++)
            {
                sim.World.Vehicles.S[0] += 0.001f;
                sim.Enqueue(Command.MakeClearSelection());
                sim.Enqueue(Command.MakeSetTargetVehicleCount(n % 7));
                sim.Enqueue(Command.MakeSpawnTrip(in spawn));
                sim.Enqueue(Command.MakeSelectVehicle(sim.World.Vehicles.HandleOf(n % 10)));
                sim.Step(1);
                _ = sim.Events.Length;
                _ = sim.Vehicles[0];
                _ = sim.PreviousVehicles[0];
                _ = sim.SelectedRoute;
            }
        }

        RunTicks(300); // прогрев: JIT-тиринг

        Assert.Equal(0, AllocationProbe.Measure(() => RunTicks(1000)));
        Assert.Equal(0, AllocationProbe.Measure(() => sim.Step(1000))); // и просто крупный Step
    }

    [Fact]
    public void ComputeStateHash_DoesNotAllocate()
    {
        using Simulation sim = Setup(5);
        sim.Step(10);
        _ = sim.ComputeStateHash();
        Assert.Equal(0, AllocationProbe.Measure(() => _ = sim.ComputeStateHash()));
    }

    [Fact]
    public void SameDescTwice_BuildsIdenticalNetwork()
    {
        using Simulation a = Setup(3);
        using Simulation b = Setup(3);
        Assert.Equal(a.World.Net.Hash(), b.World.Net.Hash());
        Assert.NotEqual(0UL, a.World.Net.Hash());
        Assert.Equal(a.Network.Version, b.Network.Version);
    }
}
