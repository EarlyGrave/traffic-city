using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Demand;

/// <summary>
/// Выбор жителя (zoning-demand.md §6, ADR-0021): частота дискреционных туров, цель, способ и слот выезда - вложенный целочисленный логит (<see cref="DetLogit"/>) по скимам и доступности.
/// Сигнатуры <c>Choose*</c> заморожены Z1; решения читают только состояние (скимы, поле доступности, здания) и поток PRNG <c>Demand</c>, поэтому одинаковы в micro и aggregate.
/// <para>
/// Обращения к PRNG (фиксированное число на решение): частота - 0 или 2 слова (0, если целей нет); цель - по 2 слова + 1 слово на кандидата и 2 слова на итоговый выбор (0, если нет
/// ячеек с C); способ - 2 слова при наличии доступного; слот - 2 слова при наличии допустимого. Решения не обращаются к PRNG при пустом выборе, и это детерминировано состоянием.
/// </para>
/// <para>
/// Стоимость пути - <c>Skims.Get</c> (+ поправка по фактическому расстоянию участков внутри ячейки, §5.4) плюс <c>ASC_m</c>; стоимость тура - ноги туда (период выезда) и обратно
/// (период возврата) плюс 2 · ASC. Ноль байт на решение: рабочие массивы предвыделены в <see cref="Skims.Prepare"/>, мелкие буферы - <c>stackalloc</c>.
/// </para>
/// </summary>
internal static class Choice
{
    private const int NoCost = DetLogit.NoCost;

    /// <summary>Число дискреционных туров на сегодня, <c>0..freq_max</c> (§6.3).</summary>
    public static int ChooseFrequency(World w, int resident)
    {
        CityWorld c = w.City!;
        CityChoiceDesc ch = c.Cfg.Choice;
        int fmax = ch.FreqMax;
        if (fmax <= 0)
        {
            return 0;
        }

        Skims s = c.Skims;
        s.Prepare(w);
        int home = c.Residents.Home[resident];
        if (home < 0)
        {
            return 0;
        }

        int cell = c.Lots.Cell[c.Buildings.Lot[home]];
        int ls = Accessibility.Combined(s, cell, HasCar(c, resident), ch.ThetaMode64);
        if (ls == Skims.NoLs)
        {
            return 0;
        }

        long delta = s.LsRefSet ? (long)ls - s.LsRef : 0;
        long term = ch.AscFreqDs - ((ch.BetaFreqQ8 * delta) >> 8) + (c.Residents.Work[resident] < 0 ? ch.AscNonWorkerDs : 0);
        Span<int> costs = stackalloc int[CityResidents.MaxTours + 1];
        for (int k = 0; k <= fmax; k++)
        {
            // U_k = k·term − k²·γ; логит по стоимости -U.
            long cost = ((long)k * k * ch.GammaFreqDs) - (k * term);
            costs[k] = (int)Math.Clamp(cost, -1_000_000_000L, 1_000_000_000L);
        }

        int pick = DetLogit.Choose(ref w.RngDemand, costs[..(fmax + 1)], ch.ThetaFreq64);
        return pick < 0 ? 0 : pick;
    }

    /// <summary>Здание-цель дискреционного тура C или -1 (§6.4): K кандидатов по гравитации ячеек, внутри ячейки равновероятно среди активных C той же дорожной компоненты, затем логит.</summary>
    public static int ChooseDestination(World w, int resident)
    {
        CityWorld c = w.City!;
        CityChoiceDesc ch = c.Cfg.Choice;
        Skims s = c.Skims;
        s.Prepare(w);
        int home = c.Residents.Home[resident];
        if (home < 0)
        {
            return -1;
        }

        int homeLot = c.Buildings.Lot[home];
        int homeCell = c.Lots.Cell[homeLot];
        int homeComp = c.Lots.RoadComp[homeLot];
        bool hasCar = HasCar(c, resident);
        int period = c.PeriodOf(w.Tick);
        int nc = s.Cells;

        // Вес ячейки-цели: ShopCap · w(лучшая стоимость до ячейки), Q30 -> 16 бит, чтобы сумма по ячейкам не переполнила long.
        Span<int> cellCost = s.ScratchInt.AsSpan(0, nc);
        int cmin = NoCost;
        for (int d = 0; d < nc; d++)
        {
            int best = NoCost;
            if (s.CellShopCap![d] > 0)
            {
                for (int m = 0; m < Skims.Modes; m++)
                {
                    int v = CellLegCost(s, hasCar, homeCell, d, (TravelMode)m, period);
                    if (v != NoCost)
                    {
                        best = Math.Min(best, v + AscOf(in ch, m));
                    }
                }
            }

            cellCost[d] = best;
            cmin = Math.Min(cmin, best);
        }

        if (cmin == NoCost)
        {
            return -1;
        }

        Span<long> weights = s.ScratchLong.AsSpan(0, nc);
        for (int d = 0; d < nc; d++)
        {
            weights[d] = cellCost[d] == NoCost ? 0L : (long)s.CellShopCap![d] * (DetLogit.Weight((long)cellCost[d] - cmin, ch.ThetaDest64) >> 14);
        }

        int k = ch.DestCandidates;
        Span<int> candB = stackalloc int[k];
        Span<int> candC = stackalloc int[k];
        int attractRef = DetLogit.Ln64(256);
        for (int i = 0; i < k; i++)
        {
            candB[i] = -1;
            candC[i] = NoCost;
            int cell = DetLogit.ChooseWeighted(ref w.RngDemand, weights);
            if (cell < 0)
            {
                return -1;
            }

            int a = s.CellStart![cell];
            int n = 0;
            for (int j = a; j < s.CellStart[cell + 1]; j++)
            {
                if (c.Lots.RoadComp[c.Buildings.Lot[s.CellList![j]]] == homeComp)
                {
                    n++;
                }
            }

            int pickIdx = (int)w.RngDemand.NextBelow((uint)Math.Max(1, n));
            if (n == 0)
            {
                continue;
            }

            int b = -1;
            for (int j = a; j < s.CellStart[cell + 1]; j++)
            {
                int cand = s.CellList![j];
                if (c.Lots.RoadComp[c.Buildings.Lot[cand]] == homeComp && pickIdx-- == 0)
                {
                    b = cand;
                    break;
                }
            }

            int destLot = c.Buildings.Lot[b];
            int cbest = NoCost;
            for (int m = 0; m < Skims.Modes; m++)
            {
                int v = LegCost(c, s, hasCar, homeLot, destLot, (TravelMode)m, period);
                if (v != NoCost)
                {
                    cbest = Math.Min(cbest, v + AscOf(in ch, m));
                }
            }

            if (cbest == NoCost)
            {
                continue;
            }

            int attract = c.Types.Rows[c.Buildings.Type[b]].AttractQ8;
            int bonus = DetLogit.LnUnitsToDs(DetLogit.Ln64(attract) - attractRef, ch.ThetaDest64);
            candB[i] = b;
            candC[i] = cbest - bonus;
        }

        int pick = DetLogit.Choose(ref w.RngDemand, candC, ch.ThetaDest64);
        return pick < 0 ? -1 : candB[pick];
    }

    /// <summary>Способ тура к зданию <paramref name="dest"/> в период <paramref name="period"/> (§6.5). Нет доступных способов - <see cref="TravelMode.Walk"/> (вызывающий проверяет <see cref="TryChooseMode"/>).</summary>
    public static TravelMode ChooseMode(World w, int resident, int dest, int period) =>
        TryChooseMode(w, resident, dest, period, out TravelMode m) ? m : TravelMode.Walk;

    /// <summary>Способ тура; false - ни один способ недоступен (тур отменяется, <c>res_tours_unreachable</c>).</summary>
    public static bool TryChooseMode(World w, int resident, int dest, int period, out TravelMode mode)
    {
        mode = TravelMode.Walk;
        CityWorld c = w.City!;
        CityChoiceDesc ch = c.Cfg.Choice;
        Skims s = c.Skims;
        s.Prepare(w);
        int home = c.Residents.Home[resident];
        if (home < 0 || dest < 0)
        {
            return false;
        }

        int homeLot = c.Buildings.Lot[home];
        int destLot = c.Buildings.Lot[dest];
        bool hasCar = HasCar(c, resident);
        int stayTicks = StaySlots(c, resident, dest) * c.SlotTicks;
        int back = (int)((period + ((long)stayTicks * s.Periods / c.DayTicks)) % s.Periods);
        Span<int> costs = stackalloc int[Skims.Modes];
        for (int m = 0; m < Skims.Modes; m++)
        {
            int there = LegCost(c, s, hasCar, homeLot, destLot, (TravelMode)m, period);
            int ret = there == NoCost ? NoCost : LegCost(c, s, hasCar, destLot, homeLot, (TravelMode)m, back);
            costs[m] = ret == NoCost ? NoCost : there + ret + (2 * AscOf(in ch, m));
        }

        int pick = DetLogit.Choose(ref w.RngDemand, costs, ch.ThetaMode64);
        if (pick < 0)
        {
            return false;
        }

        mode = (TravelMode)pick;
        return true;
    }

    /// <summary>
    /// Слот выезда для желаемого прибытия <paramref name="desiredArriveSlot"/> при времени ноги <paramref name="travelSlots"/> (§6.6): пять кандидатов <c>t* - T + j·Δ</c>, стоимость - время ноги
    /// плюс штраф раннего/позднего прибытия. Время ноги одинаково для всех кандидатов (способ и ячейки в сигнатуре не заданы); с зависимостью от периода выезда - перегрузка ниже.
    /// </summary>
    public static int ChooseDepartSlot(World w, int resident, int desiredArriveSlot, int travelSlots) =>
        PickDepart(w, desiredArriveSlot, travelSlots, -1, -1, TravelMode.Car);

    /// <summary>То же, но время ноги кандидата берётся из скима периода слота выезда (<paramref name="fromCell"/> -> <paramref name="toCell"/>, <paramref name="mode"/>): в пик стоять дорого, пик растягивается.</summary>
    public static int ChooseDepartSlot(World w, int resident, int desiredArriveSlot, int travelSlots, int fromCell, int toCell, TravelMode mode) =>
        PickDepart(w, desiredArriveSlot, travelSlots, fromCell, toCell, mode);

    private static int PickDepart(World w, int desired, int travelSlots, int fromCell, int toCell, TravelMode mode)
    {
        CityWorld c = w.City!;
        CityChoiceDesc ch = c.Cfg.Choice;
        Skims s = c.Skims;
        int slotDs = Math.Max(1, c.SlotTicks * 10 / Simulation.TicksPerSecond);
        int step = ch.TimeStepSlots;
        int baseDep = desired - travelSlots;
        bool bySkim = fromCell >= 0 && toCell >= 0;
        if (bySkim)
        {
            s.Prepare(w);
        }

        Span<int> deps = stackalloc int[5];
        Span<int> costs = stackalloc int[5];
        for (int j = 0; j < 5; j++)
        {
            int dep = baseDep + ((j - 2) * step);
            deps[j] = dep;
            if (dep < 0 || dep >= CityWorld.SlotsPerDay)
            {
                costs[j] = NoCost;
                continue;
            }

            long tDs;
            int tSlots;
            if (bySkim)
            {
                int p = (int)((long)dep * s.Periods / CityWorld.SlotsPerDay);
                int v = s.Get(fromCell, toCell, mode, p);
                tDs = v > 0 ? v : (long)travelSlots * slotDs;
                tSlots = (int)((tDs + slotDs - 1) / slotDs);
            }
            else
            {
                tSlots = travelSlots;
                tDs = (long)travelSlots * slotDs;
            }

            int arr = dep + tSlots;
            long early = Math.Max(0, desired - arr) * (long)slotDs;
            long late = Math.Max(0, arr - desired) * (long)slotDs;
            long cost = tDs + ((ch.SdeQ8 * early) >> 8) + ((ch.SdlQ8 * late) >> 8);
            costs[j] = (int)Math.Min(cost, 1_000_000_000L);
        }

        int pick = DetLogit.Choose(ref w.RngDemand, costs, ch.ThetaTime64);
        return pick < 0 ? Math.Max(0, baseDep) : deps[pick];
    }

    // ---------------------------------------------------------------- стоимости

    private static bool HasCar(CityWorld c, int resident) => (c.Residents.Flags[resident] & ResidentFlags.HasCar) != 0;

    private static int AscOf(in CityChoiceDesc ch, int m) => m == (int)TravelMode.Car ? ch.AscCarDs : (m == (int)TravelMode.Bus ? ch.AscBusDs : ch.AscWalkDs);

    /// <summary>Слотов на месте назначения: смена, если <paramref name="dest"/> - работа жителя, иначе визит.</summary>
    private static int StaySlots(CityWorld c, int resident, int dest) =>
        c.Residents.Work[resident] == dest ? c.Types.Rows[c.Buildings.Type[dest]].ShiftSlots : c.Cfg.Residents.ShopSlots;

    /// <summary>Стоимость между ячейками без привязки к участкам (для весов ячеек-целей): машина - при <paramref name="hasCar"/>, пешком - по расстоянию центров. <see cref="NoCost"/> - недоступно.</summary>
    private static int CellLegCost(Skims s, bool hasCar, int o, int d, TravelMode m, int period)
    {
        if ((m == TravelMode.Car && !hasCar) || (m == TravelMode.Walk && !s.WalkReachable(o, d)))
        {
            return NoCost;
        }

        int v = s.Get(o, d, m, period);
        return v > 0 ? v : NoCost;
    }

    /// <summary>
    /// Стоимость ноги между участками (без ASC): ским пары ячеек плюс внутриячеечная поправка по фактическому расстоянию (§5.4). Машина - свой автомобиль и общая дорожная компонента;
    /// автобус и пешком - общая пешеходная компонента (без сети пешеходов - любая); пешком - не дальше <c>max_walk</c>. <see cref="NoCost"/> - недоступно.
    /// </summary>
    internal static int LegCost(CityWorld c, Skims s, bool hasCar, int lotA, int lotB, TravelMode m, int period)
    {
        LotStore l = c.Lots;
        if (m == TravelMode.Car)
        {
            if (!hasCar || l.RoadComp[lotA] < 0 || l.RoadComp[lotA] != l.RoadComp[lotB])
            {
                return NoCost;
            }
        }
        else
        {
            int pa = l.PedComp[lotA];
            int pb = l.PedComp[lotB];
            if ((pa >= 0 || pb >= 0) && pa != pb)
            {
                return NoCost;
            }
        }

        int o = l.Cell[lotA];
        int d = l.Cell[lotB];
        int v = s.Get(o, d, m, period);
        if (v <= 0)
        {
            return NoCost;
        }

        if (m == TravelMode.Walk || (o == d && m != TravelMode.Bus))
        {
            float dx = l.X[lotA] - l.X[lotB];
            float dz = l.Z[lotA] - l.Z[lotB];
            int distCm = (int)(MathF.Sqrt((dx * dx) + (dz * dz)) * 100f);
            if (m == TravelMode.Walk && distCm > c.Cfg.Choice.MaxWalkMm / 10)
            {
                return NoCost;
            }

            if (o == d)
            {
                int speed = s.ModeSpeedCms(m, o);
                v = Math.Max(1, v + (int)((long)Skims.DetourFactorQ4 * (distCm - s.IntraCellCm) / speed));
            }
        }

        return v;
    }
}
