using System;
using System.Globalization;
using System.IO;
using System.Text;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Tools;

namespace Sim.Core.Tests.City;

/// <summary>
/// Сценарии <c>s1k-res-sr</c> и <c>s10k-res-sr</c> (services.md §8, SR6): набор станций, калибровка и отчёт приёмки. Тест <c>Report_Run</c> - ручной прогон: включается переменной <c>TC_CALIB_SR=1</c>,
/// печатает отчёт в файл <c>TC_CALIB_OUT</c> (число суток <c>TC_SR_DAYS</c>, станции <c>TC_SR_MED</c>/<c>TC_SR_HOSP</c>/<c>TC_SR_POINT</c>/<c>TC_SR_FIRE</c>, потоки <c>TC_SR_THREADS</c>, сценарий <c>TC_SR_SCEN</c>).
/// </summary>
public sealed class ServicesScenarioTests
{
    internal const int Utility = 0;
    internal const int Medicine = 1;
    internal const int Fire = 2;

    internal delegate void Tweak(ref ScenarioDesc d);

    /// <summary>Цена повышения больница -> медцентр с одной пристройкой (последний <see cref="Make"/>): итого с выкупом и занят ли сосед.</summary>
    internal static readonly System.Collections.Generic.List<(long Cost, bool Occupied)> AnnexCosts = new();

    private static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;

    private static int EnvI(string k, int d) => int.Parse(Env(k, d.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

    /// <summary>Тип здания уровня <paramref name="level"/> (1-4) записи <paramref name="rec"/>.</summary>
    internal static int TypeOf(CityWorld c, int rec, int level) => c.Services!.Table.Levels[c.Services.Table.Records[rec].FirstLevel + level - 1].Type;

    /// <summary>Ставит <paramref name="n"/> станций уровня <paramref name="level"/> записи <paramref name="rec"/> на свободные участки, равномерно по индексу; участок, не прошедший проверки, пропускается.</summary>
    internal static int Place(Simulation sim, int rec, int level, int n, uint tag0 = 500)
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
        int type = TypeOf(c, rec, level);
        for (int i = 0; i < l.Count && placed < n; i++)
        {
            if (!Free(l, i) || seen++ < next)
            {
                continue;
            }

            if (ToolsFixture.Run(sim, Command.MakeBuildService(i, type, tag0 + (uint)placed)) == RejectReason.None)
            {
                placed++;
                next = seen - 1 + step;
            }
        }

        return placed;
    }

    /// <summary>
    /// Медцентры: участки 5x5 мельче <c>lot_min</c> [6, 5], поэтому медцентр - это больница на участке плюс пристройка (сосед по ряду), <c>UpgradeService</c> с выкупом (services.md §2.8).
    /// Возвращает число поставленных медцентров; станции равномерно по индексу участков.
    /// </summary>
    internal static int PlaceMedCenters(Simulation sim, int n, uint tag0 = 500, System.Collections.Generic.List<(long Cost, bool Occupied)>? annexCosts = null)
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
        int hospital = TypeOf(c, Medicine, 2);
        for (int i = 0; i < l.Count && placed < n; i++)
        {
            if (!Free(l, i) || (l.Flags[i] & LotFlags.Annex) != 0 || seen++ < next || l.Orient[i] == LotOrient.Legacy)
            {
                continue;
            }

            int[] row = Row(l, i);
            int p = Array.IndexOf(row, i);
            if (p < 0 || p + 1 >= row.Length || (l.Flags[row[p + 1]] & (LotFlags.Lost | LotFlags.Annex)) != 0)
            {
                continue;
            }

            int nb = l.Building[row[p + 1]];
            if (nb >= 0 && c.Types.Rows[c.Buildings.Type[nb]].Kind == LotZone.Service)
            {
                continue;
            }

            if (ToolsFixture.Run(sim, Command.MakeBuildService(i, hospital, tag0 + (uint)placed)) != RejectReason.None)
            {
                continue;
            }

            int bi = l.Building[i];
            var eref = new EntityRef(bi, c.Buildings.Pool.GenerationOf(bi));
            long before = c.Ledger.City;
            if (ToolsFixture.Run(sim, Command.MakeUpgradeService(eref, row[p + 1], -1, -1, tag0 + 100 + (uint)placed)) == RejectReason.None)
            {
                annexCosts?.Add((before - c.Ledger.City, nb >= 0));
                placed++;
                next = seen - 1 + step;
            }
            else
            {
                ToolsFixture.Run(sim, Command.MakeDemolishBuilding(eref, tag0 + 200 + (uint)placed));
            }
        }

        return placed;
    }

    private static int[] Row(LotStore l, int lot)
    {
        var row = new System.Collections.Generic.List<int>();
        for (int x = 0; x < l.Count; x++)
        {
            if (ServiceLevels.SameFrontage(l, x, lot))
            {
                row.Add(x);
            }
        }

        row.Sort((a, b) => ServiceLevels.Rank(l, a).CompareTo(ServiceLevels.Rank(l, b)));
        return row.ToArray();
    }

    private static bool Free(LotStore l, int i) => l.Building[i] < 0 && (l.Flags[i] & LotFlags.Lost) == 0 && l.CarLane[i] >= 0 && l.RoadComp[i] >= 0;

    /// <summary>Город сценария с набором станций: ЖКХ <paramref name="depots"/>, медцентры/больницы/медпункты, пожарные части; казна пополнена (покупка станций не часть замера).</summary>
    internal static Simulation Make(string scenario, TripExecution exec, int depots, int med, int hosp, int point, int fire, int threads = 1, Tweak? tweak = null)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(scenario, exec);
        tweak?.Invoke(ref d);
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(20);
        CityWorld c = sim.World.City!;
        c.Ledger.Transfer(c.Ledger.ExternalAccount, c.Ledger.CityAccount, 50_000_000, FlowKind.Subsidy);
        Assert.Equal(depots, LifecycleCalibrationTests.PlaceDepots(sim, depots));
        Assert.Equal(med, PlaceMedCenters(sim, med, 500, AnnexCosts));
        Assert.Equal(hosp, Place(sim, Medicine, 2, hosp, 600));
        Assert.Equal(point, Place(sim, Medicine, 1, point, 700));
        Assert.Equal(fire, Place(sim, Fire, 1, fire, 800));
        return sim;
    }

    /// <summary>Хэши на тиках 20 + k · DayTicks / 8, k = 1..8 (сутки) для города сценария с набором станций.</summary>
    private static ulong[] Hashes(string scenario, TripExecution exec, int depots, int med, int hosp, int point, int fire)
    {
        using Simulation sim = Make(scenario, exec, depots, med, hosp, point, fire);
        int step = sim.World.City!.DayTicks / 8;
        var h = new ulong[8];
        for (int k = 0; k < h.Length; k++)
        {
            sim.Step(step);
            h[k] = sim.ComputeStateHash();
        }

        Assert.True(sim.World.Counters.FiresStarted + sim.World.Counters.IllStarted > 0, "ни болезней, ни пожаров - эталон не проверяет службы");
        return h;
    }

    // Эталоны сняты в SR6 на первых сценариях s1k-res-sr / s10k-res-sr: ЖКХ 3 (12), медцентр 1 (2), медпункт 1 (4), больница 0 (1), пожарная часть 1 (5). Переснять: TC_SR6_CAPTURE=1, вывод - в TC_SR6_OUT.
    private static readonly ulong[] _s1kAgents =
    {
        0xb5d7526d6dd9eaccUL, 0xc887203154407d68UL, 0x48060ba66c07c770UL, 0xcff0719fe155a4b5UL,
        0x00576aac8d8d2028UL, 0x232bee6f16b9618dUL, 0xe0f557fdcc154330UL, 0xf799fd7222a6a219UL,
    };
    private static readonly ulong[] _s1kAggregate =
    {
        0x378d26322b6b5fe6UL, 0xba07a9fa61f04fd3UL, 0x991cf798fabb5b61UL, 0x03f840a3e9d09ebcUL,
        0x119e0993fba8603aUL, 0x70762b431c561be5UL, 0xa48ad26a14c847e6UL, 0x45eb84a4a6cbf6dcUL,
    };
    private static readonly ulong[] _s10kAggregate =
    {
        0xe96a9640398b8f01UL, 0x1dc509d35e29439fUL, 0xb9b366c0d887ac17UL, 0x4e223da1eb85b0feUL,
        0xd15e48637af5e38dUL, 0x0918d93320288177UL, 0x1dc19a4770fae193UL, 0x2c5f44463c45843eUL,
    };
    private static readonly ulong[] _s10kAgents =
    {
        0x955e1fde58bd5040UL, 0x4320b0e54efba6c0UL, 0x55a5dbabde0fbd4bUL, 0xc2bea854e7a8a80eUL,
        0x3fb352ec220cb1efUL, 0x274f2616d4a618dcUL, 0xd2091661e43df9d6UL, 0x34ab279e4639429dUL,
    };

    [Theory]
    [InlineData("s1k-res-sr", TripExecution.Agents)]
    [InlineData("s1k-res-sr", TripExecution.Aggregate)]
    [InlineData("s10k-res-sr", TripExecution.Aggregate)]
    [InlineData("s10k-res-sr", TripExecution.Agents)]
    public void Hash_Reference(string scenario, TripExecution exec)
    {
        bool big = scenario == "s10k-res-sr";
        if (big && exec == TripExecution.Agents && Environment.GetEnvironmentVariable("TC_LONG") != "1")
        {
            return;   // ≈ 6 мин в Debug: только с TC_LONG=1
        }

        ulong[] got = big ? Hashes(scenario, exec, 12, 2, 1, 4, 5) : Hashes(scenario, exec, 3, 1, 0, 1, 1);
        if (Environment.GetEnvironmentVariable("TC_SR6_CAPTURE") == "1")
        {
            var sb = new StringBuilder();
            sb.Append(scenario).Append(' ').Append(exec).Append(':');
            foreach (ulong v in got)
            {
                sb.Append(" 0x").Append(v.ToString("x16", CultureInfo.InvariantCulture)).Append("UL,");
            }

            File.AppendAllText(Env("TC_SR6_OUT", Path.Combine(Path.GetTempPath(), "sr6-hash.txt")), sb.AppendLine().ToString());
            return;
        }

        bool agents = exec == TripExecution.Agents;
        ulong[] want = big ? (agents ? _s10kAgents : _s10kAggregate) : agents ? _s1kAgents : _s1kAggregate;
        Assert.Equal(want, got);
    }

    /// <summary>Сценарии включают режим служб с записями базового пакета: утилита, медицина, пожарные; пожар задан сценарием, в базе его нет.</summary>
    [Theory]
    [InlineData("s1k-res-sr")]
    [InlineData("s10k-res-sr")]
    [InlineData("game-city")]
    [InlineData("ys0-30k")]
    [InlineData("ys0-50k")]
    [InlineData("ys0-100k")]
    public void Scenario_EnablesServices_WithBaseRecords(string scenario)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(scenario);
        ref CityServicesDesc s = ref d.Town.Services;
        Assert.True(s.Enabled);
        Assert.Equal(3, s.Records.Length);
        Assert.Equal(CityDesc.BaseBuildingTypes().Length + CityDesc.BaseServiceBuildingTypes().Length, d.Town.BuildingTypes.Length);
        Assert.Equal(60_000, s.FireSpreadPpm);
        foreach (BuildingTypeDesc t in d.Town.BuildingTypes)
        {
            Assert.Equal(t.Kind is LotZone.Residential or LotZone.Commercial or LotZone.Industrial, t.FirePpm > 0);
        }
    }

    /// <summary>
    /// Баланс пожара (services.md §8 тест 6; решение владельца 10.1 Б - огонь перекидывается): два прогона одного seed за 10 суток - с пожарной частью и без. Ущерб без части (цена восстановления
    /// сгоревших зданий по прайсу капремонта BL) не меньше 1.5 × расход части за период (содержание, машины, вызовы). Ущерб считается ценой восстановления, а не налогом: сгоревший дом
    /// за 10 суток теряет казне доли процента потока (жители и фирмы находят замену), поэтому по налогу два прогона неотличимы от шума.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FireStation_PaysOff_AtScenarioFireRate(int seed)
    {
        FireOutcome with = FireTwin("s1k-res-sr", true, 10, seed);
        FireOutcome without = FireTwin("s1k-res-sr", false, 10, seed);
        long loss = without.Damage - with.Damage;
        long expense = with.FireCost - without.FireCost;
        Assert.True(without.Burned >= 3, $"без части сгорело {without.Burned}: тест не проверяет баланс");
        Assert.True(expense > 0 && with.Burned < without.Burned);
        Assert.True(loss * 2 >= expense * 3, $"ущерб без части {loss}, расход части {expense}, сгорело {without.Burned} против {with.Burned}");
    }

    [Fact]
    public void Determinism_Threads_1_2_3()
    {
        ulong first = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Busy(threads);
            sim.Step(6_000);
            Assert.True(sim.World.Counters.CallsQueued > 0 && sim.World.Counters.CallsDone > 0, "вызовов не было - тест не проверяет службы");
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                first = h;
            }

            Assert.Equal(first, h);
        }
    }

    [Fact]
    public void Services_ZeroAllocations_PerTick()
    {
        using Simulation sim = Busy(1);
        CityWorld c = sim.World.City!;
        sim.Step(c.DayTicks + 6_000);   // прогрев: сутки событий (болезни, вызовы, естественные пожары по fire_ppm сценария)
        long bytes = AllocationProbe.Measure(() => sim.Step(6_000));   // окно планов дня: бросок, выбор станции, машины служб в пути
        Assert.True(sim.World.Counters.CallsDone > 0 && sim.World.Counters.CareVisits > 0);
        Assert.Equal(0L, bytes);
    }

    /// <summary>s1k-res-sr с набором станций, одним критическим больным и одним пожаром на первом тике (машины служб на дороге сразу).</summary>
    private static Simulation Busy(int threads)
    {
        Simulation sim = Make("s1k-res-sr", TripExecution.Agents, 3, 1, 0, 1, 1, threads);
        World w = sim.World;
        CityWorld c = w.City!;
        sim.Step(c.DayTicks / 4);   // утро: жители заняты, дороги живые
        int r = -1;
        for (int i = 0; i < c.Residents.HighWater && r < 0; i++)
        {
            r = c.Residents.IsAlive(i) && c.Residents.Home[i] >= 0 ? i : -1;
        }

        Assert.True(r >= 0);
        Illness.Onset(w, c, r, 3);
        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            if (Flammable(c, i) && c.Lots.CarLane[c.Buildings.Lot[i]] >= 0)
            {
                Assert.True(Fires.Start(w, c, i));
                break;
            }
        }

        return sim;
    }

    [Fact]
    public void Report_Run()
    {
        if (Environment.GetEnvironmentVariable("TC_CALIB_SR") != "1")
        {
            return;
        }

        string scen = Env("TC_SR_SCEN", "s1k-res-sr");
        int days = EnvI("TC_SR_DAYS", 10);
        bool agents = Env("TC_SR_EXEC", "agg") == "agents";
        using Simulation sim = Make(scen, agents ? TripExecution.Agents : TripExecution.Aggregate, EnvI("TC_SR_DEPOT", 3), EnvI("TC_SR_MED", 1), EnvI("TC_SR_HOSP", 0), EnvI("TC_SR_POINT", 0), EnvI("TC_SR_FIRE", 1),
            EnvI("TC_SR_THREADS", 1), Apply);
        var sb = new StringBuilder();
        CityWorld c = sim.World.City!;
        sim.GetCityStats(out CityStats s0);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{scen} days={days} pop0={s0.Population} balCity0={s0.BalanceCity}");
        long popSum = 0;
        for (int day = 1; day <= days; day++)
        {
            sim.Step(c.DayTicks);
            sim.GetCityStats(out CityStats s);
            popSum += s.Population;
            SimCounters k = sim.World.Counters;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"d{day} pop={s.Population} bal={s.BalanceCity} ill={k.IllStarted} care={k.CareVisits}/{k.CareRefused} icu={k.IcuAdmitted}/{k.IcuOverflow} lostAmb={k.ResidentsLostNoAmbulance} fires={k.FiresStarted}/{k.FiresOut}/{k.BuildingsBurned} spread={k.FiresSpread} capped={k.FiresCapped} calls={k.CallsDone}/{k.CallsQueued} late={k.CallsLate} stuck={s.ResStuck}");
        }

        for (int r = 0; r < sim.ServiceRecordCount; r++)
        {
            sim.GetServiceStats(r, out ServiceStats st);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"rec{r}: stations={st.Stations} units={st.UnitsOnLine}/{st.UnitsMax} calls={st.CallsDone}/{st.CallsQueued} late={st.CallsLate} avgResp={st.AvgRespTicks * 0.05:F0}s cost/day={st.CostYesterday} beds={st.BedsUsed}/{st.Beds} icu={st.IcuUsed}/{st.Icu} visitsToday={st.VisitsToday}");
        }

        sb.Append("flows yesterday:");
        for (int f = 0; f < Ledger.FlowCount; f++)
        {
            if (c.Ledger.FlowYesterday[f] != 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {(FlowKind)f}={c.Ledger.FlowYesterday[f]}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"popSum={popSum}");
        foreach ((long cost, bool occ) in AnnexCosts)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"annex upgrade hospital->medcenter: total={cost} (step build_cost 120000) neighbourOccupied={occ}");
        }
        File.AppendAllText(Env("TC_CALIB_OUT", Path.Combine(Path.GetTempPath(), "sr-report.txt")), sb.ToString());
    }

    /// <summary>Итог двойника «пожар в типовом квартале» (<see cref="FireTwin"/>): казна, расход пожарной службы, сгоревшее.</summary>
    internal readonly record struct FireOutcome(long City, long FireCost, long Burned, long Started, long Spread, int Neighbours, int Population, long Damage);

    private static bool Flammable(CityWorld c, int bi) =>
        c.Buildings.IsAlive(bi) && c.Buildings.State[bi] == (byte)BuildingState.Active && c.Types.Rows[c.Buildings.Type[bi]].Kind is LotZone.Residential or LotZone.Commercial or LotZone.Industrial;

    /// <summary>
    /// Двойник «баланс пожара» (services.md §8 тест 6): один seed, <paramref name="days"/> суток естественных пожаров (<c>fire_ppm</c> сценария), <paramref name="station"/> - пожарная часть в городе или нет.
    /// Ущерб - цена восстановления по прайсу капремонта BL (§2.9) для зданий R/C/I, бывших активными на начало, а к концу сгоревших дотла: <c>места · restore_per_unit · Cond / 100 %</c>.
    /// Расход части - приращение потока <c>ServiceUpkeep</c> за период (содержание, машины, вызовы).
    /// </summary>
    internal static FireOutcome FireTwin(string scenario, bool station, int days, int seedShift = 0, int fireStations = 1, Tweak? tweak = null)
    {
        using Simulation sim = Make(scenario, TripExecution.Aggregate, 3, 0, 0, 0, station ? fireStations : 0, 1, (ref ScenarioDesc d) =>
        {
            d.Seed += (ulong)seedShift;
            tweak?.Invoke(ref d);
        });
        World w = sim.World;
        CityWorld c = w.City!;
        sim.Step(c.DayTicks);
        var snap = new System.Collections.Generic.List<(int Bi, int Gen, long Cap, int Cond)>();
        for (int j = 0; j < c.Buildings.HighWater; j++)
        {
            if (Flammable(c, j))
            {
                snap.Add((j, c.Buildings.Pool.GenerationOf(j), c.Types.Rows[c.Buildings.Type[j]].Capacity, c.Buildings.Cond[j]));
            }
        }

        long cost0 = c.Ledger.FlowTotal[(int)FlowKind.ServiceUpkeep];
        long burned0 = w.Counters.BuildingsBurned;
        long started0 = w.Counters.FiresStarted;
        sim.Step(days * c.DayTicks);
        long damage = 0;
        long destroyed = 0;
        foreach ((int bi, int gen, long cap, int cond0) in snap)
        {
            if (!c.Buildings.IsAlive(bi) || c.Buildings.Pool.GenerationOf(bi) != gen)
            {
                damage += cap * c.Cfg.Lifecycle.RestorePerUnit * cond0 / 10_000;
                destroyed++;
            }
        }

        SimCounters k = w.Counters;
        return new FireOutcome(0, c.Ledger.FlowTotal[(int)FlowKind.ServiceUpkeep] - cost0, k.BuildingsBurned - burned0, k.FiresStarted - started0, k.FiresSpread, (int)destroyed, snap.Count, damage);
    }

    [Fact]
    public void FireBalance_Run()
    {
        if (Environment.GetEnvironmentVariable("TC_CALIB_SR") != "1")
        {
            return;
        }

        string scen = Env("TC_SR_SCEN", "s1k-res-sr");
        var sb = new StringBuilder();
        for (int seed = 0; seed < EnvI("TC_SR_SEEDS", 3); seed++)
        {
            FireOutcome with = FireTwin(scen, true, EnvI("TC_SR_DAYS", 10), seed, 1, Apply);
            FireOutcome without = FireTwin(scen, false, EnvI("TC_SR_DAYS", 10), seed, 1, Apply);
            long loss = without.Damage - with.Damage;
            long expense = with.FireCost - without.FireCost;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"seed+{seed}: buildings={with.Population} with: started={with.Started} burned={with.Burned} destroyed={with.Neighbours} dmg={with.Damage} cost={with.FireCost} | without: started={without.Started} burned={without.Burned} destroyed={without.Neighbours} dmg={without.Damage} spread={without.Spread} | loss={loss} expense={expense} ratio={(double)loss / expense:F2}");
        }

        File.AppendAllText(Env("TC_CALIB_OUT", Path.Combine(Path.GetTempPath(), "sr-report.txt")), sb.ToString());
    }

    /// <summary>Ручные подстановки калибровки (<c>TC_SR_*</c>); без переменных ничего не меняют.</summary>
    internal static void Apply(ref ScenarioDesc d)
    {
        ref CityServicesDesc s = ref d.Town.Services;
        s.FireCap = EnvI("TC_SR_FIRECAP", s.FireCap);
        s.FireSpreadPpm = EnvI("TC_SR_SPREAD", s.FireSpreadPpm);
        s.SmolderS = EnvI("TC_SR_SMOLDER", s.SmolderS);
        s.IllLightPpm = EnvI("TC_SR_ILL_L", s.IllLightPpm);
        s.IllInpatientPpm = EnvI("TC_SR_ILL_I", s.IllInpatientPpm);
        s.IllCriticalPpm = EnvI("TC_SR_ILL_C", s.IllCriticalPpm);
        int fr = EnvI("TC_SR_FIRE_R", -1);
        if (fr >= 0)
        {
            BuildingTypeDesc[] t = d.Town.BuildingTypes;
            for (int i = 0; i < t.Length; i++)
            {
                t[i].FirePpm = t[i].Kind switch
                {
                    LotZone.Residential => fr,
                    LotZone.Commercial => EnvI("TC_SR_FIRE_C", fr),
                    LotZone.Industrial => EnvI("TC_SR_FIRE_I", fr),
                    _ => 0,
                };
            }
        }
    }
}
