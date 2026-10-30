using System;
using System.IO;
using Sim.Core.Api;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

namespace Sim.Headless.Tests;

/// <summary>Секции режима жителей (scenario-format.md, zoning-demand.md §16), <c>peds.source</c> и пакет <c>base</c> с <c>buildings.json</c> (M10, §17).</summary>
public class CityScenarioTests
{
    private const string Types = """
        {
          "format_version": 1,
          "types": [
            { "name": "car", "category": "car", "length_m": 4.5, "width_m": 1.8, "max_speed_kmh": 130, "speed_factor": 1.05,
              "idm": { "a": 1.0, "b": 1.5, "T": 1.2, "s0": 2.0, "delta": 4 },
              "mobil": { "politeness": 0.3, "threshold": 0.2, "b_safe": 4.0, "right_bias": 0.3, "duration_s": 2.0 } },
            { "name": "bus", "category": "bus", "length_m": 12.0, "width_m": 2.55, "max_speed_kmh": 80, "speed_factor": 0.95,
              "idm": { "a": 0.8, "b": 1.2, "T": 1.6, "s0": 2.5, "delta": 4 },
              "mobil": { "politeness": 0.5, "threshold": 0.3, "b_safe": 3.0, "right_bias": 0.5, "duration_s": 3.0 } }
          ]
        }
        """;

    private const string ResCaps = "\"max_lots\": 100, \"max_buildings\": 80, \"max_city_residents\": 500";

    private static string Doc(string extra = "", string capsExtra = "", string demand = "\"demand\": { \"model\": \"residents\" }") =>
        "{ \"format_version\": 2, \"seed\": 7, \"duration_s\": 10, \"vehicles\": { \"count\": 0 }, "
        + "\"capacities\": { \"max_vehicles\": 64, \"max_nodes\": 8, \"max_segments\": 8, \"max_lanes\": 64, \"max_lane_points\": 512, "
        + "\"max_zones\": 4, \"max_bus_routes\": 1, \"max_bus_stops\": 1, \"max_routes\": 64, "
        + "\"max_plan_steps\": 2048, \"max_pending_trips\": 64, \"max_signalized_nodes\": 1" + (capsExtra.Length > 0 ? ", " + capsExtra : string.Empty) + " }"
        + (demand.Length > 0 ? ", " + demand : string.Empty)
        + (extra.Length > 0 ? ", " + extra : string.Empty) + " }";

    private static string Lod => "\"lod\": { \"mode\": \"off\", \"cell_m\": 1000 }";

    private static ScenarioDesc Load(string json) => ScenarioLoader.Parse(json, Types).Desc;

    private static ScenarioException Fails(string json) => Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, Types));

    [Fact]
    public void Residents_WithoutSections_GetSpecDefaults()
    {
        ScenarioDesc d = Load(Doc(Lod, ResCaps));
        Assert.Equal(DemandModel.Residents, d.Demand.Model);
        CityDesc t = d.Town;
        CityDesc def = CityDesc.Default();
        Assert.Equal(72_000, t.Calendar.DayTicks);
        Assert.Equal(40_000, t.Zoning.LotFrontageMm);
        Assert.True(t.Zoning.Auto);
        Assert.Equal(CityDesc.Q16(0.15), t.Zoning.CCenterShareQ16);
        Assert.Equal(def.Residents, t.Residents);
        Assert.Equal(def.Economy, t.Economy);
        Assert.Equal(def.Choice, t.Choice);
        Assert.Equal(7, t.BuildingTypes.Length);   // v2: базовая таблица (BL3: седьмая строка - депо ЖКХ)
        Assert.Equal(100, d.Capacities.MaxLots);
        Assert.Equal(80, d.Capacities.MaxBuildings);
        Assert.Equal(500, d.Capacities.MaxCityResidents);
        Assert.Equal(65_536, d.Capacities.SkimObsCapacity);
    }

    [Fact]
    public void Residents_Sections_ConvertUnits()
    {
        string sections = Lod + ", \"calendar\": { \"day_s\": 7200 }, "
            + "\"zoning\": { \"lot_frontage_m\": 30, \"lot_depth_m\": 25.5, \"node_clear_m\": 12, \"growth\": false, \"max_new_buildings_per_day\": 5, "
            + "\"auto\": { \"c_center_share\": 0.25, \"i_edge_share\": 0.1, \"unzoned_share\": 0.5 } }, "
            + "\"residents\": { \"initial_fill\": 0.5, \"car_ownership\": 1.0, \"trip_execution\": \"aggregate\", \"wait_max_s\": 300, \"plan_slot\": 40, \"switch_ratio\": 2.0 }, "
            + "\"economy\": { \"wage_per_shift\": 80, \"ewma_shift\": 2 }, "
            + "\"choice\": { \"theta_mode_per_s\": 0.02, \"asc_freq_s\": -150.5, \"w_wait\": 1.5, \"max_walk_m\": 900, \"skim_periods\": 4 }";
        CityDesc t = Load(Doc(sections, ResCaps)).Town;
        Assert.Equal(144_000, t.Calendar.DayTicks);
        Assert.Equal(30_000, t.Zoning.LotFrontageMm);
        Assert.Equal(25_500, t.Zoning.LotDepthMm);
        Assert.Equal(12_000, t.Zoning.NodeClearMm);
        Assert.False(t.Zoning.Growth);
        Assert.Equal(5, t.Zoning.MaxNewBuildingsPerDay);
        Assert.Equal(16_384, t.Zoning.CCenterShareQ16);
        Assert.Equal(6_554, t.Zoning.IEdgeShareQ16);
        Assert.Equal(32_768, t.Zoning.UnzonedShareQ16);
        Assert.Equal(32_768, t.Residents.InitialFillQ16);
        Assert.Equal(65_536, t.Residents.CarOwnershipQ16);
        Assert.Equal(TripExecution.Aggregate, t.Residents.TripExecution);
        Assert.Equal(6_000, t.Residents.WaitMaxTicks);
        Assert.Equal(40, t.Residents.PlanSlot);
        Assert.Equal(512, t.Residents.SwitchRatioQ8);
        Assert.Equal(80, t.Economy.WagePerShift);
        Assert.Equal(2, t.Economy.EwmaShift);
        Assert.Equal(8_389, t.Choice.ThetaMode64);   // 0.02 / 10 · 64 · 65536
        Assert.Equal(-1_505, t.Choice.AscFreqDs);
        Assert.Equal(384, t.Choice.WWaitQ8);
        Assert.Equal(900_000, t.Choice.MaxWalkMm);
        Assert.Equal(4, t.Choice.SkimPeriods);
    }

    [Fact]
    public void Residents_AutoFalse_DisablesAutoZoning()
    {
        CityDesc t = Load(Doc(Lod + ", \"zoning\": { \"auto\": false }", ResCaps)).Town;
        Assert.False(t.Zoning.Auto);
    }

    [Fact]
    public void Residents_Requirements_AreEnforced()
    {
        Assert.Contains("lod.cell_m", Fails(Doc(string.Empty, ResCaps)).Message, StringComparison.Ordinal);
        Assert.Contains("lod.cell_m", Fails(Doc("\"lod\": { \"mode\": \"off\" }", ResCaps)).Message, StringComparison.Ordinal);
        Assert.Contains("max_lots", Fails(Doc(Lod, "\"max_buildings\": 80, \"max_city_residents\": 500")).Message, StringComparison.Ordinal);
        Assert.Contains("day_s", Fails(Doc(Lod + ", \"calendar\": { \"day_s\": 100 }", ResCaps)).Message, StringComparison.Ordinal);
        Assert.Contains("trip_execution", Fails(Doc(Lod + ", \"residents\": { \"trip_execution\": \"both\" }", ResCaps)).Message, StringComparison.Ordinal);
        Assert.Contains("unknown key", Fails(Doc(Lod + ", \"economy\": { \"taxes\": 1 }", ResCaps)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CitySections_AreRejectedWithoutResidentsModel()
    {
        foreach (string key in new[] { "calendar", "zoning", "residents", "economy", "choice" })
        {
            Assert.Contains("residents", Fails(Doc("\"" + key + "\": {}", string.Empty, "\"demand\": { \"model\": \"gravity\" }")).Message, StringComparison.Ordinal);
        }

        Assert.Contains("max_lots", Fails(Doc(string.Empty, "\"max_lots\": 10", string.Empty)).Message, StringComparison.Ordinal);
        ScenarioDesc plain = Load(Doc(string.Empty, string.Empty, string.Empty));
        Assert.Equal(default, plain.Town);
        Assert.Equal(0, plain.Capacities.MaxLots);
    }

    [Fact]
    public void PedsSource_DefaultsToPreform_AndCityNeedsResidents()
    {
        string peds = "\"peds\": { \"count\": 10, \"source\": \"city\" }";
        string pedCaps = "\"max_peds\": 130, \"max_residents\": 200";
        Assert.Equal(PedSource.Preform, Load(Doc("\"peds\": { \"count\": 10 }", pedCaps, string.Empty)).Peds.Source);
        Assert.Equal(PedSource.City, Load(Doc(Lod + ", " + peds, pedCaps + ", " + ResCaps)).Peds.Source);
        Assert.Contains("residents", Fails(Doc(peds, pedCaps, string.Empty)).Message, StringComparison.Ordinal);
        Assert.Contains("source", Fails(Doc("\"peds\": { \"count\": 10, \"source\": \"x\" }", pedCaps, string.Empty)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ пакет base: buildings.json (M10)

    private static string ContentDir()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "traffic-city.sln")))
            {
                return Path.Combine(d.FullName, "content");
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void BasePackage_BuildingsEqualTheCoreDefaults()
    {
        PackageSet set = PackageLoader.Load([new PackageSearchDir(ContentDir(), true)], ["base"]);
        set.ThrowIfFailed();
        var issues = new System.Collections.Generic.List<ModIssue>();
        ContentCatalog cat = ContentCatalog.Build(set, issues);
        Assert.Empty(issues);
        BuildingTypeDesc[] def = CityDesc.BaseBuildingTypes();
        BuildingTypeDesc[] svc = CityDesc.BaseServiceBuildingTypes();   // SR6: после 7 типов ядра - 5 типов служб медицины и пожарных
        Assert.Equal(def.Length + svc.Length, cat.Buildings.Count);
        for (int i = 0; i < def.Length + svc.Length; i++)
        {
            Assert.Equal(i < def.Length ? def[i] : svc[i - def.Length], cat.Buildings[i].Desc);
            Assert.Equal(i, cat.FindBuilding(cat.Buildings[i].Id));
        }

        Assert.Equal("base:building/r_low", cat.Buildings[0].Id.ToString());
    }

    [Fact]
    public void BadBuildings_FieldsAreChecked_NotJustPresence()
    {
        string fixtures = Path.Combine(ContentDir(), "..", "tests", "ModFixtures");
        PackageSet set = PackageLoader.Load([new PackageSearchDir(fixtures, false)], ["bad_buildings"]);
        Assert.True(set.Ok, "манифест с непустым buildings теперь допустим");
        var issues = new System.Collections.Generic.List<ModIssue>();
        ContentCatalog cat = ContentCatalog.Build(set, issues);
        Assert.Empty(cat.Buildings);
        Assert.Equal(3, issues.Count);
        Assert.Contains(issues, i => i.Code == ModError.OutOfRange && i.JsonPath.Contains("capacity", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.JsonPath.Contains("kind", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.JsonPath.Contains("shop_capacity", StringComparison.Ordinal));
    }

    [Fact]
    public void ScenarioV3_WithBasePackage_UsesTheCatalogBuildings()
    {
        string path = Path.Combine(ContentDir(), "..", "scenarios", "s1k-v3.json");
        string text = File.ReadAllText(path).Replace("\"model\": \"gravity\"", "\"model\": \"residents\"", StringComparison.Ordinal);
        Assert.Contains("residents", text, StringComparison.Ordinal);
        text = text.Replace("\"max_vehicles\"", "\"max_lots\": 4000, \"max_buildings\": 3000, \"max_city_residents\": 20000, \"max_vehicles\"", StringComparison.Ordinal);
        text = text.Replace("\"lod\": {", "\"lod\": { \"cell_m\": 1000,", StringComparison.Ordinal);
        if (!text.Contains("\"lod\"", StringComparison.Ordinal))
        {
            text = text.Replace("\"rules\"", "\"lod\": { \"cell_m\": 1000 }, \"rules\"", StringComparison.Ordinal);
        }

        LoadedScenario sc = ScenarioLoader.Parse(text, name => File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, name)), "s1k-res-test", ScenarioLoader.DefaultSearchDirs(Path.GetDirectoryName(path)!));
        Assert.NotNull(sc.Catalog);
        Assert.Equal(sc.Catalog!.Buildings.Count, sc.Desc.Town.BuildingTypes.Length);
        Assert.Equal(CityDesc.BaseBuildingTypes()[2], sc.Desc.Town.BuildingTypes[2]);
    }
}
