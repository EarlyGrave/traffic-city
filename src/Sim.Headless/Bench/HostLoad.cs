using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sim.Headless.Bench;

/// <summary>
/// Чужая нагрузка на хост за окно замера (Linux, <c>/proc</c>): в общем облачном контейнере пики тика и шум p95 в основном от соседей (N3, lessons.md).
/// Занятые джиффи всей системы минус джиффи этого процесса = чужая работа; делённое на длительность окна - сколько ядер в среднем занято чужим.
/// На других ОС и при ошибке чтения все значения null. Ядро симуляции не затрагивает.
/// </summary>
internal readonly struct HostLoad
{
    private const double JiffyS = 0.01;

    private readonly long _busy, _steal, _self;
    private readonly long _ticks;

    private HostLoad(long busy, long steal, long self, long timestamp)
    {
        _busy = busy;
        _steal = steal;
        _self = self;
        _ticks = timestamp;
    }

    public bool Valid => _ticks != 0;

    public static HostLoad Read()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return default;
            }

            string[] f = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long user = Num(f[1]), nice = Num(f[2]), sys = Num(f[3]), irq = Num(f[6]), sirq = Num(f[7]), steal = Num(f[8]);
            string st = File.ReadAllText("/proc/self/stat");
            string[] g = st[(st.LastIndexOf(')') + 2)..].Split(' ');
            long self = Num(g[11]) + Num(g[12]);
            return new HostLoad(user + nice + sys + irq + sirq, steal, self, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>Среднее число ядер, занятых чужими процессами между снимками; null - нет данных.</summary>
    public static double? ForeignCores(in HostLoad a, in HostLoad b)
    {
        if (!a.Valid || !b.Valid)
        {
            return null;
        }

        double sec = (b._ticks - a._ticks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return sec <= 0 ? null : Math.Max(0, (b._busy - a._busy) - (b._self - a._self)) * JiffyS / sec;
    }

    /// <summary>Среднее число ядер, украденных гипервизором между снимками; null - нет данных.</summary>
    public static double? StolenCores(in HostLoad a, in HostLoad b)
    {
        if (!a.Valid || !b.Valid)
        {
            return null;
        }

        double sec = (b._ticks - a._ticks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return sec <= 0 ? null : (b._steal - a._steal) * JiffyS / sec;
    }

    /// <summary>Load average за 1 минуту или null.</summary>
    public static double? LoadAvg1()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            string[] f = File.ReadAllText("/proc/loadavg").Split(' ');
            return double.Parse(f[0], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static long Num(string s) => long.Parse(s, CultureInfo.InvariantCulture);
}
