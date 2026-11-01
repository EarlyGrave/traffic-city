using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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

    /// <summary>
    /// Каталог «Новой игры» (scenarios-mode.md §1.2): перебирает <c>scenarios/*.json</c> (без подкаталогов) и читает только секцию <c>meta</c>, без загрузки сети.
    /// Файлы без <c>meta</c> (тестовые сценарии, типы ТС, профили) пропускаются. Порядок - по <c>order</c>, затем по имени файла. Испорченная <c>meta</c> или JSON - ошибка с именем файла.
    /// </summary>
    public static IReadOnlyList<ScenarioListEntry> List(string? scenariosDir = null)
    {
        string dir = scenariosDir ?? FindScenariosDir();
        string[] files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var list = new List<ScenarioListEntry>();
        foreach (string file in files)
        {
            ScenarioMeta? meta = ReadMeta(file);
            if (meta != null)
            {
                list.Add(new ScenarioListEntry(Path.GetFileNameWithoutExtension(file), file, meta));
            }
        }

        list.Sort(static (a, b) => a.Meta.Order != b.Meta.Order ? a.Meta.Order.CompareTo(b.Meta.Order) : string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>Только секция <c>meta</c> файла сценария; <c>null</c> - секции нет или корень не объект.</summary>
    public static ScenarioMeta? ReadMeta(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ScenarioException($"cannot read '{path}': {e.Message}", e);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("meta", out JsonElement m))
            {
                return null;
            }

            return ScenarioLoader.ParseMeta(new JsonObjectReader(m, "$.meta"));
        }
        catch (JsonException e)
        {
            throw new ScenarioException($"{Path.GetFileName(path)}: {e.Message}", e);
        }
        catch (ScenarioException e)
        {
            throw new ScenarioException($"{Path.GetFileName(path)}: {e.Message}", e);
        }
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

/// <summary>Карточка каталога «Новой игры»: имя (как у <c>--scenario</c>), путь к файлу, метаданные.</summary>
public sealed record ScenarioListEntry(string Name, string Path, ScenarioMeta Meta);
