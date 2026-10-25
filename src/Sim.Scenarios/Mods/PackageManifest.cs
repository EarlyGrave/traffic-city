using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sim.Scenarios.Mods;

/// <summary>Зависимость пакета: id и ограничение версии.</summary>
public readonly record struct PackageRequire(string Id, VersionConstraint Version);

/// <summary>Списки файлов контента из <c>package.json</c> (порядок значим, modding.md §5.1).</summary>
public sealed record PackageContent(
    IReadOnlyList<string> Vehicles,
    IReadOnlyList<string> Roads,
    IReadOnlyList<string> Scenarios)
{
    public static PackageContent Empty { get; } = new([], [], []);
}

/// <summary>Разобранный <c>package.json</c> (modding.md §1.2).</summary>
public sealed record PackageManifest(
    string Id,
    SemVer Version,
    int ContentFormat,
    string Title,
    IReadOnlyList<PackageRequire> Requires,
    PackageContent Content)
{
    public const int CurrentFormatVersion = 1;
    public const int CurrentContentFormat = 1;
    public const string FileName = "package.json";

    /// <summary>Id, зарезервированные за игрой: <c>base</c> и префикс <c>tc_</c>.</summary>
    public static bool IsReservedId(string id) => id == "base" || id.StartsWith("tc_", StringComparison.Ordinal);

    /// <summary>
    /// Разбор и проверка. Ошибки дописываются в <paramref name="issues"/>; при любой ошибке результат <c>null</c>.
    /// </summary>
    /// <param name="root">Корень JSON.</param>
    /// <param name="dirName">Имя каталога пакета (должно совпасть с <c>id</c>).</param>
    /// <param name="fromGameDir">Каталог поставляется игрой: только он может объявлять зарезервированные id.</param>
    /// <param name="files">Файлы пакета (относительные пути) — для проверки путей <c>content</c>.</param>
    /// <param name="issues">Накопитель ошибок.</param>
    public static PackageManifest? Parse(
        JsonElement root, string dirName, bool fromGameDir, ISet<string> files, List<ModIssue> issues)
    {
        int before = issues.Count;
        var p = new Parser(dirName, issues);
        if (root.ValueKind != JsonValueKind.Object)
        {
            p.Add("$", ModError.BadType, "expected object");
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!seen.Add(prop.Name))
            {
                p.Add("$." + prop.Name, ModError.BadValue, "duplicate key");
            }
        }

        foreach (string key in seen)
        {
            if (key is not ("format_version" or "id" or "version" or "content_format" or "title" or "requires" or "content"))
            {
                p.Add("$." + key, ModError.UnknownKey, string.Empty);
            }
        }

        if (p.Int(root, "format_version", out int fv))
        {
            if (fv != CurrentFormatVersion)
            {
                p.Add("$.format_version", ModError.UnsupportedFormatVersion, fv.ToString(CultureInfo.InvariantCulture));
            }
        }

        string id = string.Empty;
        if (p.Str(root, "id", out string idText))
        {
            id = idText;
            if (!AssetId.IsValidPackageId(id))
            {
                p.Add("$.id", ModError.BadValue, "expected [a-z][a-z0-9_]{1,31}");
            }
            else if (id != dirName)
            {
                p.Add("$.id", ModError.DirNameMismatch, $"directory '{dirName}', id '{id}'");
            }
            else if (IsReservedId(id) && !fromGameDir)
            {
                p.Add("$.id", ModError.BadValue, "reserved id (base, tc_*) outside the game's content directory");
            }
        }

        SemVer version = default;
        if (p.Str(root, "version", out string vText) && !SemVer.TryParse(vText, out version))
        {
            p.Add("$.version", ModError.BadValue, "expected MAJOR.MINOR.PATCH");
        }

        int contentFormat = 0;
        if (p.Int(root, "content_format", out contentFormat) && contentFormat is < 1 or > CurrentContentFormat)
        {
            p.Add("$.content_format", ModError.UnsupportedFormatVersion, contentFormat.ToString(CultureInfo.InvariantCulture));
        }

        string title = string.Empty;
        if (root.TryGetProperty("title", out JsonElement t))
        {
            if (t.ValueKind != JsonValueKind.String)
            {
                p.Add("$.title", ModError.BadType, "expected string");
            }
            else
            {
                title = t.GetString()!;
                if (Encoding.UTF8.GetByteCount(title) > ModLimits.MaxTitleBytes)
                {
                    p.Add("$.title", ModError.OutOfRange, $"longer than {ModLimits.MaxTitleBytes} bytes");
                }
            }
        }

        var requires = new List<PackageRequire>();
        if (root.TryGetProperty("requires", out JsonElement req))
        {
            ParseRequires(p, req, requires);
        }

        PackageContent content = PackageContent.Empty;
        if (root.TryGetProperty("content", out JsonElement c))
        {
            content = ParseContent(p, c, files);
        }

        return issues.Count > before ? null : new PackageManifest(id, version, contentFormat, title, requires, content);
    }

    private static void ParseRequires(Parser p, JsonElement req, List<PackageRequire> result)
    {
        if (req.ValueKind != JsonValueKind.Array)
        {
            p.Add("$.requires", ModError.BadType, "expected array");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement r in req.EnumerateArray())
        {
            string path = $"$.requires[{i++.ToString(CultureInfo.InvariantCulture)}]";
            if (r.ValueKind != JsonValueKind.Object)
            {
                p.Add(path, ModError.BadType, "expected object");
                continue;
            }

            foreach (JsonProperty prop in r.EnumerateObject())
            {
                if (prop.Name is not ("id" or "version"))
                {
                    p.Add(path + "." + prop.Name, ModError.UnknownKey, string.Empty);
                }
            }

            bool okId = p.Str(r, "id", out string rid, path);
            if (okId && !AssetId.IsValidPackageId(rid))
            {
                p.Add(path + ".id", ModError.BadValue, "expected [a-z][a-z0-9_]{1,31}");
                okId = false;
            }
            else if (okId && !ids.Add(rid))
            {
                p.Add(path + ".id", ModError.BadValue, "duplicate dependency");
                okId = false;
            }

            if (!p.Str(r, "version", out string rv, path))
            {
                continue;
            }

            if (!VersionConstraint.TryParse(rv, out VersionConstraint vc))
            {
                p.Add(path + ".version", ModError.BadValue, "expected X.Y.Z or ^X.Y.Z");
            }
            else if (okId)
            {
                result.Add(new PackageRequire(rid, vc));
            }
        }
    }

    private static PackageContent ParseContent(Parser p, JsonElement c, ISet<string> files)
    {
        if (c.ValueKind != JsonValueKind.Object)
        {
            p.Add("$.content", ModError.BadType, "expected object");
            return PackageContent.Empty;
        }

        List<string> vehicles = [], roads = [], scenarios = [];
        foreach (JsonProperty prop in c.EnumerateObject())
        {
            string path = "$.content." + prop.Name;
            switch (prop.Name)
            {
                case "vehicles":
                    ParsePaths(p, prop.Value, path, files, vehicles);
                    break;
                case "roads":
                    ParsePaths(p, prop.Value, path, files, roads);
                    break;
                case "scenarios":
                    ParsePaths(p, prop.Value, path, files, scenarios);
                    break;
                case "buildings":
                case "props":
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                    {
                        p.Add(path, ModError.BadType, "expected array");
                    }
                    else if (prop.Value.GetArrayLength() > 0)
                    {
                        p.Add(path, ModError.UnsupportedContent, "reserved until ADR-0011");
                    }

                    break;
                default:
                    p.Add(path, ModError.UnknownKey, string.Empty);
                    break;
            }
        }

        return new PackageContent(vehicles, roads, scenarios);
    }

    private static void ParsePaths(Parser p, JsonElement arr, string path, ISet<string> files, List<string> result)
    {
        if (arr.ValueKind != JsonValueKind.Array)
        {
            p.Add(path, ModError.BadType, "expected array");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            string ep = $"{path}[{i++.ToString(CultureInfo.InvariantCulture)}]";
            if (e.ValueKind != JsonValueKind.String)
            {
                p.Add(ep, ModError.BadType, "expected string");
                continue;
            }

            string file = e.GetString()!;
            if (!SafePath.IsSafeRelative(file))
            {
                p.Add(ep, ModError.PathEscape, "path must be relative, '/'-separated, without '..' or leading '.'");
            }
            else if (!file.EndsWith(".json", StringComparison.Ordinal))
            {
                p.Add(ep, ModError.BadValue, "content files must be .json");
            }
            else if (!files.Contains(file))
            {
                p.Add(ep, ModError.BadValue, "file not found in package");
            }
            else if (!seen.Add(file))
            {
                p.Add(ep, ModError.BadValue, "duplicate file");
            }
            else
            {
                result.Add(file);
            }
        }
    }

    private readonly struct Parser(string package, List<ModIssue> issues)
    {
        public void Add(string jsonPath, ModError code, string detail) =>
            issues.Add(new ModIssue(package, FileName, jsonPath, code, detail));

        public bool Str(JsonElement obj, string key, out string value, string prefix = "$")
        {
            value = string.Empty;
            if (!obj.TryGetProperty(key, out JsonElement e))
            {
                Add($"{prefix}.{key}", ModError.MissingKey, string.Empty);
                return false;
            }

            if (e.ValueKind != JsonValueKind.String)
            {
                Add($"{prefix}.{key}", ModError.BadType, "expected string");
                return false;
            }

            value = e.GetString()!;
            return true;
        }

        public bool Int(JsonElement obj, string key, out int value)
        {
            value = 0;
            if (!obj.TryGetProperty(key, out JsonElement e))
            {
                Add("$." + key, ModError.MissingKey, string.Empty);
                return false;
            }

            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out value))
            {
                Add("$." + key, ModError.BadType, "expected integer");
                return false;
            }

            return true;
        }
    }
}
