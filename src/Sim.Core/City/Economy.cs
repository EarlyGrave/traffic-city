using System;
using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Потоки денег и правила фирм (zoning-demand.md §7.2-§7.4). Деньги меняются только через <see cref="Ledger.Transfer"/> (сумма счетов = 0, CZ2). Накопители фирмы «сегодня/вчера» лежат
/// в слотах <c>(i &lt;&lt; 1) | (day &amp; 1)</c>: потоки суток <c>D</c> пишутся в слот <c>D &amp; 1</c>, дневной проход суток <c>D + 1</c> читает слот вчерашних суток, использует и обнуляет его
/// (к началу суток <c>D + 2</c> слот снова чистый). Всё, что проход выплачивает сам (товары, экспорт), записывается в слот сегодняшних суток и учитывается в прибыли на следующий день.
/// <para>
/// Отклонение от §7.2: товары <c>Goods</c> каждая C-фирма платит <b>одной</b> I-фирме - жребий по вчерашней выработке (смены) среди I-фирм, а не всем пропорционально: при 10 тыс. C и I пар
/// было бы 10^8 проводок в сутки; ожидаемая доля та же. Нет выработки - вся сумма уходит как <c>Import</c>.
/// </para>
/// </summary>
internal static class Economy
{
    /// <summary>Слот накопителя здания <paramref name="bi"/> для суток с чётностью <paramref name="parity"/>.</summary>
    internal static int Slot(int bi, int parity) => (bi << 1) | parity;

    /// <summary>Конец смены: фирма платит <c>wage_per_shift · present / ShiftSlots</c> (опоздание уменьшает); I-фирме идёт смена в выработку. Не работник этой фирмы (уволен, фирма закрыта) - без оплаты.</summary>
    public static void PayWage(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        BuildingStore b = c.Buildings;
        int firm = res.PlanDest[(r * CityResidents.MaxTours) + res.PlanCursor[r]];
        if (OutsideRef.Is(firm) && res.Work[r] == firm)
        {
            // Внешняя фирма (ADR-0037): зарплата от внешнего мира, налог города - как с обычной; в Ledger фирма не заводится.
            long presentX = Math.Min((w.Tick - res.StateTick[r]) / c.SlotTicks, OutsideState.ShiftSlots);
            long wageX = c.Cfg.Economy.WagePerShift * presentX / OutsideState.ShiftSlots;
            c.Ledger.Transfer(c.Ledger.ExternalAccount, Ledger.AccountOfResident(r), wageX, FlowKind.Wage);
            long taxX = BudgetFlows.TaxWage(c, r, wageX);
            res.IncomeEwma[r] += (wageX - taxX - res.IncomeEwma[r]) >> c.Cfg.Economy.EwmaShift;
            return;
        }

        if (firm < 0 || !b.IsAlive(firm) || res.Work[r] != firm)
        {
            return;
        }

        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[firm]];
        long present = Math.Min((w.Tick - res.StateTick[r]) / c.SlotTicks, t.ShiftSlots);
        long wage = c.Cfg.Economy.WagePerShift * present / Math.Max(1, t.ShiftSlots);
        int slot = Slot(firm, c.Day & 1);
        c.Ledger.Transfer(c.Ledger.AccountOfBuilding(firm), Ledger.AccountOfResident(r), wage, FlowKind.Wage);
        b.CostDay[slot] += wage;
        if (t.Kind == LotZone.Industrial)
        {
            b.VisitsDay[slot]++;
        }

        long tax = BudgetFlows.TaxWage(c, r, wage);   // налог с зарплаты (city-economy.md §3); без бюджета 0
        res.IncomeEwma[r] += (wage - tax - res.IncomeEwma[r]) >> c.Cfg.Economy.EwmaShift;
    }

    /// <summary>
    /// Визит в C (прибытие): покупка корзины, если фирма активна, в ней ещё есть место на сегодня (<c>ShopCapacity · часов работы</c>) и у жителя хватает денег; иначе визит впустую.
    /// </summary>
    public static void Visit(World w, CityWorld c, int r, int shop)
    {
        CityResidents res = c.Residents;
        BuildingStore b = c.Buildings;
        int parity = c.Day & 1;
        c.VisitsDayTotal[parity]++;
        int price = c.Cfg.Economy.BasketPrice;
        bool served = false;
        if (shop >= 0 && b.IsAlive(shop) && b.State[shop] == (byte)BuildingState.Active)
        {
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[shop]];
            int slot = Slot(shop, parity);
            long dayCap = (long)t.ShopCapacity * Math.Max(1, t.ShiftSlots / 12);
            if (c.Cfg.Lifecycle.Enabled)
            {
                dayCap = dayCap * CityLife.OutPct(c, shop) / 100;   // выработка по состоянию (building-lifecycle.md §2.2)
            }

            if (b.VisitsDay[slot] < dayCap && res.Account[r] >= price)
            {
                c.Ledger.Transfer(Ledger.AccountOfResident(r), c.Ledger.AccountOfBuilding(shop), price, FlowKind.Purchase);
                b.RevenueDay[slot] += price;
                b.VisitsDay[slot]++;
                served = true;
            }
        }

        if (!served)
        {
            w.Counters.ResVisitsUnserved++;
            c.UnservedDayTotal[parity]++;
        }
    }

    /// <summary>
    /// Проход по зданию (§7.4): завершение стройки; для активной C/I - сглаженная прибыль за вчера, товары и экспорт, найм, сокращение, закрытие.
    /// </summary>
    public static void BuildingDay(World w, CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        if (!b.IsAlive(bi))
        {
            return;
        }

        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        if (b.State[bi] == (byte)BuildingState.Constructing)
        {
            if (w.Tick - b.StateTick[bi] >= (long)t.ConstructDays * c.DayTicks)
            {
                Growth.Complete(w, c, bi);
            }

            return;
        }

        if (c.Cfg.Lifecycle.Enabled && CityLife.Day(w, c, bi, in t))
        {
            return;   // building-lifecycle.md §2.2: износ, потолок, пороги; заброшенное - без экономики фирмы
        }

        if (b.State[bi] != (byte)BuildingState.Active || t.Kind == LotZone.Residential)
        {
            return;
        }

        CityEconomyDesc e = c.Cfg.Economy;
        int yi = (c.Day + 1) & 1;
        int ti = c.Day & 1;
        int ys = Slot(bi, yi);
        int ts = Slot(bi, ti);
        long revenue = b.RevenueDay[ys];
        long profit = revenue - b.CostDay[ys];
        if (c.Budget != null)
        {
            BudgetFlows.FirmPayrollEwma(c, bi);
            profit -= BudgetFlows.FirmTaxAndRent(c, bi, in t, profit, ts);   // налог на положительную прибыль и земля (в расходы сегодняшних суток); ProfitEwma - после налога
        }

        long shifts = b.VisitsDay[ys];
        if (t.Kind == LotZone.Commercial)
        {
            PayGoods(w, c, bi, revenue, ts);
        }
        else
        {
            long export = e.ExportPerShift * shifts;
            if (c.Cfg.Lifecycle.Enabled)
            {
                export = export * CityLife.OutPct(c, bi) / 100;   // выработка по состоянию (building-lifecycle.md §2.2)
            }

            if (export > 0)
            {
                c.Ledger.Transfer(c.Ledger.ExternalAccount, c.Ledger.AccountOfBuilding(bi), export, FlowKind.Export);
                b.RevenueDay[ts] += export;
            }
        }

        // Слот вчерашних суток чистит Growth.ClearYesterday после прохода: выработка I-фирм нужна всему проходу (жребий поставщика товаров).
        b.RevenueEwma[bi] += (revenue - b.RevenueEwma[bi]) >> e.EwmaShift;
        b.ProfitEwma[bi] += (profit - b.ProfitEwma[bi]) >> e.EwmaShift;
        b.LossDays[bi] = b.ProfitEwma[bi] < 0 ? (short)Math.Min(b.LossDays[bi] + 1, short.MaxValue) : (short)0;
        int step = Math.Max(1, t.Capacity / 8);
        if (b.ProfitEwma[bi] > (long)e.HireMarginPct * e.WagePerShift * b.Occupants[bi] / 100 && b.Jobs[bi] < t.Capacity)
        {
            c.Market.SetJobs(w, c, bi, Math.Min(t.Capacity, b.Jobs[bi] + step));
        }
        else if (b.LossDays[bi] > e.LossDaysFire && b.Jobs[bi] > 0)
        {
            c.Market.SetJobs(w, c, bi, b.Jobs[bi] - step);
        }

        if (b.LossDays[bi] > e.LossDaysClose && b.Account[bi] < 0)
        {
            w.Counters.FirmClosures++;
            Growth.Abandon(w, c, bi);
        }
        else if (c.Budget != null)
        {
            BudgetFlows.FirmDividend(c, bi);   // остаток сверх буфера фонда оплаты уходит за город (§7.2)
        }
    }

    /// <summary>C-фирма платит <c>goods_pct</c> вчерашней выручки: доля <c>import_pct</c> - внешнему миру, остальное - одной I-фирме по жребию (см. заметку класса).</summary>
    private static void PayGoods(World w, CityWorld c, int bi, long revenue, int ts)
    {
        CityEconomyDesc e = c.Cfg.Economy;
        BuildingStore b = c.Buildings;
        long goods = revenue * e.GoodsPct / 100;
        if (goods <= 0)
        {
            return;
        }

        int acct = c.Ledger.AccountOfBuilding(bi);
        long import = goods * e.ImportPct / 100;
        long domestic = goods - import;
        int supplier = domestic > 0 ? c.Market.PickSupplier(w, c) : -1;
        if (supplier < 0)
        {
            import = goods;
            domestic = 0;
        }

        c.Ledger.Transfer(acct, c.Ledger.ExternalAccount, import, FlowKind.Import);
        if (domestic > 0)
        {
            c.Ledger.Transfer(acct, c.Ledger.AccountOfBuilding(supplier), domestic, FlowKind.Goods);
            b.RevenueDay[Slot(supplier, c.Day & 1)] += domestic;
        }

        b.CostDay[ts] += goods;
    }
}
