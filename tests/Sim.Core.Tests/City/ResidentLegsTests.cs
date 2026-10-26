using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.City;

/// <summary>Расписание и ноги micro (Z2, zoning-demand.md §19 «Расписание и ноги»): день жителя машиной, пешком и автобусом, WaitDepart, CZ3, CZ5, CZ6, сейв посреди дня.</summary>
public sealed class ResidentLegsTests
{
    /// <summary>Первый план дня наступает на 9000-12000 тиках (слоты 36..48), первые выезды рабочих - после 11000.</summary>
    private const int PlansDone = 13_000;

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    /// <summary>CZ3: жители по состояниям покрывают всех; в пути ровно столько жителей, сколько агентов с жителем (машины + пешеходы города).</summary>
    private static void AssertConservation(Simulation sim)
    {
        World w = sim.World;
        CityResidents r = City(sim).Residents;
        var states = new int[5];
        for (int i = 0; i < r.HighWater; i++)
        {
            if (r.Pool.IsAlive(i))
            {
                states[r.State[i]]++;
                if (r.State[i] != (byte)ResidentState.InLeg)
                {
                    Assert.Equal(-1, r.Agent[i]);
                }
            }
        }

        Assert.Equal(r.AliveCount, states[0] + states[1] + states[2] + states[3] + states[4]);
        int cars = 0;
        for (int v = 0; v < w.Vehicles.HighWater; v++)
        {
            if (w.Vehicles.Pool.IsAlive(v) && w.Vehicles.Resident[v] >= 0)
            {
                cars++;
            }
        }

        int peds = w.Peds?.P.AliveCount ?? 0;
        Assert.Equal(states[(int)ResidentState.InLeg], cars + peds);
    }

    [Fact]
    public void Day_ConservesResidents_AndLegsClose()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        using Simulation sim = Make(d);
        long people = City(sim).Residents.AliveCount;
        for (int t = 0; t < 40_000; t += 2000)
        {
            sim.Step(2000);
            AssertConservation(sim);
        }

        ref readonly SimCounters k = ref sim.Counters;
        Assert.Equal(people, City(sim).Residents.AliveCount);
        Assert.True(k.ResToursPlanned > 100, $"планов мало: {k.ResToursPlanned}");
        Assert.True(k.ResLegsCar > 100 && k.ResLegsWalk > 10, $"car={k.ResLegsCar} walk={k.ResLegsWalk}");
        Assert.True(k.ResLegsDone > 100, $"завершённых ног мало: {k.ResLegsDone}");
        Assert.Equal(k.ResLegsDone, k.SkimObserved + k.SkimObsDropped);
        Assert.Equal(0, k.ResStuck);
        Assert.Equal(0, k.PedNoPath);
        Assert.Equal(0, k.ResToursCancelled);
        Assert.True(k.CityEvents > k.ResToursPlanned);
    }

    [Fact]
    public void Worker_ByCar_GoesHomeWorkHome()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        using Simulation sim = Make(d);
        sim.Step(PlansDone);
        CityWorld c = City(sim);
        CityResidents r = c.Residents;
        int who = -1;
        for (int i = 0; i < r.HighWater && who < 0; i++)
        {
            if (r.Pool.IsAlive(i) && r.PlanCount[i] > 0 && r.PlanKind[i * 3] == TourKind.Work && r.PlanMode[i * 3] == (byte)TravelMode.Car && r.PlanSlot[i * 3] >= 60)
            {
                who = i;
            }
        }

        Assert.True(who >= 0, "нет рабочего на машине с поздним выездом");
        int work = r.Work[who];
        var seen = new List<ResidentState>();
        long atWorkTicks = 0;
        for (int t = 0; t < 70_000 && (r.PlanCursor[who] == 0 || r.State[who] != (byte)ResidentState.AtHome); t++)
        {
            sim.Step(1);
            var s = (ResidentState)r.State[who];
            if (seen.Count == 0 || seen[^1] != s)
            {
                seen.Add(s);
            }

            atWorkTicks += s == ResidentState.AtWork ? 1 : 0;
        }

        Assert.Contains(ResidentState.InLeg, seen);
        Assert.Contains(ResidentState.AtWork, seen);
        Assert.Equal(ResidentState.AtHome, (ResidentState)r.State[who]);
        Assert.True(r.PlanCursor[who] >= 1);
        Assert.Equal(-1, r.Agent[who]);
        // Смена: не меньше ShiftSlots за вычетом сдвига житель (±3 слота) и опоздания.
        long shift = c.Types.Rows[c.Buildings.Type[work]].ShiftSlots * (long)c.SlotTicks;
        Assert.True(atWorkTicks >= shift - (8 * c.SlotTicks), $"на работе {atWorkTicks} тиков при смене {shift}");
        // Путь туда и обратно: ИнLeg встречается дважды.
        int legs = 0;
        for (int i = 0; i < seen.Count; i++)
        {
            legs += seen[i] == ResidentState.InLeg ? 1 : 0;
        }

        Assert.True(legs >= 2, $"ног {legs}, состояния: {string.Join(",", seen)}");
        AssertConservation(sim);
    }

    [Fact]
    public void Walker_WalksToWorkAndBack()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        using Simulation sim = Make(d);
        sim.Step(PlansDone);
        CityWorld c = City(sim);
        CityResidents r = c.Residents;
        int who = -1;
        for (int i = 0; i < r.HighWater && who < 0; i++)
        {
            if (r.Pool.IsAlive(i) && r.PlanCount[i] > 0 && r.PlanKind[i * 3] == TourKind.Work && r.PlanSlot[i * 3] >= 60)
            {
                int lh = c.Buildings.Lot[r.Home[i]];
                int lw = c.Buildings.Lot[r.Work[i]];
                if (TripLegs.PedFeasible(sim.World, c, lh, lw) && c.Lots.EuclidM(lh, lw) is > 100 and < 700)
                {
                    who = i;
                }
            }
        }

        Assert.True(who >= 0, "нет рабочего с пешеходной работой рядом");
        r.PlanMode[who * 3] = (byte)TravelMode.Walk;
        long pedsBefore = sim.Counters.PedTripsCompleted;
        long walkBefore = sim.Counters.ResLegsWalk;
        bool sawWalking = false;
        for (int t = 0; t < 70_000 && (r.PlanCursor[who] == 0 || r.State[who] != (byte)ResidentState.AtHome); t++)
        {
            sim.Step(1);
            if (r.State[who] == (byte)ResidentState.InLeg)
            {
                Assert.Equal((byte)TravelMode.Walk, r.LegMode[who]);
                Assert.True(sim.World.Peds!.P.Pool.IsAlive(r.Agent[who], r.AgentGen[who]));
                Assert.Equal(who, sim.World.Peds.P.Resident[r.Agent[who]]);
                sawWalking = true;
            }
        }

        Assert.True(sawWalking);
        Assert.Equal(ResidentState.AtHome, (ResidentState)r.State[who]);
        Assert.True(sim.Counters.ResLegsWalk >= walkBefore + 2);   // две ноги этого жителя среди прочих
        Assert.True(sim.Counters.PedTripsCompleted >= pedsBefore + 2);
        AssertConservation(sim);
    }

    [Fact]
    public void Rider_TakesTheBus_WhenPlanned()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        using Simulation sim = Make(d);
        sim.Step(PlansDone);
        World w = sim.World;
        CityWorld c = City(sim);
        CityResidents r = c.Residents;
        int who = -1;
        for (int i = 0; i < r.HighWater && who < 0; i++)
        {
            if (r.Pool.IsAlive(i) && r.PlanCount[i] > 0 && r.PlanKind[i * 3] == TourKind.Work && r.PlanSlot[i * 3] >= 60)
            {
                int lh = c.Buildings.Lot[r.Home[i]];
                int lw = c.Buildings.Lot[r.Work[i]];
                if (TripLegs.PedFeasible(w, c, lh, lw) &&
                    w.Peds!.FindBusPair(w, c.Lots.PedEdge[lh], c.Lots.PedSMm[lh], c.Lots.PedEdge[lw], c.Lots.PedSMm[lw], out _, out _) &&
                    w.Peds.FindBusPair(w, c.Lots.PedEdge[lw], c.Lots.PedSMm[lw], c.Lots.PedEdge[lh], c.Lots.PedSMm[lh], out _, out _))
                {
                    who = i;
                }
            }
        }

        Assert.True(who >= 0, "нет жителя с автобусной парой туда и обратно");
        r.PlanMode[who * 3] = (byte)TravelMode.Bus;
        long boardings = sim.Counters.PedStopBoardings;
        long busLegs = sim.Counters.ResLegsBus;
        bool sawBusLeg = false;
        int ticks = 0;
        for (; ticks < 90_000 && (r.PlanCursor[who] == 0 || r.State[who] != (byte)ResidentState.AtHome); ticks++)
        {
            sim.Step(1);
            if (r.State[who] == (byte)ResidentState.InLeg && r.LegMode[who] == (byte)TravelMode.Bus)
            {
                sawBusLeg = true;
                Assert.True(r.LegStopA[who] >= 0 && r.LegStopB[who] > r.LegStopA[who]);
            }
        }

        Assert.True(sawBusLeg, "нога на автобусе не началась");
        Assert.Equal(ResidentState.AtHome, (ResidentState)r.State[who]);
        Assert.True(sim.Counters.ResLegsBus >= busLegs + 2);
        Assert.True(sim.Counters.PedStopBoardings >= boardings + 2, "житель не садился в автобус дважды");
        Assert.Equal(0, sim.Counters.ResStuck);
        AssertConservation(sim);
    }

    [Fact]
    public void WaitDepart_Deadline_SwitchesModeOrCancelsTour()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        d.Town.Residents.WaitMaxTicks = 200;
        using Simulation sim = Make(d);
        World w = sim.World;
        // Очередь поездок забита поездками без маршрута и зоны: места нет, заявки жителей остаются в WaitDepart.
        var junk = new PendingTrip { FromZone = -1, ToZone = -1, FromLane = -1, DestLane = -1, Route = -1 };
        while (w.Trips.TryEnqueue(in junk))
        {
        }

        CityResidents r = City(sim).Residents;
        long maxWait = 0;
        for (int t = 0; t < 30_000; t += 50)
        {
            sim.Step(50);
            for (int i = 0; i < r.HighWater; i++)
            {
                if (r.Pool.IsAlive(i) && r.State[i] == (byte)ResidentState.WaitDepart)
                {
                    maxWait = Math.Max(maxWait, sim.Tick - r.LegReq[i]);
                }
            }
        }

        ref readonly SimCounters k = ref sim.Counters;
        Assert.True(maxWait <= 200 + 1, $"житель ждал {maxWait} тиков при дедлайне 200");
        Assert.True(k.ResToursCancelled > 0, "по дедлайну ни один тур не отменён");
        Assert.True(k.ResLegsWalk > 0, "нет смены способа на пеший");
        for (int v = 0; v < w.Vehicles.HighWater; v++)
        {
            Assert.False(w.Vehicles.Pool.IsAlive(v) && w.Vehicles.Resident[v] >= 0, "машина жителя вышла при забитой очереди поездок");
        }

        // Житель, чей тур отменён, остался дома с корректным следующим событием.
        for (int i = 0; i < r.HighWater; i++)
        {
            if (r.Pool.IsAlive(i) && r.State[i] == (byte)ResidentState.AtHome)
            {
                Assert.True(r.EvTick[i] >= sim.Tick, $"житель {i} дома без будущего события");
            }
        }

        AssertConservation(sim);
    }

    [Fact]
    public void TwoDays_PlansRepeatDaily()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        d.Town.Calendar.DayTicks = 28_800;   // 1440 с на сутки: слот = 100 тиков
        using Simulation sim = Make(d);
        sim.Step(28_800);
        CityWorld c = City(sim);
        Assert.Equal(1, c.Day);
        long day1 = sim.Counters.ResToursPlanned;
        Assert.True(day1 > 100);
        sim.Step(28_800);
        Assert.Equal(2, c.Day);
        Assert.Equal(2, sim.Counters.CityDays);
        long day2 = sim.Counters.ResToursPlanned - day1;
        Assert.InRange(day2, day1 / 2, day1 * 2);
        AssertConservation(sim);
    }

    [Fact]
    public void Hash_IsIndependentOfThreadCount()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        ulong[] hashes = new ulong[3];
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Make(d, threads);
            sim.Step(18_000);
            hashes[threads - 1] = sim.ComputeStateHash();
            Assert.True(sim.Counters.ResLegsCar > 0);
        }

        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(hashes[0], hashes[2]);
    }

    [Fact]
    public void SaveLoad_MidDay_ContinuesIdentically_AndOpenLegsClose()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        using Simulation a = Make(d);
        a.Step(22_000);
        CityResidents ra = City(a).Residents;
        int waiting = 0;
        int inCar = 0;
        int onFoot = 0;
        for (int i = 0; i < ra.HighWater; i++)
        {
            if (!ra.Pool.IsAlive(i))
            {
                continue;
            }

            waiting += ra.State[i] == (byte)ResidentState.WaitDepart ? 1 : 0;
            inCar += ra.State[i] == (byte)ResidentState.InLeg && ra.LegMode[i] == (byte)TravelMode.Car ? 1 : 0;
            onFoot += ra.State[i] == (byte)ResidentState.InLeg && ra.LegMode[i] != (byte)TravelMode.Car ? 1 : 0;
        }

        Assert.True(inCar > 10 && onFoot > 0, $"в момент сейва мало жителей в пути: car={inCar} foot={onFoot} wait={waiting}");
        byte[] raw = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(raw);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        long doneBefore = b.Counters.ResLegsDone;
        for (int t = 0; t < 3000; t += 250)
        {
            a.Step(250);
            b.Step(250);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }

        Assert.True(b.Counters.ResLegsDone > doneBefore + 20, "ноги, открытые на момент сейва, не закрылись");
        AssertConservation(b);
    }

    [Fact]
    public void Step_AllocatesZeroBytes_WithResidentsMoving()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        using Simulation sim = Make(d);
        sim.Step(20_000);
        Assert.True(sim.World.Vehicles.AliveCount + sim.World.Peds!.P.AliveCount > 100);
        long bytes = AllocationProbe.Measure(() => sim.Step(100), out _);
        Assert.Equal(0, bytes);
    }
}
