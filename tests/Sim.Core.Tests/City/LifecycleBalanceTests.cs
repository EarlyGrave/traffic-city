using System;
using System.Globalization;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Tests.City;

/// <summary>
/// Баланс вреда (building-lifecycle.md §2.4, §7 тест 6): заброшка, которой дали рассыпаться самой, обходится казне заметно дороже сноса в день 0.
/// Здание типа T в «типовом квартале» (не меньше 8 соседей в окне 3×3 клеток поля) становится заброшенным и стоит до рассыпания. Каждые сутки считается контрфактум «а если бы его снесли в день 0»:
/// земельный налог соседей (жители - на человека, фирмы - на место) с вредом заброшки в полях и без вклада этого здания; сумма за все сутки обязана быть не меньше 1.5 × <c>demolish_cost</c> типа.
/// Контрфактум, а не второй прогон: два прогона одного seed расходятся хаотично (шум разницы казны ±10-20 тыс. при полезном сигнале в тысячи), поэтому померить вред прогоном нельзя.
/// Считается только земельная часть; отток жителей (переезд ×2 при беспорядке), выработка фирм (−10 %) и случаи K4 в казну не входят, так что оценка заниженная.
/// </summary>
public sealed class LifecycleBalanceTests
{
    private const string Scenario = "s1k-res-bl";
    private const int MinNeighbours = 8;

    public readonly record struct Outcome(int Type, int Neighbours, long DemolishCost, long Rubble, int Days, long Loss);

    /// <summary>Здание вида <paramref name="kind"/>/<paramref name="dense"/> с не менее <see cref="MinNeighbours"/> активными соседями в окне 3×3 клеток <c>cell_m</c>; берётся первое найденное.</summary>
    internal static int Candidate(CityWorld c, LotZone kind, bool dense, out int neighbours)
    {
        BuildingStore b = c.Buildings;
        LotStore l = c.Lots;
        float half = c.Cfg.Lifecycle.CellM * 1.5f;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (!b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active)
            {
                continue;
            }

            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[i]];
            if (t.Kind != kind || (t.Density != 0) != dense)
            {
                continue;
            }

            int n = 0;
            for (int j = 0; j < b.HighWater; j++)
            {
                if (j != i && b.IsAlive(j) && b.State[j] == (byte)BuildingState.Active && c.Types.Rows[b.Type[j]].Kind != LotZone.Service &&
                    MathF.Abs(l.X[b.Lot[j]] - l.X[b.Lot[i]]) <= half && MathF.Abs(l.Z[b.Lot[j]] - l.Z[b.Lot[i]]) <= half)
                {
                    n++;
                }
            }

            if (n >= MinNeighbours)
            {
                neighbours = n;
                return i;
            }
        }

        neighbours = 0;
        return -1;
    }

    /// <summary>
    /// Потеря земельного налога за сегодняшние сутки из-за вклада заброшенного здания <paramref name="victim"/> в поля, в «базисных» единицах (до деления на <c>Ledger.Bp</c>):
    /// <c>Σ (LV без вклада − LV с вкладом) · места · land_tax_bp_day</c> по активным соседям окна 3×3.
    /// </summary>
    internal static long DailyLandLossBp(CityWorld c, int victim, int[] scratch)
    {
        NeighborhoodFields f = c.Fields!;
        CityBudget bud = c.Budget!;
        BuildingStore b = c.Buildings;
        LotStore l = c.Lots;
        Array.Clear(scratch);
        CityLife.Contribute(c, victim, in c.Types.Rows[b.Type[victim]], scratch, false);
        int vcell = f.CellOf(l.X[b.Lot[victim]], l.Z[b.Lot[victim]]);
        long vPts = scratch[(vcell * NeighborhoodFields.Stride) + NeighborhoodFields.Blight];
        long lvb = c.Cfg.Lifecycle.LvBlightPermille;
        long loss = 0;
        for (int n = 0; n < b.HighWater; n++)
        {
            if (n == victim || !b.IsAlive(n) || b.State[n] != (byte)BuildingState.Active)
            {
                continue;
            }

            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[n]];
            if (t.Kind == LotZone.Service)
            {
                continue;
            }

            int lot = b.Lot[n];
            int q = f.CellOf(l.X[lot], l.Z[lot]);
            int dx = Math.Abs((q % f.Nx) - (vcell % f.Nx));
            int dz = Math.Abs((q / f.Nx) - (vcell / f.Nx));
            if (dx > 1 || dz > 1)
            {
                continue;
            }

            long w = q == vcell ? 2 : 1;
            long bl = f.Window(q, NeighborhoodFields.Blight);
            long h = f.Window(q, NeighborhoodFields.Heritage);
            long lv = bud.LandValue[l.Cell[lot]];
            long with = lv * (1000 + Math.Min(200L, 10 * h) - Math.Min(400L, lvb * bl));
            long without = lv * (1000 + Math.Min(200L, 10 * h) - Math.Min(400L, lvb * Math.Max(0, bl - (w * vPts))));
            long places = t.Kind == LotZone.Residential ? b.Occupants[n] : t.Capacity;
            loss += (without - with) * places / 1000 * bud.Policy.LandTaxBpDay;
        }

        return loss;
    }

    public static Outcome Run(ScenarioDesc desc, LotZone kind, bool dense, int maxDays = 600)
    {
        using Simulation sim = Simulation.Create(in desc, new SimRuntimeOptions { WorkerThreads = 1 });
        LifecycleCalibrationTests.ApplyTypes(sim.World.City!);   // ручная калибровка (TC_CALIB_*); без переменных ничего не меняет
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        int victim = Candidate(c, kind, dense, out int neigh);
        Assert.True(victim >= 0, $"нет типового здания {kind}/{(dense ? "high" : "low")}");
        int type = c.Buildings.Type[victim];
        long demolishCost = c.Types.Rows[type].DemolishCost;
        long rubble = demolishCost * c.Cfg.Lifecycle.RubbleCostPct / 100;
        int gen = c.Buildings.Pool.GenerationOf(victim);
        Growth.Abandon(w, c, victim);
        int[] scratch = new int[c.Fields!.Cur.Length];
        long lossBp = 0;
        int days = 0;
        while (days < maxDays && c.Buildings.IsAlive(victim) && c.Buildings.Pool.GenerationOf(victim) == gen)
        {
            sim.Step(c.DayTicks);
            if (c.Buildings.IsAlive(victim) && c.Buildings.Pool.GenerationOf(victim) == gen)
            {
                lossBp += DailyLandLossBp(c, victim, scratch);
            }

            days++;
        }

        Assert.True(days < maxDays, "здание не рассыпалось за горизонт");
        return new Outcome(type, neigh, demolishCost, rubble, days, lossBp / Ledger.Bp);
    }

    public static string Format(string name, Outcome o) =>
        string.Create(CultureInfo.InvariantCulture, $"{name}: type={o.Type} neigh={o.Neighbours} demolish={o.DemolishCost} rubble={o.Rubble} days={o.Days} landLoss={o.Loss} ratio={(double)o.Loss / o.DemolishCost:F2}");

    [Theory]
    [InlineData(LotZone.Residential, false)]
    [InlineData(LotZone.Residential, true)]
    [InlineData(LotZone.Commercial, false)]
    [InlineData(LotZone.Commercial, true)]
    [InlineData(LotZone.Industrial, false)]
    [InlineData(LotZone.Industrial, true)]
    public void AbandonedLeftToCrumble_CostsTheTreasury_AtLeast1_5xDemolish(LotZone kind, bool dense)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(Scenario);
        Outcome o = Run(d, kind, dense);
        Assert.True(o.Loss * 2 >= o.DemolishCost * 3, Format($"{kind}/{(dense ? "high" : "low")}", o));
    }

    [Theory]
    [InlineData(LotZone.Residential, true)]
    [InlineData(LotZone.Industrial, false)]
    public void LandDamage_IsPositive_AndGrowsWithLvBlight(LotZone kind, bool dense)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(Scenario);
        Outcome basic = Run(d, kind, dense);
        d.Town.Lifecycle.LvBlightPermille *= 4;
        Outcome strong = Run(d, kind, dense);
        Assert.True(basic.Loss > 0, Format("basic", basic));
        Assert.True(strong.Loss > basic.Loss, Format("strong", strong));
        Assert.True(basic.Days > 20, "заброшка должна стоять годами (десятки суток), а не рассыпаться за дни");
    }
}
