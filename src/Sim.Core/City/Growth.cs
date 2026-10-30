using System;
using Sim.Core.Api;
using Sim.Core.Demand;

namespace Sim.Core.City;

/// <summary>
/// Спрос и рост города (zoning-demand.md §8.2-§8.4): сглаженные <c>R/C/I_demand</c> (показываются клиенту как есть, урок E1), въезд жителей, снос заброшенных зданий и рост новых
/// на пустых зонированных участках по баллу доступности (не больше <c>max_new_buildings_per_day</c>), завершение стройки и заброшенность. Суточный шаг <see cref="Daily"/> (стадия 2 дневного
/// прохода) идёт одним тиком за сутки: O(участки + здания [+ жители при сносе]); срез по курсору - работа Z5/Z9, если замеры покажут пик на больших городах.
/// </summary>
internal static class Growth
{
    // ------------------------------------------------------------------ состояние зданий

    /// <summary>Конец стройки: <c>Constructing</c> -> <c>Active</c>; фирма открывает все места и платит <c>construct_cost</c> внешнему миру (потоки <c>Construction</c>).</summary>
    public static void Complete(World w, CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        b.State[bi] = (byte)BuildingState.Active;
        b.StateTick[bi] = w.Tick;
        b.Version++;   // скимы и рынки перестраивают индексы по версии
        c.Lots.Version++;
        if (t.Kind != LotZone.Residential)
        {
            b.Jobs[bi] = t.Capacity;
            c.Ledger.Transfer(c.Ledger.AccountOfBuilding(bi), c.Ledger.ExternalAccount, c.Cfg.Economy.ConstructCost, FlowKind.Construction);
        }

        Parking.ParkingBuild.OnBuildingActive(w, c, bi);   // стоянка здания (parking.md §3); без режима - ничего
        CityLife.OnComplete(c, bi);   // building-lifecycle.md §2.2: новое здание; без режима - ничего
        w.Counters.BuildingsBuilt++;
    }

    /// <summary>
    /// Заброшенность (§8.4): работники становятся ищущими, жители - выселенными (<c>Evicted</c>, переезд с приоритетом, отъезд по дедлайну). Здание удаляется суточным шагом через
    /// <c>vacate_days</c> или когда жителей не осталось. Вызывают закрытие фирмы, <c>PaintZone</c> и потеря дороги (Z6).
    /// </summary>
    public static void Abandon(World w, CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        CityResidents res = c.Residents;
        if (!b.IsAlive(bi) || b.State[bi] == (byte)BuildingState.Abandoned)
        {
            return;
        }

        if (c.Types.Rows[b.Type[bi]].Kind == LotZone.Residential)
        {
            for (int r = b.HeadRes[bi]; r >= 0; r = res.NextHome[r])
            {
                res.Flags[r] |= ResidentFlags.Evicted;
                res.EvictDays[r] = 0;
            }
        }
        else
        {
            while (b.HeadWork[bi] >= 0)
            {
                c.Market.Fire(w, c, b.HeadWork[bi]);
            }

            b.Jobs[bi] = 0;
        }

        b.State[bi] = (byte)BuildingState.Abandoned;
        b.StateTick[bi] = w.Tick;
        b.Version++;
        c.Lots.Version++;
        Parking.ParkingBuild.OnBuildingAbandoned(w, c, bi);   // стоянка здания закрывается
        w.Counters.BuildingsAbandoned++;
    }

    // ------------------------------------------------------------------ суточный шаг

    /// <summary>Накопители вчерашних суток зданий обнуляются после прохода по зданиям (слот будет сегодняшним через сутки).</summary>
    public static void ClearYesterday(CityWorld c)
    {
        BuildingStore b = c.Buildings;
        int yi = (c.Day + 1) & 1;
        for (int i = 0; i < b.HighWater; i++)
        {
            int s = Economy.Slot(i, yi);
            b.RevenueDay[s] = 0;
            b.CostDay[s] = 0;
            b.VisitsDay[s] = 0;
        }
    }

    /// <summary>Стадия 2 дневного прохода: очистка вчерашних накопителей, спрос, снос, въезд, рост.</summary>
    public static void Daily(World w, CityWorld c)
    {
        ClearYesterday(c);
        Market m = c.Market;
        m.Sync(c);
        if (c.Cfg.Zoning.GrowthStep != 0)
        {
            RemoveAbandoned(w, c);   // спрос, въезд и рост идут по часам (Hourly)
            c.Outside?.DayDecay(m.TotalVac);   // затухание внешних мест - раз в сутки и при росте по часам (§2.4)
            return;
        }

        UpdateDemand(c, m);
        RemoveAbandoned(w, c);
        c.Outside?.DayDecay(m.TotalVac);   // затухание внешних мест по своим вакансиям города (§2.4)
        Grow(w, c, m, 1);
    }

    /// <summary>
    /// <c>zoning.growth_step = "hour"</c>: спрос, въезд и рост зданий на сдвиге игрового часа (последний тик часа, в том числе в сутки 0); лимиты суток делятся на 24 с округлением вверх.
    /// Остальной дневной проход (фирмы, жители, снос) остаётся суточным.
    /// </summary>
    public static void Hourly(World w, CityWorld c)
    {
        Market m = c.Market;
        m.Sync(c);
        UpdateDemand(c, m);
        Grow(w, c, m, 24);
    }

    /// <summary>Въезд и рост с лимитами суток, делёнными на <paramref name="parts"/> (1 - суточный шаг, 24 - часовой).</summary>
    private static void Grow(World w, CityWorld c, Market m, int parts)
    {
        int pop = c.Residents.AliveCount;
        long cap = Math.Max(c.Cfg.Residents.MinImmigrantsPerDay, ((long)c.Cfg.Residents.MaxImmigrantsPerDayQ16 * pop) >> 16);
        cap = (cap + parts - 1) / parts;
        int want = (int)Math.Min(Math.Max(1L, cap), Math.Max(0, c.DemandR));
        if (c.DemandR > 0 && want > 0)
        {
            m.Immigrate(w, c, want);
        }

        if (c.Cfg.Zoning.Growth)
        {
            Build(w, c, m, parts);
        }
    }

    /// <summary>Сглаженный спрос (§8.3): R = вакансии − ищущие + r_bias, C = визиты впустую + превышение нормы, I = ищущие − вакансии; EWMA со сдвигом <c>ewma_shift</c>.</summary>
    private static void UpdateDemand(CityWorld c, Market m)
    {
        CityResidents res = c.Residents;
        BuildingStore b = c.Buildings;
        long seekers = 0;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (res.Pool.IsAlive(r) && (res.Flags[r] & ResidentFlags.Labour) != 0 && res.Work[r] == -1)
            {
                seekers++;
            }
        }

        long dayCap = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind == LotZone.Commercial)
            {
                ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[i]];
                dayCap += (long)t.ShopCapacity * Math.Max(1, t.ShiftSlots / 12);
            }
        }

        int y = (c.Day + 1) & 1;
        long visits = c.VisitsDayTotal[y];
        long excess = Math.Max(0L, visits - (c.Cfg.Economy.CTargetPct * dayCap / 100));
        long rawC = c.UnservedDayTotal[y] + excess;
        long rawI = seekers - m.TotalVac;
        long rawR = m.TotalVac + (c.Outside?.TotalVac() ?? 0) - seekers + c.Cfg.Residents.RBias;   // внешние вакансии - только в спрос R (ADR-0037), в I не входят
        int shift = c.Cfg.Economy.EwmaShift;
        c.DemandR += (int)((Clamp(rawR) - c.DemandR) >> shift);
        c.DemandC += (int)((Clamp(rawC) - c.DemandC) >> shift);
        c.DemandI += (int)((Clamp(rawI) - c.DemandI) >> shift);
    }

    private static long Clamp(long v) => Math.Clamp(v, -1_000_000_000L, 1_000_000_000L);

    // ------------------------------------------------------------------ снос

    /// <summary>
    /// Удаляет заброшенные здания, у которых прошло <c>vacate_days</c> или не осталось жильцов/работников. Здание, на которое ссылается тур или нога жителя в пути, остаётся до завтра;
    /// будущие туры дома к сносимому зданию отбрасываются, ссылки завершённых туров сбрасываются.
    /// </summary>
    private static void RemoveAbandoned(World w, CityWorld c)
    {
        BuildingStore b = c.Buildings;
        CityResidents res = c.Residents;
        bool[] marked = c.Market.Removing;
        long vacate = (long)c.Cfg.Residents.VacateDays * c.DayTicks;
        bool life = c.Cfg.Lifecycle.Enabled;
        int n = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            marked[i] = false;
            if (!b.IsAlive(i) || b.State[i] != (byte)BuildingState.Abandoned)
            {
                continue;
            }

            if (life)
            {
                // building-lifecycle.md §2.3: заброшенное стоит; удаляются только снесённые игроком и рассыпавшиеся, vacate_days не действует
                if (!CityLife.ToRemove(c, i))
                {
                    continue;
                }

                if (b.Occupants[i] == 0)
                {
                    marked[i] = true;
                    n++;
                }
                else
                {
                    for (int r = b.HeadRes[i]; r >= 0; r = res.NextHome[r])
                    {
                        res.Flags[r] |= ResidentFlags.Leaving;
                    }
                }

                continue;
            }

            if (b.Occupants[i] == 0)
            {
                marked[i] = true;
                n++;
            }
            else if (w.Tick - b.StateTick[i] >= vacate)
            {
                for (int r = b.HeadRes[i]; r >= 0; r = res.NextHome[r])
                {
                    res.Flags[r] |= ResidentFlags.Leaving;   // срок вышел: оставшиеся уедут при ближайшем возвращении домой
                }
            }
        }

        if (n == 0)
        {
            return;
        }

        const int Tours = CityResidents.MaxTours;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (!res.Pool.IsAlive(r))
            {
                continue;
            }

            bool away = res.State[r] != (byte)ResidentState.AtHome;
            if (away)
            {
                int cur = res.PlanCursor[r];
                int dest = cur < res.PlanCount[r] ? res.PlanDest[(r * Tours) + cur] : -1;
                if (dest >= 0 && marked[dest])
                {
                    marked[dest] = false;
                }

                if (res.LegDest[r] >= 0 && marked[res.LegDest[r]])
                {
                    marked[res.LegDest[r]] = false;
                }
            }
        }

        for (int r = 0; r < res.HighWater; r++)
        {
            if (!res.Pool.IsAlive(r))
            {
                continue;
            }

            for (int k = 0; k < Tours; k++)
            {
                int i = (r * Tours) + k;
                int dest = res.PlanDest[i];
                if (dest < 0 || !marked[dest])
                {
                    continue;
                }

                if (k >= res.PlanCursor[r] && k < res.PlanCount[r])
                {
                    res.PlanCount[r] = (byte)k;   // тур к сносимому зданию и все следующие отбрасываются
                }

                res.PlanKind[i] = 0;
                res.PlanMode[i] = 0;
                res.PlanDest[i] = -1;
                res.PlanSlot[i] = 0;
            }
        }

        for (int i = 0; i < b.HighWater; i++)
        {
            if (!marked[i])
            {
                continue;
            }

            marked[i] = false;
            int lot = b.Lot[i];
            if (life)
            {
                CityLife.OnRemove(w, c, i);   // рассыпавшееся - дешёвая очистка из казны (§2.5)
            }

            c.Ledger.Transfer(c.Ledger.AccountOfBuilding(i), c.Ledger.ExternalAccount, b.Account[i], FlowKind.Emigration);
            Parking.ParkingBuild.OnBuildingRemoved(w, c, i);   // стоянка здания снесена (до Destroy: слот ещё хранит участок)
            b.Destroy(i);
            if (lot >= 0)
            {
                c.Lots.Building[lot] = -1;
            }

            c.Lots.Version++;
            w.Counters.BuildingsRemoved++;
        }
    }

    // ------------------------------------------------------------------ рост

    /// <summary>
    /// Балл R ячейки без своих рабочих мест (ADR-0037): при внешних подключениях с вакансиями - минус ским машиной до ближайшего узла плюс <c>job_cost</c>, иначе «нельзя».
    /// Без секции <c>outside</c> - прежний <c>int.MinValue</c>.
    /// </summary>
    private static int OutsideScoreR(CityWorld c, Skims s, int cell)
    {
        OutsideState? o = c.Outside;
        if (o == null)
        {
            return int.MinValue;
        }

        int best = int.MaxValue;
        for (int k = 0; k < o.Count; k++)
        {
            int lot = c.Lots.Capacity + k;
            if (o.Vac(k) <= 0 || c.Lots.CarLane[lot] < 0)
            {
                continue;
            }

            int v = s.Get(cell, c.Lots.Cell[lot], TravelMode.Car, 0);
            if (v > 0)
            {
                best = Math.Min(best, v + o.JobCostDs[k]);
            }
        }

        return best == int.MaxValue ? int.MinValue : -best;
    }

    /// <summary>Новые здания (§8.3): для каждой зоны с положительным спросом - лучшие по баллу доступности пустые участки, не больше <c>max_new_buildings_per_day</c> в сумме.</summary>
    private static void Build(World w, CityWorld c, Market m, int parts)
    {
        CityDesc cfg = c.Cfg;
        int maxNew = Math.Max(1, (cfg.Zoning.MaxNewBuildingsPerDay + parts - 1) / parts);
        long freeHousing = m.TotalFree;
        long needR = c.DemandR - freeHousing;
        long needC = c.DemandC;
        long needI = c.DemandI;
        Span<int> want = stackalloc int[3];
        want[0] = Units(c, LotZone.Residential, needR);
        want[1] = Units(c, LotZone.Commercial, needC);
        want[2] = Units(c, LotZone.Industrial, needI);
        long sum = want[0] + (long)want[1] + want[2];
        if (sum == 0)
        {
            return;
        }

        if (sum > maxNew)
        {
            for (int z = 0; z < 3; z++)
            {
                want[z] = want[z] == 0 ? 0 : (int)Math.Max(1L, (long)maxNew * want[z] / sum);
            }
        }

        Skims s = c.Skims;
        s.Prepare(w);
        int theta = cfg.Choice.ThetaMode64;
        for (int cell = 0; cell < s.Cells; cell++)
        {
            m.ScoreR[cell] = s.LsJobs[cell] == Skims.NoLs ? OutsideScoreR(c, s, cell) : -s.LsJobs[cell];
            int ls = Accessibility.Combined(s, cell, true, theta);
            m.ScoreC[cell] = ls == Skims.NoLs ? int.MinValue : -ls;
        }

        m.TopCount[0] = 0;
        m.TopCount[1] = 0;
        m.TopCount[2] = 0;
        LotStore l = c.Lots;
        for (int i = 0; i < l.Count; i++)
        {
            int z = l.Zone[i] - 1;
            if (z < 0 || z > 2 || want[z] == 0 || (l.Flags[i] & LotFlags.Lost) != 0)
            {
                continue;
            }

            // Занятый участок - только заброшенное здание, годное к повторному заселению (building-lifecycle.md §2.3); без режима - как раньше
            if (l.Building[i] >= 0 && !(cfg.Lifecycle.Enabled && CityLife.Resettable(c, l.Building[i], (LotZone)l.Zone[i])))
            {
                continue;
            }

            int score = z == 0 ? m.ScoreR[l.Cell[i]] : m.ScoreC[l.Cell[i]];
            if (score == int.MinValue)
            {
                continue;
            }

            Insert(m, z, want[z], maxNew, i, score);
        }

        int remaining = maxNew;
        for (int z = 0; z < 3 && remaining > 0; z++)
        {
            for (int k = 0; k < m.TopCount[z] && remaining > 0; k++)
            {
                if (!Place(w, c, m.TopLot[(z * maxNew) + k]))
                {
                    return;   // ёмкость зданий исчерпана
                }

                remaining--;
            }
        }
    }

    /// <summary>Сколько зданий вида <paramref name="kind"/> нужно под потребность <paramref name="need"/> (в единицах вида: места жилья, визиты в сутки, рабочие места); 0, если потребности нет.</summary>
    private static int Units(CityWorld c, LotZone kind, long need)
    {
        if (need <= 0)
        {
            return 0;
        }

        int t = c.Types.Find(kind, 0, 0);
        if (t < 0)
        {
            return 0;
        }

        ref readonly BuildingTypeDesc d = ref c.Types.Rows[t];
        long unit = kind switch
        {
            LotZone.Residential => d.Capacity,
            LotZone.Commercial => (long)d.ShopCapacity * Math.Max(1, d.ShiftSlots / 12),
            _ => d.Capacity,
        };
        return (int)Math.Min(int.MaxValue / 4, (need + Math.Max(1L, unit) - 1) / Math.Max(1L, unit));
    }

    private static void Insert(Market m, int zone, int cap, int maxNew, int lot, int score)
    {
        int baseIdx = zone * maxNew;
        int count = m.TopCount[zone];
        cap = Math.Min(cap, maxNew);
        if (count == cap && score <= m.TopScore[baseIdx + count - 1])
        {
            return;
        }

        int pos = count < cap ? count : count - 1;
        while (pos > 0 && m.TopScore[baseIdx + pos - 1] < score)
        {
            m.TopLot[baseIdx + pos] = m.TopLot[baseIdx + pos - 1];
            m.TopScore[baseIdx + pos] = m.TopScore[baseIdx + pos - 1];
            pos--;
        }

        m.TopLot[baseIdx + pos] = lot;
        m.TopScore[baseIdx + pos] = score;
        if (count < cap)
        {
            m.TopCount[zone]++;
        }
    }

    /// <summary>Создаёт здание на участке (<c>Constructing</c>); фирма сразу получает стартовый баланс от <c>External</c>. false - нет места в пуле зданий.</summary>
    private static bool Place(World w, CityWorld c, int lot)
    {
        LotStore l = c.Lots;
        if (l.Building[lot] >= 0)
        {
            CityLife.Resettle(w, c, l.Building[lot]);   // Build берёт занятый участок только с годной заброшкой (режим city.lifecycle)
            return true;
        }

        int type = c.Types.Find((LotZone)l.Zone[lot], l.Density[lot], lot);
        if (type < 0)
        {
            return true;
        }

        if (!c.Buildings.TryCreate(lot, type, BuildingState.Constructing, w.Tick, out int bi))
        {
            return false;
        }

        CityLife.OnCreate(c, bi);
        l.Building[lot] = bi;
        l.Version++;
        if (c.Types.Rows[type].Kind != LotZone.Residential)
        {
            c.Ledger.Transfer(c.Ledger.ExternalAccount, c.Ledger.AccountOfBuilding(bi), c.Cfg.Economy.InitialFirmBalance, FlowKind.Endowment);
        }

        return true;
    }
}
