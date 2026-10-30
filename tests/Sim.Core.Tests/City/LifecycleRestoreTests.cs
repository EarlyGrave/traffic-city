using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// Капремонт и чтение жизни здания (building-lifecycle.md §2.9, §6.2, §7 тесты 1-3; задача BL4): цена Capacity · restore_per_unit · (2 − Cond/10 000), отказ ниже 50 %,
/// заброшенное ≥ 50 % заселяется заново, поток Restoration (не фонд ЖКХ, не зависит от субсидии), API <c>TryGetBuildingLife</c> / <c>GetUtilityInfo</c>, детерминизм 1/2/3 потока, сейв, 0 Б/тик.
/// </summary>
public sealed class LifecycleRestoreTests
{
    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static ScenarioDesc Desc(TripExecution exec = TripExecution.Aggregate, bool life = true)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json")).Desc;
        d.Town.Economy.CityBudget = true;
        d.Town.Residents.TripExecution = exec;
        if (life)
        {
            d.Town.Lifecycle = CityLifecycleDesc.Default();
        }

        return d;
    }

    private static int Find(CityWorld c, LotZone kind, int skip = 0)
    {
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind == kind && skip-- == 0)
            {
                return i;
            }
        }

        Assert.Fail($"нет активного здания {kind}");
        return -1;
    }

    private static Command Restore(CityWorld c, int bi, uint tag = 1) => Command.MakeRestoreBuilding(new EntityRef(bi, c.Buildings.Pool.GenerationOf(bi)), tag);

    private static long Price(CityWorld c, int bi) => (long)c.Types.Rows[c.Buildings.Type[bi]].Capacity * c.Cfg.Lifecycle.RestorePerUnit * ((2 * CityLife.Full) - c.Buildings.Cond[bi]) / CityLife.Full;

    // ---------------------------------------------------------------- цена и применение

    [Fact]
    public void Restore_PaysFormula_FromTreasury_AsRestorationFlow_KeepsSignifAndAge()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Residential);
        int cap = c.Types.Rows[b.Type[bi]].Capacity;
        b.Cond[bi] = 6_000;
        b.Ceil[bi] = 8_000;
        b.Signif[bi] = 12_345;
        b.BornDay[bi] = -7;

        long expected = (long)cap * 150 * (20_000 - 6_000) / 10_000;   // 1.4 × база
        Assert.Equal(expected, Price(c, bi));
        Command cmd = Restore(c, bi);
        Assert.Equal(RejectReason.None, sim.Preview(in cmd, out PreviewResult pr, new PreviewIssue[4]));
        Assert.Equal(expected, pr.Cost);

        long city = c.Ledger.City;
        long fund = c.Ledger.Utility;
        long restoration = c.Ledger.FlowTotal[(int)FlowKind.Restoration];
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
        Assert.Equal(CityLife.Full, b.Cond[bi]);
        Assert.Equal(CityLife.Full, b.Ceil[bi]);
        Assert.Equal(12_345, b.Signif[bi]);
        Assert.Equal(-7, b.BornDay[bi]);
        Assert.Equal(expected, city - c.Ledger.City);
        Assert.Equal(fund, c.Ledger.Utility);
        Assert.Equal(expected, c.Ledger.FlowTotal[(int)FlowKind.Restoration] - restoration);
        Assert.Equal(1, w.Counters.Restorations);
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    [Fact]
    public void Price_RangesFromOneToTwoTimesBase_AndGrowsWithWear()
    {
        using Simulation sim = Make(Desc());
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Commercial);
        long baseCost = (long)c.Types.Rows[b.Type[bi]].Capacity * c.Cfg.Lifecycle.RestorePerUnit;
        b.Ceil[bi] = CityLife.Full;
        b.Cond[bi] = 9_999;
        Assert.True(Price(c, bi) >= baseCost);
        long prev = 0;
        foreach (ushort cond in new ushort[] { 9_000, 7_000, 5_500, 5_000 })
        {
            b.Cond[bi] = cond;
            long p = Price(c, bi);
            Assert.True(p > prev && p <= 2 * baseCost, $"cond {cond}: {p}");
            prev = p;
        }

        // Только Ceil ниже 100 % при Cond = 100 %: чинить нечего до потолка, но потолок поднимается - цена ровно 1× базы.
        b.Ceil[bi] = 8_000;
        b.Cond[bi] = CityLife.Full;
        Command cmd = Restore(c, bi);
        Assert.Equal(RejectReason.None, sim.Preview(in cmd, out PreviewResult pr, new PreviewIssue[4]));
        Assert.Equal(baseCost, pr.Cost);
    }

    // ---------------------------------------------------------------- отказы

    [Fact]
    public void Rejects_BelowHalf_Demolishing_Constructing_FullyNew_StaleHandle_NoMode()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Residential);
        long city = c.Ledger.City;

        b.Cond[bi] = 4_999;   // ремонту не подлежит
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Restore(c, bi, 1)));
        b.Cond[bi] = 5_000;   // граница включена
        Assert.Equal(RejectReason.None, sim.Preview(Restore(c, bi, 2), out _, new PreviewIssue[4]));

        b.Cond[bi] = CityLife.Full;   // нечего чинить
        b.Ceil[bi] = CityLife.Full;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Restore(c, bi, 3)));

        b.Cond[bi] = 7_000;
        b.LifeFlags[bi] |= BuildingLifeFlags.Demolishing;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Restore(c, bi, 4)));
        b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.Demolishing);

        byte st = b.State[bi];
        b.State[bi] = (byte)BuildingState.Constructing;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Restore(c, bi, 5)));
        b.State[bi] = st;

        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, Command.MakeRestoreBuilding(new EntityRef(bi, b.Pool.GenerationOf(bi) + 1), 6)));
        Assert.Equal(city, c.Ledger.City);
        Assert.Equal(0, sim.World.Counters.Restorations);

        using Simulation off = Make(Desc(life: false));
        CityWorld oc = off.World.City!;
        int ob = Find(oc, LotZone.Residential);
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(off, Command.MakeRestoreBuilding(new EntityRef(ob, oc.Buildings.Pool.GenerationOf(ob)), 1)));
    }

    [Fact]
    public void Abandoned_AtLeastHalf_RestoredAndResettled_BelowHalf_Rejected()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Commercial);
        Growth.Abandon(w, c, bi);
        Assert.Equal((byte)BuildingState.Abandoned, b.State[bi]);

        b.Cond[bi] = 4_000;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Restore(c, bi, 1)));
        Assert.Equal((byte)BuildingState.Abandoned, b.State[bi]);

        b.Cond[bi] = 6_000;   // выше 50 %, но ниже порога рынка 70 %: это и есть «третий путь»
        b.Ceil[bi] = 7_000;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Restore(c, bi, 2)));
        Assert.Equal((byte)BuildingState.Active, b.State[bi]);
        Assert.Equal(CityLife.Full, b.Cond[bi]);
        Assert.Equal(CityLife.Full, b.Ceil[bi]);
        Assert.Equal(1, w.Counters.BuildingsResettled);
        Assert.Equal(c.Types.Rows[b.Type[bi]].Capacity, b.Jobs[bi]);
        Assert.Equal(0L, c.Ledger.SumAll());

        // Участок потерян или зона участка сменилась - заброшенное не заселить.
        int second = Find(c, LotZone.Commercial, 1);
        Growth.Abandon(w, c, second);
        b.Cond[second] = 6_000;
        int lot = b.Lot[second];
        c.Lots.Flags[lot] |= LotFlags.Lost;
        Assert.Equal(RejectReason.ZoneRule, ToolsFixture.Run(sim, Restore(c, second, 3)));
        c.Lots.Flags[lot] &= unchecked((byte)~LotFlags.Lost);
        byte zone = c.Lots.Zone[lot];
        c.Lots.Zone[lot] = (byte)LotZone.Industrial;
        Assert.Equal(RejectReason.ZoneRule, ToolsFixture.Run(sim, Restore(c, second, 4)));
        c.Lots.Zone[lot] = zone;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Restore(c, second, 5)));
    }

    [Fact]
    public void Rejects_WhenTreasuryBeyondCreditLimit()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        CityWorld c = sim.World.City!;
        int bi = Find(c, LotZone.Residential);
        c.Buildings.Cond[bi] = 6_000;
        Ledger lg = c.Ledger;
        lg.Transfer(lg.CityAccount, lg.ExternalAccount, lg.City + 1_000_000_000L, FlowKind.Construction);
        Assert.Equal(RejectReason.InsufficientFunds, ToolsFixture.Run(sim, Restore(c, bi)));
        Assert.Equal(6_000, c.Buildings.Cond[bi]);
    }

    // ---------------------------------------------------------------- капремонт и ЖКХ: фонд и субсидия не участвуют

    [Fact]
    public void Price_IsIndependentOfUtilityFund_AndSubsidyFlag()
    {
        foreach (bool subsidy in new[] { false, true })
        {
            using Simulation sim = Make(Desc());
            sim.Step(20);
            CityWorld c = sim.World.City!;
            if (subsidy)
            {
                c.Budget!.Policy.Flags |= BudgetPolicyFlags.UtilitySubsidy;
            }

            int bi = Find(c, LotZone.Industrial);
            c.Buildings.Cond[bi] = 5_500;
            long expected = Price(c, bi);
            long city = c.Ledger.City;
            Assert.Equal(0L, c.Ledger.Utility);   // фонд пуст: ремонт ЖКХ ждал бы денег, капремонт - нет
            Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Restore(c, bi)));
            Assert.Equal(expected, city - c.Ledger.City);
            Assert.Equal(0L, c.Ledger.Utility);
            Assert.Equal(0L, c.Ledger.FlowTotal[(int)FlowKind.UtilitySubsidy]);
            Assert.Equal(expected, c.Ledger.FlowTotal[(int)FlowKind.Restoration]);
        }
    }

    [Fact]
    public void Restore_DropsPendingUtilityRequest_NoDoubleCharge()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Find(c, LotZone.Residential);
        c.Buildings.Ceil[bi] = CityLife.Full;
        c.Buildings.Cond[bi] = 7_000;
        Utilities.Request(w, c, bi);
        Assert.NotEqual(0, c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Requested);
        Assert.Equal(1, c.Utility!.Queue.Count);

        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Restore(c, bi)));
        sim.Step(200);
        Assert.Equal(0, c.Utility.Queue.Count);   // голова очереди снята как «уже в норме»
        Assert.Equal(0, c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Requested);
        Assert.Equal(0, w.Counters.RepairsDone);
        Assert.Equal(0L, c.Ledger.FlowTotal[(int)FlowKind.Repair]);
    }

    // ---------------------------------------------------------------- чтение API

    [Fact]
    public void BuildingLifeApi_ReportsFieldsAndPrices()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        int bi = Find(c, LotZone.Residential);
        int gen = b.Pool.GenerationOf(bi);
        b.Cond[bi] = 6_500;
        b.Ceil[bi] = 9_000;
        b.BornDay[bi] = c.Day - 11;
        b.Signif[bi] = 7_000;

        Assert.True(sim.TryGetBuildingLife(bi, gen, out BuildingLifeInfo li));
        Assert.Equal((6_500, 9_000, 11, 7_000), (li.CondBp, li.CeilBp, li.AgeDays, li.Signif));
        Assert.Equal(CityLife.Level(7_000), li.SignifLevel);
        Assert.Equal(RepairState.None, li.RepairState);
        Assert.Equal(1, li.CanRestore);
        Assert.Equal(Price(c, bi), li.RestorePrice);
        Assert.Equal(c.Types.Rows[b.Type[bi]].DemolishCost, li.DemolishPrice);
        Assert.Equal(Utilities.RepairCost(c, bi), li.RepairPrice);
        Assert.Equal(0, li.HarmStage);

        Utilities.Request(sim.World, c, bi);
        Assert.True(sim.TryGetBuildingLife(bi, gen, out li));
        Assert.Equal(RepairState.Queued, li.RepairState);
        Assert.Equal(BuildingRenderFlags.Requested, li.Flags);

        b.Cond[bi] = 4_000;
        Assert.True(sim.TryGetBuildingLife(bi, gen, out li));
        Assert.Equal((0, 0L), (li.CanRestore, li.RestorePrice));

        Assert.False(sim.TryGetBuildingLife(bi, gen + 1, out _));
        using Simulation off = Make(Desc(life: false));
        CityWorld oc = off.World.City!;
        int ob = Find(oc, LotZone.Residential);
        Assert.False(off.TryGetBuildingLife(ob, oc.Buildings.Pool.GenerationOf(ob), out _));
        Assert.False(off.GetUtilityInfo(out _));
    }

    [Fact]
    public void AbandonedHarmStage_FromCond()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Find(c, LotZone.Industrial);
        Growth.Abandon(w, c, bi);
        int gen = c.Buildings.Pool.GenerationOf(bi);
        foreach ((ushort cond, int stage) in new[] { ((ushort)6_000, 1), ((ushort)2_000, 2), ((ushort)1_000, 3) })
        {
            c.Buildings.Cond[bi] = cond;
            Assert.True(sim.TryGetBuildingLife(bi, gen, out BuildingLifeInfo li));
            Assert.Equal(stage, li.HarmStage);
        }
    }

    [Fact]
    public void UtilityInfoApi_ReportsFundTariffSubsidyQueue()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        Assert.True(sim.GetUtilityInfo(out UtilityInfo ui));
        Assert.Equal(1, ui.FundEmpty);
        Assert.Equal((c.Cfg.Lifecycle.UtilityTariff, c.Cfg.Lifecycle.MaxUtilityTariff, 0, 0), (ui.Tariff, ui.MaxTariff, (int)ui.Subsidy, (int)ui.PendingSubsidy));
        Assert.Equal((0, 0, 0), (ui.Depots, ui.Crews, ui.QueueLength));

        Ledger lg = c.Ledger;
        lg.Transfer(lg.CityAccount, lg.UtilityAccount, 5_000, FlowKind.UtilitySubsidy);
        int bi = Find(c, LotZone.Residential);
        c.Buildings.Cond[bi] = 7_000;
        Utilities.Request(w, c, bi);
        Assert.True(sim.GetUtilityInfo(out ui));
        Assert.Equal(1, ui.QueueLength);
        BudgetPolicy p = c.Budget!.Policy;
        p.R0 = 300;
        p.Flags |= BudgetPolicyFlags.UtilitySubsidy;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBudgetPolicy(in p, 1)));

        Assert.True(sim.GetUtilityInfo(out ui));
        Assert.Equal((5_000L, 0), (ui.Fund, (int)ui.FundEmpty));
        Assert.Equal((CityLifecycleDesc.Default().UtilityTariff, 300, 0, 1), (ui.Tariff, ui.PendingTariff, (int)ui.Subsidy, (int)ui.PendingSubsidy));
        sim.Step(c.DayTicks);
        Assert.True(sim.GetUtilityInfo(out ui));
        Assert.Equal((300, 1), (ui.Tariff, (int)ui.Subsidy));
        Assert.Equal(ui.RepairsDone > 0 ? ui.RepairDelayTicksTotal / ui.RepairsDone : 0, ui.AvgDelayTicks);
    }

    // ---------------------------------------------------------------- детерминизм, сейв, 0 Б/тик

    private static Simulation Scene(int threads)
    {
        Simulation sim = Make(Desc(TripExecution.Agents), threads);
        sim.Step(200);
        CityWorld c = sim.World.City!;
        for (int k = 0; k < 3; k++)
        {
            int bi = Find(c, LotZone.Residential, k * 5);
            c.Buildings.Ceil[bi] = 9_000;
            c.Buildings.Cond[bi] = (ushort)(8_000 - (k * 1_000));
            Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Restore(c, bi, (uint)(k + 1))));
        }

        return sim;
    }

    [Fact]
    public void Hash_SameAt_1_2_3_Threads_AfterRestore()
    {
        ulong? first = null;
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = Scene(t);
            sim.Step(3_000);
            Assert.Equal(3, sim.World.Counters.Restorations);
            ulong h = sim.ComputeStateHash();
            first ??= h;
            Assert.Equal(first.Value, h);
        }
    }

    [Fact]
    public void Save_EveryTick_AroundRestore_AndContinues()
    {
        using Simulation sim = Make(Desc(TripExecution.Agents));
        sim.Step(200);
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Find(c, LotZone.Commercial);
        Growth.Abandon(w, c, bi);
        c.Buildings.Cond[bi] = 6_500;
        for (int t = 0; t < 6; t++)
        {
            if (t == 3)
            {
                Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Restore(c, bi)));
            }

            byte[] raw = Sim.Core.Tests.Save.WorldSaveFixtures.SaveBytes(sim);
            using Simulation loaded = Sim.Core.Tests.Save.WorldSaveFixtures.Load(raw);
            Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
            Assert.Equal(sim.World.Counters.Restorations, loaded.World.Counters.Restorations);
            sim.Step(1);
        }

        byte[] last = Sim.Core.Tests.Save.WorldSaveFixtures.SaveBytes(sim);
        using Simulation cont = Sim.Core.Tests.Save.WorldSaveFixtures.Load(last);
        sim.Step(1_000);
        cont.Step(1_000);
        Assert.Equal(sim.ComputeStateHash(), cont.ComputeStateHash());
        Assert.Equal(1, cont.World.Counters.Restorations);
    }

    [Fact]
    public void ZeroAllocations_AfterRestore()
    {
        using Simulation sim = Scene(1);
        CityWorld c = sim.World.City!;
        sim.Step(c.DayTicks + 10);
        long bytes = Fixtures.AllocationProbe.Measure(() => sim.Step(400));
        Assert.Equal(0L, bytes);
    }

    // ---------------------------------------------------------------- загрузчик

    [Fact]
    public void Loader_ReadsRestorePerUnit_AndValidates()
    {
        string dir = Path.Combine(GenFixture.RepoRoot(), "scenarios");
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "s1k-res-econ.json")))!;
        root["city"]!["lifecycle"] = new JsonObject { ["restore_per_unit"] = 220 };
        LoadedScenario sc = ScenarioLoader.Parse(root.ToJsonString(), p => File.ReadAllText(Path.Combine(dir, p)), "bl4");
        Assert.Equal(220, sc.Desc.Town.Lifecycle.RestorePerUnit);
        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(sc.Desc));

        ScenarioDesc d = Desc();
        Assert.Equal(150, d.Town.Lifecycle.RestorePerUnit);
        d.Town.Lifecycle.RestorePerUnit = -1;
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in d));
    }
}
