using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Tools;
using Xunit;

namespace Sim.Core.Tests.City;

/// <summary>
/// Диспетчер служб SR2 (services.md §2.3-§2.4, §8 тесты 3-5): отклик растёт с пробками, приоритет скорой (<c>on_call</c>), ЖКХ флага не получает, перегруз (срочные раньше, дальняя
/// станция, <c>deadline_s</c>), ползунок 50/150 % (<c>Duty</c>, отклик, расход по формуле); детерминизм 1/2/3 потока, сейв посреди вызова, 0 Б/тик. Тестовая запись «скорая»
/// (вызовы critical и fire, машина ambulance) поверх записи ЖКХ - в малом городе фикстуры BL.
/// </summary>
public sealed class ServicesDispatchTests
{
    private const string Scenario = "s1k-res-bl";
    private const int Med = 1;   // индекс тестовой записи (0 - utility)
    private const int UpkeepDay = 500;
    private const int PerUnitDay = 100;

    internal static Simulation MakeMed(TripExecution exec = TripExecution.Agents, byte critFlags = ServiceCallFlags.Urgent, int deadlineS = 0, int units = 1, int threads = 1,
        bool specialOnCall = false)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(Scenario, exec);
        d.Demand.SpecialOnCall = specialOnCall;
        BuildingTypeDesc[] types = d.Town.BuildingTypes is { Length: > 0 } bt ? bt : CityDesc.BaseBuildingTypes();
        int depot = Array.FindIndex(types, t => t.ServiceKind == ServiceKind.Utility);
        Assert.True(depot >= 0);
        BuildingTypeDesc med = types[depot];
        med.ServiceKind = ServiceKind.Medicine;
        med.Vehicles = 0;
        med.VehicleType = byte.MaxValue;
        med.BuildCost = 0;
        var all = new BuildingTypeDesc[types.Length + 1];
        types.CopyTo(all, 0);
        all[^1] = med;
        d.Town.BuildingTypes = all;
        int amb = Array.FindIndex(d.VehicleTypes, t => t.Category == VehicleCategory.Special);
        Assert.True(amb >= 0 && amb != types[depot].VehicleType);

        CityServicesDesc s = CityServicesDesc.Default();
        CityServicesDesc.BaseRecords(depot, types[depot].VehicleType, out ServiceRecordDesc[] r, out ServiceLevelDesc[] l, out ServiceCallDesc[] c);
        s.Records = new[] { r[0], new ServiceRecordDesc { Fund = ServiceFund.City, Rev = 1, FirstLevel = 1, LevelCount = 1, FirstCall = 1, CallCount = 2 } };
        s.Levels = new[]
        {
            l[0],
            new ServiceLevelDesc
            {
                Type = all.Length - 1, Roles = ServiceRole.Critical | ServiceRole.Fire, LotMinF = 1, LotMinD = 1, DispatchVehicle = (short)amb, Units = units,
                PerUnitDay = PerUnitDay, UpkeepDay = UpkeepDay,
            },
        };
        s.Calls = new[]
        {
            c[0],
            new ServiceCallDesc { Kind = ServiceCallKind.Critical, Flags = critFlags, OnsiteS = 60, DeadlineS = deadlineS, PerCall = 20 },
            new ServiceCallDesc { Kind = ServiceCallKind.Fire, Flags = 0, OnsiteS = 60, PerCall = 0 },
        };
        d.Town.Services = s;
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(20);
        return sim;
    }

    private static int MedType(CityWorld c)
    {
        for (int i = c.Types.Count - 1; i >= 0; i--)
        {
            if (c.Types.Rows[i].ServiceKind == ServiceKind.Medicine)
            {
                return i;
            }
        }

        Assert.Fail("нет типа станции скорой");
        return -1;
    }

    private static bool FreeLot(LotStore l, int i) => l.Building[i] < 0 && (l.Flags[i] & LotFlags.Lost) == 0 && l.CarLane[i] >= 0 && l.RoadComp[i] >= 0;

    /// <summary>Станция скорой на свободном участке <paramref name="lot"/>; индекс станции.</summary>
    private static int Station(Simulation sim, int lot)
    {
        CityWorld c = sim.World.City!;
        Assert.True(Utilities.BuildDepot(sim.World, c, lot, MedType(c)));
        int bi = c.Lots.Building[lot];
        SiteStore s = c.Services!.Sites;
        for (int i = 0; i < s.Capacity; i++)
        {
            if (s.Building[i] == bi)
            {
                return i;
            }
        }

        Assert.Fail("станция не создана");
        return -1;
    }

    private static int FirstFreeLot(CityWorld c)
    {
        for (int i = 0; i < c.Lots.Count; i++)
        {
            if (FreeLot(c.Lots, i))
            {
                return i;
            }
        }

        Assert.Fail("нет свободного участка");
        return -1;
    }

    /// <summary>Активное здание (не служба) той же дорожной компоненты, что участок <paramref name="lot"/>, на расстоянии до <paramref name="maxM"/> - самое дальнее (или ближайшее).</summary>
    private static int Target(CityWorld c, int lot, int maxM, bool nearest = false, int skip = -1, int skip2 = -1)
    {
        BuildingStore b = c.Buildings;
        LotStore l = c.Lots;
        int best = -1;
        int bestD = nearest ? int.MaxValue : -1;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (i == skip || i == skip2 || !b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || c.Types.Rows[b.Type[i]].Kind == LotZone.Service)
            {
                continue;
            }

            int bl = b.Lot[i];
            if (l.CarLane[bl] < 0 || l.RoadComp[bl] != l.RoadComp[lot])
            {
                continue;
            }

            int dm = l.ManhattanM(lot, bl);
            if (dm <= maxM && (nearest ? dm < bestD : dm > bestD))
            {
                best = i;
                bestD = dm;
            }
        }

        Assert.True(best >= 0, "нет цели вызова");
        return best;
    }

    private static void Call(Simulation sim, ServiceCallKind kind, int target) =>
        Assert.True(Utilities.Call(sim.World, sim.World.City!, Med, kind, target));

    private static void StepUntil(Simulation sim, Func<bool> done, int maxTicks)
    {
        for (int t = 0; t < maxTicks && !done(); t += 20)
        {
            sim.Step(20);
        }

        Assert.True(done(), "условие не наступило за " + maxTicks + " тиков");
    }

    /// <summary>Добавочная OD-нагрузка: <paramref name="n"/> легковых поездок станция → цель (по 4 за тик).</summary>
    private static void Load(Simulation sim, int fromLot, int toLot, int n)
    {
        World w = sim.World;
        LotStore l = w.City!.Lots;
        int added = 0;
        while (added < n)
        {
            for (int k = 0; k < 4 && added < n; k++)
            {
                var trip = new PendingTrip { FromZone = -1, ToZone = -1, FromLane = l.CarLane[fromLot], Type = 0, DestLane = l.CarLane[toLot], DestS = 1f, Route = -1 };
                Assert.True(w.Trips.TryEnqueue(in trip));
                added++;
            }

            sim.Step(1);
        }
    }

    /// <summary>Отклик одного вызова critical от станции на участке <paramref name="lot"/> к самому дальнему зданию в пределах 1.5 км; <paramref name="load"/> - поездок впереди.</summary>
    private static long Response(byte flags, int load)
    {
        using Simulation sim = MakeMed(critFlags: flags);
        CityWorld c = sim.World.City!;
        int lot = FirstFreeLot(c);
        Station(sim, lot);
        int target = Target(c, lot, 1_500);
        if (load > 0)
        {
            Load(sim, lot, c.Buildings.Lot[target], load);
        }
        else
        {
            sim.Step((load + 3) / 4);
        }

        Call(sim, ServiceCallKind.Critical, target);
        CityServices sv = c.Services!;
        StepUntil(sim, () => sv.RecCallsDone[Med] == 1, 40_000);
        Assert.Equal(0L, sv.RecCallsLate[Med]);
        return sv.RecRespTicks[Med];
    }

    [Fact]
    public void Response_GrowsWithOdLoad()
    {
        long free = Response(ServiceCallFlags.Urgent, 0);
        long loaded = Response(ServiceCallFlags.Urgent, 60);
        Assert.True(free > 0);
        Assert.True(loaded > free, $"отклик с нагрузкой {loaded} не больше свободного {free}");
    }

    /// <summary>Σ откликов серии вызовов critical к 8 зданиям, разнесённым по городу (по одному: следующий - когда машина на станции).</summary>
    private static long ResponseTour(byte flags)
    {
        using Simulation sim = MakeMed(critFlags: flags);
        CityWorld c = sim.World.City!;
        CityServices sv = c.Services!;
        int lot = FirstFreeLot(c);
        int site = Station(sim, lot);
        sim.Step(c.DayTicks / 3);   // день: город ездит, светофоры работают на поток
        BuildingStore b = c.Buildings;
        int eligible = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            eligible += Eligible(c, lot, i) ? 1 : 0;
        }

        int stride = Math.Max(1, eligible / 8);
        int seen = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (!Eligible(c, lot, i) || seen++ % stride != stride / 2)
            {
                continue;
            }

            long before = sv.RecCallsDone[Med];
            Call(sim, ServiceCallKind.Critical, i);
            StepUntil(sim, () => sv.RecCallsDone[Med] > before && AtStation(sv, site), 40_000);
        }

        Assert.True(sv.RecCallsDone[Med] >= 6);
        return sv.RecRespTicks[Med];
    }

    private static bool AtStation(CityServices sv, int site)
    {
        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.Site[k] == site && u.State[k] is not (CrewState.Idle or CrewState.OffDuty))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Eligible(CityWorld c, int lot, int i)
    {
        BuildingStore b = c.Buildings;
        return b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind != LotZone.Service && c.Lots.CarLane[b.Lot[i]] >= 0 &&
            c.Lots.RoadComp[b.Lot[i]] == c.Lots.RoadComp[lot] && c.Lots.ManhattanM(lot, b.Lot[i]) >= 400;
    }

    [Fact]
    public void OnCall_Ambulance_IsFaster()
    {
        // та же серия вызовов днём: на вызове машина проходит красный (VC3) - Σ отклика меньше; без флага стоит на светофорах
        long plain = ResponseTour(ServiceCallFlags.Urgent);
        long onCall = ResponseTour(ServiceCallFlags.Urgent | ServiceCallFlags.OnCall);
        Assert.True(onCall < plain, $"на вызове {onCall} не меньше, чем без флага {plain}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnCall_Flags_ByCallKindAndLeg(bool onReturn)
    {
        byte flags = (byte)(ServiceCallFlags.Urgent | ServiceCallFlags.OnCall | (onReturn ? ServiceCallFlags.OnCallReturn : 0));
        using Simulation sim = MakeMed(critFlags: flags, specialOnCall: true);   // demand.special_on_call в режиме служб не действует на их машины
        World w = sim.World;
        CityWorld c = w.City!;
        CityServices sv = c.Services!;
        LifecycleCalibrationTests.PlaceDepots(sim, 3);
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i += 5)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind != LotZone.Service)
            {
                b.Cond[i] = 7_500;   // заявки ЖКХ
            }
        }

        int lot = FirstFreeLot(c);
        Station(sim, lot);
        Call(sim, ServiceCallKind.Critical, Target(c, lot, 1_200));
        bool seenTo = false;
        bool seenBack = false;
        bool seenUtility = false;
        int dayEnd = 2 * c.DayTicks;
        for (int t = 0; t < dayEnd; t += 10)
        {
            sim.Step(10);
            CrewStore u = sv.Crews;
            for (int k = 0; k < u.Capacity; k++)
            {
                int v = u.Vehicle[k];
                if (v < 0 || !w.Vehicles.Pool.IsAlive(v, u.VehicleGen[k]))
                {
                    continue;
                }

                bool flag = (w.Vehicles.Flags[v] & (byte)VehicleFlags.OnCall) != 0;
                if (sv.Sites.Svc[u.Site[k]] != Med)
                {
                    Assert.False(flag, "машина ЖКХ получила OnCall");
                    seenUtility = true;
                }
                else if (u.State[k] == CrewState.ToSite)
                {
                    Assert.True(flag);
                    seenTo = true;
                }
                else if (u.State[k] == CrewState.Return)
                {
                    Assert.Equal(onReturn, flag);
                    seenBack = true;
                }
            }

            if (seenTo && seenBack && seenUtility)
            {
                break;
            }
        }

        Assert.True(seenTo && seenBack && seenUtility, $"к цели {seenTo}, назад {seenBack}, ЖКХ {seenUtility}");
    }

    [Fact]
    public void Overload_UrgentFirst_ThenFarStation()
    {
        using Simulation sim = MakeMed();
        CityWorld c = sim.World.City!;
        CityServices sv = c.Services!;
        int near = FirstFreeLot(c);
        int nearSite = Station(sim, near);
        int far = -1;
        int farD = -1;
        for (int i = 0; i < c.Lots.Count; i++)
        {
            if (FreeLot(c.Lots, i) && c.Lots.RoadComp[i] == c.Lots.RoadComp[near] && c.Lots.ManhattanM(near, i) > farD)
            {
                far = i;
                farD = c.Lots.ManhattanM(near, i);
            }
        }

        int farSite = Station(sim, far);
        int t1 = Target(c, near, 2_000, nearest: true);
        int t2 = Target(c, near, 2_000, nearest: true, skip: t1);
        int t3 = Target(c, near, 2_000, nearest: true, skip: t1, skip2: t2);
        sim.Step((20 - (int)(sim.World.Tick % 20)) % 20);   // вызовы - перед тиком выезда (фаза 9r видит Tick % 20 == 0)
        Call(sim, ServiceCallKind.Fire, t1);       // обычный: первым в очереди
        Call(sim, ServiceCallKind.Critical, t2);   // срочный
        Call(sim, ServiceCallKind.Critical, t3);
        Assert.Equal(3, sim.GetServiceCalls(new ServiceCallInfo[8]));
        Assert.Equal(0L, sim.World.Tick % 20);
        sim.Step(1);
        CrewStore u = sv.Crews;
        int toNear = -1;
        int toFar = -1;
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.State[k] != CrewState.ToSite)
            {
                continue;
            }

            Assert.Equal((byte)ServiceCallKind.Critical, u.Kind[k]);   // обе машины ушли на срочные, обычный ждёт
            if (u.Site[k] == nearSite)
            {
                toNear = u.Target[k];
            }
            else if (u.Site[k] == farSite)
            {
                toFar = u.Target[k];
            }
        }

        Assert.Equal(t2, toNear);   // первый срочный - ближайшая станция
        Assert.Equal(t3, toFar);    // второй - дальняя: на ближней машин нет
        Assert.Equal(1, sv.Normal[Med].Count);
        Assert.True(sim.GetServiceStats(Med, out ServiceStats st));
        Assert.Equal(2, st.Stations);
        Assert.Equal(2, st.UnitsBusy);
        Assert.Equal(1, st.Waiting);
        Assert.Equal(3, sim.GetServiceCalls(new ServiceCallInfo[8]));
    }

    [Fact]
    public void Deadline_LateCallsCounted()
    {
        using Simulation sim = MakeMed(deadlineS: 60);
        CityWorld c = sim.World.City!;
        CityServices sv = c.Services!;
        int lot = FirstFreeLot(c);
        Station(sim, lot);
        int target = Target(c, lot, 1_500);
        for (int i = 0; i < 4; i++)
        {
            Call(sim, ServiceCallKind.Critical, target);   // одна машина на линии: остальные ждут дольше 60 с
        }

        StepUntil(sim, () => sv.RecCallsLate[Med] >= 3 && sv.Normal[Med].Count + sv.Urgent[Med].Count == 0, 20_000);
        Assert.Equal(sv.RecCallsLate[Med], sim.World.Counters.CallsLate);
        Assert.Equal(4L, sv.RecCallsQueued[Med]);
    }

    private static (long Resp, int Duty, long Cost) Budget(int pct)
    {
        using Simulation sim = MakeMed(units: 4);
        CityWorld c = sim.World.City!;
        CityServices sv = c.Services!;
        int lot = FirstFreeLot(c);
        int site = Station(sim, lot);
        Assert.Equal(6, sv.Sites.UnitsMax[site]);   // units + ceil(units / 2)
        Assert.Equal(4, sv.Sites.Duty[site]);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceBudget(Med, pct)));
        Assert.Equal((byte)100, sv.Pct[Med]);   // ожидающий - с границы суток
        int day = c.Day;
        StepUntil(sim, () => c.Day > day, 2 * c.DayTicks);
        Assert.Equal((byte)pct, sv.Pct[Med]);
        int duty = sv.Sites.Duty[site];
        int online = 0;
        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity; k++)
        {
            online += u.Site[k] == site && u.State[k] is not (CrewState.Free or CrewState.OffDuty) ? 1 : 0;
        }

        Assert.Equal(duty, online);
        sim.Step(c.DayTicks / 2);   // проход зданий суток - расход станции списан
        long cost = sv.SvcCostDay[(Med * 2) + (c.Day & 1)];
        Assert.Equal((long)(UpkeepDay + (PerUnitDay * duty)) * pct / 100, cost);   // §2.4: (upkeep + per_unit_day · Duty) · pct / 100, вызовов вчера не было

        int target = Target(c, lot, 1_200);
        for (int i = 0; i < 8; i++)
        {
            Call(sim, ServiceCallKind.Critical, target);
        }

        StepUntil(sim, () => sv.RecCallsDone[Med] == 8, 60_000);
        return (sv.RecRespTicks[Med], duty, cost);
    }

    [Fact]
    public void Budget_Slider_DutyResponseAndCost()
    {
        (long resp50, int duty50, long cost50) = Budget(50);
        (long resp150, int duty150, long cost150) = Budget(150);
        Assert.Equal(2, duty50);
        Assert.Equal(6, duty150);
        Assert.True(resp50 > resp150, $"отклик 50 % {resp50} не хуже 150 % {resp150}");
        Assert.True(cost150 > cost50);
    }

    [Fact]
    public void SetServiceBudget_Validation()
    {
        using Simulation sim = MakeMed();
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetServiceBudget(Med, 55)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetServiceBudget(Med, 160)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetServiceBudget(9, 100)));
        using Simulation bl = ServicesRefactorTests.Make(Scenario, TripExecution.Agents, 1);
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(bl, Command.MakeSetServiceBudget(0, 100)));
    }

    [Fact]
    public void Coverage_RespEwmaAndEstimate()
    {
        using Simulation sim = MakeMed();
        CityWorld c = sim.World.City!;
        int lot = FirstFreeLot(c);
        Station(sim, lot);
        int target = Target(c, lot, 1_000);
        Call(sim, ServiceCallKind.Critical, target);
        StepUntil(sim, () => c.Services!.RecCallsDone[Med] == 1, 20_000);
        int n = sim.ServiceCellCount;
        Assert.True(n > 0);
        var cov = new ushort[n];
        Assert.Equal(n, sim.GetServiceCoverage(Med, cov));
        int tl = c.Buildings.Lot[target];
        int cell = c.Fields!.CellOf(c.Lots.X[tl], c.Lots.Z[tl]);
        long resp = c.Services!.RecRespTicks[Med] / Simulation.TicksPerSecond;
        Assert.Equal((ushort)resp, cov[cell]);   // первое событие клетки - отклик как есть
        Assert.All(cov, v => Assert.True(v > 0));   // пустые клетки - оценка от станции
    }

    private static Simulation Busy(int threads)
    {
        Simulation sim = MakeMed(critFlags: ServiceCallFlags.Urgent | ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn, units: 2, threads: threads);
        CityWorld c = sim.World.City!;
        LifecycleCalibrationTests.PlaceDepots(sim, 2);
        int lot = FirstFreeLot(c);
        Station(sim, lot);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetServiceBudget(Med, 70)));
        int t1 = Target(c, lot, 1_500);
        int t2 = Target(c, lot, 800);
        Call(sim, ServiceCallKind.Critical, t1);
        Call(sim, ServiceCallKind.Fire, t2);
        Call(sim, ServiceCallKind.Critical, t2);
        return sim;
    }

    [Fact]
    public void Dispatch_Hash_SameAt_1_2_3_Threads()
    {
        ulong? first = null;
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = Busy(t);
            sim.Step(sim.World.City!.DayTicks + 2_000);
            Assert.True(sim.World.City!.Services!.RecCallsDone[Med] > 0);
            ulong h = sim.ComputeStateHash();
            first ??= h;
            Assert.Equal(first, h);
        }
    }

    [Fact]
    public void Dispatch_Save_Continues_SameHash()
    {
        using Simulation sim = Busy(1);
        sim.Step(200);   // скорая в пути
        Assert.Contains(sim.World.City!.Services!.Crews.State, s => s == CrewState.ToSite);
        byte[] raw = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(raw);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        int step = sim.World.City!.DayTicks + 1_000;
        sim.Step(step);
        loaded.Step(step);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.True(loaded.World.City!.Services!.RecCallsDone[Med] > 0);
    }

    [Fact]
    public void Dispatch_ZeroAllocations_PerTick()
    {
        using Simulation sim = Busy(1);
        sim.Step(100);
        long bytes = AllocationProbe.Measure(() => sim.Step(400));
        Assert.Equal(0L, bytes);
    }
}
