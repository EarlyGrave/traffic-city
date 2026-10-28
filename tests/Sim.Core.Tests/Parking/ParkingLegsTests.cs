using System;
using System.IO;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Parking;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Scenarios;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Parking;

/// <summary>
/// Резерв и ноги micro (K2, parking.md §5-§6, §17 «Резерв и ноги»): выбор места по стоимости, освобождение резерва, PK2-PK5 каждые 100 тиков на <c>s1k-res-park</c>, «мест нет вообще» (жители ждут дома,
/// меняют способ по дедлайну, <c>res_stuck = 0</c>), резерв до выезда (на дороге нет машины гостя без резерва), meso-прибытие, хэш при 1/2/3 потоках, 0 Б/тик, <c>Save -&gt; Load</c> посреди ноги.
/// </summary>
public sealed class ParkingLegsTests
{
    private readonly ITestOutputHelper _out;

    public ParkingLegsTests(ITestOutputHelper output) => _out = output;

    private static ScenarioDesc Load(string name = "s1k-res-park") =>
        ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json")).Desc;

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    private static ParkingStore Park(Simulation sim) => City(sim).Parking!;

    private static void AssertValid(Simulation sim)
    {
        Assert.Equal(0, ParkingCheck.Validate(sim.World, out long row));
        Assert.Equal(0, row);
    }

    /// <summary>Машины жителей на дороге: гость (нога не домой) обязан нести резерв места цели (нет кружения, PK5); едущий домой резерва не имеет.</summary>
    private static int AssertEveryGuestCarReserved(Simulation sim)
    {
        World w = sim.World;
        CityResidents res = City(sim).Residents;
        VehicleStore v = w.Vehicles;
        int guests = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            int r = v.Resident[i];
            if (!v.Pool.IsAlive(i) || r < 0)
            {
                continue;
            }

            bool home = res.LegDest[r] == res.Home[r];
            if (home)
            {
                Assert.Equal(-1, v.ParkFac[i]);
            }
            else
            {
                Assert.True(v.ParkFac[i] > 0 && City(sim).Parking!.Pool.IsAlive(v.ParkFac[i], v.ParkFacGen[i]), "машина гостя без резерва");
                guests++;
            }
        }

        return guests;
    }

    /// <summary>Стоянки зданий и гаражи заполнены: гостям остаются обочины (закрепления жителей и стоящие машины не трогаются).</summary>
    private static void CloseBuildingStands(Simulation sim)
    {
        ParkingStore p = Park(sim);
        for (int f = 1; f < p.HighWater; f++)
        {
            if (p.IsAlive(f) && p.Kind[f] != ParkKind.Curb)
            {
                p.Capacity[f] = p.Assigned[f] + p.Occupied[f] + p.Reserved[f];
            }
        }
    }

    // ------------------------------------------------------------------ выбор и резерв

    [Fact]
    public void TryReserve_PicksCheapestFreePublicObject_SkipsFullAndClosing_ReleaseRestores()
    {
        using Simulation sim = Make(Load());
        World w = sim.World;
        CityWorld c = City(sim);
        ParkingStore p = Park(sim);
        int r = -1;
        int dest = -1;
        int lot = -1;
        for (int i = 0; i < c.Residents.HighWater && r < 0; i++)
        {
            int wk = c.Residents.Work[i];
            if (c.Residents.Pool.IsAlive(i) && wk >= 0 && p.Catch.Count[c.Buildings.Lot[wk]] >= 2)
            {
                r = i;
                dest = wk;
                lot = c.Buildings.Lot[wk];
            }
        }

        Assert.True(r >= 0, "нет работника с двумя кандидатами у цели");
        int b0 = lot * p.Catch.Stride;
        int n = p.Catch.Count[lot];
        // Ожидание: первый допустимый кандидат окрестности (список отсортирован по WalkMm, плата 0 -> стоимость монотонна по ходьбе).
        int expect = -1;
        for (int k = 0; k < n && expect < 0; k++)
        {
            int f = p.Catch.Fac[b0 + k];
            if (p.Free(f) > 0 && ((p.Flags[f] & ParkFlags.Public) != 0 || p.Building[f] == dest))
            {
                expect = f;
            }
        }

        Assert.True(expect > 0);
        int before = p.Reserved[expect];
        Assert.True(ParkingOps.TryReserve(w, r, dest, out int fac));
        Assert.Equal(expect, fac);
        Assert.Equal(before + 1, p.Reserved[fac]);
        ParkingOps.Release(w, fac);
        Assert.Equal(before, p.Reserved[fac]);

        // Заполнить выбранный: следующий кандидат.
        int saved = p.Capacity[fac];
        p.Capacity[fac] = p.Assigned[fac] + p.Occupied[fac] + p.Reserved[fac];
        Assert.True(ParkingOps.TryReserve(w, r, dest, out int second));
        Assert.NotEqual(fac, second);
        ParkingOps.Release(w, second);
        p.Capacity[fac] = saved;

        // Closing пропускается.
        p.Flags[fac] |= ParkFlags.Closing;
        if (ParkingOps.TryReserve(w, r, dest, out int third))
        {
            Assert.NotEqual(fac, third);
            ParkingOps.Release(w, third);
        }

        p.Flags[fac] &= unchecked((byte)~ParkFlags.Closing);
        AssertValid(sim);
    }

    [Fact]
    public void TryReserve_NoFreeObject_ReturnsFalse_AndHomeLegAlwaysSucceeds()
    {
        using Simulation sim = Make(Load());
        World w = sim.World;
        CityWorld c = City(sim);
        ParkingStore p = Park(sim);
        int r = -1;
        for (int i = 0; i < c.Residents.HighWater && r < 0; i++)
        {
            if (c.Residents.Pool.IsAlive(i) && c.Residents.Work[i] >= 0 && (c.Residents.Flags[i] & ResidentFlags.HasCar) != 0)
            {
                r = i;
            }
        }

        Assert.True(r >= 0);
        int dest = c.Residents.Work[r];
        for (int f = 1; f < p.HighWater; f++)
        {
            if (p.IsAlive(f))
            {
                p.Capacity[f] = p.Assigned[f] + p.Occupied[f] + p.Reserved[f];
            }
        }

        Assert.False(ParkingOps.TryReserve(w, r, dest, out int fac));
        Assert.Equal(-1, fac);
        Assert.True(ParkingOps.TryReserve(w, r, c.Residents.Home[r], out int home));
        Assert.Equal(c.Residents.HomeFac[r], home);
    }

    // ------------------------------------------------------------------ сутки на s1k-res-park

    [Fact]
    public void Day_KeepsPk2To5_EveryHundredTicks_AndLegsComplete()
    {
        using Simulation sim = Make(Load());
        int guests = 0;
        for (int t = 0; t < 400; t++)
        {
            sim.Step(100);
            AssertValid(sim);
            guests += AssertEveryGuestCarReserved(sim);
        }

        SimCounters k = sim.Counters;
        _out.WriteLine($"legs car/bus/walk {k.ResLegsCar}/{k.ResLegsBus}/{k.ResLegsWalk} done {k.ResLegsDone}; search n={k.ParkSearchTimeCount} sum={k.ParkSearchTimeSum} h={k.ParkSearchHist0},{k.ParkSearchHist1},{k.ParkSearchHist2},{k.ParkSearchHist3},{k.ParkSearchHist4},{k.ParkSearchHist5}; " +
                       $"nospot={k.ParkNoSpotWaits} switch={k.ParkModeSwitch} dropped={k.ParkToursDropped} overflow={k.ParkOverflow} walkTicks={k.ParkWalkTicks} stuck={k.ResStuck}");
        Assert.True(k.ResLegsCar > 20, "машинные ноги не начались");
        Assert.True(guests > 0, "ни разу не видели машину гостя на дороге");
        Assert.True(k.ParkSearchTimeCount > 0);
        Assert.Equal(0, k.ParkOverflow);
        Assert.Equal(0, k.ResStuck);
    }

    [Fact]
    [Trait("Category", "Long")]
    public void ThreeDays_PK2To5_NoStuck_NoOverflow_NoSearch()
    {
        // Три игровых суток: PK2-PK5 каждые 100 тиков (проверка раз в 1000 для скорости Debug), res_stuck = 0 (цель позади на том же сегменте - перестановка без выезда), поиска и Overflow нет.
        using Simulation sim = Make(Load());
        for (int t = 0; t < 2160; t++)
        {
            sim.Step(100);
            AssertEveryGuestCarReserved(sim);
            if (t % 10 == 9)
            {
                AssertValid(sim);
            }
        }

        SimCounters k = sim.Counters;
        _out.WriteLine($"legs car {k.ResLegsCar} done {k.ResLegsDone} search n={k.ParkSearchTimeCount} sum={k.ParkSearchTimeSum} stuck={k.ResStuck} overflow={k.ParkOverflow} searching={k.ParkSearching}");
        Assert.Equal(0, k.ResStuck);
        Assert.Equal(0, k.ParkOverflow);
        Assert.Equal(0, k.ParkSearching);
        Assert.Equal(0, k.ParkSearchTimeSum);
        Assert.True(k.ParkSearchTimeCount > 5000);
    }

    [Fact]
    public void T5_TargetStandFull_CarsParkOnNearbyCurb_NobodyCirclesAndNoWaiting()
    {
        // Фикстура T5 (cs2-lessons.md): стоянка цели полна, обочина рядом свободна -> все гости встают на обочину; на дороге у каждой машины гостя есть резерв (кружить нечем), ожидания мест нет.
        using Simulation sim = Make(Load());
        CloseBuildingStands(sim);
        ParkingStore p = Park(sim);
        int curbGuests = 0;
        for (int t = 0; t < 400; t++)
        {
            sim.Step(100);
            AssertValid(sim);
            AssertEveryGuestCarReserved(sim);
            for (int f = 1; f < p.HighWater; f++)
            {
                if (p.IsAlive(f))
                {
                    Assert.True(p.Kind[f] == ParkKind.Curb || p.Occupied[f] == 0, "гость встал на закрытую стоянку здания");
                    curbGuests += p.Kind[f] == ParkKind.Curb ? p.Occupied[f] : 0;
                }
            }
        }

        SimCounters k = sim.Counters;
        _out.WriteLine($"curb guest samples {curbGuests}; walkTicks {k.ParkWalkTicks}; search n={k.ParkSearchTimeCount} sum={k.ParkSearchTimeSum} h={k.ParkSearchHist0},{k.ParkSearchHist1},{k.ParkSearchHist2},{k.ParkSearchHist3},{k.ParkSearchHist4},{k.ParkSearchHist5}; nospot={k.ParkNoSpotWaits}");
        Assert.True(curbGuests > 0);
        Assert.True(k.ParkWalkTicks > 0, "пеший участок от обочины до двери не исполнялся");
        Assert.Equal(0, k.ParkOverflow);
        Assert.Equal(0, k.ResStuck);
        Assert.Equal(k.ParkSearchTimeCount, k.ParkSearchHist0 + k.ParkSearchHist1);   // ожидание мест не длиннее 20 с (поиска нет)
    }

    [Fact]
    public void NoSpotsAtAll_ResidentsWaitAtHome_SwitchModeOrDropTour_NoCarWithoutReservation()
    {
        using Simulation sim = Make(Load());
        ParkingStore p = Park(sim);
        for (int f = 1; f < p.HighWater; f++)
        {
            if (p.IsAlive(f))
            {
                p.Capacity[f] = p.Assigned[f];   // у гостей мест нет нигде; свои закрепления остаются
            }
        }

        for (int t = 0; t < 400; t++)
        {
            sim.Step(100);
            AssertValid(sim);
            Assert.Equal(0, AssertEveryGuestCarReserved(sim));   // машина гостя на дороге без резерва невозможна: «мест нет» - никто не выезжает
        }

        SimCounters k = sim.Counters;
        _out.WriteLine($"nospot={k.ParkNoSpotWaits} switch={k.ParkModeSwitch} dropped={k.ParkToursDropped} legs car/bus/walk {k.ResLegsCar}/{k.ResLegsBus}/{k.ResLegsWalk} stuck={k.ResStuck} cancelled={k.ResToursCancelled}");
        Assert.True(k.ParkNoSpotWaits > 0);
        Assert.True(k.ParkModeSwitch + k.ParkToursDropped > 0, "дедлайн ожидания не сработал");
        Assert.Equal(0, k.ResStuck);
        for (int f = 1; f < p.HighWater; f++)
        {
            if (p.IsAlive(f))
            {
                Assert.Equal(0, p.Reserved[f]);
                Assert.Equal(0, p.Occupied[f]);
            }
        }
    }

    [Fact]
    public void Meso_ArrivalGoesThroughParking_Pk2To5Hold()
    {
        ScenarioDesc d = Load();
        d.Lod = new LodDesc { Mode = LodMode.AllMeso, CellM = 1000f, MicroBudget = 15000, FollowSelected = true, FocusCommands = true };
        d.Capacities.MaxRoutes = Math.Max(d.Capacities.MaxRoutes, 8192);
        using Simulation sim = Make(d);
        for (int t = 0; t < 300; t++)
        {
            sim.Step(100);
            AssertValid(sim);
        }

        Assert.True(sim.Counters.ParkSearchTimeCount > 0, "ни одна машинная нога не закрылась в meso");
        Assert.Equal(0, sim.Counters.ParkOverflow);
    }

    // ------------------------------------------------------------------ потоки, аллокации, сейв

    [Fact]
    public void StateHash_IsIndependentOfWorkerThreads_InActiveWindow()
    {
        ScenarioDesc d = Load();
        ulong reference = 0;
        foreach (int threads in new[] { 1, 2, 3 })
        {
            using Simulation sim = Make(d, threads);
            sim.Step(30000);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                reference = h;
            }

            Assert.Equal(reference, h);
            Assert.True(sim.Counters.ParkSearchTimeCount > 0);
        }
    }

    [Fact]
    public void Step_AllocatesZeroBytes_InActiveWindow()
    {
        using Simulation sim = Make(Load());
        sim.Step(26000);
        long bytes = AllocationProbe.Measure(() => sim.Step(1200), out _);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void SaveLoad_InTheMiddleOfLegs_ContinuesWithSameHash()
    {
        using Simulation sim = Make(Load());
        CloseBuildingStands(sim);
        bool sawWalk = false;
        for (int t = 0; t < 600 && !sawWalk; t++)
        {
            sim.Step(100);
            CityResidents res = City(sim).Residents;
            for (int r = 0; r < res.HighWater && !sawWalk; r++)
            {
                sawWalk = res.Pool.IsAlive(r) && (res.LegStage[r] == LegStages.WalkFromCar || res.LegStage[r] == LegStages.WalkToCar) && sim.World.Tick > 24000;
            }
        }

        Assert.True(sawWalk, "не нашли жителя на пешем участке у машины");
        byte[] first = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(first);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        AssertValid(loaded);
        sim.Step(3000);
        loaded.Step(3000);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        AssertValid(loaded);
    }

    [Fact]
    public void NoParkingSection_NeverTouchesParkingState()
    {
        using Simulation sim = Make(Load("s1k-res"));
        sim.Step(30000);
        Assert.Null(City(sim).Parking);
        SimCounters k = sim.Counters;
        Assert.Equal(0, k.ParkNoSpotWaits + k.ParkModeSwitch + k.ParkSearchTimeCount + k.ParkWalkTicks + k.ParkOverflow);
    }
}
