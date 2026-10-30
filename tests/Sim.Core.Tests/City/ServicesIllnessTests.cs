using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Metrics;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Xunit;

namespace Sim.Core.Tests.City;

/// <summary>
/// Болезнь и медицина SR3 (services.md §2.5-§2.6, §8 тест 7): частота броска хэшем = ppm, больные реже ходят на работу, лёгкий идёт к ближайшей станции с местом, мест нет - болеет
/// дольше и жалоба <c>NoCare</c> со второго дня, стационар держит койку <c>days</c> суток, критический - скорая и реанимация (micro = агрегат по событиям), опоздание и нет станции -
/// житель уезжает и жалоба <c>NoAmbulance</c>; детерминизм 1/2/3 потока, сейв посреди событий, 0 Б/тик. Тестовая запись «медцентр» (визиты light/inpatient, вызов critical, машина
/// ambulance) поверх записи ЖКХ - в малом городе фикстуры BL.
/// </summary>
public sealed class ServicesIllnessTests
{
    private const string Scenario = "s1k-res-bl";
    private const int Med = 1;
    private const int Light = Illness.Light;
    private const int Inpatient = Illness.Inpatient;
    private const int Critical = Illness.Critical;

    private struct Opt
    {
        public TripExecution Exec;
        public int Threads;
        public int LightPpm;
        public int InpatientPpm;
        public int CriticalPpm;
        public int CapacityDay;
        public int Beds;
        public int Icu;
        public int DeadlineS;
        public int Units;
    }

    private static Opt Defaults() => new() { Exec = TripExecution.Agents, Threads = 1, CapacityDay = 1_000, Beds = 50, Icu = 10, Units = 2 };

    private static Simulation Make(Opt o)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(Scenario, o.Exec);
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
        Assert.True(amb >= 0);

        CityServicesDesc s = CityServicesDesc.Default();
        CityServicesDesc.BaseRecords(depot, types[depot].VehicleType, out ServiceRecordDesc[] r, out ServiceLevelDesc[] l, out ServiceCallDesc[] c);
        s.Records = new[] { r[0], new ServiceRecordDesc { Fund = ServiceFund.City, Rev = 1, FirstLevel = 1, LevelCount = 1, FirstCall = 1, CallCount = 1, FirstVisit = 0, VisitCount = 2 } };
        s.Levels = new[]
        {
            l[0],
            new ServiceLevelDesc
            {
                Type = all.Length - 1, Roles = ServiceRole.Light | ServiceRole.Inpatient | ServiceRole.Critical, LotMinF = 1, LotMinD = 1, DispatchVehicle = (short)amb, Units = o.Units,
                UpkeepDay = 500, PerUnitDay = 100, CapacityDay = o.CapacityDay, Beds = o.Beds, Icu = o.Icu, OpenSlot = 0, CloseSlot = 288, StaySlots = 2,
            },
        };
        s.Calls = new[] { c[0], new ServiceCallDesc { Kind = ServiceCallKind.Critical, Flags = ServiceCallFlags.Urgent | ServiceCallFlags.OnCall, OnsiteS = 60, DeadlineS = o.DeadlineS, PerCall = 20 } };
        s.Visits = new[]
        {
            new ServiceVisitDesc { Reason = ServiceVisitReason.Light, MinRole = ServiceRole.Light, PerVisit = 5 },
            new ServiceVisitDesc { Reason = ServiceVisitReason.Inpatient, MinRole = ServiceRole.Inpatient, PerVisit = 30 },
        };
        s.IllLightPpm = o.LightPpm;
        s.IllInpatientPpm = o.InpatientPpm;
        s.IllCriticalPpm = o.CriticalPpm;
        d.Town.Services = s;
        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(in d));
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = o.Threads });
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

        Assert.Fail("нет типа медцентра");
        return -1;
    }

    private static bool FreeLot(LotStore l, int i) => l.Building[i] < 0 && (l.Flags[i] & LotFlags.Lost) == 0 && l.CarLane[i] >= 0 && l.RoadComp[i] >= 0;

    /// <summary>Свободный участок: первый, либо самый дальний от <paramref name="from"/> в той же дорожной компоненте.</summary>
    private static int FreeLotAt(CityWorld c, int from = -1)
    {
        LotStore l = c.Lots;
        int best = -1;
        int bestD = -1;
        for (int i = 0; i < l.Count; i++)
        {
            if (!FreeLot(l, i))
            {
                continue;
            }

            if (from < 0)
            {
                return i;
            }

            int dm = l.ManhattanM(from, i);
            if (l.RoadComp[i] == l.RoadComp[from] && dm > bestD)
            {
                best = i;
                bestD = dm;
            }
        }

        Assert.True(best >= 0, "нет свободного участка");
        return best;
    }

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

    /// <summary>Житель дома, ждущий плана дня, - ближайший домом к участку <paramref name="lot"/> (та же компонента), кроме <paramref name="skip"/>.</summary>
    private static int Resident(CityWorld c, int lot, int skip = -1)
    {
        CityResidents res = c.Residents;
        LotStore l = c.Lots;
        int best = -1;
        int bestD = int.MaxValue;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (r == skip || !res.IsAlive(r) || res.State[r] != (byte)ResidentState.AtHome || res.EvKind[r] != ResidentEvent.Plan || res.Home[r] < 0 || res.Ill[r] != 0)
            {
                continue;
            }

            int hl = c.Buildings.Lot[res.Home[r]];
            int dm = l.ManhattanM(lot, hl);
            if (l.RoadComp[hl] == l.RoadComp[lot] && l.CarLane[hl] >= 0 && dm < bestD)
            {
                best = r;
                bestD = dm;
            }
        }

        Assert.True(best >= 0, "нет жителя дома");
        return best;
    }

    private static void StepUntil(Simulation sim, Func<bool> done, int maxTicks, int step = 20)
    {
        for (int t = 0; t < maxTicks && !done(); t += step)
        {
            sim.Step(step);
        }

        Assert.True(done(), "условие не наступило за " + maxTicks + " тиков");
    }

    // ------------------------------------------------------------------ бросок

    [Fact]
    public void Roll_FrequencyMatchesPpm()
    {
        CityServicesDesc s = CityServicesDesc.Default();
        int[] n = new int[4];
        int[] dis = new int[4];
        const int Samples = 1_000_000;
        for (int i = 0; i < Samples; i++)
        {
            int u = Illness.RollPpm(0x5eedUL, i / 1_000, i % 1_000, i & 7);
            n[Illness.SeverityOf(u, in s, 100)]++;
            dis[Illness.SeverityOf(u, in s, s.DisorderIllPct)]++;
        }

        // 10⁶ бросков: ожидание ppm, допуск 10 % (σ ≈ 1.8 % у лёгкой, 5.8 % у критической)
        Assert.InRange(n[Light], 2_700, 3_300);
        Assert.InRange(n[Inpatient], 540, 660);
        Assert.InRange(n[Critical], 270, 330);
        Assert.InRange(dis[Light] + dis[Inpatient] + dis[Critical], 3_900 * 125 / 100 * 9 / 10, 3_900 * 125 / 100 * 11 / 10);   // беспорядок рядом: × disorder_ill_pct
        Assert.Equal(Illness.RollPpm(7, 3, 11, 2), Illness.RollPpm(7, 3, 11, 2));   // без состояния: тот же житель в те же сутки - тот же бросок
        Assert.NotEqual(Illness.RollPpm(7, 3, 11, 2), Illness.RollPpm(7, 3, 11, 3));   // другое поколение слота - другой житель
    }

    // ------------------------------------------------------------------ лёгкие и стационар

    [Fact]
    public void Sick_FewerWorkTours_AndWages()
    {
        Opt healthy = Defaults();
        Opt sick = Defaults();
        sick.LightPpm = 300_000;
        using Simulation a = Make(healthy);
        using Simulation b = Make(sick);
        int days = a.World.City!.DayTicks;
        a.Step(days);
        b.Step(days);
        Assert.Equal(0L, a.World.Counters.IllStarted);
        Assert.True(b.World.Counters.IllStarted > 100);
        Assert.True(b.World.Counters.ResToursPlanned < a.World.Counters.ResToursPlanned * 9 / 10, $"туров {b.World.Counters.ResToursPlanned} против {a.World.Counters.ResToursPlanned}");
        long wa = a.World.City!.Ledger.FlowTotal[(int)FlowKind.Wage];
        long wb = b.World.City!.Ledger.FlowTotal[(int)FlowKind.Wage];
        Assert.True(wb < wa, $"зарплата больных {wb} не меньше {wa}");
        Assert.True(b.World.Counters.CareRefused > 0);   // станций нет: отказ
    }

    [Fact]
    public void Light_GoesToNearestStationWithRoom()
    {
        Opt o = Defaults();
        o.LightPpm = 200_000;
        o.CapacityDay = 4;
        using Simulation sim = Make(o);
        CityWorld c = sim.World.City!;
        int nearLot = FreeLotAt(c);
        int near = Station(sim, nearLot);
        int far = Station(sim, FreeLotAt(c, nearLot));
        CityServices sv = c.Services!;
        sim.Step(c.DayTicks - 200 - (int)sim.World.Tick);   // сутки 0: планы, записи и приёмы
        int vNear = c.Buildings.VisitsDay[Economy.Slot(sv.Sites.Building[near], 0)];
        int vFar = c.Buildings.VisitsDay[Economy.Slot(sv.Sites.Building[far], 0)];
        Assert.Equal(4, vNear);   // ближняя заполнена до capacity_day
        Assert.Equal(4, vFar);    // остальные - к дальней, пока там есть место
        Assert.InRange(sim.World.Counters.CareVisits, 1L, 8L);   // приёмы - не больше записей (последние могут быть ещё в пути)
        Assert.True(sim.World.Counters.CareRefused > 0);   // мест на всех не хватило
        Assert.True(sim.GetServiceStats(Med, out ServiceStats st));
        Assert.Equal(2, st.Stations);
        Assert.Equal(8, st.VisitsToday);
        var care = new ushort[sim.ServiceCellCount];
        Assert.Equal(care.Length, sim.GetCareCoverage(care));
        Assert.Contains(care, v => v > 0);
    }

    /// <summary>Житель <paramref name="r"/> заболевает тяжестью <paramref name="sev"/> на <paramref name="days"/> суток без лечения (как бросок, без вызова скорой для некритических).</summary>
    private static void SetIll(CityWorld c, int r, int sev, int days, bool refused = false)
    {
        c.Residents.Ill[r] = Illness.Make(sev, days, false);
        if (refused)
        {
            c.Residents.Flags[r] |= ResidentFlags.CareRefused;
        }
    }

    [Fact]
    public void NoRoom_LongerIllness_AndNoCareOnSecondDay()
    {
        using Simulation sim = Make(Defaults());   // запись с визитами есть, станций нет
        CityWorld c = sim.World.City!;
        int r = Resident(c, FreeLotAt(c));
        SetIll(c, r, Light, 2, refused: true);   // заболел вчера (2 · ill_days), вчера отказ
        int home = c.Residents.Home[r];
        int hgen = c.Buildings.Pool.GenerationOf(home);
        StepUntil(sim, () => Illness.Days(c.Residents.Ill[r]) == 1, c.DayTicks + 4_000, step: 1);
        Assert.Equal(1L, sim.World.Counters.CareRefused);
        Assert.Equal(1, c.Kpi.Complaints.Pending(ComplaintKind.NoCare, home, hgen));   // второй отказ подряд - случай и жалоба у дома
        Assert.Equal(0, (int)c.Residents.PlanCount[r]);   // больной без приёма сидит дома
        StepUntil(sim, () => c.Residents.Ill[r] == 0, c.DayTicks + 4_000);   // без лечения - ещё сутки
        Assert.Equal(0, c.Residents.Flags[r] & ResidentFlags.CareRefused);
    }

    [Fact]
    public void Inpatient_OccupiesBedForDays_AggregateEqualsMicro()
    {
        (long visits, int bedsAfterVisit, byte ill, int releaseDays) = InpatientRun(TripExecution.Agents);
        Assert.Equal(1L, visits);
        Assert.Equal(1, bedsAfterVisit);
        Assert.True(Illness.IsTreated(ill));
        Assert.Equal(2, Illness.Days(ill));
        Assert.Equal(2, releaseDays);   // 6 суток без лечения: в день плана 5, приём снимает ill_days = 3 - койка на 2 суток
        Assert.Equal((visits, bedsAfterVisit, ill, releaseDays), InpatientRun(TripExecution.Aggregate));   // агрегат = micro по событиям
    }

    private static (long Visits, int Beds, byte Ill, int ReleaseDays) InpatientRun(TripExecution exec)
    {
        Opt o = Defaults();
        o.Exec = exec;
        using Simulation sim = Make(o);
        CityWorld c = sim.World.City!;
        int lot = FreeLotAt(c);
        int site = Station(sim, lot);
        CityServices sv = c.Services!;
        int r = Resident(c, lot);
        SetIll(c, r, Inpatient, 6);
        StepUntil(sim, () => sim.World.Counters.CareVisits == 1, 2 * c.DayTicks);
        int beds = sv.Sites.BedsUsed[site];
        byte ill = c.Residents.Ill[r];
        int admitted = c.Day;
        StepUntil(sim, () => sv.Sites.BedsUsed[site] == 0, 4 * c.DayTicks, step: 200);
        return (sim.World.Counters.CareVisits, beds, ill, c.Day - admitted);
    }

    // ------------------------------------------------------------------ критический

    [Theory]
    [InlineData(TripExecution.Agents)]
    [InlineData(TripExecution.Aggregate)]
    public void Critical_Ambulance_ToIcu_AndOverflow(TripExecution exec)
    {
        (long admitted, long overflow, long lost, byte ill1, byte ill2, int icu) = CriticalRun(exec);
        Assert.Equal(2L, admitted);
        Assert.Equal(1L, overflow);   // icu = 1: второй принят сверх мест
        Assert.Equal(0L, lost);
        Assert.Equal(2, icu);
        Assert.True(Illness.IsTreated(ill1) && Illness.IsTreated(ill2));
        Assert.Equal(5, Illness.Days(ill1));    // 10 − ill_days 5
        Assert.Equal(10, Illness.Days(ill2));   // сверх мест - без сокращения (×2)
        if (exec == TripExecution.Aggregate)
        {
            Assert.Equal(CriticalRun(TripExecution.Agents), (admitted, overflow, lost, ill1, ill2, icu));
        }
    }

    private static (long Admitted, long Overflow, long Lost, byte Ill1, byte Ill2, int Icu) CriticalRun(TripExecution exec)
    {
        Opt o = Defaults();
        o.Exec = exec;
        o.Icu = 1;
        o.Units = 1;
        using Simulation sim = Make(o);
        CityWorld c = sim.World.City!;
        int lot = FreeLotAt(c);
        int site = Station(sim, lot);
        int r1 = Resident(c, lot);
        int r2 = Resident(c, lot, r1);
        Illness.Onset(sim.World, c, r1, Critical);
        Illness.Onset(sim.World, c, r2, Critical);
        Assert.Equal(2L, c.Services!.RecCallsQueued[Med]);
        StepUntil(sim, () => sim.World.Counters.IcuAdmitted == 2, 30_000);
        return (sim.World.Counters.IcuAdmitted, sim.World.Counters.IcuOverflow, sim.World.Counters.ResidentsLostNoAmbulance, c.Residents.Ill[r1], c.Residents.Ill[r2],
            c.Services!.Sites.IcuUsed[site]);
    }

    [Fact]
    public void Critical_NoStation_ResidentLeaves_NoAmbulance()
    {
        using Simulation sim = Make(Defaults());
        CityWorld c = sim.World.City!;
        int r = Resident(c, FreeLotAt(c));
        int home = c.Residents.Home[r];
        int hgen = c.Buildings.Pool.GenerationOf(home);
        Illness.Onset(sim.World, c, r, Critical);
        sim.Step(40);   // диспетчер (каждые 20 тиков): станции с ролью нет - как опоздание
        Assert.Equal(1L, sim.World.Counters.ResidentsLostNoAmbulance);
        Assert.Equal(1L, sim.World.Counters.CallsLate);
        Assert.NotEqual(0, c.Residents.Flags[r] & ResidentFlags.Leaving);
        Assert.Equal(1, c.Kpi.Complaints.Pending(ComplaintKind.NoAmbulance, home, hgen));
    }

    [Fact]
    public void Critical_Late_ResidentLeaves()
    {
        Opt o = Defaults();
        o.DeadlineS = 60;   // минимум (= onsite_s): от станции на другом краю не успеть
        using Simulation sim = Make(o);
        CityWorld c = sim.World.City!;
        int lot = FreeLotAt(c);
        Station(sim, lot);
        int r = Resident(c, FreeLotAt(c, lot));   // житель - ближе к дальнему краю
        Illness.Onset(sim.World, c, r, Critical);
        StepUntil(sim, () => sim.World.Counters.ResidentsLostNoAmbulance == 1, 20_000);
        Assert.Equal(0L, sim.World.Counters.IcuAdmitted);
        Assert.NotEqual(0, c.Residents.Flags[r] & ResidentFlags.Leaving);
        StepUntil(sim, () => !c.Residents.IsAlive(r) || c.Residents.Home[r] < 0, 2 * c.DayTicks, step: 200);   // уехал из города в ближайшей точке «дома»
    }

    [Fact]
    public void Critical_NoRecord_ResidentLeaves()
    {
        using Simulation sim = ServicesRefactorTests.Make(Scenario, TripExecution.Agents, 1, mode: true);   // режим только с ЖКХ: скорой нет вовсе
        CityWorld c = sim.World.City!;
        int r = Resident(c, FreeLotAt(c));
        Illness.Onset(sim.World, c, r, Critical);
        Assert.Equal(1L, sim.World.Counters.ResidentsLostNoAmbulance);
    }

    // ------------------------------------------------------------------ детерминизм, сейв, аллокации

    private static Simulation Busy(int threads)
    {
        Opt o = Defaults();
        o.Threads = threads;
        o.LightPpm = 60_000;
        o.InpatientPpm = 20_000;
        o.CriticalPpm = 3_000;
        o.Beds = 8;
        o.Icu = 2;
        o.CapacityDay = 40;
        Simulation sim = Make(o);
        CityWorld c = sim.World.City!;
        int lot = FreeLotAt(c);
        Station(sim, lot);
        Station(sim, FreeLotAt(c, lot));
        return sim;
    }

    [Fact]
    public void Illness_Hash_SameAt_1_2_3_Threads()
    {
        ulong? first = null;
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = Busy(t);
            sim.Step(sim.World.City!.DayTicks / 2);   // планы 03:00, приёмы и скорая к полудню
            ref readonly SimCounters k = ref sim.World.Counters;
            Assert.True(k.CareVisits > 0 && k.IcuAdmitted > 0 && k.IllStarted > 0, $"visits {k.CareVisits}, icu {k.IcuAdmitted}");
            ulong h = sim.ComputeStateHash();
            first ??= h;
            Assert.Equal(first, h);
        }
    }

    [Fact]
    public void Illness_Save_MidEvents_SameHash()
    {
        using Simulation sim = Busy(1);
        CityWorld c = sim.World.City!;
        CityServices sv = c.Services!;
        StepUntil(sim, () => HasPatient(sv) && sim.World.Counters.CareVisits > 0, 2 * c.DayTicks);   // скорая везёт пациента, приёмы были
        byte[] raw = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(raw);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        sim.Step(c.DayTicks / 2);
        loaded.Step(c.DayTicks / 2);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    private static bool HasPatient(CityServices sv)
    {
        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.Ref[k] >= 0)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Illness_ZeroAllocations_PerTick()
    {
        using Simulation sim = Busy(1);
        CityWorld c = sim.World.City!;
        sim.Step(c.DayTicks + 6_000);   // прогрев: сутки событий
        long bytes = AllocationProbe.Measure(() => sim.Step(6_000));   // окно планов дня (03:00-04:05): бросок, выбор станции, вызовы
        Assert.Equal(0L, bytes);
    }
}
