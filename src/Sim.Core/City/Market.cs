using System;
using Sim.Core.Api;
using Sim.Core.Demand;

namespace Sim.Core.City;

/// <summary>
/// Рынки труда и жилья (zoning-demand.md §8.1-§8.2): очередь ищущих работу (FIFO по <c>Seq</c>, <c>match_per_tick</c> за тик), подбор фирмы (K = 8 ячеек-кандидатов по весу
/// <c>вакансии · w(C_commute)</c>, внутри ячейки - первая фирма с вакансией со сдвигом по <c>Seq</c>, выбор между кандидатами - логит по стоимости ноги на работу), смена работы, переезд,
/// въезд и отъезд. Все изменения занятости и жилья идут через методы этого класса: они держат списки (<c>HeadWork</c>/<c>HeadRes</c>), <c>Occupants</c> и производные суммы вакансий и
/// свободного жилья по ячейкам согласованными. Производные (<see cref="CellVac"/>, <see cref="CellFree"/>, списки зданий по ячейкам, кэш стоимостей ячеек) в сейв не входят и
/// перестраиваются при смене <c>Buildings.Version</c> и после загрузки (<see cref="Invalidate"/>); любое изменение набора или состояния зданий обязано увеличивать версию.
/// <para>Ноль байт на тик в стационаре: все рабочие массивы выделены в конструкторе (размеры - ёмкость зданий, число ячеек, <c>max_new_buildings_per_day</c>).</para>
/// </summary>
internal sealed class Market
{
    public const int Candidates = 8;
    private const int NoCost = DetLogit.NoCost;
    private const int MaxCachedCells = 1024;

    public readonly int[] CellVac;      // [NC] Σ max(0, Jobs - Occupants) по Active C/I
    public readonly int[] CellFree;     // [NC] Σ max(0, Capacity - Occupants) по Active R
    public long TotalVac;
    public long TotalFree;

    private readonly int _cells;
    private readonly int[] _firmStart;  // [NC + 1] CSR Active C/I по ячейкам (индексы по возрастанию)
    private readonly int[] _firmList;
    private readonly int[] _resStart;   // [NC + 1] CSR Active R по ячейкам
    private readonly int[] _resList;
    private readonly long[] _weight;    // [NC]
    private readonly int[] _cost;       // [NC]
    private readonly int[]? _rowCost;   // [NC * NC * 2] кэш стоимости ячейка -> ячейка (авто/без) на сутки; null при больших NC
    private readonly int[]? _rowDay;    // [NC * 2] сутки, на которые кэш строки (o, hasCar) верен, -1 нет
    private int _builtVersion = -1;
    private readonly long[] _cum;       // [max_buildings] накопленная вчерашняя выработка I-фирм по индексу здания (жребий поставщика товаров)
    private bool _cumValid;
    private long _cumTotal;

    // Рост зданий: лучшие участки по зонам (R, C, I), scratch на max_new_buildings_per_day.
    internal readonly int[] TopLot;     // [3 * maxNew]
    internal readonly long[] TopScore;  // [3 * maxNew]
    internal readonly int[] TopCount;   // [3]
    internal readonly bool[] Removing;  // [max_buildings] снос сегодня
    internal readonly int[] ScoreR;     // [NC] балл ячейки для роста R (больше - лучше)
    internal readonly int[] ScoreC;     // [NC] то же для C и I

    public Market(int cells, int buildingCapacity, int maxNew)
    {
        _cells = cells;
        CellVac = new int[cells];
        CellFree = new int[cells];
        _firmStart = new int[cells + 1];
        _firmList = new int[buildingCapacity];
        _resStart = new int[cells + 1];
        _resList = new int[buildingCapacity];
        _weight = new long[cells];
        _cost = new int[cells];
        if (cells <= MaxCachedCells)
        {
            _rowCost = new int[cells * cells * 2];
            _rowDay = new int[cells * 2];
            Array.Fill(_rowDay, -1);
        }

        _cum = new long[buildingCapacity];
        TopLot = new int[3 * maxNew];
        TopScore = new long[3 * maxNew];
        TopCount = new int[3];
        Removing = new bool[buildingCapacity];
        ScoreR = new int[cells];
        ScoreC = new int[cells];
    }

    /// <summary>Кэш стоимости строк (ячейка, есть ли машина) устарел: пересчитать при следующем обращении.</summary>
    public void InvalidateRows()
    {
        if (_rowDay != null)
        {
            Array.Fill(_rowDay, -1);
        }
    }

    /// <summary>Производные устарели (загрузка сейва, внешнее изменение): перестроить при следующем обращении.</summary>
    public void Invalidate()
    {
        _builtVersion = -1;
        _cumValid = false;
        if (_rowDay != null)
        {
            Array.Fill(_rowDay, -1);
        }
    }

    // ------------------------------------------------------------------ поставщик товаров

    /// <summary>Выработка вчерашних суток изменилась (начало прохода по зданиям): накопленные суммы поставщиков устарели.</summary>
    public void InvalidateSupplier() => _cumValid = false;

    /// <summary>
    /// I-фирма - получатель товаров C-фирмы: жребий по вчерашней выработке (смены, <c>VisitsDay</c> вчерашнего слота) среди активных I-фирм; -1, если выработки не было.
    /// Накопленные суммы строятся лениво (при первом вызове прохода и после загрузки) по неизменным до конца прохода данным.
    /// </summary>
    public int PickSupplier(World w, CityWorld c)
    {
        BuildingStore b = c.Buildings;
        if (!_cumValid)
        {
            int yi = (c.Day + 1) & 1;
            long acc = 0;
            for (int i = 0; i < b.HighWater; i++)
            {
                if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind == LotZone.Industrial)
                {
                    acc += b.VisitsDay[Economy.Slot(i, yi)];
                }

                _cum[i] = acc;
            }

            _cumTotal = acc;
            _cumValid = true;
        }

        if (_cumTotal <= 0)
        {
            return -1;
        }

        long x = (long)(DetLogit.NextUInt64(ref w.RngDemand) % (ulong)_cumTotal);
        int lo = 0;
        int hi = b.HighWater - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cum[mid] > x)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    // ------------------------------------------------------------------ производные

    /// <summary>Приводит производные в соответствие версии зданий (суммы вакансий и свободного жилья верны после вызова).</summary>
    public void Sync(CityWorld c) => Ensure(c);

    /// <summary>Тик <see cref="MatchStep"/> ничего не изменит: очередь ищущих пуста либо вакансий нет (прыжок агрегата, zoning-demand.md §9.2).</summary>
    public bool MatchIsNoop(CityWorld c)
    {
        if (c.Residents.SeekQueue.IsEmpty)
        {
            return true;
        }

        Ensure(c);
        return TotalVac <= 0;
    }

    private void Ensure(CityWorld c)
    {
        if (_builtVersion == c.Buildings.Version)
        {
            return;
        }

        Rebuild(c);
        _builtVersion = c.Buildings.Version;
    }

    private void Rebuild(CityWorld c)
    {
        BuildingStore b = c.Buildings;
        LotStore l = c.Lots;
        Array.Clear(CellVac);
        Array.Clear(CellFree);
        Array.Clear(_firmStart);
        Array.Clear(_resStart);
        TotalVac = 0;
        TotalFree = 0;
        int hw = b.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || b.Lot[i] < 0)
            {
                continue;
            }

            int cell = l.Cell[b.Lot[i]];
            if (c.Types.Rows[b.Type[i]].Kind == LotZone.Residential)
            {
                _resStart[cell + 1]++;
                CellFree[cell] += FreeOf(c, i);
                TotalFree += FreeOf(c, i);
            }
            else
            {
                _firmStart[cell + 1]++;
                CellVac[cell] += VacOf(c, i);
                TotalVac += VacOf(c, i);
            }
        }

        for (int i = 0; i < _cells; i++)
        {
            _firmStart[i + 1] += _firmStart[i];
            _resStart[i + 1] += _resStart[i];
        }

        Span<int> fillF = _cost.AsSpan();
        fillF.Clear();
        // Заполнение: счётчики вставки во временных массивах _cost (фирмы), затем _weight-подобный проход для жилья.
        for (int i = 0; i < hw; i++)
        {
            if (!b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || b.Lot[i] < 0 || c.Types.Rows[b.Type[i]].Kind == LotZone.Residential)
            {
                continue;
            }

            int cell = l.Cell[b.Lot[i]];
            _firmList[_firmStart[cell] + fillF[cell]++] = i;
        }

        fillF.Clear();
        for (int i = 0; i < hw; i++)
        {
            if (!b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || b.Lot[i] < 0 || c.Types.Rows[b.Type[i]].Kind != LotZone.Residential)
            {
                continue;
            }

            int cell = l.Cell[b.Lot[i]];
            _resList[_resStart[cell] + fillF[cell]++] = i;
        }
    }

    /// <summary>Открытые места фирмы (0 для неактивных и жилых).</summary>
    internal static int VacOf(CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        if (b.State[bi] != (byte)BuildingState.Active || c.Types.Rows[b.Type[bi]].Kind == LotZone.Residential)
        {
            return 0;
        }

        return Math.Max(0, b.Jobs[bi] - b.Occupants[bi]);
    }

    /// <summary>Свободные жилые места здания (0 для неактивных и не жилых).</summary>
    internal static int FreeOf(CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        if (b.State[bi] != (byte)BuildingState.Active || c.Types.Rows[b.Type[bi]].Kind != LotZone.Residential)
        {
            return 0;
        }

        return Math.Max(0, c.Types.Rows[b.Type[bi]].Capacity - b.Occupants[bi]);
    }

    private void AdjustVac(CityWorld c, int bi, int before)
    {
        int d = VacOf(c, bi) - before;
        CellVac[c.Lots.Cell[c.Buildings.Lot[bi]]] += d;
        TotalVac += d;
    }

    private void AdjustFree(CityWorld c, int bi, int before)
    {
        int d = FreeOf(c, bi) - before;
        CellFree[c.Lots.Cell[c.Buildings.Lot[bi]]] += d;
        TotalFree += d;
    }

    // ------------------------------------------------------------------ занятость

    /// <summary>Нанимает жителя <paramref name="r"/> в фирму <paramref name="firm"/>: список работников, <c>Occupants</c>, вакансии.</summary>
    public void Employ(World w, CityWorld c, int r, int firm)
    {
        Ensure(c);
        BuildingStore b = c.Buildings;
        CityResidents res = c.Residents;
        int before = VacOf(c, firm);
        res.Work[r] = firm;
        res.NextWork[r] = b.HeadWork[firm];
        b.HeadWork[firm] = r;
        b.Occupants[firm]++;
        res.SeekDays[r] = 0;
        AdjustVac(c, firm, before);
        w.Counters.ResHired++;
    }

    /// <summary>Убирает жителя из списка работников его фирмы (<c>Work = -1</c>); в очередь ищущих не ставит.</summary>
    public void Unemploy(CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        int firm = res.Work[r];
        if (firm < 0)
        {
            return;
        }

        Ensure(c);
        BuildingStore b = c.Buildings;
        int before = VacOf(c, firm);
        if (b.HeadWork[firm] == r)
        {
            b.HeadWork[firm] = res.NextWork[r];
        }
        else
        {
            int prev = b.HeadWork[firm];
            while (prev >= 0 && res.NextWork[prev] != r)
            {
                prev = res.NextWork[prev];
            }

            if (prev >= 0)
            {
                res.NextWork[prev] = res.NextWork[r];
            }
        }

        res.NextWork[r] = -1;
        res.Work[r] = -1;
        b.Occupants[firm]--;
        AdjustVac(c, firm, before);
    }

    /// <summary>Житель начинает искать работу (рабочая сила): счётчик дней с нуля, место в очереди.</summary>
    public static void StartSeeking(CityWorld c, int r)
    {
        c.Residents.SeekDays[r] = 0;
        Enqueue(c, r);
    }

    private static void Enqueue(CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if ((res.Flags[r] & ResidentFlags.Labour) == 0 || (res.Flags[r] & ResidentFlags.InSeekQueue) != 0)
        {
            return;
        }

        if (res.SeekQueue.TryEnqueue(new SeekEntry { Index = r, Generation = res.Pool.GenerationOf(r) }))
        {
            res.Flags[r] |= ResidentFlags.InSeekQueue;
        }
    }

    /// <summary>Увольнение: работник уходит из фирмы и встаёт в очередь ищущих.</summary>
    public void Fire(World w, CityWorld c, int r)
    {
        Unemploy(c, r);
        w.Counters.ResFired++;
        StartSeeking(c, r);
    }

    /// <summary>Меняет число открытых мест фирмы; лишние работники (последние нанятые - голова списка) увольняются.</summary>
    public void SetJobs(World w, CityWorld c, int firm, int jobs)
    {
        Ensure(c);
        BuildingStore b = c.Buildings;
        int before = VacOf(c, firm);
        b.Jobs[firm] = Math.Max(0, jobs);
        AdjustVac(c, firm, before);
        while (b.Occupants[firm] > b.Jobs[firm] && b.HeadWork[firm] >= 0)
        {
            Fire(w, c, b.HeadWork[firm]);
        }
    }

    // ------------------------------------------------------------------ жильё

    /// <summary>Заселяет жителя в здание <paramref name="home"/> (список жильцов, <c>Occupants</c>, свободное жильё).</summary>
    public void MoveIn(CityWorld c, int r, int home)
    {
        Ensure(c);
        BuildingStore b = c.Buildings;
        CityResidents res = c.Residents;
        int before = FreeOf(c, home);
        res.Home[r] = home;
        res.NextHome[r] = b.HeadRes[home];
        b.HeadRes[home] = r;
        b.Occupants[home]++;
        AdjustFree(c, home, before);
    }

    /// <summary>Выселяет жителя из дома (<c>Home = -1</c> до следующего <see cref="MoveIn"/>).</summary>
    public void MoveOut(CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        int home = res.Home[r];
        if (home < 0)
        {
            return;
        }

        Parking.HomeAssign.Release(c, r);   // домашнее место и машина уходят с жителем (parking.md §4); без режима - ничего
        Ensure(c);
        BuildingStore b = c.Buildings;
        int before = FreeOf(c, home);
        if (b.HeadRes[home] == r)
        {
            b.HeadRes[home] = res.NextHome[r];
        }
        else
        {
            int prev = b.HeadRes[home];
            while (prev >= 0 && res.NextHome[prev] != r)
            {
                prev = res.NextHome[prev];
            }

            if (prev >= 0)
            {
                res.NextHome[prev] = res.NextHome[r];
            }
        }

        res.NextHome[r] = -1;
        res.Home[r] = -1;
        b.Occupants[home]--;
        AdjustFree(c, home, before);
    }

    // ------------------------------------------------------------------ стоимости

    private static int AscOf(in CityChoiceDesc ch, int m) => m == (int)TravelMode.Car ? ch.AscCarDs : (m == (int)TravelMode.Bus ? ch.AscBusDs : ch.AscWalkDs);

    /// <summary>Лучшая стоимость ноги между ячейками (среднее по периодам суток, плюс ASC), дс; <see cref="NoCost"/> - ничем нельзя. Кэш по суткам для строки (откуда, есть ли машина).</summary>
    private int CellCost(CityWorld c, bool hasCar, int o, int d)
    {
        if (_rowCost == null || _rowDay == null)
        {
            return CellCostRaw(c, hasCar, o, d);
        }

        int row = (o * 2) + (hasCar ? 1 : 0);
        int baseIdx = row * _cells;
        if (_rowDay[row] != c.Day)
        {
            for (int k = 0; k < _cells; k++)
            {
                _rowCost[baseIdx + k] = CellCostRaw(c, hasCar, o, k);
            }

            _rowDay[row] = c.Day;
        }

        return _rowCost[baseIdx + d];
    }

    private static int CellCostRaw(CityWorld c, bool hasCar, int o, int d)
    {
        Skims s = c.Skims;
        CityChoiceDesc ch = c.Cfg.Choice;
        int best = NoCost;
        for (int m = 0; m < Skims.Modes; m++)
        {
            if (m == (int)TravelMode.Car && !hasCar)
            {
                continue;
            }

            int v = Accessibility.AvgCost(s, o, d, (TravelMode)m);
            if (v > 0)
            {
                best = Math.Min(best, v + AscOf(in ch, m));
            }
        }

        return best;
    }

    /// <summary>Стоимость ноги между участками в период <paramref name="period"/> (лучший способ жителя, плюс ASC), дс.</summary>
    private static int LotCost(CityWorld c, bool hasCar, int lotA, int lotB, int period)
    {
        CityChoiceDesc ch = c.Cfg.Choice;
        int best = NoCost;
        for (int m = 0; m < Skims.Modes; m++)
        {
            if (m == (int)TravelMode.Car && !hasCar)
            {
                continue;
            }

            int v = Choice.LegCost(c, c.Skims, hasCar, lotA, lotB, (TravelMode)m, period);
            if (v != NoCost)
            {
                best = Math.Min(best, v + AscOf(in ch, m));
            }
        }

        return best;
    }

    private static bool HasCar(CityWorld c, int r) => (c.Residents.Flags[r] & ResidentFlags.HasCar) != 0;

    // ------------------------------------------------------------------ подбор фирмы

    /// <summary>
    /// Фирма с вакансией для жителя <paramref name="r"/> (§8.1): K ячеек-кандидатов по весу <c>вакансии · w(C_commute)</c>, внутри ячейки первая достижимая фирма с вакансией (сдвиг по <c>Seq</c>),
    /// логит между кандидатами по стоимости ноги на работу. <paramref name="exclude"/> - фирма, которую не предлагать (текущая при смене работы). -1 - подходящей нет.
    /// </summary>
    public int PickFirm(World w, CityWorld c, int r, int exclude, out int cost)
    {
        cost = NoCost;
        Ensure(c);
        if (TotalVac <= 0)
        {
            return -1;
        }

        c.Skims.Prepare(w);
        CityChoiceDesc ch = c.Cfg.Choice;
        CityResidents res = c.Residents;
        BuildingStore b = c.Buildings;
        bool hasCar = HasCar(c, r);
        int homeLot = b.Lot[res.Home[r]];
        int o = c.Lots.Cell[homeLot];
        int cmin = NoCost;
        for (int d = 0; d < _cells; d++)
        {
            int v = CellVac[d] > 0 ? CellCost(c, hasCar, o, d) : NoCost;
            _cost[d] = v;
            cmin = Math.Min(cmin, v);
        }

        if (cmin == NoCost)
        {
            return -1;
        }

        for (int d = 0; d < _cells; d++)
        {
            _weight[d] = _cost[d] == NoCost ? 0L : (long)CellVac[d] * (DetLogit.Weight((long)_cost[d] - cmin, ch.ThetaDest64) >> 14);
        }

        Span<int> candF = stackalloc int[Candidates];
        Span<int> candC = stackalloc int[Candidates];
        for (int k = 0; k < Candidates; k++)
        {
            candF[k] = -1;
            candC[k] = NoCost;
            int cell = DetLogit.ChooseWeighted(ref w.RngDemand, _weight);
            if (cell < 0)
            {
                break;
            }

            int a = _firmStart[cell];
            int n = _firmStart[cell + 1] - a;
            int offset = (int)(((long)res.Seq[r] * 13 + (k * 7)) % Math.Max(1, n));
            for (int j = 0; j < n; j++)
            {
                int f = _firmList[a + ((offset + j) % n)];
                if (f == exclude || b.Jobs[f] - b.Occupants[f] <= 0)
                {
                    continue;
                }

                int period = c.PeriodOf(c.TickOfSlot(0, c.Types.Rows[b.Type[f]].ShiftStartSlot));
                int v = LotCost(c, hasCar, homeLot, b.Lot[f], period);
                if (v == NoCost)
                {
                    continue;
                }

                candF[k] = f;
                candC[k] = v;
                break;
            }
        }

        int pick = DetLogit.Choose(ref w.RngDemand, candC, ch.ThetaDest64);
        if (pick < 0)
        {
            return -1;
        }

        cost = candC[pick];
        return candF[pick];
    }

    /// <summary>Шаг рынка труда за тик: до <c>match_per_tick</c> ищущих из головы очереди; не нашедшие встают в хвост.</summary>
    public void MatchStep(World w, CityWorld c)
    {
        CityResidents res = c.Residents;
        if (res.SeekQueue.IsEmpty)
        {
            return;
        }

        Ensure(c);
        if (TotalVac <= 0)
        {
            return;
        }

        int n = Math.Min(c.Cfg.Residents.MatchPerTick, res.SeekQueue.Count);
        for (int k = 0; k < n; k++)
        {
            if (!res.SeekQueue.TryDequeue(out SeekEntry e))
            {
                break;
            }

            int r = e.Index;
            if (!res.Pool.IsAlive(r, e.Generation))
            {
                continue;   // житель уехал, слот мог быть занят другим: запись устарела
            }

            res.Flags[r] = (byte)(res.Flags[r] & ~ResidentFlags.InSeekQueue);
            if (res.Work[r] >= 0 || (res.Flags[r] & ResidentFlags.Labour) == 0)
            {
                continue;
            }

            int firm = PickFirm(w, c, r, -1, out _);
            if (firm >= 0)
            {
                Employ(w, c, r, firm);
            }
            else
            {
                Enqueue(c, r);
            }
        }
    }

    // ------------------------------------------------------------------ подбор дома

    /// <summary>
    /// Дом для жителя (§8.2): K мест-кандидатов по весу <c>свободно · w(C_commute от кандидата)</c> (до работы жителя, если есть), логит по <c>2 · C_commute + logsum C</c>;
    /// текущий дом входит в варианты, если <paramref name="includeCurrent"/>. Возвращает здание (может быть текущим) или -1, если вариантов нет.
    /// </summary>
    private int PickHome(World w, CityWorld c, int r, bool includeCurrent)
    {
        Ensure(c);
        if (TotalFree <= 0 && !includeCurrent)
        {
            return -1;
        }

        c.Skims.Prepare(w);
        CityChoiceDesc ch = c.Cfg.Choice;
        CityResidents res = c.Residents;
        BuildingStore b = c.Buildings;
        bool hasCar = HasCar(c, r);
        int workCell = res.Work[r] >= 0 ? c.Lots.Cell[b.Lot[res.Work[r]]] : -1;
        int cmin = NoCost;
        for (int d = 0; d < _cells; d++)
        {
            int v = 0;
            if (CellFree[d] <= 0)
            {
                v = NoCost;
            }
            else if (workCell >= 0)
            {
                v = CellCost(c, hasCar, d, workCell);
            }

            _cost[d] = v;
            cmin = Math.Min(cmin, v);
        }

        Span<int> candB = stackalloc int[Candidates + 1];
        Span<int> candC = stackalloc int[Candidates + 1];
        candB.Fill(-1);
        candC.Fill(NoCost);
        if (cmin != NoCost)
        {
            for (int d = 0; d < _cells; d++)
            {
                _weight[d] = _cost[d] == NoCost ? 0L : (long)CellFree[d] * (DetLogit.Weight((long)_cost[d] - cmin, ch.ThetaDest64) >> 14);
            }

            for (int k = 0; k < Candidates; k++)
            {
                int cell = DetLogit.ChooseWeighted(ref w.RngDemand, _weight);
                if (cell < 0)
                {
                    break;
                }

                int a = _resStart[cell];
                int n = _resStart[cell + 1] - a;
                int offset = (int)(((long)res.Seq[r] * 13 + (k * 7)) % Math.Max(1, n));
                for (int j = 0; j < n; j++)
                {
                    int cand = _resList[a + ((offset + j) % n)];
                    if (cand == res.Home[r] || FreeOf(c, cand) <= 0)
                    {
                        continue;
                    }

                    int cost = HomeCost(c, hasCar, cell, workCell);
                    if (cost != NoCost)
                    {
                        candB[k] = cand;
                        candC[k] = cost;
                    }

                    break;
                }
            }
        }

        if (includeCurrent && res.Home[r] >= 0)
        {
            int cell = c.Lots.Cell[b.Lot[res.Home[r]]];
            candB[Candidates] = res.Home[r];
            candC[Candidates] = HomeCost(c, hasCar, cell, workCell);
        }

        int pick = DetLogit.Choose(ref w.RngDemand, candC, ch.ThetaDest64);
        return pick < 0 ? -1 : candB[pick];
    }

    /// <summary>Стоимость места жительства: туда-обратно на работу плюс logsum доступности C из ячейки; <see cref="NoCost"/>, если работа недостижима.</summary>
    private int HomeCost(CityWorld c, bool hasCar, int cell, int workCell)
    {
        int commute = 0;
        if (workCell >= 0)
        {
            int v = CellCost(c, hasCar, cell, workCell);
            if (v == NoCost)
            {
                return NoCost;
            }

            commute = 2 * v;
        }

        int ls = Accessibility.Combined(c.Skims, cell, hasCar, c.Cfg.Choice.ThetaMode64);
        return ls == Skims.NoLs ? commute : commute + ls;
    }

    /// <summary>
    /// Переезд жителя, который сейчас дома (§8.2): выбранный дом отличается от текущего - житель переезжает мгновенно. true - переехал.
    /// <paramref name="evicted"/> - дом заброшен, текущий в варианты не входит.
    /// </summary>
    public bool TryRelocate(World w, CityWorld c, int r, bool evicted)
    {
        int to = PickHome(w, c, r, !evicted);
        if (to < 0 || to == c.Residents.Home[r])
        {
            return false;
        }

        MoveOut(c, r);
        MoveIn(c, r, to);
        Parking.HomeAssign.OnMoveIn(w, c, r);   // домашнее место на новом доме; нет места - машина не переезжает (parking.md §4)
        w.Counters.ResRelocations++;
        return true;
    }

    // ------------------------------------------------------------------ въезд и отъезд

    /// <summary>Отъезд жителя, который сейчас дома: из списков, баланс на <c>External</c>, handle свободен.</summary>
    public void Emigrate(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        Unemploy(c, r);
        MoveOut(c, r);
        long balance = res.Account[r];
        c.Ledger.Transfer(Ledger.AccountOfResident(r), c.Ledger.ExternalAccount, balance, FlowKind.Emigration);
        CityPhase.SetEvent(w, c, r, -1, ResidentEvent.None);
        res.Destroy(r);
        w.Counters.Emigrants++;
    }

    /// <summary>
    /// Въезд до <paramref name="count"/> жителей в свободное жильё по баллу доступности (<c>LsJobs</c> ячейки): ячейка по весу <c>свободно · w</c>, внутри - первое здание со свободным местом.
    /// Каждый получает <c>initial_resident_balance</c> от <c>External</c>, план дня на ближайший <c>plan_slot</c>, рабочие встают в очередь ищущих. Возвращает число въехавших.
    /// </summary>
    public int Immigrate(World w, CityWorld c, int count)
    {
        Ensure(c);
        CityChoiceDesc ch = c.Cfg.Choice;
        CityResidentsDesc rd = c.Cfg.Residents;
        CityResidents res = c.Residents;
        int done = 0;
        for (int i = 0; i < count && TotalFree > 0; i++)
        {
            long total = 0;
            int lsMin = int.MaxValue;
            for (int d = 0; d < _cells; d++)
            {
                int ls = c.Skims.LsJobs[d];
                _cost[d] = CellFree[d] > 0 && ls != Skims.NoLs ? ls : NoCost;
                lsMin = Math.Min(lsMin, _cost[d]);
            }

            for (int d = 0; d < _cells; d++)
            {
                if (CellFree[d] <= 0)
                {
                    _weight[d] = 0;
                }
                else if (lsMin == int.MaxValue)
                {
                    _weight[d] = CellFree[d];   // доступность нигде не определена: по свободному жилью
                }
                else
                {
                    _weight[d] = _cost[d] == NoCost ? 0L : (long)CellFree[d] * (DetLogit.Weight((long)_cost[d] - lsMin, ch.ThetaDest64) >> 14);
                }

                total += _weight[d];
            }

            int cell = total > 0 ? DetLogit.ChooseWeighted(ref w.RngDemand, _weight) : -1;
            if (cell < 0)
            {
                break;
            }

            int home = -1;
            int a = _resStart[cell];
            int n = _resStart[cell + 1] - a;
            int offset = (int)(res.NextSeq % Math.Max(1, n));
            for (int j = 0; j < n; j++)
            {
                int cand = _resList[a + ((offset + j) % n)];
                if (FreeOf(c, cand) > 0)
                {
                    home = cand;
                    break;
                }
            }

            if (home < 0 || !res.TryCreate(out int r))
            {
                break;
            }

            MoveIn(c, r, home);
            byte flags = 0;
            if ((w.RngDemand.NextUInt() >> 16) < (uint)c.Cfg.Residents.CarOwnershipQ16)
            {
                flags |= ResidentFlags.HasCar;
            }

            if ((w.RngDemand.NextUInt() >> 16) < (uint)c.Cfg.Residents.LabourShareQ16)
            {
                flags |= ResidentFlags.Labour;
            }

            res.Flags[r] = flags;
            Parking.HomeAssign.OnMoveIn(w, c, r);   // домашнее место; нет места - HasCar снимается (parking.md §4)
            res.State[r] = (byte)ResidentState.AtHome;
            res.StateTick[r] = w.Tick;
            c.Ledger.Transfer(c.Ledger.ExternalAccount, Ledger.AccountOfResident(r), c.Cfg.Economy.InitialResidentBalance, FlowKind.Endowment);
            int slot = rd.PlanSlot + (res.Seq[r] % 13);
            long when = c.TickOfSlot(Calendar.DayOf(c, w.Tick), slot);
            CityPhase.SetEvent(w, c, r, when > w.Tick ? when : Calendar.NextPlanTick(c, r, w.Tick), ResidentEvent.Plan);
            StartSeeking(c, r);
            w.Counters.Immigrants++;
            done++;
        }

        return done;
    }

    // ------------------------------------------------------------------ дневной проход жителя

    /// <summary>
    /// Дневной проход по жителю (§3.3, §8): счётчики дней поиска и выселения и их дедлайны (<c>Leaving</c> -> отъезд из дома), переезд выселенных, добровольный переезд и смена работы.
    /// <c>res_stuck</c> растёт, если дедлайн просрочен больше, чем на двое суток (житель за сутки обязан побывать дома).
    /// </summary>
    public void ResidentDay(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        CityResidentsDesc rd = c.Cfg.Residents;
        int shift = c.Cfg.Economy.EwmaShift;
        bool home = res.State[r] == (byte)ResidentState.AtHome;
        if (res.Work[r] < 0)
        {
            res.IncomeEwma[r] -= res.IncomeEwma[r] >> shift;
        }

        if ((res.Flags[r] & ResidentFlags.Labour) != 0 && res.Work[r] < 0)
        {
            if (res.SeekDays[r] < short.MaxValue)
            {
                res.SeekDays[r]++;
            }

            if (res.SeekDays[r] > rd.SeekMaxDays)
            {
                res.Flags[r] |= ResidentFlags.Leaving;
            }

            Enqueue(c, r);
        }

        if ((res.Flags[r] & ResidentFlags.Evicted) != 0)
        {
            if (res.EvictDays[r] < short.MaxValue)
            {
                res.EvictDays[r]++;
            }

            if (home && TryRelocate(w, c, r, true))
            {
                res.Flags[r] = (byte)(res.Flags[r] & ~ResidentFlags.Evicted);
                res.EvictDays[r] = 0;
            }
            else if (res.EvictDays[r] > rd.EvictMaxDays)
            {
                res.Flags[r] |= ResidentFlags.Leaving;
            }
        }

        if ((res.Flags[r] & ResidentFlags.Leaving) != 0)
        {
            bool seekOver = res.Work[r] < 0 && res.SeekDays[r] > rd.SeekMaxDays + 2;
            bool evictOver = (res.Flags[r] & ResidentFlags.Evicted) != 0 && res.EvictDays[r] > rd.EvictMaxDays + 2;
            if (seekOver || evictOver)
            {
                w.Counters.ResStuck++;   // дедлайн просрочен: житель не бывает дома (норма 0)
            }

            if (home)
            {
                Emigrate(w, c, r);
            }

            return;
        }

        if (!home || res.Home[r] < 0)
        {
            return;
        }

        if (res.Work[r] >= 0 && rd.MaxJobSwitchPerDayQ16 > 0)
        {
            TrySwitchJob(w, c, r);
        }

        if (rd.MaxRelocatePerDayQ16 > 0 && (w.RngDemand.NextUInt() >> 16) < (uint)rd.MaxRelocatePerDayQ16)
        {
            TryRelocate(w, c, r, false);
        }
    }

    /// <summary>Добровольная смена работы (§8.1): дорога на работу дороже <c>switch_ratio · LS_jobs(дом)</c>, жребий <c>max_jobswitch_per_day</c>, другая фирма с вакансией дешевле.</summary>
    private void TrySwitchJob(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        BuildingStore b = c.Buildings;
        Skims s = c.Skims;
        s.Prepare(w);
        bool hasCar = HasCar(c, r);
        int homeLot = b.Lot[res.Home[r]];
        int ls = s.LsJobs[c.Lots.Cell[homeLot]];
        if (ls == Skims.NoLs)
        {
            return;
        }

        int firm = res.Work[r];
        int period = c.PeriodOf(c.TickOfSlot(0, c.Types.Rows[b.Type[firm]].ShiftStartSlot));
        int cur = LotCost(c, hasCar, homeLot, b.Lot[firm], period);
        if (cur == NoCost || (long)cur * 256 <= (long)c.Cfg.Residents.SwitchRatioQ8 * Math.Max(ls, 1))
        {
            return;
        }

        if ((w.RngDemand.NextUInt() >> 16) >= (uint)c.Cfg.Residents.MaxJobSwitchPerDayQ16)
        {
            return;
        }

        int to = PickFirm(w, c, r, firm, out int cost);
        if (to < 0 || cost >= cur)
        {
            return;
        }

        Unemploy(c, r);
        Employ(w, c, r, to);
        w.Counters.ResJobSwitches++;
    }
}
