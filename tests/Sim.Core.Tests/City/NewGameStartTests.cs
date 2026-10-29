using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>Y4 (new-game-scratch.md §2.2): сетка города по <c>world_bounds</c>, пол въезда, абсолютная казна, рост по часам, <c>calendar.start_hour</c>.</summary>
public sealed class NewGameStartTests
{
    private const int Day = 72_000;
    private const int Hour = Day / 24;
    private const int GrowthBias = 200_000;   // пустые дома города (около 18 тыс. мест) уже покрывают малый спрос: чтобы спрос R требовал новых домов, он должен быть больше

    /// <summary>Город без населения (автозонирование даёт участки и пустые здания) на сети s1k, карта 6 x 6 км, старт в 06:00, спрос R держит <c>r_bias</c> (внешних работ пока нет, это Y3).</summary>
    private static ScenarioDesc Scratch(bool hourly, int minImmigrants = 0, int rBias = 100)
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Tools = new ToolsDesc { HasWorldBounds = true, BoundsMinX = -3000f, BoundsMinZ = -3000f, BoundsMaxX = 3000f, BoundsMaxZ = 3000f };
        d.Lod.CellM = 1000f;
        d.Town.Zoning.Auto = true;   // без Y3 (внешние работы) спросу R нужны хоть какие-то места работы: готовые пустые фирмы города, а жителей нет
        d.Town.Zoning.Growth = true;
        d.Town.Zoning.GrowthStep = (byte)(hourly ? 1 : 0);
        d.Town.Zoning.GridFromBounds = 1;
        d.Town.Residents.InitialFillQ16 = 0;
        d.Town.Residents.RBias = rBias;
        d.Town.Residents.MinImmigrantsPerDay = minImmigrants;
        d.Town.Calendar.StartHour = 6;
        d.Town.Economy.CityBudget = true;
        d.Town.Economy.InitialCityBalance = 150_000;
        return d;
    }

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static int Alive(CityWorld c)
    {
        int n = 0;
        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            n += c.Buildings.IsAlive(i) ? 1 : 0;
        }

        return n;
    }

    private static void PaintResidential(Simulation sim)
    {
        LotStore l = sim.World.City!.Lots;
        int lot = -1;
        for (int i = 0; i < l.Count && lot < 0; i++)
        {
            lot = l.Building[i] < 0 ? i : -1;
        }

        Assert.True(lot >= 0, "нет пустого участка");
        float cx = l.X[lot], cz = l.Z[lot];
        var cmd = new PaintZoneCmd { X0 = cx - 240f, Z0 = cz - 240f, X1 = cx + 240f, Z1 = cz + 240f, ZoneKind = (byte)LotZone.Residential, Density = 0 };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakePaintZone(cmd, 1)));
    }

    [Fact]
    public void Create_GridFollowsWorldBounds_StartHourShiftsTick_BalanceIsAbsolute()
    {
        ScenarioDesc d = Scratch(hourly: true);
        using Simulation sim = Make(in d);
        CityWorld c = sim.World.City!;
        Assert.Equal(6, c.Grid.CellsX);
        Assert.Equal(6, c.Grid.CellsZ);
        Assert.Equal(-3000f, c.Grid.OriginX);
        Assert.Equal(-3000f, c.Grid.OriginZ);
        Assert.Equal(6 * Hour, sim.Tick);
        Assert.Equal(0, c.Residents.AliveCount);
        Assert.Equal(150_000, c.Ledger.City);
    }

    [Fact]
    public void Defaults_KeepOldBehaviour()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(in d);
        CityWorld c = sim.World.City!;
        Assert.Equal(0, d.Town.Calendar.StartHour);
        Assert.Equal(0, d.Town.Zoning.GrowthStep);
        Assert.Equal(0, d.Town.Zoning.GridFromBounds);
        Assert.Equal(0, d.Town.Residents.MinImmigrantsPerDay);
        Assert.Equal(0, d.Town.Economy.InitialCityBalance);
        Assert.Equal(0, sim.Tick);
        Assert.True(c.Grid.CellsX * c.Grid.CellsZ >= 1);
    }

    [Fact]
    public void HourlyGrowth_PaintedResidential_GetsBuildingsWithinTwoGameHours()
    {
        ScenarioDesc d = Scratch(hourly: true, rBias: GrowthBias);
        using Simulation sim = Make(in d);
        PaintResidential(sim);
        int before = Alive(sim.World.City!);
        sim.Step(2 * Hour);
        Assert.True(Alive(sim.World.City!) > before, "за 2 игровых часа ни одного здания в стройке");
    }

    [Fact]
    public void DailyGrowth_SameStart_HasNoBuildingsInFirstHours()
    {
        ScenarioDesc d = Scratch(hourly: false, rBias: GrowthBias);
        using Simulation sim = Make(in d);
        PaintResidential(sim);
        int before = Alive(sim.World.City!);
        sim.Step(2 * Hour);
        Assert.Equal(before, Alive(sim.World.City!));
    }

    [Fact]
    public void MinImmigrants_IsFloorOfIntake()
    {
        int Residents(int min)
        {
            ScenarioDesc d = Scratch(hourly: true, min);
            using Simulation sim = Make(in d);
            sim.Step(5 * Hour);
            return sim.World.City!.Residents.AliveCount;
        }

        int none = Residents(0);
        int floor = Residents(240);   // 10 в час
        Assert.True(none <= 5, $"без пола при N = 0 въезд не больше 1 в час, получено {none}");
        Assert.True(floor >= 10, $"с полом 240 в сутки за 5 часов ждём >= 10, получено {floor}");
    }

    [Fact]
    public void Hourly_HashIsIndependentOfThreadCount()
    {
        ulong Run(int threads)
        {
            ScenarioDesc d = Scratch(hourly: true, 240);
            using Simulation sim = Make(in d, threads);
            sim.Step(3 * Hour);
            return sim.ComputeStateHash();
        }

        ulong h1 = Run(1);
        Assert.Equal(h1, Run(2));
        Assert.Equal(h1, Run(3));
    }

    [Fact]
    public void Hourly_SaveRoundTrip_ContinuesIdentically()
    {
        ScenarioDesc d = Scratch(hourly: true, 240);
        using Simulation a = Make(in d);
        a.Step(Hour + 500);
        using Simulation b = ToolsFixture.RoundTrip(a);
        a.Step(2 * Hour);
        b.Step(2 * Hour);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(1, b.World.City!.Cfg.Zoning.GrowthStep);
        Assert.Equal(240, b.World.City.Cfg.Residents.MinImmigrantsPerDay);
        Assert.Equal(150_000, b.World.City.Cfg.Economy.InitialCityBalance);
    }

    [Fact]
    public void Hourly_Step_AllocatesNothing_AcrossHourEnds()
    {
        ScenarioDesc d = Scratch(hourly: true, 240);
        using Simulation sim = Make(in d);
        sim.Step(Hour);
        long bytes = AllocationProbe.Measure(() => sim.Step(Hour + 200));   // сдвиг часа внутри окна
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Loader_ParsesNewKeys_AndRejectsBadOnes()
    {
        string json = File.ReadAllText(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json"));
        static string Reader(string file) => File.ReadAllText(Path.Combine(GenFixture.RepoRoot(), "scenarios", file));
        string ok = json
            .Replace("\"day_s\": 3600", "\"day_s\": 3600, \"start_hour\": 6")
            .Replace("\"initial_fill\": 0.1", "\"initial_fill\": 0.1, \"min_immigrants_per_day\": 120")
            .Replace("\"calendar\": {", "\"zoning\": { \"growth_step\": \"hour\", \"grid_from_bounds\": true }, \"economy\": { \"initial_city_balance\": 150000 }, \"tools\": { \"world_bounds\": { \"min_x\": -3000, \"min_z\": -3000, \"max_x\": 3000, \"max_z\": 3000 } }, \"calendar\": {");
        CityDesc t = ScenarioLoader.Parse(ok, Reader, "t").Desc.Town;
        Assert.Equal(6, t.Calendar.StartHour);
        Assert.Equal(1, t.Zoning.GrowthStep);
        Assert.Equal(1, t.Zoning.GridFromBounds);
        Assert.Equal(120, t.Residents.MinImmigrantsPerDay);
        Assert.Equal(150_000, t.Economy.InitialCityBalance);
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(ok.Replace("\"hour\"", "\"week\""), Reader, "t"));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(ok.Replace("\"start_hour\": 6", "\"start_hour\": 24"), Reader, "t"));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(ok.Replace("\"tools\"", "\"tools_\""), Reader, "t"));
    }
}
