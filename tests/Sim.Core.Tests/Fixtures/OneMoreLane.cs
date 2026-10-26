using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.City;

namespace Sim.Core.Tests.Fixtures;

/// <summary>Показатели суток прогона <see cref="OneMoreLane"/> (статистика «вчера» после границы суток <c>Day</c>).</summary>
internal readonly record struct LaneDay(int Day, int Population, int CarLegs, int AvgCostCarDs, long CarCrossings, double Tours);

/// <summary>
/// Фикстура «one more lane» (Duranton &amp; Turner 2011, zoning-demand.md 10.2) в режиме aggregate: s1k-res, сутки 0 в micro (калибровка K), дальше агрегат.
/// Ёмкость обоих вариантов после калибровки делится на <c>capDivisor</c> (иначе s1k-res не перегружен: BPR в Q8 не видит разницы). Вариант B в день <c>upgradeDay</c> удваивает <c>Cap</c> всех ячеек через внутренний хук (<see cref="DoubleCapacity"/>): модель BPR видит вдвое больше полос,
/// сеть и K не меняются. A - тот же прогон без хука. Одинаковые seed и начальное население. Выигрыш <c>G(d) = T_A(d) − T_B(d)</c> - средняя стоимость машинной
/// ноги (дс), машино-км - пересечения клеток машинными ногами.
/// </summary>
internal static class OneMoreLane
{
    public const int CalibrationDay = 1;

    public static ScenarioDesc Desc(int betaFreqQ8)
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        d.Town.Choice.BetaFreqQ8 = betaFreqQ8;
        return d;
    }

    /// <summary>Хук теста: <c>Cap</c> каждой ячейки × 2 (производное, не в сейве и хэше; <c>EnsureCap</c> не пересчитывает, пока версия сети та же).</summary>
    public static void DoubleCapacity(Simulation sim) => ScaleCapacity(sim, 2, 1);

    /// <summary>Хук теста: <c>Cap</c> каждой ячейки × <paramref name="mul"/> / <paramref name="div"/> (не меньше 1 там, где дороги есть).</summary>
    public static void ScaleCapacity(Simulation sim, int mul, int div)
    {
        CityWorld c = sim.World.City!;
        c.Loads.EnsureCap(sim.World, c);
        int[] cap = c.Loads.Cap;
        for (int i = 0; i < cap.Length; i++)
        {
            if (cap[i] > 0)
            {
                cap[i] = Math.Max(1, (int)((long)cap[i] * mul / div));
            }
        }
    }

    /// <summary>Прогон <paramref name="days"/> суток; <paramref name="upgradeDay"/> &lt; 0 - вариант A. Результат - по суткам 1..days.</summary>
    public static LaneDay[] Run(int days, int upgradeDay, int capDivisor, int betaFreqQ8)
    {
        ScenarioDesc d = Desc(betaFreqQ8);
        using Simulation sim = Simulation.Create(in d);
        CityWorld city = sim.World.City!;
        long dayTicks = city.DayTicks;
        var res = new LaneDay[days];
        for (int day = 1; day <= days; day++)
        {
            if (day == CalibrationDay)
            {
                // команда на последнем тике суток до калибровки: к первому тику суток режим уже aggregate
                sim.Step((int)((day * dayTicks) - 1 - sim.Tick));
                if (!sim.Enqueue(Command.MakeSetTripExecution(TripExecution.Aggregate)))
                {
                    throw new InvalidOperationException("SetTripExecution rejected");
                }

                sim.Step(1);
                ScaleCapacity(sim, 1, capDivisor);   // перегруженный коридор: спрос подобран к ёмкости (спека 10.2)
            }

            sim.Step((int)((day * dayTicks) + 1 - sim.Tick));
            if (day == upgradeDay)
            {
                DoubleCapacity(sim);
            }

            sim.GetCityStats(out CityStats st);
            res[day - 1] = new LaneDay(day, st.Population, st.LegsYesterdayCar, st.AvgCostCarDs, sim.CityCarCellCrossingsYesterday, (double)st.ToursYesterday / Math.Max(1, st.Population));
        }

        return res;
    }
}
