using System;
using Sim.Core.Api;

namespace Sim.Core.Rules;

/// <summary>
/// Intelligent Driver Model (Treiber, Hennecke, Helbing 2000) - чистые функции, без состояния и аллокаций.
/// <c>a = amax * (1 - (v/v0)^delta - (s*/gap)^2)</c>, <c>s* = s0 + max(0, v*T + v*dv / (2*sqrt(amax*b)))</c>,
/// <c>dv = v - vLeader</c> (скорость сближения). Результат ограничен снизу <see cref="MaxBrake"/>.
/// </summary>
internal static class Idm
{
    /// <summary>Физический предел торможения, м/с²: IDM при мизерном зазоре даёт сколь угодно большое -a.</summary>
    public const float MaxBrake = 9f;

    /// <summary>«Бесконечный» зазор для свободной дороги (интерактивный член обнуляется).</summary>
    public const float FreeGap = 1e6f;

    private const float MinGap = 0.01f;
    private const float MinV0 = 0.1f;

    /// <summary>Желаемая скорость на полосе: <c>min(MaxSpeed, SpeedFactor * limit)</c> (scenario-format §3).</summary>
    public static float DesiredSpeed(in VehicleTypeDesc t, float laneLimit)
    {
        float v = t.SpeedFactor * laneLimit;
        return v < t.MaxSpeed ? v : t.MaxSpeed;
    }

    /// <summary>Ускорение на свободной дороге (без лидера).</summary>
    public static float FreeAccel(float v, float v0, float amax, float delta)
    {
        float a = amax * (1f - Pow(v / (v0 < MinV0 ? MinV0 : v0), delta));
        return a < -MaxBrake ? -MaxBrake : a;
    }

    /// <summary>Ускорение при лидере на расстоянии <paramref name="gap"/> (бампер-в-бампер, м) со сближением <paramref name="dv"/>.</summary>
    public static float Accel(float v, float v0, float gap, float dv, float amax, float b, float headway, float s0, float delta)
    {
        if (gap >= FreeGap)
        {
            return FreeAccel(v, v0, amax, delta);
        }

        if (gap < MinGap)
        {
            gap = MinGap;
        }

        float sStar = (v * headway) + (v * dv / (2f * MathF.Sqrt(amax * b)));
        sStar = s0 + (sStar > 0f ? sStar : 0f);
        float r = sStar / gap;
        float a = amax * (1f - Pow(v / (v0 < MinV0 ? MinV0 : v0), delta) - (r * r));
        return a < -MaxBrake ? -MaxBrake : a;
    }

    /// <summary>Ускорение машины типа <paramref name="t"/> со скоростью <paramref name="v"/>.</summary>
    public static float Accel(in VehicleTypeDesc t, float v, float v0, float gap, float dv) =>
        Accel(v, v0, gap, dv, t.Accel, t.ComfortDecel, t.HeadwayTime, t.MinGap, t.Delta);

    // Показатель 4 (по умолчанию) - умножениями: результат не зависит от libm.
    private static float Pow(float x, float delta)
    {
        if (delta == 4f)
        {
            float x2 = x * x;
            return x2 * x2;
        }

        if (delta == 2f)
        {
            return x * x;
        }

        return MathF.Pow(x, delta);
    }
}
