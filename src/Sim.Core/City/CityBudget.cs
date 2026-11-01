using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>Запись undo-кредита (city-economy.md §2.2, §8.3): цель, сумма и тик истечения. Мёртвая запись - нули (<c>TargetGeneration = 0</c>). Заполняет E4.</summary>
internal struct UndoCredit
{
    public int TargetIndex;
    public int TargetGeneration;
    public long Amount;
    public long ExpireTick;
}

/// <summary>
/// Состояние бюджета города (city-economy.md §2.2, ADR-0025): есть только при <c>economy.city_budget = true</c> (<c>CityWorld.Budget</c> иначе <c>null</c>). Целые числа и плотные массивы;
/// деньги - <c>long</c>. E1 хранит и сохраняет состояние, меняет только политику на границе суток; потоки, цену земли, содержание, долг и лимит добавляет E2 (<c>BudgetFlows</c>, <c>BudgetLand</c>).
/// </summary>
internal sealed class CityBudget
{
    /// <summary>Размер кольца undo-кредитов (§8.3).</summary>
    public const int UndoSlots = 64;

    /// <summary>Действующая и ожидающая границы суток политики (<see cref="HasPending"/> - есть ли ожидающая).</summary>
    public BudgetPolicy Policy;
    public BudgetPolicy PendingPolicy;
    public byte HasPending;

    /// <summary>Цена места в ячейке, деньги (EWMA, §4); длина = число ячеек города.</summary>
    public readonly int[] LandValue;

    /// <summary>Сглаженные доход и расход казны за сутки (<c>&gt;&gt; budget_ewma_shift</c>).</summary>
    public long IncomeEwma;
    public long ExpenseEwma;

    /// <summary>Переносы остатков <see cref="Ledger.TransferPct"/> по виду потока (<see cref="Ledger.FlowCount"/> элементов); тот же массив, что <c>Ledger.Carry</c>.</summary>
    public readonly long[] Carry = new long[Ledger.FlowCount];

    /// <summary>Дней подряд с <c>City &lt; 0</c> / за кредитным лимитом.</summary>
    public int DebtDays;
    public int OverLimitDays;

    /// <summary>1 - казна за кредитным лимитом сейчас (обновляет E2 на границе суток).</summary>
    public byte Insolvent;

    /// <summary>Уровень предупреждения о лимите (progression.md §5, PG5): 0 нет, 1/2 - выдан <c>BudgetWarning</c> этого уровня. Сейв CBGT 31 (w11), хэш - раздел казны (PG5).</summary>
    public byte WarnLevel;

    /// <summary>Кольцо undo-кредитов (§8.3); мёртвые записи - нули.</summary>
    public readonly UndoCredit[] UndoCredits = new UndoCredit[UndoSlots];

    /// <summary>Накопители суток «сегодня/вчера» (пары, индекс <c>day &amp; 1</c>, как <c>CityWorld.ToursDay</c>): ноги без оплаты и выселения за долг. Нужны <c>CityBudgetInfo</c> (§10).</summary>
    public readonly int[] FareEvadedDay = new int[2];
    public readonly int[] DebtEvictionsDay = new int[2];

    // ---- производные (в сейв не входят; пересчёт в CityWorld.RestoreDerived) ----

    /// <summary>Содержание дорог и автобусов в сутки (§6.1): E1 - 0, считает E2.</summary>
    public long MaintRoadDay;
    public long MaintBusDay;

    /// <summary>Децисекунд на единицу денег, Q8 (§5): <c>(shift_ds * 100 * 256) / (wage_per_shift * vot_pct)</c>.</summary>
    public int VotDsQ8;

    public CityBudget(in CityEconomyDesc e, int cells)
    {
        LandValue = new int[cells];
        System.Array.Fill(LandValue, e.LvRef);   // E1: цена эталона; E2 заменит целью T (§4) при Create/Enable
        Policy = DefaultPolicy(in e);
    }

    /// <summary>Политика по умолчанию из секции <c>economy</c> (ставки, плата за проезд, бит <see cref="BudgetPolicyFlags.Enable"/>).</summary>
    public static BudgetPolicy DefaultPolicy(in CityEconomyDesc e) => new()
    {
        TaxRBp = (ushort)e.TaxRBp,
        TaxCBp = (ushort)e.TaxCProfitBp,
        TaxIBp = (ushort)e.TaxIProfitBp,
        LandTaxBpDay = (ushort)e.LandTaxBpDay,
        Fare = (ushort)e.BusFare,
        Flags = BudgetPolicyFlags.Enable,
    };

    /// <summary>Кредитный лимит (§6.3): <c>(credit_days + бонус вех) * IncomeEwma</c>; бонус - <see cref="Progression.CreditDaysBonus"/> (PG3, progression.md §4.4), без прогрессии 0.</summary>
    public long CreditLimit(in CityEconomyDesc e, int bonusDays) => (e.CreditDays + (long)bonusDays) * IncomeEwma;

    /// <summary>Производные значения по параметрам (после <c>Create</c> и <c>Load</c>). Содержание (<c>MaintRoadDay</c>, <c>MaintBusDay</c>) пересчитывает E2.</summary>
    public void RecomputeDerived(CityWorld c)
    {
        CityEconomyDesc e = c.Cfg.Economy;
        long shiftDs = (long)96 * c.SlotTicks / 2;   // эталонная смена: 96 слотов; тик = 0.05 с = 0.5 дс
        long denom = (long)e.WagePerShift * e.VotPct;
        VotDsQ8 = denom > 0 ? (int)System.Math.Min(int.MaxValue, shiftDs * 100 * 256 / denom) : 0;
    }

    /// <summary>
    /// Пределы параметров казны (city-economy.md §13) и согласованность дефолтной политики с пределами: без деления на ноль и переполнений
    /// (<c>BudgetPolicy</c> - <c>ushort</c>, ставки &lt;= 10 000 б.п.). Проверяются всегда, при любом <c>city_budget</c>: параметры живут в сейве.
    /// </summary>
    public static bool IsValid(in CityEconomyDesc e)
    {
        bool ok = e.MaxTaxBp is >= 0 and <= Ledger.Bp && e.MaxLandBp is >= 0 and <= Ledger.Bp && e.MaxFare is >= 0 and <= ushort.MaxValue;
        ok &= e.TaxRBp >= 0 && e.TaxRBp <= e.MaxTaxBp && e.TaxCProfitBp >= 0 && e.TaxCProfitBp <= e.MaxTaxBp && e.TaxIProfitBp >= 0 && e.TaxIProfitBp <= e.MaxTaxBp;
        ok &= e.LandTaxBpDay >= 0 && e.LandTaxBpDay <= e.MaxLandBp && e.BusFare >= 0 && e.BusFare <= e.MaxFare;
        ok &= e.InitialCityBalancePerResident is >= 0 and <= 100_000_000 && e.InterestBpDay is >= 0 and <= Ledger.Bp && e.CreditDays is >= 0 and <= 3650;
        ok &= e.MaintRoadStreetPerLaneKmDay is >= 0 and <= 100_000_000 && e.MaintRoadArterialPerLaneKmDay is >= 0 and <= 100_000_000;
        ok &= e.MaintBusDay is >= 0 and <= 100_000_000 && e.MaintStopDay is >= 0 and <= 100_000_000;
        ok &= e.LvMin >= 0 && e.LvMax >= e.LvMin && e.LvMax <= 100_000_000 && e.LvRef >= e.LvMin && e.LvRef <= e.LvMax && e.LvSlopePerMin is >= 0 and <= 100_000_000 && e.LvShift is >= 0 and <= 16;
        ok &= e.VotPct is >= 1 and <= 10_000 && e.ResBufferDays is >= 0 and <= 3650 && e.FirmBufferDays is >= 0 and <= 3650 && e.PayoutShift is >= 0 and <= 16;
        ok &= e.EvictDebtDays is >= 1 and <= 3650 && e.BudgetEwmaShift is >= 0 and <= 16 && e.UndoRefundS is >= 0 and <= 3600;
        return ok;
    }

    /// <summary>
    /// Проверка политики (§11): резерв и неизвестные биты - <see cref="RejectReason.InvalidArgument"/>; бит <c>Enable</c> обязан стоять (бюджет не выключить), ставки не выше
    /// <c>max_tax_bp</c> / <c>max_land_bp_day</c> / <c>max_fare</c> - иначе <see cref="RejectReason.BudgetRule"/>.
    /// </summary>
    public static RejectReason CheckPolicy(in CityEconomyDesc e, in CityLifecycleDesc lf, in BudgetPolicy p)
    {
        // Режим city.lifecycle (building-lifecycle.md §2.10): R0 - тариф ЖКХ, бит UtilitySubsidy; без режима - резерв.
        ushort known = lf.Enabled ? (ushort)(BudgetPolicyFlags.Enable | BudgetPolicyFlags.UtilitySubsidy) : BudgetPolicyFlags.Enable;
        if ((p.Flags & ~known) != 0 || (!lf.Enabled && p.R0 != 0) || p.R1 != 0)
        {
            return RejectReason.InvalidArgument;
        }

        if (lf.Enabled && p.R0 > lf.MaxUtilityTariff)
        {
            return RejectReason.BudgetRule;
        }

        if ((p.Flags & BudgetPolicyFlags.Enable) == 0 || p.TaxRBp > e.MaxTaxBp || p.TaxCBp > e.MaxTaxBp || p.TaxIBp > e.MaxTaxBp || p.LandTaxBpDay > e.MaxLandBp || p.Fare > e.MaxFare)
        {
            return RejectReason.BudgetRule;
        }

        return RejectReason.None;
    }

    /// <summary>Политики совпадают побайтно.</summary>
    public static bool Same(in BudgetPolicy a, in BudgetPolicy b) =>
        a.TaxRBp == b.TaxRBp && a.TaxCBp == b.TaxCBp && a.TaxIBp == b.TaxIBp && a.LandTaxBpDay == b.LandTaxBpDay && a.Fare == b.Fare && a.Flags == b.Flags && a.R0 == b.R0 && a.R1 == b.R1;
}
