using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Scenarios;
using Xunit.Abstractions;

namespace Sim.Core.Tests.City;

/// <summary>
/// Потоки и казна (E2, city-economy.md §15 «Потоки»): фаззер проводок и EC1 по дням, налог жителя, налог на прибыль, плата за проезд (micro и aggregate), содержание, проценты, <c>Insolvent</c>
/// и события, буферы жителей и фирм, <c>LV</c>, <c>Enable</c> на идущем городе (EC8), сейв посреди потоков, хэш по потокам и 0 байт на тик на сценарии <c>s1k-res-econ</c>.
/// </summary>
public sealed class BudgetFlowsTests
{
    private readonly ITestOutputHelper _out;

    public BudgetFlowsTests(ITestOutputHelper output) => _out = output;

    private const int Day = 72_000;       // сутки сценария s1k-res: агрегат
    private const int MicroDay = 28_800;  // сутки микро-тестов (ноги агентов успевают закончиться, тест короче)

    /// <summary>
    /// Сценарий <c>s1k-res.json</c> (параметры выбора из файла, иначе экономика нереалистична) с бюджетом; по умолчанию ноги считает агрегат (быстро), <paramref name="micro"/> - агенты
    /// с короткими сутками <see cref="MicroDay"/>.
    /// </summary>
    private static ScenarioDesc Short(bool budget = true, bool micro = false)
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json"));
        ScenarioDesc d = sc.Desc;
        d.Town.Economy.CityBudget = budget;
        if (micro)
        {
            d.Town.Calendar.DayTicks = MicroDay;
        }
        else
        {
            d.Town.Residents.TripExecution = TripExecution.Aggregate;
        }

        return d;
    }

    /// <summary>Настоящий <c>scenarios/s1k-res-econ.json</c> с микро-агентами и короткими сутками (граница суток попадает в окна тестов).</summary>
    private static ScenarioDesc EconScenario()
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res-econ.json"));
        ScenarioDesc d = sc.Desc;
        d.Town.Calendar.DayTicks = MicroDay;
        return d;
    }

    /// <summary>
    /// Все туры, уже спланированные на сегодня, - автобусом (в сценарии s1k-res выбор способа автобус не берёт, и плата за проезд иначе не проверяется). Вызывать после плана дня
    /// (<c>plan_slot</c> + 12 слотов) и до выезда.
    /// </summary>
    private static void ForceBus(Simulation sim)
    {
        CityResidents r = City(sim).Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            for (int k = 0; k < r.PlanCount[i]; k++)
            {
                r.PlanMode[(i * CityResidents.MaxTours) + k] = (byte)TravelMode.Bus;
            }
        }
    }

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    private static void StepChecked(Simulation sim, int ticks)
    {
        Ledger lg = City(sim).Ledger;
        for (int done = 0; done < ticks; done += 60)
        {
            sim.Step(Math.Min(60, ticks - done));
            Assert.Equal(0, lg.SumAll());
        }
    }

    // ------------------------------------------------------------------ EC1

    [Fact]
    public void Fuzzer_RandomTransfers_KeepZeroSum()
    {
        using Simulation sim = Make(Short());
        CityWorld c = City(sim);
        Ledger lg = c.Ledger;
        ulong x = 0x2545F4914F6CDD1DUL;
        int Next()
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            return (int)(x >> 33);
        }

        int Account()
        {
            return (Next() % 4) switch
            {
                0 => Ledger.AccountOfResident(Next() % c.Residents.HighWater),
                1 => lg.AccountOfBuilding(Next() % c.Buildings.HighWater),
                2 => lg.ExternalAccount,
                _ => lg.CityAccount,
            };
        }

        for (int i = 0; i < 20_000; i++)
        {
            int from = Account();
            int to = Account();
            var kind = (FlowKind)(Next() % Ledger.FlowCount);
            if (Next() % 2 == 0)
            {
                lg.Transfer(from, to, (Next() % 2_000_001) - 1_000_000, kind);
            }
            else
            {
                lg.TransferPct(from, to, Next() % 5_000_000, Next() % (Ledger.Bp + 1), kind);
            }

            if (i % 50 == 0)
            {
                Assert.Equal(0, lg.SumAll());
            }
        }

        Assert.Equal(0, lg.SumAll());
        for (int k = 0; k < Ledger.FlowCount; k++)
        {
            Assert.InRange(c.Budget!.Carry[k], 0, Ledger.Bp - 1);
        }
    }

    [Fact]
    public void Days_KeepZeroSum_AndTreasuryDeltaEqualsFlows()
    {
        using Simulation sim = Make(Short());
        CityWorld c = City(sim);
        Ledger lg = c.Ledger;
        Assert.True(lg.City > 0, "вклад в казну при создании");
        Assert.Equal(60L * c.Residents.AliveCount, lg.City);
        long previous = lg.City;
        for (int day = 1; day <= 6; day++)
        {
            StepChecked(sim, Day);
            long[] y = lg.FlowYesterday;
            long delta = y[(int)FlowKind.Tax] + y[(int)FlowKind.Rent] + y[(int)FlowKind.Fare]
                         - y[(int)FlowKind.Maintenance] - y[(int)FlowKind.Interest] - y[(int)FlowKind.Construction] + y[(int)FlowKind.Refund];
            Assert.Equal(delta, lg.City - previous);
            previous = lg.City;
        }

        Assert.True(lg.FlowTotal[(int)FlowKind.Tax] > 0);
        Assert.True(lg.FlowTotal[(int)FlowKind.Rent] > 0);
    }

    // ------------------------------------------------------------------ налоги, земля, сборы

    [Fact]
    public void ResidentTax_IsExactShareOfWages()
    {
        ScenarioDesc d = Short();
        d.Town.Economy.TaxCProfitBp = 0;
        d.Town.Economy.TaxIProfitBp = 0;
        using Simulation sim = Make(d);
        StepChecked(sim, 3 * Day);
        Ledger lg = City(sim).Ledger;
        long wages = lg.FlowTotal[(int)FlowKind.Wage];
        Assert.True(wages > 0);
        Assert.Equal(wages * d.Town.Economy.TaxRBp, (lg.FlowTotal[(int)FlowKind.Tax] * Ledger.Bp) + City(sim).Budget!.Carry[(int)FlowKind.Tax]);
    }

    [Fact]
    public void FirmTax_IgnoresLoss_AndTaxesPositiveProfit()
    {
        using Simulation sim = Make(Short());
        sim.Step(Day + 10);
        CityWorld c = City(sim);
        BuildingStore b = c.Buildings;
        int firm = -1;
        for (int i = 0; i < b.HighWater && firm < 0; i++)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind != LotZone.Residential)
            {
                firm = i;
            }
        }

        Assert.True(firm >= 0);
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[firm]];
        long before = c.Ledger.FlowTotal[(int)FlowKind.Tax];
        long taxLoss = BudgetFlows.FirmTaxAndRent(c, firm, in t, -5_000, 0);
        Assert.Equal(0, taxLoss);
        Assert.Equal(before, c.Ledger.FlowTotal[(int)FlowKind.Tax]);
        long tax = BudgetFlows.FirmTaxAndRent(c, firm, in t, 10_000, 0);
        int rate = t.Kind == LotZone.Commercial ? c.Budget!.Policy.TaxCBp : c.Budget!.Policy.TaxIBp;
        Assert.InRange(tax, (10_000L * rate / Ledger.Bp) - 1, (10_000L * rate / Ledger.Bp) + 1);
        Assert.Equal(0, c.Ledger.SumAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fare_IsChargedOnBusLegs_InMicroAndAggregate(bool aggregate)
    {
        ScenarioDesc d = Short(micro: !aggregate);
        d.Town.Economy.BusFare = 3;
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        sim.Step((int)c.TickOfSlot(0, 62));   // планы дня построены (plan_slot 36 + до 12), выезды ещё впереди
        ForceBus(sim);
        StepChecked(sim, c.DayTicks);
        Ledger lg = c.Ledger;
        long fares = lg.FlowTotal[(int)FlowKind.Fare];
        long evaded = sim.World.Counters.FareEvaded;
        Assert.True(fares > 0, "нет автобусных ног с оплатой");
        Assert.Equal(0, fares % 3);
        Assert.Equal(fares / 3, lg.FlowTotal[(int)FlowKind.Fare] / 3);
        Assert.True(evaded >= 0);
        Assert.Equal(0, lg.SumAll());
    }

    [Fact]
    public void Fare_WithoutEnoughMoney_IsEvaded_AndCounted()
    {
        using Simulation sim = Make(Short());
        sim.Step(10);
        CityWorld c = City(sim);
        int r = -1;
        for (int i = 0; i < c.Residents.HighWater && r < 0; i++)
        {
            r = c.Residents.Pool.IsAlive(i) ? i : -1;
        }

        c.Ledger.Transfer(Ledger.AccountOfResident(r), c.Ledger.ExternalAccount, c.Residents.Account[r], FlowKind.Emigration);   // баланс 0, сумма счетов сохраняется
        Assert.Equal(0, c.Residents.Account[r]);
        long before = c.Ledger.FlowTotal[(int)FlowKind.Fare];
        BudgetFlows.ChargeFare(sim.World, c, r);
        Assert.Equal(before, c.Ledger.FlowTotal[(int)FlowKind.Fare]);
        Assert.Equal(1, sim.World.Counters.FareEvaded);
        Assert.Equal(1, c.Budget!.FareEvadedDay[c.Day & 1]);
        c.Ledger.Transfer(c.Ledger.ExternalAccount, Ledger.AccountOfResident(r), 50, FlowKind.Emigration);
        BudgetFlows.ChargeFare(sim.World, c, r);
        Assert.Equal(before + c.Budget.Policy.Fare, c.Ledger.FlowTotal[(int)FlowKind.Fare]);
        Assert.Equal(0, c.Ledger.SumAll());
    }

    [Fact]
    public void LandValue_StartsAtTarget_StaysInRange_AndFeedsRent()
    {
        using Simulation sim = Make(Short());
        CityWorld c = City(sim);
        CityBudget b = c.Budget!;
        CityEconomyDesc e = c.Cfg.Economy;
        bool differs = false;
        for (int cell = 0; cell < b.LandValue.Length; cell++)
        {
            Assert.Equal(BudgetLand.Target(in e, c.Skims, cell), b.LandValue[cell]);   // LV = T при создании
            Assert.InRange(b.LandValue[cell], e.LvMin, e.LvMax);
            differs |= b.LandValue[cell] != e.LvRef;
        }

        Assert.True(differs, "LV не зависит от доступности");
        StepChecked(sim, 4 * Day);
        int span = sim.GetLandValue(new int[b.LandValue.Length]);
        Assert.Equal(b.LandValue.Length, span);
        for (int cell = 0; cell < b.LandValue.Length; cell++)
        {
            Assert.InRange(b.LandValue[cell], e.LvMin, e.LvMax);
        }

        Assert.True(c.Ledger.FlowTotal[(int)FlowKind.Rent] > 0);
    }

    // ------------------------------------------------------------------ содержание

    [Fact]
    public void Maintenance_FollowsTheFormula_AndIsPaidAtDayBoundary()
    {
        ScenarioDesc d = Short();
        d.Town.Economy.MaintRoadStreetPerLaneKmDay = 1000;
        d.Town.Economy.MaintRoadArterialPerLaneKmDay = 3000;
        d.Town.Economy.MaintBusDay = 500;
        d.Town.Economy.MaintStopDay = 20;
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        CityBudget b = c.Budget!;
        NetworkStore net = sim.World.Net;
        long street = 0;
        long art = 0;
        for (int i = 0; i < net.SegmentCount; i++)
        {
            SegmentData s = net.Segments[i];
            if (!NetworkAlive.IsAlive(s.Generation))
            {
                continue;
            }

            long laneMm = (long)(s.LanesAB + s.LanesBA) * (long)(s.Length * 1000f);
            if (s.Kind == RoadKind.Arterial)
            {
                art += laneMm;
            }
            else
            {
                street += laneMm;
            }
        }

        Assert.InRange(b.MaintRoadDay, ((street * 1000) + (art * 3000)) / 1_000_000 - 2, ((street * 1000) + (art * 3000)) / 1_000_000 + 2);
        Assert.True(b.MaintRoadDay > 0);
        long stops = 0;
        int routes = 0;
        for (int r = 0; r < net.BusRouteCount; r++)
        {
            if (NetworkAlive.IsAlive(net.BusRoutes[r].Generation))
            {
                stops += net.BusRoutes[r].StopCount;
                routes++;
            }
        }

        Assert.True(routes > 0);
        Assert.Equal(0, (b.MaintBusDay - (stops * 20)) % 500);
        Assert.True((b.MaintBusDay - (stops * 20)) / 500 >= routes);
        long expected = b.MaintRoadDay + b.MaintBusDay;
        StepChecked(sim, Day);
        Assert.Equal(expected, c.Ledger.FlowYesterday[(int)FlowKind.Maintenance]);
    }

    [Fact]
    public void Maintenance_IsRecomputedAfterLoad()
    {
        ScenarioDesc d = Short();
        d.Town.Economy.MaintRoadStreetPerLaneKmDay = 1000;
        d.Town.Economy.MaintBusDay = 500;
        using Simulation sim = Make(d);
        sim.Step(100);
        using Simulation again = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim));
        Assert.Equal(City(sim).Budget!.MaintRoadDay, City(again).Budget!.MaintRoadDay);
        Assert.Equal(City(sim).Budget!.MaintBusDay, City(again).Budget!.MaintBusDay);
        Assert.True(City(again).Budget!.MaintRoadDay > 0);
    }

    // ------------------------------------------------------------------ долг, проценты, лимит

    [Fact]
    public void Debt_PaysInterest_BecomesInsolvent_AndRecovers()
    {
        ScenarioDesc d = Short();
        d.Town.Economy.InitialCityBalancePerResident = 0;
        d.Town.Economy.CreditDays = 0;
        d.Town.Economy.MaintBusDay = 20_000;
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        Ledger lg = c.Ledger;
        bool negative = false;
        bool limit = false;
        sim.Step(Day - 1);
        Assert.Equal(0, c.Budget!.Insolvent);
        sim.Step(1);   // первая граница суток
        foreach (SimEvent e in sim.Events)
        {
            negative |= e.Kind == SimEventKind.BudgetNegative && e.A == 1 && e.B < 0;
            limit |= e.Kind == SimEventKind.BudgetLimitReached && e.A == 1;
        }

        Assert.True(negative, "нет BudgetNegative");
        Assert.True(limit, "нет BudgetLimitReached");
        Assert.True(lg.City < 0);
        Assert.Equal(1, c.Budget.Insolvent);
        Assert.Equal(1, c.Budget.DebtDays);
        Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
        Assert.Equal((byte)1, info.Insolvent);
        Assert.True(info.Balance < -info.CreditLimit);
        Assert.Equal(0, lg.SumAll());

        StepChecked(sim, Day);
        Assert.Equal(2, c.Budget.DebtDays);
        long interest = lg.FlowYesterday[(int)FlowKind.Interest];
        long debt = -lg.City;   // процент ~ 3 б.п. от базы (долг + содержание - доход за сутки), а долг уже включает его
        long expected = debt * d.Town.Economy.InterestBpDay / Ledger.Bp;
        Assert.True(interest > 0);
        Assert.InRange(interest, expected - 2, expected + 2);

        lg.Transfer(lg.ExternalAccount, lg.CityAccount, 2_000_000_000L, FlowKind.Endowment);
        sim.Step(Day - 1);
        sim.Step(1);
        bool recovered = false;
        foreach (SimEvent e in sim.Events)
        {
            recovered |= e.Kind == SimEventKind.BudgetRecovered;
        }

        Assert.True(recovered, "нет BudgetRecovered");
        Assert.Equal(0, c.Budget.Insolvent);
        Assert.Equal(0, c.Budget.DebtDays);
        Assert.Equal(0, lg.SumAll());
    }

    [Fact]
    public void CreditLimit_IsCreditDaysTimesIncomeEwma_AndEwmaTracksFlows()
    {
        using Simulation sim = Make(Short());
        CityWorld c = City(sim);
        StepChecked(sim, 5 * Day);
        CityBudget b = c.Budget!;
        Assert.True(b.IncomeEwma > 0);
        Assert.Equal(60 * b.IncomeEwma, b.CreditLimit(in c.Cfg.Economy, 0));
        long y = c.Ledger.FlowYesterday[(int)FlowKind.Tax] + c.Ledger.FlowYesterday[(int)FlowKind.Rent] + c.Ledger.FlowYesterday[(int)FlowKind.Fare];
        Assert.InRange(b.IncomeEwma, y / 4, y * 4);
        Assert.Equal(0, b.Insolvent);
    }

    [Fact]
    public void DailyCounters_AreZeroedForNewDay()
    {
        using Simulation sim = Make(Short());
        CityWorld c = City(sim);
        sim.Step(Day - 3);
        c.Budget!.FareEvadedDay[1] = 7;       // «завтрашний» элемент пары (день 0 -> 1 на границе)
        c.Budget.DebtEvictionsDay[1] = 5;
        sim.Step(3);
        Assert.Equal(0, c.Budget.FareEvadedDay[1]);
        Assert.Equal(0, c.Budget.DebtEvictionsDay[1]);
    }

    // ------------------------------------------------------------------ буферы

    [Fact]
    public void Buffers_BoundBalancesOfResidentsAndFirms()
    {
        using Simulation sim = Make(Short());
        StepChecked(sim, 3 * Day);   // у фирм появляется ProfitEwma
        CityWorld c = City(sim);
        Ledger lg = c.Ledger;
        int r = -1;
        for (int i = 0; i < c.Residents.HighWater && r < 0; i++)
        {
            r = c.Residents.Pool.IsAlive(i) && c.Residents.Home[i] >= 0 ? i : -1;
        }

        int f = -1;
        for (int i = 0; i < c.Buildings.HighWater && f < 0; i++)
        {
            f = c.Buildings.IsAlive(i) && c.Buildings.State[i] == (byte)BuildingState.Active && c.Types.Rows[c.Buildings.Type[i]].Kind != LotZone.Residential && c.Buildings.ProfitEwma[i] > 0 ? i : -1;
        }

        const long Gift = 1_000_000;
        Assert.True(f >= 0, "нет прибыльной фирмы");
        lg.Transfer(lg.ExternalAccount, Ledger.AccountOfResident(r), Gift, FlowKind.Endowment);
        lg.Transfer(lg.ExternalAccount, lg.AccountOfBuilding(f), Gift, FlowKind.Endowment);
        StepChecked(sim, 30 * Day);
        Assert.True(c.Buildings.IsAlive(f));
        Assert.True(c.Residents.Pool.IsAlive(r));
        Assert.True(c.Residents.Account[r] < Gift / 20, $"житель: {c.Residents.Account[r]}");
        Assert.True(c.Buildings.Account[f] < Gift / 4, $"фирма: {c.Buildings.Account[f]}");
        Assert.True(lg.FlowTotal[(int)FlowKind.Leakage] > Gift / 2);
        Assert.True(lg.FlowTotal[(int)FlowKind.Dividend] > Gift / 2);
        Assert.Equal(0, lg.SumAll());
    }

    // ------------------------------------------------------------------ Enable (EC8)

    [Fact]
    public void Enable_OnRunningCity_Ec8()
    {
        using Simulation sim = Make(Short(budget: false));
        StepChecked(sim, 3 * Day);
        CityWorld c = City(sim);
        Assert.Null(c.Budget);
        Assert.False(sim.GetCityBudget(out _));
        int before = c.Residents.AliveCount;
        long hashBefore = (long)sim.ComputeStateHash();

        BudgetPolicy p = default;
        p.Flags = BudgetPolicyFlags.Enable;
        sim.Enqueue(Command.MakeSetBudgetPolicy(in p, 3));
        sim.Step(1);
        bool applied = false;
        foreach (SimEvent e in sim.Events)
        {
            applied |= e.Tag == 3 && e.Kind == SimEventKind.CommandApplied;
        }

        Assert.True(applied, "Enable не принят");
        Assert.NotNull(c.Budget);
        Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
        Assert.True(info.Balance > 0);
        Assert.True(info.IncomeEwma > 0, "лимит в первый день не должен быть нулевым");
        Assert.NotEqual(hashBefore, (long)sim.ComputeStateHash());
        Assert.Equal(0, c.Ledger.SumAll());

        StepChecked(sim, 30 * Day);
        Assert.True(c.Ledger.City >= 0);
        Assert.True(c.Residents.AliveCount >= before * 0.95, $"население {before} -> {c.Residents.AliveCount}");
        Assert.True(sim.World.Counters.DebtEvictions <= before * 0.02);
        Assert.True(c.Ledger.FlowTotal[(int)FlowKind.Tax] > 0);

        // сейв после включения содержит бюджет и продолжается тем же хэшем
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation again = WorldSaveFixtures.Load(bytes);
        Assert.NotNull(City(again).Budget);
        Assert.Equal(sim.ComputeStateHash(), again.ComputeStateHash());
        sim.Step(Day);
        again.Step(Day);
        Assert.Equal(sim.ComputeStateHash(), again.ComputeStateHash());
    }

    [Fact]
    public void Enable_TwiceOrWithoutFlag_IsHandledByPolicyBranch()
    {
        using Simulation sim = Make(Short(budget: false));
        sim.Step(10);
        BudgetPolicy none = default;
        sim.Enqueue(Command.MakeSetBudgetPolicy(in none, 5));
        sim.Step(1);
        RejectReason why = RejectReason.None;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Tag == 5 && e.Kind == SimEventKind.CommandRejected)
            {
                why = (RejectReason)e.A;
            }
        }

        Assert.Equal(RejectReason.BudgetRule, why);
        Assert.Null(City(sim).Budget);
    }

    // ------------------------------------------------------------------ детерминизм и память

    [Fact]
    public void StateHash_OnEconScenario_IsIndependentOfWorkerThreads()
    {
        ulong reference = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Make(EconScenario(), threads);
            StepChecked(sim, MicroDay + 600);   // сутки микро-агентов и граница суток внутри
            Assert.True(City(sim).Ledger.FlowTotal[(int)FlowKind.Tax] > 0);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                reference = h;
            }

            Assert.Equal(reference, h);
        }
    }

    [Fact]
    public void Step_OnEconScenario_AllocatesZeroBytes()
    {
        using Simulation sim = Make(EconScenario());
        sim.Step(MicroDay - 600);
        long bytes = AllocationProbe.Measure(() => sim.Step(1200), out _);   // граница суток внутри окна (тик Day - 1)
        Assert.Equal(0, bytes);
        Assert.Equal(0, City(sim).Ledger.SumAll());
    }

    [Fact]
    public void WithoutBudget_FlowsAreSilent()
    {
        using Simulation sim = Make(Short(budget: false));
        StepChecked(sim, 2 * Day);
        Ledger lg = City(sim).Ledger;
        for (int k = Ledger.LegacyFlowCount; k < Ledger.FlowCount; k++)
        {
            Assert.Equal(0, lg.FlowTotal[k]);
        }

        Assert.Equal(0, lg.FlowTotal[(int)FlowKind.Tax]);
        Assert.Equal(0, lg.FlowTotal[(int)FlowKind.Rent]);
        Assert.Equal(0, lg.City);
    }

    [Fact]
    public void Econ30Days_ClosedLoop_NoRunaway_AndPrintsTreasurySeries()
    {
        // 30 суток s1k-res-econ (агрегат): Σ = 0 в каждый тик, казна не за лимитом, балансы жителей и фирм ограничены буферами (нет разгона), население не падает. Ряд казны - в вывод теста.
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res-econ.json")).Desc;
        d.Town.Residents.TripExecution = TripExecution.Aggregate;
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        Ledger lg = c.Ledger;
        int popStart = c.Residents.AliveCount;
        for (int day = 1; day <= 30; day++)
        {
            StepChecked(sim, Day);
            long residents = 0;
            for (int r = 0; r < c.Residents.HighWater; r++)
            {
                residents += c.Residents.Pool.IsAlive(r) ? c.Residents.Account[r] : 0;
            }

            long firms = 0;
            int firmCount = 0;
            for (int b = 0; b < c.Buildings.HighWater; b++)
            {
                if (c.Buildings.IsAlive(b) && c.Types.Rows[c.Buildings.Type[b]].Kind != LotZone.Residential)
                {
                    firms += c.Buildings.Account[b];
                    firmCount++;
                }
            }

            long[] y = lg.FlowYesterday;
            _out.WriteLine($"d{day}: city={lg.City} income={y[(int)FlowKind.Tax] + y[(int)FlowKind.Rent] + y[(int)FlowKind.Fare]} (tax {y[(int)FlowKind.Tax]}, rent {y[(int)FlowKind.Rent]}, fare {y[(int)FlowKind.Fare]}) " +
                            $"maint={y[(int)FlowKind.Maintenance]} interest={y[(int)FlowKind.Interest]} incomeEwma={c.Budget!.IncomeEwma} limit={c.Budget.CreditLimit(c.Cfg.Economy, 0)} " +
                            $"pop={c.Residents.AliveCount} resAvg={residents / Math.Max(1, c.Residents.AliveCount)} firmAvg={firms / Math.Max(1, firmCount)} wage={y[(int)FlowKind.Wage]} div={y[(int)FlowKind.Dividend]} leak={y[(int)FlowKind.Leakage]} closures={sim.World.Counters.FirmClosures}");
            Assert.Equal(0, c.Budget.Insolvent);
            Assert.True(lg.City > 0, $"казна ушла в минус на сутках {day}");
            Assert.True(residents / Math.Max(1, c.Residents.AliveCount) < 1_000, "разгон балансов жителей");
            Assert.True(firms / Math.Max(1, firmCount) < 10_000, "разгон балансов фирм");
        }

        Assert.True(c.Residents.AliveCount >= popStart * 0.95);
    }
}
