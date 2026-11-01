using System;
using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// Жизнь здания, часть 1 (building-lifecycle.md §2-§2.6, §5, §7 тесты 1-3 и 10; задача BL1): износ и потолок, пороги (выработка, жалоба один раз, переезд, заброшенность),
/// заброшенное стоит, повторное заселение, рассыпание с очисткой, снос игроком, без режима - прежние хэши, сейв с режимом.
/// </summary>
public sealed class LifecycleTests
{
    private static Simulation Make(in ScenarioDesc d) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });

    private static ScenarioDesc Desc(bool life = true)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json")).Desc;
        d.Town.Economy.CityBudget = true;
        d.Town.Residents.TripExecution = TripExecution.Aggregate;
        if (life)
        {
            d.Town.Lifecycle = CityLifecycleDesc.Default();
        }

        return d;
    }

    private static int Find(CityWorld c, LotZone kind, bool occupied = true)
    {
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind == kind && (!occupied || b.Occupants[i] > 0))
            {
                return i;
            }
        }

        Assert.Fail($"нет активного здания {kind}");
        return -1;
    }

    private static bool Day(Simulation sim, int bi)
    {
        CityWorld c = sim.World.City!;
        return CityLife.Day(sim.World, c, bi, in c.Types.Rows[c.Buildings.Type[bi]]);
    }

    // ---------------------------------------------------------------- тест 1: износ и пороги

    [Fact]
    public void NewBuildings_AreFull_AndBornAtStart()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        int n = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i))
            {
                Assert.Equal(CityLife.Full, b.Cond[i]);
                Assert.Equal(CityLife.Full, b.Ceil[i]);
                Assert.Equal(0, b.BornDay[i]);
                n++;
            }
        }

        Assert.True(n > 0);
    }

    [Fact]
    public void Wear_FollowsFormula_ByType_Load_AndCeiling()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        foreach (LotZone kind in new[] { LotZone.Residential, LotZone.Industrial })
        {
            int bi = Find(c, kind);
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
            Assert.True(t.DecayBpDay > 0 && t.CeilDropBpDay > 0);

            // Загрузка: ниже 90 % - 1.0, на полной вместимости - 1.5 (Q8 384).
            int occ = b.Occupants[bi];
            b.Occupants[bi] = t.Capacity * 9 / 10;
            Assert.Equal(256, CityLife.LoadQ8(c, bi, in t));
            b.Occupants[bi] = t.Capacity;
            Assert.Equal(384, CityLife.LoadQ8(c, bi, in t));
            b.Occupants[bi] = occ;

            int load = CityLife.LoadQ8(c, bi, in t);
            Assert.False(CityLife.IsLowland(sim.World, c, b.Lot[bi]));   // без рельефа низины нет
            int before = b.Cond[bi];
            int ceil = b.Ceil[bi] - t.CeilDropBpDay;
            long drop = ((long)t.DecayBpDay * load * CityLife.SiteQ8) >> 16;
            Assert.False(Day(sim, bi));
            Assert.Equal(ceil, b.Ceil[bi]);
            Assert.Equal((int)Math.Min(before - drop, ceil), b.Cond[bi]);
            Assert.True(b.Cond[bi] <= b.Ceil[bi]);
        }

        // Плотный тип изнашивается быстрее (дефолты §2.2), I - быстрее R той же плотности.
        BuildingTypeDesc.LifeDefaults(LotZone.Residential, 0, out ushort rLow, out _, out _);
        BuildingTypeDesc.LifeDefaults(LotZone.Residential, 1, out ushort rHigh, out _, out _);
        BuildingTypeDesc.LifeDefaults(LotZone.Industrial, 0, out ushort iLow, out _, out _);
        Assert.True(rHigh > rLow && iLow > rLow);
    }

    [Fact]
    public void Ceiling_CapsCondition()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Residential);
        b.Ceil[bi] = 9_500;
        b.Cond[bi] = 9_500;
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        for (int d = 0; d < 5; d++)
        {
            Day(sim, bi);
            Assert.True(b.Cond[bi] <= b.Ceil[bi]);
        }

        Assert.Equal(9_500 - (5 * t.CeilDropBpDay), b.Ceil[bi]);
    }

    [Fact]
    public void Lowland_WearsFaster()
    {
        ScenarioDesc d = Desc();
        d.Terrain = new TerrainDesc { Enabled = true, Source = TerrainSource.Flat, CellMm = 8000, MarginM = 200f };
        using Simulation sim = Make(d);
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Residential);
        int lot = b.Lot[bi];
        Assert.False(CityLife.IsLowland(w, c, lot));   // сухой ровный рельеф

        Sim.Core.Terrain.TerrainStore ter = w.Terrain!;
        int center = ter.NearestSample(c.Lots.X[lot], c.Lots.Z[lot]);
        int wet = center + 2 < ter.SamplesX * ter.SamplesZ ? center + 2 : center - 2;   // отсчёт в радиусе 2
        ter.Water[wet] = (ushort)(ter.Ground[wet] + 50);
        Assert.True(CityLife.IsLowland(w, c, lot));

        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        int load = CityLife.LoadQ8(c, bi, in t);
        int before = b.Cond[bi];
        Day(sim, bi);
        long drop = ((long)t.DecayBpDay * load * CityLife.LowlandSiteQ8) >> 16;
        Assert.Equal((int)Math.Min(before - drop, b.Ceil[bi]), b.Cond[bi]);
        Assert.True(drop > (((long)t.DecayBpDay * load * CityLife.SiteQ8) >> 16));
    }

    [Fact]
    public void Thresholds_Output_Complaint_Relocate_Abandon()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        int ci = Find(c, LotZone.Industrial);
        b.Cond[ci] = 9_000;
        Assert.Equal(100, CityLife.OutPct(c, ci));
        b.Cond[ci] = 8_999;
        Assert.Equal(95, CityLife.OutPct(c, ci));
        b.Cond[ci] = 6_999;
        Assert.Equal(80, CityLife.OutPct(c, ci));

        // Жалоба - один раз при пересечении 90 % вниз; снова только после возврата в норму.
        int ri = Find(c, LotZone.Residential);
        b.Cond[ri] = 9_000;
        b.Ceil[ri] = 10_000;
        int gen = b.Pool.GenerationOf(ri);
        Assert.Equal(0, c.Kpi.Complaints.Pending(ComplaintKind.BuildingWorn, ri, gen));
        Day(sim, ri);
        Assert.True(b.Cond[ri] < CityLife.NormBp);
        Assert.Equal(Sim.Core.Metrics.Complaints.MinCount, c.Kpi.Complaints.Pending(ComplaintKind.BuildingWorn, ri, gen));
        Day(sim, ri);
        Assert.Equal(Sim.Core.Metrics.Complaints.MinCount, c.Kpi.Complaints.Pending(ComplaintKind.BuildingWorn, ri, gen));

        // 60-70 %: случаи K4 по Occupants/4, без переезда; 50-60 % - полоса переезда.
        b.Cond[ri] = 6_900;
        long inc = c.Kpi.Cur[0].IncBuildingWorn;
        Day(sim, ri);
        Assert.Equal(inc + (b.Occupants[ri] / 4), c.Kpi.Cur[0].IncBuildingWorn);
        Assert.False(CityLife.InRelocateBand(c, ri));
        b.Cond[ri] = 5_500;
        Assert.True(CityLife.InRelocateBand(c, ri));

        // < 50 %: не функционирует - заброшено, ремонту не подлежит.
        b.Cond[ri] = 5_000;
        Assert.True(Day(sim, ri));
        Assert.Equal((byte)BuildingState.Abandoned, b.State[ri]);
        Assert.True(b.Cond[ri] < CityLife.AbandonBp);
    }

    // ---------------------------------------------------------------- тест 2: заброшка, заселение, рассыпание

    [Fact]
    public void Abandoned_Stands_WithoutVacateDays_ThenResettles()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Industrial);
        int gen = b.Pool.GenerationOf(bi);
        Growth.Abandon(w, c, bi);
        Assert.Equal(0, b.Occupants[bi]);

        // Пустое заброшенное без режима удалилось бы в тот же суточный шаг; с режимом стоит и изнашивается вдвое быстрее.
        w.Tick += (long)(c.Cfg.Residents.VacateDays + 1) * c.DayTicks;
        int before = b.Cond[bi];
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        Assert.True(Day(sim, bi));
        long drop = ((long)t.DecayBpDay * (256 * 2) * CityLife.SiteQ8) >> 16;
        Assert.Equal((int)Math.Min(before - drop, b.Ceil[bi]), b.Cond[bi]);
        Growth.Daily(w, c);
        Assert.True(b.IsAlive(bi) && b.Pool.GenerationOf(bi) == gen);

        // Повторное заселение при Cond >= 70 %: Active без стройки, фирма со стартовым счётом, места открыты.
        Assert.True(CityLife.Resettable(c, bi, t.Kind));
        long resettled = w.Counters.BuildingsResettled;
        CityLife.Resettle(w, c, bi);
        Assert.Equal((byte)BuildingState.Active, b.State[bi]);
        Assert.Equal(t.Capacity, b.Jobs[bi]);
        Assert.Equal(c.Cfg.Economy.InitialFirmBalance, b.Account[bi]);
        Assert.Equal(resettled + 1, w.Counters.BuildingsResettled);

        // Ниже 70 % - не заселяется.
        Growth.Abandon(w, c, bi);
        b.Cond[bi] = CityLife.MinorBp - 1;
        Assert.False(CityLife.Resettable(c, bi, t.Kind));
    }

    [Fact]
    public void Crumbled_IsRemoved_WithRubbleCost_AndLotFreed()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Commercial, occupied: false);
        int lot = b.Lot[bi];
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        Growth.Abandon(w, c, bi);
        b.Cond[bi] = 1;
        b.Ceil[bi] = 1;
        Day(sim, bi);
        Assert.Equal(0, b.Cond[bi]);
        Assert.True(CityLife.ToRemove(c, bi));

        long city = c.Ledger.City;
        long crumbled = w.Counters.BuildingsCrumbled;
        Growth.Daily(w, c);
        Assert.False(b.IsAlive(bi));
        int now = c.Lots.Building[lot];
        Assert.True(now < 0 || (b.IsAlive(now) && b.State[now] == (byte)BuildingState.Constructing));   // участок свободен или рост уже ставит новое
        Assert.Equal(crumbled + 1, w.Counters.BuildingsCrumbled);
        long rubble = (long)t.DemolishCost * c.Cfg.Lifecycle.RubbleCostPct / 100;
        Assert.True(rubble > 0);
        Assert.True(city - c.Ledger.City >= rubble);   // очистка из казны (рост в тот же шаг может тратить ещё)
    }

    // ---------------------------------------------------------------- тест 3: снос игроком

    [Fact]
    public void Demolish_PaysCost_FlagsAndRemovesSafely()
    {
        using Simulation sim = Make(Desc());
        sim.Step(10);
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Residential);
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        Command cmd = Command.MakeDemolishBuilding(new EntityRef(bi, b.Pool.GenerationOf(bi)), 1);
        Assert.Equal(RejectReason.None, sim.Preview(in cmd, out PreviewResult pr, new PreviewIssue[4]));
        Assert.Equal(t.DemolishCost, pr.Cost);

        long city = c.Ledger.City;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
        Assert.Equal((byte)BuildingState.Abandoned, b.State[bi]);
        Assert.Equal(BuildingRenderFlags.Demolishing, CityLife.RenderFlags(c, bi));
        Assert.True(city - c.Ledger.City >= t.DemolishCost);
        Assert.Equal(1, w.Counters.BuildingsDemolished);

        // Повтор - InvalidArgument; устаревший handle - InvalidHandle.
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeDemolishBuilding(new EntityRef(bi, b.Pool.GenerationOf(bi)), 2)));
        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, Command.MakeDemolishBuilding(new EntityRef(bi, b.Pool.GenerationOf(bi) + 1), 3)));

        // Удаление - на суточном шаге, когда жильцы уехали и ни один тур/нога не ведёт к зданию (прежние проверки ссылок).
        for (int day = 0; day < 6 && b.IsAlive(bi); day++)
        {
            sim.Step(c.DayTicks);
        }

        Assert.False(b.IsAlive(bi));
        Assert.Equal(0, w.Counters.BuildingsCrumbled);   // снос не платит очистку
        CityResidents res = c.Residents;
        for (int r = 0; r < res.HighWater; r++)
        {
            Assert.True(!res.Pool.IsAlive(r) || res.Home[r] != bi || b.IsAlive(bi));
        }
    }

    [Fact]
    public void Demolish_WithoutMode_NotSupported()
    {
        using Simulation sim = Make(Desc(life: false));
        CityWorld c = sim.World.City!;
        int bi = Find(c, LotZone.Residential);
        Command cmd = Command.MakeDemolishBuilding(new EntityRef(bi, c.Buildings.Pool.GenerationOf(bi)), 1);
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(sim, cmd));
    }

    // ---------------------------------------------------------------- тест 10: без режима - прежнее; сейв и снапшот с режимом

    [Fact]
    public void WithoutMode_FieldsZero_SnapshotUnchanged()
    {
        using Simulation sim = Make(Desc(life: false));
        sim.Step(200);
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i++)
        {
            Assert.True(b.Cond[i] == 0 && b.Ceil[i] == 0 && b.BornDay[i] == 0 && b.LifeFlags[i] == 0);
        }

        var snap = new BuildingRenderData[b.HighWater];
        sim.GetBuildings(snap);
        foreach (BuildingRenderData r in snap)
        {
            Assert.True(r.R0 == 0 && r.R1 == 0);
        }
    }

    [Fact]
    public void Mode_ChangesHash_OnlyThroughLifecycleSections()
    {
        using Simulation off = Make(Desc(life: false));
        using Simulation on = Make(Desc());
        Assert.NotEqual(off.ComputeStateHash(), on.ComputeStateHash());
        var snap = new BuildingRenderData[on.World.City!.Buildings.HighWater];
        on.GetBuildings(snap);
        int i = Find(on.World.City!, LotZone.Residential);
        Assert.Equal(100, snap[i].R0);
    }

    [Fact]
    public void Save_RoundTrip_WithMode()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Find(c, LotZone.Residential);
        c.Buildings.Cond[bi] = 7_123;
        c.Buildings.Ceil[bi] = 8_000;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeDemolishBuilding(new EntityRef(bi, c.Buildings.Pool.GenerationOf(bi)), 1)));

        byte[] raw = Sim.Core.Tests.Save.WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = Sim.Core.Tests.Save.WorldSaveFixtures.Load(raw);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        BuildingStore lb = loaded.World.City!.Buildings;
        Assert.Equal(7_123, lb.Cond[bi]);
        Assert.Equal(8_000, lb.Ceil[bi]);
        Assert.Equal(BuildingRenderFlags.Demolishing, CityLife.RenderFlags(loaded.World.City!, bi));
        Assert.True(loaded.World.Town.Lifecycle.Enabled);
        Assert.Equal(1, loaded.World.Counters.BuildingsDemolished);
        sim.Step(100);
        loaded.Step(100);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void Mode_ZeroAllocations_AcrossDayBoundary()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        sim.Step(c.DayTicks + 10);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeDemolishBuilding(new EntityRef(Find(c, LotZone.Residential), c.Buildings.Pool.GenerationOf(Find(c, LotZone.Residential))), 1)));
        long bytes = Fixtures.AllocationProbe.Measure(() => sim.Step(c.DayTicks));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void Validate_RejectsBadParams()
    {
        ScenarioDesc d = Desc();
        d.Town.Lifecycle.RubbleCostPct = 101;
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in d));
        d = Desc();
        d.Town.Economy.CityBudget = false;
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in d));
        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(Desc()));
    }

    [Fact]
    public void Loader_ReadsLifecycleSection()
    {
        string dir = Path.Combine(GenFixture.RepoRoot(), "scenarios");
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "s1k-res-econ.json")))!;
        root["city"]!["lifecycle"] = new JsonObject { ["abandoned_decay_pct"] = 300, ["lowland_m"] = 1.5, ["worn_relocate"] = 0.1, ["rubble_cost_pct"] = 20, ["cell_m"] = 64, ["sig_shift"] = 5, ["heritage_loss_radius_m"] = 250, ["lv_blight"] = 7 };
        LoadedScenario sc = ScenarioLoader.Parse(root.ToJsonString(), p => File.ReadAllText(Path.Combine(dir, p)), "bl1");
        CityLifecycleDesc l = sc.Desc.Town.Lifecycle;
        Assert.True(l.Enabled);
        Assert.Equal(300, l.AbandonedDecayPct);
        Assert.Equal(150, l.LowlandCm);
        Assert.Equal(CityDesc.Q16(0.1), l.WornRelocateQ16);
        Assert.Equal(20, l.RubbleCostPct);
        Assert.Equal((64, 5, 250, 7), (l.CellM, l.SigShift, l.HeritageLossRadiusM, l.LvBlightPermille));
        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(sc.Desc));
    }
}
