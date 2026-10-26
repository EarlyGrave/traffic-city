using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;

namespace Sim.Core.Demand;

/// <summary>
/// Скимы (zoning-demand.md §5): EWMA обобщённой стоимости по паре LOD-ячеек, способу и периоду суток.
/// Индекс записи <c>((o * NC + d) * M + m) * P + p</c> (<see cref="KeyOf"/>), 0 в <see cref="Skim"/> - «нет данных».
/// <para>
/// Жизненный цикл: <see cref="Observe"/> копит наблюдения завершённых ног в <see cref="Current"/>; <see cref="Advance"/> (фаза 9r, каждый тик) на границе периода замораживает накопитель
/// в отсортированный слепок (<c>Frozen*</c>, двойной буфер) и порциями по <c>skim_update_per_tick</c> применяет его к <see cref="Skim"/> (<c>S += (mean - S) &gt;&gt; skim_shift</c>).
/// <see cref="Get"/> отдаёт сохранённое значение, а для пары без данных - априорную оценку (§5.4) из производных по ячейкам (<see cref="Prepare"/>).
/// </para>
/// <para>
/// Производное (вне сейва, строится <see cref="Prepare"/> лениво и по версии сети/зданий): свободная скорость, остановки и интервалы по ячейкам, суммы мест C/работ по ячейкам и список
/// активных C-зданий по ячейкам (CSR). Состояние Z3: <see cref="LsC"/>, <see cref="LsJobs"/> (поле доступности, §6.7) и <see cref="LsRef"/>.
/// </para>
/// </summary>
internal sealed class Skims
{
    public const int Modes = 3;

    /// <summary>Значение «нет доступа» в <see cref="LsC"/> и <see cref="LsJobs"/>.</summary>
    public const int NoLs = int.MaxValue;

    // Константы априорной оценки (§5.4); единицы - сантиметры, см/с, децисекунды.
    private const int DetourQ4 = 13;              // коэффициент 1.3 в десятых: секунды = DetourQ4 * см / (10 * см/с), децисекунды = DetourQ4 * см / (см/с)
    private const int DefaultVFreeCms = 1389;     // 50 км/ч, если в ячейке нет улиц
    private const int CarOverheadDs = 600;        // 60 с на парковку и выезд
    private const int WalkCms = 130;              // 1.3 м/с
    private const int BusCms = 800;               // 8 м/с
    private const int BusWalkCm = 25_000;         // пешком до остановки и от неё: по 250 м
    private const int DefaultHeadwayDs = 6000;    // интервал без данных: 10 мин

    public readonly int Cells;               // NC
    public readonly int Periods;             // P = skim_periods
    public readonly int[] Skim;              // [NC * NC * M * P] децисекунды
    public readonly int[] K;                 // [NC * NC * P] поправка агрегата Q8 (0 - нет; Z5)
    public readonly ObsTable Current;        // накопитель текущего периода
    public readonly int[] FrozenKey;         // накопитель прошлого периода: отсортированный слепок (двойной буфер)
    public readonly long[] FrozenSum;
    public readonly int[] FrozenCount;
    public int FrozenN;
    public int UpdateCursor;                 // сколько записей слепка уже применено к Skim
    public long CurPeriod;                   // абсолютный номер периода, в который копится Current
    public int LsRef;                        // LS_ref: среднее logsum до C-целей по городу на день 0 (§6.3)
    public bool LsRefSet;
    public readonly int[] LsC;               // [NC * M] logsum до C-целей из ячейки по способу, дс (§6.7); NoLs - целей нет
    public readonly int[] LsJobs;            // [NC] logsum до рабочих мест из ячейки по всем способам, дс; NoLs - мест нет

    // ---- производное (вне сейва и хэша) ----
    internal int[]? VFreeCms;                // [NC] средний лимит скорости улиц ячейки, см/с (0 - улиц нет)
    internal int[]? StopCount;               // [NC] остановок автобуса в ячейке
    internal int[]? HeadwayDs;               // [NC] наименьший интервал маршрутов остановок ячейки, дс (0 - нет)
    internal int[]? CellShopCap;             // [NC] Σ ShopCapacity активных C-зданий (насыщение int)
    internal int[]? CellJobCap;              // [NC] Σ Capacity активных C/I-зданий
    internal int[]? CellResCount;            // [NC] активных жилых зданий
    internal int[]? CellStart;               // [NC + 1] CSR активных C-зданий по ячейкам
    internal int[]? CellList;                // здания (индексы по возрастанию внутри ячейки)
    internal int[]? ScratchInt;              // [NC * M] рабочие массивы (один поток)
    internal long[]? ScratchLong;            // [NC]
    private int _netVersion = -1;
    private int _stopSig = -1;
    private int _buildVersion = -1;
    private int _wWaitQ8;
    private int _wWalkQ8;
    private int _cellsX;
    private int _cellCm;
    private int _maxWalkCm2;                 // квадрат max_walk, см²

    public Skims(int cells, int periods, int obsCapacity)
    {
        Cells = cells;
        Periods = periods;
        Skim = new int[checked(cells * cells * Modes * periods)];
        K = new int[checked(cells * cells * periods)];
        Current = new ObsTable(obsCapacity);
        FrozenKey = new int[obsCapacity];
        FrozenSum = new long[obsCapacity];
        FrozenCount = new int[obsCapacity];
        LsC = new int[checked(cells * Modes)];
        LsJobs = new int[cells];
        Array.Fill(LsC, NoLs);
        Array.Fill(LsJobs, NoLs);
    }

    /// <summary>Предел числа записей скима (память; s1m-res до двухуровневого скима этапа 4.1 - <c>NotSupported</c>).</summary>
    public const long MaxSkimEntries = 1L << 27;

    public static long EntriesFor(int cells, int periods) => (long)cells * cells * Modes * periods;

    public int KeyOf(int o, int d, TravelMode m, int period) => ((((o * Cells) + d) * Modes) + (int)m) * Periods + period;

    /// <summary>Период суток тика: <c>tickOfDay * P / dayTicks</c>.</summary>
    public int PeriodOf(long tick, int dayTicks) => (int)(((tick % dayTicks) * Periods) / dayTicks);

    /// <summary>Абсолютный номер периода тика (для двойного буфера): <c>tick * P / dayTicks</c>.</summary>
    public long AbsolutePeriodOf(long tick, int dayTicks) => (tick * Periods) / dayTicks;

    /// <summary>
    /// Наблюдение завершённой ноги (§5.2): стоимость <paramref name="costDs"/> в децисекундах по ключу <c>(o, d, mode, period)</c>, где период - тика заявки.
    /// false - накопитель полон, наблюдение отброшено (вызывающий считает <c>SkimObsDropped</c>).
    /// </summary>
    public bool Observe(int o, int d, TravelMode mode, int period, int costDs) => Current.Add(KeyOf(o, d, mode, period), costDs);

    /// <summary>
    /// Ским пары (§5.3-5.4): сохранённое значение, а без данных - априорная оценка по свободному потоку (после <see cref="Prepare"/>); 0 - способ для пары недоступен
    /// (машина без улиц, автобус без остановок на обоих концах). Внутриячеечную поправку по фактическому расстоянию добавляет <c>Choice</c>.
    /// </summary>
    public int Get(int o, int d, TravelMode mode, int period)
    {
        int s = Skim[KeyOf(o, d, mode, period)];
        return s != 0 ? s : Prior(o, d, mode);
    }

    // ---------------------------------------------------------------- обновление (§5.3)

    /// <summary>
    /// Шаг фазы 9r: граница периода - заморозка накопителя (недоприменённый слепок доприменяется целиком), затем порция обновления. Возвращает число обновлённых записей.
    /// Ноль байт на тик в стационаре: слепок и накопитель - предвыделенные массивы.
    /// </summary>
    public int Advance(World w)
    {
        CityWorld c = w.City!;
        int updated = 0;
        long ap = AbsolutePeriodOf(w.Tick, c.DayTicks);
        if (ap != CurPeriod)
        {
            while (UpdateCursor < FrozenN)
            {
                ApplyOne(c.Cfg.Choice.SkimShift);
                updated++;
            }

            FrozenN = Current.CopySorted(FrozenKey, FrozenSum, FrozenCount);
            UpdateCursor = 0;
            Current.Clear();
            CurPeriod = ap;
        }

        for (int budget = c.Cfg.Choice.SkimUpdatePerTick; budget > 0 && UpdateCursor < FrozenN; budget--)
        {
            ApplyOne(c.Cfg.Choice.SkimShift);
            updated++;
        }

        w.Counters.SkimUpdates += updated;
        return updated;
    }

    private void ApplyOne(int shift)
    {
        int i = UpdateCursor++;
        int key = FrozenKey[i];
        long m = FrozenSum[i] / FrozenCount[i];
        int mean = m < 1 ? 1 : (m > int.MaxValue ? int.MaxValue : (int)m);
        int s = Skim[key];
        Skim[key] = s == 0 ? mean : s + ((mean - s) >> shift);
    }

    // ---------------------------------------------------------------- производные

    /// <summary>
    /// Готовит производные (идемпотентно): априорные поля ячеек пересчитываются при смене версии сети/остановок, индекс зданий по ячейкам - при смене версии зданий.
    /// Первый вызов выделяет рабочие массивы (единожды); дальше аллокаций нет.
    /// </summary>
    public void Prepare(World w)
    {
        CityWorld c = w.City!;
        if (ScratchInt == null)
        {
            VFreeCms = new int[Cells];
            StopCount = new int[Cells];
            HeadwayDs = new int[Cells];
            CellShopCap = new int[Cells];
            CellJobCap = new int[Cells];
            CellResCount = new int[Cells];
            CellStart = new int[Cells + 1];
            CellList = new int[c.Buildings.Capacity];
            ScratchInt = new int[Cells * Modes];
            ScratchLong = new long[Cells];
        }

        CityChoiceDesc ch = c.Cfg.Choice;
        _wWaitQ8 = ch.WWaitQ8;
        _wWalkQ8 = ch.WWalkQ8;
        _cellsX = c.Grid.CellsX;
        _cellCm = (int)(c.Grid.CellM * 100f);
        long mw = ch.MaxWalkMm / 10;
        _maxWalkCm2 = (int)Math.Min(int.MaxValue, mw * mw);

        NetworkStore net = w.Net;
        int sig = net.BusStopCount + (net.BusRouteCount * 4099);
        if (_netVersion != net.Version || _stopSig != sig)
        {
            RebuildPrior(w, c);
            _netVersion = net.Version;
            _stopSig = sig;
        }

        if (_buildVersion != c.Buildings.Version)
        {
            RebuildCellIndex(c);
            _buildVersion = c.Buildings.Version;
        }
    }

    private static int SegmentCell(NetworkStore net, CityGrid g, int seg)
    {
        ref readonly SegmentData s = ref net.Segments[seg];
        Util.Vec2 a = net.Nodes[s.NodeA].Position;
        Util.Vec2 b = net.Nodes[s.NodeB].Position;
        return g.CellOf((a.X + b.X) * 0.5f, (a.Z + b.Z) * 0.5f);
    }

    private void RebuildPrior(World w, CityWorld c)
    {
        NetworkStore net = w.Net;
        CityGrid g = c.Grid;
        int[] vf = VFreeCms!;
        int[] stops = StopCount!;
        int[] head = HeadwayDs!;
        Array.Clear(vf);
        Array.Clear(stops);
        Array.Clear(head);
        Span<int> cnt = ScratchInt.AsSpan(0, Cells);
        cnt.Clear();
        for (int s = 0; s < net.SegmentCount; s++)
        {
            ref readonly SegmentData sd = ref net.Segments[s];
            if (sd.LanesAB + sd.LanesBA == 0 || !(sd.SpeedLimit > 0f))
            {
                continue;
            }

            int cell = SegmentCell(net, g, s);
            vf[cell] += (int)(sd.SpeedLimit * 100f);
            cnt[cell]++;
        }

        for (int i = 0; i < Cells; i++)
        {
            vf[i] = cnt[i] > 0 ? vf[i] / cnt[i] : 0;
        }

        for (int i = 0; i < net.BusStopCount; i++)
        {
            ref readonly BusStopData bs = ref net.BusStops[i];
            if ((uint)bs.Lane >= (uint)net.LaneCount)
            {
                continue;
            }

            int seg = net.Lanes[bs.Lane].Segment;
            if ((uint)seg >= (uint)net.SegmentCount)
            {
                continue;
            }

            int cell = SegmentCell(net, g, seg);
            stops[cell]++;
            if ((uint)bs.Route < (uint)net.BusRouteCount)
            {
                int h = net.BusRoutes[bs.Route].HeadwayTicks * 10 / Simulation.TicksPerSecond;
                if (h > 0 && (head[cell] == 0 || h < head[cell]))
                {
                    head[cell] = h;
                }
            }
        }
    }

    private void RebuildCellIndex(CityWorld c)
    {
        BuildingStore b = c.Buildings;
        LotStore l = c.Lots;
        int[] shop = CellShopCap!;
        int[] job = CellJobCap!;
        int[] res = CellResCount!;
        int[] start = CellStart!;
        Array.Clear(shop);
        Array.Clear(job);
        Array.Clear(res);
        Array.Clear(start);
        int hw = b.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || b.Lot[i] < 0)
            {
                continue;
            }

            int cell = l.Cell[b.Lot[i]];
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[i]];
            switch (t.Kind)
            {
                case LotZone.Residential:
                    res[cell]++;
                    break;
                case LotZone.Commercial:
                    shop[cell] = (int)Math.Min(int.MaxValue, (long)shop[cell] + t.ShopCapacity);
                    job[cell] = (int)Math.Min(int.MaxValue, (long)job[cell] + t.Capacity);
                    start[cell + 1]++;
                    break;
                default:
                    job[cell] = (int)Math.Min(int.MaxValue, (long)job[cell] + t.Capacity);
                    break;
            }
        }

        for (int i = 0; i < Cells; i++)
        {
            start[i + 1] += start[i];
        }

        // Заполнение: позиции хранятся во временном CellNext-подобном проходе через ScratchInt (счётчики вставки).
        Span<int> fill = ScratchInt.AsSpan(0, Cells);
        fill.Clear();
        int[] list = CellList!;
        for (int i = 0; i < hw; i++)
        {
            if (!b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || b.Lot[i] < 0 || c.Types.Rows[b.Type[i]].Kind != LotZone.Commercial)
            {
                continue;
            }

            int cell = l.Cell[b.Lot[i]];
            list[start[cell] + fill[cell]++] = i;
        }
    }

    // ---------------------------------------------------------------- априорная оценка (§5.4)

    private int CellDistCm(int o, int d)
    {
        if (o == d)
        {
            return _cellCm / 3;
        }

        int dx = Math.Abs((o % _cellsX) - (d % _cellsX));
        int dz = Math.Abs((o / _cellsX) - (d / _cellsX));
        return (dx + dz) * _cellCm;
    }

    /// <summary>Квадрат расстояния между центрами ячеек, см² (long), для проверки пешей доступности без корня.</summary>
    internal long CellDist2Cm(int o, int d)
    {
        long dx = (long)((o % _cellsX) - (d % _cellsX)) * _cellCm;
        long dz = (long)((o / _cellsX) - (d / _cellsX)) * _cellCm;
        return (dx * dx) + (dz * dz);
    }

    /// <summary>Пешком по центрам ячеек не дальше <c>max_walk</c> (внутри ячейки - всегда).</summary>
    internal bool WalkReachable(int o, int d) => o == d || CellDist2Cm(o, d) <= _maxWalkCm2;

    /// <summary>Априорная стоимость пары (без ASC), дс; 0 - способ недоступен. До <see cref="Prepare"/> - 0.</summary>
    internal int Prior(int o, int d, TravelMode mode)
    {
        if (VFreeCms == null)
        {
            return 0;
        }

        int dist = CellDistCm(o, d);
        switch (mode)
        {
            case TravelMode.Car:
                {
                    int vo = VFreeCms[o];
                    int vd = VFreeCms[d];
                    if (vo == 0 || vd == 0)
                    {
                        return 0;
                    }

                    int v = (vo + vd) / 2;
                    return Math.Max(1, (int)(((long)DetourQ4 * dist / Math.Max(1, v)) + CarOverheadDs));
                }

            case TravelMode.Walk:
                return Math.Max(1, (int)((long)DetourQ4 * dist / WalkCms));
            default:
                {
                    if (o == d || StopCount![o] == 0 || StopCount[d] == 0)
                    {
                        return 0;
                    }

                    long walk = (long)DetourQ4 * BusWalkCm * 2 / WalkCms;
                    int hw = Math.Max(HeadwayDs![o], HeadwayDs[d]);
                    long wait = (hw == 0 ? DefaultHeadwayDs : hw) / 2;
                    long ride = (long)DetourQ4 * dist / BusCms;
                    return Math.Max(1, (int)(((walk * _wWalkQ8) >> 8) + ((wait * _wWaitQ8) >> 8) + ride));
                }
        }
    }

    /// <summary>Скорость способа внутри ячейки, см/с, для поправки по фактическому расстоянию (автобус - 0: поправки нет).</summary>
    internal int ModeSpeedCms(TravelMode mode, int cell) => mode switch
    {
        TravelMode.Car => VFreeCms != null && VFreeCms[cell] > 0 ? VFreeCms[cell] : DefaultVFreeCms,
        TravelMode.Walk => WalkCms,
        _ => 0,
    };

    /// <summary>Длина внутриячеечного плеча по умолчанию, см (<c>cell_m / 3</c>).</summary>
    internal int IntraCellCm => _cellCm / 3;

    /// <summary>Множитель извилистости 1.3 в виде «13 / см·с», для поправки.</summary>
    internal static int DetourFactorQ4 => DetourQ4;
}
