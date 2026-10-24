using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Routing;

/// <summary>H1 в составе мира: Create строит оверлей, BuildRoad (запас / перестройка / выключение), волна по тикам, детерминизм, 0 B/тик.</summary>
public class OverlayWorldTests
{
    private readonly ITestOutputHelper _out;

    public OverlayWorldTests(ITestOutputHelper output) => _out = output;

    // Три узла на прямой (0..400 м); ячейки 100 м, уровень 2 = 200 м. Ёмкости с запасом под много BuildRoad.
    private static ScenarioDesc Line(int customizeBudget = 10000, int levels = 2)
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Capacities.MaxNodes = 512;
        d.Capacities.MaxSegments = 512;
        d.Capacities.MaxLanes = 8192;
        d.Capacities.MaxLanePoints = 65536;
        d.Capacities.CommandQueueCapacity = 16;
        d.Capacities.EventCapacity = 256;
        d.Routing = OverlayFixture.Desc(levels, 100f, fanout: 2, customizeBudget: customizeBudget);
        return d;
    }

    private static void Road(Simulation sim, float z, uint tag)
    {
        // Сегмент пересекает границу ячеек x = 100 (обе ячейки уровня 1 получают и вход, и выход).
        var r = new BuildRoadCmd { Ax = 10, Az = z, Bx = 150, Bz = z, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        sim.Enqueue(Command.MakeBuildRoad(in r, tag));
    }

    private static void Settle(Simulation sim, Overlay ov)
    {
        for (int i = 0; i < 2000 && ov.WaveActive; i++)
        {
            sim.Step(1);
        }

        Assert.False(ov.WaveActive);
    }

    [Fact]
    public void Create_BuildsOverlay_OnlyWhenLevelsPositive()
    {
        ScenarioDesc d = Line();
        using Simulation on = Simulation.Create(in d);
        Assert.NotNull(on.World.Overlay);
        Assert.False(on.World.Overlay!.WaveActive);
        Assert.True(OverlayFixture.AssertMatricesMatchReference(on.World.Overlay, on.World.Router.Graph) > 0);

        d.Routing.Levels = 0;
        using Simulation off = Simulation.Create(in d);
        Assert.Null(off.World.Overlay);

        d.Routing.Levels = 7;
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out _));
        d.Routing = OverlayFixture.Desc(2, 100f);
        d.Routing.WindowSteps = 4;
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out _));
    }

    // H-T3: BuildRoad внутри запаса - дописать вход/выход, грязные ячейки, волна; сверх запаса - полная перестройка;
    // матрицы = эталон после волны.
    [Fact]
    public void BuildRoad_InsideSlack_ThenRebuildBeyondIt()
    {
        ScenarioDesc d = Line(customizeBudget: 1);
        using Simulation sim = Simulation.Create(in d);
        Overlay ov = sim.World.Overlay!;
        RouteGraph g = sim.World.Router.Graph;
        int cell = ov.CellBase[1] + 1; // x в [100, 200): пока без границ; каждая новая дорога даёт ей вход и выход
        Assert.Equal(0, ov.EntryCount[cell]);
        Assert.Equal(4, ov.EntryCap[cell]);

        int firstRebuild = -1;
        for (int k = 0; k < 8; k++)
        {
            int entriesBefore = ov.EntryCount[cell];
            long rebuildsBefore = ov.Rebuilds;
            Road(sim, 30f * (k + 1), (uint)k);
            sim.Step(1);
            Assert.Equal(k + 1, (int)sim.Counters.CommandsApplied);
            if (ov.Rebuilds > rebuildsBefore)
            {
                firstRebuild = firstRebuild < 0 ? k : firstRebuild;
                Assert.False(ov.WaveActive); // перестройка синхронная: полная кастомизация внутри команды
                Assert.True(ov.EntryCap[cell] >= ov.EntryCount[cell] + 4);
            }
            else
            {
                Assert.Equal(entriesBefore + 1, ov.EntryCount[cell]); // дописан в запас
                Assert.True(ov.WaveActive); // бюджет 1: за тик одна строка, ячейки с новыми вершинами ещё грязные
                Assert.Equal(1, ov.Dirty[cell]);
            }

            Settle(sim, ov);
            Assert.True(OverlayFixture.AssertMatricesMatchReference(ov, g) > 0);
            Assert.Equal(ov.Rebuilds, sim.Counters.OverlayRebuilds);
        }

        Assert.Equal(4, firstRebuild); // четыре дороги влезают в запас (4 строки), пятая - перестройка
        Assert.Equal(1, ov.Rebuilds);
        Assert.False(ov.Disabled);
        Assert.Equal(0, sim.Counters.OverlayDisabled);
        Assert.Equal(g.DirSegCount, sim.World.Net.SegmentCount * 2);
    }

    // §15 п.6: Graph.Rebuild сбрасывает SegCost к свободному потоку; BuildRoad возвращает веса по текущей загрузке (RecalcCosts),
    // иначе под нагрузкой волна после дороги пересчитывала бы все ячейки с замедленными сегментами.
    [Fact]
    public void BuildRoad_KeepsLoadWeights_AfterGraphRebuild()
    {
        ScenarioDesc d = Line();
        using Simulation sim = Simulation.Create(in d);
        Overlay ov = sim.World.Overlay!;
        RouteGraph g = sim.World.Router.Graph;
        int segs = sim.World.Net.SegmentCount;
        for (int s = 0; s < segs; s++)
        {
            sim.World.SegmentLoads[s].SpeedRatio = 0.4f;
        }

        // Оверлей уже считан по весам загрузки (как после обычного пересчёта).
        g.RecalcCosts(sim.World.SegmentLoads, segs);
        ov.AfterRecalc(g.WeightsVersion);
        ov.RunWave(0);
        Assert.False(ov.WaveActive);

        int versionBefore = g.WeightsVersion;
        long rowsBefore = ov.CustomizeRows;
        Road(sim, 30f, 1);
        sim.Step(1);
        Assert.Equal(1, (int)sim.Counters.CommandsApplied);
        Assert.Equal(segs + 1, sim.World.Net.SegmentCount);
        Assert.True(g.WeightsVersion > versionBefore);
        for (int s = 0; s < segs; s++)
        {
            ref readonly SegmentData sd = ref sim.World.Net.Segments[s];
            float want = sd.Length / (sd.SpeedLimit * 0.4f);
            Assert.Equal(want, g.SegCost[2 * s], 3);
            Assert.Equal(want, g.SegCost[(2 * s) + 1], 3);
        }

        ref readonly SegmentData fresh = ref sim.World.Net.Segments[segs]; // новая дорога - загрузки нет (1)
        Assert.Equal(fresh.Length / fresh.SpeedLimit, g.SegCost[2 * segs], 3);

        // Волна (бюджет 10000 - целиком за тик команды) считает только ячейки узлов новой дороги и их предков.
        Assert.False(ov.WaveActive);
        var cells = new System.Collections.Generic.HashSet<int>();
        for (int l = 1; l <= ov.Levels; l++)
        {
            cells.Add(ov.CellOfNode(l, fresh.NodeA));
            cells.Add(ov.CellOfNode(l, fresh.NodeB));
        }

        long expectedRows = 0;
        foreach (int gc in cells)
        {
            expectedRows += ov.EntryCount[gc];
        }

        _out.WriteLine($"wave rows after BuildRoad under load: {ov.CustomizeRows - rowsBefore}, expected {expectedRows}");
        Assert.Equal(expectedRows, ov.CustomizeRows - rowsBefore);
        Assert.True(OverlayFixture.AssertMatricesMatchReference(ov, g) > 0);
    }

    [Fact]
    public void BuildRoad_UntilArraysOverflow_DisablesOverlay_CommandsStillApplied()
    {
        ScenarioDesc d = Line();
        using Simulation sim = Simulation.Create(in d);
        Overlay ov = sim.World.Overlay!;
        int applied = 0;
        for (int k = 0; k < 200 && !ov.Disabled; k++)
        {
            Road(sim, 6f * (k + 4), (uint)k); // шаг 6 м больше радиуса прилипания узлов (5 м)
            sim.Step(1);
            Assert.Equal(0, sim.Counters.CommandsRejected);
            applied = (int)sim.Counters.CommandsApplied;
        }

        _out.WriteLine($"applied {applied} commands, rebuilds {ov.Rebuilds}, disabled {ov.Disabled}");
        Assert.True(ov.Disabled, "массивы оверлея исчерпаны: он должен выключиться");
        Assert.Equal(1, sim.Counters.OverlayDisabled);
        Assert.True(ov.Rebuilds >= 1);

        // Выключенный оверлей не мешает: мир продолжает работать, новые команды применяются (старая маршрутизация).
        Road(sim, 2000f, 999);
        sim.Step(5);
        Assert.Equal(0, sim.Counters.CommandsRejected);
        Assert.Equal(applied + 1, (int)sim.Counters.CommandsApplied);
    }

    // Волна в составе мира: период пересчёта весов, откладывание нового пересчёта до конца волны, воспроизводимость.
    private static ScenarioDesc City(int recalcTicks, int customizeBudget, int vehicles)
    {
        ScenarioDesc d = Worlds.CityWorldTests.Desc("s1k");
        d.Demand.TargetVehicles = vehicles;
        d.Demand.SpawnRampTicks = 100;
        d.Demand.TypeMix = new[] { 1f, 0f };
        d.Capacities.MaxRoutes = 4096;
        d.Capacities.MaxPendingTrips = 2048;
        d.Routing = OverlayFixture.Desc(2, 1000f, customizeBudget: customizeBudget);
        d.Routing.PathfindBudgetPerTick = 32;
        d.Routing.CostRecalcTicks = recalcTicks;
        return d;
    }

    [Fact]
    public void WaveRunsInWorld_RecalcWaitsForWave_AndHashesMatch()
    {
        ulong[] Run(out long rows, out long lag, out long recalcs)
        {
            ScenarioDesc d = City(recalcTicks: 60, customizeBudget: 200, vehicles: 400);
            using Simulation sim = Simulation.Create(in d);
            var hashes = new ulong[10];
            for (int i = 0; i < hashes.Length; i++)
            {
                for (int t = 0; t < 100; t++)
                {
                    // Искусственная нагрузка перед тиком пересчёта (MetricsPhase идёт позже RoutePhase): гарантирует грязные ячейки.
                    if (sim.World.Tick % 60 == 0)
                    {
                        var loads = sim.World.SegmentLoads;
                        for (int s = 0; s < sim.World.Net.SegmentCount; s++)
                        {
                            loads[s].SpeedRatio = ((sim.World.Tick / 60) + s) % 3 == 0 ? 0.3f : 1f;
                        }
                    }

                    sim.Step(1);
                }

                hashes[i] = sim.ComputeStateHash();
            }

            rows = sim.Counters.CustomizeRows;
            lag = sim.Counters.CustomizeLagTicks;
            recalcs = sim.Counters.CostRecalcs;
            Assert.Equal(0, sim.Counters.OverlayDisabled);
            return hashes;
        }

        ulong[] a = Run(out long rows, out long lag, out long recalcs);
        ulong[] b = Run(out long rows2, out long lag2, out long recalcs2);
        _out.WriteLine($"rows {rows}, lag ticks {lag}, recalcs {recalcs}");
        Assert.Equal(a, b);
        Assert.Equal(rows, rows2);
        Assert.Equal(lag, lag2);
        Assert.Equal(recalcs, recalcs2);
        Assert.True(rows > 100, "волна должна работать");
        Assert.True(lag > 0, "бюджет 200 релаксаций на тик: волна длиннее периода пересчёта, новый пересчёт откладывается");
        Assert.True(recalcs >= 3);
    }

    // 0 B/тик (routing.md 9.8) при активной волне в составе мира.
    [Fact]
    public void Step_WithActiveWave_DoesNotAllocate()
    {
        ScenarioDesc d = City(recalcTicks: 20, customizeBudget: 300, vehicles: 300);
        using Simulation sim = Simulation.Create(in d);
        sim.Step(400); // прогрев: рампа спавна, очереди, прогрев JIT
        long rowsBefore = sim.Counters.CustomizeRows;
        long bytes = AllocationProbe.Measure(() =>
        {
            for (int t = 0; t < 60; t++)
            {
                if (sim.World.Tick % 20 == 0)
                {
                    var loads = sim.World.SegmentLoads;
                    for (int s = 0; s < sim.World.Net.SegmentCount; s++)
                    {
                        loads[s].SpeedRatio = ((sim.World.Tick / 20) + s) % 2 == 0 ? 0.3f : 1f;
                    }
                }

                sim.Step(1);
            }
        });
        _out.WriteLine($"wave rows during probe: {sim.Counters.CustomizeRows - rowsBefore}");
        Assert.True(sim.Counters.CustomizeRows > rowsBefore + 50, "в окне замера волна должна идти");
        Assert.Equal(0, bytes);
    }
}
