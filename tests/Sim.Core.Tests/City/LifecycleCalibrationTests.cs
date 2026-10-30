using System;
using System.Globalization;
using System.IO;
using System.Text;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// Калибровка стартовых значений жизни здания (building-lifecycle.md §2, §7; задача BL5). Тест <c>Calibration_Run</c> - ручной прогон: включается переменной <c>TC_CALIB=1</c>,
/// печатает таблицу по суткам в файл <c>TC_CALIB_OUT</c>. Без переменной - пропускается.
/// </summary>
public sealed class LifecycleCalibrationTests
{
    private static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;

    private static int EnvI(string k, int d) => int.Parse(Env(k, d.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

    /// <summary>Сценарий из <c>scenarios/</c> с режимом жизни; агрегатное исполнение (прогон на десятки суток).</summary>
    internal static ScenarioDesc Load(string name, TripExecution exec = TripExecution.Aggregate)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json")).Desc;
        d.Town.Residents.TripExecution = exec;
        ApplyDesc(ref d);
        return d;
    }

    internal static int DepotType(CityWorld c)
    {
        for (int i = 0; i < c.Types.Count; i++)
        {
            if (c.Types.Rows[i].Kind == LotZone.Service)
            {
                return i;
            }
        }

        Assert.Fail("нет типа депо");
        return -1;
    }

    /// <summary>Ставит <paramref name="n"/> депо на свободные участки с равным шагом по индексу (грубо - равномерно по городу).</summary>
    internal static int PlaceDepots(Simulation sim, int n)
    {
        CityWorld c = sim.World.City!;
        LotStore l = c.Lots;
        int free = 0;
        for (int i = 0; i < l.Count; i++)
        {
            free += Free(l, i) ? 1 : 0;
        }

        int step = Math.Max(1, free / (n + 1));
        int placed = 0;
        int seen = 0;
        int next = step / 2;
        for (int i = 0; i < l.Count && placed < n; i++)
        {
            if (!Free(l, i) || seen++ != next)
            {
                continue;
            }

            next += step;
            if (ToolsFixture.Run(sim, Command.MakeBuildService(i, DepotType(c), (uint)(100 + placed))) == RejectReason.None)
            {
                placed++;
            }
        }

        return placed;
    }

    private static bool Free(LotStore l, int i) => l.Building[i] < 0 && (l.Flags[i] & LotFlags.Lost) == 0 && l.CarLane[i] >= 0 && l.RoadComp[i] >= 0;

    /// <summary>Ручные подстановки калибровки в описание сценария (переменные <c>TC_CALIB_*</c>); без переменных ничего не меняет.</summary>
    internal static void ApplyDesc(ref ScenarioDesc d)
    {
        ref CityLifecycleDesc lf = ref d.Town.Lifecycle;
        lf.UtilityTariff = EnvI("TC_CALIB_TARIFF", lf.UtilityTariff);
        lf.RepairRequestBp = EnvI("TC_CALIB_REQ", lf.RepairRequestBp);
        lf.RepairMinutes = EnvI("TC_CALIB_REPMIN", lf.RepairMinutes);
        lf.CrewDayCost = EnvI("TC_CALIB_CREWCOST", lf.CrewDayCost);
        lf.RepairPerUnit = EnvI("TC_CALIB_REPUNIT", lf.RepairPerUnit);
        lf.TariffBurdenPct = EnvI("TC_CALIB_BURDEN", lf.TariffBurdenPct);
        lf.TariffLeaveQ16 = EnvI("TC_CALIB_LEAVEQ16", lf.TariffLeaveQ16);
        lf.AbandonedDecayPct = EnvI("TC_CALIB_ABDECAY", lf.AbandonedDecayPct);
        lf.WornRelocateQ16 = EnvI("TC_CALIB_RELOCQ16", lf.WornRelocateQ16);
        lf.LvBlightPermille = EnvI("TC_CALIB_LVBLIGHT", lf.LvBlightPermille);
        lf.RubbleCostPct = EnvI("TC_CALIB_RUBBLE", lf.RubbleCostPct);
        lf.MaxDispatchPerTick = EnvI("TC_CALIB_DISPATCH", lf.MaxDispatchPerTick);
    }

    /// <summary>Масштаб износа/потолка и число бригад депо в созданном мире (<c>TC_CALIB_DECAY</c>, <c>TC_CALIB_CEIL</c>, <c>TC_CALIB_VEH</c>, проценты).</summary>
    internal static void ApplyTypes(CityWorld c)
    {
        int decayPct = EnvI("TC_CALIB_DECAY", 100);
        int ceilPct = EnvI("TC_CALIB_CEIL", 100);
        int veh = EnvI("TC_CALIB_VEH", -1);
        for (int i = 0; i < c.Types.Count; i++)
        {
            ref BuildingTypeDesc t = ref c.Types.Rows[i];
            t.DecayBpDay = (ushort)(t.DecayBpDay * decayPct / 100);
            t.CeilDropBpDay = (ushort)(t.CeilDropBpDay * ceilPct / 100);
            if (veh > 0 && t.Kind == LotZone.Service)
            {
                t.Vehicles = (byte)veh;
            }
        }
    }

    /// <summary>
    /// «ЖКХ успевает» при дефолтном тарифе (building-lifecycle.md §2.10, BL5): набор депо сценария (s1k-res-bl - 3, s10k-res-bl - 12 по 16 бригад), прогон агрегатом; к концу фонд не пуст,
    /// ремонты идут, активных зданий с состоянием ниже 70 % - не больше 2 %, средняя задержка ремонта - не дольше недели игровых суток, уход по тарифу - не больше 0.2 % населения в сутки.
    /// </summary>
    [Theory]
    [InlineData("s1k-res-bl", 3, 60)]
    [InlineData("s10k-res-bl", 12, 60)]
    public void Utility_KeepsUp_AtDefaultTariff(string scenario, int depots, int days)
    {
        ScenarioDesc d = Load(scenario);
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        sim.Step(20);
        CityWorld c = sim.World.City!;
        Assert.Equal(depots, PlaceDepots(sim, depots));
        Assert.Equal(CityLifecycleDesc.Default().UtilityTariff, c.Budget!.Policy.R0);
        long popSum = 0;
        for (int day = 0; day < days; day++)
        {
            sim.Step(c.DayTicks);
            sim.GetCityStats(out CityStats s);
            popSum += s.Population;
        }

        Assert.True(sim.GetUtilityInfo(out UtilityInfo u));
        BuildingStore b = c.Buildings;
        int active = 0;
        int worn = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind != LotZone.Service)
            {
                active++;
                worn += b.Cond[i] < 7_000 ? 1 : 0;
            }
        }

        Assert.Equal(depots, u.Depots);
        Assert.Equal(0, u.FundEmpty);
        Assert.True(u.RepairsDone > 0);
        Assert.True(worn * 50 <= active, $"ниже 70 %: {worn} из {active}");
        Assert.True(u.AvgDelayTicks <= 7L * c.DayTicks, $"средняя задержка {u.AvgDelayTicks} тиков");
        Assert.True(sim.World.Counters.TariffLeaves * 500 <= popSum, $"уход по тарифу {sim.World.Counters.TariffLeaves} при сумме населения по суткам {popSum}");
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    /// <summary>Режим жизни здания включён в новых сценариях и в игровых (building-lifecycle.md §2.1, §8 BL5); значения - дефолты калибровки BL5, уход по тарифу смягчён сценарием.</summary>
    [Theory]
    [InlineData("s1k-res-bl")]
    [InlineData("s10k-res-bl")]
    [InlineData("game-city")]
    [InlineData("yuryev-polsky")]
    [InlineData("ys0-30k")]
    [InlineData("ys0-50k")]
    [InlineData("ys0-100k")]
    public void Scenarios_EnableLifecycle(string name)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json")).Desc;
        CityLifecycleDesc lf = d.Town.Lifecycle;
        CityLifecycleDesc def = CityLifecycleDesc.Default();
        Assert.True(lf.Enabled);
        Assert.True(d.Town.Economy.CityBudget);
        Assert.Equal(def.UtilityTariff, lf.UtilityTariff);
        Assert.Equal(def.TariffBurdenPct, lf.TariffBurdenPct);
        Assert.Equal(CityDesc.Q16(0.002), lf.TariffLeaveQ16);
    }

    [Fact]
    public void Balance_Probe()
    {
        if (Env("TC_CALIB_BAL", "") != "1")
        {
            return;
        }

        StringBuilder sb = new();
        ScenarioDesc d = Load(Env("TC_CALIB_SCEN", "s1k-res-bl"));
        (LotZone, bool)[] kinds = [(LotZone.Residential, false), (LotZone.Residential, true), (LotZone.Commercial, false), (LotZone.Commercial, true), (LotZone.Industrial, false), (LotZone.Industrial, true)];
        foreach ((LotZone k, bool dense) in kinds)
        {
            LifecycleBalanceTests.Outcome o = LifecycleBalanceTests.Run(d, k, dense);
            sb.AppendLine(LifecycleBalanceTests.Format($"{k}/{(dense ? "high" : "low")}", o));
            File.WriteAllText(Env("TC_CALIB_OUT", Path.Combine(Path.GetTempPath(), "bal.txt")), sb.ToString());
        }
    }

    [Fact]
    public void Calibration_Run()
    {
        if (Env("TC_CALIB", "") != "1" || Env("TC_CALIB_BAL", "") == "1")
        {
            return;
        }

        string scen = Env("TC_CALIB_SCEN", "s1k-res-bl");
        int days = EnvI("TC_CALIB_DAYS", 10);
        int depots = EnvI("TC_CALIB_DEPOTS", 1);
        int threads = EnvI("TC_CALIB_THREADS", 1);
        string outPath = Env("TC_CALIB_OUT", Path.Combine(Path.GetTempPath(), "calib.txt"));
        ScenarioDesc d = Load(scen, Env("TC_CALIB_EXEC", "a") == "m" ? TripExecution.Agents : TripExecution.Aggregate);
        if (Env("TC_CALIB_NOLIFE", "") == "1")
        {
            d.Town.Lifecycle = default;
        }


        StringBuilder sb = new();
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(20);
        CityWorld c = sim.World.City!;
        ApplyTypes(c);

        if (EnvI("TC_CALIB_SUBSIDY", 0) == 1)
        {
            c.Budget!.Policy.Flags |= BudgetPolicyFlags.UtilitySubsidy;
        }

        int placed = depots > 0 ? PlaceDepots(sim, depots) : 0;
        sb.Append(CultureInfo.InvariantCulture, $"# scenario={scen} days={days} depots={placed} tariff={c.Budget!.Policy.R0} exec={d.Town.Residents.TripExecution}\n");
        int[] cnt = new int[8];
        long[] cap = new long[8];
        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            if (c.Buildings.IsAlive(i))
            {
                ref readonly BuildingTypeDesc t = ref c.Types.Rows[c.Buildings.Type[i]];
                int k = ((int)t.Kind * 2) + (t.Density != 0 ? 1 : 0);
                cnt[k & 7]++;
                cap[k & 7] += t.Capacity;
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $"# types(kind*2+dens): n=[{string.Join(",", cnt)}] cap=[{string.Join(",", cap)}] dayTicks={c.DayTicks}\n");
        sb.Append("day pop act aban ctor | b>=90 70-90 50-70 <50 avgCond | repairs queue crews busy | fund  fee repair wages | city  leaves crumbl demol relocs | ms\n");
        long wall = Environment.TickCount64;
        for (int day = 1; day <= days; day++)
        {
            sim.Step(c.DayTicks);
            sim.GetCityStats(out CityStats s);
            int[] h = new int[4];
            int[] ab = new int[4];
            long sum = 0;
            int n = 0;
            BuildingStore b = c.Buildings;
            for (int i = 0; i < b.HighWater; i++)
            {
                if (!b.IsAlive(i) || c.Types.Rows[b.Type[i]].Kind == LotZone.Service)
                {
                    continue;
                }

                if (b.State[i] == (byte)BuildingState.Abandoned)
                {
                    ab[(int)c.Types.Rows[b.Type[i]].Kind]++;
                }

                int cond = b.Cond[i];
                h[cond >= 9000 ? 0 : cond >= 7000 ? 1 : cond >= 5000 ? 2 : 3]++;
                sum += cond;
                n++;
            }

            CityResidents rs = c.Residents;
            int alive = 0, lab = 0, emp = 0, zero = 0, over = 0;
            long inc = 0;
            for (int i = 0; i < rs.HighWater; i++)
            {
                if (!rs.Pool.IsAlive(i) || rs.Home[i] < 0)
                {
                    continue;
                }

                alive++;
                lab += (rs.Flags[i] & ResidentFlags.Labour) != 0 ? 1 : 0;
                emp += rs.Work[i] != -1 ? 1 : 0;
                zero += rs.IncomeEwma[i] == 0 ? 1 : 0;
                over += c.Budget!.Policy.R0 > rs.IncomeEwma[i] * c.Cfg.Lifecycle.TariffBurdenPct ? 1 : 0;
                inc += rs.IncomeEwma[i];
            }

            sb.Append(CultureInfo.InvariantCulture, $"    res: alive={alive} labour={lab} employed={emp} zeroInc={zero} overBurden={over} avgInc={(alive > 0 ? inc / alive : 0)} abR={ab[1]} abC={ab[2]} abI={ab[3]}\n");
            sim.GetUtilityInfo(out UtilityInfo u);
            Ledger lg = c.Ledger;
            SimCounters k = sim.World.Counters;
            long ms = Environment.TickCount64 - wall;
            wall = Environment.TickCount64;
            sb.Append(CultureInfo.InvariantCulture,
                $"{day,3} {s.Population,6} {s.Active,5} {s.Abandoned,4} {s.Constructing,3} | {h[0],5} {h[1],5} {h[2],5} {h[3],4} {(n > 0 ? sum / n : 0),5} | {u.RepairsDone,5} {u.QueueLength,5} {u.Crews,3} {u.CrewsBusy,3} | {u.Fund,8} {lg.FlowTotal[(int)FlowKind.UtilityFee],8} {lg.FlowTotal[(int)FlowKind.Repair],8} {lg.FlowTotal[(int)FlowKind.CrewWages],8} | {lg.City,9} {k.TariffLeaves,5} {k.BuildingsCrumbled,4} {k.BuildingsDemolished,4} {k.BuildingsResettled,4} | dly={u.AvgDelayTicks / 20}s nodep={k.RepairNoDepot} | {ms}\n");
            File.WriteAllText(outPath, sb.ToString());
        }
    }
}
