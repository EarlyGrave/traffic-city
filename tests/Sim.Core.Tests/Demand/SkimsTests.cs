using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Demand;
using Sim.Core.Tests.City;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.Demand;

/// <summary>Скимы Z3 (zoning-demand.md §5, §19 «Скимы и выбор»): EWMA по периодам, порции обновления, априорная оценка, сейв посередине обновления, 0 B/тик.</summary>
public sealed class SkimsTests
{
    private const int DayTicks = 576;          // 2 слота = 1 период... 576 / 6 периодов = 96 тиков

    private static ScenarioDesc Fixture(int updatePerTick = 256)
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Calendar.DayTicks = DayTicks;
        d.Town.Choice.SkimUpdatePerTick = updatePerTick;
        return d;
    }

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static void AtPeriodStart(World w, long day, int period) => w.Tick = (day * DayTicks) + (period * (DayTicks / 6));

    [Fact]
    public void Update_FirstValueIsTheMean_ThenEwmaFollowsIntegerRule()
    {
        ScenarioDesc d = Fixture();
        using Simulation sim = Make(d);
        World w = sim.World;
        Skims s = w.City!.Skims;
        const int o = 1, dd = 2, p = 3;
        int key = s.KeyOf(o, dd, TravelMode.Car, p);

        // День 0: три наблюдения 900/1000/1100 в периоде 3 -> после границы периода ским = 1000.
        AtPeriodStart(w, 0, p);
        s.Advance(w);
        Assert.True(s.Observe(o, dd, TravelMode.Car, p, 900));
        Assert.True(s.Observe(o, dd, TravelMode.Car, p, 1000));
        Assert.True(s.Observe(o, dd, TravelMode.Car, p, 1100));
        AtPeriodStart(w, 0, p + 1);
        s.Advance(w);
        Assert.Equal(1000, s.Skim[key]);
        Assert.Equal(1, s.FrozenN);
        Assert.Equal(1, w.Counters.SkimUpdates);

        // Постоянное наблюдение 2000 каждый день в том же периоде: ским следует целочисленному EWMA с lambda = 1/4 и приходит к 2000 за ≈ 4 дня на 70%+.
        int reference = 1000;
        int shift = w.City.Cfg.Choice.SkimShift;
        for (int day = 1; day <= 12; day++)
        {
            AtPeriodStart(w, day, p);
            s.Advance(w);
            s.Observe(o, dd, TravelMode.Car, p, 2000);
            AtPeriodStart(w, day, p + 1);
            s.Advance(w);
            reference += (2000 - reference) >> shift;
            Assert.Equal(reference, s.Skim[key]);
            if (day == 4)
            {
                Assert.True(s.Skim[key] >= 1000 + (long)(0.65 * 1000), $"за 4 периода ским дошёл только до {s.Skim[key]}");
            }
        }

        Assert.InRange(s.Skim[key], 1960, 2000);
    }

    [Fact]
    public void Update_IsAppliedInPortions_InAscendingKeyOrder()
    {
        ScenarioDesc d = Fixture(updatePerTick: 3);
        using Simulation sim = Make(d);
        World w = sim.World;
        Skims s = w.City!.Skims;
        AtPeriodStart(w, 0, 1);
        s.Advance(w);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(s.Observe(i % 3, (i / 3) % 3, TravelMode.Walk, 1, 500 + i));
        }

        int distinct = s.Current.Count;
        AtPeriodStart(w, 0, 2);
        int n1 = s.Advance(w);
        Assert.Equal(3, n1);
        Assert.Equal(distinct, s.FrozenN);
        Assert.Equal(3, s.UpdateCursor);
        for (int i = 1; i < s.FrozenN; i++)
        {
            Assert.True(s.FrozenKey[i] > s.FrozenKey[i - 1], "слепок не отсортирован");
        }

        Assert.Equal(0, s.Current.Count);
        int total = n1;
        while (s.UpdateCursor < s.FrozenN)
        {
            total += s.Advance(w);
        }

        Assert.Equal(distinct, total);
        Assert.Equal(total, w.Counters.SkimUpdates);
    }

    [Fact]
    public void Update_DoesNotDependOnObservationOrder()
    {
        static ulong Run(bool reverse)
        {
            using Simulation sim = Make(Fixture());
            World w = sim.World;
            Skims s = w.City!.Skims;
            AtPeriodStart(w, 0, 0);
            s.Advance(w);
            for (int i = 0; i < 40; i++)
            {
                int j = reverse ? 39 - i : i;
                s.Observe(j % 4, (j / 4) % 4, (TravelMode)(j % 3), 0, 300 + (j * 17));
            }

            AtPeriodStart(w, 0, 1);
            s.Advance(w);
            return sim.ComputeStateHash();
        }

        Assert.Equal(Run(false), Run(true));
    }

    [Fact]
    public void Update_SkippedPeriods_StillFreezeOnce()
    {
        using Simulation sim = Make(Fixture());
        World w = sim.World;
        Skims s = w.City!.Skims;
        AtPeriodStart(w, 0, 0);
        s.Advance(w);
        s.Observe(0, 0, TravelMode.Walk, 0, 400);
        AtPeriodStart(w, 3, 2);       // прыжок через несколько суток (агрегатное исполнение)
        s.Advance(w);
        Assert.Equal(400, s.Skim[s.KeyOf(0, 0, TravelMode.Walk, 0)]);
        Assert.Equal(s.AbsolutePeriodOf(w.Tick, DayTicks), s.CurPeriod);
    }

    [Fact]
    public void Prior_IsUsedWithoutObservations_AndUnavailableModesAreZero()
    {
        using Simulation sim = Make(Fixture());
        World w = sim.World;
        Skims s = w.City!.Skims;
        int nc = s.Cells;
        int carPairs = 0;
        for (int o = 0; o < nc; o++)
        {
            for (int d = 0; d < nc; d++)
            {
                int car = s.Get(o, d, TravelMode.Car, 0);
                int walk = s.Get(o, d, TravelMode.Walk, 0);
                int bus = s.Get(o, d, TravelMode.Bus, 0);
                Assert.True(walk > 0);
                Assert.True(car >= 0 && bus >= 0);
                carPairs += car > 0 ? 1 : 0;
                if (car > 0)
                {
                    Assert.True(car >= 600, "в машинной оценке есть 60 с накладных");
                }

                if (o == d)
                {
                    Assert.Equal(0, bus);   // внутри ячейки автобус не берём
                }
            }
        }

        Assert.True(carPairs > 0);

        // Дальше - дороже; пара без улиц у ячеек - 0; внутри ячейки (cell_m / 3) дешевле, чем через ячейку.
        int c0 = 0;
        for (int d = 1; d < nc; d++)
        {
            if (s.Get(c0, c0, TravelMode.Walk, 0) > 0 && s.Get(c0, d, TravelMode.Walk, 0) > 0)
            {
                Assert.True(s.Get(c0, d, TravelMode.Walk, 0) > s.Get(c0, c0, TravelMode.Walk, 0));
            }
        }

        // Наблюдение вытесняет оценку.
        int key = s.KeyOf(0, 1, TravelMode.Walk, 0);
        s.Skim[key] = 77;
        Assert.Equal(77, s.Get(0, 1, TravelMode.Walk, 0));
    }

    [Fact]
    public void SaveLoad_InTheMiddleOfAnUpdate_ContinuesIdentically()
    {
        ScenarioDesc d = Fixture(updatePerTick: 2);
        using Simulation a = Make(d);
        World w = a.World;
        Skims s = w.City!.Skims;
        AtPeriodStart(w, 0, 0);
        s.Advance(w);
        for (int i = 0; i < 9; i++)
        {
            s.Observe(i % 3, i / 3, TravelMode.Car, 0, 800 + (i * 31));
        }

        AtPeriodStart(w, 0, 1);
        s.Advance(w);       // 2 из 9 применены
        s.Observe(1, 1, TravelMode.Bus, 1, 1234);
        Assert.True(s.UpdateCursor > 0 && s.UpdateCursor < s.FrozenN);
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(bytes);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.True(bytes.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(b)));

        for (int i = 0; i < 6; i++)
        {
            a.World.City!.Skims.Advance(a.World);
            b.World.City!.Skims.Advance(b.World);
        }

        AtPeriodStart(b.World, 0, 2);
        AtPeriodStart(a.World, 0, 2);
        a.World.City!.Skims.Advance(a.World);
        b.World.City!.Skims.Advance(b.World);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void ObserveAndAdvance_AllocateZeroBytes_InSteadyState()
    {
        using Simulation sim = Make(Fixture());
        World w = sim.World;
        Skims s = w.City!.Skims;
        long day = 0;
        long bytes = Fixtures.AllocationProbe.Measure(() =>
        {
            for (int p = 0; p < 6; p++)
            {
                AtPeriodStart(w, day, p);
                s.Advance(w);
                for (int i = 0; i < 50; i++)
                {
                    s.Observe(i % 5, (i / 5) % 5, (TravelMode)(i % 3), p, 400 + i);
                }
            }

            day++;
        });
        Assert.Equal(0, bytes);
    }
}
