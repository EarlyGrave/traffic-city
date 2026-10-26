using System.Runtime.CompilerServices;

namespace Sim.Core.Util;

/// <summary>
/// Детерминированные числовые помощники, одинаковые на net8, net10 и в Rust.
/// Приведение float/double в целое вне диапазона зависит от рантайма (net8: <c>int.MinValue</c>, net10: насыщение),
/// поэтому в Sim.Core такие приведения идут через <c>ToIntSat/ToLongSat</c>: насыщение, NaN в 0, дробная часть отбрасывается
/// (совпадает с Rust <c>as</c>). В доказанном диапазоне результат равен обычному <c>(int)x</c>.
/// </summary>
public static class DetMath
{
    private const float Pow31F = 2147483648f;
    private const float Pow63F = 9223372036854775808f;
    private const double Pow31D = 2147483648.0;
    private const double Pow63D = 9223372036854775808.0;

    /// <summary>float в int: насыщение, NaN в 0, усечение к нулю.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToIntSat(float v)
    {
        if (v >= Pow31F)
        {
            return int.MaxValue;
        }

        if (v <= -Pow31F)
        {
            return int.MinValue;
        }

        return float.IsNaN(v) ? 0 : (int)v;
    }

    /// <summary>double в int: насыщение, NaN в 0, усечение к нулю.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToIntSat(double v)
    {
        if (v >= Pow31D)
        {
            return int.MaxValue;
        }

        if (v <= -Pow31D)
        {
            return int.MinValue;
        }

        return double.IsNaN(v) ? 0 : (int)v;
    }

    /// <summary>float в long: насыщение, NaN в 0, усечение к нулю.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToLongSat(float v)
    {
        if (v >= Pow63F)
        {
            return long.MaxValue;
        }

        if (v <= -Pow63F)
        {
            return long.MinValue;
        }

        return float.IsNaN(v) ? 0L : (long)v;
    }

    /// <summary>double в long: насыщение, NaN в 0, усечение к нулю.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToLongSat(double v)
    {
        if (v >= Pow63D)
        {
            return long.MaxValue;
        }

        if (v <= -Pow63D)
        {
            return long.MinValue;
        }

        return double.IsNaN(v) ? 0L : (long)v;
    }
}
