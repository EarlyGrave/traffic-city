using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Routing;

namespace Sim.Core.Tests.Routing;

/// <summary>Общие помощники тестов оверлея: сборка и эталонная Дейкстра по подграфу ячейки (routing.md 9.1, 9.3).</summary>
internal static class OverlayFixture
{
    public static RoutingDesc Desc(int levels, float cell, int fanout = 4, int customizeBudget = 10000, float eps = 0.05f) => new()
    {
        Levels = levels,
        Fanout = fanout,
        CellSize = cell,
        CustomizeBudgetPerTick = customizeBudget,
        CustomizeEps = eps,
    };

    public static Overlay Build(RoutingRig r, in RoutingDesc d)
    {
        CreateStatus st = Overlay.TryCreate(r.Net, r.Builder.Graph, in d, out Overlay? ov);
        Assert.Equal(CreateStatus.Ok, st);
        return ov!;
    }

    /// <summary>Случайные нагрузки: часть сегментов остаётся свободной, остальные замедлены сильно (изменение веса заведомо выше порога).</summary>
    public static SegmentLoad[] RandomLoads(int segments, ulong seed, int everyNth = 3)
    {
        var rng = Sim.Core.Util.Pcg32.Create(seed, 5);
        var loads = new SegmentLoad[segments];
        for (int i = 0; i < segments; i++)
        {
            loads[i].SpeedRatio = i % everyNth == 0 ? rng.NextRange(0.25f, 0.75f) : 1f;
        }

        return loads;
    }

    /// <summary>
    /// Эталон строки матрицы: плоская Дейкстра из входа <paramref name="e"/> по DirSeg, внутренним для ячейки <paramref name="gc"/>
    /// (оба конца в ячейке на её уровне); результат - стоимость до каждого выхода ячейки (ключ - DirSeg).
    /// </summary>
    public static Dictionary<int, float> ReferenceRow(Overlay ov, RouteGraph g, int gc, int level, int e)
    {
        var exits = new Dictionary<int, float>();
        var dist = new Dictionary<int, float>();
        var pq = new PriorityQueue<int, (float, int)>();

        void Relax(int v, float gv)
        {
            for (int k = g.ArcFirst[v]; k < g.ArcFirst[v + 1]; k++)
            {
                int w = g.ArcTo[k];
                float nd = gv + g.ArcCost[k] + g.SegCost[w];
                if (ov.CellOfNode(level, g.EndNode[w]) != gc)
                {
                    if (!exits.TryGetValue(w, out float old) || nd < old)
                    {
                        exits[w] = nd;
                    }
                }
                else if (!dist.TryGetValue(w, out float d) || nd < d)
                {
                    dist[w] = nd;
                    pq.Enqueue(w, (nd, w));
                }
            }
        }

        Relax(e, 0f);
        while (pq.TryDequeue(out int v, out (float, int) pri))
        {
            if (pri.Item1 > dist[v])
            {
                continue;
            }

            Relax(v, pri.Item1);
        }

        return exits;
    }

    /// <summary>Каждая клетка матрицы каждой ячейки всех уровней = эталон в допуске; возвращает число конечных клеток.</summary>
    public static int AssertMatricesMatchReference(Overlay ov, RouteGraph g, float relTol = 1e-4f)
    {
        int finite = 0;
        for (int gc = 0; gc < ov.CellCap; gc++)
        {
            int level = ov.LevelOf(gc);
            for (int r = 0; r < ov.EntryCount[gc]; r++)
            {
                int e = ov.Entries[ov.EntryFirst[gc] + r];
                Dictionary<int, float> expect = ReferenceRow(ov, g, gc, level, e);
                int rowBase = ov.MatrixFirst[gc] + (r * ov.ExitCap[gc]);
                int reached = 0;
                for (int c = 0; c < ov.ExitCount[gc]; c++)
                {
                    int x = ov.Exits[ov.ExitFirst[gc] + c];
                    float actual = ov.Matrix[rowBase + c];
                    if (expect.TryGetValue(x, out float want))
                    {
                        reached++;
                        finite++;
                        Assert.True(
                            MathF.Abs(actual - want) <= (relTol * want) + 1e-4f,
                            $"cell {gc} (level {level}) entry {e} -> exit {x}: matrix {actual}, reference {want}");
                    }
                    else
                    {
                        Assert.True(float.IsPositiveInfinity(actual), $"cell {gc} entry {e} -> exit {x}: matrix {actual}, reference unreachable");
                    }
                }

                Assert.Equal(expect.Count, reached); // каждый достижимый выход эталона есть среди выходов ячейки
                for (int c = ov.ExitCount[gc]; c < ov.ExitCap[gc]; c++)
                {
                    Assert.True(float.IsPositiveInfinity(ov.Matrix[rowBase + c]), "запасной столбец должен быть +inf");
                }
            }
        }

        return finite;
    }

    /// <summary>Побитовая копия используемой части матриц.</summary>
    public static float[] CopyMatrices(Overlay ov)
    {
        int used = ov.MatrixFirst[ov.CellCap - 1] + (ov.EntryCap[ov.CellCap - 1] * ov.ExitCap[ov.CellCap - 1]);
        var copy = new float[used];
        Array.Copy(ov.Matrix, copy, used);
        return copy;
    }
}
