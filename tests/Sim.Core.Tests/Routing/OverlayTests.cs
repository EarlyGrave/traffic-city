using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Util;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Routing;

/// <summary>H1 (routing.md 9.12): оверлей ячеек, кастомизация, волна. Эталон - плоская Дейкстра по подграфу ячейки.</summary>
public class OverlayTests
{
    private readonly ITestOutputHelper _out;

    public OverlayTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Heap_PopsInKeyThenIdOrder_AndDecreaseKeyWorks()
    {
        var heap = new IndexedHeap4(500);
        var rng = Pcg32.Create(5, 1);
        var best = new float[500];
        Array.Fill(best, float.PositiveInfinity);
        for (int i = 0; i < 4000; i++)
        {
            int id = rng.NextInt(0, 500);
            float key = rng.NextInt(0, 40); // много равных ключей: тай-брейк по id
            heap.PushOrDecrease(id, key);
            best[id] = Math.Min(best[id], key);
        }

        Assert.True(heap.Count > 0);
        float prevKey = -1f;
        int prevId = -1;
        int popped = 0;
        while (heap.Count > 0)
        {
            int id = heap.Pop(out float key);
            Assert.Equal(best[id], key);
            Assert.True(key > prevKey || (key == prevKey && id > prevId));
            prevKey = key;
            prevId = id;
            popped++;
        }

        int expected = 0;
        foreach (float b in best)
        {
            expected += float.IsPositiveInfinity(b) ? 0 : 1;
        }

        Assert.Equal(expected, popped);
        heap.PushOrDecrease(3, 1f);
        heap.Clear();
        Assert.Equal(0, heap.Count);
        Assert.False(heap.Contains(3));
    }

    [Fact]
    public void Geometry_CellsNestAndBoundariesMatchDefinition_S10k()
    {
        RoutingRig r = RoutingFixture.City("s10k");
        RouteGraph g = r.Builder.Graph;
        RoutingDesc d = OverlayFixture.Desc(3, 300f);
        Overlay ov = OverlayFixture.Build(r, in d);

        Assert.Equal(3, ov.Levels);
        Assert.Equal(ov.CellBase[4], ov.CellCap);
        for (int n = 0; n < r.Net.NodeCount; n++)
        {
            Vec2 p = r.Net.Nodes[n].Position;
            int cx = Math.Clamp((int)MathF.Floor((p.X - ov.Origin.X) / 300f), 0, ov.LevelNx[1] - 1);
            int cz = Math.Clamp((int)MathF.Floor((p.Z - ov.Origin.Z) / 300f), 0, ov.LevelNz[1] - 1);
            Assert.Equal(cx, ov.NodeCellX[n]);
            Assert.Equal(cz, ov.NodeCellZ[n]);
            Assert.Equal(ov.CellBase[1] + (cz * ov.LevelNx[1]) + cx, ov.CellOfNode(1, n));
            Assert.Equal(ov.CellBase[2] + (cz / 4 * ov.LevelNx[2]) + (cx / 4), ov.CellOfNode(2, n));
            Assert.Equal(ov.CellBase[3] + (cz / 16 * ov.LevelNx[3]) + (cx / 16), ov.CellOfNode(3, n));
        }

        // Входы/выходы - ровно граничные DirSeg, по возрастанию DirSeg, индексы согласованы.
        int dirCap = g.DirSegCapacity;
        int boundaryPairs = 0;
        for (int u = 0; u < g.DirSegCount; u++)
        {
            ref readonly SegmentData sd = ref r.Net.Segments[u >> 1];
            bool hasLanes = ((u & 1) == 0 ? sd.LanesAB : sd.LanesBA) > 0;
            int cut = 0;
            for (int l = 1; l <= 3; l++)
            {
                if (hasLanes && ov.CellOfNode(l, g.StartNode[u]) != ov.CellOfNode(l, g.EndNode[u]))
                {
                    cut = l;
                }
            }

            Assert.Equal(cut, ov.CutLevel[u]);
            for (int l = 1; l <= 3; l++)
            {
                int ei = ov.EntryIdx[((l - 1) * dirCap) + u];
                int xi = ov.ExitIdx[((l - 1) * dirCap) + u];
                if (l <= cut)
                {
                    boundaryPairs++;
                    int ce = ov.CellOfNode(l, g.EndNode[u]);
                    int cx = ov.CellOfNode(l, g.StartNode[u]);
                    Assert.Equal(u, ov.Entries[ov.EntryFirst[ce] + ei]);
                    Assert.Equal(u, ov.Exits[ov.ExitFirst[cx] + xi]);
                }
                else
                {
                    Assert.Equal(-1, ei);
                    Assert.Equal(-1, xi);
                }
            }
        }

        int entries = 0;
        for (int gc = 0; gc < ov.CellCap; gc++)
        {
            entries += ov.EntryCount[gc];
            for (int i = 1; i < ov.EntryCount[gc]; i++)
            {
                Assert.True(ov.Entries[ov.EntryFirst[gc] + i] > ov.Entries[ov.EntryFirst[gc] + i - 1]);
            }

            Assert.True(ov.EntryCap[gc] >= ov.EntryCount[gc] + 4);
            Assert.True(ov.ExitCap[gc] >= ov.ExitCount[gc] + 4);
        }

        Assert.Equal(boundaryPairs, entries);
        int maxCut = 0;
        for (int u = 0; u < g.DirSegCount; u++)
        {
            maxCut = Math.Max(maxCut, ov.CutLevel[u]);
        }

        Assert.Equal(3, maxCut); // на s10k с ячейками 300 м есть граница уровня 3
    }

    [Fact]
    public void Matrices_EqualFlatDijkstraInsideCell_S10kCell300Levels3()
    {
        RoutingRig r = RoutingFixture.City("s10k");
        RoutingDesc d = OverlayFixture.Desc(3, 300f);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Overlay ov = OverlayFixture.Build(r, in d);
        long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        int finite = OverlayFixture.AssertMatricesMatchReference(ov, r.Builder.Graph);
        _out.WriteLine($"s10k cell 300 levels 3: cells {ov.CellCap}, rows {ov.FullRows}, work {ov.FullWork}, finite cells {finite}, build {ms} ms");
        Assert.True(finite > 1000);
        Assert.True(ov.FullRows > 0 && ov.FullWork > 0);
    }

    [Fact]
    public void Matrices_EqualFlatDijkstra_S1kFanout2()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RoutingDesc d = OverlayFixture.Desc(3, 400f, fanout: 2);
        Overlay ov = OverlayFixture.Build(r, in d);
        Assert.True(OverlayFixture.AssertMatricesMatchReference(ov, r.Builder.Graph) > 200);
    }

    // Время препроцессинга оверлея (Create) на лестнице - ключевой риск шага 2.2; параметры как в scenarios/*.json.
    // Цифры идут в stdout (для отчёта берутся из прогона Release); проверка - только что матрицы не пусты и работа посчитана.
    [Theory]
    [InlineData("s1k", 1)]
    [InlineData("s10k", 1)]
    [InlineData("s100k", 2)]
    public void Create_PreprocessingTime_Ladder(string scenario, int levels)
    {
        RoutingRig r = RoutingFixture.City(scenario);
        RoutingDesc d = OverlayFixture.Desc(levels, 1000f);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Overlay ov = OverlayFixture.Build(r, in d);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        long matrixBytes = (long)ov.Matrix.Length * 4;
        _out.WriteLine(
            $"{scenario}: DirSeg {r.Builder.Graph.DirSegCount}, levels {levels}, cells {ov.CellCap}, rows {ov.FullRows}, relaxations {ov.FullWork}, "
            + $"overlay create {ms:F1} ms, matrix {matrixBytes / 1024} KiB, entries+exits arrays {(ov.Entries.Length + ov.Exits.Length) * 4L / 1024} KiB");
        Assert.True(ov.FullRows > 0 && ov.FullWork > 0);
    }

    [Fact]
    public void Create_RejectsBadParameters_AndTinyCells()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Builder.Graph;
        RoutingDesc d = OverlayFixture.Desc(0, 1000f);
        Assert.Equal(CreateStatus.InvalidCapacity, Overlay.TryCreate(r.Net, g, in d, out Overlay? ov));
        Assert.Null(ov);
        d = OverlayFixture.Desc(5, 1000f);
        Assert.Equal(CreateStatus.InvalidCapacity, Overlay.TryCreate(r.Net, g, in d, out ov));
        d = OverlayFixture.Desc(2, 1000f, fanout: 1);
        Assert.Equal(CreateStatus.InvalidCapacity, Overlay.TryCreate(r.Net, g, in d, out ov));
        d = OverlayFixture.Desc(1, 0.01f); // 3 км / 1 см = слишком много ячеек
        Assert.Equal(CreateStatus.InvalidCapacity, Overlay.TryCreate(r.Net, g, in d, out ov));
    }

    // H-T2: волна при customize_budget = 1 даёт те же матрицы, что полная кастомизация.
    [Fact]
    public void Wave_BudgetOne_EqualsFullCustomization_AndIsDeterministic()
    {
        RoutingRig r = RoutingFixture.City("s10k");
        RouteGraph g = r.Builder.Graph;
        RoutingDesc d = OverlayFixture.Desc(3, 300f, customizeBudget: 1);

        var free = new SegmentLoad[r.Net.SegmentCount];
        Array.Fill(free, new SegmentLoad { SpeedRatio = 1f });

        (float[] matrices, ulong trace, long rows) Run()
        {
            g.RecalcCosts(free, free.Length);
            Overlay ov = OverlayFixture.Build(r, in d);
            g.RecalcCosts(OverlayFixture.RandomLoads(r.Net.SegmentCount, 99), r.Net.SegmentCount);
            ov.AfterRecalc(g.WeightsVersion);
            Assert.True(ov.WaveActive);
            int dirtyRows = 0;
            for (int gc = 0; gc < ov.CellCap; gc++)
            {
                dirtyRows += ov.Dirty[gc] != 0 ? ov.EntryCount[gc] : 0;
            }

            Assert.True(dirtyRows > 0);
            var h = Fnv1a64.Create();
            int calls = 0;
            while (ov.WaveActive)
            {
                int before = (int)ov.CustomizeRows;
                int work = ov.RunWave(1);
                calls++;
                Assert.True(work > 0);
                Assert.Equal(before + 1, (int)ov.CustomizeRows); // бюджет 1: ровно одна строка за вызов
                h.Add(ov.WaveCell);
                h.Add(ov.WaveRow);
                h.Add(work);
            }

            Assert.Equal(dirtyRows, calls);
            Assert.Equal(dirtyRows, ov.CustomizeRows);
            for (int gc = 0; gc < ov.CellCap; gc++)
            {
                Assert.Equal(0, ov.Dirty[gc]);
            }

            return (OverlayFixture.CopyMatrices(ov), h.Value, ov.CustomizeRows);
        }

        (float[] m1, ulong t1, long rows1) = Run();
        (float[] m2, ulong t2, long rows2) = Run();
        Assert.Equal(t1, t2); // порядок строк и работа волны воспроизводимы
        Assert.Equal(rows1, rows2);
        Assert.True(m1.AsSpan().SequenceEqual(m2));

        // Свежая полная кастомизация на тех же весах побитово равна результату волны.
        RoutingDesc full = OverlayFixture.Desc(3, 300f);
        Overlay fresh = OverlayFixture.Build(r, in full);
        Assert.True(OverlayFixture.CopyMatrices(fresh).AsSpan().SequenceEqual(m1));
        Assert.True(OverlayFixture.AssertMatricesMatchReference(fresh, g) > 1000);
    }

    [Fact]
    public void Wave_UnchangedAndSubEpsWeights_DoNotMakeCellsDirty()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Builder.Graph;
        RoutingDesc d = OverlayFixture.Desc(2, 500f);
        Overlay ov = OverlayFixture.Build(r, in d);

        var loads = new SegmentLoad[r.Net.SegmentCount];
        Array.Fill(loads, new SegmentLoad { SpeedRatio = 1f });
        g.RecalcCosts(loads, loads.Length);
        ov.AfterRecalc(g.WeightsVersion);
        Assert.False(ov.WaveActive);
        Assert.Equal(g.WeightsVersion, ov.WeightsVersion);

        // 2% - ниже порога 5%: не грязно. 30% - выше: грязно ровно ячейки начала сегмента и предки.
        loads[0].SpeedRatio = 0.98f;
        g.RecalcCosts(loads, loads.Length);
        ov.AfterRecalc(g.WeightsVersion);
        Assert.False(ov.WaveActive);

        loads[1].SpeedRatio = 0.7f;
        g.RecalcCosts(loads, loads.Length);
        ov.AfterRecalc(g.WeightsVersion);
        Assert.True(ov.WaveActive);
        int c1 = ov.CellOfNode(1, g.StartNode[2]);
        int c2 = ov.CellOfNode(2, g.StartNode[2]);
        int dirty = 0;
        for (int gc = 0; gc < ov.CellCap; gc++)
        {
            dirty += ov.Dirty[gc];
        }

        Assert.Equal(1, ov.Dirty[c1]);
        Assert.Equal(1, ov.Dirty[c2]);
        Assert.Equal(2, dirty);
        ov.RunWave(0);
        Assert.False(ov.WaveActive);
        Assert.True(OverlayFixture.AssertMatricesMatchReference(ov, g, 0.2f) > 100); // сегмент 0 в допуске eps, остальное точно
    }

    // 0 B на волну (routing.md 9.8): AfterRecalc, RunWave, полная кастомизация и перестройка раскладки не аллоцируют.
    [Fact]
    public void Wave_RecalcAndCustomize_DoNotAllocate()
    {
        RoutingRig r = RoutingFixture.City("s1k");
        RouteGraph g = r.Builder.Graph;
        RoutingDesc d = OverlayFixture.Desc(2, 500f, customizeBudget: 300);
        Overlay ov = OverlayFixture.Build(r, in d);
        SegmentLoad[] a = OverlayFixture.RandomLoads(r.Net.SegmentCount, 1);
        SegmentLoad[] b = OverlayFixture.RandomLoads(r.Net.SegmentCount, 2);
        long rowsBefore = ov.CustomizeRows;
        bool flip = false;

        long bytes = AllocationProbe.Measure(() =>
        {
            flip = !flip;
            g.RecalcCosts(flip ? a : b, r.Net.SegmentCount);
            ov.AfterRecalc(g.WeightsVersion);
            while (ov.WaveActive)
            {
                ov.RunWave(300);
            }
        });
        Assert.Equal(0, bytes);
        Assert.True(ov.CustomizeRows > rowsBefore + 10, "волна должна реально работать");

        Assert.Equal(0, AllocationProbe.Measure(ov.CustomizeAll));
        Assert.Equal(0, AllocationProbe.Measure(ov.RebuildAll));
        Assert.True(ov.Rebuilds >= 2);
    }
}
