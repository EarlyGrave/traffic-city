using System;
using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

/// <summary>Семантика Rust <c>as</c>: насыщение, NaN в 0, усечение к нулю - одинаково на net8 и net10.</summary>
public class DetMathTests
{
    public static TheoryData<float, int> FloatToInt => new()
    {
        { 0f, 0 },
        { -0f, 0 },
        { 0.9f, 0 },
        { -0.9f, 0 },
        { 1.5f, 1 },
        { -1.5f, -1 },
        { 2147483520f, 2147483520 },                       // наибольший float < 2^31
        { 2147483648f, int.MaxValue },                     // 2^31
        { 3e9f, int.MaxValue },
        { float.MaxValue, int.MaxValue },
        { float.PositiveInfinity, int.MaxValue },
        { -2147483648f, int.MinValue },                    // ровно int.MinValue
        { -2147483904f, int.MinValue },                    // следующий float ниже
        { -3e9f, int.MinValue },
        { float.MinValue, int.MinValue },
        { float.NegativeInfinity, int.MinValue },
        { float.NaN, 0 },
        { BitConverter.Int32BitsToSingle(unchecked((int)0x80000001)), 0 },   // отрицательный денормал
        { float.Epsilon, 0 },
    };

    [Theory]
    [MemberData(nameof(FloatToInt))]
    public void ToIntSat_Float(float v, int expected) => Assert.Equal(expected, DetMath.ToIntSat(v));

    public static TheoryData<double, int> DoubleToInt => new()
    {
        { 0.0, 0 },
        { -0.0, 0 },
        { 2147483647.9, 2147483647 },
        { 2147483648.0, int.MaxValue },
        { 1e300, int.MaxValue },
        { double.PositiveInfinity, int.MaxValue },
        { -2147483648.0, int.MinValue },
        { -2147483648.9, int.MinValue },
        { -2147483649.0, int.MinValue },
        { double.NegativeInfinity, int.MinValue },
        { double.NaN, 0 },
        { double.Epsilon, 0 },
        { -double.Epsilon, 0 },
        { 123456.789, 123456 },
        { -123456.789, -123456 },
    };

    [Theory]
    [MemberData(nameof(DoubleToInt))]
    public void ToIntSat_Double(double v, int expected) => Assert.Equal(expected, DetMath.ToIntSat(v));

    public static TheoryData<float, long> FloatToLong => new()
    {
        { 0f, 0L },
        { -0f, 0L },
        { 1.5f, 1L },
        { -1.5f, -1L },
        { 3e9f, 3_000_000_000L },
        { 9.2233715e18f, 9223371487098961920L },           // наибольший float < 2^63
        { 9223372036854775808f, long.MaxValue },           // 2^63
        { 1e30f, long.MaxValue },
        { float.PositiveInfinity, long.MaxValue },
        { -9223372036854775808f, long.MinValue },
        { -1e30f, long.MinValue },
        { float.NegativeInfinity, long.MinValue },
        { float.NaN, 0L },
        { float.Epsilon, 0L },
    };

    [Theory]
    [MemberData(nameof(FloatToLong))]
    public void ToLongSat_Float(float v, long expected) => Assert.Equal(expected, DetMath.ToLongSat(v));

    public static TheoryData<double, long> DoubleToLong => new()
    {
        { 0.0, 0L },
        { -0.0, 0L },
        { 4294967296.5, 4294967296L },
        { -4294967296.5, -4294967296L },
        { 9223372036854774784.0, 9223372036854774784L },   // наибольший double < 2^63
        { 9223372036854775808.0, long.MaxValue },
        { 1e300, long.MaxValue },
        { double.PositiveInfinity, long.MaxValue },
        { -9223372036854775808.0, long.MinValue },
        { -1e300, long.MinValue },
        { double.NegativeInfinity, long.MinValue },
        { double.NaN, 0L },
        { double.Epsilon, 0L },
    };

    [Theory]
    [MemberData(nameof(DoubleToLong))]
    public void ToLongSat_Double(double v, long expected) => Assert.Equal(expected, DetMath.ToLongSat(v));

    [Fact]
    public void InRange_EqualsPlainCast()
    {
        var rng = Pcg32.Create(12345UL, 1UL);
        for (int i = 0; i < 20000; i++)
        {
            float f = ((int)rng.NextUInt() / 4096f) + (rng.NextUInt() % 1000 / 1000f);
            Assert.Equal((int)f, DetMath.ToIntSat(f));
            Assert.Equal((long)f, DetMath.ToLongSat(f));
            double d = f * 1.37;
            Assert.Equal((int)d, DetMath.ToIntSat(d));
            Assert.Equal((long)d, DetMath.ToLongSat(d));
        }
    }
}
