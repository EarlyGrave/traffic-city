using System;
using System.Collections.Generic;
using System.IO;

namespace Sim.Scenarios.Mods.Gltf;

/// <summary>
/// Проверка ключей <c>visual.model</c> / <c>visual.profile</c> каталога набора пакетов (modding.md §4.1): файл есть в пакете,
/// расширение <c>.gltf|.glb</c>, содержимое проходит <see cref="GltfValidator"/>. Ассеты без <c>visual</c> не проверяются
/// (клиент рисует процедурный бокс).
/// </summary>
public static class VisualValidator
{
    /// <summary>Проверить все визуалы каталога. Ошибки и предупреждения добавляются в списки; общий файл проверяется один раз.</summary>
    public static void Validate(
        PackageSet set, ContentCatalog catalog, RenderBudgets budgets, List<ModIssue> errors, List<ModIssue> warnings)
    {
        var done = new HashSet<(string Package, string File)>();
        foreach (VehicleEntry v in catalog.Vehicles)
        {
            if (v.VisualModel != null)
            {
                One(set, v.Id, v.VisualModel, "visual.model", budgets, done, errors, warnings);
            }
        }

        foreach (RoadEntry r in catalog.Roads)
        {
            if (r.VisualProfile != null)
            {
                One(set, r.Id, r.VisualProfile, "visual.profile", budgets, done, errors, warnings);
            }
        }
    }

    private static void One(
        PackageSet set,
        AssetId id,
        string file,
        string key,
        RenderBudgets budgets,
        HashSet<(string Package, string File)> done,
        List<ModIssue> errors,
        List<ModIssue> warnings)
    {
        LoadedPackage? pkg = set.Find(id.Package);
        if (pkg == null || !done.Add((id.Package, file)))
        {
            return;
        }

        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".gltf" or ".glb"))
        {
            errors.Add(new ModIssue(pkg.Id, file, string.Empty, ModError.FileNotAllowed, $"{id}: {key} must be .gltf or .glb"));
            return;
        }

        if (!HasFile(pkg, file) || !SafePath.IsInsideRoot(pkg.Directory, file))
        {
            errors.Add(new ModIssue(pkg.Id, file, string.Empty, ModError.UnknownAssetRef, $"{id}: {key} file not found in the package"));
            return;
        }

        GltfValidator.Validate(pkg, file, id.Kind, budgets, errors, warnings);
    }

    private static bool HasFile(LoadedPackage pkg, string file)
    {
        foreach (PackageFile f in pkg.Files)
        {
            if (string.Equals(f.Path, file, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
