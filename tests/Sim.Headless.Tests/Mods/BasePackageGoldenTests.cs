#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Scenarios;
using Sim.Scenarios.Mods;
using ModAssetKind = Sim.Scenarios.Mods.AssetKind;

namespace Sim.Headless.Tests.Mods;

/// <summary>
/// Пакет <c>base</c> и сценарии v3 (modding.md §3, §5.2; M2): MD2, MD3 - <c>ScenarioDesc</c> v3 поле за полем равен v2,
/// golden-хэши s1k/s10k на v3-копиях равны эталонам <see cref="HotPathGoldenHashTests"/>.
/// </summary>
public sealed class BasePackageGoldenTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tc-base-" + Guid.NewGuid().ToString("N"));

    public BasePackageGoldenTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, true);
        }
        catch (IOException)
        {
            // временный каталог - не критично
        }
    }

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "traffic-city.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Scenarios() => Path.Combine(Root(), "scenarios");

    private static PackageSearchDir[] GameDirs() => [new PackageSearchDir(Path.Combine(Root(), "content"), true)];

    private PackageSearchDir[] AllDirs() => [.. GameDirs(), new PackageSearchDir(_tmp, false)];

    // ------------------------------------------------------------ глубокое сравнение ScenarioDesc

    private static void AssertDeepEqual(object? expected, object? actual, string path)
    {
        if (expected == null || actual == null)
        {
            Assert.True(expected == null && actual == null, $"{path}: one side is null");
            return;
        }

        Type t = expected.GetType();
        Assert.True(t == actual.GetType(), $"{path}: type {t} vs {actual.GetType()}");
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
        {
            Assert.True(expected.Equals(actual), $"{path}: {expected} vs {actual}");
            return;
        }

        if (expected is IEnumerable ea)
        {
            object?[] l = ea.Cast<object?>().ToArray();
            object?[] r = ((IEnumerable)actual).Cast<object?>().ToArray();
            Assert.True(l.Length == r.Length, $"{path}: length {l.Length} vs {r.Length}");
            for (int i = 0; i < l.Length; i++)
            {
                AssertDeepEqual(l[i], r[i], $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]");
            }

            return;
        }

        foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            AssertDeepEqual(f.GetValue(expected), f.GetValue(actual), $"{path}.{f.Name}");
        }
    }

    public static IEnumerable<object[]> V2Scenarios() =>
        Directory.GetFiles(Path.Combine(Root(), "scenarios"), "*.json")
            .Select(Path.GetFileName)
            .Where(n => n != "vehicle-types.json" && n != "road-profiles.json" && !n!.EndsWith("-v3.json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n! });

    // ------------------------------------------------------------ MD3: ScenarioDesc v3 == v2

    [Theory]
    [InlineData("s1k")]
    [InlineData("s10k")]
    public void CommittedV3Copy_ScenarioDesc_EqualsV2(string name)
    {
        LoadedScenario v2 = ScenarioLoader.LoadInfo(Path.Combine(Scenarios(), name + ".json"));
        LoadedScenario v3 = ScenarioLoader.LoadInfo(Path.Combine(Scenarios(), name + "-v3.json"));
        AssertDeepEqual(v2.Desc, v3.Desc, "$");
        Assert.Equal(v2.TypeNames, v3.TypeNames);
        Assert.NotNull(v3.Catalog);
        Assert.Equal(["base"], v3.Packages!.Packages.Select(p => p.Id).ToArray());
    }

    [Theory]
    [InlineData("s1k")]
    [InlineData("s10k")]
    public void CommittedV3Copy_IsTheV2Upgrade(string name)
    {
        JsonNode v2 = JsonNode.Parse(File.ReadAllText(Path.Combine(Scenarios(), name + ".json")))!;
        JsonNode v3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Scenarios(), name + "-v3.json")))!;
        Assert.True(JsonNode.DeepEquals(FormatUpgrades.ScenarioV2ToV3(v2), v3), "committed v3 copy differs from the v2 -> v3 upgrade of the v2 file");
    }

    // Все сценарии v2 (в т.ч. *-peds, s100k, s1m) поднимаются чистой функцией и дают тот же ScenarioDesc.
    [Theory]
    [MemberData(nameof(V2Scenarios))]
    public void UpgradedV2_GivesSameScenarioDesc(string file)
    {
        string path = Path.Combine(Scenarios(), file);
        LoadedScenario v2 = ScenarioLoader.LoadInfo(path);
        string v3Text = FormatUpgrades.ScenarioV2ToV3(JsonNode.Parse(File.ReadAllText(path))!).ToJsonString();
        LoadedScenario v3 = ScenarioLoader.Parse(v3Text, _ => throw new InvalidOperationException("no files in v3"), "x", GameDirs());
        AssertDeepEqual(v2.Desc, v3.Desc, "$");
    }

    [Fact]
    public void V2UpgradeIsPure_InputUnchanged()
    {
        JsonNode v2 = JsonNode.Parse(File.ReadAllText(Path.Combine(Scenarios(), "s1k.json")))!;
        string before = v2.ToJsonString();
        _ = FormatUpgrades.ScenarioV2ToV3(v2);
        Assert.Equal(before, v2.ToJsonString());
    }

    // ------------------------------------------------------------ MD3: golden-хэши на v3-копиях

    private static Simulation CreateV3(string name)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(Scenarios(), name + "-v3.json")).Desc;
        d.City.SignalMode = CitySignalMode.Fixed;                    // эталоны HotPathGoldenHashTests сняты на fixed + legacy
        d.City.SignalCoordination = CitySignalCoordination.Legacy;
        return Simulation.Create(d, new SimRuntimeOptions { WorkerThreads = 1 });
    }

    private static string Hex(ulong h) => h.ToString("x16", CultureInfo.InvariantCulture);

    [Fact]
    public void S1kV3_StateHash_MatchesHotPathGolden()
    {
        using Simulation sim = CreateV3("s1k");
        string[] expected = { "2af924566810da2f", "bf55c7d416e70bc8", "293a50c4eaa29f71", "558d3c3ef0b080e0" };   // = HotPathGoldenHashTests
        foreach (string e in expected)
        {
            sim.Step(1000);
            Assert.Equal(e, Hex(sim.ComputeStateHash()));
        }
    }

    [Fact]
    public void S10kV3_StateHash_MatchesHotPathGolden()
    {
        using Simulation sim = CreateV3("s10k");
        string[] expected = { "968acda58db1f475", "b6de7d495370be2c", "4a215863ab2ef5ae" };   // = HotPathGoldenHashTests
        foreach (string e in expected)
        {
            sim.Step(200);
            Assert.Equal(e, Hex(sim.ComputeStateHash()));
        }
    }

    // ------------------------------------------------------------ каталог base

    [Fact]
    public void Base_Catalog_HasCarThenBus_AndTwoRoads()
    {
        PackageSet set = PackageLoader.Load(GameDirs(), ["base"]);
        Assert.True(set.Ok, string.Join("; ", set.Errors));
        var issues = new List<ModIssue>();
        ContentCatalog cat = ContentCatalog.Build(set, issues);
        Assert.Empty(issues);
        Assert.Equal(["base:vehicle/car", "base:vehicle/bus"], cat.Vehicles.Select(v => v.Id.ToString()).ToArray());
        Assert.Equal(["base:road/street_2", "base:road/arterial_4"], cat.Roads.Select(v => v.Id.ToString()).ToArray());
        Assert.Equal(0, cat.FindVehicle(new AssetId("base", ModAssetKind.Vehicle, "car")));
        Assert.Equal(1, cat.FindVehicle(new AssetId("base", ModAssetKind.Vehicle, "bus")));
        Assert.Equal(-1, cat.FindVehicle(new AssetId("base", ModAssetKind.Vehicle, "tram")));
    }

    [Fact]
    public void Base_Roads_MatchGenerator()
    {
        var issues = new List<ModIssue>();
        ContentCatalog cat = ContentCatalog.Build(PackageLoader.Load(GameDirs(), ["base"]), issues);
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(Scenarios(), "s1k.json")).Desc;
        RoadEntry street = cat.Roads[0];
        RoadEntry art = cat.Roads[1];
        Assert.Equal((1, 1, d.City.StreetSpeed), (street.LanesAB, street.LanesBA, street.SpeedLimit));
        Assert.Equal((2, 2, d.City.ArterialSpeed), (art.LanesAB, art.LanesBA, art.SpeedLimit));
    }

    // ------------------------------------------------------------ сценарий v3 с модом: индексы, ссылки, ошибки

    private void WritePkg(string id, string vehicles, string requires = "[{ \"id\": \"base\", \"version\": \"^1.0.0\" }]", string? roads = null)
    {
        string dir = Path.Combine(_tmp, id);
        Directory.CreateDirectory(dir);
        string content = "\"vehicles\": [\"vehicles.json\"]" + (roads != null ? ", \"roads\": [\"roads.json\"]" : string.Empty);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            $"{{ \"format_version\": 1, \"id\": \"{id}\", \"version\": \"1.0.0\", \"content_format\": 1, \"requires\": {requires}, \"content\": {{ {content} }} }}");
        File.WriteAllText(Path.Combine(dir, "vehicles.json"), vehicles);
        if (roads != null)
        {
            File.WriteAllText(Path.Combine(dir, "roads.json"), roads);
        }
    }

    private static string Van(string name = "van", string category = "car", string length = "5.5") =>
        $"{{ \"name\": \"{name}\", \"category\": \"{category}\", \"length_m\": {length}, \"width_m\": 2.0, \"max_speed_kmh\": 110, \"speed_factor\": 1.0, "
        + "\"idm\": { \"a\": 0.9, \"b\": 1.5, \"T\": 1.3, \"s0\": 2.0, \"delta\": 4 }, "
        + "\"mobil\": { \"politeness\": 0.3, \"threshold\": 0.2, \"b_safe\": 4.0, \"right_bias\": 0.3, \"duration_s\": 2.0 } }";

    private static string Vehicles(params string[] types) =>
        "{ \"format_version\": 2, \"types\": [" + string.Join(", ", types) + "] }";

    private static string Doc(string packages, string typeMix = "", string transit = "", string city = "") =>
        "{ \"format_version\": 3, \"seed\": 7, \"duration_s\": 10, \"packages\": " + packages + ", \"vehicles\": { \"count\": 5"
        + (typeMix.Length > 0 ? ", \"type_mix\": " + typeMix : string.Empty) + " }, "
        + "\"capacities\": { \"max_vehicles\": 64, \"max_nodes\": 8, \"max_segments\": 8, \"max_lanes\": 64, \"max_lane_points\": 512, "
        + "\"max_zones\": 4, \"max_bus_routes\": 1, \"max_bus_stops\": 1, \"max_routes\": 64, \"max_plan_steps\": 2048, "
        + "\"max_pending_trips\": 64, \"max_signalized_nodes\": 1 }"
        + (transit.Length > 0 ? ", \"transit\": " + transit : string.Empty)
        + (city.Length > 0 ? ", \"city\": " + city : string.Empty) + " }";

    private LoadedScenario Parse(string json) =>
        ScenarioLoader.Parse(json, _ => throw new InvalidOperationException("no files"), "t", AllDirs());

    private ScenarioException Fails(string json, ModError? code = null)
    {
        ScenarioException e = Assert.Throws<ScenarioException>(() => Parse(json));
        if (code != null)
        {
            Assert.True(code == e.ModCode, $"expected {code}, got {e.ModCode}: {e.Message}");
        }

        return e;
    }

    [Fact]
    public void ModPackage_AppendsTypesAfterBase_AndMixResolvesFullIds()
    {
        WritePkg("transit_plus", Vehicles(Van(), Van("bus_long", "bus", "18.0")));
        LoadedScenario sc = Parse(Doc("[\"transit_plus\"]", "{ \"base:vehicle/car\": 0.9, \"transit_plus:vehicle/van\": 0.1 }",
            "{ \"routes\": 1, \"bus_type\": \"transit_plus:vehicle/bus_long\" }"));
        Assert.Equal(["base", "transit_plus"], sc.Packages!.Packages.Select(p => p.Id).ToArray());
        Assert.Equal(["car", "bus", "van", "bus_long"], sc.TypeNames);
        Assert.Equal(4, sc.Desc.VehicleTypes.Length);
        Assert.Equal(new float[] { 0.9f, 0f, 0.1f, 0f }, sc.Desc.Demand.TypeMix);
        Assert.Equal(3, sc.Desc.Transit.BusType);
    }

    [Fact]
    public void ShortRefs_ResolveToBase_AndDefaultsAreCarAndBus()
    {
        LoadedScenario sc = Parse(Doc("[\"base\"]", "{ \"vehicle/bus\": 1.0 }", "{ \"routes\": 1, \"bus_type\": \"vehicle/bus\" }"));
        Assert.Equal(new float[] { 0f, 1f }, sc.Desc.Demand.TypeMix);
        Assert.Equal(1, sc.Desc.Transit.BusType);
        LoadedScenario def = Parse(Doc("[\"base\"]", transit: "{ \"routes\": 1 }"));
        Assert.Equal(new float[] { 1f, 0f }, def.Desc.Demand.TypeMix);
        Assert.Equal(1, def.Desc.Transit.BusType);
    }

    [Fact]
    public void Catalog_IsIndependentOfSearchDirOrderAndPackageListOrder()
    {
        WritePkg("aaa_mod", Vehicles(Van("a1")));
        WritePkg("zzz_mod", Vehicles(Van("z1")));
        string[] names1 = ScenarioLoader.Parse(Doc("[\"zzz_mod\", \"aaa_mod\"]"), _ => "", "t", AllDirs()).TypeNames;
        string[] names2 = ScenarioLoader.Parse(Doc("[\"aaa_mod\", \"zzz_mod\"]"), _ => "", "t", [.. AllDirs().Reverse()]).TypeNames;
        Assert.Equal(["car", "bus", "a1", "z1"], names1);
        Assert.Equal(names1, names2);
    }

    [Fact]
    public void VehiclesV1File_IsUpgradedAndAccepted()
    {
        WritePkg("old_mod", Vehicles(Van()).Replace("\"format_version\": 2", "\"format_version\": 1", StringComparison.Ordinal));
        Assert.Equal(3, Parse(Doc("[\"old_mod\"]")).Desc.VehicleTypes.Length);
    }

    [Fact]
    public void Errors_UnknownAndUndeclaredRefs()
    {
        WritePkg("lonely", Vehicles(Van("solo")), "[]");
        Fails(Doc("[\"base\"]", "{ \"base:vehicle/tram\": 1.0 }"), ModError.UnknownAssetRef);
        Fails(Doc("[\"base\"]", "{ \"transit_plus:vehicle/van\": 1.0 }"), ModError.UndeclaredDependency);
        Fails(Doc("[\"base\"]", "{ \"car\": 1.0 }"), ModError.BadValue);
        Fails(Doc("[\"base\"]", transit: "{ \"routes\": 1, \"bus_type\": \"base:vehicle/tram\" }"), ModError.UnknownAssetRef);
        Fails(Doc("[\"nope_pkg\"]"), ModError.MissingDependency);
        Fails(Doc("[\"base\", \"base\"]"), ModError.BadValue);
    }

    [Fact]
    public void Errors_DuplicateAndFirstTypeAndRanges()
    {
        WritePkg("dup_mod", Vehicles(Van(), Van()));
        Fails(Doc("[\"dup_mod\"]"), ModError.DuplicateId);

        WritePkg("range_mod", Vehicles(Van(length: "40")));
        Fails(Doc("[\"range_mod\"]"), ModError.OutOfRange);

        WritePkg("key_mod", Vehicles(Van().Replace("\"name\"", "\"colour\": 1, \"name\"", StringComparison.Ordinal)));
        Fails(Doc("[\"key_mod\"]"), ModError.UnknownKey);

        WritePkg("badname_mod", Vehicles(Van("Van")));
        Fails(Doc("[\"badname_mod\"]"), ModError.BadValue);

        // Первый тип набора обязан быть car: пакет без requires на base, но с автобусом первым.
        WritePkg("busfirst", Vehicles(Van("b", "bus")), "[]");
        Fails(Doc("[\"busfirst\"]"), ModError.BadValue);
    }

    [Fact]
    public void Errors_CapacityAndAssetLimits()
    {
        WritePkg("many_mod", Vehicles(Enumerable.Range(0, 255).Select(i => Van("t" + i.ToString(CultureInfo.InvariantCulture))).ToArray()));
        Fails(Doc("[\"many_mod\"]"), ModError.CapacityExceeded);   // 2 base + 255 = 257 > 256

        string roads = "{ \"format_version\": 1, \"roads\": ["
            + string.Join(", ", Enumerable.Range(0, 4097).Select(i =>
                $"{{ \"name\": \"r{i.ToString(CultureInfo.InvariantCulture)}\", \"kind\": \"street\", \"lanes_ab\": 1, \"lanes_ba\": 1, \"speed_kmh\": 30 }}"))
            + "] }";
        WritePkg("roads_mod", Vehicles(Van()), roads: roads);
        Fails(Doc("[\"roads_mod\"]"), ModError.TooLarge);
    }

    [Fact]
    public void V3_Rejects_VehicleTypesFile_AndMissingPackages_AndPlainV2Keys()
    {
        Fails(Doc("[\"base\"]").Replace("\"seed\"", "\"vehicle_types_file\": \"vehicle-types.json\", \"seed\"", StringComparison.Ordinal), ModError.UnknownKey);
        Fails(Doc("[\"base\"]").Replace("\"packages\": [\"base\"], ", string.Empty, StringComparison.Ordinal));
        Fails(Doc("[]"), ModError.BadValue);
        Assert.Contains("loader supports 2 and 3", Assert.Throws<ScenarioException>(() =>
            ScenarioLoader.Parse(Doc("[\"base\"]").Replace("\"format_version\": 3", "\"format_version\": 4", StringComparison.Ordinal),
                _ => "", "t", AllDirs())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoadAssets_AreValidated()
    {
        const string ok = "{ \"road_assets\": { \"street\": \"base:road/street_2\", \"arterial\": \"road/arterial_4\" } }";
        Assert.Equal(2, Parse(Doc("[\"base\"]", city: ok)).Catalog!.Roads.Count);
        Fails(Doc("[\"base\"]", city: "{ \"road_assets\": { \"street\": \"base:road/nope\" } }"), ModError.UnknownAssetRef);
        Fails(Doc("[\"base\"]", city: "{ \"road_assets\": { \"street\": \"base:road/arterial_4\" } }"), ModError.BadValue);
        Fails(Doc("[\"base\"]", city: "{ \"road_assets\": { \"plaza\": \"base:road/street_2\" } }"), ModError.UnknownKey);
    }

    [Fact]
    public void V3_WithoutContentDir_FailsWithMessage_NotException()
    {
        ScenarioException e = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(Doc("[\"base\"]"), _ => "", "t", null));
        Assert.Equal(ModError.MissingDependency, e.ModCode);
    }
}
