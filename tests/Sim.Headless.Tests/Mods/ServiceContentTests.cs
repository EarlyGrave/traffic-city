using System;
using System.Collections.Generic;
using System.IO;
using Sim.Core.Api;
using Sim.Scenarios.Mods;
using AssetKind = Sim.Scenarios.Mods.AssetKind;

namespace Sim.Headless.Tests.Mods;

/// <summary>
/// Загрузчик записей служб <c>services.json</c> (services.md §2.2, §8 тест 2): схема, ссылки на типы зданий и ТС, <c>on_call</c> у <c>repair</c> - ошибка, роли уровней,
/// лимиты, дубликаты, моды поверх <c>base</c>; базовая запись <c>utility</c> = встроенная запись ядра.
/// </summary>
public sealed class ServiceContentTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tc-svc-" + Guid.NewGuid().ToString("N"));

    public ServiceContentTests() => Directory.CreateDirectory(_tmp);

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

    private static string ContentDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "traffic-city.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir ?? throw new InvalidOperationException("repo root not found"), "content");
    }

    private static ContentCatalog Catalog(PackageSet set, List<ModIssue> issues)
    {
        Assert.True(set.Ok, string.Join("; ", set.Errors));
        return ContentCatalog.Build(set, issues);
    }

    /// <summary>Мод <c>svc_mod</c> поверх <c>base</c> с одним файлом <c>services.json</c>.</summary>
    private (ContentCatalog Cat, List<ModIssue> Issues) Mod(string servicesJson)
    {
        string dir = Path.Combine(_tmp, "svc_mod");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """
            { "format_version": 1, "id": "svc_mod", "version": "1.0.0", "content_format": 1,
              "requires": [{ "id": "base", "version": "^1.0.0" }], "content": { "services": ["services.json"] } }
            """);
        File.WriteAllText(Path.Combine(dir, "services.json"), servicesJson);
        PackageSet set = PackageLoader.Load([new PackageSearchDir(ContentDir(), true), new PackageSearchDir(_tmp, false)], ["svc_mod"]);
        var issues = new List<ModIssue>();
        return (Catalog(set, issues), issues);
    }

    private static string One(string record) => "{ \"format_version\": 1, \"services\": [" + record + "] }";

    private const string Level = "\"levels\": [{ \"type\": \"base:building/utility_depot\", \"role\": [\"repair\"], \"dispatch\": { \"vehicle\": \"base:vehicle/utility\", \"units\": 4 } }]";

    [Fact]
    public void BasePackage_UtilityRecord_EqualsCoreBuiltIn()
    {
        PackageSet set = PackageLoader.Load([new PackageSearchDir(ContentDir(), true)], ["base"]);
        var issues = new List<ModIssue>();
        ContentCatalog cat = Catalog(set, issues);
        Assert.Empty(issues);
        ServiceEntry e = Assert.Single(cat.Services);
        Assert.Equal("base:service/utility", e.Id.ToString());
        Assert.Equal(0, cat.FindService(e.Id));
        var d = CityServicesDesc.Default();
        cat.FillServices(null, ref d);
        int depot = cat.FindBuilding(new AssetId("base", AssetKind.Building, "utility_depot"));
        CityServicesDesc.BaseRecords(depot, cat.Buildings[depot].Desc.VehicleType, out ServiceRecordDesc[] rec, out ServiceLevelDesc[] lv, out ServiceCallDesc[] calls);
        Assert.Equal(rec, d.Records);
        Assert.Equal(lv, d.Levels);
        Assert.Equal(calls, d.Calls);
        Assert.Empty(d.Upgrades);
        Assert.Empty(d.Visits);
    }

    [Fact]
    public void Mod_FullRecord_FlattensRangesAndUpgrades()
    {
        (ContentCatalog cat, List<ModIssue> issues) = Mod(One("""
            { "id": "rescue", "fund": "city", "title": "Rescue",
              "calls": [{ "kind": "critical", "urgent": true, "on_call": true, "on_call_return": true, "onsite_s": 120, "deadline_s": 600, "per_call": 150 }],
              "visits": [{ "reason": "light", "min_role": "light", "per_visit": 10, "max_cost_s": 1200 }],
              "levels": [
                { "type": "base:building/utility_depot", "role": ["light"], "lot_min": [2, 3], "build_cost": 100, "upkeep_day": 5,
                  "visit": { "capacity_day": 60, "open_slot": 96, "close_slot": 216, "stay_slots": 6 },
                  "upgrades": [{ "id": "room", "cost": 50, "effect": "visit_capacity", "value": 30 }] },
                { "type": "base:building/utility_depot", "role": ["light", "critical"], "milestone": 2,
                  "dispatch": { "vehicle": "base:vehicle/utility", "units": 4, "per_unit_day": 300 },
                  "upgrades": [{ "id": "bay", "cost": 70, "effect": "units", "value": 2 }, { "id": "icu", "cost": 90, "effect": "icu", "value": 5 }] }] }
            """));
        Assert.Empty(issues);
        Assert.Equal(2, cat.Services.Count);   // base:service/utility + svc_mod:service/rescue
        var d = CityServicesDesc.Default();
        cat.FillServices(null, ref d);
        Assert.Equal(2, d.Records.Length);
        ServiceRecordDesc r = d.Records[1];
        Assert.Equal((ServiceFund.City, 1, 2, 1, 1, 0, 1), (r.Fund, r.FirstLevel, r.LevelCount, r.FirstCall, r.CallCount, r.FirstVisit, r.VisitCount));
        Assert.Equal((2, 3, 0, 1), (d.Levels[1].LotMinF, d.Levels[1].LotMinD, d.Levels[1].FirstUpgrade, d.Levels[1].UpgradeCount));
        Assert.Equal((1, 2, (short)-1), (d.Levels[2].FirstUpgrade, d.Levels[2].UpgradeCount, d.Levels[1].DispatchVehicle));
        Assert.Equal(ServiceUpgradeEffect.Icu, d.Upgrades[2].Effect);
        Assert.Equal(ServiceCallFlags.Urgent | ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn, d.Calls[1].Flags);

        // services_off: выключенная запись не попадает в таблицы
        var d2 = CityServicesDesc.Default();
        cat.FillServices(new HashSet<AssetId> { new("base", AssetKind.Service, "utility") }, ref d2);
        Assert.Single(d2.Records);
        Assert.Equal(0, d2.Records[0].FirstLevel);
        Assert.Equal(0, d2.Levels[1].FirstUpgrade - 1);
    }

    [Theory]
    [InlineData("{ \"id\": \"x\", \"fund\": \"utility\", \"calls\": [{ \"kind\": \"repair\", \"on_call\": true, \"onsite_s\": 10 }], " + Level + " }", ModError.BadValue, "on_call")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"calls\": [{ \"kind\": \"fire\", \"onsite_s\": 100, \"deadline_s\": 50 }], " + Level + " }", ModError.BadValue, "deadline_s")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"levels\": [{ \"type\": \"base:building/r_low\", \"role\": [\"fire\"] }] }", ModError.BadValue, "type")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"levels\": [{ \"type\": \"base:building/nope\", \"role\": [\"fire\"] }] }", ModError.BadValue, "type")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"levels\": [{ \"type\": \"base:building/utility_depot\", \"role\": [\"fire\"], \"dispatch\": { \"vehicle\": \"base:vehicle/car\", \"units\": 1 } }] }", ModError.BadValue, "vehicle")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"levels\": [{ \"type\": \"base:building/utility_depot\", \"role\": [\"light\", \"critical\"] }, { \"type\": \"base:building/utility_depot\", \"role\": [\"light\"] }] }", ModError.BadValue, "role")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"levels\": [] }", ModError.OutOfRange, "levels")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"levels\": [{ \"type\": \"base:building/utility_depot\", \"role\": [\"heal\"] }] }", ModError.BadValue, "role")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"bank\", " + Level + " }", ModError.BadValue, "fund")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"extra\": 1, " + Level + " }", ModError.UnknownKey, "extra")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"levels\": [{ \"type\": \"base:building/utility_depot\", \"role\": [\"fire\"], \"dispatch\": { \"vehicle\": \"base:vehicle/utility\", \"units\": 65 } }] }", ModError.OutOfRange, "units")]
    [InlineData("{ \"id\": \"x\", \"fund\": \"city\", \"calls\": [{ \"kind\": \"fire\", \"onsite_s\": 1 }, { \"kind\": \"fire\", \"onsite_s\": 2 }], " + Level + " }", ModError.BadValue, "kind")]
    public void BadRecord_IsRejected(string record, ModError code, string pathPart)
    {
        (ContentCatalog cat, List<ModIssue> issues) = Mod(One(record));
        ModIssue i = Assert.Single(issues);
        Assert.Equal(code, i.Code);
        Assert.Contains(pathPart, i.JsonPath, StringComparison.Ordinal);
        Assert.Single(cat.Services);   // только base:service/utility
    }

    [Fact]
    public void DuplicateId_AndLimit()
    {
        (_, List<ModIssue> dup) = Mod("{ \"format_version\": 1, \"services\": [{ \"id\": \"a\", \"fund\": \"city\", " + Level + " }, { \"id\": \"a\", \"fund\": \"city\", " + Level + " }] }");
        Assert.Contains(dup, i => i.Code == ModError.DuplicateId);

        var recs = new List<string>();
        for (int k = 0; k < ModLimits.MaxServices; k++)
        {
            recs.Add("{ \"id\": \"s" + k + "\", \"fund\": \"city\", " + Level + " }");
        }

        (ContentCatalog cat, List<ModIssue> lim) = Mod("{ \"format_version\": 1, \"services\": [" + string.Join(",", recs) + "] }");
        Assert.Equal(ModLimits.MaxServices + 1, cat.Services.Count);
        Assert.Contains(lim, i => i.Code == ModError.CapacityExceeded);
    }

    [Fact]
    public void UnsupportedFormatVersion()
    {
        (_, List<ModIssue> issues) = Mod("{ \"format_version\": 2, \"services\": [] }");
        Assert.Contains(issues, i => i.Code == ModError.UnsupportedFormatVersion);
    }
}
