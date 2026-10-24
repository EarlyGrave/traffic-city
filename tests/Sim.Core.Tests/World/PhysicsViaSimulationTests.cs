using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Tests.Worlds;

/// <summary>Фазы 4-8 через обёртки Run(World) на мире, созданном Simulation.Create (без команд, спавна и метрик).</summary>
public class PhysicsViaSimulationTests
{
    private static void Step(Simulation sim, int ticks)
    {
        World w = sim.World;
        for (int n = 0; n < ticks; n++)
        {
            SignalPhase.Run(w);
            GatePhase.Run(w);
            BehaviourPhase.Run(w);
            IntegratePhase.Run(w);
            TransitionPhase.Run(w);
            ArrivalPhase.Run(w);
            w.Tick++;
        }
    }

    private static void SetDesiredSpeed(Simulation sim, VehicleHandle h)
    {
        World w = sim.World;
        w.Vehicles.DesiredSpeed[h.Index] = 10f;
    }

    private static ulong Hash(Simulation sim)
    {
        var h = Sim.Core.Util.Fnv1a64.Create();
        VehicleStore v = sim.World.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i))
            {
                h.Add(v.Lane[i]);
                h.Add(v.S[i]);
                h.Add(v.Speed[i]);
            }
        }

        return h.Value;
    }

    [Fact]
    public void Car_DrivesThroughNodeAndArrivesAtDestination()
    {
        ScenarioDesc d = TestScenarios.Line();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        VehicleHandle h = TestScenarios.PlaceCar(sim, w.Net.Segments[0].FirstLaneAB, 10f, 5f);
        SetDesiredSpeed(sim, h);
        int dest = w.Net.Segments[1].FirstLaneAB;
        w.Vehicles.DestLane[h.Index] = dest;
        w.Vehicles.DestS[h.Index] = 100f;

        Step(sim, 20 * 60);
        Assert.False(sim.IsAlive(h));
        Assert.Equal(1, sim.Counters.TripsCompleted);
        Assert.True(sim.Counters.ConnectorEntries >= 1);
        Assert.Equal(0, sim.Counters.HardClamps);
    }

    [Fact]
    public void Cars_StepZeroBytesPerTick_AndDeterministic()
    {
        static Simulation Make()
        {
            ScenarioDesc d = TestScenarios.Line();
            Simulation sim = Simulation.Create(in d);
            World w = sim.World;
            for (int k = 0; k < 6; k++)
            {
                VehicleHandle h = TestScenarios.PlaceCar(sim, w.Net.Segments[0].FirstLaneAB, 10f + (k * 20f), 3f);
                w.Vehicles.DesiredSpeed[h.Index] = 8f + k;
            }

            return sim;
        }

        using Simulation a = Make();
        using Simulation b = Make();
        Step(a, 200);
        Step(b, 200);
        Assert.Equal(Hash(a), Hash(b));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Step(a, 300);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Step(b, 300);
        Assert.Equal(Hash(a), Hash(b));
        Assert.True(a.World.Vehicles.AliveCount > 0);
    }
}
