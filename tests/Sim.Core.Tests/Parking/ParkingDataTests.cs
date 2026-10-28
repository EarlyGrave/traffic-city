using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Parking;
using Sim.Core.Save;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Core.Util;
using Sim.Scenarios;

namespace Sim.Core.Tests.Parking;

/// <summary>
/// Поверхность и данные парковки (K1, parking.md §17 «Данные»): создание объектов детерминировано и равно пересборке после <c>Load</c>, ёмкость обочины по формуле, окрестности
/// (локальная пересборка = полной), закрепление дома в начальном населении, снос объекта без нарушения PK2/PK4/PK6, <c>Save -&gt; Load -&gt; Save</c> побайтно, хэш не зависит от потоков,
/// 0 байт на тик, землепользование (<c>city.landuse</c>) в сейве (долг VS8). PK1 (прежние golden и корпус w1-w9) проверяют golden-тесты и <c>SaveCorpusTests</c>.
/// </summary>
public sealed class ParkingDataTests
{
    private static ScenarioDesc Load(string name = "s1k-res-park") =>
        ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json")).Desc;

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static string Reader(string file) => File.ReadAllText(Path.Combine(GenFixture.RepoRoot(), "scenarios", file));

    private static CityWorld City(Simulation sim) => sim.World.City!;

    private static ParkingStore Park(Simulation sim) => City(sim).Parking!;

    private static void AssertValid(Simulation sim)
    {
        Assert.Equal(0, ParkingCheck.Validate(sim.World, out long row));
        Assert.Equal(0, row);
    }

    [Fact]
    public void NoParkingSection_LeavesWorldWithoutParking()
    {
        ScenarioDesc d = Load("s1k-res");
        Assert.False(d.Parking.Enabled);
        using Simulation sim = Make(d);
        Assert.Null(City(sim).Parking);
        Assert.False(sim.World.ParkCfg.Enabled);
        Assert.Equal(0, ParkingCheck.Validate(sim.World, out _));
    }

    [Fact]
    public void Create_BuildsOverflowCurbsAndBuildingLots()
    {
        using Simulation sim = Make(Load());
        CityWorld c = City(sim);
        ParkingStore p = Park(sim);
        Assert.Equal(ParkKind.Overflow, p.Kind[0]);
        Assert.Equal(int.MaxValue, p.Capacity[0]);
        int curbs = 0;
        int buildings = 0;
        for (int f = 1; f < p.HighWater; f++)
        {
            Assert.True(p.IsAlive(f));
            if (p.Kind[f] == ParkKind.Curb)
            {
                curbs++;
                Assert.Equal(ParkFlags.Public | ParkFlags.Permit, p.Flags[f]);
                Assert.Equal(f, p.CurbOfLot[p.Lot[f]]);
            }
            else
            {
                buildings++;
                int bi = p.Building[f];
                Assert.Equal(f, p.FacOfBuilding[bi]);
                Assert.Equal(c.Buildings.Lot[bi], p.Lot[f]);
                Assert.Equal(c.Types.Rows[c.Buildings.Type[bi]].ParkingCapacity, p.Capacity[f]);
            }
        }

        // Улицы - обочина есть, артерии - нет (curb.arterial = off): по числу участков со стороной-улицей.
        int expectedCurbs = 0;
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            if (c.Lots.CarLane[lot] >= 0 && sim.World.Net.Segments[c.Lots.Seg[lot]].Kind == RoadKind.Street)
            {
                expectedCurbs++;
            }
        }

        Assert.Equal(expectedCurbs, curbs);
        Assert.True(curbs > 100 && buildings > 100, $"curbs={curbs} buildings={buildings}");
        AssertValid(sim);
    }

    [Fact]
    public void CurbCapacity_FollowsFormula()
    {
        using Simulation sim = Make(Load());
        World w = sim.World;
        CityWorld c = City(sim);
        ParkingStore p = Park(sim);
        int checkedCurbs = 0;
        int withEntrance = 0;
        for (int f = 1; f < p.HighWater; f++)
        {
            if (p.Kind[f] != ParkKind.Curb)
            {
                continue;
            }

            int lot = p.Lot[f];
            long slots = (c.Lots.S1Mm[lot] - (long)c.Lots.S0Mm[lot]) / p.Cfg.CurbSlotMm;
            int bi = c.Lots.Building[lot];
            bool entrance = bi >= 0 && p.FacOfBuilding[bi] >= 0;
            withEntrance += entrance ? 1 : 0;
            Assert.True(p.Capacity[f] <= slots - (entrance ? 1 : 0), $"lot {lot}: capacity {p.Capacity[f]} > {slots} - entrance");
            Assert.Equal(ParkingBuild.CurbCapacity(w, c, p, lot), p.Capacity[f]);
            checkedCurbs++;
        }

        Assert.True(checkedCurbs > 100 && withEntrance > 10);
    }

    [Fact]
    public void Create_IsDeterministic_AndEqualsReloadedDerived()
    {
        ScenarioDesc d = Load();
        using Simulation a = Make(d);
        using Simulation b = Make(d);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());

        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(a));
        ParkingStore pa = Park(a);
        ParkingStore pl = Park(loaded);
        Assert.Equal(pa.HighWater, pl.HighWater);
        for (int f = 0; f < pa.HighWater; f++)
        {
            Assert.Equal((pa.X[f], pa.Z[f], pa.AccessLane[f], pa.AccessS[f], pa.PedEdge[f], pa.PedSMm[f], pa.RoadComp[f], pa.PedComp[f], pa.WalkMm[f], pa.HomeAway[f]),
                (pl.X[f], pl.Z[f], pl.AccessLane[f], pl.AccessS[f], pl.PedEdge[f], pl.PedSMm[f], pl.RoadComp[f], pl.PedComp[f], pl.WalkMm[f], pl.HomeAway[f]));
        }

        Assert.True(pa.Catch.SameAs(pl.Catch), "окрестности после Load отличаются от Create");
        Assert.Equal(pa.CurbOfLot, pl.CurbOfLot);
        Assert.Equal(pa.FacOfBuilding, pl.FacOfBuilding);
        AssertValid(loaded);
    }

    [Fact]
    public void Catchment_IsSortedBoundedAndWithinWalkMax()
    {
        using Simulation sim = Make(Load());
        CityWorld c = City(sim);
        ParkingStore p = Park(sim);
        Catchment cat = p.Catch;
        int nonEmpty = 0;
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            int n = cat.Count[lot];
            Assert.InRange(n, 0, cat.Stride);
            nonEmpty += n > 0 ? 1 : 0;
            for (int k = 0; k < n; k++)
            {
                int i = (lot * cat.Stride) + k;
                int f = cat.Fac[i];
                Assert.True(p.IsAlive(f) && f > 0);
                Assert.InRange(cat.WalkMm[i], 0, p.Cfg.WalkMaxMm);
                Assert.Equal(c.Lots.RoadComp[lot], p.RoadComp[f]);
                if (k > 0)
                {
                    Assert.True(cat.WalkMm[i - 1] < cat.WalkMm[i] || (cat.WalkMm[i - 1] == cat.WalkMm[i] && cat.Fac[i - 1] < f));
                }
            }

            int bi = c.Lots.Building[lot];
            if (bi >= 0 && p.FacOfBuilding[bi] >= 0)
            {
                Assert.Equal(p.FacOfBuilding[bi], cat.Fac[lot * cat.Stride]);   // стоянка своего здания (WalkMm = 0) идёт первой
                Assert.Equal(0, cat.WalkMm[lot * cat.Stride]);
            }
        }

        Assert.True(nonEmpty > c.Lots.Count / 2);
    }

    [Fact]
    public void Catchment_LocalRebuild_EqualsFull()
    {
        using Simulation sim = Make(Load());
        World w = sim.World;
        CityWorld c = City(sim);
        ParkingStore p = Park(sim);
        // Снос нескольких объектов (локальная пересборка внутри Remove) и создание (стоянка здания) - затем полная пересборка даёт те же окрестности.
        int removed = 0;
        for (int f = 1; f < p.HighWater && removed < 25; f += 7)
        {
            if (p.IsAlive(f))
            {
                ParkingOps.Remove(w, f);
                removed++;
            }
        }

        Assert.True(removed > 10);
        int[] count = (int[])p.Catch.Count.Clone();
        int[] fac = (int[])p.Catch.Fac.Clone();
        int[] walk = (int[])p.Catch.WalkMm.Clone();
        p.Catch.RebuildAll(w, c);
        Assert.Equal(count, p.Catch.Count);
        Assert.Equal(fac, p.Catch.Fac);
        Assert.Equal(walk, p.Catch.WalkMm);
        AssertValid(sim);
    }

    [Fact]
    public void InitialHomes_CarShedEqualsLostCars_AndEveryCarHasAHome()
    {
        ScenarioDesc withPark = Load();
        ScenarioDesc without = Load("s1k-res");
        using Simulation a = Make(withPark);
        using Simulation b = Make(without);
        CityResidents ra = City(a).Residents;
        CityResidents rb = City(b).Residents;
        Assert.Equal(rb.HighWater, ra.HighWater);
        int carsWithout = 0;
        int carsWith = 0;
        for (int i = 0; i < ra.HighWater; i++)
        {
            bool carB = (rb.Flags[i] & ResidentFlags.HasCar) != 0;
            bool carA = (ra.Flags[i] & ResidentFlags.HasCar) != 0;
            carsWithout += carB ? 1 : 0;
            carsWith += carA ? 1 : 0;
            Assert.True(carB || !carA, "парковка не может добавить машину");
            if (carA)
            {
                Assert.True(ra.HomeFac[i] > 0);
                Assert.Equal(ra.HomeFac[i], ra.CarFac[i]);
            }
            else
            {
                Assert.Equal((-1, -1), (ra.HomeFac[i], ra.CarFac[i]));
            }
        }

        Assert.True(carsWithout > 0);
        Assert.Equal(carsWithout - carsWith, a.Counters.ParkCarShed);
        Assert.Equal(0, b.Counters.ParkCarShed);
        AssertValid(a);
    }

    [Fact]
    public void ScarceParking_ShedsCars_AndStillValid()
    {
        ScenarioDesc d = Load();
        d.Parking.CurbStreet = CurbMode.Off;        // только стоянки зданий
        d.Parking.CurbArterial = CurbMode.Off;
        d.Capacities.MaxParking = 0;                // ёмкость по умолчанию (лоты + здания + 64)
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        AssertValid(sim);
        ParkingStore p = Park(sim);
        for (int f = 1; f < p.HighWater; f++)
        {
            Assert.NotEqual(ParkKind.Curb, p.Kind[f]);
            Assert.True(p.Assigned[f] <= p.Capacity[f]);
        }

        Assert.True(sim.Counters.ParkCarShed >= 0);
        Assert.True(c.Residents.AliveCount > 0);
    }

    [Fact]
    public void Remove_ReassignsHomes_AndKeepsInvariants()
    {
        using Simulation sim = Make(Load());
        World w = sim.World;
        ParkingStore p = Park(sim);
        // Берём объект с закрепившими жителями.
        int target = -1;
        for (int f = 1; f < p.HighWater; f++)
        {
            if (p.IsAlive(f) && p.Assigned[f] >= 2)
            {
                target = f;
                break;
            }
        }

        Assert.True(target > 0);
        int assigned = p.Assigned[target];
        int overflowBefore = p.Occupied[0];
        ParkingOps.Remove(w, target);
        Assert.False(p.IsAlive(target));
        AssertValid(sim);
        CityResidents r = City(sim).Residents;
        int reassigned = 0;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (r.Pool.IsAlive(i) && (r.Flags[i] & ResidentFlags.HasCar) != 0)
            {
                Assert.True(r.HomeFac[i] > 0 || r.CarFac[i] == 0);
                reassigned += r.HomeFac[i] > 0 ? 0 : 1;
            }
        }

        Assert.True(reassigned == p.Occupied[0] - overflowBefore);
        Assert.True(assigned >= 2);
    }

    [Fact]
    public void Step_KeepsInvariants_WhileCityGrowsAndPeopleMove()
    {
        ScenarioDesc d = Load();
        using Simulation sim = Make(d);
        for (int k = 0; k < 12; k++)
        {
            sim.Step(2000);
            AssertValid(sim);
        }

        Assert.True(sim.Counters.ResLegsDone > 0);
    }

    [Fact]
    public void Step_OverSeveralDays_KeepsInvariants_ThroughGrowthMovesAndImmigration()
    {
        ScenarioDesc d = Load();
        d.Town.Calendar.DayTicks = 14_400;   // короткие сутки: рост, въезд, переезд и снос за секунды
        using Simulation sim = Make(d);
        for (int k = 0; k < 20; k++)
        {
            sim.Step(7200);
            AssertValid(sim);
        }

        Assert.True(sim.Counters.BuildingsBuilt + sim.Counters.Immigrants + sim.Counters.ResRelocations > 0);
    }

    [Fact]
    public void BuildRoad_AddsCurbsForNewLots_AndCatchmentsStayEqualToFull()
    {
        using Simulation sim = Make(Load());
        sim.Step(200);
        ParkingStore p = Park(sim);
        CityWorld c = City(sim);
        int lots = c.Lots.Count;
        int objects = p.Pool.AliveCount;
        // Отдельная улица к югу от города: участки с обеих сторон, обочины создаются хуком BuildRoad.
        var cmd = Command.MakeBuildRoad(new BuildRoadCmd { Ax = -600, Az = 1700, Bx = 600, Bz = 1700, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street }, 3);
        Assert.Equal(RejectReason.None, Sim.Core.Tests.Tools.ToolsFixture.Run(sim, in cmd));
        Assert.True(c.Lots.Count > lots, "BuildRoad не нарезал участки");
        int newCurbs = 0;
        for (int lot = lots; lot < c.Lots.Count; lot++)
        {
            newCurbs += p.CurbOfLot[lot] >= 0 ? 1 : 0;
        }

        Assert.True(newCurbs > 0 && p.Pool.AliveCount == objects + newCurbs, $"newCurbs={newCurbs} alive {objects} -> {p.Pool.AliveCount}");
        AssertValid(sim);
        int[] count = (int[])p.Catch.Count.Clone();
        int[] fac = (int[])p.Catch.Fac.Clone();
        p.Catch.RebuildAll(sim.World, c);
        Assert.Equal(count, p.Catch.Count);
        Assert.Equal(fac, p.Catch.Fac);
    }

    [Fact]
    public void RemoveRoad_DropsCurbsOfLostLots_AndKeepsInvariants()
    {
        using Simulation sim = Make(Load());
        sim.Step(100);
        World w = sim.World;
        CityWorld c = City(sim);
        ParkingStore p = Park(sim);
        LotStore l = c.Lots;
        int seg = -1;
        int lotWithBuilding = -1;
        RejectReason r = RejectReason.Busy;
        for (int skip = 0; skip < 60 && r != RejectReason.None; skip++)
        {
            lotWithBuilding = -1;
            for (int i = 0, seen = 0; i < l.Count; i++)
            {
                if (l.Building[i] >= 0 && p.CurbOfLot[i] >= 0 && seen++ == skip)
                {
                    lotWithBuilding = i;
                    break;
                }
            }

            Assert.True(lotWithBuilding >= 0);
            seg = l.Seg[lotWithBuilding];
            r = Sim.Core.Tests.Tools.ToolsFixture.Run(sim, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = Sim.Core.Tests.Tools.ToolsFixture.Segment(sim, seg) }, 1));
        }

        Assert.Equal(RejectReason.None, r);
        int gen = w.Net.Segments[seg].Generation;
        var mine = new System.Collections.Generic.List<int>();
        for (int i = 0; i < l.Count; i++)
        {
            if (l.Seg[i] == seg && l.SegGen[i] == gen)
            {
                mine.Add(i);
            }
        }

        bool removed = false;
        for (int t = 0; t < 6000 && !removed; t++)
        {
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                removed |= e.Kind == SimEventKind.SegmentRemoved && e.A == seg;
            }
        }

        Assert.True(removed);
        foreach (int i in mine)
        {
            Assert.NotEqual(0, l.Flags[i] & LotFlags.Lost);
            Assert.Equal(-1, p.CurbOfLot[i]);
        }

        AssertValid(sim);
        for (int k = 0; k < 6; k++)
        {
            sim.Step(2500);
            AssertValid(sim);
        }
    }

    [Fact]
    public void SaveLoadSave_IsByteEqual_AndContinuesWithSameHash()
    {
        using Simulation sim = Make(Load());
        sim.Step(3000);
        byte[] first = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(first);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.True(first.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(loaded)));
        sim.Step(1500);
        loaded.Step(1500);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        AssertValid(loaded);
    }

    [Fact]
    public void StateHash_IsIndependentOfWorkerThreads()
    {
        ScenarioDesc d = Load();
        ulong reference = 0;
        foreach (int threads in new[] { 1, 2, 3 })
        {
            using Simulation sim = Make(d, threads);
            sim.Step(2500);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                reference = h;
            }

            Assert.Equal(reference, h);
        }
    }

    [Fact]
    public void ParkingSection_ChangesHash_ButOnlyInMode()
    {
        using Simulation with = Make(Load());
        using Simulation without = Make(Load("s1k-res"));
        Assert.NotEqual(with.ComputeStateHash(), without.ComputeStateHash());
    }

    [Fact]
    public void Step_AllocatesZeroBytes_WithParking()
    {
        using Simulation sim = Make(Load());
        sim.Step(3000);
        long bytes = AllocationProbe.Measure(() => sim.Step(1200), out _);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Loader_RejectsParkingWithoutResidents_AndUnknownKeys()
    {
        string root = GenFixture.RepoRoot();
        string json = File.ReadAllText(Path.Combine(root, "scenarios", "s1k-res-park.json"));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json.Replace("\"model\": \"resource\"", "\"model\": \"resource\", \"bogus\": 1"), Reader, "t"));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json.Replace("\"model\": \"resource\"", "\"model\": \"street\""), Reader, "t"));
        string noRes = File.ReadAllText(Path.Combine(root, "scenarios", "s1k.json")).Replace("\"rules\": {", "\"parking\": { \"model\": \"resource\" }, \"rules\": {");
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(noRes, Reader, "t"));
    }

    // ---- землепользование в сейве (VS8, ADR-0027; долг K1) ----

    private static ulong Fnv(ReadOnlySpan<LandUsePoly> polys)
    {
        var h = Fnv1a64.Create();
        h.Add(polys.Length);
        foreach (LandUsePoly p in polys)
        {
            h.Add(p.P0.X);
            h.Add(p.P0.Z);
            h.Add(p.P1.X);
            h.Add(p.P1.Z);
            h.Add(p.P2.X);
            h.Add(p.P2.Z);
            h.Add(p.P3.X);
            h.Add(p.P3.Z);
            h.Add(p.Kind);
            h.Add(p.Flags);
        }

        return h.Value;
    }

    private static ScenarioDesc WithLandUse()
    {
        ScenarioDesc d = Load();
        d.City.LandUse = new LandUseDesc { Enabled = true, ParkShare = 0.1f, PondShare = 0.25f, Coast = LandUseCoast.South, CoastWidthM = 300f };
        return d;
    }

    [Fact]
    public void LandUse_RoundTrip_RestoresPolygonsHashAndVersion()
    {
        using Simulation sim = Make(WithLandUse());
        Assert.True(sim.LandUse.Length > 2);
        Assert.Equal(1, sim.LandUseVersion);
        sim.Step(1500);
        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim));
        Assert.Equal(sim.LandUse.Length, loaded.LandUse.Length);
        Assert.Equal(Fnv(sim.LandUse), Fnv(loaded.LandUse));
        Assert.Equal(1, loaded.LandUseVersion);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void LandUse_AfterLoad_LotBuilderRespectsParksAndWater()
    {
        using Simulation sim = Make(WithLandUse());
        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim));
        int polys = 0;
        foreach (LandUsePoly q in loaded.LandUse)
        {
            float cx = (q.P0.X + q.P1.X + q.P2.X + q.P3.X) * 0.25f;
            float cz = (q.P0.Z + q.P1.Z + q.P2.Z + q.P3.Z) * 0.25f;
            Assert.True(LotBuilder.InLandUse(loaded.World, cx, cz));
            Assert.True(LotBuilder.InLandUse(sim.World, cx, cz));
            polys++;
        }

        Assert.True(polys > 2);
        Assert.False(LotBuilder.InLandUse(loaded.World, 1e7f, 1e7f));

        // Живые участки ни одним центром не лежат в парке или воде (нарезка Create их отбросила) - и в загруженном мире тоже.
        CityWorld c = City(loaded);
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            if ((c.Lots.Flags[lot] & LotFlags.Lost) == 0)
            {
                Assert.False(LotBuilder.InLandUse(loaded.World, c.Lots.X[lot], c.Lots.Z[lot]), $"lot {lot} в парке");
            }
        }
    }

    [Fact]
    public void LandUse_WithoutLandUse_SavesNoBlockAndStaysEmpty()
    {
        using Simulation sim = Make(Load());
        Assert.Equal(0, sim.LandUse.Length);
        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim));
        Assert.Equal(0, loaded.LandUse.Length);
        Assert.Equal(0, loaded.LandUseVersion);
    }
}
