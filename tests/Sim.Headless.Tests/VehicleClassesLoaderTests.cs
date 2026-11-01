using System;
using System.IO;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>Загрузчик классов ТС, задача VC1 (vehicle-classes.md §7, тест 1): пять значений <c>category</c>, старые файлы без правок, неизвестное значение - ошибка.</summary>
public class VehicleClassesLoaderTests
{
    private const string Mobil = "\"idm\": { \"a\": 1.0, \"b\": 1.5, \"T\": 1.2, \"s0\": 2.0, \"delta\": 4 }, "
        + "\"mobil\": { \"politeness\": 0.3, \"threshold\": 0.2, \"b_safe\": 4.0, \"right_bias\": 0.3, \"duration_s\": 2.0 }";

    private const string Caps = """
        "capacities": { "max_vehicles": 64, "max_nodes": 8, "max_segments": 8, "max_lanes": 64, "max_lane_points": 512,
          "max_zones": 4, "max_bus_routes": 1, "max_bus_stops": 1, "max_routes": 64,
          "max_plan_steps": 2048, "max_pending_trips": 64, "max_signalized_nodes": 1 }
        """;

    private static string Types(params string[] cats)
    {
        var parts = new string[cats.Length];
        for (int i = 0; i < cats.Length; i++)
        {
            parts[i] = "{ \"name\": \"" + (i == 0 ? "car" : i == 1 ? "bus" : "t" + i) + "\", \"category\": \"" + cats[i] + "\", \"length_m\": 5.0, \"width_m\": 2.0, "
                + "\"max_speed_kmh\": 100, \"speed_factor\": 1.0, " + Mobil + " }";
        }

        return "{ \"format_version\": 1, \"types\": [" + string.Join(", ", parts) + "] }";
    }

    private static readonly string _scenario = "{ \"format_version\": 2, \"seed\": 7, \"duration_s\": 10, \"vehicles\": { \"count\": 5 }, " + Caps + " }";

    private static string RepoFile(params string[] rel)
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "traffic-city.sln")))
            {
                return Path.Combine([d.FullName, .. rel]);
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void AllFiveCategories_Parse()
    {
        ScenarioDesc d = ScenarioLoader.Parse(_scenario, Types("car", "bus", "van", "truck", "special")).Desc;
        Assert.Equal(
            [VehicleCategory.Car, VehicleCategory.Bus, VehicleCategory.Van, VehicleCategory.Truck, VehicleCategory.Special],
            Array.ConvertAll(d.VehicleTypes, t => t.Category));
    }

    [Fact]
    public void OldFile_CarBus_ReadsAsBefore()
    {
        ScenarioDesc d = ScenarioLoader.Parse(_scenario, Types("car", "bus")).Desc;
        Assert.Equal(2, d.VehicleTypes.Length);
        Assert.Equal(VehicleCategory.Car, d.VehicleTypes[0].Category);
        Assert.Equal(VehicleCategory.Bus, d.VehicleTypes[1].Category);
    }

    [Theory]
    [InlineData("tram")]
    [InlineData("Truck")]
    [InlineData("")]
    public void UnknownCategory_IsError(string cat)
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(_scenario, Types("car", cat)));
        Assert.Contains("category", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedCatalog_HasSevenTypes_CarFirst()
    {
        ScenarioDesc d = ScenarioLoader.Parse(_scenario, File.ReadAllText(RepoFile("scenarios", "vehicle-types.json"))).Desc;
        Assert.Equal(
            [VehicleCategory.Car, VehicleCategory.Bus, VehicleCategory.Van, VehicleCategory.Truck, VehicleCategory.Special, VehicleCategory.Special, VehicleCategory.Special],
            Array.ConvertAll(d.VehicleTypes, t => t.Category));
    }
}
