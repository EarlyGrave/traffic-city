using System;
using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// ЖКХ - первая диспетчерская служба (building-lifecycle.md §2.10, §7 тесты 5, 7, 8, 9; задача BL3): депо, заявка, выезд ближайшей бригады, ремонт до потолка, возврат,
/// плата из фонда, дефицит ждёт, субсидия, нет депо, таймаут, цель исчезла в пути, агрегат; трафик удлиняет ремонт; тариф; детерминизм, сейв на каждом тике, 0 Б/тик.
/// </summary>
public sealed class LifecycleUtilityTests
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

    private static int DepotType(CityWorld c)
    {
        for (int i = 0; i < c.Types.Count; i++)
        {
            if (c.Types.Rows[i].Kind == LotZone.Service)
            {
                return i;
            }
        }

        Assert.Fail("нет типа депо");
        return -1;
    }

    /// <summary>Свободный участок с полосой; <paramref name="skip"/> - пропустить первые.</summary>
    private static int FreeLot(CityWorld c, int skip = 0)
    {
        LotStore l = c.Lots;
        for (int i = 0; i < l.Count; i++)
        {
            if (l.Building[i] < 0 && (l.Flags[i] & LotFlags.Lost) == 0 && l.CarLane[i] >= 0 && l.RoadComp[i] >= 0 && skip-- == 0)
            {
                return i;
            }
        }

        Assert.Fail("нет свободного участка");
        return -1;
    }

    /// <summary>Активный жилой дом той же дорожной компоненты, что и участок <paramref name="lot"/>, подальше от него (по Manhattan).</summary>
    private static int Target(CityWorld c, int lot, int except = -1)
    {
        BuildingStore b = c.Buildings;
        LotStore l = c.Lots;
        int best = -1;
        int bestD = -1;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (i != except && b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind == LotZone.Residential &&
                l.RoadComp[b.Lot[i]] == l.RoadComp[lot] && l.CarLane[b.Lot[i]] >= 0)
            {
                int d = l.ManhattanM(lot, b.Lot[i]);
                if (d > bestD)
                {
                    best = i;
                    bestD = d;
                }
            }
        }

        Assert.True(best >= 0, "нет дома в компоненте депо");
        return best;
    }

    /// <summary>Бригад в депо базового пакета.</summary>
    private static int DepotCrews(CityWorld c) => c.Types.Rows[DepotType(c)].Vehicles;

    private static int BuildDepot(Simulation sim, int lot)
    {
        CityWorld c = sim.World.City!;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeBuildService(lot, DepotType(c), 1)));
        int bi = c.Lots.Building[lot];
        Assert.True(bi >= 0 && c.Buildings.State[bi] == (byte)BuildingState.Active);
        return bi;
    }

    private static void Fund(CityWorld c, long amount)
    {
        Ledger lg = c.Ledger;
        lg.Transfer(lg.CityAccount, lg.UtilityAccount, amount, FlowKind.UtilitySubsidy);
    }

    private static void Worn(CityWorld c, int bi, ushort cond = 7_000)
    {
        c.Buildings.Ceil[bi] = CityLife.Full;
        c.Buildings.Cond[bi] = cond;
    }

    private static int Count(UtilityService u, byte state)
    {
        int n = 0;
        for (int k = 0; k < u.Capacity; k++)
        {
            n += u.State[k] == state ? 1 : 0;
        }

        return n;
    }

    /// <summary>Шаги по <paramref name="chunk"/> тиков, пока не выполнится условие (не дольше <paramref name="max"/>).</summary>
    private static bool StepUntil(Simulation sim, Func<bool> done, int max, int chunk = 20)
    {
        for (int t = 0; t < max; t += chunk)
        {
            if (done())
            {
                return true;
            }

            sim.Step(chunk);
        }

        return done();
    }

    // ---------------------------------------------------------------- тест 5: цикл заявки

    [Fact]
    public void BuildService_ChargesCost_AndCreatesIdleCrews()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        CityWorld c = sim.World.City!;
        int lot = FreeLot(c);
        int type = DepotType(c);
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[type];
        Command cmd = Command.MakeBuildService(lot, type, 1);
        Assert.Equal(RejectReason.None, sim.Preview(in cmd, out PreviewResult pr, new PreviewIssue[4]));
        Assert.Equal(t.BuildCost, pr.Cost);
        long city = c.Ledger.City;
        int bi = BuildDepot(sim, lot);
        Assert.True(city - c.Ledger.City >= t.BuildCost);
        Assert.Equal(t.Vehicles, Count(c.Utility!, CrewState.Idle));
        Assert.Equal(0L, c.Ledger.SumAll());

        // Занятый участок - ZoneRule; не тот тип - InvalidArgument; без режима - NotSupported.
        Assert.Equal(RejectReason.ZoneRule, ToolsFixture.Run(sim, Command.MakeBuildService(lot, type, 2)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeBuildService(FreeLot(c), 0, 3)));
        Assert.True(c.Buildings.IsAlive(bi));
        using Simulation off = Make(Desc(life: false));
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(off, Command.MakeBuildService(FreeLot(off.World.City!), 0, 1)));
    }

    [Fact]
    public void Aggregate_Request_Dispatch_Repair_Return_PaysFromFund()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        UtilityService u = c.Utility!;
        int lot = FreeLot(c);
        BuildDepot(sim, lot);
        int bi = Target(c, lot);
        Worn(c, bi);
        long price = Utilities.RepairCost(c, bi);
        Assert.True(price > 0);
        Fund(c, price * 2);
        long fund = c.Ledger.Utility;

        Utilities.Request(w, c, bi);
        Assert.Equal(1, u.Queue.Count);
        Assert.NotEqual(0, c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Requested);
        Utilities.Request(w, c, bi);
        Assert.Equal(1, u.Queue.Count);   // повтор не дублирует

        Assert.True(StepUntil(sim, () => Count(u, CrewState.ToSite) == 1, 40));
        Assert.Equal(0, u.Queue.Count);
        Assert.True(StepUntil(sim, () => w.Counters.RepairsDone == 1, 20_000));
        Assert.Equal(CityLife.Full, c.Buildings.Cond[bi]);
        Assert.Equal(0, c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Requested);
        Assert.Equal(fund - price, c.Ledger.Utility);
        Assert.Equal(price, c.Ledger.FlowDay[(int)FlowKind.Repair]);
        Assert.True(w.Counters.RepairDelayTicksTotal > 0);
        Assert.Equal(1, Count(u, CrewState.Return));
        Assert.True(StepUntil(sim, () => Count(u, CrewState.Idle) == DepotCrews(c), 20_000));
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    [Fact]
    public void Deficit_Waits_ThenSubsidyCovers()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        UtilityService u = c.Utility!;
        int lot = FreeLot(c);
        BuildDepot(sim, lot);
        int bi = Target(c, lot);
        Worn(c, bi);
        Assert.Equal(0L, c.Ledger.Utility);
        Utilities.Request(w, c, bi);
        sim.Step(200);
        Assert.Equal(1, u.Queue.Count);   // фонд пуст, субсидии нет: заявка ждёт денег
        Assert.Equal(DepotCrews(c), Count(u, CrewState.Idle));

        c.Budget!.Policy.Flags |= BudgetPolicyFlags.UtilitySubsidy;
        Assert.True(StepUntil(sim, () => w.Counters.RepairsDone == 1, 20_000));
        Assert.True(c.Ledger.FlowTotal[(int)FlowKind.UtilitySubsidy] > 0);
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    [Fact]
    public void Subsidy_IsPendingPolicy_AppliedAtDayBoundary()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        CityWorld c = sim.World.City!;
        BudgetPolicy p = c.Budget!.Policy;
        p.Flags |= BudgetPolicyFlags.UtilitySubsidy;
        p.R0 = 250;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBudgetPolicy(in p, 1)));
        Assert.Equal(0, c.Budget.Policy.Flags & BudgetPolicyFlags.UtilitySubsidy);
        sim.Step(c.DayTicks);
        Assert.NotEqual(0, c.Budget.Policy.Flags & BudgetPolicyFlags.UtilitySubsidy);
        Assert.Equal(250, c.Budget.Policy.R0);

        p.R0 = (ushort)(c.Cfg.Lifecycle.MaxUtilityTariff + 1);
        Assert.Equal(RejectReason.BudgetRule, ToolsFixture.Run(sim, Command.MakeSetBudgetPolicy(in p, 2)));
    }

    [Fact]
    public void NoDepotInComponent_DropsRequest()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Target(c, FreeLot(c));
        Worn(c, bi);
        Utilities.Request(w, c, bi);
        sim.Step(40);
        Assert.Equal(1, w.Counters.RepairNoDepot);
        Assert.Equal(0, c.Utility!.Queue.Count);
        Assert.Equal(0, c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Requested);
    }

    [Fact]
    public void BelowHalf_NotRepairable_AndAbove85_NoRequest()
    {
        using Simulation sim = Make(Desc());
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Target(c, FreeLot(c));
        Worn(c, bi, CityLife.AbandonBp - 1);
        Utilities.Request(w, c, bi);
        Worn(c, bi, (ushort)c.Cfg.Lifecycle.RepairRequestBp);
        Utilities.Request(w, c, bi);
        Assert.Equal(0, c.Utility!.Queue.Count);
    }

    [Fact]
    public void TargetGone_InFlight_NoRepair_CrewReturns()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        World w = sim.World;
        CityWorld c = w.City!;
        UtilityService u = c.Utility!;
        int lot = FreeLot(c);
        BuildDepot(sim, lot);
        int bi = Target(c, lot);
        Worn(c, bi);
        Fund(c, 1_000_000);
        Utilities.Request(w, c, bi);
        Assert.True(StepUntil(sim, () => Count(u, CrewState.ToSite) == 1, 40));
        Growth.Abandon(w, c, bi);
        Assert.True(StepUntil(sim, () => Count(u, CrewState.Idle) == DepotCrews(c), 40_000));
        Assert.Equal(0, w.Counters.RepairsDone);
        Assert.Equal(0L, c.Ledger.FlowTotal[(int)FlowKind.Repair]);
    }

    [Fact]
    public void Micro_CrewVehicle_LinksAndRepairs_WithoutOnCall()
    {
        using Simulation sim = Make(Desc(TripExecution.Agents));
        sim.Step(200);
        World w = sim.World;
        CityWorld c = w.City!;
        UtilityService u = c.Utility!;
        int lot = FreeLot(c);
        BuildDepot(sim, lot);
        int bi = Target(c, lot);
        Worn(c, bi);
        Fund(c, 1_000_000);
        Utilities.Request(w, c, bi);
        bool linked = false;
        for (int t = 0; t < 40_000 && w.Counters.RepairsDone + w.Counters.CrewTimeouts == 0; t++)
        {
            sim.Step(1);
            VehicleStore v = w.Vehicles;
            for (int i = 0; i < v.HighWater; i++)
            {
                if (v.Pool.IsAlive(i) && v.Resident[i] <= -2)
                {
                    linked = true;
                    int k = -2 - v.Resident[i];
                    Assert.Equal(i, u.Vehicle[k]);
                    Assert.Equal(0, v.Flags[i] & (byte)VehicleFlags.OnCall);   // приоритета на красном у ЖКХ нет никогда
                }
            }
        }

        Assert.True(linked, "машина бригады не появилась на дороге");
        Assert.Equal(1, w.Counters.RepairsDone);
        Assert.Equal(CityLife.Full, c.Buildings.Cond[bi]);
        Assert.True(StepUntil(sim, () => Count(u, CrewState.Idle) == DepotCrews(c), 40_000));
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    [Fact]
    public void Micro_Timeout_CrewBackToDepot_RequestCleared()
    {
        using Simulation sim = Make(Desc(TripExecution.Agents));
        sim.Step(200);
        World w = sim.World;
        CityWorld c = w.City!;
        UtilityService u = c.Utility!;
        int lot = FreeLot(c);
        BuildDepot(sim, lot);
        int bi = Target(c, lot);
        Worn(c, bi);
        Fund(c, 1_000_000);
        Utilities.Request(w, c, bi);
        Assert.True(StepUntil(sim, () => Count(u, CrewState.ToSite) == 1, 40, 1));
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.State[k] == CrewState.ToSite)
            {
                u.DueTick[k] = w.Tick;   // срок вышел
            }
        }

        sim.Step(Utilities.Step);
        Assert.Equal(1, w.Counters.CrewTimeouts);
        Assert.Equal(DepotCrews(c), Count(u, CrewState.Idle));
        Assert.Equal(0, c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Requested);
        for (int i = 0; i < w.Vehicles.HighWater; i++)
        {
            Assert.False(w.Vehicles.Pool.IsAlive(i) && w.Vehicles.Resident[i] <= -2);   // машина отвязана и едет пустой
        }
    }

    [Fact]
    public void DayBoundary_PaysWages()
    {
        using Simulation sim = Make(Desc());
        sim.Step(20);
        CityWorld c = sim.World.City!;
        BuildDepot(sim, FreeLot(c));
        sim.Step(c.DayTicks);
        Assert.Equal(DepotCrews(c) * (long)c.Cfg.Lifecycle.CrewDayCost, c.Ledger.FlowTotal[(int)FlowKind.CrewWages]);
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    // ---------------------------------------------------------------- тест 7: ЖКХ в трафике

    [Fact]
    public void Traffic_OnDepotRoute_IncreasesRepairDelay()
    {
        long Delay(int extra)
        {
            using Simulation sim = Make(Desc(TripExecution.Agents));
            sim.Step(200);
            World w = sim.World;
            CityWorld c = w.City!;
            int lot = FreeLot(c);
            BuildDepot(sim, lot);
            int bi = Target(c, lot);
            Worn(c, bi);
            Fund(c, 1_000_000);
            int from = c.Lots.CarLane[lot];
            int dest = c.Lots.CarLane[c.Buildings.Lot[bi]];
            for (int i = 0; i < extra && !w.Trips.IsFull; i++)
            {
                var trip = new PendingTrip { FromZone = -1, ToZone = -1, FromLane = from, FromS = 0f, Type = 0, DestLane = dest, DestS = 0f, Route = -1 };
                w.Trips.TryEnqueue(in trip);
            }

            Utilities.Request(w, c, bi);
            Assert.True(StepUntil(sim, () => w.Counters.RepairsDone == 1, 60_000, 1), $"ремонт не завершён (extra {extra}, timeouts {w.Counters.CrewTimeouts})");
            return w.Counters.RepairDelayTicksTotal;
        }

        long free = Delay(0);
        long busy = Delay(120);
        Assert.True(busy > free, $"задержка с нагрузкой {busy} не больше свободной {free}");
    }

    // ---------------------------------------------------------------- тест 8: тариф

    [Fact]
    public void Tariff_AboveBurden_PoorLeaveFirst_RichStay()
    {
        ScenarioDesc d = Desc();
        d.Town.Lifecycle.TariffLeaveQ16 = CityDesc.Q16(1.0);
        d.Town.Lifecycle.UtilityTariff = 0;   // до проверки никто не уезжает из-за тарифа
        using Simulation sim = Make(d);
        sim.Step(c0(sim).DayTicks + 20);
        World w = sim.World;
        CityWorld c = w.City!;
        CityResidents r = c.Residents;
        int poor = -1;
        int rich = -1;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (r.Pool.IsAlive(i) && r.Home[i] >= 0 && (r.Flags[i] & ResidentFlags.Leaving) == 0)
            {
                if (poor < 0 || r.IncomeEwma[i] < r.IncomeEwma[poor])
                {
                    poor = i;
                }

                if (rich < 0 || r.IncomeEwma[i] > r.IncomeEwma[rich])
                {
                    rich = i;
                }
            }
        }

        Assert.True(poor >= 0 && rich >= 0);
        if (poor == rich || r.IncomeEwma[rich] <= r.IncomeEwma[poor] * 4)
        {
            // доходы в первые сутки ещё не разошлись: задаём разброс явно (правило сравнивает тариф с долей IncomeEwma жителя)
            rich = poor == rich ? Other(r, poor) : rich;
            r.IncomeEwma[poor] = 5;
            r.IncomeEwma[rich] = 100;
        }

        long pct = c.Cfg.Lifecycle.TariffBurdenPct;
        ushort tariff = (ushort)Math.Min(c.Cfg.Lifecycle.MaxUtilityTariff, (r.IncomeEwma[poor] * pct) + 1);
        Assert.True(tariff <= r.IncomeEwma[rich] * pct, "разброс доходов мал для теста");
        c.Budget!.Policy.R0 = tariff;
        long inc = c.Kpi.Cur[0].IncBuildingWorn;
        // вариант Б (2026-10-30): жребий только у рабочей силы; иждивенец с нулевым доходом не уезжает
        r.Flags[poor] &= unchecked((byte)~ResidentFlags.Labour);
        Utilities.TariffLeave(w, c, poor);
        Assert.Equal(0, r.Flags[poor] & ResidentFlags.Leaving);
        r.Flags[poor] |= ResidentFlags.Labour;
        r.Flags[rich] |= ResidentFlags.Labour;
        Utilities.TariffLeave(w, c, rich);
        Assert.Equal(0, r.Flags[rich] & ResidentFlags.Leaving);
        Utilities.TariffLeave(w, c, poor);
        Assert.NotEqual(0, r.Flags[poor] & ResidentFlags.Leaving);
        Assert.Equal(1, w.Counters.TariffLeaves);
        Assert.Equal(inc + 1, c.Kpi.Cur[0].IncBuildingWorn);
        int home = r.Home[poor];
        Assert.True(c.Kpi.Complaints.Pending(ComplaintKind.HighRent, home, c.Buildings.Pool.GenerationOf(home)) > 0);

        static CityWorld c0(Simulation s) => s.World.City!;
        static int Other(CityResidents r, int not)
        {
            for (int i = 0; i < r.HighWater; i++)
            {
                if (i != not && r.Pool.IsAlive(i) && r.Home[i] >= 0 && (r.Flags[i] & ResidentFlags.Leaving) == 0)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    [Fact]
    public void Tariff_FillsUtilityFund_LedgerBalanced()
    {
        ScenarioDesc d = Desc();
        d.Town.Lifecycle.UtilityTariff = 1000;
        d.Town.Lifecycle.TariffLeaveQ16 = CityDesc.Q16(0.5);
        using Simulation sim = Make(d);
        CityWorld c = sim.World.City!;
        Assert.Equal(1000, c.Budget!.Policy.R0);
        sim.Step(c.DayTicks + 20);
        Assert.True(c.Ledger.FlowTotal[(int)FlowKind.UtilityFee] > 0);
        Assert.True(c.Ledger.Utility > 0);
        Assert.True(sim.World.Counters.TariffLeaves > 0);
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    // ---------------------------------------------------------------- тест 9: детерминизм, сейв, 0 Б/тик

    private static Simulation Scene(int threads, out int bi)
    {
        Simulation sim = Make(Desc(TripExecution.Agents), threads);
        sim.Step(200);
        CityWorld c = sim.World.City!;
        int lot = FreeLot(c);
        BuildDepot(sim, lot);
        bi = Target(c, lot);
        Worn(c, bi);
        Fund(c, 1_000_000);
        Utilities.Request(sim.World, c, bi);
        int second = Target(c, FreeLot(c), bi);
        Worn(c, second, 6_000);
        Utilities.Request(sim.World, c, second);
        return sim;
    }

    [Fact]
    public void Hash_SameAt_1_2_3_Threads_WithCrews()
    {
        ulong? first = null;
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = Scene(t, out _);
            sim.Step(6_000);
            Assert.True(sim.World.Counters.RepairsDone > 0 || sim.World.City!.Utility!.Crews() > 0);
            ulong h = sim.ComputeStateHash();
            first ??= h;
            Assert.Equal(first.Value, h);
        }
    }

    [Fact]
    public void Save_EveryTick_AroundDispatchAndRepair()
    {
        using Simulation sim = Scene(1, out int bi);
        World w = sim.World;
        UtilityService u = w.City!.Utility!;
        int saves = 0;
        bool sawToSite = false;
        bool sawWorking = false;
        int sig = -1;
        int after = 0;
        for (int t = 0; t < 20_000 && w.Counters.RepairsDone < 2; t++)
        {
            sim.Step(1);
            int now = (Count(u, CrewState.ToSite) * 100) + (Count(u, CrewState.Working) * 10) + Count(u, CrewState.Return);
            sawToSite |= Count(u, CrewState.ToSite) > 0;
            sawWorking |= Count(u, CrewState.Working) > 0;

            // сейв на каждом тике вокруг смены состояния бригад (выезд, прибытие, конец ремонта, возврат) - 3 тика до и после
            if (now != sig)
            {
                sig = now;
                after = 3;
            }
            else if (after > 0)
            {
                after--;
            }
            else if (t >= 30)
            {
                continue;
            }

            byte[] raw = Sim.Core.Tests.Save.WorldSaveFixtures.SaveBytes(sim);
            using Simulation loaded = Sim.Core.Tests.Save.WorldSaveFixtures.Load(raw);
            Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
            saves++;
            if ((saves % 25) == 0)
            {
                using Simulation probe = Sim.Core.Tests.Save.WorldSaveFixtures.Load(raw);
                using Simulation twin = Sim.Core.Tests.Save.WorldSaveFixtures.Load(raw);
                probe.Step(40);
                twin.Step(40);
                Assert.Equal(probe.ComputeStateHash(), twin.ComputeStateHash());
            }
        }

        Assert.True(sawToSite && sawWorking, "цикл бригады не пройден");
        Assert.True(saves > 40, $"сейвов {saves}");
        Assert.Equal(CityLife.Full, w.City!.Buildings.Cond[bi]);
    }

    [Fact]
    public void Save_Continues_SameHash_WithCrewsInFlight()
    {
        using Simulation sim = Scene(1, out _);
        sim.Step(60);
        Assert.True(sim.World.City!.Utility!.Crews() > 0);
        byte[] raw = Sim.Core.Tests.Save.WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = Sim.Core.Tests.Save.WorldSaveFixtures.Load(raw);
        sim.Step(4_000);
        loaded.Step(4_000);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void ZeroAllocations_WithCrewsInFlight()
    {
        using Simulation sim = Scene(1, out _);
        UtilityService u = sim.World.City!.Utility!;
        Assert.True(StepUntil(sim, () => Count(u, CrewState.ToSite) > 0, 400, 1));
        long bytes = Fixtures.AllocationProbe.Measure(() => sim.Step(400));
        Assert.Equal(0L, bytes);
    }

    // ---------------------------------------------------------------- без режима и загрузчик

    [Fact]
    public void WithoutMode_NoService_LedgerUtilityZero()
    {
        using Simulation sim = Make(Desc(life: false));
        sim.Step(200);
        CityWorld c = sim.World.City!;
        Assert.Null(c.Utility);
        Assert.Equal(0L, c.Ledger.Utility);
        Assert.Equal(0, c.Budget!.Policy.R0);
    }

    [Fact]
    public void Loader_ReadsUtilityKeys()
    {
        string dir = Path.Combine(GenFixture.RepoRoot(), "scenarios");
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "s1k-res-econ.json")))!;
        root["city"]!["lifecycle"] = new JsonObject
        {
            ["repair_request"] = 0.8,
            ["repair_per_unit"] = 30,
            ["repair_minutes"] = 90,
            ["crew_day_cost"] = 200,
            ["max_service_vehicles"] = 64,
            ["max_dispatch_per_tick"] = 2,
            ["tariff_burden_pct"] = 7,
            ["tariff_leave"] = 0.1,
            ["max_utility_tariff"] = 500,
            ["utility_tariff"] = 120,
        };
        LoadedScenario sc = ScenarioLoader.Parse(root.ToJsonString(), p => File.ReadAllText(Path.Combine(dir, p)), "bl3");
        CityLifecycleDesc l = sc.Desc.Town.Lifecycle;
        Assert.Equal((8_000, 30, 90, 200, 64, 2, 7), (l.RepairRequestBp, l.RepairPerUnit, l.RepairMinutes, l.CrewDayCost, l.MaxServiceVehicles, l.MaxDispatchPerTick, l.TariffBurdenPct));
        Assert.Equal((CityDesc.Q16(0.1), 500, 120), (l.TariffLeaveQ16, l.MaxUtilityTariff, l.UtilityTariff));
        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(sc.Desc));
    }
}
