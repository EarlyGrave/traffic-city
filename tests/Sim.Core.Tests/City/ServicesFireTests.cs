using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Xunit;

namespace Sim.Core.Tests.City;

/// <summary>
/// Пожар SR4 (services.md §2.7, §8 тест 6 - механика; решение владельца 10.1 Б): бросок возгорания, стадии (тление, огонь, заброшено, сгорело дотла), тушение пожарной через диспетчер
/// (<c>Ceil −= ущерб / 2</c>), перекидывание на дом той же клетки, потолок одновременных очагов (пул и очередь вызовов не растут лавиной), детерминизм 1/2/3 потока, сейв посреди огня, 0 Б/тик.
/// Малый город фикстуры BL, пожарная часть - тестовая запись «fire» (вызов fire срочный с приоритетом) поверх записи ЖКХ.
/// </summary>
public sealed class ServicesFireTests
{
    private const string Scenario = "s1k-res-bl";
    private const int Fire = 1;   // индекс тестовой записи (0 - utility)

    private static Simulation Make(int ppm = 0, int cap = 32, int spreadPpm = 0, int maxFires = 256, int smolderS = 120, int threads = 1, bool station = true)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(Scenario, TripExecution.Agents);
        BuildingTypeDesc[] types = d.Town.BuildingTypes is { Length: > 0 } bt ? bt : CityDesc.BaseBuildingTypes();
        int depot = Array.FindIndex(types, t => t.ServiceKind == ServiceKind.Utility);
        Assert.True(depot >= 0);
        var all = new BuildingTypeDesc[types.Length + 1];
        types.CopyTo(all, 0);
        for (int i = 0; i < types.Length; i++)
        {
            if (all[i].Kind is LotZone.Residential or LotZone.Commercial or LotZone.Industrial)
            {
                all[i].FirePpm = ppm;
            }
        }

        BuildingTypeDesc fire = types[depot];
        fire.ServiceKind = ServiceKind.Fire;
        fire.Vehicles = 0;
        fire.VehicleType = byte.MaxValue;
        fire.BuildCost = 0;
        all[^1] = fire;
        d.Town.BuildingTypes = all;
        int truck = Array.FindIndex(d.VehicleTypes, t => t.Category == VehicleCategory.Special);
        Assert.True(truck >= 0 && truck != types[depot].VehicleType);

        CityServicesDesc s = CityServicesDesc.Default();
        CityServicesDesc.BaseRecords(depot, types[depot].VehicleType, out ServiceRecordDesc[] r, out ServiceLevelDesc[] l, out ServiceCallDesc[] c);
        s.Records = new[] { r[0], new ServiceRecordDesc { Fund = ServiceFund.City, Rev = 1, FirstLevel = 1, LevelCount = 1, FirstCall = 1, CallCount = 1 } };
        s.Levels = new[]
        {
            l[0],
            new ServiceLevelDesc { Type = all.Length - 1, Roles = ServiceRole.Fire, LotMinF = 1, LotMinD = 1, DispatchVehicle = (short)truck, Units = 2, PerUnitDay = 100, UpkeepDay = 500 },
        };
        s.Calls = new[]
        {
            c[0],
            new ServiceCallDesc { Kind = ServiceCallKind.Fire, Flags = ServiceCallFlags.Urgent | ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn, OnsiteS = 60, PerCall = 200 },
        };
        s.IllLightPpm = 0;
        s.IllInpatientPpm = 0;
        s.IllCriticalPpm = 0;
        s.FireCap = cap;
        s.FireSpreadPpm = spreadPpm;
        s.MaxFires = maxFires;
        s.SmolderS = smolderS;
        d.Town.Services = s;
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(20);
        if (station)
        {
            CityWorld cw = sim.World.City!;
            Assert.True(Utilities.BuildDepot(sim.World, cw, FirstFreeLot(cw), cw.Types.Count - 1));
        }

        return sim;
    }

    private static int FirstFreeLot(CityWorld c)
    {
        LotStore l = c.Lots;
        for (int i = 0; i < l.Count; i++)
        {
            if (l.Building[i] < 0 && (l.Flags[i] & LotFlags.Lost) == 0 && l.CarLane[i] >= 0 && l.RoadComp[i] >= 0)
            {
                return i;
            }
        }

        Assert.Fail("нет свободного участка");
        return -1;
    }

    private static bool Flammable(CityWorld c, int bi) =>
        c.Buildings.IsAlive(bi) && c.Buildings.State[bi] == (byte)BuildingState.Active && c.Types.Rows[c.Buildings.Type[bi]].Kind is LotZone.Residential or LotZone.Commercial or LotZone.Industrial;

    /// <summary>Активное здание R/C/I в дорожной компоненте станции (чтобы бригада доехала) - k-е по порядку.</summary>
    private static int Pick(CityWorld c, int k = 0)
    {
        SiteStore s = c.Services!.Sites;
        int comp = -1;
        for (int i = 0; i < s.Capacity && comp < 0; i++)
        {
            if (s.Building[i] >= 0 && s.Svc[i] == Fire)
            {
                comp = c.Lots.RoadComp[c.Buildings.Lot[s.Building[i]]];
            }
        }

        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            if (Flammable(c, i) && c.Lots.CarLane[c.Buildings.Lot[i]] >= 0 && (comp < 0 || c.Lots.RoadComp[c.Buildings.Lot[i]] == comp) && k-- == 0)
            {
                return i;
            }
        }

        Assert.Fail("нет здания для поджога");
        return -1;
    }

    private static void StepUntil(Simulation sim, Func<bool> done, int maxTicks)
    {
        for (int t = 0; t < maxTicks && !done(); t += 20)
        {
            sim.Step(20);
        }

        Assert.True(done(), "условие не наступило за " + maxTicks + " тиков");
    }

    private static bool Burning(CityWorld c, int bi) => (c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Burning) != 0;

    [Fact]
    public void Fire_Stages_Burnout_WithoutStation()
    {
        using Simulation sim = Make(station: false);
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Pick(c);
        int cond0 = c.Buildings.Cond[bi];
        Assert.True(Fires.Start(w, c, bi));
        Assert.True(Burning(c, bi));
        Assert.Equal(1L, w.Counters.FiresStarted);
        Assert.Equal(1, c.Services!.Fires.Used());

        // тление: 5 б.п. в секунду (smolder_s = 120 с)
        sim.Step(20 * 20);
        Assert.Equal(cond0 - (19 * Fires.SmolderBpS), (int)c.Buildings.Cond[bi]);   // первая секунда - загорание, шаг со следующей

        // открытый огонь: 15 б.п. в секунду; ниже 50 % - заброшено (BL), без пожарной горит дотла
        StepUntil(sim, () => c.Buildings.State[bi] == (byte)BuildingState.Abandoned, 20_000);
        Assert.True(Burning(c, bi));
        StepUntil(sim, () => w.Counters.BuildingsBurned == 1, 20_000);
        Assert.Equal(0, c.Services.Fires.Used());
        Assert.Equal(0L, w.Counters.FiresOut);
        Assert.False(c.Buildings.IsAlive(bi) && Burning(c, bi));
        Assert.Equal(0, (int)c.Buildings.Cond[bi]);
    }

    [Fact]
    public void Fire_Extinguished_ByCrew_CeilDrops()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Pick(c);
        int ceil0 = c.Buildings.Ceil[bi];
        int cond0 = c.Buildings.Cond[bi];
        Assert.True(Fires.Start(w, c, bi));
        Assert.Equal(1, c.Services!.Urgent[Fire].Count);   // срочный вызов fire в момент начала
        Assert.Equal(1L, c.Services.RecCallsQueued[Fire]);
        StepUntil(sim, () => w.Counters.FiresOut == 1, 30_000);
        Assert.False(Burning(c, bi));
        Assert.Equal(0, c.Services!.Fires.Used());
        Assert.Equal(0L, w.Counters.BuildingsBurned);
        Assert.Equal(1L, c.Services.RecCallsDone[Fire]);
        int cond = c.Buildings.Cond[bi];
        int damage = cond0 - cond;
        Assert.True(damage > 0, "огонь не причинил ущерба");
        Assert.Equal(ceil0 - (damage / 2), (int)c.Buildings.Ceil[bi]);   // Ceil -= ущерб / 2
        Assert.True(cond <= c.Buildings.Ceil[bi]);
        CrewStore u = c.Services.Crews;
        StepUntil(sim, () => Array.TrueForAll(u.State, st => st is CrewState.Free or CrewState.Idle or CrewState.OffDuty), 20_000);   // бригады вернулись на станцию
    }

    [Fact]
    public void Fire_NoSuchBuilding_RecordFreed()
    {
        using Simulation sim = Make(station: false);
        World w = sim.World;
        CityWorld c = w.City!;
        int bi = Pick(c);
        Assert.True(Fires.Start(w, c, bi));
        Assert.False(Fires.Start(w, c, bi));   // уже горит
        Assert.Equal(1, c.Services!.Fires.Used());
    }

    [Fact]
    public void Fire_WorkTime_GrowsWithBurnTime()
    {
        using Simulation sim = Make(station: false);
        World w = sim.World;
        CityServices sv = w.City!.Services!;
        int bi = Pick(w.City!);
        Assert.True(Fires.Start(w, w.City!, bi));
        int gen = w.City!.Buildings.Pool.GenerationOf(bi);
        Assert.Equal(60, Fires.OnsiteS(w, sv, bi, gen, 60));
        sim.Step(20 * 100);   // 100 с горения (без пожарной)
        int s100 = Fires.OnsiteS(w, sv, bi, gen, 60);
        Assert.Equal(60 + (100 / 4), s100);
        sim.Step(20 * 400);
        Assert.True(Fires.OnsiteS(w, sv, bi, gen, 60) <= Fires.MaxOnsiteS);
        Assert.True(Fires.OnsiteS(w, sv, bi, gen, 60) > s100);
    }

    [Fact]
    public void Roll_Frequency_MatchesPpm_AndCapBounds()
    {
        using Simulation sim = Make(ppm: 100_000, cap: 4_096, maxFires: 4_096, station: false);
        World w = sim.World;
        CityWorld c = w.City!;
        int n = 0;
        for (int bi = 0; bi < c.Buildings.HighWater; bi++)
        {
            if (Flammable(c, bi))
            {
                n++;
                Fires.Roll(w, c, bi, in c.Types.Rows[c.Buildings.Type[bi]]);
            }
        }

        int used = c.Services!.Fires.Used();
        Assert.True(n >= 100, $"зданий {n}");
        double rate = used / (double)n;
        Assert.InRange(rate, 0.04, 0.17);   // 10 % за сутки (множители риска квартала на старте ≈ 1)
        Assert.Equal(0L, w.Counters.FiresCapped);
    }

    [Fact]
    public void Roll_Cap_PoolAndQueue_StayBounded()
    {
        // бросок выпадает у каждого здания (100 % в сутки), потолок 4: очагов не больше 4, остальные отброшены счётчиком
        using Simulation sim = Make(ppm: 1_000_000, cap: 4);
        World w = sim.World;
        CityWorld c = w.City!;
        for (int bi = 0; bi < c.Buildings.HighWater; bi++)
        {
            if (Flammable(c, bi))
            {
                Fires.Roll(w, c, bi, in c.Types.Rows[c.Buildings.Type[bi]]);
            }
        }

        Assert.Equal(4, c.Services!.Fires.Used());
        Assert.True(w.Counters.FiresCapped > 10, $"отброшено {w.Counters.FiresCapped}");
        sim.Step(c.DayTicks / 3);
        Assert.True(c.Services.Fires.Used() <= 4);
    }

    [Fact]
    public void Spread_SameCell_Bounded_NoAvalanche()
    {
        // огонь перекидывается на 100 % в минуту, пожарных нет, потолок 3: очагов и вызовов никогда больше 3, перекидывания были и упёрлись в потолок
        using Simulation sim = Make(ppm: 1, cap: 3, spreadPpm: 1_000_000, smolderS: 10, station: false);
        World w = sim.World;
        CityWorld c = w.City!;
        CityServices sv = c.Services!;
        int best = Pick(c);
        int bestN = 0;
        for (int k = 0; k < 400; k++)
        {
            int bi;
            try
            {
                bi = Pick(c, k);
            }
            catch
            {
                break;
            }

            NeighborhoodFields f = c.Fields!;
            int cell = f.CellOf(c.Lots.X[c.Buildings.Lot[bi]], c.Lots.Z[c.Buildings.Lot[bi]]);
            int m = 0;
            for (int j = 0; j < c.Buildings.HighWater; j++)
            {
                m += Flammable(c, j) && f.CellOf(c.Lots.X[c.Buildings.Lot[j]], c.Lots.Z[c.Buildings.Lot[j]]) == cell ? 1 : 0;
            }

            if (m > bestN)
            {
                bestN = m;
                best = bi;
            }
        }

        Assert.True(bestN >= 4, $"клетка с {bestN} домами");
        Assert.True(Fires.Start(w, c, best));
        int maxUsed = 0;
        int maxQueue = 0;
        for (int t = 0; t < 16_000; t += 20)
        {
            sim.Step(20);
            maxUsed = Math.Max(maxUsed, sv.Fires.Used());
            maxQueue = Math.Max(maxQueue, sv.Urgent[Fire].Count + sv.Normal[Fire].Count);
        }

        Assert.True(maxUsed <= 3, $"очагов {maxUsed}");
        Assert.True(maxQueue <= 3, $"вызовов в кольцах {maxQueue}");
        Assert.True(w.Counters.FiresSpread > 0, "перекидываний не было");
        Assert.True(w.Counters.FiresStarted >= 1 + w.Counters.FiresSpread);
        Assert.True(w.Counters.FiresCapped > 0 || w.Counters.FiresSpread >= 2);
    }

    [Fact]
    public void Spread_StaysInCell()
    {
        using Simulation sim = Make(ppm: 1, cap: 8, spreadPpm: 1_000_000, smolderS: 0, station: false);
        World w = sim.World;
        CityWorld c = w.City!;
        NeighborhoodFields f = c.Fields!;
        int src = Pick(c);
        int cell = f.CellOf(c.Lots.X[c.Buildings.Lot[src]], c.Lots.Z[c.Buildings.Lot[src]]);
        Assert.True(Fires.Start(w, c, src));
        sim.Step(20 * 600);
        for (int bi = 0; bi < c.Buildings.HighWater; bi++)
        {
            if (c.Buildings.IsAlive(bi) && Burning(c, bi))
            {
                Assert.Equal(cell, f.CellOf(c.Lots.X[c.Buildings.Lot[bi]], c.Lots.Z[c.Buildings.Lot[bi]]));
            }
        }
    }

    [Fact]
    public void Fire_RecallsBrigade_WhenCallLost()
    {
        // вызов без станции: пожарных нет, затем станция появилась - очаг сам вызывает повторно
        using Simulation sim = Make(station: false);
        World w = sim.World;
        CityWorld c = w.City!;
        int lot = FirstFreeLot(c);
        Assert.True(Fires.Start(w, c, Pick(c)));
        sim.Step(100);
        Assert.Equal(0L, w.Counters.CallsDone);
        Assert.True(Utilities.BuildDepot(w, c, lot, c.Types.Count - 1));
        StepUntil(sim, () => w.Counters.FiresOut == 1 || w.Counters.BuildingsBurned == 1, 30_000);
        Assert.Equal(1L, w.Counters.FiresOut);
    }

    private static Simulation Busy(int threads)
    {
        Simulation sim = Make(ppm: 0, cap: 6, spreadPpm: 400_000, smolderS: 20, threads: threads);
        CityWorld c = sim.World.City!;
        Assert.True(Fires.Start(sim.World, c, Pick(c, 0)));
        Assert.True(Fires.Start(sim.World, c, Pick(c, 5)));
        Assert.True(Fires.Start(sim.World, c, Pick(c, 11)));
        return sim;
    }

    [Fact]
    public void Fire_Hash_SameAt_1_2_3_Threads()
    {
        ulong? first = null;
        for (int t = 1; t <= 3; t++)
        {
            using Simulation sim = Busy(t);
            sim.Step(6_000);
            Assert.True(sim.World.Counters.FiresStarted >= 3);
            ulong h = sim.ComputeStateHash();
            first ??= h;
            Assert.Equal(first, h);
        }
    }

    [Fact]
    public void Fire_Save_EveryTick_AroundIgnitionAndExtinguish()
    {
        using Simulation sim = Busy(1);
        World w = sim.World;
        int saves = 0;
        long sig = -1;
        int after = 0;
        for (int t = 0; t < 8_000 && (w.Counters.FiresOut < 2 || t < 600); t++)
        {
            sim.Step(1);
            long now = (w.Counters.FiresStarted * 1_000_000L) + (w.Counters.FiresOut * 1_000L) + w.Counters.BuildingsBurned;
            if (now != sig)
            {
                sig = now;
                after = 3;
            }
            else if (after > 0)
            {
                after--;
            }
            else if (t >= 20 && t % 200 != 0)
            {
                continue;
            }

            byte[] raw = WorldSaveFixtures.SaveBytes(sim);
            using Simulation loaded = WorldSaveFixtures.Load(raw);
            Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
            saves++;
            if (saves % 12 == 0)
            {
                using Simulation probe = WorldSaveFixtures.Load(raw);
                using Simulation twin = WorldSaveFixtures.Load(raw);
                probe.Step(60);
                twin.Step(60);
                Assert.Equal(probe.ComputeStateHash(), twin.ComputeStateHash());
            }
        }

        Assert.True(saves > 20, $"сейвов {saves}");
        Assert.True(w.Counters.FiresOut >= 1, "ни одного тушения");
    }

    [Fact]
    public void Fire_GetFires_ReportsBurning()
    {
        using Simulation sim = Make(station: false);
        World w = sim.World;
        int bi = Pick(w.City!);
        Assert.True(Fires.Start(w, w.City!, bi));
        sim.Step(200);
        var buf = new FireInfo[8];
        Assert.Equal(1, sim.GetFires(buf));
        Assert.Equal(bi, buf[0].Building);
        Assert.Equal(1, buf[0].Smoldering);
        Assert.True(buf[0].Cond < buf[0].CondStart);
        Assert.Equal(1, sim.ActiveFireCount);
        Assert.Equal((ushort)16, (ushort)(CityLife.RenderFlags(w.City!, bi) & 16));
    }

    [Fact]
    public void Fire_ZeroAllocations_PerTick()
    {
        using Simulation sim = Busy(1);
        sim.Step(100);
        long bytes = AllocationProbe.Measure(() => sim.Step(400));
        Assert.Equal(0L, bytes);
    }
}
