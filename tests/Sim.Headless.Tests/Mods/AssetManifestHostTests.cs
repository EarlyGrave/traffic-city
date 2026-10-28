#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using System.IO;
using System.Linq;
using System.Text;
using Sim.Core.Api;
using Sim.Headless.Bench;
using Sim.Scenarios;
using Sim.Scenarios.Mods;
using CoreKind = Sim.Core.Api.AssetKind;

namespace Sim.Headless.Tests.Mods;

/// <summary>
/// Манифест из каталога, отпечаток контента и <see cref="AssetNotice"/> (modding.md §5.3, §6.1; M3): <c>MANI</c> s1k-v3, golden отпечатка,
/// алиас наследия <c>vehicle:car</c>, снятый пакет (<c>AssetsMissing &gt; 0</c>, продолжение побитово), <c>compare</c> при разном отпечатке.
/// </summary>
public sealed class AssetManifestHostTests : IDisposable
{
    /// <summary>Отпечаток <c>base</c> v1.0.0 (s1k-v3): если контент <c>base</c> менялся намеренно, обновить вместе с описанием коммита.</summary>
    private const string BaseFingerprint = "c3ba046c5562f937";   // K1 (ADR-0026): в отпечаток base вошли поля парковки зданий

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tc-mani-" + Guid.NewGuid().ToString("N"));

    public AssetManifestHostTests() => Directory.CreateDirectory(_tmp);

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

    private static PackageSearchDir[] GameDirs() => [new PackageSearchDir(Path.Combine(Root(), "content"), true)];

    private PackageSearchDir[] AllDirs() => [.. GameDirs(), new PackageSearchDir(_tmp, false)];

    private static string Text(ReadOnlySpan<byte> ids, in AssetRef r) => Encoding.UTF8.GetString(ids.Slice((int)r.IdOffset, (int)r.IdLength));

    private static LoadedScenario S1kV3() => ScenarioLoader.LoadInfo(Path.Combine(Root(), "scenarios", "s1k-v3.json"));

    private static string Van(string maxSpeed = "110") =>
        "{ \"name\": \"van\", \"category\": \"car\", \"length_m\": 5.5, \"width_m\": 2.0, \"max_speed_kmh\": " + maxSpeed + ", \"speed_factor\": 1.0, "
        + "\"idm\": { \"a\": 0.9, \"b\": 1.5, \"T\": 1.3, \"s0\": 2.0, \"delta\": 4 }, "
        + "\"mobil\": { \"politeness\": 0.3, \"threshold\": 0.2, \"b_safe\": 4.0, \"right_bias\": 0.3, \"duration_s\": 2.0 } }";

    private void WritePkg(string id, string version, string van)
    {
        string dir = Path.Combine(_tmp, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "package.json"),
            $"{{ \"format_version\": 1, \"id\": \"{id}\", \"version\": \"{version}\", \"content_format\": 1, "
            + "\"requires\": [{ \"id\": \"base\", \"version\": \"^1.0.0\" }], \"content\": { \"vehicles\": [\"vehicles.json\"] } }");
        File.WriteAllText(Path.Combine(dir, "vehicles.json"), "{ \"format_version\": 2, \"types\": [" + van + "] }");
    }

    private LoadedScenario ModScenario()
    {
        string text = File.ReadAllText(Path.Combine(Root(), "scenarios", "s1k-v3.json"))
            .Replace("\"packages\": [\"base\"]", "\"packages\": [\"base\", \"transit_plus\"]", StringComparison.Ordinal)
            .Replace("\"base:vehicle/car\": 1.0", "\"base:vehicle/car\": 0.7, \"transit_plus:vehicle/van\": 0.3", StringComparison.Ordinal);
        Assert.Contains("transit_plus:vehicle/van", text, StringComparison.Ordinal);
        string dir = Path.Combine(Root(), "scenarios");
        return ScenarioLoader.Parse(text, rel => File.ReadAllText(Path.Combine(dir, rel)), "s1k-mod", AllDirs());
    }

    private static byte[] SaveWithManifest(LoadedScenario sc, int ticks)
    {
        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions());
        if (sc.Catalog != null && sc.Packages != null)
        {
            AssetManifestBuilder.ForCatalog(sc.Catalog, sc.Packages, out AssetRef[] refs, out byte[] ids);
            Assert.True(sim.SetAssetManifest(refs, ids));
        }
        else
        {
            AssetManifestBuilder.ForVehicleTypes(sc.TypeNames, sc.Desc.VehicleTypes, out AssetRef[] refs, out byte[] ids);
            Assert.True(sim.SetAssetManifest(refs, ids));
        }

        sim.Step(ticks);
        byte[] buf = new byte[sim.MeasureSave()];
        Assert.Equal(SaveStatus.Ok, sim.Save(buf, out long written));
        Assert.Equal(buf.Length, written);
        return buf;
    }

    // ------------------------------------------------------------ MANI и отпечаток

    [Fact]
    public void ForCatalog_S1kV3_ListsVehiclesThenPackages()
    {
        LoadedScenario sc = S1kV3();
        AssetManifestBuilder.ForCatalog(sc.Catalog!, sc.Packages!, out AssetRef[] refs, out byte[] ids);
        string[] got = refs.Select(r => $"{r.Kind}:{r.SavedIndex}:{Text(ids, r)}:v{r.Version}").ToArray();
        Assert.Equal(
            new[] { "1:0:base:vehicle/car:v1", "1:1:base:vehicle/bus:v1", $"4:-1:base:v{ContentFingerprint.PackVersion(new SemVer(1, 0, 0), "base")}" },
            got);
        Assert.Equal(ContentFingerprint.PackageHash("base", sc.Catalog!), refs[2].ContentHash);
        Assert.Equal(AssetManifestBuilder.ParamsHash(sc.Desc.VehicleTypes[0]), refs[0].ContentHash);
    }

    [Fact]
    public void Fingerprint_S1kV3_IsStable_AndV2IsEmpty()
    {
        Assert.Equal(BaseFingerprint, ContentFingerprint.Hex(ContentFingerprint.For(S1kV3())));
        Assert.Equal(ContentFingerprint.Hex(ContentFingerprint.Empty), ContentFingerprint.Hex(ContentFingerprint.For(
            ScenarioLoader.LoadInfo(Path.Combine(Root(), "scenarios", "s1k.json")))));
        Assert.Equal("ef46db3751d8e999", ContentFingerprint.Hex(ContentFingerprint.Empty));
    }

    [Fact]
    public void Fingerprint_ChangesWithPackageContentAndVersion_NotWithModelOrRev()
    {
        WritePkg("transit_plus", "1.0.0", Van());
        ulong a = ContentFingerprint.For(ModScenario());
        Assert.NotEqual(ContentFingerprint.For(S1kV3()), a);
        Assert.Equal(a, ContentFingerprint.For(ModScenario()));
        WritePkg("transit_plus", "1.0.0", Van("111"));
        Assert.NotEqual(a, ContentFingerprint.For(ModScenario()));
        WritePkg("transit_plus", "1.0.1", Van());
        Assert.NotEqual(a, ContentFingerprint.For(ModScenario()));
    }

    // ------------------------------------------------------------ сейв: алиас наследия, снятый пакет

    [Fact]
    public void LegacyVehicleAlias_ResolvesAgainstBase_NoNotices()
    {
        byte[] save = SaveWithManifest(ScenarioLoader.LoadInfo(Path.Combine(Root(), "scenarios", "s1k.json")), 100);
        LoadStatus st = SaveAssets.Load(save, new LoadOptions { StrictHash = 1 }, new SimRuntimeOptions(), GameDirs(), out Simulation? sim, out LoadReport rep, out SaveAssetsResult res);
        using (sim)
        {
            Assert.Equal(LoadStatus.Ok, st);
            Assert.Equal(2, rep.AssetsInManifest);
            Assert.Equal((0, 0), (rep.AssetsMissing, rep.AssetsChanged));
            Assert.Empty(res.Notices);
            Assert.NotNull(res.Catalog);
        }
    }

    [Fact]
    public void RemovedPackage_NoticesMissing_ContinuesBitIdentically()
    {
        WritePkg("transit_plus", "1.0.0", Van());
        byte[] save = SaveWithManifest(ModScenario(), 400);

        LoadStatus stA = SaveAssets.Load(save, new LoadOptions { StrictHash = 1 }, new SimRuntimeOptions(), AllDirs(), out Simulation? withMod, out LoadReport repA, out SaveAssetsResult resA);
        Assert.Equal(LoadStatus.Ok, stA);
        using (withMod)
        {
            Assert.Equal(0, repA.AssetsMissing);
            Assert.Empty(resA.Notices);
            withMod!.Step(200);
            ulong expected = withMod.ComputeStateHash();

            LoadStatus stB = SaveAssets.Load(save, new LoadOptions { StrictHash = 1 }, new SimRuntimeOptions(), GameDirs(), out Simulation? without, out LoadReport repB, out SaveAssetsResult resB);
            Assert.Equal(LoadStatus.Ok, stB);
            using (without)
            {
                Assert.True(repB.AssetsMissing > 0);
                Assert.Equal(2, repB.AssetsMissing);   // тип ТС van и сам пакет
                Assert.Equal(2, resB.Missing);
                AssetNotice van = resB.Notices.Single(n => n.Kind == CoreKind.VehicleType);
                Assert.Equal("transit_plus:vehicle/van", van.Id);
                Assert.Equal(AssetNoticeState.Missing, van.State);
                Assert.True(van.Count > 0, "placeholder count must be the live vans");
                Assert.Contains(resB.Notices, n => n is { Kind: CoreKind.Package, Id: "transit_plus", State: AssetNoticeState.Missing });
                Assert.Equal((byte)AssetState.Missing, without!.AssetStates[2]);

                without.Step(200);
                Assert.Equal(expected, without.ComputeStateHash());
            }
        }
    }

    [Fact]
    public void ChangedPackage_NoticesChanged_ContinuesWithSavedParams()
    {
        WritePkg("transit_plus", "1.0.0", Van());
        byte[] save = SaveWithManifest(ModScenario(), 200);
        WritePkg("transit_plus", "2.0.0", Van("111"));
        LoadStatus st = SaveAssets.Load(save, new LoadOptions { StrictHash = 1 }, new SimRuntimeOptions(), AllDirs(), out Simulation? sim, out _, out SaveAssetsResult res);
        using (sim)
        {
            Assert.Equal(LoadStatus.Ok, st);
            Assert.Equal(0, res.Missing);
            Assert.Equal(2, res.Changed);   // тип van (новая max_speed) и пакет (другой ContentHash)
            Assert.Contains(res.Notices, n => n is { Kind: CoreKind.Package, State: AssetNoticeState.VersionChanged });
        }
    }

    [Fact]
    public void NewCityWithAbsentPackage_IsLoadError()
    {
        string text = File.ReadAllText(Path.Combine(Root(), "scenarios", "s1k-v3.json"))
            .Replace("\"packages\": [\"base\"]", "\"packages\": [\"base\", \"transit_plus\"]", StringComparison.Ordinal);
        ScenarioException e = Assert.Throws<ScenarioException>(
            () => ScenarioLoader.Parse(text, rel => File.ReadAllText(Path.Combine(Root(), "scenarios", rel)), "t", GameDirs()));
        Assert.Equal(ModError.MissingDependency, e.ModCode);
    }

    // ------------------------------------------------------------ Headless: compare и --packages-dir

    private string WriteResult(string name, string? fingerprint)
    {
        string path = Path.Combine(_tmp, name);
        string fp = fingerprint == null ? string.Empty : $"\"content_fingerprint\":\"{fingerprint}\",";
        File.WriteAllText(path, "{" + fp + "\"scenario\":\"s1k\",\"perf\":{\"tick_ms\":{\"p95\":1.0},\"alloc_bytes_per_tick\":0}}");
        return path;
    }

    private static int Compare(string result, string baseline, out string stdout, out string stderr)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CompareCommand.Run(new[] { "compare", "--result", result, "--baseline", baseline }, o, e);
        stdout = o.ToString();
        stderr = e.ToString();
        return code;
    }

    [Fact]
    public void Compare_DifferentFingerprint_ExitsWithFingerprintMismatch()
    {
        Assert.Equal(CompareCommand.ExitUsage, Compare(WriteResult("r.json", "aaaa"), WriteResult("b.json", "bbbb"), out _, out string err));
        Assert.Contains("FingerprintMismatch", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_SameFingerprintOrBaselineWithout_Compares()
    {
        Assert.Equal(CompareCommand.ExitOk, Compare(WriteResult("r.json", "aaaa"), WriteResult("b.json", "aaaa"), out _, out _));
        Assert.Equal(CompareCommand.ExitOk, Compare(WriteResult("r2.json", "aaaa"), WriteResult("b2.json", null), out _, out _));
    }

    [Fact]
    public void Run_Json_PrintsFingerprint_AndPackagesDirIsUsed()
    {
        WritePkg("transit_plus", "1.0.0", Van());
        string scenario = Path.Combine(_tmp, "mod.json");
        File.WriteAllText(
            scenario,
            File.ReadAllText(Path.Combine(Root(), "scenarios", "s1k-v3.json"))
                .Replace("\"packages\": [\"base\"]", "\"packages\": [\"base\", \"transit_plus\"]", StringComparison.Ordinal));
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(0, RunCommand.Run(new[] { "run", "--scenario", scenario, "--ticks", "20", "--json", "--packages-dir", _tmp }, o, e));
        using var doc = System.Text.Json.JsonDocument.Parse(o.ToString());
        string fp = doc.RootElement.GetProperty("content_fingerprint").GetString()!;
        Assert.Equal(ContentFingerprint.Hex(ContentFingerprint.For(ModScenario())), fp);

        // без --packages-dir мод не найден: понятная ошибка, а не падение
        Assert.Equal(RunCommand.ExitUsage, RunCommand.Run(new[] { "run", "--scenario", scenario, "--ticks", "20" }, new StringWriter(), e));
        Assert.Contains("transit_plus", e.ToString(), StringComparison.Ordinal);
    }
}
