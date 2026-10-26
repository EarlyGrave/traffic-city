using System;
using Sim.Core.Api;

namespace Sim.Core.Demand;

/// <summary>
/// Скимы (zoning-demand.md §5): EWMA обобщённой стоимости по паре LOD-ячеек, способу и периоду суток. Z1 - данные, финальные индексы и сигнатуры
/// <see cref="Observe"/>/<see cref="Get"/> (Observe копит наблюдение в текущий накопитель; обновление скима порциями, двойной буфер периода и априорная оценка - Z3).
/// Индекс записи <c>((o * NC + d) * M + m) * P + p</c> (<see cref="KeyOf"/>), 0 в <see cref="Skim"/> - «нет данных».
/// </summary>
internal sealed class Skims
{
    public const int Modes = 3;

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

    /// <summary>Ским пары (§5.3). Z1: сохранённое значение или 0 («нет данных»); априорная оценка пары без данных (§5.4) - Z3.</summary>
    public int Get(int o, int d, TravelMode mode, int period) => Skim[KeyOf(o, d, mode, period)];
}
