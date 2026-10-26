using System;
using System.Linq;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Demand;
using Sim.Core.Tests.City;
using Sim.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Demand;

/// <summary>
/// Агрегат (Z5, zoning-demand.md §9, §19 «Агрегат»): прыжки = пошаговый проход, переключение micro/aggregate на границе суток, калибровка K, согласие с micro,
/// деньги, res_stuck, 0 B/тик, независимость от числа потоков.
/// </summary>
public sealed class AggregateTests
{
    private const int DayTicks = 11_520;       // 40 тиков в слоте, период скимов = 1 920 тиков

    private readonly ITestOutputHelper _out;

    public AggregateTests(ITestOutputHelper output) => _out = output;

    private static ScenarioDesc Fixture(bool peds)
    {
        ScenarioDesc d = peds ? CityFixtures.S1kResPeds() : CityFixtures.S1kRes();
        d.Town.Calendar.DayTicks = DayTicks;
        return d;
    }

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    /// <summary>Идёт micro до последнего тика суток <paramref name="days"/>, ставит команду и проходит границу: к следующему вызову <c>Step</c> режим уже переключён.</summary>
    private static void SwitchAtDay(Simulation sim, int day, TripExecution mode)
    {
        long target = ((long)day * City(sim).DayTicks) - 1;
        sim.Step((int)(target - sim.Tick));
        Assert.True(sim.Enqueue(Command.MakeSetTripExecution(mode)));
        sim.Step(1);
        Assert.Equal(day, City(sim).Day);
        Assert.Equal(mode, City(sim).Exec);
    }

    private static long MoneySum(Simulation sim) => City(sim).Ledger.SumAll();

    [Fact]
    public void Aggregate_JumpEqualsStep()
    {
        ScenarioDesc d = Fixture(peds: false);
        using Simulation a = Make(d);
        using Simulation b = Make(d);
        b.World.DisableJump = true;
        SwitchAtDay(a, 1, TripExecution.Aggregate);
        SwitchAtDay(b, 1, TripExecution.Aggregate);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        for (int k = 0; k < 4; k++)
        {
            // шаги разной длины: прыжок не должен зависеть от того, как нарезан Step
            a.Step((DayTicks / 2) + (k * 37));
            b.Step(DayTicks / 2);
            b.Step(k * 37);
            Assert.Equal(b.ComputeStateHash(), a.ComputeStateHash());
            a.Step(DayTicks / 2 - (k * 37));
            b.Step(DayTicks / 2 - (k * 37));
            Assert.Equal(b.ComputeStateHash(), a.ComputeStateHash());
        }

        Assert.True(a.World.JumpedTicks > 0, "прыжков не было");
        Assert.Equal(0, b.World.JumpedTicks);
        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(0, MoneySum(a));
        Assert.Equal(0, a.Counters.ResStuck);
        _out.WriteLine($"jumped {a.World.JumpedTicks} of {a.Tick} ticks, legs aggregate {a.Counters.AggregateLegs}, res_stuck {a.Counters.ResStuck}");
    }

    [Fact]
    public void Switch_WaitsForTheDayBoundary_AndCalibratesK()
    {
        ScenarioDesc d = Fixture(peds: false);
        using Simulation sim = Make(d);
        sim.Step(DayTicks / 2);
        Assert.True(sim.Enqueue(Command.MakeSetTripExecution(TripExecution.Aggregate)));
        sim.Step(10);
        Assert.Equal(TripExecution.Agents, City(sim).Exec);
        Assert.Equal((int)TripExecution.Aggregate, City(sim).ExecPending);
        sim.Step(DayTicks - (DayTicks / 2) - 10);
        Assert.Equal(TripExecution.Aggregate, City(sim).Exec);
        Assert.Equal(-1, City(sim).ExecPending);
        Skims s = City(sim).Skims;
        int calibrated = 0;
        for (int i = 0; i < s.K.Length; i++)
        {
            if (s.K[i] != 0)
            {
                Assert.InRange(s.K[i], 128, 1024);
                calibrated++;
            }
        }

        Assert.True(calibrated > 0, "K не откалибровано");
        // повторная команда того же режима - отмена: ничего не отложено
        Assert.True(sim.Enqueue(Command.MakeSetTripExecution(TripExecution.Aggregate)));
        sim.Step(1);
        Assert.Equal(-1, City(sim).ExecPending);
        // неверный режим отклоняется
        var bad = Command.MakeSetTripExecution((TripExecution)7);
        Assert.True(sim.Enqueue(bad));
        sim.Step(1);
        Assert.Equal(-1, City(sim).ExecPending);
        _out.WriteLine($"K entries {calibrated} of {s.K.Length}");
    }

    [Fact]
    public void Aggregate_RoundTrip_KeepsInvariants()
    {
        ScenarioDesc d = Fixture(peds: false);
        using Simulation sim = Make(d);
        SwitchAtDay(sim, 1, TripExecution.Aggregate);
        sim.Step(2 * DayTicks);
        Assert.True(sim.Counters.AggregateLegs > 0);
        SwitchAtDay(sim, 4, TripExecution.Agents);
        sim.Step(2 * DayTicks);
        SwitchAtDay(sim, 7, TripExecution.Aggregate);
        sim.Step(DayTicks);
        Assert.Equal(0, MoneySum(sim));
        Assert.Equal(0, sim.Counters.ResStuck);
        int alive = City(sim).Residents.AliveCount;
        Assert.True(alive > 0);
        sim.GetCityStats(out CityStats st);
        Assert.Equal(0, st.BalanceResidents + st.BalanceFirms + st.BalanceExternal + st.BalanceCity);
    }

    [Fact]
    public void Aggregate_SaveLoad_ContinuesTheSame()
    {
        ScenarioDesc d = Fixture(peds: false);
        using Simulation a = Make(d);
        SwitchAtDay(a, 1, TripExecution.Aggregate);
        a.Step((DayTicks / 3) + 5);   // в пути есть ноги агрегата
        long size = a.MeasureSave();
        byte[] buf = new byte[size];
        Assert.Equal(SaveStatus.Ok, a.Save(buf, out long written));
        Assert.Equal(size, written);
        var o = new LoadOptions { StrictHash = 1 };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(buf.AsSpan(0, (int)written), in o, default, out Simulation? b, out _));
        using (b)
        {
            Assert.Equal(a.ComputeStateHash(), b!.ComputeStateHash());
            a.Step(DayTicks);
            b.Step(DayTicks);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Aggregate_Hash_DoesNotDependOnThreads(int threads)
    {
        ScenarioDesc d = Fixture(peds: false);
        using Simulation one = Make(d, 1);
        using Simulation many = Make(d, threads);
        SwitchAtDay(one, 1, TripExecution.Aggregate);
        SwitchAtDay(many, 1, TripExecution.Aggregate);
        one.Step(DayTicks);
        many.Step(DayTicks);
        Assert.Equal(one.ComputeStateHash(), many.ComputeStateHash());
    }

    [Fact]
    public void Aggregate_Step_AllocatesZeroBytes()
    {
        ScenarioDesc d = Fixture(peds: false);
        using Simulation sim = Make(d);
        SwitchAtDay(sim, 1, TripExecution.Aggregate);
        sim.Step(DayTicks / 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            sim.Step(1);
        }

        sim.Step(DayTicks / 4);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void TimerWheel_AdvanceTo_SkipsLikeCollect()
    {
        var wheel = new Sim.Core.Util.TimerWheel(8);
        wheel.Schedule(3, 100, 0);
        wheel.Schedule(5, 9000, 0);   // переполнение: переносится на 4096 и 8192
        Assert.Equal(100, wheel.NextDue(0, 50_000));
        Assert.Equal(long.MaxValue, wheel.NextDue(0, 99));
        var buf = new int[8];
        Assert.Equal(1, wheel.Collect(100, buf));
        Assert.Equal(3, buf[0]);
        Assert.Equal(4096, wheel.NextDue(100, 50_000));   // перенос из переполнения - тоже работа
        wheel.AdvanceTo(100, 4095);
        Assert.Equal(0, wheel.Collect(4096, buf));
        Assert.Equal(8192, wheel.NextDue(4096, 50_000));
        wheel.AdvanceTo(4096, 8191);
        Assert.Equal(0, wheel.Collect(8192, buf));
        Assert.Equal(9000, wheel.NextDue(8192, 50_000));
        wheel.AdvanceTo(8192, 8999);
        Assert.Equal(1, wheel.Collect(9000, buf));
        Assert.Equal(5, buf[0]);
        Assert.Equal(long.MaxValue, wheel.NextDue(9000, 50_000));
    }

    /// <summary>Вчерашние сутки: ноги и суммарная стоимость по способам (из <see cref="CityStats"/>: среднее · число).</summary>
    private static void Yesterday(Simulation sim, long[] legs, long[] cost)
    {
        sim.GetCityStats(out CityStats s);
        legs[0] += s.LegsYesterdayCar;
        legs[1] += s.LegsYesterdayBus;
        legs[2] += s.LegsYesterdayWalk;
        cost[0] += (long)s.AvgCostCarDs * s.LegsYesterdayCar;
        cost[1] += (long)s.AvgCostBusDs * s.LegsYesterdayBus;
        cost[2] += (long)s.AvgCostWalkDs * s.LegsYesterdayWalk;
    }

    private static double Avg(long sum, long n) => n > 0 ? sum / (double)n : 0.0;

    /// <summary>
    /// Согласие aggregate и micro: <paramref name="calibrationDay"/> суток micro (первые - переходный процесс старта), затем <paramref name="days"/> суток сравнения. Допуски §19:
    /// время по способам <paramref name="costTol"/>, доли способов ±3 п. п., ноги <paramref name="legsTol"/>, нулевые <c>res_stuck</c> и сумма денег.
    /// </summary>
    private void AssertAgreement(ScenarioDesc d, int calibrationDay, int days, double costTol, double legsTol)
    {
        using Simulation micro = Make(d);
        using Simulation agg = Make(d);
        int dayTicks = City(micro).DayTicks;
        micro.Step(calibrationDay * dayTicks);
        SwitchAtDay(agg, calibrationDay, TripExecution.Aggregate);
        var ml = new long[3];
        var mc = new long[3];
        var al = new long[3];
        var ac = new long[3];
        long microStuck = micro.Counters.ResStuck;
        for (int day = 0; day < days; day++)
        {
            micro.Step(dayTicks);
            agg.Step(dayTicks);
            Yesterday(micro, ml, mc);
            Yesterday(agg, al, ac);
        }

        long mt = ml[0] + ml[1] + ml[2];
        long at = al[0] + al[1] + al[2];
        _out.WriteLine($"micro legs car/bus/walk {ml[0]}/{ml[1]}/{ml[2]} cost ds {Avg(mc[0], ml[0]):F0}/{Avg(mc[1], ml[1]):F0}/{Avg(mc[2], ml[2]):F0}");
        _out.WriteLine($"aggr  legs car/bus/walk {al[0]}/{al[1]}/{al[2]} cost ds {Avg(ac[0], al[0]):F0}/{Avg(ac[1], al[1]):F0}/{Avg(ac[2], al[2]):F0}");
        _out.WriteLine($"res_stuck micro {micro.Counters.ResStuck} aggregate {agg.Counters.ResStuck}; jumped {agg.World.JumpedTicks} of {agg.Tick}");
        Assert.True(mt > 100 && at > 100, $"мало ног: micro {mt}, aggregate {at}");
        Assert.InRange(at / (double)mt, 1 - legsTol, 1 + legsTol);
        for (int m = 0; m < 3; m++)
        {
            Assert.InRange(Math.Abs((al[m] / (double)at) - (ml[m] / (double)mt)), 0.0, 0.03);
            if (ml[m] >= 30 && al[m] >= 30)
            {
                Assert.InRange(Avg(ac[m], al[m]) / Avg(mc[m], ml[m]), 1 - costTol, 1 + costTol);
            }
        }

        Assert.Equal(microStuck, micro.Counters.ResStuck);
        Assert.Equal(0, agg.Counters.ResStuck);
        Assert.Equal(0, MoneySum(micro));
        Assert.Equal(0, MoneySum(agg));
    }

    /// <summary>Короткие сутки (11 520 тиков): время ноги по способам ±15% и доли ±3 п. п. После калибровки aggregate воспроизводит среднюю стоимость micro; ноги - порядок (допуск ±25%: сутки короче ног).</summary>
    [Fact]
    public void Aggregate_AgreesWithMicro_ShortDays()
    {
        AssertAgreement(Fixture(peds: true), calibrationDay: 2, days: 3, costTol: 0.15, legsTol: 0.25);
    }

    /// <summary>Спека §19: настоящие сутки (72 000 тиков), 3 дня после суток калибровки: время ±15%, доли ±3 п. п., ноги ±5%. Долго (≈ 10 минут в Debug): <c>TC_LONG=1</c>.</summary>
    [Fact]
    [Trait("Category", "Long")]
    public void Aggregate_AgreesWithMicro_Over3Days()
    {
        if (Environment.GetEnvironmentVariable("TC_LONG") != "1")
        {
            return;
        }

        AssertAgreement(CityFixtures.S1kResPeds(), calibrationDay: 2, days: 3, costTol: 0.15, legsTol: 0.05);
    }

    [Fact]
    public void Aggregate_DropsMicroPedestrians_AndPausesTraffic()
    {
        ScenarioDesc d = Fixture(peds: true);
        using Simulation sim = Make(d);
        World w = sim.World;
        SwitchAtDay(sim, 1, TripExecution.Aggregate);
        sim.Step(2 * DayTicks);
        Assert.Equal(0, w.Peds!.P.AliveCount);
        Assert.Equal(0, w.Vehicles.AliveCount);
        Assert.True(w.JumpedTicks > 0);
        Assert.Equal(0, sim.Counters.ResStuck);
        Assert.Equal(0, MoneySum(sim));
    }
}
