using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

namespace Sim.Headless.Tests.Mods;

/// <summary>Загрузчик пакетов (modding.md §1, §2, §5.1, §7, §9; M1): MD1, MD4, MD7.</summary>
public sealed class PackageLoaderTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tc-mods-" + Guid.NewGuid().ToString("N"));

    public PackageLoaderTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, true);
        }
        catch (IOException)
        {
            // временный каталог — не критично
        }
    }

    private static string Fixtures()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "traffic-city.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir ?? throw new InvalidOperationException("repo root not found"), "tests", "ModFixtures");
    }

    private static PackageSet Load(string fixture) =>
        PackageLoader.Load([new PackageSearchDir(Fixtures(), false)], [fixture]);

    private static void AssertFails(PackageSet set, ModError code, string? path = null)
    {
        Assert.False(set.Ok);
        Assert.Empty(set.Packages);
        Assert.Contains(set.Errors, i => i.Code == code && (path == null || i.JsonPath == path));
    }

    // ---------- успешные пакеты ----------

    [Fact]
    public void Ok_Alpha_ParsesManifest_AndIgnoresDocs()
    {
        PackageSet set = Load("ok_alpha");
        Assert.True(set.Ok, string.Join("; ", set.Errors));
        LoadedPackage p = Assert.Single(set.Packages);
        Assert.Equal("ok_alpha", p.Id);
        Assert.Equal(new SemVer(1, 2, 0), p.Manifest.Version);
        Assert.Equal("Alpha", p.Manifest.Title);
        Assert.Equal(["vehicles.json"], p.Manifest.Content.Vehicles);
        Assert.Equal(["roads.json"], p.Manifest.Content.Roads);
        string[] paths = [.. p.Files.Select(f => f.Path)];
        Assert.Contains("models/car.gltf", paths);
        Assert.DoesNotContain("README.md", paths);   // документация игнорируется
        Assert.Equal(paths.OrderBy(x => x, StringComparer.Ordinal), paths);   // обход упорядочен
    }

    [Fact]
    public void Ok_Chain_LoadsDependenciesTransitively_InKahnOrder()
    {
        PackageSet set = Load("ok_zeta");
        Assert.True(set.Ok, string.Join("; ", set.Errors));
        Assert.Equal(["ok_alpha", "ok_beta", "ok_zeta"], set.Packages.Select(p => p.Id));
        Assert.Equal(2, set.IndexOf("ok_zeta"));
        Assert.Equal(-1, set.IndexOf("nope"));
    }

    [Fact]
    public void Order_IsSmallestIdFirst_AmongReady()
    {
        // zeta → alpha, beta: готовы alpha и beta (alpha меньше), затем zeta
        PackageSet set = PackageLoader.Load([new PackageSearchDir(Fixtures(), false)], ["ok_zeta", "ok_beta", "ok_alpha"]);
        Assert.Equal(["ok_alpha", "ok_beta", "ok_zeta"], set.Packages.Select(p => p.Id));
    }

    [Fact]
    public void ReservedIds_AllowedOnlyInGameDirectory()
    {
        string fx = Fixtures();
        string reserved = Path.Combine(fx, "reserved");
        AssertFails(PackageLoader.Load([new PackageSearchDir(reserved, false)], ["base"]), ModError.BadValue, "$.id");
        AssertFails(PackageLoader.Load([new PackageSearchDir(fx, false)], ["tc_mod"]), ModError.BadValue, "$.id");
        Assert.True(PackageLoader.Load([new PackageSearchDir(reserved, true)], ["base"]).Ok);
        Assert.True(PackageLoader.Load([new PackageSearchDir(fx, true)], ["tc_mod"]).Ok);
    }

    // ---------- MD4: порядок не зависит от ФС ----------

    [Fact]
    public void Md4_DifferentSearchDirOrderAndMtimes_GiveSameCatalogOrder()
    {
        string a = Path.Combine(_tmp, "a");
        string b = Path.Combine(_tmp, "b");
        Copy(Path.Combine(Fixtures(), "ok_alpha"), Path.Combine(a, "ok_alpha"));
        Copy(Path.Combine(Fixtures(), "ok_zeta"), Path.Combine(a, "ok_zeta"));
        Copy(Path.Combine(Fixtures(), "ok_beta"), Path.Combine(b, "ok_beta"));

        PackageSet one = PackageLoader.Load([new PackageSearchDir(a, false), new PackageSearchDir(b, false)], ["ok_zeta"]);
        var t = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string f in Directory.EnumerateFiles(_tmp, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, t = t.AddSeconds(-97));
        }

        PackageSet two = PackageLoader.Load([new PackageSearchDir(b, false), new PackageSearchDir(a, false)], ["ok_zeta"]);
        Assert.True(one.Ok && two.Ok);
        Assert.Equal(one.Packages.Select(p => p.Id), two.Packages.Select(p => p.Id));
        Assert.Equal(
            one.Packages.SelectMany(p => p.Files.Select(f => p.Id + "/" + f.Path + ":" + f.Length)),
            two.Packages.SelectMany(p => p.Files.Select(f => p.Id + "/" + f.Path + ":" + f.Length)));
    }

    // ---------- коды §9 в области M1 (фикстуры) ----------

    [Theory]
    [InlineData("bad_unknown_key", ModError.UnknownKey, "$.extra")]
    [InlineData("bad_content_key", ModError.UnknownKey, "$.content.sounds")]
    [InlineData("bad_type", ModError.BadType, "$.format_version")]
    [InlineData("bad_semver", ModError.BadValue, "$.version")]
    [InlineData("bad_semver_zero", ModError.BadValue, "$.version")]
    [InlineData("bad_id_pattern", ModError.BadValue, "$.id")]
    [InlineData("bad_constraint", ModError.BadValue, "$.requires[0].version")]
    [InlineData("bad_syntax", ModError.BadValue, "$")]
    [InlineData("bad_missing_file", ModError.BadValue, "$.content.vehicles[0]")]
    [InlineData("bad_not_json_content", ModError.BadValue, "$.content.vehicles[0]")]
    [InlineData("bad_dup_path", ModError.BadValue, "$.content.vehicles[1]")]
    [InlineData("bad_title_long", ModError.OutOfRange, "$.title")]
    [InlineData("bad_missing_id", ModError.MissingKey, "$.id")]
    [InlineData("bad_dirname", ModError.DirNameMismatch, "$.id")]
    [InlineData("bad_missing_dep", ModError.MissingDependency, null)]
    [InlineData("bad_version_conflict", ModError.VersionConflict, "$.requires[0]")]
    [InlineData("bad_version_exact", ModError.VersionConflict, "$.requires[0]")]
    [InlineData("cyc_a", ModError.DependencyCycle, null)]
    [InlineData("bad_future_format", ModError.UnsupportedFormatVersion, "$.format_version")]
    [InlineData("bad_content_format", ModError.UnsupportedFormatVersion, "$.content_format")]
    [InlineData("bad_gd", ModError.FileNotAllowed, null)]
    [InlineData("bad_tscn", ModError.FileNotAllowed, null)]
    [InlineData("bad_dll", ModError.FileNotAllowed, null)]
    [InlineData("bad_no_ext", ModError.FileNotAllowed, null)]
    [InlineData("bad_dotdot", ModError.PathEscape, "$.content.vehicles[0]")]
    [InlineData("bad_absolute", ModError.PathEscape, "$.content.vehicles[0]")]
    [InlineData("bad_backslash", ModError.PathEscape, "$.content.vehicles[0]")]
    [InlineData("bad_dotfile", ModError.PathEscape, null)]
    public void Fixture_ProducesExpectedError(string fixture, ModError code, string? path)
    {
        PackageSet set = Load(fixture);
        AssertFails(set, code, path);
        Assert.All(set.Errors, i => Assert.False(string.IsNullOrEmpty(i.Package) && i.Code != ModError.BadValue));
    }

    [Fact]
    public void Hostile_GdFile_ReportsFileName()
    {
        PackageSet set = Load("bad_gd");
        ModIssue i = Assert.Single(set.Errors);
        Assert.Equal(("bad_gd", "scripts/evil.gd", ModError.FileNotAllowed), (i.Package, i.File, i.Code));
    }

    [Fact]
    public void DuplicatePackage_SameIdInTwoSearchDirs()
    {
        string dup = Path.Combine(Fixtures(), "dup");
        PackageSet set = PackageLoader.Load(
            [new PackageSearchDir(Path.Combine(dup, "a"), false), new PackageSearchDir(Path.Combine(dup, "b"), false)], ["dup_pkg"]);
        AssertFails(set, ModError.DuplicatePackage);
        Assert.True(PackageLoader.Load([new PackageSearchDir(Path.Combine(dup, "a"), false)], ["dup_pkg"]).Ok);
    }

    [Fact]
    public void InvalidRootId_IsBadValue_NotException()
    {
        AssertFails(PackageLoader.Load([new PackageSearchDir(Fixtures(), false)], ["../etc"]), ModError.BadValue);
        AssertFails(PackageLoader.Load([new PackageSearchDir(Fixtures(), false)], ["absent_pkg"]), ModError.MissingDependency);
        AssertFails(PackageLoader.Load([], ["ok_alpha"]), ModError.MissingDependency);
    }

    [Fact]
    public void ScenarioException_WrapsFirstIssue()
    {
        PackageSet set = Load("bad_gd");
        ScenarioException e = Assert.Throws<ScenarioException>(set.ThrowIfFailed);
        Assert.Contains("FileNotAllowed", e.Message, StringComparison.Ordinal);
        Load("ok_beta").ThrowIfFailed();
    }

    [Fact]
    public void Cycle_ReportsAllMembers_AndSelfRequireIsCycle()
    {
        ModIssue i = Assert.Single(Load("cyc_a").Errors);
        Assert.Equal("cyc_a cyc_b", i.Detail);
        WritePkg("self_dep", """{ "format_version":1,"id":"self_dep","version":"1.0.0","content_format":1,"requires":[{"id":"self_dep","version":"1.0.0"}] }""");
        AssertFails(PackageLoader.Load([new PackageSearchDir(_tmp, false)], ["self_dep"]), ModError.DependencyCycle);
    }

    [Fact]
    public void BrokenDependency_FailsWholeSet()
    {
        // ok_zeta требует ok_alpha ^1.0.0; в наборе bad_version_conflict + ok_alpha набор целиком не грузится
        PackageSet set = PackageLoader.Load([new PackageSearchDir(Fixtures(), false)], ["ok_zeta", "bad_gd"]);
        Assert.False(set.Ok);
        Assert.Empty(set.Packages);
    }

    // ---------- генерируемые враждебные пакеты ----------

    private string WritePkg(string id, string manifest, string? sub = null)
    {
        string dir = Path.Combine(_tmp, sub ?? string.Empty, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), manifest);
        return dir;
    }

    private static string Min(string id) =>
        $$"""{ "format_version":1,"id":"{{id}}","version":"1.0.0","content_format":1 }""";

    private PackageSet LoadTmp(string id) => PackageLoader.Load([new PackageSearchDir(_tmp, false)], [id]);

    [Fact]
    public void Hostile_Symlink_File_IsPathEscape()
    {
        string dir = WritePkg("sym_file", Min("sym_file"));
        string target = Path.Combine(_tmp, "outside.json");
        File.WriteAllText(target, "{}");
        File.CreateSymbolicLink(Path.Combine(dir, "link.json"), target);
        AssertFails(LoadTmp("sym_file"), ModError.PathEscape);
    }

    [Fact]
    public void Hostile_Symlink_Directory_IsPathEscape()
    {
        string dir = WritePkg("sym_dir", Min("sym_dir"));
        string outside = Path.Combine(_tmp, "outside_dir");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "x.json"), "{}");
        Directory.CreateSymbolicLink(Path.Combine(dir, "d"), outside);
        AssertFails(LoadTmp("sym_dir"), ModError.PathEscape);
    }

    [Fact]
    public void Hostile_Symlink_PackageDirectory_IsPathEscape()
    {
        string real = WritePkg("real_pkg", Min("sym_pkg"), "elsewhere");
        Directory.CreateSymbolicLink(Path.Combine(_tmp, "sym_pkg"), real);
        AssertFails(LoadTmp("sym_pkg"), ModError.PathEscape);
    }

    [Fact]
    public void Hostile_JsonDepth_32_Ok_33_TooDeep()
    {
        string ok = WritePkg("deep_ok", Min("deep_ok"));
        File.WriteAllText(Path.Combine(ok, "d.json"), Nested(32));
        Assert.True(LoadTmp("deep_ok").Ok);

        string bad = WritePkg("deep_bad", Min("deep_bad"));
        File.WriteAllText(Path.Combine(bad, "d.json"), Nested(33));
        PackageSet set = LoadTmp("deep_bad");
        AssertFails(set, ModError.TooDeep);
        Assert.Equal("d.json", set.Errors[0].File);
    }

    private static string Nested(int depth) => new string('[', depth) + new string(']', depth);

    [Fact]
    public void Hostile_Json_9MiB_IsTooLarge_BeforeReading()
    {
        string dir = WritePkg("big_json", Min("big_json"));
        using (FileStream fs = File.Create(Path.Combine(dir, "big.json")))
        {
            byte[] chunk = Encoding.ASCII.GetBytes("[" + new string(' ', 1024 * 1024 - 1));
            for (int i = 0; i < 9; i++)
            {
                fs.Write(chunk);
            }
        }

        PackageSet set = LoadTmp("big_json");
        AssertFails(set, ModError.TooLarge);
        Assert.Equal("big.json", set.Errors[0].File);
    }

    [Fact]
    public void Limits_JsonString_And_Array()
    {
        string s = WritePkg("long_str", Min("long_str"));
        File.WriteAllText(Path.Combine(s, "s.json"), "{\"a\":\"" + new string('x', 1025) + "\"}");
        AssertFails(LoadTmp("long_str"), ModError.TooLarge, "$.a");

        string s2 = WritePkg("ok_str", Min("ok_str"));
        File.WriteAllText(Path.Combine(s2, "s.json"), "{\"a\":\"" + new string('x', 1024) + "\"}");
        Assert.True(LoadTmp("ok_str").Ok);

        string a = WritePkg("long_arr", Min("long_arr"));
        File.WriteAllText(Path.Combine(a, "a.json"), "[" + string.Join(',', Enumerable.Repeat("0", 65_537)) + "]");
        AssertFails(LoadTmp("long_arr"), ModError.TooLarge, "$");

        string a2 = WritePkg("ok_arr", Min("ok_arr"));
        File.WriteAllText(Path.Combine(a2, "a.json"), "[" + string.Join(',', Enumerable.Repeat("0", 65_536)) + "]");
        Assert.True(LoadTmp("ok_arr").Ok);
    }

    [Fact]
    public void Limits_FileCount_4096_Ok_4097_TooLarge()
    {
        string ok = WritePkg("files_ok", Min("files_ok"));
        for (int i = 0; i < ModLimits.MaxPackageFiles - 1; i++)
        {
            File.WriteAllBytes(Path.Combine(ok, $"f{i}.bin"), []);
        }

        Assert.True(LoadTmp("files_ok").Ok);

        string bad = WritePkg("files_bad", Min("files_bad"));
        for (int i = 0; i < ModLimits.MaxPackageFiles; i++)
        {
            File.WriteAllBytes(Path.Combine(bad, $"f{i}.bin"), []);
        }

        AssertFails(LoadTmp("files_bad"), ModError.TooLarge);
    }

    [Theory]
    [InlineData("tex.png", 64L * 1024 * 1024 + 1)]
    [InlineData("tex.ktx2", 64L * 1024 * 1024 + 1)]
    [InlineData("m.glb", 128L * 1024 * 1024 + 1)]
    [InlineData("m.gltf", 128L * 1024 * 1024 + 1)]
    [InlineData("b.bin", 128L * 1024 * 1024 + 1)]
    public void Limits_PerFileSize_IsTooLarge_FromFileLength(string name, long length)
    {
        string dir = WritePkg("big_file", Min("big_file"));
        using (FileStream fs = File.Create(Path.Combine(dir, name)))
        {
            fs.SetLength(length);   // разреженный файл: содержимое не читается
        }

        PackageSet set = LoadTmp("big_file");
        AssertFails(set, ModError.TooLarge);
        Assert.Equal(name, set.Errors[0].File);
    }

    [Fact]
    public void Limits_PackageSize_512MiB_TooLarge()
    {
        string dir = WritePkg("big_pkg", Min("big_pkg"));
        for (int i = 0; i < 5; i++)
        {
            using FileStream fs = File.Create(Path.Combine(dir, $"p{i}.bin"));
            fs.SetLength(110L * 1024 * 1024);   // 5 × 110 МиБ = 550 МиБ
        }

        AssertFails(LoadTmp("big_pkg"), ModError.TooLarge);
    }

    [Fact]
    public void Limits_PackagesInSet_MoreThan256_TooLarge()
    {
        var reqs = new StringBuilder();
        for (int i = 0; i < 257; i++)
        {
            string id = $"dep_{i:D3}";
            WritePkg(id, Min(id));
            reqs.Append(i == 0 ? string.Empty : ",").Append($$"""{"id":"{{id}}","version":"1.0.0"}""");
        }

        WritePkg(
            "many_deps",
            $$"""{ "format_version":1,"id":"many_deps","version":"1.0.0","content_format":1,"requires":[{{reqs}}] }""");
        AssertFails(LoadTmp("many_deps"), ModError.TooLarge);
    }

    [Fact]
    public void NoException_OnGarbageManifests()
    {
        string[] bodies = ["", "null", "[]", "42", "{", "{\"id\":1}", "\"x\"", "{\"requires\":5,\"content\":[]}", "{\"requires\":[1,{}],\"content\":{\"vehicles\":5}}"];
        for (int i = 0; i < bodies.Length; i++)
        {
            string id = $"junk_{i}";
            WritePkg(id, bodies[i]);
            PackageSet set = LoadTmp(id);
            Assert.False(set.Ok, bodies[i]);
            Assert.NotEmpty(set.Errors);
        }
    }

    [Fact]
    public void MissingManifest_IsMissingKey()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "empty_pkg"));
        AssertFails(LoadTmp("empty_pkg"), ModError.MissingKey);
    }

    // ---------- SemVer / ограничения ----------

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("0.0.0", true)]
    [InlineData("10.20.30", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0.0.0", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0.0-beta", false)]
    [InlineData("1.0.0+b", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("-1.0.0", false)]
    [InlineData("1..0", false)]
    [InlineData("9999999999.0.0", false)]
    [InlineData("", false)]
    public void SemVer_Parse(string text, bool ok) => Assert.Equal(ok, SemVer.TryParse(text, out _));

    [Theory]
    [InlineData("^1.0.0", "1.0.0", true)]
    [InlineData("^1.0.0", "1.9.3", true)]
    [InlineData("^1.2.0", "1.1.9", false)]
    [InlineData("^1.0.0", "2.0.0", false)]
    [InlineData("^1.0.0", "0.9.0", false)]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.1", false)]
    public void VersionConstraint_Satisfies(string constraint, string version, bool expected)
    {
        Assert.True(VersionConstraint.TryParse(constraint, out VersionConstraint c));
        Assert.True(SemVer.TryParse(version, out SemVer v));
        Assert.Equal(expected, c.Satisfies(v));
    }

    [Fact]
    public void SemVer_Compare_And_Format()
    {
        Assert.True(SemVer.TryParse("1.10.0", out SemVer a));
        Assert.True(SemVer.TryParse("1.9.9", out SemVer b));
        Assert.True(a > b);
        Assert.Equal("1.10.0", a.ToString());
        Assert.False(VersionConstraint.TryParse("~1.0.0", out _));
        Assert.False(VersionConstraint.TryParse(null, out _));
    }

    // ---------- id и ссылки ----------

    [Theory]
    [InlineData("base:vehicle/car", true)]
    [InlineData("transit_plus:road/avenue_4", true)]
    [InlineData("base:building/b1", true)]
    [InlineData("base:prop/tree", true)]
    [InlineData("base:truck/car", false)]
    [InlineData("Base:vehicle/car", false)]
    [InlineData("base:vehicle/Car", false)]
    [InlineData("base:vehicle/", false)]
    [InlineData("base:vehicle/a-b", false)]
    [InlineData("vehicle/car", false)]
    [InlineData("base/vehicle:car", false)]
    [InlineData("a:vehicle/car", false)]
    public void AssetId_Parse(string text, bool ok)
    {
        Assert.Equal(ok, AssetId.TryParse(text, out AssetId id));
        if (ok)
        {
            Assert.Equal(text, id.ToString());
        }
    }

    [Fact]
    public void AssetId_Length_Limit_128Bytes()
    {
        string name64 = new('a', 64);
        string pkg32 = new('p', 32);
        Assert.True(AssetId.TryParse($"{pkg32}:vehicle/{name64}", out _));   // 32+1+7+64 = 104
        Assert.False(AssetId.TryParse($"{pkg32}:vehicle/{name64}x", out _));   // имя 65
        Assert.False(AssetId.TryParse($"{new string('p', 33)}:vehicle/a", out _));
    }

    [Fact]
    public void Refs_FullIdNeedsDeclaredDependency_ShortSearchesOwnThenBase()
    {
        string fx = Fixtures();
        WritePkg("base", Min("base"), "game");
        WritePkg("uses_base", Min("uses_base").Replace("\"content_format\":1", "\"content_format\":1,\"requires\":[{\"id\":\"base\",\"version\":\"^1.0.0\"}]", StringComparison.Ordinal));
        string game = Path.Combine(_tmp, "game");
        PackageSet set = PackageLoader.Load(
            [new PackageSearchDir(game, true), new PackageSearchDir(_tmp, false), new PackageSearchDir(fx, false)],
            ["uses_base", "ok_beta"]);
        Assert.True(set.Ok, string.Join("; ", set.Errors));

        var c = new List<AssetId>();
        Assert.True(set.TryExpandRef("vehicle/car", "uses_base", c, out _));
        Assert.Equal(["uses_base:vehicle/car", "base:vehicle/car"], c.Select(x => x.ToString()));

        Assert.True(set.TryExpandRef("base:vehicle/car", "uses_base", c, out _));
        Assert.Equal(["base:vehicle/car"], c.Select(x => x.ToString()));

        Assert.False(set.TryExpandRef("base:vehicle/car", "ok_beta", c, out ModError e));   // ok_beta не требует base
        Assert.Equal(ModError.UndeclaredDependency, e);

        Assert.True(set.TryExpandRef("vehicle/car", "ok_beta", c, out _));
        Assert.Equal(["ok_beta:vehicle/car"], c.Select(x => x.ToString()));   // base не объявлен — в кандидаты не идёт

        Assert.False(set.TryExpandRef("car", "uses_base", c, out e));
        Assert.Equal(ModError.BadValue, e);
    }

    // ---------- SafePath ----------

    [Theory]
    [InlineData("a.json", true)]
    [InlineData("models/car.glb", true)]
    [InlineData("../a.json", false)]
    [InlineData("a/../b.json", false)]
    [InlineData("./a.json", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/.b", false)]
    [InlineData("/abs.json", false)]
    [InlineData("a\\b.json", false)]
    [InlineData("C:/a.json", false)]
    [InlineData("a//b.json", false)]
    [InlineData("a/", false)]
    [InlineData("a\0b", false)]
    [InlineData("", false)]
    public void SafePath_Lexical(string path, bool ok) => Assert.Equal(ok, SafePath.IsSafeRelative(path));

    [Fact]
    public void SafePath_IsInsideRoot_RejectsSymlinkSegments()
    {
        string root = Path.Combine(_tmp, "r");
        Directory.CreateDirectory(Path.Combine(root, "d"));
        File.WriteAllText(Path.Combine(root, "d", "f.json"), "{}");
        Assert.True(SafePath.IsInsideRoot(root, "d/f.json"));
        string outside = Path.Combine(_tmp, "o");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "s"), outside);
        Assert.False(SafePath.IsInsideRoot(root, "s/x.json"));
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }

        foreach (string d in Directory.GetDirectories(from))
        {
            Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
