using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Gen;

/// <summary>
/// Землепользование (visual-style.md §8, ADR-0027): клетки решётки плана получают вид Urban/Park (пруд - флаг парка), плюс необязательная береговая полоса воды за краем карты.
/// Порядок нормативен: клетки по <c>kz * Nx + kx</c>, на клетку всегда 2 вызова PRNG (<c>u1</c> - парк, <c>u2</c> - пруд). PRNG локальный (<c>RngStream.LandUse</c>) и живёт только в <c>Create</c>:
/// в <c>World</c> не хранится, хэш состояния и общий поток генератора не затронуты. Вызывается по финальному плану (после удалений и слияний, до эмиссии).
/// </summary>
internal static class LandUseGenerator
{
    /// <summary>Множитель шанса парка у клетки, рядом с которой удалена улица («дырки» сетки естественно становятся парками).</summary>
    internal const float HoleParkFactor = 2f;

    public static LandUsePoly[] Generate(in LandUseDesc d, GridPlan pl, float halfW, float halfH, float module, ulong seed)
    {
        var list = new List<LandUsePoly>();
        Pcg32 rng = Pcg32.ForStream(seed, RngStream.LandUse);
        int nx = pl.Nx;
        int nz = pl.Nz;
        int stride = nx + 1;
        for (int kz = 0; kz < nz; kz++)
        {
            for (int kx = 0; kx < nx; kx++)
            {
                float u1 = rng.NextFloat01();
                float u2 = rng.NextFloat01();
                if (kx == 0 || kz == 0 || kx == nx - 1 || kz == nz - 1)
                {
                    continue;   // периметр - не парк
                }

                int p00 = (kz * stride) + kx;
                float chance = d.ParkShare * (HasHole(pl, kx, kz) ? HoleParkFactor : 1f);
                if (u1 >= chance)
                {
                    continue;
                }

                list.Add(new LandUsePoly
                {
                    P0 = Corner(pl, p00),
                    P1 = Corner(pl, p00 + 1),
                    P2 = Corner(pl, p00 + stride + 1),
                    P3 = Corner(pl, p00 + stride),
                    Kind = (byte)LandUseKind.Park,
                    Flags = (byte)(u2 < d.PondShare ? LandUseMarks.Pond : LandUseMarks.None),
                });
            }
        }

        if (d.Coast != LandUseCoast.None)
        {
            list.Add(CoastPoly(d, halfW, halfH, module));
        }

        return list.ToArray();
    }

    /// <summary>Хотя бы одно из четырёх рёбер клетки мертво (улица удалена или схлопнута слиянием).</summary>
    private static bool HasHole(GridPlan pl, int kx, int kz) =>
        pl.EdgeAlive[pl.HorizontalEdge(kx, kz)] == 0 ||
        pl.EdgeAlive[pl.HorizontalEdge(kx, kz + 1)] == 0 ||
        pl.EdgeAlive[pl.VerticalEdge(kx, kz)] == 0 ||
        pl.EdgeAlive[pl.VerticalEdge(kx + 1, kz)] == 0;

    /// <summary>Позиция угла клетки. Слитый узел (<c>q = p + 1</c>, см. TryMerge) мёртв и занимает место выжившего <c>p</c>.</summary>
    private static Vec2 Corner(GridPlan pl, int p)
    {
        if (pl.NodeAlive[p] == 0 && p > 0 && (pl.NodeFlags[p - 1] & GridPlan.FlagMerged) != 0)
        {
            p--;
        }

        return new Vec2(pl.X[p], pl.Z[p]);
    }

    private static LandUsePoly CoastPoly(in LandUseDesc d, float halfW, float halfH, float module)
    {
        float cw = MathF.Max(module, MathF.Round(d.CoastWidthM / module) * module);
        float x0 = -halfW;
        float x1 = halfW;
        float z0 = -halfH;
        float z1 = halfH;
        switch (d.Coast)
        {
            case LandUseCoast.South:
                z0 = halfH;
                z1 = halfH + cw;
                break;
            case LandUseCoast.North:
                z1 = -halfH;
                z0 = -halfH - cw;
                break;
            case LandUseCoast.East:
                x0 = halfW;
                x1 = halfW + cw;
                break;
            default:
                x1 = -halfW;
                x0 = -halfW - cw;
                break;
        }

        return new LandUsePoly
        {
            P0 = new Vec2(x0, z0),
            P1 = new Vec2(x1, z0),
            P2 = new Vec2(x1, z1),
            P3 = new Vec2(x0, z1),
            Kind = (byte)LandUseKind.Water,
            Flags = (byte)LandUseMarks.Coast,
        };
    }
}
