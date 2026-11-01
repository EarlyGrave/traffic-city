using System;
using System.IO;
using System.Linq;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>Рельеф-заготовка Юрьева-Польского 10×10 км (задача YS3): SRTM, продлённая Колокша, вал, без города.</summary>
public class Yuryev10kTerrainScenarioTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string ScenarioPath()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "traffic-city.sln")))
            {
                return Path.Combine(d.FullName, "scenarios", "yuryev-10k-terrain.json");
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("traffic-city.sln not found");
    }

    private static Simulation Create()
    {
        LoadedScenario s = ScenarioLoader.LoadInfo(ScenarioPath());
        ScenarioDesc desc = s.Desc;
        return Simulation.Create(in desc);
    }

    [Fact]
    public void Loads_IsDeterministic_AndArterialFitsGradeLimit()
    {
        using Simulation a = Create();
        using Simulation b = Create();
        Assert.True(a.HasTerrain);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.ComputeNetworkHash(), b.ComputeNetworkHash());

        NetworkView n = a.Network;
        Assert.Equal(36, n.Segments.Length);
        // продлённая Колокша теперь пересекает южную часть трассы: мосты допустимы (auto_bridge)
        output.WriteLine($"bridges={n.Segments.ToArray().Count(s => (s.Flags & SegmentFlags.Bridge) != 0)}");
        output.WriteLine($"maxgrade={n.LaneGrade.ToArray().Max(g => Math.Abs(g)):F4} len={n.Segments.ToArray().Sum(x => x.Length):F0} minY={n.Nodes.ToArray().Min(x => x.Elevation):F2} maxY={n.Nodes.ToArray().Max(x => x.Elevation):F2}");
        // артерия: предел уклона 6% (terrain.max_grade_pct.arterial)
        Assert.True(n.LaneGrade.ToArray().All(g => Math.Abs(g) <= 0.06f));
    }
}
