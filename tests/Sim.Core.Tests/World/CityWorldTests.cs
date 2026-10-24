using System;
using System.Diagnostics;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Worlds;

public class CityWorldTests
{
    private readonly ITestOutputHelper _out;

    public CityWorldTests(ITestOutputHelper output) => _out = output;

    internal static ScenarioDesc Desc(string scenario)
    {
        GenRun r = GenFixture.Load(scenario);
        SimCapacities c = r.Caps;
        c.MaxVehicles = 1024;
        c.MaxRoutes = 64;
        c.MaxPendingTrips = 64;
        c.CommandQueueCapacity = 64;
        c.EventCapacity = 256;
        VehicleTypeDesc car = TestScenarios.Car();
        VehicleTypeDesc bus = car;
        bus.Category = VehicleCategory.Bus;
        bus.Length = 12f;
        return new ScenarioDesc
        {
            Seed = r.Seed,
            DurationTicks = 1000,
            Capacities = c,
            Routing = new RoutingDesc { PathfindBudgetPerTick = 4 },
            City = r.City,
            Demand = r.Demand,
            Transit = r.Transit,
            Rules = r.Rules,
            VehicleTypes = new[] { car, bus },
        };
    }

    [Theory]
    [InlineData("s1k", 438, 6252, 60)]
    [InlineData("s10k", -1, -1, -1)]
    public void Create_Ok_NetworkAndZonesVisible(string scenario, int nodes, int lanes, int zones)
    {
        ScenarioDesc d = Desc(scenario);
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? sim));
        using Simulation s = sim!;
        NetworkView v = s.Network;
        if (nodes >= 0)
        {
            Assert.Equal(nodes, v.Nodes.Length);
            Assert.Equal(lanes, v.Lanes.Length);
            Assert.Equal(zones, v.Zones.Length);
        }

        Assert.True(v.Nodes.Length > 0);
        int withSpawn = 0;
        for (int i = 0; i < v.Zones.Length; i++)
        {
            withSpawn += v.Zones[i].SpawnLaneCount > 0 ? 1 : 0;
        }

        Assert.True(withSpawn > 0);
        Assert.True(s.World.SignalCount > 0);
        Assert.Equal(s.World.SignalCount, s.World.SignalPlanSeeds.Length);
        Assert.Equal(s.World.SignalCount, s.Counters.SignalizedNodes);
        Assert.Equal(v.Zones.Length, s.Counters.ZoneCount);
        Assert.NotNull(s.World.Transit);
        Assert.Equal(s.World.Transit!.RouteCount, v.BusRoutes.Length);
        Assert.True(v.BusRoutes.Length > 0);
        Assert.Equal(s.World.Transit.StopCount, v.BusStops.Length);

        // Полосы спавна зоны - валидные полосы сети; зоны не пересекаются по полосам.
        var seen = new bool[v.Lanes.Length];
        for (int z = 0; z < v.Zones.Length; z++)
        {
            ZoneData zd = v.Zones[z];
            for (int k = 0; k < zd.SpawnLaneCount; k++)
            {
                int lane = s.World.Net.ZoneLanes[zd.FirstSpawnLane + k];
                Assert.InRange(lane, 0, v.Lanes.Length - 1);
                Assert.False(seen[lane]);
                seen[lane] = true;
            }
        }
    }

    [Fact]
    public void Create_Twice_SameHashes()
    {
        ScenarioDesc d = Desc("s1k");
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.World.Net.Hash(), b.World.Net.Hash());
        Assert.Equal(a.World.RngTransit.State, b.World.RngTransit.State);
        Assert.NotEqual(0UL, a.ComputeStateHash());

        ScenarioDesc d2 = d;
        d2.Seed = d.Seed + 1;
        using Simulation c = Simulation.Create(in d2);
        Assert.NotEqual(a.World.Net.Hash(), c.World.Net.Hash());
    }

    [Fact]
    public void Step200_NoVehicles_ZeroAllocations()
    {
        ScenarioDesc d = Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        sim.Step(50);
        long allocated = AllocationProbe.Measure(() => sim.Step(200), out int windows);
        Assert.Equal(0, allocated);
        Assert.Equal(50 + (200 * windows), sim.Tick);
    }

    [Fact]
    public void S100k_Create_FitsCapacities()
    {
        ScenarioDesc d = Desc("s100k");
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? sim));
        using Simulation s = sim!;
        Assert.True(s.Network.Zones.Length > 0);
    }

    [Fact]
    public void CreateTiming_Report()
    {
        foreach (string name in new[] { "s1k", "s10k", "s100k" })
        {
            ScenarioDesc d = Desc(name);
            using (Simulation warm = Simulation.Create(in d))
            {
            }

            long t0 = Stopwatch.GetTimestamp();
            using Simulation s = Simulation.Create(in d);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _out.WriteLine($"{name}: Create {ms:F1} ms, nodes={s.Network.Nodes.Length}, lanes={s.Network.Lanes.Length}, zones={s.Network.Zones.Length}, routes={s.Network.BusRoutes.Length}, signals={s.World.SignalCount}");
        }
    }
}
