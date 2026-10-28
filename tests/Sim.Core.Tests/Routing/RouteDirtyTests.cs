using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Routing;

/// <summary>ADR-0016, D7: <c>AssignConnectors</c> работает только по флагу <c>RouteDirty</c>; сеть/узлы помечают всех. Полную сверку делает Debug.Assert в самой фазе.</summary>
public class RouteDirtyTests
{
    private static long Checks(Simulation sim) => sim.Work.RouteRefillChecks;

    [Fact]
    public void SteadyState_ChecksOnlyChangedVehicles()
    {
        ScenarioDesc d = Step2.S1k(buses: true);
        using Simulation sim = Simulation.Create(in d);
        sim.Step(1200);
        long before = Checks(sim);
        sim.Step(20);
        long perTick = (Checks(sim) - before) / 20;
        int alive = sim.World.Vehicles.Pool.AliveCount;
        Assert.True(alive > 500);
        Assert.True(perTick < alive / 4, $"checks per tick {perTick} of {alive} alive");
    }

    [Fact]
    public void BuildRoad_MarksAllVehicles()
    {
        ScenarioDesc d = Step2.S1k(buses: true);
        using Simulation sim = Simulation.Create(in d);
        sim.SetToolsConfig(Tools.ToolsFixture.Unbounded);
        sim.Step(1200);
        var r = new BuildRoadCmd { Ax = 9000, Az = 9000, Bx = 9200, Bz = 9000, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street, Flags = 1 };
        Assert.True(sim.Enqueue(Command.MakeBuildRoad(in r)));
        long before = Checks(sim);
        sim.Step(1);
        long checks = Checks(sim) - before;
        int onLane = 0;
        for (int i = 0; i < sim.World.Vehicles.HighWater; i++)
        {
            if (sim.World.Vehicles.Lane[i] >= 0 && sim.World.Vehicles.Pool.IsAlive(i))
            {
                onLane++;
            }
        }

        Assert.True(checks >= onLane * 9 / 10, $"checks {checks} of {onLane} on lanes");
    }
}
