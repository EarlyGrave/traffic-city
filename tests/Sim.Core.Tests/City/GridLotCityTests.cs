using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Xunit.Abstractions;

namespace Sim.Core.Tests.City;

/// <summary>RP5: город-генератор s1k-res по профилям фикстуры - участки на сетке, детерминизм при 1/2/3 потоках, сейв, 0 B/тик, согласованность с парковкой.</summary>
public sealed class GridLotCityTests
{
    private readonly ITestOutputHelper _out;

    public GridLotCityTests(ITestOutputHelper output) => _out = output;

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    [Fact]
    public void S1kRes_Profiles_CutsGridLots_NoOverlap_AndComparesToLegacy()
    {
        ScenarioDesc legacy = CityFixtures.S1kResPeds();
        ScenarioDesc prof = ProfileSaveFixtures.Res().Desc;
        using Simulation a = Make(legacy);
        using Simulation b = Make(prof);
        LotStore lb = b.World.City!.Lots;
        _out.WriteLine($"s1k-res: legacy lots {a.World.City!.Lots.Count}, profile (grid) lots {lb.Count}");
        Assert.True(lb.Count > 500);
        var hist = new int[9, 7];
        for (int i = 0; i < lb.Count; i++)
        {
            hist[lb.CellsF[i], lb.CellsD[i]]++;
        }

        for (int f = 2; f <= 8; f++)
        {
            _out.WriteLine($"F={f}: D2..6 = {hist[f, 2]},{hist[f, 3]},{hist[f, 4]},{hist[f, 5]},{hist[f, 6]}");
        }

        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < lb.Count; i++)
        {
            Assert.NotEqual(LotOrient.Legacy, lb.Orient[i]);
            Assert.Equal(0, lb.Flags[i] & LotFlags.Lost);
            Assert.True(lb.RoadComp[i] >= 0);
            LotBuilder.CellBounds(lb, i, out int x0, out int z0, out int x1, out int z1);
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Assert.True(seen.Add((x, z)), $"клетка ({x},{z}) участка {i} занята дважды");
                }
            }
        }
    }

    [Fact]
    public void S1kRes_Profiles_HashIsStableAcrossThreads()
    {
        ScenarioDesc d = ProfileSaveFixtures.Res().Desc;
        ulong h1;
        using (Simulation s = Make(d, 1))
        {
            s.Step(300);
            h1 = s.ComputeStateHash();
        }

        foreach (int threads in new[] { 2, 3 })
        {
            using Simulation s = Make(d, threads);
            s.Step(300);
            Assert.Equal(h1, s.ComputeStateHash());
        }
    }

    [Fact]
    public void S1kRes_Profiles_SaveLoad_KeepsLotsAndDerived()
    {
        ScenarioDesc d = ProfileSaveFixtures.Res().Desc;
        using Simulation a = Make(d);
        a.Step(200);
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(bytes);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        LotStore la = a.World.City!.Lots;
        LotStore lb = b.World.City!.Lots;
        Assert.Equal(la.Count, lb.Count);
        int n = la.Count;
        Assert.True(la.CellX.AsSpan(0, n).SequenceEqual(lb.CellX.AsSpan(0, n)));
        Assert.True(la.CellZ.AsSpan(0, n).SequenceEqual(lb.CellZ.AsSpan(0, n)));
        Assert.True(la.Orient.AsSpan(0, n).SequenceEqual(lb.Orient.AsSpan(0, n)));
        Assert.True(la.X.AsSpan(0, n).SequenceEqual(lb.X.AsSpan(0, n)));
        Assert.True(la.Heading.AsSpan(0, n).SequenceEqual(lb.Heading.AsSpan(0, n)));
        Assert.True(la.CarS.AsSpan(0, n).SequenceEqual(lb.CarS.AsSpan(0, n)));
        Assert.True(la.PedSMm.AsSpan(0, n).SequenceEqual(lb.PedSMm.AsSpan(0, n)));
        a.Step(100);
        b.Step(100);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void S1kRes_Profiles_SteadyState_AllocatesNothing()
    {
        ScenarioDesc d = ProfileSaveFixtures.Res().Desc;
        using Simulation sim = Make(d);
        sim.Step(400);
        Assert.Equal(0, AllocationProbe.Measure(() => sim.Step(100), out _));
    }

    [Fact]
    public void S1kResPark_Profiles_ParkingFollowsGridLots_AndSurvivesLoad()
    {
        ScenarioDesc d = ProfileSaveFixtures.Res("s1k-res-park").Desc;
        using Simulation a = Make(d);
        Assert.NotNull(a.World.City!.Parking);
        Assert.True(a.World.City.Lots.Count > 500);
        a.Step(200);
        using Simulation b = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(a));
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        var pa = a.World.City.Parking!;
        var pb = b.World.City!.Parking!;
        Assert.Equal(pa.HighWater, pb.HighWater);
        Assert.True(pa.X.AsSpan(0, pa.HighWater).SequenceEqual(pb.X.AsSpan(0, pb.HighWater)), "позиции объектов стоянки после загрузки совпадают");
        Assert.True(pa.AccessS.AsSpan(0, pa.HighWater).SequenceEqual(pb.AccessS.AsSpan(0, pb.HighWater)));
    }
}
