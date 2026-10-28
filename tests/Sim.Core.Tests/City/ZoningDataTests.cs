using System;
using System.Runtime.CompilerServices;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Save;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.City;

/// <summary>Данные города Z1 (zoning-demand.md §19 «Данные»): нарезка, автозонирование, начальное население, деньги, API, команды-заглушки, 0 B/тик, детерминизм.</summary>
public sealed class ZoningDataTests
{
    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    // ------------------------------------------------------------ нарезка

    [Fact]
    public void Lots_AreDeterministic_AndStateHashIsStable()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation a = Make(d);
        using Simulation b = Make(d);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        LotStore la = City(a).Lots;
        LotStore lb = City(b).Lots;
        Assert.Equal(la.Count, lb.Count);
        Assert.True(la.Count > 500, $"на s1k участков мало: {la.Count}");
        Assert.True(la.Seg.AsSpan(0, la.Count).SequenceEqual(lb.Seg.AsSpan(0, lb.Count)));
        Assert.True(la.S0Mm.AsSpan(0, la.Count).SequenceEqual(lb.S0Mm.AsSpan(0, lb.Count)));
        Assert.True(la.X.AsSpan(0, la.Count).SequenceEqual(lb.X.AsSpan(0, lb.Count)));
        Assert.True(la.Zone.AsSpan(0, la.Count).SequenceEqual(lb.Zone.AsSpan(0, lb.Count)));
    }

    [Fact]
    public void Lots_FollowTheSliceRules_AndDoNotOverlap()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        World w = sim.World;
        CityWorld c = w.City!;
        LotStore l = c.Lots;
        long clear = c.Cfg.Zoning.NodeClearMm;
        for (int i = 0; i < l.Count; i++)
        {
            Assert.True(l.Side[i] <= 1);
            Assert.True(l.S0Mm[i] >= clear, $"участок {i}: отступ от узла меньше node_clear_m");
            long lengthMm = (long)MathF.Round(w.Net.Segments[l.Seg[i]].Length * 1000f);
            Assert.True(l.S1Mm[i] <= lengthMm - clear, $"участок {i}: выходит за отступ у конца сегмента");
            Assert.Equal(w.Net.Segments[l.Seg[i]].Generation, l.SegGen[i]);
            if (i > 0 && l.Seg[i] == l.Seg[i - 1] && l.Side[i] == l.Side[i - 1])
            {
                Assert.True(l.S0Mm[i] >= l.S1Mm[i - 1], "соседние участки стороны пересекаются по фасаду");
            }

            float frontage = (l.S1Mm[i] - l.S0Mm[i]) * 0.001f;
            Assert.InRange(frontage, c.Cfg.Zoning.LotFrontageMm * 0.001f, (c.Cfg.Zoning.LotFrontageMm * 0.001f * 2f) + 0.01f);
        }

        // Прямоугольники, сжатые на полклетки сетки занятости, не пересекаются (SAT по 4 осям).
        float shrink = LotBuilder.OccCellM * 0.5f + 0.1f;
        float halfD = (c.Cfg.Zoning.LotDepthMm * 0.0005f) - shrink;
        for (int i = 0; i < l.Count; i++)
        {
            for (int j = i + 1; j < l.Count; j++)
            {
                float dx = l.X[i] - l.X[j];
                float dz = l.Z[i] - l.Z[j];
                if ((dx * dx) + (dz * dz) > 160f * 160f)
                {
                    continue;
                }

                Assert.False(Overlap(l, i, j, shrink, halfD), $"участки {i} и {j} пересекаются");
            }
        }
    }

    private static bool Overlap(LotStore l, int i, int j, float shrink, float halfD)
    {
        // Курс участка направлен к дороге; нормаль фасада - тот же вектор, касательная - перпендикуляр.
        float nxi = MathF.Cos(l.Heading[i]), nzi = MathF.Sin(l.Heading[i]);
        float nxj = MathF.Cos(l.Heading[j]), nzj = MathF.Sin(l.Heading[j]);
        float hli = ((l.S1Mm[i] - l.S0Mm[i]) * 0.0005f) - shrink;
        float hlj = ((l.S1Mm[j] - l.S0Mm[j]) * 0.0005f) - shrink;
        float[] axes = { nxi, nzi, -nzi, nxi, nxj, nzj, -nzj, nxj };
        for (int a = 0; a < 4; a++)
        {
            float ax = axes[a * 2], az = axes[(a * 2) + 1];
            float ri = Radius(ax, az, nxi, nzi, halfD, hli);
            float rj = Radius(ax, az, nxj, nzj, halfD, hlj);
            float dist = MathF.Abs(((l.X[i] - l.X[j]) * ax) + ((l.Z[i] - l.Z[j]) * az));
            if (dist >= ri + rj)
            {
                return false;
            }
        }

        return true;
    }

    private static float Radius(float ax, float az, float nx, float nz, float halfD, float halfL) =>
        (MathF.Abs((ax * nx) + (az * nz)) * halfD) + (MathF.Abs((ax * -nz) + (az * nx)) * halfL);

    [Fact]
    public void LotAccessPoints_PointToTheLaneOfTheirSide()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        World w = sim.World;
        LotStore l = w.City!.Lots;
        int withLane = 0;
        for (int i = 0; i < l.Count; i++)
        {
            int lane = l.CarLane[i];
            if (lane < 0)
            {
                continue;
            }

            withLane++;
            ref readonly LaneData ld = ref w.Net.Lanes[lane];
            Assert.Equal(l.Seg[i], ld.Segment);
            Assert.Equal(l.Side[i] == 0 ? 0 : 1, ld.Direction);
            Assert.InRange(l.CarS[i], 0f, ld.Length);
            Assert.True(l.RoadComp[i] >= 0);
            Assert.InRange(l.Cell[i], 0, w.City.Grid.Cells - 1);
        }

        Assert.True(withLane > l.Count / 2);
    }

    // ------------------------------------------------------------ зоны и население

    [Fact]
    public void AutoZoning_RespectsShares_AndKeepsCentreCommercial()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        LotStore l = c.Lots;
        int none = 0, r = 0, com = 0, ind = 0;
        double dc = 0, dr = 0, di = 0;
        float midX = c.Grid.OriginX + (c.Grid.CellsX * c.Grid.CellM * 0.5f);
        float midZ = c.Grid.OriginZ + (c.Grid.CellsZ * c.Grid.CellM * 0.5f);
        for (int i = 0; i < l.Count; i++)
        {
            double dist = Math.Sqrt(Math.Pow(l.X[i] - midX, 2) + Math.Pow(l.Z[i] - midZ, 2));
            switch ((LotZone)l.Zone[i])
            {
                case LotZone.None: none++; break;
                case LotZone.Residential: r++; dr += dist; break;
                case LotZone.Commercial: com++; dc += dist; break;
                default: ind++; di += dist; break;
            }
        }

        Assert.InRange(none / (double)l.Count, 0.14, 0.26);   // unzoned_share 0.2
        Assert.True(com > 0 && ind > 0 && r > com && r > ind);
        Assert.True(dc / com < dr / r, "C должна быть ближе к центру, чем R");
        Assert.True(dr / r < di / ind, "I должна быть дальше от центра, чем R");
        for (int i = 0; i < l.Count; i++)
        {
            bool arterial = sim.World.Net.Segments[l.Seg[i]].Kind == Sim.Core.Network.RoadKind.Arterial;
            Assert.Equal(arterial ? 1 : 0, l.Density[i]);
        }
    }

    [Fact]
    public void InitialPopulation_EqualsFillTimesHousing_AndIsConsistent()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        long housing = 0;
        for (int b = 0; b < c.Buildings.HighWater; b++)
        {
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[c.Buildings.Type[b]];
            Assert.Equal((byte)BuildingState.Active, c.Buildings.State[b]);
            housing += t.Kind == LotZone.Residential ? t.Capacity : 0;
        }

        Assert.Equal((housing * c.Cfg.Residents.InitialFillQ16) >> 16, c.Residents.AliveCount);
        Assert.True(sim.GetCityStats(out CityStats s));
        Assert.Equal(c.Residents.AliveCount, s.Population);
        Assert.Equal(s.Labour, s.Employed + s.Seeking);
        Assert.InRange(s.Labour / (double)s.Population, 0.55, 0.65);   // labour_share 0.6
        int cars = 0;
        for (int i = 0; i < c.Residents.HighWater; i++)
        {
            cars += (c.Residents.Flags[i] & ResidentFlags.HasCar) != 0 ? 1 : 0;
            Assert.Equal((byte)ResidentState.AtHome, c.Residents.State[i]);
            Assert.Equal(ResidentEvent.Plan, c.Residents.EvKind[i]);
            long planned = c.Residents.EvTick[i];
            Assert.InRange(planned, c.TickOfSlot(0, c.Cfg.Residents.PlanSlot), c.TickOfSlot(0, c.Cfg.Residents.PlanSlot + 12));
        }

        Assert.InRange(cars / (double)s.Population, 0.65, 0.75);   // car_ownership 0.7
        Assert.Equal(0, s.Seeking);                                 // на s1k работы с запасом
    }

    [Fact]
    public void Money_SumsToZero_AndEndowmentsAreFlows()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        Assert.Equal(0L, c.Ledger.SumAll());
        Assert.True(sim.GetCityStats(out CityStats s));
        Assert.Equal(0L, s.BalanceResidents + s.BalanceFirms + s.BalanceExternal + s.BalanceCity);
        Assert.Equal(c.Ledger.FlowTotal[(int)FlowKind.Endowment], -c.Ledger.External);
        var total = new long[Simulation.FlowKindCount];
        Assert.True(sim.CopyFlowTotals(total, FlowPeriod.Total));
        Assert.Equal(-c.Ledger.External, total[(int)FlowKind.Endowment]);
    }

    [Fact]
    public void Lists_MatchResidentFields()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        int listed = 0;
        for (int b = 0; b < c.Buildings.HighWater; b++)
        {
            int len = 0;
            for (int m = c.Buildings.HeadRes[b]; m != -1; m = c.Residents.NextHome[m])
            {
                Assert.Equal(b, c.Residents.Home[m]);
                len++;
            }

            Assert.Equal(len, c.Types.Rows[c.Buildings.Type[b]].Kind == LotZone.Residential ? c.Buildings.Occupants[b] : 0);
            listed += len;
        }

        Assert.Equal(c.Residents.AliveCount, listed);
    }

    // ------------------------------------------------------------ побитовая прежняя работа без residents (CZ1)

    [Fact]
    public void WithoutResidents_NothingIsAllocated()
    {
        ScenarioDesc d = Worlds.Step2.S1k(buses: true);
        using Simulation sim = Make(d);
        Assert.Null(sim.World.City);
        Assert.False(sim.HasCity);
        Assert.False(sim.GetCityStats(out _));
        Assert.Equal(0, sim.GetBuildings(new BuildingRenderData[8]));
        Assert.Equal(0, sim.GetLots(new LotRenderData[8]));
        Assert.Equal(0, sim.BuildingsVersion);
        for (int i = 0; i < sim.World.Vehicles.HighWater; i++)
        {
            Assert.Equal(-1, sim.World.Vehicles.Resident[i]);
        }
    }

    // ------------------------------------------------------------ шаг, потоки, аллокации

    [Fact]
    public void Step_ChangesNothingInTheCity_AndKeepsOdGeneratorOff()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Transit.Routes = 0;
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        int pop = c.Residents.AliveCount;
        long sum = c.Residents.Account[0];
        sim.Step(300);
        Assert.Equal(pop, c.Residents.AliveCount);
        Assert.Equal(sum, c.Residents.Account[0]);
        Assert.Equal(0, sim.ActiveVehicleCount);   // OD-генератор в режиме жителей выключен, автобусов нет
        Assert.Equal(0L, c.Ledger.SumAll());
    }

    [Fact]
    public void StateHash_IsIndependentOfWorkerThreads()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        ulong h1, h2, h3;
        using (Simulation s = Make(d, 1))
        {
            s.Step(400);
            h1 = s.ComputeStateHash();
        }

        using (Simulation s = Make(d, 2))
        {
            s.Step(400);
            h2 = s.ComputeStateHash();
        }

        using (Simulation s = Make(d, 3))
        {
            s.Step(400);
            h3 = s.ComputeStateHash();
        }

        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3);
    }

    [Fact]
    public void Step_AllocatesZeroBytes_InSteadyState()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        sim.Step(400);
        long bytes = Fixtures.AllocationProbe.Measure(() => sim.Step(100));
        Assert.Equal(0L, bytes);
    }

    // ------------------------------------------------------------ API и размеры POD

    [Fact]
    public void RenderStructs_HaveFrozenSizes()
    {
        Assert.Equal(24, Unsafe.SizeOf<BuildingRenderData>());
        Assert.Equal(28, Unsafe.SizeOf<LotRenderData>());
        Assert.Equal(64, Unsafe.SizeOf<Command>());
    }

    [Fact]
    public void ApiReads_ReturnBuildingsLotsAndInfo()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        var buildings = new BuildingRenderData[sim.BuildingSlotCount];
        Assert.Equal(buildings.Length, sim.GetBuildings(buildings));
        var lots = new LotRenderData[sim.LotCount];
        Assert.Equal(lots.Length, sim.GetLots(lots));
        Assert.True(sim.BuildingsVersion > 0 && sim.LotsVersion > 0);
        int nonEmpty = 0;
        for (int i = 0; i < buildings.Length; i++)
        {
            if (buildings[i].Kind == 0)
            {
                continue;
            }

            nonEmpty++;
            Assert.Equal(c.Lots.X[c.Buildings.Lot[i]], buildings[i].X);
            Assert.InRange(buildings[i].FillPct, 0, 100);
            Assert.Equal((byte)BuildingState.Active, buildings[i].State);
            Assert.True(sim.TryGetBuildingInfo(i, buildings[i].Gen, out BuildingInfo bi));
            Assert.Equal((LotZone)buildings[i].Kind, bi.Kind);
            Assert.False(sim.TryGetBuildingInfo(i, buildings[i].Gen + 2, out _));
        }

        Assert.Equal(c.Buildings.AliveCount, nonEmpty);
        int r = -1;
        for (int i = 0; i < c.Residents.HighWater && r < 0; i++)
        {
            r = c.Residents.IsAlive(i) ? i : -1;
        }

        Assert.True(sim.TryGetResidentInfo(r, sim.GetResidentGeneration(r), out ResidentInfo ri));
        Assert.Equal(c.Residents.Home[r], ri.Home);
        Assert.Equal(300L, ri.Balance);
        Assert.Equal(-1, ri.Agent);
        var acc = new int[sim.CityCellCount];
        Assert.Equal(9, sim.GetAccessibility(acc, AccessKind.Jobs));
    }

    // ------------------------------------------------------------ Create: пределы

    [Fact]
    public void Create_RejectsBadCityParameters()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        ScenarioDesc small = d;
        small.Capacities.MaxLots = 10;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in small, out _));

        ScenarioDesc noCaps = d;
        noCaps.Capacities.MaxCityResidents = 0;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in noCaps, out _));

        ScenarioDesc zero = d;
        zero.Town = default;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in zero, out _));

        ScenarioDesc badDay = d;
        badDay.Town.Calendar.DayTicks = 1000;
        Assert.Equal(CreateStatus.InvalidCity, Simulation.TryCreate(in badDay, out _));

        ScenarioDesc badSource = Worlds.Step2.S1k(buses: true);
        badSource.Peds.Source = PedSource.City;
        Assert.Equal(CreateStatus.InvalidPeds, Simulation.TryCreate(in badSource, out _));

        ScenarioDesc ok = d;
        ok.Town.BuildingTypes = null!;   // пустая таблица = базовые 6 типов
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in ok, out Simulation? s));
        s!.Dispose();
    }

    // ------------------------------------------------------------ сейв

    [Fact]
    public void SaveLoadSave_IsByteEqual_AndLotsRebuildIdentically()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        sim.Step(250);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(bytes);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.True(bytes.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(loaded)));

        LotStore a = City(sim).Lots;
        LotStore b = City(loaded).Lots;
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.X[i], b.X[i]);
            Assert.Equal(a.Z[i], b.Z[i]);
            Assert.Equal(a.Heading[i], b.Heading[i]);
            Assert.Equal(a.Cell[i], b.Cell[i]);
            Assert.Equal(a.CarLane[i], b.CarLane[i]);
            Assert.Equal(a.CarS[i], b.CarS[i]);
            Assert.Equal(a.RoadComp[i], b.RoadComp[i]);
        }

        sim.Step(100);
        loaded.Step(100);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void SaveLoad_WithPeds_KeepsDerivedPedestrianAccessPoints()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Peds = new PedDesc { Enabled = true, Count = 100, MidblockAtStops = true, Source = PedSource.City };
        d.Transit.PassengerModel = PassengerModel.Agents;
        SimCapacities c = d.Capacities;
        c.MaxPeds = 200;
        c.MaxResidents = 200;
        c.MaxPedPathSteps = 64;
        d.Capacities = c;
        using Simulation sim = Make(d);
        LotStore a = City(sim).Lots;
        int withEdge = 0;
        for (int i = 0; i < a.Count; i++)
        {
            withEdge += a.PedEdge[i] >= 0 ? 1 : 0;
        }

        Assert.True(withEdge > a.Count / 2);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(bytes);
        LotStore b = City(loaded).Lots;
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.PedEdge[i], b.PedEdge[i]);
            Assert.Equal(a.PedSMm[i], b.PedSMm[i]);
            Assert.Equal(a.PedComp[i], b.PedComp[i]);
        }

        Assert.Equal(PedSource.City, loaded.World.PedCfg.Source);
    }

    [Fact]
    public void Load_RejectsCorruptedCityState()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(d);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(bytes, SaveSchema.KnownBlockIds(), out SaveTables? t, out _));

        // Бит в Occupants здания: список жителей и счётчик расходятся.
        int bldg = t!.FindBlock(SaveFormat.FourCc("BLDG"u8));
        Assert.True(bldg >= 0);
        Assert.True(t.TryGetColumn(bldg, 9, out ReadOnlySpan<int> occ));
        int[] broken = occ.ToArray();
        int victim = Array.FindIndex(broken, v => v > 0);
        broken[victim] += 1;
        Assert.True(t.SetColumn(bldg, 9, broken));
        SaveHeaderInfo h = t.Header;
        long n = SaveWriter.Measure(ref t, out long len) == SaveStatus.Ok ? len : -1;
        byte[] file = new byte[n];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, h, file, out _));
        var o = new LoadOptions { StrictHash = 0 };
        Assert.Equal(LoadStatus.InvalidValue, Simulation.Load(file, in o, default, out Simulation? bad, out _));
        Assert.Null(bad);
    }
}
