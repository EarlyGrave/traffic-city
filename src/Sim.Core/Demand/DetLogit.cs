using System;
using Sim.Core.Util;

namespace Sim.Core.Demand;

/// <summary>
/// Детерминированный целочисленный логит (zoning-demand.md §6.2, ADR-0021, ADR-0022): без libm, только целое умножение и сдвиги, поэтому C# и Rust совпадают побитово.
/// <list type="bullet">
/// <item><see cref="ExpNeg"/>[k] = round(2^30 · e^(−k/64)), k = 0..1023; строится повторным целым умножением на литерал <see cref="E1"/> = round(2^30 · e^(−1/64)).</item>
/// <item>Вес варианта: <c>w = ExpNeg[min(1023, ((C − Cmin) · θ64) &gt;&gt; 16)]</c>, <c>θ64 = θ[1/дс] · 64 · 2^16</c> (шаг таблицы 1/64 даёт ошибку веса не более 1.6%).</item>
/// <item>Выбор: <c>r = NextUInt64 % Σw</c>, обход по индексу варианта (ничья - меньший индекс). Всегда ровно одно обращение к PRNG, если есть доступный вариант.</item>
/// <item>LogSum = Cmin − ln(Σw / 2^30) / θ; логарифм - бинарный поиск по убывающей <see cref="ExpNeg"/>.</item>
/// </list>
/// Стоимости - децисекунды (int), <see cref="NoCost"/> = вариант недоступен.
/// </summary>
internal static class DetLogit
{
    /// <summary>Размер таблицы <see cref="ExpNeg"/>.</summary>
    public const int TableSize = 1024;

    /// <summary>round(2^30 · e^(−1/64)); литерал, одинаковый в C# и Rust.</summary>
    public const long E1 = 1057095000;

    /// <summary>Недоступный вариант.</summary>
    public const int NoCost = int.MaxValue;

    /// <summary>2^30.</summary>
    public const long One = 1L << 30;

    /// <summary>Таблица <c>round(2^30 · e^(−k/64))</c>.</summary>
    public static readonly int[] ExpNeg = BuildTable();

    private static int[] BuildTable()
    {
        var t = new int[TableSize];
        long v = One;
        t[0] = (int)v;
        for (int k = 1; k < TableSize; k++)
        {
            v = ((v * E1) + (One >> 1)) >> 30;
            t[k] = (int)v;
        }

        return t;
    }

    /// <summary>Вес варианта с разностью стоимостей <paramref name="deltaC"/> ≥ 0 к лучшему (Q30, <c>2^30</c> при нуле).</summary>
    public static int Weight(long deltaC, int theta64)
    {
        long k = (deltaC * theta64) >> 16;
        return ExpNeg[k >= TableSize ? TableSize - 1 : (k < 0 ? 0 : (int)k)];
    }

    /// <summary>64-бит число из двух обращений к PRNG (старшие 32 бита - первое).</summary>
    public static ulong NextUInt64(ref Pcg32 rng)
    {
        ulong hi = rng.NextUInt();
        ulong lo = rng.NextUInt();
        return (hi << 32) | lo;
    }

    /// <summary>
    /// Выбор варианта по логиту: индекс или -1, если все недоступны. Стоимости <see cref="NoCost"/> пропускаются; PRNG читается ровно один раз (два слова), если есть хотя бы один доступный вариант.
    /// </summary>
    public static int Choose(ref Pcg32 rng, ReadOnlySpan<int> costs, int theta64)
    {
        int cmin = NoCost;
        for (int i = 0; i < costs.Length; i++)
        {
            if (costs[i] < cmin)
            {
                cmin = costs[i];
            }
        }

        if (cmin == NoCost)
        {
            return -1;
        }

        ulong sum = 0;
        for (int i = 0; i < costs.Length; i++)
        {
            if (costs[i] != NoCost)
            {
                sum += (ulong)Weight((long)costs[i] - cmin, theta64);
            }
        }

        ulong r = NextUInt64(ref rng) % sum;
        ulong acc = 0;
        int last = -1;
        for (int i = 0; i < costs.Length; i++)
        {
            if (costs[i] == NoCost)
            {
                continue;
            }

            last = i;
            acc += (ulong)Weight((long)costs[i] - cmin, theta64);
            if (r < acc)
            {
                return i;
            }
        }

        return last;
    }

    /// <summary>Выбор по неотрицательным весам <paramref name="weights"/>: индекс или -1 при нулевой сумме (тогда PRNG не читается; иначе ровно два слова).</summary>
    public static int ChooseWeighted(ref Pcg32 rng, ReadOnlySpan<long> weights)
    {
        ulong sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            sum += (ulong)weights[i];
        }

        if (sum == 0)
        {
            return -1;
        }

        ulong r = NextUInt64(ref rng) % sum;
        ulong acc = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            acc += (ulong)weights[i];
            if (r < acc)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }

    /// <summary>Индекс <c>k</c> таблицы, для которого <c>ExpNeg[k]</c> ближе всего к <paramref name="target"/> (0 &lt; target ≤ 2^30; меньше таблицы - 1023; при равенстве расстояний - больший k).</summary>
    public static int NearestIndex(long target)
    {
        int lo = 0;
        int hi = TableSize - 1;
        if (target <= ExpNeg[hi])
        {
            return hi;
        }

        // Первый k с ExpNeg[k] <= target (таблица убывает).
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (ExpNeg[mid] <= target)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        if (lo > 0 && ExpNeg[lo - 1] - target < target - ExpNeg[lo])
        {
            return lo - 1;
        }

        return lo;
    }

    /// <summary><c>round(64 · ln x)</c> для целого <paramref name="x"/> ≥ 1 (обратный поиск по таблице; больше 2^30 даёт 1023).</summary>
    public static int Ln64(long x)
    {
        if (x <= 1)
        {
            return 0;
        }

        return x > One ? TableSize - 1 : NearestIndex(One / x);
    }

    /// <summary>Перевод индекса таблицы (единицы 1/64 натурального логарифма) в децисекунды: <c>k / 64 / θ = k · 2^16 / θ64</c>, с округлением.</summary>
    public static int LnUnitsToDs(int k, int theta64)
    {
        if (theta64 <= 0)
        {
            return 0;
        }

        return (int)((((long)k << 16) + (theta64 >> 1)) / theta64);
    }

    /// <summary>Logsum доступных вариантов в децисекундах; <see cref="NoCost"/>, если доступных нет.</summary>
    public static int LogSum(ReadOnlySpan<int> costs, int theta64)
    {
        int cmin = NoCost;
        for (int i = 0; i < costs.Length; i++)
        {
            if (costs[i] < cmin)
            {
                cmin = costs[i];
            }
        }

        if (cmin == NoCost)
        {
            return NoCost;
        }

        ulong sum = 0;
        for (int i = 0; i < costs.Length; i++)
        {
            if (costs[i] != NoCost)
            {
                sum += (ulong)Weight((long)costs[i] - cmin, theta64);
            }
        }

        // ln(Σ / 2^30) ≈ k / 64, где ExpNeg[k] ≈ 2^60 / Σ.
        int k = NearestIndex((long)((1UL << 60) / sum));
        return cmin - LnUnitsToDs(k, theta64);
    }
}
