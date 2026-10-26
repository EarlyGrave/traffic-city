using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Sim.Scenarios.Mods.Gltf;

/// <summary>Дальнее представление объекта за последним LOD (render.md §4.1, §4.3); строит клиент, ассет его не несёт.</summary>
public enum FarMode
{
    None,
    Points,
    ImpostorBox,
    FlatRibbon,
}

/// <summary>
/// Бюджет одного вида ассета (render.md §4.1). <c>null</c> / пустой список — «не проверять». Для формата 2 уровни сверх длины массива
/// берут последний элемент (<see cref="ExtendLast"/>); формат 1 читается как раньше — уровни сверх длины не проверяются.
/// </summary>
public sealed record KindBudget(
    int MinLods,
    IReadOnlyList<int?> MaxTris,
    IReadOnlyList<int?>? MaxSurfaces = null,
    int? MaxTextureSide = null,
    int? UntexturedFromLod = null,
    double? ProfileTileM = null,
    IReadOnlyList<double>? LodDistancesM = null,
    FarMode Far = FarMode.None,
    double? FarDistanceM = null,
    bool ExtendLast = false)
{
    /// <summary>Предел треугольников уровня <paramref name="level"/> или <c>null</c>.</summary>
    public int? TrisLimit(int level) => Limit(MaxTris, level, ExtendLast);

    /// <summary>Предел поверхностей уровня <paramref name="level"/> или <c>null</c>.</summary>
    public int? SurfacesLimit(int level) => Limit(MaxSurfaces, level, ExtendLast);

    private static int? Limit(IReadOnlyList<int?>? list, int level, bool extend)
    {
        if (list == null || list.Count == 0)
        {
            return null;
        }

        return level < list.Count ? list[level] : extend ? list[^1] : null;
    }
}

/// <summary>Пешеходы (клиентская модель, не ассет): дистанции LOD и дальнее представление.</summary>
public sealed record PedestrianBudget(IReadOnlyList<double> LodDistancesM, FarMode Far, double? FarDistanceM);

/// <summary>Тени пресета: дальность, число каскадов (1, 2 или 4), размер атласа.</summary>
public sealed record ShadowBudget(double DistanceM, int Splits, int Atlas);

/// <summary>Пресет качества клиента (<c>low</c>/<c>high</c>); <c>Caps</c> — лимиты инстансов на кадр по виду (4 уровня).</summary>
public sealed record PresetBudget(
    double LodScale, double PointMinPx, double Scaling3d, ShadowBudget Shadow, IReadOnlyDictionary<string, IReadOnlyList<int>> Caps);

/// <summary>
/// Бюджеты рендера из <c>content/render-budgets.json</c> (render.md §4.1, modding.md §4.3; ADR-0014). Числа — только из файла.
/// Форматы: 1 (<c>min_lods</c> + <c>max_tris</c>) и 2 (пределы поверхностей, текстур, дистанции LOD, пешеходы, пресеты клиента).
/// Секции <c>pedestrian</c> и <c>presets</c> читает клиент; валидатор проверяет только их форму.
/// </summary>
public sealed class RenderBudgets
{
    public const string FileName = "render-budgets.json";

    /// <summary>Текущий формат файла; формат 1 тоже читается.</summary>
    public const int FormatVersion = 2;
    public const int MaxLodLevels = 8;

    /// <summary>Вид без записи в файле (или файла нет): 3 LOD, пределы не проверяются (формат 1, cs2-lessons.md №4).</summary>
    public static readonly KindBudget Fallback = new(3, []);

    private static readonly string[] _capKinds = ["vehicle", "pedestrian", "building", "prop", "road"];

    private readonly Dictionary<AssetKind, KindBudget> _kinds;

    private RenderBudgets(
        int version, Dictionary<AssetKind, KindBudget> kinds, PedestrianBudget? pedestrian, Dictionary<string, PresetBudget> presets)
    {
        Version = version;
        _kinds = kinds;
        Pedestrian = pedestrian;
        Presets = presets;
    }

    /// <summary>Набор по умолчанию (файла нет): <see cref="Fallback"/> для каждого вида.</summary>
    public static RenderBudgets Default { get; } = new(1, [], null, []);

    /// <summary><c>format_version</c> прочитанного файла (1 у набора по умолчанию).</summary>
    public int Version { get; }

    /// <summary>Секция <c>pedestrian</c> (формат 2) или <c>null</c>.</summary>
    public PedestrianBudget? Pedestrian { get; }

    /// <summary>Пресеты клиента по имени (<c>low</c>, <c>high</c>, ...); пусто в формате 1.</summary>
    public IReadOnlyDictionary<string, PresetBudget> Presets { get; }

    /// <summary>Бюджет вида. Проп без своей записи берёт запись здания (формат 1); вид без записи — <see cref="Fallback"/>.</summary>
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

        return Fallback;
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
            if (version is not (1 or 2))
            {
                issues.Add(new ModIssue(string.Empty, name, "$.format_version", ModError.UnsupportedFormatVersion, $"version {version}, supported 1..{FormatVersion}"));
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

                map[kind] = ParseKind(kinds.ReqObject(key), kind, version);
            }

            kinds.Finish();

            PedestrianBudget? ped = null;
            var presets = new Dictionary<string, PresetBudget>();
            if (version >= 2)
            {
                JsonObjectReader? p = r.OptObject("pedestrian");
                if (p != null)
                {
                    (List<double> dist, FarMode far, double? farDist) = ParseDistances(p, null);
                    p.Finish();
                    ped = new PedestrianBudget(dist, far, farDist);
                }

                JsonObjectReader? ps = r.OptObject("presets");
                if (ps != null)
                {
                    foreach (string key in new List<string>(ps.Keys))
                    {
                        presets[key] = ParsePreset(ps.ReqObject(key));
                    }

                    ps.Finish();
                }
            }

            r.Finish();
            return new RenderBudgets(version, map, ped, presets);
        }
        catch (ScenarioException e)
        {
            issues.Add(ContentCatalog.FromException(string.Empty, name, e));
            return null;
        }
    }

    private static KindBudget ParseKind(JsonObjectReader k, AssetKind kind, int version)
    {
        int minLods = k.Int("min_lods", min: 1, max: MaxLodLevels);
        List<int?> tris = IntLimits(k, "max_tris", minLods, version);
        if (version == 1)
        {
            k.Finish();
            return new KindBudget(minLods, tris);
        }

        List<int?> surfaces = IntLimits(k, "max_surfaces", minLods, version);
        int? side = null;
        if (k.Has("max_texture_side"))
        {
            int s = k.Int("max_texture_side", min: 1, max: ModLimits.MaxTextureSide);
            if ((s & (s - 1)) != 0)
            {
                throw new ScenarioException($"{k.ChildPath("max_texture_side")}: {s} is not a power of two");
            }

            side = s;
        }

        int? untextured = k.Has("untextured_from_lod") ? k.Int("untextured_from_lod", min: 0, max: MaxLodLevels - 1) : null;
        double? tile = null;
        if (k.Has("profile_tile_m"))
        {
            if (kind != AssetKind.Road)
            {
                throw new ScenarioException($"{k.ChildPath("profile_tile_m")}: allowed only for road");
            }

            tile = k.Num("profile_tile_m", min: 0.01, max: 64);
        }

        (List<double> dist, FarMode far, double? farDist) = ParseDistances(k, minLods);
        k.Finish();
        return new KindBudget(minLods, tris, surfaces, side, untextured, tile, dist, far, farDist, ExtendLast: true);
    }

    // max_tris / max_surfaces: null = не проверять. Формат 2 - длина minLods..MaxLodLevels, формат 1 - 0..MaxLodLevels.
    private static List<int?> IntLimits(JsonObjectReader k, string key, int minLods, int version)
    {
        var list = new List<int?>();
        JsonElement[]? arr = k.OptArray(key);
        if (arr == null)
        {
            return list;
        }

        string path = k.ChildPath(key);
        if (arr.Length > MaxLodLevels)
        {
            throw new ScenarioException($"{path}: out of range, more than {MaxLodLevels} levels");
        }

        if (version >= 2 && arr.Length < minLods)
        {
            throw new ScenarioException($"{path}: {arr.Length} levels, at least min_lods ({minLods}) required");
        }

        for (int i = 0; i < arr.Length; i++)
        {
            list.Add(arr[i].ValueKind == JsonValueKind.Null
                ? null
                : JsonObjectReader.IntValue(arr[i], JsonObjectReader.Index(path, i), 1, int.MaxValue));
        }

        return list;
    }

    // lod_distances_m (строго возрастают; длина == exactLen, если задана, иначе 1..MaxLodLevels), far, far_distance_m.
    private static (List<double> Dist, FarMode Far, double? FarDist) ParseDistances(JsonObjectReader k, int? exactLen)
    {
        var dist = new List<double>();
        JsonElement[]? arr = k.OptArray("lod_distances_m");
        string path = k.ChildPath("lod_distances_m");
        if (arr != null)
        {
            if (arr.Length == 0 || arr.Length > MaxLodLevels)
            {
                throw new ScenarioException($"{path}: out of range, {arr.Length} levels, expected 1..{MaxLodLevels}");
            }

            if (exactLen is int e && arr.Length != e)
            {
                throw new ScenarioException($"{path}: {arr.Length} distances, min_lods is {exactLen}");
            }

            for (int i = 0; i < arr.Length; i++)
            {
                string ip = JsonObjectReader.Index(path, i);
                double d = JsonObjectReader.NumValue(arr[i], ip, 0.001, 1e6);
                if (i > 0 && d <= dist[i - 1])
                {
                    throw new ScenarioException($"{ip}: distances must strictly increase, {Fmt(d)} <= {Fmt(dist[i - 1])}");
                }

                dist.Add(d);
            }
        }

        FarMode far = FarMode.None;
        if (k.Has("far"))
        {
            string s = k.Str("far");
            far = s switch
            {
                "points" => FarMode.Points,
                "impostor_box" => FarMode.ImpostorBox,
                "flat_ribbon" => FarMode.FlatRibbon,
                "none" => FarMode.None,
                _ => throw new ScenarioException($"{k.ChildPath("far")}: unknown value '{s}', expected points|impostor_box|flat_ribbon|none"),
            };
        }

        double? farDist = null;
        if (k.Has("far_distance_m"))
        {
            string fp = k.ChildPath("far_distance_m");
            if (far == FarMode.None)
            {
                throw new ScenarioException($"{fp}: not allowed when far is none or absent");
            }

            double fd = k.Num("far_distance_m", min: 0.001, max: 1e6);
            if (dist.Count > 0 && fd <= dist[^1])
            {
                throw new ScenarioException($"{fp}: {Fmt(fd)} must be greater than the last lod distance {Fmt(dist[^1])}");
            }

            farDist = fd;
        }

        return (dist, far, farDist);
    }

    private static PresetBudget ParsePreset(JsonObjectReader p)
    {
        double lodScale = p.Num("lod_scale", min: 0.01, max: 10);
        double pointPx = p.Num("point_min_px", min: 0.01, max: 64);
        double scaling = p.Num("scaling_3d", min: 0.01, max: 4);
        JsonObjectReader s = p.ReqObject("shadow");
        double distance = s.Num("distance_m", min: 0.001, max: 1e6);
        int splits = s.Int("splits");
        if (splits is not (1 or 2 or 4))
        {
            throw new ScenarioException($"{s.ChildPath("splits")}: value {splits} out of range, expected 1, 2 or 4");
        }

        int atlas = s.Int("atlas", min: 1, max: 16384);
        if ((atlas & (atlas - 1)) != 0)
        {
            throw new ScenarioException($"{s.ChildPath("atlas")}: {atlas} is not a power of two");
        }

        s.Finish();
        var caps = new Dictionary<string, IReadOnlyList<int>>();
        JsonObjectReader? c = p.OptObject("caps");
        if (c != null)
        {
            foreach (string key in new List<string>(c.Keys))
            {
                if (Array.IndexOf(_capKinds, key) < 0)
                {
                    throw new ScenarioException($"{c.ChildPath(key)}: unknown key");
                }

                JsonElement[] arr = c.ReqArray(key);
                string cp = c.ChildPath(key);
                if (arr.Length != 4)
                {
                    throw new ScenarioException($"{cp}: out of range, {arr.Length} elements, expected 4");
                }

                var vals = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    vals[i] = JsonObjectReader.IntValue(arr[i], JsonObjectReader.Index(cp, i), 0, int.MaxValue);
                }

                caps[key] = vals;
            }

            c.Finish();
        }

        p.Finish();
        return new PresetBudget(lodScale, pointPx, scaling, new ShadowBudget(distance, splits, atlas), caps);
    }

    private static string Fmt(double d) => d.ToString(CultureInfo.InvariantCulture);
}
