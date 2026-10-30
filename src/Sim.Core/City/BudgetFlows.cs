using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Потоки и политика казны (city-economy.md §3, §6, §7.2, §11). Все деньги - проводки <see cref="Ledger.Transfer"/>/<see cref="Ledger.TransferPct"/> (Σ счетов = 0, EC1), целые числа (CZ5-CZ7),
/// без выделений памяти. Налоги, плата за проезд, земля, дивиденды и утечка висят на событиях 3.2 (<c>PayWage</c>, <c>CompleteLeg</c>, дневные проходы зданий и жителей); граница суток
/// (<see cref="OnDayBoundary"/>) - цена земли, содержание, проценты, сглаженные доход и расход, лимит и <c>Insolvent</c>, применение ожидающей политики (§6.2). Команда <see cref="CommandKind.SetBudgetPolicy"/>
/// пишет политику в ожидающую (E1), <see cref="Enable"/> включает бюджет на идущем городе (EC8).
/// </summary>
internal static class BudgetFlows
{
    /// <summary>
    /// Обработчик <see cref="CommandKind.SetBudgetPolicy"/> (§11): проверки и запись в ожидающую политику; <see cref="RejectReason.None"/> - принята. Без города - <c>NotSupported</c>;
    /// у города без бюджета запрос <c>Enable</c> включает бюджет (<see cref="Enable"/>; ставки в самой команде игнорируются - действуют дефолты секции <c>economy</c>, дальше политику меняют обычной командой),
    /// политика без <c>Enable</c> - <c>BudgetRule</c> (нечего задавать); у города с бюджетом - <see cref="CityBudget.CheckPolicy"/>. Политика, равная действующей, снимает ожидающую («последняя команда суток побеждает»).
    /// </summary>
    public static RejectReason SetPolicy(World w, in Command cmd)
    {
        CityWorld? c = w.City;
        if (c == null)
        {
            return RejectReason.NotSupported;
        }

        BudgetPolicy p = cmd.SetBudgetPolicy.Policy;
        CityBudget? b = c.Budget;
        if (b == null)
        {
            if ((p.Flags & ~BudgetPolicyFlags.Enable) != 0 || p.R0 != 0 || p.R1 != 0)
            {
                return RejectReason.InvalidArgument;
            }

            return (p.Flags & BudgetPolicyFlags.Enable) != 0 ? Enable(w, c) : RejectReason.BudgetRule;
        }

        RejectReason why = CityBudget.CheckPolicy(in c.Cfg.Economy, in p);
        if (why != RejectReason.None)
        {
            return why;
        }

        if (CityBudget.Same(in p, in b.Policy))
        {
            b.PendingPolicy = default;
            b.HasPending = 0;
        }
        else
        {
            b.PendingPolicy = p;
            b.HasPending = 1;
        }

        return RejectReason.None;
    }

    /// <summary>
    /// Включение бюджета на идущем городе (§11, EC8): создаёт <see cref="CityBudget"/> с политикой по умолчанию, вклад в казну (<c>Endowment</c>), <c>LV = T</c>, содержание, <c>PayrollEwma</c> зданий
    /// из вчерашнего фонда и <c>IncomeEwma</c> по оценке §7.1 из вчерашних потоков (лимит не нулевой в первый день). Параметры <c>economy</c> уже в мире (дефолты §13), поэтому сейв после включения
    /// содержит <c>CBGT</c>. Не бывает повторно: у города с бюджетом команда идёт по ветке политики.
    /// </summary>
    public static RejectReason Enable(World w, CityWorld c)
    {
        if (c.Budget != null)
        {
            return RejectReason.None;
        }

        if (!CityBudget.IsValid(in c.Cfg.Economy))
        {
            return RejectReason.BudgetRule;
        }

        w.Town.Economy.CityBudget = true;
        c.Cfg.Economy.CityBudget = true;
        var b = new CityBudget(in c.Cfg.Economy, c.Grid.Cells);
        c.Budget = b;
        c.Ledger.Carry = b.Carry;
        b.RecomputeDerived(c);
        Endow(c);
        BudgetLand.Update(w, c, true);
        RecomputeMaintenance(w, c);
        BuildingStore bs = c.Buildings;
        int ys = (c.Day + 1) & 1;
        for (int i = 0; i < bs.HighWater; i++)
        {
            if (bs.IsAlive(i) && bs.State[i] == (byte)BuildingState.Active && c.Types.Rows[bs.Type[i]].Kind != LotZone.Residential)
            {
                bs.PayrollEwma[i] = Payroll(c, i);
            }
        }

        b.IncomeEwma = EstimateIncome(c, ys);
        return RejectReason.None;
    }

    /// <summary>Вклад в казну при <c>Create</c> (в конце <c>CityInit.Run</c>) и <c>Enable</c>: <c>initial_city_balance_per_resident * Alive</c> от <c>External</c> (§3).</summary>
    internal static void Endow(CityWorld c)
    {
        long amount = ((long)c.Cfg.Economy.InitialCityBalancePerResident * c.Residents.AliveCount) + c.Cfg.Economy.InitialCityBalance;
        c.Ledger.Transfer(c.Ledger.ExternalAccount, c.Ledger.CityAccount, amount, FlowKind.Endowment);
    }

    /// <summary>Начальные производные бюджета города, созданного с <c>city_budget</c> (после <c>Accessibility.Init</c> и найма): вклад, <c>LV = T</c>, содержание, оценка дохода.</summary>
    internal static void InitCreated(World w, CityWorld c)
    {
        CityBudget? b = c.Budget;
        if (b == null)
        {
            return;
        }

        Endow(c);
        BudgetLand.Update(w, c, true);
        RecomputeMaintenance(w, c);
        BuildingStore bs = c.Buildings;
        for (int i = 0; i < bs.HighWater; i++)
        {
            if (bs.IsAlive(i) && bs.State[i] == (byte)BuildingState.Active && c.Types.Rows[bs.Type[i]].Kind != LotZone.Residential)
            {
                bs.PayrollEwma[i] = Payroll(c, i);
            }
        }

        b.IncomeEwma = EstimateIncome(c, (c.Day + 1) & 1);
    }

    /// <summary>
    /// Земля участка (building-lifecycle.md §2.6): <c>LV[1 км] · (1000 + min(200, 10·h) − min(400, lv_blight · b)) / 1000</c>, где <c>h</c> и <c>b</c> - окно 3×3 полей <c>Heritage</c> и <c>Blight</c> (вес 2 у своей клетки).
    /// Без режима <c>city.lifecycle</c> - ровно <c>LV[1 км]</c>, как раньше.
    /// </summary>
    internal static long LandAt(CityWorld c, CityBudget b, int lot)
    {
        long lv = b.LandValue[c.Lots.Cell[lot]];
        NeighborhoodFields? f = c.Fields;
        if (f == null)
        {
            return lv;
        }

        int cell = f.CellOf(c.Lots.X[lot], c.Lots.Z[lot]);
        long h = f.Window(cell, NeighborhoodFields.Heritage);
        long bl = f.Window(cell, NeighborhoodFields.Blight);
        long permille = 1000 + Math.Min(200L, 10 * h) - Math.Min(400L, c.Cfg.Lifecycle.LvBlightPermille * bl);
        return lv * permille / 1000;
    }

    /// <summary>Номинальный фонд оплаты фирмы в сутки: работники * ставка смены.</summary>
    private static long Payroll(CityWorld c, int bi) => (long)c.Buildings.Occupants[bi] * c.Cfg.Economy.WagePerShift;

    /// <summary>
    /// Оценка дохода казны в сутки (§11): налог жителей с вчерашних зарплат + земля (места жителей и <c>LV * Capacity</c> активных C/I) + плата за проезд вчерашних автобусных ног.
    /// Налог на прибыль не оценивается (занижает лимит в первые дни, а не завышает).
    /// </summary>
    private static long EstimateIncome(CityWorld c, int yesterdayParity)
    {
        CityBudget b = c.Budget!;
        long income = c.Ledger.FlowYesterday[(int)FlowKind.Wage] * b.Policy.TaxRBp / Ledger.Bp;
        long landBase = 0;
        BuildingStore bs = c.Buildings;
        CityResidents res = c.Residents;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (res.Pool.IsAlive(r) && res.Home[r] >= 0)
            {
                landBase += LandAt(c, b, bs.Lot[res.Home[r]]);
            }
        }

        for (int i = 0; i < bs.HighWater; i++)
        {
            if (bs.IsAlive(i) && bs.State[i] == (byte)BuildingState.Active)
            {
                ref readonly BuildingTypeDesc t = ref c.Types.Rows[bs.Type[i]];
                if (t.Kind != LotZone.Residential)
                {
                    landBase += LandAt(c, b, bs.Lot[i]) * t.Capacity;
                }
            }
        }

        income += landBase * b.Policy.LandTaxBpDay / Ledger.Bp;
        income += (long)c.LegsDay[(yesterdayParity * Demand.Skims.Modes) + (int)TravelMode.Bus] * b.Policy.Fare;
        return income;
    }

    // ------------------------------------------------------------------ потоки событий 3.2

    /// <summary>Налог жителя с зарплаты (§3): сразу после <c>Wage</c>; возвращает сумму налога (доход для <c>IncomeEwma</c> - после налога).</summary>
    internal static long TaxWage(CityWorld c, int r, long wage)
    {
        CityBudget? b = c.Budget;
        if (b == null || wage <= 0)
        {
            return 0;
        }

        return c.Ledger.TransferPct(Ledger.AccountOfResident(r), c.Ledger.CityAccount, wage, b.Policy.TaxRBp, FlowKind.Tax);
    }

    /// <summary>Плата за проезд на закрытии автобусной ноги (§3): <c>Policy.Fare</c>, если хватает денег, иначе 0 и счётчик <c>fare_evaded</c> (поездка уже совершена).</summary>
    internal static void ChargeFare(World w, CityWorld c, int r)
    {
        CityBudget? b = c.Budget;
        int fare = b == null ? 0 : b.Policy.Fare;
        if (fare <= 0)
        {
            return;
        }

        if (c.Residents.Account[r] >= fare)
        {
            c.Ledger.Transfer(Ledger.AccountOfResident(r), c.Ledger.CityAccount, fare, FlowKind.Fare);
        }
        else
        {
            w.Counters.FareEvaded++;
            b!.FareEvadedDay[c.Day & 1]++;
        }
    }

    /// <summary>
    /// Налог на прибыль и земля фирмы (дневной проход, §3): налог - с положительной вчерашней прибыли, земля <c>LV[cell] * Capacity</c> входит в расходы сегодняшних суток (<paramref name="todaySlot"/>)
    /// и во вчерашнюю прибыль следующего дня. Возвращает уплаченный налог (прибыль для <c>ProfitEwma</c> - после налога).
    /// </summary>
    internal static long FirmTaxAndRent(CityWorld c, int bi, in BuildingTypeDesc t, long profit, int todaySlot)
    {
        CityBudget b = c.Budget!;
        BuildingStore bs = c.Buildings;
        int acct = c.Ledger.AccountOfBuilding(bi);
        long tax = 0;
        if (profit > 0)
        {
            int rate = t.Kind == LotZone.Commercial ? b.Policy.TaxCBp : b.Policy.TaxIBp;
            tax = c.Ledger.TransferPct(acct, c.Ledger.CityAccount, profit, rate, FlowKind.Tax);
        }

        long landBase = LandAt(c, b, bs.Lot[bi]) * t.Capacity;
        long rent = c.Ledger.TransferPct(acct, c.Ledger.CityAccount, landBase, b.Policy.LandTaxBpDay, FlowKind.Rent);
        bs.CostDay[todaySlot] += rent;
        return tax;
    }

    /// <summary>
    /// Дивиденд фирмы (§7.2, после правил найма): остаток сверх буфера уходит за город долей <c>1 / 2^payout_shift</c>. Буфер - <c>max(firm_buffer_days * PayrollEwma, initial_firm_balance)</c>:
    /// отклонение от спеки - стартовый капитал не выплачивается (у новой фирмы <c>PayrollEwma</c> мал, и выплаты первых дней съедали оборотный капитал, фирмы уходили в минус и закрывались).
    /// </summary>
    internal static void FirmDividend(CityWorld c, int bi)
    {
        CityEconomyDesc e = c.Cfg.Economy;
        BuildingStore bs = c.Buildings;
        long buffer = Math.Max(e.FirmBufferDays * bs.PayrollEwma[bi], e.InitialFirmBalance);
        long pay = (bs.Account[bi] - buffer) >> e.PayoutShift;
        if (pay > 0)
        {
            c.Ledger.Transfer(c.Ledger.AccountOfBuilding(bi), c.Ledger.ExternalAccount, pay, FlowKind.Dividend);
        }
    }

    /// <summary>Сглаживание фонда оплаты (начало дневного прохода активной C/I-фирмы).</summary>
    internal static void FirmPayrollEwma(CityWorld c, int bi)
    {
        BuildingStore bs = c.Buildings;
        bs.PayrollEwma[bi] += (Payroll(c, bi) - bs.PayrollEwma[bi]) >> c.Cfg.Economy.EwmaShift;
    }

    /// <summary>Дневной проход жителя (§3, §7.2): земля за место, затем утечка остатка сверх <c>res_buffer_days * IncomeEwma</c>. Выселение за долг - E3.</summary>
    internal static void ResidentDay(CityWorld c, int r)
    {
        CityBudget? b = c.Budget;
        if (b == null)
        {
            return;
        }

        CityEconomyDesc e = c.Cfg.Economy;
        CityResidents res = c.Residents;
        int acct = Ledger.AccountOfResident(r);
        int home = res.Home[r];
        if (home >= 0)
        {
            c.Ledger.TransferPct(acct, c.Ledger.CityAccount, LandAt(c, b, c.Buildings.Lot[home]), b.Policy.LandTaxBpDay, FlowKind.Rent);
        }

        long pay = (res.Account[r] - (e.ResBufferDays * res.IncomeEwma[r])) >> e.PayoutShift;
        if (pay > 0)
        {
            c.Ledger.Transfer(acct, c.Ledger.ExternalAccount, pay, FlowKind.Leakage);
        }
    }

    // ------------------------------------------------------------------ граница суток

    /// <summary>
    /// Граница суток (фаза 9r, после <c>Day++</c>, до <c>Ledger.RollDay</c>; §6.2): <c>LV</c> -> <c>Maintenance</c> -> <c>Interest</c> -> <c>IncomeEwma/ExpenseEwma</c> по только что закончившимся суткам
    /// (<c>FlowDay</c>, станет «вчера» после <c>RollDay</c>) -> лимит и <c>Insolvent</c> -> обнуление накопителей новых суток -> применение <c>PendingPolicy</c>.
    /// </summary>
    public static void OnDayBoundary(World w, CityWorld c)
    {
        CityBudget? b = c.Budget;
        if (b == null)
        {
            return;
        }

        CityEconomyDesc e = c.Cfg.Economy;
        Ledger lg = c.Ledger;
        BudgetLand.Update(w, c, false);
        RecomputeMaintenance(w, c);
        lg.Transfer(lg.CityAccount, lg.ExternalAccount, b.MaintRoadDay + b.MaintBusDay, FlowKind.Maintenance);
        if (lg.City < 0)
        {
            lg.TransferPct(lg.CityAccount, lg.ExternalAccount, -lg.City, e.InterestBpDay, FlowKind.Interest);
        }

        long[] f = lg.FlowDay;
        long income = f[(int)FlowKind.Tax] + f[(int)FlowKind.Rent] + f[(int)FlowKind.Fare];
        long expense = f[(int)FlowKind.Maintenance] + f[(int)FlowKind.Interest] + f[(int)FlowKind.Construction] - f[(int)FlowKind.Refund];
        b.IncomeEwma += (income - b.IncomeEwma) >> e.BudgetEwmaShift;
        b.ExpenseEwma += (expense - b.ExpenseEwma) >> e.BudgetEwmaShift;
        System.Diagnostics.Debug.Assert(Math.Abs(lg.City) < (1L << 56), "казна: |City| >= 2^56");

        long limit = b.CreditLimit(in e, Progression.CreditDaysBonus(w));
        bool negative = lg.City < 0;
        bool over = lg.City < -limit;
        b.DebtDays = negative ? Math.Min(b.DebtDays + 1, int.MaxValue - 1) : 0;
        b.OverLimitDays = over ? Math.Min(b.OverLimitDays + 1, int.MaxValue - 1) : 0;
        if (negative && b.DebtDays == 1)
        {
            w.Emit(SimEventKind.BudgetNegative, 0, c.Day, Sat(lg.City));
        }

        if (over && b.Insolvent == 0)
        {
            b.Insolvent = 1;
            w.Emit(SimEventKind.BudgetLimitReached, 0, c.Day, Sat(lg.City));
        }
        else if (!over && b.Insolvent != 0)
        {
            b.Insolvent = 0;
            w.Emit(SimEventKind.BudgetRecovered, 0, c.Day, Sat(lg.City));
        }

        Warn(w, c, b);
        int idx = c.Day & 1;
        b.FareEvadedDay[idx] = 0;
        b.DebtEvictionsDay[idx] = 0;
        if (b.HasPending != 0)
        {
            b.Policy = b.PendingPolicy;
            b.PendingPolicy = default;
            b.HasPending = 0;
            w.Emit(SimEventKind.PolicyApplied, 0, c.Day, b.Policy.Fare);
        }
    }

    /// <summary>
    /// Предупреждения о кредитном лимите (PG5, progression.md §5): по дням до лимита (<see cref="CityKpi.DaysToLimit"/>, после EWMA и лимита этих суток) уровень 1 при &lt;= 7 днях, 2 при &lt;= 2
    /// (и за лимитом). Событие <see cref="SimEventKind.BudgetWarning"/> - только на переходе уровня вверх; гистерезис - уровень снимается, когда дней &gt; 10 или тренд &gt;= 0.
    /// </summary>
    internal static void Warn(World w, CityWorld c, CityBudget b)
    {
        int days = CityKpi.DaysToLimit(c, Progression.CreditDaysBonus(w));
        if (days < 0 || days > WarnClearDays)
        {
            b.WarnLevel = 0;
            return;
        }

        byte level = days <= Warn2Days ? (byte)2 : days <= Warn1Days ? (byte)1 : (byte)0;
        if (level > b.WarnLevel)
        {
            b.WarnLevel = level;
            w.Emit(SimEventKind.BudgetWarning, 0, level, days);
        }
    }

    /// <summary>[Старт] пороги предупреждений, дни до лимита (progression.md §5).</summary>
    public const int Warn1Days = 7;
    public const int Warn2Days = 2;
    public const int WarnClearDays = 10;

    private static int Sat(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);

    // ------------------------------------------------------------------ содержание

    /// <summary>
    /// Пересчёт содержания (§6.1). Дороги: <c>Σ lanes * len_m * rate[RoadKind] / 1000</c> по живым сегментам (длина в мм, целая арифметика). Автобусы: на маршрут <c>buses = ceil(cycle / headway)</c>,
    /// <c>cycle = 2 * len / v_bus + stops * dwell_base</c>, где <c>len</c> - сумма расстояний по прямой между соседними остановками маршрута (пути маршрута ядро не хранит), <c>v_bus</c> - <c>MaxSpeed</c> типа;
    /// <c>MaintBusDay = Σ buses * maint_bus_day + Σ stops * maint_stop_day</c>. Считается на границе суток, при создании, включении и загрузке (а не точечно в 1c): O(сегментов + остановок) раз в сутки,
    /// правка сети учитывается со следующей границы. Не зависит от живых автобусов.
    /// </summary>
    public static void RecomputeMaintenance(World w, CityWorld c)
    {
        CityBudget? b = c.Budget;
        if (b == null)
        {
            return;
        }

        CityEconomyDesc e = c.Cfg.Economy;
        NetworkStore net = w.Net;
        long streetLaneMm = 0;
        long arterialLaneMm = 0;
        for (int i = 0; i < net.SegmentCount; i++)
        {
            ref readonly SegmentData sg = ref net.Segments[i];
            if (!NetworkAlive.IsAlive(sg.Generation))
            {
                continue;
            }

            long laneMm = (long)(sg.LanesAB + sg.LanesBA) * DetMath.ToIntSat(sg.Length * 1000f);
            if (sg.Kind == RoadKind.Arterial)
            {
                arterialLaneMm += laneMm;
            }
            else
            {
                streetLaneMm += laneMm;
            }
        }

        b.MaintRoadDay = ((streetLaneMm * e.MaintRoadStreetPerLaneKmDay) + (arterialLaneMm * e.MaintRoadArterialPerLaneKmDay)) / 1_000_000L;

        long buses = 0;
        long stops = 0;
        int dwellBase = w.TransitCfg.DwellBaseTicks;
        for (int r = 0; r < net.BusRouteCount; r++)
        {
            ref readonly BusRouteData br = ref net.BusRoutes[r];
            if (!NetworkAlive.IsAlive(br.Generation) || br.StopCount < 1)
            {
                continue;
            }

            stops += br.StopCount;
            if (br.HeadwayTicks <= 0 || (uint)br.VehicleType >= (uint)w.Types.Length)
            {
                continue;
            }

            long lenMm = RouteLengthMm(net, in br);
            long vMmps = DetMath.ToIntSat(w.Types[br.VehicleType].MaxSpeed * 1000f);
            long cycleTicks = (vMmps > 0 ? 2 * lenMm * Simulation.TicksPerSecond / vMmps : 0) + ((long)br.StopCount * dwellBase);
            buses += Math.Max(1, (cycleTicks + br.HeadwayTicks - 1) / br.HeadwayTicks);
        }

        b.MaintBusDay = (buses * e.MaintBusDay) + (stops * e.MaintStopDay);
    }

    private static long RouteLengthMm(NetworkStore net, in BusRouteData br)
    {
        double sum = 0;
        int hint = 0;
        Vec2 prev = default;
        for (int k = 0; k < br.StopCount; k++)
        {
            ref readonly BusStopData s = ref net.BusStops[net.BusRouteStops[br.FirstStop + k]];
            LaneGeometry.Evaluate(net, s.Lane, s.S, ref hint, out Vec2 pos, out _);
            if (k > 0)
            {
                float dx = pos.X - prev.X;
                float dz = pos.Z - prev.Z;
                sum += MathF.Sqrt((dx * dx) + (dz * dz));
            }

            prev = pos;
        }

        return DetMath.ToIntSat(sum * 1000.0);
    }
}
