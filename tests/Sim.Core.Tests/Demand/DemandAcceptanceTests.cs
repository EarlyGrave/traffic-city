using System;
using Sim.Core.Api;
using Sim.Core.Tests.City;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Demand;

/// <summary>
/// Приёмка спроса Z8 (zoning-demand.md §10.1): DM1 (туров на жителя в день в коридоре вокруг расчётного по параметрам) и DM2 (нет «качелей» на стабильном
/// городе, <c>zoning.growth = false</c>). s1k-res из <c>scenarios/s1k-res.json</c> с настоящими сутками (72 000 тиков), micro.
/// </summary>
public sealed class DemandAcceptanceTests
{
    private const int DayTicks = 72_000;      // настоящие сутки s1k-res (day_s = 3600): на коротких сутках туры не помещаются и числа не те

    private readonly ITestOutputHelper _out;

    public DemandAcceptanceTests(ITestOutputHelper output) => _out = output;

    private static ScenarioDesc Fixture(bool growth)
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        d.Town.Zoning.Growth = growth;
        return d;
    }

    /// <summary>Статистика «вчера» после границы суток <paramref name="day"/>: прогон до первого тика суток.</summary>
    private static CityStats AtDay(Simulation sim, int day)
    {
        sim.Step((int)((((long)day * DayTicks) + 1) - sim.Tick));
        sim.GetCityStats(out CityStats st);
        Assert.Equal(day, (int)st.Day);
        return st;
    }

    /// <summary>
    /// Ожидаемое число дискреционных туров на жителя по параметрам (§6.3) при <c>LS = LS_ref</c>: логит по <c>k ∈ 0..freq_max</c> с
    /// <c>U_k = k · term − k² · γ</c> (<c>term</c> = <c>asc_freq</c>, неработающим + <c>asc_nonworker</c>).
    /// </summary>
    private static double ExpectedDiscretionary(CityDesc cfg, bool worker)
    {
        double theta = cfg.Choice.ThetaFreq64 / (64.0 * 65536.0);
        double term = cfg.Choice.AscFreqDs + (worker ? 0 : cfg.Choice.AscNonWorkerDs);
        double sw = 0, sk = 0;
        for (int k = 0; k <= cfg.Choice.FreqMax; k++)
        {
            double u = (k * term) - ((double)k * k * cfg.Choice.GammaFreqDs);
            double w = Math.Exp(theta * u);
            sw += w;
            sk += k * w;
        }

        return sk / sw;
    }

    [Fact]
    [Trait("Category", "Long")]
    public void DM1_ToursPerResident_InCorridorAroundParameterEstimate()
    {
        if (Environment.GetEnvironmentVariable("TC_LONG") != "1")
        {
            return;   // настоящие сутки в Debug - минуты: включается TC_LONG=1 (отчётные прогоны; в pre-commit не входит)
        }

        ScenarioDesc d = Fixture(growth: true);
        using Simulation sim = Simulation.Create(in d);
        double sum = 0;
        double expectedSum = 0;
        int n = 0;
        for (int day = 1; day <= 10; day++)
        {
            CityStats st = AtDay(sim, day);
            if (day < 5)
            {
                continue;
            }

            double workers = (double)st.Employed / st.Population;
            double expected = workers * (1 + ExpectedDiscretionary(d.Town, worker: true)) + ((1 - workers) * ExpectedDiscretionary(d.Town, worker: false));
            double actual = (double)st.ToursYesterday / st.Population;
            _out.WriteLine($"day {day}: pop {st.Population}, tours/res {actual:F3}, expected {expected:F3}");
            sum += actual;
            expectedSum += expected;
            n++;
        }

        double ratio = sum / expectedSum;
        _out.WriteLine($"DM1: mean tours/res {sum / n:F3}, expected {expectedSum / n:F3}, ratio {ratio:F3}");
        Assert.InRange(ratio, 0.7, 1.3);
        Assert.Equal(0, sim.Counters.ResStuck);
    }

    [Fact]
    [Trait("Category", "Long")]
    public void DM2_StableCity_NoSeesaw()
    {
        if (Environment.GetEnvironmentVariable("TC_LONG") != "1")
        {
            return;   // настоящие сутки в Debug - минуты: включается TC_LONG=1 (отчётные прогоны; в pre-commit не входит)
        }

        ScenarioDesc d = Fixture(growth: false);
        using Simulation sim = Simulation.Create(in d);
        double prevCar = -1, prevShare = -1;
        double worstCar = 0, worstShare = 0;
        for (int day = 1; day <= 20; day++)
        {
            CityStats st = AtDay(sim, day);
            double legs = st.LegsYesterdayCar + st.LegsYesterdayBus + st.LegsYesterdayWalk;
            double share = st.LegsYesterdayCar / Math.Max(1.0, legs);
            _out.WriteLine($"day {day}: car legs {st.LegsYesterdayCar}, bus {st.LegsYesterdayBus}, walk {st.LegsYesterdayWalk}, car share {share:P1}, pop {st.Population}");
            if (day > 5)
            {
                double dCar = Math.Abs(st.LegsYesterdayCar - prevCar) / Math.Max(1.0, prevCar);
                double dShare = Math.Abs(share - prevShare);
                worstCar = Math.Max(worstCar, dCar);
                worstShare = Math.Max(worstShare, dShare);
            }

            prevCar = st.LegsYesterdayCar;
            prevShare = share;
        }

        _out.WriteLine($"DM2: max |d car legs| {worstCar:P2}, max |d car share| {worstShare * 100:F2} pp");
        Assert.True(worstCar <= 0.05, $"|d car legs| {worstCar:P2} > 5%");
        Assert.True(worstShare <= 0.02, $"|d car share| {worstShare * 100:F2} pp > 2 pp");
        Assert.Equal(0, sim.Counters.ResStuck);
    }
}
