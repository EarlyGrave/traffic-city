using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Save;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Host;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// Показатели города (PG2, progression.md §2): кольцо 24 игровых часов из закрытых ног и счётчиков движения - Σ ног кольца за сутки = <c>LegsDay</c> вчера (micro и aggregate с прыжками),
/// Σ машино-тиков за сутки = счётчику, суточное кольцо баланса, дни до лимита, хэш при 1/2/3 потоках, сейв посреди часа, миграция w10 (пустое кольцо), 0 байт на тик.
/// </summary>
public sealed class CityKpiTests
{
    private const int MicroDay = 28_800;   // час = 1 200 тиков
    private const int Day = 72_000;

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static ScenarioDesc Econ(bool aggregate = false, bool progression = true)
    {
        ScenarioDesc d = ProgressionFixtures.Econ(aggregate ? 0 : MicroDay, progression ? ProgressionFixtures.Section : """{ "enabled": false }""").Desc;
        if (aggregate)
        {
            d.Town.Residents.TripExecution = TripExecution.Aggregate;
        }

        return d;
    }

    private static KpiHour SumRing(Simulation sim, out int n)
    {
        var hours = new KpiHour[CityKpi.Hours];
        n = sim.CopyKpiHours(hours);
        KpiHour s = default;
        for (int i = 0; i < n; i++)
        {
            s.LegsCar += hours[i].LegsCar;
            s.LegsBus += hours[i].LegsBus;
            s.LegsWalk += hours[i].LegsWalk;
            s.VehTicks += hours[i].VehTicks;
            s.MovingTicks += hours[i].MovingTicks;
        }

        return s;
    }

    private static void AssertRingIsYesterday(Simulation sim)
    {
        Assert.True(sim.GetCityStats(out CityStats st));
        KpiHour s = SumRing(sim, out int n);
        Assert.Equal(CityKpi.Hours, n);
        Assert.Equal(st.LegsYesterdayCar, s.LegsCar);
        Assert.Equal(st.LegsYesterdayBus, s.LegsBus);
        Assert.Equal(st.LegsYesterdayWalk, s.LegsWalk);
        Assert.True(s.LegsAll > 0);
    }

    [Fact]
    public void Micro_RingOfDay_EqualsLegsDay_AndVehicleTicks()
    {
        using Simulation sim = Make(Econ());
        sim.Step(MicroDay);   // последний исполненный тик - конец суток 0: кольцо = сутки 0 целиком
        Assert.Equal(1, sim.CityDay);
        AssertRingIsYesterday(sim);
        KpiHour s = SumRing(sim, out _);
        Assert.Equal(sim.Counters.VehicleTicksTotal, s.VehTicks);
        Assert.Equal(sim.Counters.MovingVehicleTicksTotal, s.MovingTicks);

        sim.Step(MicroDay / 48);   // полчаса следующих суток: кольцо то же
        AssertRingIsYesterday(sim);
        Assert.True(sim.GetCityKpi(out CityKpiInfo k));
        Assert.Equal(CityKpi.Hours, k.HoursFilled);
        Assert.Equal(MicroDay / 48, k.CurrentHourTicks);
        Assert.InRange(k.TravelMinX10, 1, 100_000);
        Assert.InRange(k.IdlePermille, 0, 1000);
        Assert.InRange(k.SatisfactionPermille, 0, 1000);
        Assert.InRange(k.BusSharePermille, 0, 1000);
        Assert.Equal(1, k.HasBudget);
        Assert.Equal(sim.CityPopulation, k.Population);
        Assert.Equal(1, k.DaysFilled);
        var days = new long[CityKpi.Days];
        Assert.Equal(1, sim.CopyKpiDays(days));
    }

    [Fact]
    public void Aggregate_WithJumps_RollsEveryHour_AndMatchesLegsDay()
    {
        using Simulation sim = Make(Econ(aggregate: true, progression: false));
        sim.Step(Day);
        Assert.True(sim.CityJumpedTicks > 0);   // прыжки остались, но тики сдвига часа исполнены
        AssertRingIsYesterday(sim);
        sim.Step(Day);
        AssertRingIsYesterday(sim);
        var days = new long[CityKpi.Days];
        Assert.Equal(2, sim.CopyKpiDays(days));
        Assert.Equal(sim.World.City!.Ledger.City, days[1]);   // граница суток - последний исполненный тик
    }

    [Fact]
    public void Aggregate_JumpsEqualSteps_WithKpi()
    {
        using Simulation a = Make(Econ(aggregate: true));
        using Simulation b = Make(Econ(aggregate: true));
        b.World.DisableJump = true;
        a.Step(Day + 5000);
        b.Step(Day + 5000);
        Assert.Equal(b.ComputeStateHash(), a.ComputeStateHash());
    }

    [Fact]
    public void Hash_IsThreadIndependent_WithProgression()
    {
        ulong reference = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Make(Econ(), threads);
            sim.Step(3700);   // три сдвига часа
            Assert.Equal(3, sim.World.City!.Kpi.HoursFilled);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                reference = h;
            }

            Assert.Equal(reference, h);
        }
    }

    [Fact]
    public void Save_MidHour_ContinuesBitIdentical()
    {
        using Simulation sim = Make(Econ());
        sim.Step(1800);   // посреди второго часа
        byte[] raw = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(raw);
        Assert.True(raw.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(loaded)));
        sim.Step(1300);   // через сдвиг часа (тик 2 399)
        loaded.Step(1300);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        var x = new KpiHour[CityKpi.Hours];
        var y = new KpiHour[CityKpi.Hours];
        Assert.Equal(2, sim.CopyKpiHours(x));
        Assert.Equal(2, loaded.CopyKpiHours(y));
        Assert.True(x.AsSpan().SequenceEqual(y));
        Assert.True(WorldSaveFixtures.SaveBytes(sim).AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(loaded)));
    }

    [Fact]
    public void Step_OnEconScenario_WithKpi_AllocatesZeroBytes()
    {
        using Simulation sim = Make(Econ());
        sim.Step(1000);
        long bytes = AllocationProbe.Measure(() => sim.Step(1500), out _);   // сдвиги часа на тиках 1 199 и 2 399 внутри окна
        Assert.Equal(0, bytes);
        var hours = new KpiHour[CityKpi.Hours];
        var days = new long[CityKpi.Days];
        long read = AllocationProbe.Measure(() =>
        {
            sim.GetCityKpi(out _);
            sim.GetProgression(out _);
            sim.CopyKpiHours(hours);
            sim.CopyKpiDays(days);
        });
        Assert.Equal(0, read);
    }

    [Fact]
    public void DaysToLimit_FollowsTrend()
    {
        using Simulation sim = Make(Econ());
        CityWorld c = sim.World.City!;
        CityBudget b = c.Budget!;
        b.IncomeEwma = 1000;
        b.ExpenseEwma = 900;
        Assert.Equal(-1, CityKpi.DaysToLimit(c));   // тренд >= 0
        b.ExpenseEwma = 1300;   // дефицит 300 в сутки
        long room = c.Ledger.City + b.CreditLimit(in c.Cfg.Economy);
        Assert.True(room > 0);
        Assert.Equal((int)((room + 299) / 300), CityKpi.DaysToLimit(c));
        Assert.True(sim.GetCityKpi(out CityKpiInfo k));
        Assert.Equal(-300, k.TrendPerDay);
        Assert.Equal(CityKpi.DaysToLimit(c), k.DaysToLimit);
        b.IncomeEwma = 0;
        b.ExpenseEwma = 1;
        Assert.Equal(c.Ledger.City <= 0 ? 0 : (int)Math.Min(int.MaxValue, c.Ledger.City), CityKpi.DaysToLimit(c));
    }

    [Fact]
    public void HourBoundaries_AlignWithDays()
    {
        foreach (int d in new[] { 72_000, 28_800, 1_000, 7 })
        {
            int rolls = 0;
            for (long t = 0; t < 2L * d; t++)
            {
                if (CityKpi.IsHourEnd(t, d))
                {
                    rolls++;
                    Assert.Equal(t, CityKpi.NextHourEnd(t, d));
                }
                else
                {
                    Assert.True(CityKpi.NextHourEnd(t, d) > t);
                }
            }

            Assert.True(CityKpi.IsHourEnd(d - 1, d));
            Assert.Equal(d >= 24 ? 48 : rolls, rolls);
        }
    }

    [Fact]
    public void MigratedW10_StartsWithEmptyRing_AndCollects()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SaveCorpus", "w10-s1k-res-econ-d3-t216500.tcsave");
        byte[] raw = SaveEnvelope.ReadContainer(path);
        var opt = new LoadOptions { StrictHash = 0 };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
        using (sim!)
        {
            Assert.True(rep.MigrationsApplied >= 1);
            Assert.False(sim!.HasProgression);
            Assert.True(sim.GetCityKpi(out CityKpiInfo k));
            Assert.Equal(0, k.HoursFilled);
            Assert.Equal(0, k.DaysFilled);
            Assert.Equal(sim.Counters.VehicleTicksTotal, sim.World.City!.Kpi.VehAtHourStart);
            sim.Step(3000);   // час s1k-res-econ = 3 000 тиков: хотя бы один сдвиг
            Assert.True(sim.GetCityKpi(out k));
            Assert.Equal(1, k.HoursFilled);
        }
    }
}
