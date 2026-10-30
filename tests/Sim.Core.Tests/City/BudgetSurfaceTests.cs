using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Save;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.City;

/// <summary>
/// Поверхность казны города (E1, city-economy.md §15 «Поверхность»): <c>TransferPct</c> с переносом, <c>SetBudgetPolicy</c> и применение на границе суток, <c>GetCityBudget</c>/<c>GetLandValue</c>,
/// сейв <c>Save -&gt; Load -&gt; Save</c> побайтно с бюджетом, проверки <c>ValidateCity</c>, детерминизм по потокам, ноль байт на тик. EC0 (хэш без бюджета прежний) держат golden-тесты и корпус w1-w8.
/// </summary>
public sealed class BudgetSurfaceTests
{
    private const int ShortDay = 1440;

    private static ScenarioDesc ShortCity(bool budget = true)
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Calendar.DayTicks = ShortDay;
        d.Town.Residents.InitialFillQ16 = CityDesc.Q16(0.1);
        d.Town.Economy.CityBudget = budget;
        return d;
    }

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    private static BudgetPolicy Raised(Simulation sim)
    {
        Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
        BudgetPolicy p = info.Policy;
        p.TaxRBp = 900;
        p.Fare = 5;
        return p;
    }

    private static RejectReason Run(Simulation sim, in BudgetPolicy p, uint tag = 7)
    {
        sim.Enqueue(Command.MakeSetBudgetPolicy(in p, tag));
        sim.Step(1);
        foreach (SimEvent e in sim.Events)
        {
            if (e.Tag != tag)
            {
                continue;
            }

            if (e.Kind == SimEventKind.CommandApplied)
            {
                return RejectReason.None;
            }

            if (e.Kind == SimEventKind.CommandRejected)
            {
                return (RejectReason)e.A;
            }
        }

        throw new InvalidOperationException("нет события команды");
    }

    // ------------------------------------------------------------------ параметры, структуры, виды потоков

    [Fact]
    public void Surface_Constants_AreFrozen()
    {
        Assert.Equal(23, Ledger.FlowCount);
        Assert.Equal(22, Ledger.LifecycleFlowCount);
        Assert.Equal(22, (int)FlowKind.ServiceUpkeep);
        Assert.Equal(17, Ledger.BudgetFlowCount);
        Assert.Equal(23, Simulation.FlowKindCount);
        Assert.Equal(13, (int)FlowKind.Interest);
        Assert.Equal(14, (int)FlowKind.Dividend);
        Assert.Equal(15, (int)FlowKind.Leakage);
        Assert.Equal(16, (int)FlowKind.Refund);
        Assert.Equal(36, (int)CommandKind.SetBudgetPolicy);
        Assert.Equal(12, (int)RejectReason.InsufficientFunds);
        Assert.Equal(16, (int)RejectReason.BudgetRule);
        Assert.Equal(16, System.Runtime.CompilerServices.Unsafe.SizeOf<BudgetPolicy>());
        Assert.Equal(64, System.Runtime.CompilerServices.Unsafe.SizeOf<Command>());
        Assert.Equal(18, SaveSchema.WorldVersion);   // w18 - службы (SR1, блоки SVCR/SERV), w17 - жизнь здания (BL1), w16 - классы ТС (VC1, ADR-0039), w11 - прогрессия (PG1, ADR-0034), w12 - рельеф (TR1, ADR-0036), w13 - старт с нуля (Y4), w14 - внешние подключения (Y3), w15 - цели сценария (YS1)
    }

    [Fact]
    public void Defaults_FollowTheSpec()
    {
        CityEconomyDesc e = CityDesc.Default().Economy;
        Assert.False(e.CityBudget);
        Assert.Equal(600, e.TaxRBp);
        Assert.Equal(2000, e.TaxCProfitBp);
        Assert.Equal(2000, e.TaxIProfitBp);
        Assert.Equal(15, e.LandTaxBpDay);
        Assert.Equal(2, e.BusFare);
        Assert.Equal(60, e.InitialCityBalancePerResident);
        Assert.Equal(3, e.InterestBpDay);
        Assert.Equal(60, e.CreditDays);
        Assert.Equal((200, 20, 50, 1000, 3), (e.LvRef, e.LvSlopePerMin, e.LvMin, e.LvMax, e.LvShift));
        Assert.Equal((50, 5, 10, 3, 3, 3, 10), (e.VotPct, e.ResBufferDays, e.FirmBufferDays, e.PayoutShift, e.EvictDebtDays, e.BudgetEwmaShift, e.UndoRefundS));
        Assert.Equal((5000, 100, 1000), (e.MaxTaxBp, e.MaxLandBp, e.MaxFare));
        Assert.True(CityBudget.IsValid(in e));
    }

    [Fact]
    public void BudgetParams_AreChecked()
    {
        ScenarioDesc d = ShortCity();
        d.Town.Economy.VotPct = 0;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in d, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim));
        sim?.Dispose();
        d = ShortCity();
        d.Town.Economy.TaxRBp = d.Town.Economy.MaxTaxBp + 1;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in d, new SimRuntimeOptions { WorkerThreads = 1 }, out sim));
        sim?.Dispose();
        d = ShortCity();
        d.Town.Economy.LvMax = d.Town.Economy.LvMin - 1;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in d, new SimRuntimeOptions { WorkerThreads = 1 }, out sim));
        sim?.Dispose();
    }

    [Fact]
    public void CityBudget_WithoutResidents_IsRejected()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Town.Economy.CityBudget = true;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in d, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim));
        sim?.Dispose();
    }

    // ------------------------------------------------------------------ TransferPct

    [Fact]
    public void TransferPct_SumsAreExactToOneDenominator_AndKeepZeroSum()
    {
        using Simulation sim = Make(ShortCity());
        Ledger lg = City(sim).Ledger;
        CityBudget b = City(sim).Budget!;
        ulong x = 0x9E3779B97F4A7C15UL;
        int Next()
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            return (int)(x >> 33);
        }

        BigSum exact = default;
        long moved = 0;
        for (int i = 0; i < 10_000; i++)
        {
            long baseAmount = Next() % 1_000_000;
            int rate = Next() % (Ledger.Bp + 1);
            int from = Ledger.AccountOfResident(Next() % City(sim).Residents.HighWater);
            moved += lg.TransferPct(from, lg.CityAccount, baseAmount, rate, FlowKind.Tax);
            exact.Add(baseAmount * rate);
            Assert.InRange(b.Carry[(int)FlowKind.Tax], 0, Ledger.Bp - 1);
        }

        Assert.Equal(0, lg.SumAll());
        Assert.Equal(moved, lg.FlowTotal[(int)FlowKind.Tax]);
        Assert.Equal(exact.Value, (moved * Ledger.Bp) + b.Carry[(int)FlowKind.Tax]);   // точно до одного D
        for (int k = 0; k < Ledger.FlowCount; k++)
        {
            if (k != (int)FlowKind.Tax && k != (int)FlowKind.Endowment)
            {
                Assert.Equal(0, b.Carry[k]);
            }
        }
    }

    [Fact]
    public void TransferPct_SmallPayments_AlternateRounding()
    {
        using Simulation sim = Make(ShortCity());
        Ledger lg = City(sim).Ledger;
        // 7 * 15 / 10000 = 0.0105: платежей 10000 штук в сумме дают ровно 105 (а не 0 при округлении каждой проводки вниз).
        long sum = 0;
        for (int i = 0; i < 10_000; i++)
        {
            sum += lg.TransferPct(Ledger.AccountOfResident(0), lg.CityAccount, 7, 15, FlowKind.Rent);
        }

        Assert.Equal(10_000L * 7 * 15 / Ledger.Bp, sum);
        Assert.Equal(0, lg.SumAll());
    }

    [Fact]
    public void TransferPct_WithoutBudget_RoundsDown()
    {
        using Simulation sim = Make(ShortCity(budget: false));
        Ledger lg = City(sim).Ledger;
        Assert.Null(City(sim).Budget);
        Assert.Null(lg.Carry);
        Assert.Equal(0, lg.TransferPct(Ledger.AccountOfResident(0), lg.CityAccount, 7, 15, FlowKind.Rent));
        Assert.Equal(63, lg.TransferPct(Ledger.AccountOfResident(0), lg.CityAccount, 10_000, 63, FlowKind.Rent));
        Assert.Equal(0, lg.SumAll());
    }

    private struct BigSum
    {
        public long Value;

        public void Add(long v) => Value += v;
    }

    // ------------------------------------------------------------------ API чтения

    [Fact]
    public void GetCityBudget_AndLandValue_ExistOnlyWithBudget()
    {
        using Simulation off = Make(ShortCity(budget: false));
        Assert.False(off.GetCityBudget(out _));
        Assert.Equal(0, off.GetLandValue(new int[off.CityCellCount]));
        Assert.Null(City(off).Budget);

        using Simulation sim = Make(ShortCity());
        Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
        CityEconomyDesc e = City(sim).Cfg.Economy;
        Assert.Equal(City(sim).Ledger.City, info.Balance);
        Assert.Equal(e.CreditDays * City(sim).Budget!.IncomeEwma, info.CreditLimit);   // E2: оценка дохода при создании
        Assert.Equal((ushort)e.TaxRBp, info.Policy.TaxRBp);
        Assert.Equal((ushort)e.TaxCProfitBp, info.Policy.TaxCBp);
        Assert.Equal((ushort)e.TaxIProfitBp, info.Policy.TaxIBp);
        Assert.Equal((ushort)e.LandTaxBpDay, info.Policy.LandTaxBpDay);
        Assert.Equal((ushort)e.BusFare, info.Policy.Fare);
        Assert.Equal(BudgetPolicyFlags.Enable, info.Policy.Flags);
        Assert.Equal(0, info.HasPending);
        Assert.Equal(0, info.Insolvent);
        var lv = new int[sim.CityCellCount + 3];
        Assert.Equal(sim.CityCellCount, sim.GetLandValue(lv));
        for (int i = 0; i < sim.CityCellCount; i++)
        {
            Assert.Equal(BudgetLand.Target(in e, City(sim).Skims, i), lv[i]);   // E2: LV = T при создании
        }

        Assert.Equal(0, sim.GetLandValue(Array.Empty<int>()));
        Assert.NotEqual(0, City(sim).Budget!.VotDsQ8);
    }

    // ------------------------------------------------------------------ SetBudgetPolicy

    [Fact]
    public void SetBudgetPolicy_Checks()
    {
        using Simulation sim = Make(ShortCity());
        CityEconomyDesc e = City(sim).Cfg.Economy;
        BudgetPolicy ok = Raised(sim);
        BudgetPolicy p = ok;
        p.TaxRBp = (ushort)(e.MaxTaxBp + 1);
        Assert.Equal(RejectReason.BudgetRule, Run(sim, in p));
        p = ok;
        p.TaxCBp = (ushort)(e.MaxTaxBp + 1);
        Assert.Equal(RejectReason.BudgetRule, Run(sim, in p));
        p = ok;
        p.TaxIBp = (ushort)(e.MaxTaxBp + 1);
        Assert.Equal(RejectReason.BudgetRule, Run(sim, in p));
        p = ok;
        p.LandTaxBpDay = (ushort)(e.MaxLandBp + 1);
        Assert.Equal(RejectReason.BudgetRule, Run(sim, in p));
        p = ok;
        p.Fare = (ushort)(e.MaxFare + 1);
        Assert.Equal(RejectReason.BudgetRule, Run(sim, in p));
        p = ok;
        p.Flags = 0;   // выключить бюджет нельзя
        Assert.Equal(RejectReason.BudgetRule, Run(sim, in p));
        p = ok;
        p.Flags |= 2;
        Assert.Equal(RejectReason.InvalidArgument, Run(sim, in p));
        p = ok;
        p.R1 = 1;
        Assert.Equal(RejectReason.InvalidArgument, Run(sim, in p));
        Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
        Assert.Equal(0, info.HasPending);   // отказы ничего не меняют
        Assert.Equal(8, sim.Counters.CommandsRejected);   // 6 BudgetRule + 2 InvalidArgument

        p = ok;
        p.TaxRBp = (ushort)e.MaxTaxBp;   // предел включительно
        Assert.Equal(RejectReason.None, Run(sim, in p));
    }

    [Fact]
    public void SetBudgetPolicy_AppliesAtDayBoundary_LastCommandWins()
    {
        using Simulation sim = Make(ShortCity());
        sim.Step(100);
        Assert.True(sim.GetCityBudget(out CityBudgetInfo before));
        BudgetPolicy first = Raised(sim);
        Assert.Equal(RejectReason.None, Run(sim, in first));
        BudgetPolicy second = first;
        second.LandTaxBpDay = 40;
        Assert.Equal(RejectReason.None, Run(sim, in second));
        Assert.True(sim.GetCityBudget(out CityBudgetInfo pend));
        Assert.Equal(1, pend.HasPending);
        Assert.Equal(40, pend.PendingPolicy.LandTaxBpDay);
        Assert.Equal(before.Policy.TaxRBp, pend.Policy.TaxRBp);   // действующая не изменилась

        sim.Step(ShortDay - (int)sim.Tick - 1);   // до последнего тика суток
        Assert.True(sim.GetCityBudget(out pend));
        Assert.Equal(1, pend.HasPending);
        sim.Step(1);                                // граница суток
        bool applied = false;
        foreach (SimEvent e in sim.Events)
        {
            applied |= e.Kind == SimEventKind.PolicyApplied && e.A == 1;
        }

        Assert.True(applied, "нет PolicyApplied на границе суток");
        Assert.True(sim.GetCityBudget(out CityBudgetInfo after));
        Assert.Equal(0, after.HasPending);
        Assert.Equal(900, after.Policy.TaxRBp);
        Assert.Equal(40, after.Policy.LandTaxBpDay);
        Assert.Equal(5, after.Policy.Fare);
        Assert.Equal(default, after.PendingPolicy);
    }

    [Fact]
    public void SetBudgetPolicy_EqualToCurrent_ClearsPending()
    {
        using Simulation sim = Make(ShortCity());
        Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
        BudgetPolicy cur = info.Policy;
        BudgetPolicy up = Raised(sim);
        Assert.Equal(RejectReason.None, Run(sim, in up));
        Assert.True(sim.GetCityBudget(out info));
        Assert.Equal(1, info.HasPending);
        Assert.Equal(RejectReason.None, Run(sim, in cur));
        Assert.True(sim.GetCityBudget(out info));
        Assert.Equal(0, info.HasPending);
        Assert.Equal(default, info.PendingPolicy);
    }

    [Fact]
    public void SetBudgetPolicy_WithoutBudget_OrCity()
    {
        BudgetPolicy withEnable = CityBudget.DefaultPolicy(CityDesc.Default().Economy);
        using (Simulation noBudget = Make(ShortCity(budget: false)))
        {
            BudgetPolicy without = withEnable;
            without.Flags = 0;
            Assert.Equal(RejectReason.BudgetRule, Run(noBudget, in without));
            without.R0 = 3;
            Assert.Equal(RejectReason.InvalidArgument, Run(noBudget, in without));
            Assert.Null(City(noBudget).Budget);
            Assert.Equal(RejectReason.None, Run(noBudget, in withEnable));   // E2: Enable включает бюджет на идущем городе
            Assert.NotNull(City(noBudget).Budget);
        }

        ScenarioDesc plain = WorldSaveFixtures.Small();
        using Simulation noCity = Make(plain);
        Assert.Equal(RejectReason.NotSupported, Run(noCity, in withEnable));
    }

    // ------------------------------------------------------------------ сейв

    private static void Poke(Simulation sim)
    {
        CityWorld c = City(sim);
        CityBudget b = c.Budget!;
        Ledger lg = c.Ledger;
        lg.Transfer(lg.ExternalAccount, lg.CityAccount, 1000, FlowKind.Refund);
        lg.Transfer(lg.CityAccount, lg.ExternalAccount, 250, FlowKind.Interest);
        lg.Transfer(Ledger.AccountOfResident(0), lg.ExternalAccount, 12, FlowKind.Leakage);
        lg.Transfer(lg.AccountOfBuilding(0), lg.ExternalAccount, 33, FlowKind.Dividend);
        lg.TransferPct(Ledger.AccountOfResident(1), lg.CityAccount, 777, 123, FlowKind.Tax);
        b.IncomeEwma = 123_456;
        b.ExpenseEwma = 65_432;
        b.DebtDays = 2;
        b.OverLimitDays = 1;
        b.Insolvent = 1;
        b.LandValue[0] = c.Cfg.Economy.LvMin;
        b.LandValue[^1] = c.Cfg.Economy.LvMax;
        b.UndoCredits[3] = new UndoCredit { TargetIndex = 5, TargetGeneration = 1, Amount = -300, ExpireTick = sim.Tick + 100 };
        b.FareEvadedDay[0] = 3;
        b.DebtEvictionsDay[1] = 2;
        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            if (c.Buildings.Pool.IsAlive(i))
            {
                c.Buildings.PayrollEwma[i] = 100 + i;
            }
        }

        sim.World.Counters.FareEvaded = 4;
        sim.World.Counters.DebtEvictions = 5;
        sim.World.Counters.InsufficientFunds = 6;
    }

    [Fact]
    public void SaveLoadSave_WithBudget_IsByteEqual_AndKeepsState()
    {
        using Simulation sim = Make(ShortCity());
        sim.Step(300);
        BudgetPolicy up = Raised(sim);
        Assert.Equal(RejectReason.None, Run(sim, in up));   // в сейве есть ожидающая политика
        Poke(sim);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation again = WorldSaveFixtures.Load(bytes);
        Assert.Equal(sim.ComputeStateHash(), again.ComputeStateHash());
        Assert.True(bytes.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(again)));
        CityBudget a = City(sim).Budget!;
        CityBudget r = City(again).Budget!;
        Assert.NotNull(r);
        Assert.True(CityBudget.Same(in a.Policy, in r.Policy) && CityBudget.Same(in a.PendingPolicy, in r.PendingPolicy) && r.HasPending == 1);
        Assert.Equal(a.LandValue, r.LandValue);
        Assert.Equal(a.Carry, r.Carry);
        Assert.Equal((a.IncomeEwma, a.ExpenseEwma, a.DebtDays, a.OverLimitDays, a.Insolvent), (r.IncomeEwma, r.ExpenseEwma, r.DebtDays, r.OverLimitDays, r.Insolvent));
        Assert.Equal(a.UndoCredits, r.UndoCredits);
        Assert.Equal(a.FareEvadedDay, r.FareEvadedDay);
        Assert.Equal(a.DebtEvictionsDay, r.DebtEvictionsDay);
        Assert.Equal(City(sim).Buildings.PayrollEwma, City(again).Buildings.PayrollEwma);
        Assert.Equal(City(sim).Ledger.FlowTotal, City(again).Ledger.FlowTotal);
        Assert.Equal(a.VotDsQ8, r.VotDsQ8);
        Assert.Equal((4L, 5L, 6L), (again.Counters.FareEvaded, again.Counters.DebtEvictions, again.Counters.InsufficientFunds));
        Assert.Same(r.Carry, City(again).Ledger.Carry);   // Ledger и бюджет делят один массив переносов

        sim.Step(2 * ShortDay);
        again.Step(2 * ShortDay);
        Assert.Equal(sim.ComputeStateHash(), again.ComputeStateHash());
    }

    [Fact]
    public void SaveLoad_WithoutBudget_HasNoBudgetBlock()
    {
        using Simulation sim = Make(ShortCity(budget: false));
        sim.Step(100);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation again = WorldSaveFixtures.Load(bytes);
        Assert.Null(City(again).Budget);
        Assert.False(City(again).Cfg.Economy.CityBudget);
        Assert.True(bytes.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(again)));
        Assert.Equal(sim.ComputeStateHash(), again.ComputeStateHash());
    }

    [Fact]
    public void HashSection_AppearsOnlyWithBudget_AndDependsOnBudgetState()
    {
        using Simulation off = Make(ShortCity(budget: false));
        using Simulation on = Make(ShortCity());
        // Параметры казны сами в хэш не входят, а состояние бюджета - входит (раздел «казна»).
        Assert.NotEqual(off.ComputeStateHash(), on.ComputeStateHash());
        ulong before = on.ComputeStateHash();
        long saved = City(on).Budget!.IncomeEwma;
        City(on).Budget!.IncomeEwma = saved + 1;
        Assert.NotEqual(before, on.ComputeStateHash());
        City(on).Budget!.IncomeEwma = saved;
        Assert.Equal(before, on.ComputeStateHash());
        City(on).Buildings.PayrollEwma[0] = 5;
        Assert.NotEqual(before, on.ComputeStateHash());
    }

    private static void ExpectLoadFails(Action<CityWorld> corrupt)
    {
        using Simulation sim = Make(ShortCity());
        sim.Step(200);
        corrupt(City(sim));
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        var o = new LoadOptions { StrictHash = 0 };
        Assert.NotEqual(LoadStatus.Ok, Simulation.Load(bytes, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? loaded, out _));
        loaded?.Dispose();
    }

    [Fact]
    public void ValidateCity_RejectsBrokenBudgetState()
    {
        ExpectLoadFails(c => c.Budget!.LandValue[1] = c.Cfg.Economy.LvMax + 1);
        ExpectLoadFails(c => c.Budget!.LandValue[1] = c.Cfg.Economy.LvMin - 1);
        ExpectLoadFails(c => c.Budget!.Policy.Fare = (ushort)(c.Cfg.Economy.MaxFare + 1));
        ExpectLoadFails(c => c.Budget!.Policy.Flags = 0);
        ExpectLoadFails(c => c.Budget!.HasPending = 2);
        ExpectLoadFails(c => c.Budget!.HasPending = 1);   // ожидающая - нули, без Enable
        ExpectLoadFails(c => c.Budget!.PendingPolicy.TaxRBp = 1);   // HasPending = 0, но ожидающая не пуста
        ExpectLoadFails(c => c.Budget!.Carry[2] = Ledger.Bp);
        ExpectLoadFails(c => c.Budget!.Carry[2] = -1);
        ExpectLoadFails(c => c.Budget!.IncomeEwma = -1);
        ExpectLoadFails(c => c.Budget!.DebtDays = -1);
        ExpectLoadFails(c => c.Budget!.Insolvent = 2);
        ExpectLoadFails(c => c.Budget!.Insolvent = 1);   // за лимитом без долга
        ExpectLoadFails(c => c.Budget!.UndoCredits[0] = new UndoCredit { TargetIndex = 1, TargetGeneration = 1, Amount = 5, ExpireTick = -1 });   // истёк
        ExpectLoadFails(c => c.Budget!.UndoCredits[0] = new UndoCredit { Amount = 5 });   // мёртвая запись с мусором
        ExpectLoadFails(c => c.Budget!.FareEvadedDay[0] = -1);
        ExpectLoadFails(c => c.Buildings.PayrollEwma[0] = -1);
        ExpectLoadFails(c => c.Ledger.City += 1);   // Σ счетов != 0
    }

    // ------------------------------------------------------------------ детерминизм, аллокации

    [Fact]
    public void StateHash_WithBudget_IsIndependentOfWorkerThreads()
    {
        ulong reference = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Make(ShortCity(), threads);
            sim.Step(50);
            BudgetPolicy up = Raised(sim);
            Assert.Equal(RejectReason.None, Run(sim, in up));
            sim.Step(2 * ShortDay);
            Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
            Assert.Equal(900, info.Policy.TaxRBp);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                reference = h;
            }

            Assert.Equal(reference, h);
        }
    }

    [Fact]
    public void Step_WithBudget_AllocatesZeroBytes()
    {
        using Simulation sim = Make(ShortCity());
        sim.Step(2 * ShortDay);
        BudgetPolicy up = Raised(sim);
        long bytes = AllocationProbe.Measure(() =>
        {
            sim.Enqueue(Command.MakeSetBudgetPolicy(in up, 9));
            sim.Step(2 * ShortDay);
        }, out _);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void WithoutBudget_NothingIsAllocatedOrChanged()
    {
        // EC0 по структуре: без city_budget нет ни бюджета, ни переносов; потоки 13..16 пусты, хэш не зависит от них.
        using Simulation sim = Make(ShortCity(budget: false));
        sim.Step(2 * ShortDay);
        CityWorld c = City(sim);
        Assert.Null(c.Budget);
        Assert.Null(c.Ledger.Carry);
        for (int k = Ledger.LegacyFlowCount; k < Ledger.FlowCount; k++)
        {
            Assert.Equal(0, c.Ledger.FlowTotal[k]);
        }

        ulong h = sim.ComputeStateHash();
        c.Ledger.FlowTotal[(int)FlowKind.Refund] = 5;   // без бюджета виды 13..16 в хэш не входят
        Assert.Equal(h, sim.ComputeStateHash());
        c.Ledger.FlowTotal[(int)FlowKind.Refund] = 0;
    }
}
