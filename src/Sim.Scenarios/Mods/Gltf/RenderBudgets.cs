using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sim.Scenarios.Mods.Gltf;

/// <summary>Бюджет одного вида ассета: минимум LOD-уровней и необязательные пределы треугольников по уровням (<c>null</c> — не проверяется).</summary>
public sealed record KindBudget(int MinLods, IReadOnlyList<int?> MaxTris);

/// <summary>
/// Бюджеты рендера из <c>content/render-budgets.json</c> (modding.md §4.3). Числа задаёт ADR-0014; код читает их из файла.
/// Формат: <c>{ "format_version": 1, "kinds": { "vehicle": { "min_lods": 3, "max_tris": [null, null, null] }, ... } }</c>.
/// </summary>
public sealed class RenderBudgets
{
    public const string FileName = "render-budgets.json";
    public const int FormatVersion = 1;
    public const int MaxLodLevels = 8;

    /// <summary>Заглушка до ADR-0014: <c>min_lods = 3</c> (cs2-lessons.md №4), треугольники не проверяются.</summary>
    // TODO(ADR-0014): числа задаёт ADR-0014 через content/render-budgets.json; эта заглушка нужна, только если файла нет.
    public static readonly KindBudget StubBudget = new(3, []);

    private readonly Dictionary<AssetKind, KindBudget> _kinds;

    private RenderBudgets(Dictionary<AssetKind, KindBudget> kinds) => _kinds = kinds;

    /// <summary>Набор по умолчанию (файла нет): заглушка для каждого вида.</summary>
    public static RenderBudgets Default { get; } = new([]);

    /// <summary>Бюджет вида. Проп без своей записи берёт запись здания; вид без записи — заглушку.</summary>
    public KindBudget For(AssetKind kind)
    {
        if (_kinds.TryGetValue(kind, out KindBudget? b))
        {
            return b;
        }

        if (kind == AssetKind.Prop && _kinds.TryGetValue(AssetKind.Building, out KindBudget? building))
        {
            return building;
        }

        return StubBudget;
    }

    /// <summary>Прочитать файл бюджетов. Ошибки — в <paramref name="issues"/>, результат <c>null</c>.</summary>
    public static RenderBudgets? Load(string path, List<ModIssue> issues)
    {
        string name = Path.GetFileName(path);
        try
        {
            var info = new FileInfo(path);
            if (info.Length > ModLimits.MaxJsonBytes)
            {
                issues.Add(new ModIssue(string.Empty, name, "$", ModError.TooLarge, "file too large"));
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = ModLimits.MaxJsonDepth });
            return Parse(doc.RootElement, name, issues);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            issues.Add(new ModIssue(string.Empty, name, "$", ModError.BadValue, e.GetType().Name));
            return null;
        }
    }

    private static RenderBudgets? Parse(JsonElement root, string name, List<ModIssue> issues)
    {
        try
        {
            var r = new JsonObjectReader(root, "$");
            int version = r.Int("format_version");
            if (version != FormatVersion)
            {
                issues.Add(new ModIssue(string.Empty, name, "$.format_version", ModError.UnsupportedFormatVersion, $"version {version}, supported {FormatVersion}"));
                return null;
            }

            JsonObjectReader kinds = r.ReqObject("kinds");
            var map = new Dictionary<AssetKind, KindBudget>();
            foreach (string key in new List<string>(kinds.Keys))
            {
                if (!AssetId.TryParseKind(key, out AssetKind kind))
                {
                    throw new ScenarioException($"{kinds.ChildPath(key)}: unknown key");
                }

                JsonObjectReader k = kinds.ReqObject(key);
                int minLods = k.Int("min_lods", min: 1, max: MaxLodLevels);
                var tris = new List<int?>();
                JsonElement[]? arr = k.OptArray("max_tris");
                if (arr != null)
                {
                    if (arr.Length > MaxLodLevels)
                    {
                        throw new ScenarioException($"{k.ChildPath("max_tris")}: out of range, more than {MaxLodLevels} levels");
                    }

                    for (int i = 0; i < arr.Length; i++)
                    {
                        tris.Add(arr[i].ValueKind == JsonValueKind.Null
                            ? null
                            : JsonObjectReader.IntValue(arr[i], JsonObjectReader.Index(k.ChildPath("max_tris"), i), 1, int.MaxValue));
                    }
                }

                k.Finish();
                map[kind] = new KindBudget(minLods, tris);
            }

            kinds.Finish();
            r.Finish();
            return new RenderBudgets(map);
        }
        catch (ScenarioException e)
        {
            issues.Add(ContentCatalog.FromException(string.Empty, name, e));
            return null;
        }
    }
}
