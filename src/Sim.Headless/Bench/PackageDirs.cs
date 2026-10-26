using System.Collections.Generic;
using System.IO;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

namespace Sim.Headless.Bench;

/// <summary>
/// Опция <c>--packages-dir &lt;dir[:dir...]&gt;</c> (разделитель - <see cref="Path.PathSeparator"/>): каталоги пользовательских пакетов
/// (modding.md §1.1, §5.1). Идут после <c>content/</c> игры; порядок каталогов влияет только на поиск.
/// </summary>
internal static class PackageDirs
{
    public const string Option = "--packages-dir";

    /// <summary>Каталоги поиска для сценария <paramref name="startDir"/>: <c>content/</c> игры и каталоги из <c>--packages-dir</c>.</summary>
    public static IReadOnlyList<PackageSearchDir> For(string startDir, CliArgs a)
    {
        var dirs = new List<PackageSearchDir>(ScenarioLoader.DefaultSearchDirs(startDir));
        string? extra = a.Get(Option);
        if (extra != null)
        {
            foreach (string d in extra.Split(Path.PathSeparator, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
            {
                dirs.Add(new PackageSearchDir(Path.GetFullPath(d), false));
            }
        }

        return dirs;
    }

    /// <summary>Загрузить сценарий с учётом <c>--packages-dir</c>.</summary>
    public static LoadedScenario Load(string scenarioPath, CliArgs a) =>
        ScenarioLoader.LoadInfo(scenarioPath, For(Path.GetDirectoryName(Path.GetFullPath(scenarioPath)) ?? ".", a));
}
