using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Sim.Scenarios;

namespace Sim.Core.Tests.Api;

/// <summary>
/// <c>CopyVisibleVehicles</c>/<c>CopyVisiblePeds</c> (ADR-0024 п. 3, scale.md S8): копия = фильтр снапшота по <c>Flags != 0</c> с prev по слоту,
/// нехватка ёмкости, вид, 0 B, хэш состояния не меняется. Коллекция без параллельности: замеры аллокаций.
/// </summary>
[Collection("WorkerThreads")]
public class CopyVisibleTests
{
    private static void AssertVehiclesCopy(Simulation sim)
    {
        ReadOnlySpan<VehicleRenderData> c = sim.Vehicles;
        ReadOnlySpan<VehicleRenderData> p = sim.PreviousVehicles;
        var cur = new VehicleRenderData[c.Length + 1];
        var prev = new VehicleRenderData[c.Length + 1];
        Assert.True(sim.CopyVisibleVehicles(cur, prev, out int n));
        int k = 0;
        for (int i = 0; i < c.Length; i++)
        {
            if (c[i].Flags == 0)
            {
                continue;
            }

            Assert.Equal(c[i], cur[k]);
            Assert.Equal(i < p.Length ? p[i] : default, prev[k]);
            Assert.Equal(i, cur[k].Index);
            k++;
        }

        Assert.Equal(k, n);
    }

    [Fact]
    public void Vehicles_CopyEqualsFilteredSnapshot_ForAllAndRectView_AndDoesNotChangeHash()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        using Simulation twin = Simulation.Create(in d);
        sim.Step(300);
        twin.Step(300);
        AssertVehiclesCopy(sim);
        Assert.True(sim.Vehicles.Length > 0);

        NetworkView net = sim.Network;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (NodeData nd in net.Nodes)
        {
            minX = Math.Min(minX, nd.Position.X);
            maxX = Math.Max(maxX, nd.Position.X);
            minZ = Math.Min(minZ, nd.Position.Z);
            maxZ = Math.Max(maxZ, nd.Position.Z);
        }

        int v0 = sim.ViewVersion;
        sim.SetRenderView(minX, minZ, (minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
        Assert.Equal(v0 + 1, sim.ViewVersion);
        sim.Step(2);
        twin.Step(2);
        AssertVehiclesCopy(sim);

        sim.SetRenderView(1e9f, 1e9f, 1e9f + 1f, 1e9f + 1f); // пустой вид
        sim.Step(1);
        twin.Step(1);
        var one = new VehicleRenderData[1];
        Assert.True(sim.CopyVisibleVehicles(one, one, out int none));
        Assert.Equal(0, none);

        sim.SetRenderViewAll();
        sim.Step(1);
        twin.Step(1);
        AssertVehiclesCopy(sim);
        Assert.Equal(twin.ComputeStateHash(), sim.ComputeStateHash());
    }

    [Fact]
    public void Vehicles_CapacityTooSmall_ReturnsFalseWithNeededCount()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        sim.Step(900);
        var big = new VehicleRenderData[sim.Vehicles.Length + 1];
        var bigPrev = new VehicleRenderData[big.Length];
        Assert.True(sim.CopyVisibleVehicles(big, bigPrev, out int need));
        Assert.True(need >= 2);

        var cur = new VehicleRenderData[1];
        var prev = new VehicleRenderData[1];
        Assert.False(sim.CopyVisibleVehicles(cur, prev, out int n));
        Assert.Equal(need, n);
        Assert.Equal(big[0], cur[0]);
    }

    [Fact]
    public void Vehicles_Copy_AllocatesNothing()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        sim.Step(300);
        var cur = new VehicleRenderData[sim.Vehicles.Length + 1];
        var prev = new VehicleRenderData[cur.Length];
        long bytes = AllocationProbe.Measure(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                sim.CopyVisibleVehicles(cur, prev, out _);
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Peds_CopyEqualsFilteredSnapshot_AndAllocatesNothing()
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(System.IO.Path.Combine(Gen.GenFixture.RepoRoot(), "scenarios", "s1k-peds.json"));
        using Simulation sim = Simulation.Create(sc.Desc);
        sim.Step(1500);
        ReadOnlySpan<PedestrianRenderData> c = sim.Pedestrians;
        ReadOnlySpan<PedestrianRenderData> p = sim.PreviousPedestrians;
        var cur = new PedestrianRenderData[c.Length + 1];
        var prev = new PedestrianRenderData[c.Length + 1];
        Assert.True(sim.CopyVisiblePeds(cur, prev, out int n));
        int k = 0;
        for (int i = 0; i < c.Length; i++)
        {
            if (c[i].Flags == 0)
            {
                continue;
            }

            Assert.Equal(c[i], cur[k]);
            Assert.Equal(i < p.Length ? p[i] : default, prev[k]);
            k++;
        }

        Assert.Equal(k, n);
        Assert.True(n > 0);

        long bytes = AllocationProbe.Measure(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                sim.CopyVisiblePeds(cur, prev, out _);
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Peds_WithoutPedSection_CopiesNothing()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        sim.Step(5);
        var buf = new PedestrianRenderData[4];
        Assert.True(sim.CopyVisiblePeds(buf, buf, out int n));
        Assert.Equal(0, n);
    }
}
