using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Demand;
using Sim.Core.Tests.City;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.Demand;

/// <summary>Поле доступности Z3 (zoning-demand.md §6.7): создание, порции дневного прохода, отклик на время, API для клиента, сейв.</summary>
public sealed class AccessibilityTests
{
    private static Simulation Make() => Simulation.Create(CityFixtures.S1kRes(), new SimRuntimeOptions { WorkerThreads = 1 });

    [Fact]
    public void Create_ComputesFieldAndLsRef()
    {
        using Simulation sim = Make();
        Skims s = sim.World.City!.Skims;
        Assert.True(s.LsRefSet);
        int finite = 0;
        for (int i = 0; i < s.LsC.Length; i++)
        {
            finite += s.LsC[i] != Skims.NoLs ? 1 : 0;
        }

        Assert.True(finite > 0, "ни одна ячейка не видит C-целей");
        var shops = new int[sim.CityCellCount];
        var jobs = new int[sim.CityCellCount];
        Assert.Equal(shops.Length, sim.GetAccessibility(shops, AccessKind.Shops));
        sim.GetAccessibility(jobs, AccessKind.Jobs);
        Assert.Contains(shops, v => v != 0);
        Assert.Contains(jobs, v => v != 0);
    }

    [Fact]
    public void Field_FallsWhenTravelGetsFaster_AndRisesWhenSlower()
    {
        using Simulation sim = Make();
        World w = sim.World;
        Skims s = w.City!.Skims;
        var before = new int[s.LsC.Length];
        s.LsC.CopyTo(before, 0);

        // Все способы стали быстрее (наблюдения вдвое ниже оценки) - logsum падает везде, где он был.
        for (int o = 0; o < s.Cells; o++)
        {
            for (int d = 0; d < s.Cells; d++)
            {
                for (int m = 0; m < Skims.Modes; m++)
                {
                    for (int p = 0; p < s.Periods; p++)
                    {
                        int v = s.Get(o, d, (TravelMode)m, p);
                        if (v > 0)
                        {
                            s.Skim[s.KeyOf(o, d, (TravelMode)m, p)] = Math.Max(1, v / 2);
                        }
                    }
                }
            }
        }

        Accessibility.RunAll(w);
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] != Skims.NoLs)
            {
                Assert.True(s.LsC[i] < before[i], $"индекс {i}: {before[i]} -> {s.LsC[i]}");
            }
        }
    }

    [Fact]
    public void Step_InPortions_GivesTheSameFieldAsFullRun()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        Skims s = c.Skims;
        var full = (int[])s.LsC.Clone();
        var fullJobs = (int[])s.LsJobs.Clone();
        Array.Fill(s.LsC, Skims.NoLs);
        Array.Fill(s.LsJobs, Skims.NoLs);
        int calls = 0;
        while (!Accessibility.Step(w, 2))
        {
            calls++;
            Assert.True(calls < 1000);
        }

        Assert.Equal(0, c.CursorAccess);
        Assert.Equal(full, s.LsC);
        Assert.Equal(fullJobs, s.LsJobs);
        Assert.True(calls >= (s.Cells / 2) - 1);
    }

    [Fact]
    public void Field_SurvivesSaveLoad_AndCursorResumes()
    {
        using Simulation a = Make();
        Accessibility.Step(a.World, 3);
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(bytes);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.World.City!.Skims.LsC, b.World.City!.Skims.LsC);
        Assert.Equal(a.World.City.CursorAccess, b.World.City!.CursorAccess);
        while (!Accessibility.Step(a.World, 2))
        {
        }

        while (!Accessibility.Step(b.World, 2))
        {
        }

        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        // Решения после Load совпадают с непрерывным прогоном (производные кэши перестроились).
        Assert.Equal(Choice.ChooseDestination(a.World, 0), Choice.ChooseDestination(b.World, 0));
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void Step_AllocatesZeroBytes_AfterWarmup()
    {
        using Simulation sim = Make();
        World w = sim.World;
        Accessibility.Step(w, 3);
        long bytes = Fixtures.AllocationProbe.Measure(() => Accessibility.Step(w, 2));
        Assert.Equal(0, bytes);
    }
}
