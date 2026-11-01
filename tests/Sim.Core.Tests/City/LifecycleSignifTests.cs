using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// Жизнь здания, часть 2 (building-lifecycle.md §2.4-§2.6, §7 тест 4; задача BL2): прибытия, накопитель и уровни значимости, поля квартала (стадии вреда H1-H3, окно 3×3, двойной буфер),
/// земля участка, переезд и выработка по полям, разовое недовольство при сносе значимого, сейв и детерминизм при 1/2/3 потоках.
/// </summary>
public sealed class LifecycleSignifTests
{
    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static ScenarioDesc Desc(TripExecution exec = TripExecution.Aggregate)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json")).Desc;
        d.Town.Economy.CityBudget = true;
        d.Town.Residents.TripExecution = exec;
        d.Town.Lifecycle = CityLifecycleDesc.Default();
        return d;
    }

    private static int Find(CityWorld c, LotZone kind, int skip = 0, bool occupied = true)
    {
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind == kind && (!occupied || b.Occupants[i] > 0) && skip-- == 0)
            {
                return i;
            }
        }

        Assert.Fail($"нет активного здания {kind}");
        return -1;
    }

    private static int CellOf(CityWorld c, int bi)
    {
        int lot = c.Buildings.Lot[bi];
        return c.Fields!.CellOf(c.Lots.X[lot], c.Lots.Z[lot]);
    }

    /// <summary>Суточный шаг одного здания как в проходе: износ/пороги, затем значимость и вклад в поля.</summary>
    private static void Pass(Simulation sim, int bi)
    {
        CityWorld c = sim.World.City!;
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[c.Buildings.Type[bi]];
        CityLife.Day(sim.World, c, bi, in t);
        CityLife.Finish(c, bi, in t);
    }

    // ---------------------------------------------------------------- прибытия

    [Theory]
    [InlineData(TripExecution.Aggregate)]
    [InlineData(TripExecution.Agents)]
    public void Arrivals_CountEveryLeg_InBothExecutors(TripExecution exec)
    {
        using Simulation sim = Make(Desc(exec));
        CityWorld c = sim.World.City!;
        sim.Step(c.DayTicks / 2);   // первые сутки: суточного прохода нет, слот 0 копит всё
        long done = sim.World.Counters.ResLegsDone;
        Assert.True(done > 0);
        Assert.Null(c.Outside);
        long sum = 0;
        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            sum += c.Buildings.ArrivalsDay[2 * i] + c.Buildings.ArrivalsDay[(2 * i) + 1];
        }

        Assert.Equal(done, sum);   // одна точка CompleteLeg: каждая закрытая нога - ровно одно прибытие в здание-цель (дом тоже)
    }

    [Fact]
    public void WithoutMode_NoArrivalsNoSignif()
    {
        ScenarioDesc d = Desc();
        d.Town.Lifecycle = default;
        using Simulation sim = Make(d);
        CityWorld c = sim.World.City!;
        Assert.Null(c.Fields);
        sim.Step(c.DayTicks + c.DayTicks / 2);
        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            Assert.True(c.Buildings.Signif[i] == 0 && c.Buildings.ArrivalsDay[2 * i] == 0 && c.Buildings.ArrivalsDay[(2 * i) + 1] == 0);
        }
    }

    // ---------------------------------------------------------------- накопитель и уровни

    [Fact]
    public void Levels_FollowThresholds()
    {
        Assert.Equal(0, CityLife.Level(0));
        Assert.Equal(0, CityLife.Level(1_279));
        Assert.Equal(1, CityLife.Level(1_280));
        Assert.Equal(2, CityLife.Level(6_400));
        Assert.Equal(3, CityLife.Level(19_200));
        Assert.Equal(4, CityLife.Level(38_400));
        Assert.Equal(4, CityLife.Level(63_999));
        Assert.Equal(5, CityLife.Level(64_000));
    }

    [Fact]
    public void Accumulator_LeaksTowardSixtyFourTimesArrivals_AndCaps()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Commercial, occupied: false);
        int yslot = Economy.Slot(bi, (c.Day + 1) & 1);

        b.ArrivalsDay[yslot] = 100;
        Pass(sim, bi);
        Assert.Equal(100, b.Signif[bi]);   // 0 + 100 - (0 >> 6)
        Pass(sim, bi);
        Assert.Equal(100 + 100 - (100 >> 6), b.Signif[bi]);

        // Стационар 20 прибытий в сутки -> уровень 1 (1 280), 100 -> уровень 2 (6 400).
        foreach ((int a, int level) in new[] { (20, 1), (100, 2), (300, 3) })
        {
            b.Signif[bi] = 0;
            for (int day = 0; day < 1_500; day++)
            {
                b.Cond[bi] = CityLife.Full;
                b.Ceil[bi] = CityLife.Full;
                b.ArrivalsDay[yslot] = a;
                Pass(sim, bi);
            }

            Assert.Equal(level, CityLife.Level(b.Signif[bi]));
            Assert.Equal(a * 64, b.Signif[bi]);
        }

        // Потолок суточных прибытий 4 000 -> стационар 256 000, уровень 5.
        b.Signif[bi] = 0;
        for (int day = 0; day < 1_500; day++)
        {
            b.Cond[bi] = CityLife.Full;
            b.Ceil[bi] = CityLife.Full;
            b.ArrivalsDay[yslot] = 1_000_000;
            Pass(sim, bi);
        }

        Assert.Equal(4_000 * 64, b.Signif[bi]);
        Assert.Equal(5, CityLife.Level(b.Signif[bi]));

        // Мёртвое здание теряет значимость: без прибытий накопитель затухает.
        int before = b.Signif[bi];
        b.ArrivalsDay[yslot] = 0;
        Pass(sim, bi);
        Assert.Equal(before - (before >> 6), b.Signif[bi]);
    }

    // ---------------------------------------------------------------- поля квартала

    [Fact]
    public void Fields_HeritageAndHarmStages()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        NeighborhoodFields f = c.Fields!;

        // Наследие: Σ уровней активных; вклад копится в Next и действует только после обмена.
        int h = Find(c, LotZone.Commercial, occupied: false);
        b.Signif[h] = 19_700;   // после шага (-307) останется >= 19 200: уровень 3
        Pass(sim, h);
        int hc = CellOf(c, h);
        Assert.Equal(0, f.Value(hc, NeighborhoodFields.Heritage));
        Assert.Equal(3, f.Next[(hc * NeighborhoodFields.Stride) + NeighborhoodFields.Heritage]);
        f.Swap();
        Assert.Equal(3, f.Value(hc, NeighborhoodFields.Heritage));
        Assert.Equal(0, f.Next[(hc * NeighborhoodFields.Stride) + NeighborhoodFields.Heritage]);
        Assert.Equal(6, f.Window(hc, NeighborhoodFields.Heritage));   // вес 2 у своей клетки

        // Соседняя клетка видит её с весом 1.
        int nb = f.Nx > 1 && (hc % f.Nx) + 1 < f.Nx ? hc + 1 : hc - 1;
        Assert.Equal(3, f.Window(nb, NeighborhoodFields.Heritage));
        f.Cur.AsSpan().Clear();

        // Стадии вреда заброшки: очки = вес · ceil(Capacity / 8).
        foreach ((int cond, int blight, int disorder, int fire) in new[] { (5_000, 2, 0, 0), (3_000, 4, 4, 0), (1_000, 6, 6, 6) })
        {
            int a = Find(c, LotZone.Residential, skip: 3);
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[a]];
            int size = (t.Capacity + 7) / 8;
            Growth.Abandon(w, c, a);
            b.Cond[a] = (ushort)cond;
            b.Ceil[a] = CityLife.Full;
            Array.Clear(f.Next);
            Pass(sim, a);
            int cell = CellOf(c, a);
            int s = cell * NeighborhoodFields.Stride;
            Assert.Equal(blight * size, f.Next[s + NeighborhoodFields.Blight]);
            Assert.Equal(disorder * size, f.Next[s + NeighborhoodFields.Disorder]);
            Assert.Equal(fire * size, f.Next[s + NeighborhoodFields.FireRisk]);
        }

        // Снос заказан - вред не копится.
        int d = Find(c, LotZone.Residential, skip: 4);
        Growth.Abandon(w, c, d);
        b.LifeFlags[d] |= BuildingLifeFlags.Demolishing;
        b.Cond[d] = 3_000;
        Array.Clear(f.Next);
        Pass(sim, d);
        Assert.All(f.Next, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Fields_DoubleBufferEqualsRecompute()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;

        // Разнообразие вкладов: значимые активные и заброшка всех стадий.
        for (int k = 0; k < 6; k++)
        {
            b.Signif[Find(c, LotZone.Residential, skip: k)] = 1_300 + (k * 12_000);
        }

        ushort[] conds = { 5_000, 3_000, 1_000, 8_000 };
        for (int k = 0; k < conds.Length; k++)
        {
            int a = Find(c, LotZone.Residential, skip: 8 + k);
            Growth.Abandon(w, c, a);
            b.Cond[a] = conds[k];
        }

        sim.Step(c.DayTicks + (c.DayTicks / 2));   // суточный проход суток 1 завершён, обмен буферов выполнен
        Assert.True(c.Day >= 1);
        var expect = new int[c.Fields!.Cur.Length];
        CityLife.RecomputeFields(c, expect);
        Assert.Equal(expect, c.Fields.Cur);
        Assert.Contains(expect, v => v > 0);
    }

    [Fact]
    public void LandValue_ReactsToHeritageAndBlight()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        NeighborhoodFields f = c.Fields!;
        int bi = Find(c, LotZone.Residential);
        int lot = c.Buildings.Lot[bi];
        c.Budget!.LandValue[c.Lots.Cell[lot]] = 1_000;
        int cell = CellOf(c, bi);
        int s = cell * NeighborhoodFields.Stride;

        Assert.Equal(1_000, BudgetFlows.LandAt(c, c.Budget, lot));
        f.Cur[s + NeighborhoodFields.Heritage] = 5;   // h = 10 -> +100 ‰
        Assert.Equal(1_100, BudgetFlows.LandAt(c, c.Budget, lot));
        f.Cur[s + NeighborhoodFields.Heritage] = 500;   // потолок +200 ‰
        Assert.Equal(1_200, BudgetFlows.LandAt(c, c.Budget, lot));
        f.Cur[s + NeighborhoodFields.Heritage] = 0;
        f.Cur[s + NeighborhoodFields.Blight] = 10;   // b = 20, 5 ‰ на очко -> -100 ‰
        Assert.Equal(900, BudgetFlows.LandAt(c, c.Budget, lot));
        f.Cur[s + NeighborhoodFields.Blight] = 10_000;   // потолок -400 ‰
        Assert.Equal(600, BudgetFlows.LandAt(c, c.Budget, lot));
    }

    [Fact]
    public void WithoutMode_LandIsPlainLv()
    {
        ScenarioDesc d = Desc();
        d.Town.Lifecycle = default;
        using Simulation sim = Make(d);
        CityWorld c = sim.World.City!;
        int lot = c.Buildings.Lot[Find(c, LotZone.Residential)];
        c.Budget!.LandValue[c.Lots.Cell[lot]] = 777;
        Assert.Equal(777, BudgetFlows.LandAt(c, c.Budget, lot));
        Assert.Equal(1_000, CityLife.RelocateThreshold(c, 1_000, 0));
    }

    [Fact]
    public void Relocation_AndOutput_FollowFields()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        NeighborhoodFields f = c.Fields!;
        int home = Find(c, LotZone.Residential);
        int cell = CellOf(c, home);
        int s = cell * NeighborhoodFields.Stride;
        Assert.Equal(1_000, CityLife.RelocateThreshold(c, 1_000, home));

        f.Cur[s + NeighborhoodFields.Heritage] = 5;   // h = 10 -> 20 % меньше
        Assert.Equal(800, CityLife.RelocateThreshold(c, 1_000, home));
        f.Cur[s + NeighborhoodFields.Heritage] = 500;   // не больше 50 %
        Assert.Equal(500, CityLife.RelocateThreshold(c, 1_000, home));
        f.Cur[s + NeighborhoodFields.Disorder] = 1;   // x2
        Assert.Equal(1_000, CityLife.RelocateThreshold(c, 1_000, home));
        f.Cur[s + NeighborhoodFields.Heritage] = 0;
        Assert.Equal(2_000, CityLife.RelocateThreshold(c, 1_000, home));

        // Беспорядок в клетке фирмы: выработка -10 п.п.
        int firm = Find(c, LotZone.Industrial);
        int fs = CellOf(c, firm) * NeighborhoodFields.Stride;
        Assert.Equal(100, CityLife.OutPct(c, firm));
        f.Cur[fs + NeighborhoodFields.Disorder] = 4;
        Assert.Equal(90, CityLife.OutPct(c, firm));
        c.Buildings.Cond[firm] = 8_000;
        Assert.Equal(85, CityLife.OutPct(c, firm));
    }

    // ---------------------------------------------------------------- снос значимого

    [Fact]
    public void DemolishSignificant_RaisesIncidentsInRadius()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        int target = Find(c, LotZone.Commercial, occupied: false);
        b.Signif[target] = 19_200;   // уровень 3
        int lv = CityLife.Level(b.Signif[target]);
        Assert.Equal(3, lv);

        int tl = b.Lot[target];
        float r = c.Cfg.Lifecycle.HeritageLossRadiusM;
        long expect = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (i != target && b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind == LotZone.Residential && b.Occupants[i] > 0)
            {
                float dx = c.Lots.X[b.Lot[i]] - c.Lots.X[tl];
                float dz = c.Lots.Z[b.Lot[i]] - c.Lots.Z[tl];
                if ((dx * dx) + (dz * dz) <= r * r)
                {
                    expect += (long)b.Occupants[i] * lv / 2;
                }
            }
        }

        Assert.True(expect > 0);
        int gen = b.Pool.GenerationOf(target);
        long inc = c.Kpi.Cur[0].IncBuildingWorn;
        CityLife.Demolish(w, c, target);
        Assert.Equal(inc + expect, c.Kpi.Cur[0].IncBuildingWorn);
        Assert.Equal((int)expect, c.Kpi.Complaints.Pending(ComplaintKind.HeritageLost, target, gen));

        // Уровень ниже 2 - недовольства нет.
        int small = Find(c, LotZone.Commercial, skip: 1, occupied: false);
        b.Signif[small] = 6_399;
        long inc2 = c.Kpi.Cur[0].IncBuildingWorn;
        CityLife.Demolish(w, c, small);
        Assert.Equal(inc2, c.Kpi.Cur[0].IncBuildingWorn);
    }

    [Fact]
    public void Blight_ComplaintsAndIncidents_InHarmCells()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        NeighborhoodFields f = c.Fields!;
        int home = Find(c, LotZone.Residential);
        int gen = c.Buildings.Pool.GenerationOf(home);
        int s = CellOf(c, home) * NeighborhoodFields.Stride;
        int n = Math.Max(1, c.Buildings.Occupants[home] / 4);

        f.Cur[s + NeighborhoodFields.Disorder] = 4;   // H2: только жалоба
        long inc = c.Kpi.Cur[0].IncBuildingWorn;
        Pass(sim, home);
        Assert.Equal(n, c.Kpi.Complaints.Pending(ComplaintKind.Blight, home, gen));
        Assert.Equal(inc, c.Kpi.Cur[0].IncBuildingWorn);

        f.Cur[s + NeighborhoodFields.FireRisk] = 6;   // H3: ещё и случаи K4
        Pass(sim, home);
        Assert.Equal(inc + n, c.Kpi.Cur[0].IncBuildingWorn);
    }

    // ---------------------------------------------------------------- сейв и детерминизм

    [Fact]
    public void Save_RoundTrip_KeepsSignifArrivalsAndFields()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Find(c, LotZone.Residential);
        c.Buildings.Signif[bi] = 7_000;
        Growth.Abandon(w, c, Find(c, LotZone.Residential, skip: 5));
        sim.Step(c.DayTicks + 500);
        Assert.Contains(c.Fields!.Cur, v => v > 0);

        byte[] raw = Sim.Core.Tests.Save.WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = Sim.Core.Tests.Save.WorldSaveFixtures.Load(raw);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        CityWorld lc = loaded.World.City!;
        Assert.Equal(c.Fields.Cur, lc.Fields!.Cur);
        Assert.Equal(c.Fields.Next, lc.Fields.Next);
        Assert.Equal(c.Buildings.Signif, lc.Buildings.Signif);
        Assert.Equal(c.Buildings.ArrivalsDay, lc.Buildings.ArrivalsDay);
        sim.Step(c.DayTicks);
        loaded.Step(c.DayTicks);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void Hash_IsThreadIndependent_WithMode()
    {
        ScenarioDesc d = Desc(TripExecution.Agents);
        ulong h1;
        using (Simulation a = Make(d, 1))
        {
            a.Step(a.World.City!.DayTicks + (a.World.City.DayTicks / 4));
            h1 = a.ComputeStateHash();
            Assert.True(a.World.Counters.ResLegsDone > 0);
        }

        foreach (int threads in new[] { 2, 3 })
        {
            using Simulation s = Make(d, threads);
            s.Step(s.World.City!.DayTicks + (s.World.City.DayTicks / 4));
            Assert.Equal(h1, s.ComputeStateHash());
        }
    }

    [Fact]
    public void Mode_ZeroAllocations_WithFieldsAcrossDayBoundary()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        sim.Step(c.DayTicks + 10);
        Growth.Abandon(w, c, Find(c, LotZone.Residential, skip: 5));
        long bytes = Fixtures.AllocationProbe.Measure(() => sim.Step(c.DayTicks));
        Assert.Equal(0L, bytes);
    }
}
