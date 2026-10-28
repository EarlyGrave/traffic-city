using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Sim.Scenarios.Mods;

/// <summary>Каталог поиска пакетов. <see cref="IsGame"/> — поставляется игрой (<c>content/</c>): только там допустимы <c>base</c> и <c>tc_*</c>.</summary>
public readonly record struct PackageSearchDir(string Path, bool IsGame);

/// <summary>
/// Загрузчик пакетов (modding.md §1, §2, §5.1, §7, §9). Читает только <c>package.json</c> и обходит файлы
/// (белый список расширений, пределы); содержимое контента не разбирает. Наружу не бросает: ошибки — <see cref="ModIssue"/>.
/// Результат не зависит от порядка каталогов поиска, mtime и порядка перечисления файловой системы (MD4).
/// </summary>
public static class PackageLoader
{
    /// <summary>
    /// Найти пакеты <paramref name="roots"/> и их транзитивные <c>requires</c> в каталогах поиска и упорядочить:
    /// топологическая сортировка по Кану, из готовых первым берётся наименьший id (ординально).
    /// </summary>
    public static PackageSet Load(IReadOnlyList<PackageSearchDir> searchDirs, IEnumerable<string> roots)
    {
        var issues = new List<ModIssue>();
        var loaded = new Dictionary<string, LoadedPackage>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string r in roots)
        {
            if (AssetId.IsValidPackageId(r))
            {
                pending.Add(r);
            }
            else
            {
                issues.Add(new ModIssue(string.Empty, string.Empty, "$.packages", ModError.BadValue, $"invalid package id '{r}'"));
            }
        }

        while (pending.Count > 0)
        {
            string id = pending.Min!;
            pending.Remove(id);
            if (loaded.ContainsKey(id) || failed.Contains(id))
            {
                continue;
            }

            if (loaded.Count + failed.Count >= ModLimits.MaxPackagesInSet)
            {
                issues.Add(new ModIssue(id, string.Empty, string.Empty, ModError.TooLarge, $"more than {ModLimits.MaxPackagesInSet} packages in set"));
                break;
            }

            LoadedPackage? pkg = LoadOne(searchDirs, id, issues);
            if (pkg == null)
            {
                failed.Add(id);
                continue;
            }

            loaded[id] = pkg;
            foreach (PackageRequire req in pkg.Manifest.Requires)
            {
                pending.Add(req.Id);
            }
        }

        CheckVersions(loaded, issues);
        List<LoadedPackage> ordered = Order(loaded, issues);
        return issues.Count > 0
            ? new PackageSet([], issues, [])
            : new PackageSet(ordered, [], []);
    }

    private static LoadedPackage? LoadOne(IReadOnlyList<PackageSearchDir> searchDirs, string id, List<ModIssue> issues)
    {
        var found = new List<(string Dir, bool Game)>();
        foreach (PackageSearchDir sd in searchDirs)
        {
            string candidate = System.IO.Path.Combine(sd.Path, id);
            if (Directory.Exists(candidate))
            {
                found.Add((candidate, sd.IsGame));
            }
        }

        if (found.Count == 0)
        {
            issues.Add(new ModIssue(id, string.Empty, "$.requires", ModError.MissingDependency, $"package '{id}' not found"));
            return null;
        }

        if (found.Count > 1)
        {
            issues.Add(new ModIssue(id, string.Empty, string.Empty, ModError.DuplicatePackage, $"found in {found.Count} directories"));
            return null;
        }

        (string dir, bool game) = found[0];
        if (new DirectoryInfo(dir).LinkTarget != null)
        {
            issues.Add(new ModIssue(id, string.Empty, string.Empty, ModError.PathEscape, "package directory is a symlink"));
            return null;
        }

        int before = issues.Count;
        var files = new List<PackageFile>();
        Walk(id, dir, string.Empty, files, issues, new WalkState(), 0);

        var fileSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (PackageFile f in files)
        {
            fileSet.Add(f.Path);
        }

        foreach (PackageFile f in files)
        {
            if (f.Path.EndsWith(".json", StringComparison.Ordinal))
            {
                CheckJsonFile(id, dir, f, issues);
            }
        }

        if (!fileSet.Contains(PackageManifest.FileName))
        {
            if (issues.Count == before)
            {
                issues.Add(new ModIssue(id, PackageManifest.FileName, "$", ModError.MissingKey, "package.json not found"));
            }

            return null;
        }

        return issues.Count > before ? null : BuildPackage(id, dir, game, files, fileSet, issues);
    }

    private static LoadedPackage? BuildPackage(
        string id, string dir, bool game, List<PackageFile> files, HashSet<string> fileSet, List<ModIssue> issues)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = ParseDocument(File.ReadAllBytes(System.IO.Path.Combine(dir, PackageManifest.FileName)));
            root = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            issues.Add(new ModIssue(id, PackageManifest.FileName, "$", ModError.BadValue, e.GetType().Name));
            return null;
        }

        PackageManifest? m = PackageManifest.Parse(root, id, game, fileSet, issues);
        return m == null ? null : new LoadedPackage(m, dir, files);
    }

    private static JsonDocument ParseDocument(byte[] bytes) =>
        JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = ModLimits.MaxJsonDepth });

    private sealed class WalkState
    {
        public int Files;
        public long Bytes;
        public bool Stopped;
    }

    private static void Walk(
        string pkg, string root, string rel, List<PackageFile> files, List<ModIssue> issues, WalkState st, int depth)
    {
        if (depth > 64)
        {
            issues.Add(new ModIssue(pkg, rel, string.Empty, ModError.TooLarge, "directory nesting deeper than 64"));
            st.Stopped = true;
            return;
        }

        var dirInfo = new DirectoryInfo(rel.Length == 0 ? root : System.IO.Path.Combine(root, rel));
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var entries = new List<FileSystemInfo>(dirInfo.EnumerateFileSystemInfos("*", options));
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (FileSystemInfo e in entries)
        {
            if (st.Stopped)
            {
                return;
            }

            string path = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            if (!SafePath.IsSafeRelative(path))
            {
                issues.Add(new ModIssue(pkg, path, string.Empty, ModError.PathEscape, "unsafe file name"));
                continue;
            }

            if (e.LinkTarget != null || e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                issues.Add(new ModIssue(pkg, path, string.Empty, ModError.PathEscape, "symlink"));
                continue;
            }

            if (e is DirectoryInfo)
            {
                Walk(pkg, root, path, files, issues, st, depth + 1);
                continue;
            }

            if (e is not FileInfo fi)
            {
                continue;
            }

            if (++st.Files > ModLimits.MaxPackageFiles)
            {
                issues.Add(new ModIssue(pkg, string.Empty, string.Empty, ModError.TooLarge, $"more than {ModLimits.MaxPackageFiles} files"));
                st.Stopped = true;
                return;
            }

            st.Bytes += fi.Length;
            if (st.Bytes > ModLimits.MaxPackageBytes)
            {
                issues.Add(new ModIssue(pkg, string.Empty, string.Empty, ModError.TooLarge, "package larger than 512 MiB"));
                st.Stopped = true;
                return;
            }

            CheckFile(pkg, path, fi, files, issues);
        }
    }

    private static void CheckFile(string pkg, string path, FileInfo fi, List<PackageFile> files, List<ModIssue> issues)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(ModLimits.AllowedExtensions, ext) >= 0)
        {
            if (fi.Length > ModLimits.MaxBytesFor(ext))
            {
                issues.Add(new ModIssue(pkg, path, string.Empty, ModError.TooLarge, $"{fi.Length} bytes"));
                return;
            }

            files.Add(new PackageFile(path, fi.Length));
        }
        else if (Array.IndexOf(ModLimits.IgnoredExtensions, ext) < 0 && fi.Name != "LICENSE")
        {
            issues.Add(new ModIssue(pkg, path, string.Empty, ModError.FileNotAllowed, ext.Length == 0 ? "no extension" : ext));
        }
    }

    /// <summary>Размер уже проверен при обходе; здесь — синтаксис, глубина, длина строк и массивов.</summary>
    private static void CheckJsonFile(string pkg, string dir, PackageFile f, List<ModIssue> issues)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(System.IO.Path.Combine(dir, f.Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(new ModIssue(pkg, f.Path, "$", ModError.BadValue, e.GetType().Name));
            return;
        }

        try
        {
            using JsonDocument doc = ParseDocument(bytes);
            CheckLimits(pkg, f.Path, doc.RootElement, "$", issues);
        }
        catch (JsonException)
        {
            bool tooDeep = false;
            try
            {
                using JsonDocument deep = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4096 });
                tooDeep = true;
            }
            catch (JsonException)
            {
                // синтаксическая ошибка, а не глубина
            }

            issues.Add(tooDeep
                ? new ModIssue(pkg, f.Path, "$", ModError.TooDeep, $"nesting deeper than {ModLimits.MaxJsonDepth}")
                : new ModIssue(pkg, f.Path, "$", ModError.BadValue, "invalid JSON"));
        }
    }

    private static bool CheckLimits(string pkg, string file, JsonElement e, string path, List<ModIssue> issues)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                if (Encoding.UTF8.GetByteCount(e.GetString()!) > ModLimits.MaxJsonStringBytes)
                {
                    issues.Add(new ModIssue(pkg, file, path, ModError.TooLarge, $"string longer than {ModLimits.MaxJsonStringBytes} bytes"));
                    return false;
                }

                return true;
            case JsonValueKind.Array:
                if (e.GetArrayLength() > ModLimits.MaxJsonArrayLength)
                {
                    issues.Add(new ModIssue(pkg, file, path, ModError.TooLarge, $"array longer than {ModLimits.MaxJsonArrayLength}"));
                    return false;
                }

                int i = 0;
                foreach (JsonElement x in e.EnumerateArray())
                {
                    if (!CheckLimits(pkg, file, x, $"{path}[{i++}]", issues))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    if (Encoding.UTF8.GetByteCount(p.Name) > ModLimits.MaxJsonStringBytes)
                    {
                        issues.Add(new ModIssue(pkg, file, path, ModError.TooLarge, "key too long"));
                        return false;
                    }

                    if (!CheckLimits(pkg, file, p.Value, path + "." + p.Name, issues))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private static void CheckVersions(Dictionary<string, LoadedPackage> loaded, List<ModIssue> issues)
    {
        foreach (string id in SortedKeys(loaded))
        {
            PackageManifest m = loaded[id].Manifest;
            for (int i = 0; i < m.Requires.Count; i++)
            {
                PackageRequire r = m.Requires[i];
                if (loaded.TryGetValue(r.Id, out LoadedPackage? dep) && !r.Version.Satisfies(dep.Manifest.Version))
                {
                    issues.Add(new ModIssue(
                        id, PackageManifest.FileName, $"$.requires[{i}]", ModError.VersionConflict,
                        $"{r.Id} {r.Version} required, {dep.Manifest.Version} installed"));
                }
            }
        }
    }

    private static List<LoadedPackage> Order(Dictionary<string, LoadedPackage> loaded, List<ModIssue> issues)
    {
        var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string id, LoadedPackage p) in loaded)
        {
            indegree.TryAdd(id, 0);
            foreach (PackageRequire r in p.Manifest.Requires)
            {
                if (loaded.ContainsKey(r.Id))
                {
                    indegree[id]++;
                    if (!dependents.TryGetValue(r.Id, out List<string>? list))
                    {
                        dependents[r.Id] = list = [];
                    }

                    list.Add(id);
                }
            }
        }

        var ready = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string id, int d) in indegree)
        {
            if (d == 0)
            {
                ready.Add(id);
            }
        }

        var result = new List<LoadedPackage>(loaded.Count);
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            result.Add(loaded[id]);
            if (dependents.TryGetValue(id, out List<string>? deps))
            {
                foreach (string d in deps)
                {
                    if (--indegree[d] == 0)
                    {
                        ready.Add(d);
                    }
                }
            }
        }

        if (result.Count < loaded.Count)
        {
            var cyc = new List<string>();
            foreach (string id in SortedKeys(loaded))
            {
                if (indegree[id] > 0)
                {
                    cyc.Add(id);
                }
            }

            issues.Add(new ModIssue(cyc[0], PackageManifest.FileName, "$.requires", ModError.DependencyCycle, string.Join(" ", cyc)));
        }

        return result;
    }

    private static List<string> SortedKeys(Dictionary<string, LoadedPackage> d)
    {
        var keys = new List<string>(d.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}
