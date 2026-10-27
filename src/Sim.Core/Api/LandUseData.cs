using System;
using System.Runtime.InteropServices;
using Sim.Core.Util;

namespace Sim.Core.Api;

// Землепользование генератора (visual-style.md §8, ADR-0027): полигоны парков и воды для клиента. Данные производные (seed + city.landuse),
// в сейв не пишутся. Раскладка фиксирована: клиент и Rust-порт читают как есть.

/// <summary>Вид полигона землепользования. Urban отдельным полигоном не выдаётся (всё, что не парк и не вода).</summary>
public enum LandUseKind : byte
{
    Urban = 0,
    Park = 1,
    Water = 2,
}

/// <summary>Флаги <see cref="LandUsePoly.Flags"/>.</summary>
[Flags]
public enum LandUseMarks : byte
{
    None = 0,
    Pond = 1,       // парк с прудом (клиент рисует 16-угольник с отступом внутри клетки)
    Coast = 2,      // береговая полоса за краем карты (вне сети)
}

/// <summary>Сторона карты с береговой полосой. North - минус Z, South - плюс Z, East - плюс X, West - минус X.</summary>
public enum LandUseCoast : byte
{
    None = 0,
    South = 1,
    East = 2,
    North = 3,
    West = 4,
}

/// <summary>Параметры <c>city.landuse</c> (scenario-format.md §2.1). Нулевая структура = выключено.</summary>
public struct LandUseDesc
{
    public bool Enabled;
    public float ParkShare;      // 0..1, доля клеток-парков
    public float PondShare;      // 0..1, доля парков с прудом
    public LandUseCoast Coast;
    public float CoastWidthM;    // м, округляется до module_m
}

/// <summary>Четырёхугольник землепользования: 36 байт (4 вершины по обходу + вид + флаги). Вершины - позиции узлов плана (при snap_nodes - на сетке 8 м).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 36)]
public struct LandUsePoly
{
    public Vec2 P0, P1, P2, P3;  // 0..31
    public byte Kind;            // 32 <see cref="LandUseKind"/>
    public byte Flags;           // 33 <see cref="LandUseMarks"/>

    /// <summary>Точка внутри четырёхугольника (граница включена; вырожденный в треугольник - тоже).</summary>
    public readonly bool Contains(float x, float z) => InTriangle(P0, P1, P2, x, z) || InTriangle(P0, P2, P3, x, z);

    private static bool InTriangle(Vec2 a, Vec2 b, Vec2 c, float x, float z)
    {
        float d1 = ((x - b.X) * (a.Z - b.Z)) - ((a.X - b.X) * (z - b.Z));
        float d2 = ((x - c.X) * (b.Z - c.Z)) - ((b.X - c.X) * (z - c.Z));
        float d3 = ((x - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (z - a.Z));
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }
}
