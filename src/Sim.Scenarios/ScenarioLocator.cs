using System;
using System.IO;

namespace Sim.Scenarios;

/// <summary>Разрешение аргумента <c>--scenario</c>: путь к файлу или имя (<c>s1k</c> → <c>scenarios/s1k.json</c>).</summary>
public static class ScenarioLocator
{
    private const string SolutionFile = "traffic-city.sln";

    public static string Resolve(string nameOrPath, string? scenariosDir)
    {
        bool looksLikePath = nameOrPath.Contains('/', StringComparison.Ordinal)
            || nameOrPath.Contains('\\', StringComparison.Ordinal)
            || nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (looksLikePath)
        {
            if (File.Exists(nameOrPath))
            {
                return nameOrPath;
            }

            if (scenariosDir == null)
            {
                throw new ScenarioException($"scenario file '{nameOrPath}' not found");
            }
        }

        string dir = scenariosDir ?? FindScenariosDir();
        string file = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : nameOrPath + ".json";
        string candidate = Path.Combine(dir, file);
        if (!File.Exists(candidate))
        {
            throw new ScenarioException($"scenario '{nameOrPath}' not found (looked for {candidate})");
        }

        return candidate;
    }

    private static string FindScenariosDir()
    {
        string? root = FindRepoRoot(Directory.GetCurrentDirectory()) ?? FindRepoRoot(AppContext.BaseDirectory);
        if (root == null)
        {
            throw new ScenarioException(
                $"cannot locate repo root ({SolutionFile} not found above current directory); use --scenarios-dir or pass a path");
        }

        return Path.Combine(root, "scenarios");
    }

    /// <summary>Каталог <c>content/</c> игры (modding.md §1.1): вверх от <paramref name="startDir"/> до корня репозитория.</summary>
    public static string? FindContentDir(string startDir)
    {
        string? root = FindRepoRoot(startDir);
        if (root == null)
        {
            return null;
        }

        string dir = Path.Combine(root, "content");
        return Directory.Exists(dir) ? dir : null;
    }

    private static string? FindRepoRoot(string start)
    {
        DirectoryInfo? d = new(start);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, SolutionFile)))
            {
                return d.FullName;
            }

            d = d.Parent;
        }

        return null;
    }
}
