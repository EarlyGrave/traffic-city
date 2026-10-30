using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Tools;
using Sim.Core.Tools;
using Xunit;

namespace Sim.Core.Tests.City;

/// <summary>
/// Уровни и апгрейды служб SR5 (services.md §2.8, §6, §8 тест 8): <c>BuildService</c> по уровню (цена - сумма уровней), <c>lot_min</c>, пристройки и выкуп соседей (цена, снос, памятник и служба
/// нельзя, рост пристройку не занимает), локальные апгрейды и их эффекты, веха (<c>Locked</c>), undo в окне (понижение, выключение), детерминизм 1/2/3 потока, сейв, 0 Б/тик.
/// Тестовая запись «med» из трёх уровней поверх записи ЖКХ в малом городе фикстуры BL.
/// </summary>
public sealed class ServicesLevelsTests
{
    private const string Scenario = "s1k-res-bl";
    private static readonly int[] _buildCosts = { 1_000, 2_000, 4_000 };

    /// <summary>Город с записью «med»: уровни 1-3 (типы Medicine), апгрейды L1: units +2 (500), onsite_pct 50 (300); L2: beds +5 (700).</summary>
    internal static Simulation Make(int threads = 1, int l3Milestone = 0)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(Scenario, TripExecution.Agents);
        BuildingTypeDesc[] types = d.Town.BuildingTypes is { Length: > 0 } bt ? bt : CityDesc.BaseBuildingTypes();
        int depot = Array.FindIndex(types, t => t.ServiceKind == ServiceKind.Utility);
        Assert.True(depot >= 0);
        BuildingTypeDesc med = types[depot];
        med.ServiceKind = ServiceKind.Medicine;
        med.Vehicles = 0;
        med.VehicleType = byte.MaxValue;
        med.BuildCost = 0;
        var all = new BuildingTypeDesc[types.Length + 3];
        types.CopyTo(all, 0);
        for (int k = 0; k < 3; k++)
        {
            all[types.Length + k] = med;
        }

        d.Town.BuildingTypes = all;
        int amb = Array.FindIndex(d.VehicleTypes, t => t.Category == VehicleCategory.Special);
        Assert.True(amb >= 0);

        CityServicesDesc s = CityServicesDesc.Default();
        CityServicesDesc.BaseRecords(depot, types[depot].VehicleType, out ServiceRecordDesc[] r, out ServiceLevelDesc[] l, out ServiceCallDesc[] c);
        s.Records = new[] { r[0], new ServiceRecordDesc { Fund = ServiceFund.City, Rev = 1, FirstLevel = 1, LevelCount = 3, FirstCall = 1, CallCount = 2 } };
        ServiceLevelDesc Lv(int k, ServiceRole roles, int units, int beds, int first, int count, byte milestone) => new()
        {
            Type = types.Length + k,
            Roles = roles,
            LotMinF = 1,
            LotMinD = 1,
            BuildCost = _buildCosts[k],
            UpkeepDay = 100 * (k + 1),
            DispatchVehicle = (short)amb,
            Units = units,
            PerUnitDay = 50,
            Beds = beds,
            CapacityDay = beds > 0 ? 50 : 0,
            OpenSlot = 0,
            CloseSlot = 288,
            StaySlots = 6,
            FirstUpgrade = first,
            UpgradeCount = count,
            Milestone = milestone,
        };
        s.Levels = new[]
        {
            l[0],
            Lv(0, ServiceRole.Critical, 1, 0, 0, 2, 0),
            Lv(1, ServiceRole.Critical | ServiceRole.Fire, 2, 10, 2, 1, 0),
            Lv(2, ServiceRole.Critical | ServiceRole.Fire | ServiceRole.Inpatient, 3, 20, 3, 0, (byte)l3Milestone),
        };
        s.Upgrades = new[]
        {
            new ServiceUpgradeDesc { Cost = 500, UpkeepDay = 50, Effect = ServiceUpgradeEffect.Units, Value = 2 },
            new ServiceUpgradeDesc { Cost = 300, UpkeepDay = 20, Effect = ServiceUpgradeEffect.OnsitePct, Value = 50 },
            new ServiceUpgradeDesc { Cost = 700, UpkeepDay = 60, Effect = ServiceUpgradeEffect.Beds, Value = 5 },
        };
        s.Calls = new[]
        {
            c[0],
            new ServiceCallDesc { Kind = ServiceCallKind.Critical, Flags = ServiceCallFlags.Urgent, OnsiteS = 60 },
            new ServiceCallDesc { Kind = ServiceCallKind.Fire, Flags = 0, OnsiteS = 60 },
        };
        s.IllLightPpm = 0;
        s.IllInpatientPpm = 0;
        s.IllCriticalPpm = 0;
        d.Town.Services = s;
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(20);
        CityWorld cw = sim.World.City!;
        Assert.NotNull(cw.Budget);
        cw.Ledger.Transfer(cw.Ledger.ExternalAccount, cw.Ledger.CityAccount, 50_000_000, FlowKind.Subsidy);
        return sim;
    }

    private static int TypeOf(CityWorld c, int level) => c.Services!.Table.Levels[1 + level].Type;   // 0 - уровень записи utility

    private static bool FreeLot(LotStore l, int i) => l.Building[i] < 0 && (l.Flags[i] & (LotFlags.Lost | LotFlags.Annex)) == 0 && l.CarLane[i] >= 0 && l.RoadComp[i] >= 0;

    /// <summary>Ряд лотов того же фасада, что у <paramref name="lot"/>, по порядку вдоль сегмента.</summary>
    private static int[] Row(LotStore l, int lot)
    {
        var row = new List<int>();
        for (int x = 0; x < l.Count; x++)
        {
            if (ServiceLevels.SameFrontage(l, x, lot))
            {
                row.Add(x);
            }
        }

        row.Sort((a, b) => ServiceLevels.Rank(l, a).CompareTo(ServiceLevels.Rank(l, b)));
        return row.ToArray();
    }

    /// <summary>Свободный участок-станция <c>main</c> и два его соседа вдоль фасада (не Lost, не пристройки): <c>n1</c>, <c>n2</c> - следующие по ряду; <c>far</c> - через одного дальше.</summary>
    private static (int Main, int N1, int N2, int Far) Find(CityWorld c, bool occupiedN1 = false, bool freeN1 = false)
    {
        LotStore l = c.Lots;
        for (int i = 0; i < l.Count; i++)
        {
            if (!FreeLot(l, i) || l.Orient[i] == LotOrient.Legacy)
            {
                continue;
            }

            int[] row = Row(l, i);
            int p = Array.IndexOf(row, i);
            if (p < 0 || p + 3 >= row.Length)
            {
                continue;
            }

            int n1 = row[p + 1];
            int n2 = row[p + 2];
            int far = row[p + 3];
            bool ok = true;
            foreach (int x in new[] { n1, n2, far })
            {
                int b = l.Building[x];
                ok &= (l.Flags[x] & (LotFlags.Lost | LotFlags.Annex)) == 0 && (b < 0 || c.Types.Rows[c.Buildings.Type[b]].Kind != LotZone.Service);
            }

            if (ok && (!occupiedN1 || l.Building[n1] >= 0) && (!freeN1 || FreeLot(l, n1)))
            {
                return (i, n1, n2, far);
            }
        }

        Assert.Fail("нет подходящей четвёрки участков");
        return default;
    }

    private static int Build(Simulation sim, int lot, int level, uint tag = 1)
    {
        CityWorld c = sim.World.City!;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeBuildService(lot, TypeOf(c, level), tag)));
        return c.Lots.Building[lot];
    }

    private static EntityRef Ref(CityWorld c, int bi) => new(bi, c.Buildings.Pool.GenerationOf(bi));

    private static RejectReason Preview(Simulation sim, in Command cmd, out PreviewResult r, out PreviewIssue first)
    {
        var issues = new PreviewIssue[8];
        RejectReason v = sim.Preview(in cmd, out r, issues);
        first = r.IssuesWritten > 0 ? issues[0] : default;
        return v;
    }

    private static Command[] InverseOf(Simulation sim, uint tag)
    {
        foreach (InverseRecord r in sim.Inverses)
        {
            if (r.Tag == tag)
            {
                return sim.InverseCommands.Slice(r.First, r.Count).ToArray();
            }
        }

        return Array.Empty<Command>();
    }

    private static int Site(CityWorld c, int bi) => ServiceLevels.SiteOf(c, bi);

    [Fact]
    public void BuildService_ByLevel_CostIsSumOfLevels_AndStationHasLevel()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, _, _, _) = Find(c);
        Command cmd = Command.MakeBuildService(main, TypeOf(c, 2), 5);
        Assert.Equal(RejectReason.None, Preview(sim, in cmd, out PreviewResult pr, out _));
        Assert.Equal(_buildCosts[0] + _buildCosts[1] + _buildCosts[2], pr.Cost);
        long before = c.Ledger.City;
        int bi = Build(sim, main, 2, 5);
        Assert.Equal(before - pr.Cost, c.Ledger.City);
        int site = Site(c, bi);
        Assert.Equal(3, c.Services!.Sites.Level[site]);
        Assert.Equal(TypeOf(c, 2), c.Buildings.Type[bi]);
        Assert.Equal(5, c.Services.Sites.UnitsMax[site]);   // units 3: 3 + 0 + ceil(3 / 2)
    }

    [Fact]
    public void BuildService_LotMin_RejectsSmallLot_WithValue()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, _, _, _) = Find(c);
        c.Services!.Table.Levels[3].LotMinF = (byte)(c.Lots.CellsF[main] + 1);
        Command cmd = Command.MakeBuildService(main, TypeOf(c, 2), 5);
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in cmd, out _, out PreviewIssue iss));
        Assert.Equal(ToolValidator.ServiceLotTooSmall, iss.Value);
        Assert.Equal(RejectReason.ZoneRule, ToolsFixture.Run(sim, cmd));
        Assert.True(c.Lots.Building[main] < 0);
    }

    [Fact]
    public void Milestone_LocksLevel_UntilReached()
    {
        using Simulation sim = Make(l3Milestone: 2);
        World w = sim.World;
        CityWorld c = w.City!;
        w.Progression = new ProgressionState(2, 0, 0, 1);
        (int main, _, _, _) = Find(c);
        Command cmd = Command.MakeBuildService(main, TypeOf(c, 2), 5);
        Assert.Equal(RejectReason.Locked, Preview(sim, in cmd, out _, out PreviewIssue iss));
        Assert.Equal(1, iss.Value);   // веха M2 = Defs[1]
        Assert.Equal(RejectReason.Locked, ToolsFixture.Run(sim, cmd));
        w.Progression.Reached = 2;
        Assert.Equal(RejectReason.None, Preview(sim, in cmd, out _, out _));

        // апгрейд L2 -> L3 тоже за вехой
        w.Progression.Reached = 0;
        int bi = Build(sim, main, 1, 6);
        Command up = Command.MakeUpgradeService(Ref(c, bi));
        Assert.Equal(RejectReason.Locked, ToolsFixture.Run(sim, up));
        w.Progression.Reached = 2;
        up.Tag = 7;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, up));
        Assert.Equal(3, c.Services!.Sites.Level[Site(c, bi)]);
    }

    [Fact]
    public void Upgrade_SmallLot_NeedsAdjacentAnnex_Rules()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, int n1, int n2, int far) = Find(c);
        LotStore l = c.Lots;
        int bi = Build(sim, main, 0);
        ServiceTable tb = c.Services!.Table;
        tb.Levels[2].LotMinF = (byte)(l.CellsF[main] + l.CellsF[n1]);
        tb.Levels[2].LotMinD = l.CellsD[main];
        EntityRef h = Ref(c, bi);

        Command none = Command.MakeUpgradeService(h, tag: 2);
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in none, out _, out PreviewIssue iss));
        Assert.Equal(ToolValidator.ServiceLotTooSmall, iss.Value);

        Command gap = Command.MakeUpgradeService(h, far, tag: 3);   // не подряд
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in gap, out _, out iss));
        Assert.Equal(ToolValidator.ServiceAnnexNotAdjacent, iss.Value);

        int other = -1;
        for (int i = 0; i < l.Count && other < 0; i++)
        {
            other = !ServiceLevels.SameFrontage(l, i, main) && FreeLot(l, i) ? i : -1;
        }

        Command side = Command.MakeUpgradeService(h, other, tag: 4);   // другой фасад
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in side, out _, out iss));
        Assert.Equal(ToolValidator.ServiceAnnexNotAdjacent, iss.Value);

        Command self = Command.MakeUpgradeService(h, main, tag: 8);
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in self, out _, out _));
        Command dup = Command.MakeUpgradeService(h, n1, n1, tag: 9);
        Assert.Equal(RejectReason.InvalidArgument, Preview(sim, in dup, out _, out _));

        Command ok = Command.MakeUpgradeService(h, n1, tag: 5);
        long before = c.Ledger.City;
        Assert.Equal(RejectReason.None, Preview(sim, in ok, out PreviewResult pr, out _));
        Assert.True(pr.Cost >= _buildCosts[1]);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, ok));
        Assert.Equal(before - pr.Cost, c.Ledger.City);
        int site = Site(c, bi);
        Assert.Equal(2, c.Services.Sites.Level[site]);
        Assert.Equal(TypeOf(c, 1), c.Buildings.Type[bi]);
        Assert.True(ServiceLevels.IsAnnexOf(c.Services.Sites, site, n1));
        Assert.NotEqual(0, l.Flags[n1] & LotFlags.Annex);

        // следующий шаг тянет цепочку дальше: n2 подряд с уже купленным n1, far - нет
        tb.Levels[3].LotMinF = (byte)(l.CellsF[main] + l.CellsF[n1] + l.CellsF[n2]);
        tb.Levels[3].LotMinD = l.CellsD[main];
        Command gap2 = Command.MakeUpgradeService(h, far, tag: 10);
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in gap2, out _, out _));
        Command next = Command.MakeUpgradeService(h, n2, tag: 11);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, next));
        Assert.Equal(3, c.Services.Sites.Level[site]);
        Assert.Equal(2, ServiceLevels.AnnexCount(c.Services.Sites, site));

        // уровней больше нет
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeUpgradeService(h, tag: 12)));
    }

    [Fact]
    public void Annex_Buyout_CostDemolishesBuilding_AndGrowthLeavesLotAlone()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, int n1, _, _) = Find(c, occupiedN1: true);
        LotStore l = c.Lots;
        int victim = l.Building[n1];
        Assert.True(victim >= 0);
        int bi = Build(sim, main, 0);
        EntityRef h = Ref(c, bi);
        long demolish = c.Types.Rows[c.Buildings.Type[victim]].DemolishCost;
        Command cmd = Command.MakeUpgradeService(h, n1, tag: 2);
        Assert.Equal(RejectReason.None, Preview(sim, in cmd, out PreviewResult pr, out _));
        Assert.True(pr.Cost >= _buildCosts[1] + demolish, $"{pr.Cost} < {_buildCosts[1] + demolish}");
        var issues = new PreviewIssue[8];
        sim.Preview(in cmd, out PreviewResult pr2, issues);
        Assert.Contains(issues.AsSpan(0, pr2.IssuesWritten).ToArray(), i => i.Code == IssueCode.ServiceAnnexBuyout && i.Value == 1);

        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
        Assert.NotEqual(0, c.Buildings.LifeFlags[victim] & BuildingLifeFlags.Demolishing);
        Assert.True(sim.World.Counters.BuildingsDemolished >= 1);
        sim.Step(3 * c.DayTicks);
        Assert.True(l.Building[n1] < 0, "снесённое здание не убрано");
        Assert.NotEqual(0, l.Flags[n1] & LotFlags.Annex);
        sim.Step(3 * c.DayTicks);
        Assert.True(l.Building[n1] < 0, "рост занял пристройку");
        Assert.True(ServiceLevels.IsAnnexOf(c.Services!.Sites, Site(c, bi), n1));
    }

    [Fact]
    public void Annex_Monument_AndService_CannotBeBoughtOut()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, int n1, _, _) = Find(c, occupiedN1: true);
        int bi = Build(sim, main, 0);
        c.Buildings.LifeFlags[c.Lots.Building[n1]] |= BuildingLifeFlags.Monument;
        Command cmd = Command.MakeUpgradeService(Ref(c, bi), n1, tag: 2);
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in cmd, out _, out PreviewIssue iss));
        Assert.Equal(ToolValidator.ServiceAnnexProtected, iss.Value);
        c.Buildings.LifeFlags[c.Lots.Building[n1]] &= unchecked((byte)~BuildingLifeFlags.Monument);

        // соседняя служба (подмена вида здания соседа - станция, выкупить нельзя)
        int nb = c.Lots.Building[n1];
        int oldType = c.Buildings.Type[nb];
        c.Buildings.Type[nb] = TypeOf(c, 0);
        Assert.Equal(RejectReason.ZoneRule, Preview(sim, in cmd, out _, out iss));
        Assert.Equal(ToolValidator.ServiceAnnexProtected, iss.Value);
        c.Buildings.Type[nb] = oldType;
        Assert.Equal(RejectReason.None, Preview(sim, in cmd, out _, out _));
    }

    [Fact]
    public void LocalUpgrades_Effects_UnitsBedsOnsite_AndStateChecks()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, _, _, _) = Find(c);
        int bi = Build(sim, main, 0);
        EntityRef h = Ref(c, bi);
        CityServices sv = c.Services!;
        int site = Site(c, bi);
        Assert.Equal(2, sv.Sites.UnitsMax[site]);   // units 1 -> 1 + 0 + 1

        long before = c.Ledger.City;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 0, true, 2)));
        Assert.Equal(before - 500, c.Ledger.City);
        Assert.Equal(1, sv.Sites.UpgradeMask[site]);
        Assert.Equal(4, sv.Sites.UnitsMax[site]);   // units 1, апгрейд +2: 1 + 2 + ceil(1 / 2)
        Assert.Equal(sv.Sites.UnitsMax[site], ServiceLevels.CrewCount(sv.Crews, site));
        Assert.True(sv.Sites.Duty[site] >= 1 && sv.Sites.Duty[site] <= sv.Sites.UnitsMax[site]);

        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 0, true, 3)));   // уже включён
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 2, true, 4)));   // нет такого у уровня 1

        // onsite_pct: время работы на месте множится на 50 %
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 1, true, 5)));
        ref readonly ServiceLevelDesc l1 = ref sv.Table.LevelRow(sv.Sites.Svc[site], 1);
        Assert.Equal(50, sv.Table.UpgradeEffect(in l1, sv.Sites.UpgradeMask[site], ServiceUpgradeEffect.OnsitePct));
        Assert.True(sv.Table.UpgradeUpkeep(in l1, sv.Sites.UpgradeMask[site]) == 70);

        // выключение: без возврата, машины уходят
        before = c.Ledger.City;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 0, false, 6)));
        Assert.Equal(before, c.Ledger.City);
        Assert.Equal(2, sv.Sites.UnitsMax[site]);
        Assert.Equal(2, ServiceLevels.CrewCount(sv.Crews, site));

        // койки: апгрейд уровня 2 даёт +5 койки; уровень сбрасывает маску
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeUpgradeService(h, tag: 7)));
        Assert.Equal(0, sv.Sites.UpgradeMask[site]);
        int beds = Illness.BedsCap(sv, site);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 0, true, 8)));
        Assert.Equal(beds + 5, Illness.BedsCap(sv, site));

        // расход станции в сутки включает апгрейд
        Assert.True(sim.GetServiceStation(bi, out ServiceStationInfo info));
        Assert.Equal(2, info.Level);
        Assert.Equal(3, info.LevelCount);
        Assert.Equal(1, info.UpgradeCount);
        Assert.Equal(1, info.UpgradeMask);
        Assert.Equal(_buildCosts[2], info.NextLevelCost);
    }

    [Fact]
    public void Undo_Upgrade_DowngradesAndRefundsLevel_NotBuyout()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, int n1, _, _) = Find(c, occupiedN1: true);
        LotStore l = c.Lots;
        int bi = Build(sim, main, 0);
        EntityRef h = Ref(c, bi);
        sim.Step(30);   // новая группа undo: возврат 100 % - только за последнее действие
        long before = c.Ledger.City;
        Command up = Command.MakeUpgradeService(h, n1, tag: 40);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, up));
        long spent = before - c.Ledger.City;
        Assert.True(spent > _buildCosts[1]);
        Command[] inv = InverseOf(sim, 40);
        Assert.Single(inv);
        Assert.Equal(CommandKind.UpgradeService, inv[0].Kind);
        Assert.True((inv[0].Flags & CommandFlags.Inverse) != 0);
        Assert.Contains(c.Budget!.UndoCredits, u => u.TargetGeneration != 0 && u.Amount == _buildCosts[1]);   // выкуп в кредит не входит

        long mid = c.Ledger.City;
        inv[0].Tag = 41;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, inv[0]));
        Assert.Equal(mid + _buildCosts[1], c.Ledger.City);
        int site = Site(c, bi);
        Assert.Equal(1, c.Services!.Sites.Level[site]);
        Assert.Equal(TypeOf(c, 0), c.Buildings.Type[bi]);
        Assert.Equal(0, l.Flags[n1] & LotFlags.Annex);
        Assert.Equal(0, ServiceLevels.AnnexCount(c.Services.Sites, site));

        // понижение ниже первого уровня не бывает
        Command again = inv[0];
        again.Tag = 42;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, again));
    }

    [Fact]
    public void Undo_SetUpgrade_RefundsWithinWindow()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, _, _, _) = Find(c);
        int bi = Build(sim, main, 0);
        EntityRef h = Ref(c, bi);
        sim.Step(30);
        long before = c.Ledger.City;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 0, true, 50)));
        Command[] inv = InverseOf(sim, 50);
        Assert.Single(inv);
        Assert.Equal(0, inv[0].SetServiceUpgrade.On);
        inv[0].Tag = 51;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, inv[0]));
        Assert.Equal(before, c.Ledger.City);
        Assert.Equal(0, c.Services!.Sites.UpgradeMask[Site(c, bi)]);
    }

    [Fact]
    public void StationRemoved_ReleasesAnnexLots()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, int n1, _, _) = Find(c);
        int bi = Build(sim, main, 0);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeUpgradeService(Ref(c, bi), n1, tag: 2)));
        Assert.NotEqual(0, c.Lots.Flags[n1] & LotFlags.Annex);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeDemolishBuilding(Ref(c, bi), 3)));
        sim.Step(3 * c.DayTicks);
        Assert.False(c.Buildings.IsAlive(bi));
        Assert.Equal(0, c.Lots.Flags[n1] & LotFlags.Annex);
        Assert.Equal(-1, c.Lots.Building[main]);
    }

    private static Simulation Busy(int threads)
    {
        Simulation sim = Make(threads);
        CityWorld c = sim.World.City!;
        (int main, int n1, int n2, _) = Find(c);
        int bi = Build(sim, main, 0);
        EntityRef h = Ref(c, bi);
        ServiceTable tb = c.Services!.Table;
        tb.Levels[2].LotMinF = 1;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 0, true, 2)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeUpgradeService(h, n1, tag: 3)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceUpgrade(h, 0, true, 4)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeUpgradeService(h, n2, tag: 5)));
        return sim;
    }

    [Fact]
    public void Levels_Hash_SameAt_1_2_3_Threads()
    {
        ulong? first = null;
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = Busy(t);
            sim.Step(4_000);
            ulong h = sim.ComputeStateHash();
            first ??= h;
            Assert.Equal(first, h);
        }
    }

    [Fact]
    public void Levels_Save_EveryTick_AroundUpgrade()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        (int main, int n1, _, _) = Find(c, occupiedN1: true);
        int bi = Build(sim, main, 0);
        EntityRef h = Ref(c, bi);
        Assert.True(sim.Enqueue(Command.MakeUpgradeService(h, n1, tag: 2)));
        for (int t = 0; t < 40; t++)
        {
            sim.Step(1);
            byte[] raw = WorldSaveFixtures.SaveBytes(sim);
            using Simulation loaded = WorldSaveFixtures.Load(raw);
            Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        }

        sim.Step(2 * c.DayTicks);
        byte[] late = WorldSaveFixtures.SaveBytes(sim);
        using Simulation after = WorldSaveFixtures.Load(late);
        Assert.Equal(sim.ComputeStateHash(), after.ComputeStateHash());
        CityWorld lc = after.World.City!;
        Assert.True(ServiceLevels.IsAnnexOf(lc.Services!.Sites, Site(lc, bi), n1));
        Assert.NotEqual(0, lc.Lots.Flags[n1] & LotFlags.Annex);
    }

    [Fact]
    public void Levels_ZeroAllocations_PerTick()
    {
        using Simulation sim = Busy(1);
        sim.Step(100);
        long bytes = AllocationProbe.Measure(() => sim.Step(400));
        Assert.Equal(0L, bytes);
    }
}
