using System;
using System.Collections.Generic;

namespace Sim.Scenarios.Mods;

/// <summary>Файл пакета: относительный путь (с <c>/</c>) и размер.</summary>
public readonly record struct PackageFile(string Path, long Length);

/// <summary>Загруженный пакет: манифест, каталог на диске, файлы в порядке обхода (ординальная сортировка).</summary>
public sealed record LoadedPackage(PackageManifest Manifest, string Directory, IReadOnlyList<PackageFile> Files)
{
    public string Id => Manifest.Id;
}

/// <summary>
/// Результат <see cref="PackageLoader.Load"/>: пакеты в порядке загрузки (modding.md §5.1) либо список ошибок.
/// При любой ошибке <see cref="Packages"/> пуст — набор грузится целиком или не грузится (MD7).
/// </summary>
public sealed class PackageSet
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    internal PackageSet(IReadOnlyList<LoadedPackage> packages, IReadOnlyList<ModIssue> errors, IReadOnlyList<ModIssue> warnings)
    {
        Packages = packages;
        Errors = errors;
        Warnings = warnings;
        for (int i = 0; i < packages.Count; i++)
        {
            _index[packages[i].Id] = i;
        }
    }

    public IReadOnlyList<LoadedPackage> Packages { get; }

    public IReadOnlyList<ModIssue> Errors { get; }

    public IReadOnlyList<ModIssue> Warnings { get; }

    public bool Ok => Errors.Count == 0;

    public LoadedPackage? Find(string id) => _index.TryGetValue(id, out int i) ? Packages[i] : null;

    /// <summary>Индекс пакета в порядке загрузки или -1.</summary>
    public int IndexOf(string id) => _index.TryGetValue(id, out int i) ? i : -1;

    /// <summary>Хост-обёртка: первая ошибка как <see cref="ScenarioException"/> (modding.md §9).</summary>
    public void ThrowIfFailed()
    {
        if (Errors.Count > 0)
        {
            throw new ScenarioException(Errors[0].ToString(), Errors[0].Code);
        }
    }

    /// <summary>Пакеты, ассеты которых видны из <paramref name="from"/>: он сам и его транзитивные <c>requires</c>.</summary>
    public HashSet<string> VisiblePackages(string from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            string id = stack.Pop();
            if (!seen.Add(id))
            {
                continue;
            }

            LoadedPackage? p = Find(id);
            if (p == null)
            {
                continue;
            }

            foreach (PackageRequire r in p.Manifest.Requires)
            {
                stack.Push(r.Id);
            }
        }

        return seen;
    }

    /// <summary>
    /// Раскрыть ссылку на ассет из JSON пакета <paramref name="from"/> (modding.md §2): полный id даёт одного кандидата
    /// (пакет должен быть виден, иначе <see cref="ModError.UndeclaredDependency"/>); сокращение <c>vehicle/car</c> — своего
    /// пакета, затем <c>base</c>. Существование ассета проверяет вызывающий (каталог контента — M2).
    /// </summary>
    public bool TryExpandRef(string reference, string from, List<AssetId> candidates, out ModError error)
    {
        error = default;
        candidates.Clear();
        HashSet<string> visible = VisiblePackages(from);
        if (AssetId.TryParse(reference, out AssetId full))
        {
            if (!visible.Contains(full.Package))
            {
                error = ModError.UndeclaredDependency;
                return false;
            }

            candidates.Add(full);
            return true;
        }

        if (AssetId.TryParseShort(reference, out AssetKind kind, out string name))
        {
            candidates.Add(new AssetId(from, kind, name));
            if (from != "base" && visible.Contains("base"))
            {
                candidates.Add(new AssetId("base", kind, name));
            }

            return true;
        }

        error = ModError.BadValue;
        return false;
    }
}
