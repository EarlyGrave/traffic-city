using System;

namespace Sim.Core.Api;

/// <summary>
/// Ожидание у светофоров по манёврам (intersections.md §10, 2.5): допуски на узлах <c>Signal</c>, сумма и гистограмма
/// <c>WaitTicks</c> в момент допуска. Монотонные счётчики вне хэша; пишет только однопоточная фаза 4. Массивы выделены
/// один раз; индекс манёвра: 0 straight, 1 right, 2 left, 3 u-turn.
/// </summary>
public sealed class SignalMetrics
{
    public const int Maneuvers = 4;
    public const int HistBuckets = 121;   // 1 с каждый, последний - «>= 120 с»
    public const int TicksPerBucket = 20; // 1 с при dt = 1/20

    public readonly long[] WaitTicks = new long[Maneuvers];
    public readonly long[] Grants = new long[Maneuvers];
    public readonly long[] Hist = new long[Maneuvers * HistBuckets];

    internal void Add(int maneuver, int waitTicks)
    {
        WaitTicks[maneuver] += waitTicks;
        Grants[maneuver]++;
        int b = waitTicks / TicksPerBucket;
        Hist[(maneuver * HistBuckets) + (b < HistBuckets ? b : HistBuckets - 1)]++;
    }

    /// <summary>Копия текущих значений в <paramref name="dst"/> (для разности «после - до» у клиента, без аллокаций).</summary>
    public void CopyTo(SignalMetrics dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        Array.Copy(WaitTicks, dst.WaitTicks, Maneuvers);
        Array.Copy(Grants, dst.Grants, Maneuvers);
        Array.Copy(Hist, dst.Hist, Hist.Length);
    }
}
