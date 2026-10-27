using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Scenarios;

/// <summary>
/// Подмена внешних файлов сценария для тестов и golden (road-profiles.md §2 п. 3): замороженный каталог-фикстура вместо игрового.
/// Подмена действует, только если сценарий сам объявил <c>road_profiles_file</c>: legacy-сценарий остаётся legacy.
/// </summary>
public sealed class ScenarioOverrides
{
    /// <summary>Путь к файлу каталога профилей (абсолютный или от текущего каталога) вместо <c>road_profiles_file</c> сценария.</summary>
    public string? Profiles { get; init; }
}

// Каталог профилей дорог (road-profiles.md §1) -> RoadProfilesDesc: ширины в метрах переводятся в целые мм, остальное ядро проверяет само (RoadProfileValidator).
public static partial class ScenarioLoader
{
    public const int RoadProfilesFormatVersion = 1;
    private const int DefaultModuleMm = 8000;

    /// <summary>Разбор текста каталога (<c>scenarios/road-profiles.json</c>, <c>tests/fixtures/road-profiles.fixture.json</c>) с валидацией ядра. Ошибка - <see cref="ScenarioException"/> с JSON-путём.</summary>
    public static RoadProfilesDesc ParseRoadProfiles(string json)
    {
        using JsonDocument doc = OpenDoc(json, "road profiles");
        var root = new JsonObjectReader(doc.RootElement, "$");
        CheckVersion(root, RoadProfilesFormatVersion);
        int moduleMm = Mm(root.Num("module_m", 8, min: 0.001), root.ChildPath("module_m"));
        JsonElement[] items = root.ReqArray("profiles");
        string pp = root.ChildPath("profiles");
        root.Finish();

        var profiles = new RoadProfileDesc[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            string p = JsonObjectReader.Index(pp, i);
            var r = new JsonObjectReader(items[i], p);
            profiles[i].Id = r.Str("id");
            string kind = r.Str("kind");
            profiles[i].Kind = kind switch
            {
                "street" => RoadKind.Street,
                "arterial" => RoadKind.Arterial,
                _ => throw new ScenarioException($"{r.ChildPath("kind")}: expected \"street\" or \"arterial\""),
            };
            profiles[i].WidthMm = Mm(r.Num("width_m", min: 0.001), r.ChildPath("width_m"));
            JsonElement[] els = r.ReqArray("elements");
            string ep = r.ChildPath("elements");
            r.Finish();
            profiles[i].Elements = new RoadElemDesc[els.Length];
            for (int e = 0; e < els.Length; e++)
            {
                string q = JsonObjectReader.Index(ep, e);
                var er = new JsonObjectReader(els[e], q);
                string type = er.Str("type");
                RoadElemKind ek = type switch
                {
                    "sidewalk" => RoadElemKind.Sidewalk,
                    "verge" => RoadElemKind.Verge,
                    "curb" => RoadElemKind.Curb,
                    "lane" => RoadElemKind.Lane,
                    "median" => RoadElemKind.Median,
                    "bike" => RoadElemKind.Bike,
                    "parking" => RoadElemKind.Parking,
                    "tram" => RoadElemKind.Tram,
                    "bus_bay" => RoadElemKind.BusBay,
                    _ => throw new ScenarioException($"{er.ChildPath("type")}: unknown element type"),
                };
                profiles[i].Elements[e].Kind = ek;
                profiles[i].Elements[e].WidthMm = Mm(er.Num("w", min: 0), er.ChildPath("w"));
                if (ek == RoadElemKind.Lane)
                {
                    string dir = er.Str("dir");
                    profiles[i].Elements[e].Dir = dir switch
                    {
                        "forward" => RoadLaneDir.Forward,
                        "backward" => RoadLaneDir.Backward,
                        _ => throw new ScenarioException($"{er.ChildPath("dir")}: expected \"forward\" or \"backward\""),
                    };
                }
                else if (er.Has("dir"))
                {
                    throw new ScenarioException($"{er.ChildPath("dir")}: allowed only for \"lane\" elements");
                }

                er.Finish();
            }
        }

        var desc = new RoadProfilesDesc { ModuleMm = moduleMm, Profiles = profiles };
        RoadProfileError err = RoadProfileValidator.Validate(in desc, out int bad);
        if (err != RoadProfileError.None)
        {
            string where = bad >= 0 ? $"{JsonObjectReader.Index(pp, bad)} (\"{profiles[bad].Id}\")" : pp;
            throw new ScenarioException($"{where}: invalid road profile ({err})");
        }

        return desc;
    }

    /// <summary>Метры -> целые мм; нецелое число мм - ошибка (ширины в каталоге целые мм, road-profiles.md §1).</summary>
    private static int Mm(double meters, string path)
    {
        double mm = meters * 1000.0;
        double rounded = Math.Round(mm, MidpointRounding.AwayFromZero);
        if (Math.Abs(mm - rounded) > 1e-6 || rounded > 1_000_000)
        {
            throw new ScenarioException($"{path}: value {meters.ToString(CultureInfo.InvariantCulture)} is not a whole number of millimetres (or too large)");
        }

        return (int)rounded;
    }

    /// <summary>Читает <c>road_profiles_file</c> (с подменой <paramref name="overrides"/>); ключа нет - режим legacy (<c>Profiles = null</c>).</summary>
    private static RoadProfilesDesc LoadRoadProfiles(JsonObjectReader root, Func<string, string> readRelativeFile, ScenarioOverrides? overrides)
    {
        if (!root.Has("road_profiles_file"))
        {
            return default;
        }

        string file = root.Str("road_profiles_file");
        string path = overrides?.Profiles ?? file;
        string text;
        try
        {
            text = readRelativeFile(path);
        }
        catch (ScenarioException e)
        {
            throw new ScenarioException($"$.road_profiles_file: {e.Message}", e);
        }

        try
        {
            return ParseRoadProfiles(text);
        }
        catch (ScenarioException e)
        {
            throw new ScenarioException($"{path}: {e.Message}", e);
        }
    }

    private static int ProfileRef(RoadProfilesDesc profiles, string id, string path)
    {
        RoadProfileDesc[] ps = profiles.Profiles;
        for (int i = 0; i < ps.Length; i++)
        {
            if (string.Equals(ps[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new ScenarioException($"{path}: unknown road profile '{id}'");
    }

    /// <summary><c>city.profiles</c> ({ "street": id, "arterial": id }): обязательна с каталогом, без него запрещена.</summary>
    private static void ParseCityProfiles(JsonObjectReader r, RoadProfilesDesc profiles, ref CityGenDesc c)
    {
        JsonObjectReader? pr = r.OptObject("profiles");
        if (!profiles.Enabled)
        {
            if (pr != null)
            {
                throw new ScenarioException($"{r.ChildPath("profiles")}: requires $.road_profiles_file");
            }

            return;
        }

        if (pr == null)
        {
            throw new ScenarioException($"{r.ChildPath("profiles")}: required key is missing when $.road_profiles_file is set");
        }

        int street = ProfileRef(profiles, pr.Str("street"), pr.ChildPath("street"));
        int arterial = ProfileRef(profiles, pr.Str("arterial"), pr.ChildPath("arterial"));
        if (profiles.Profiles[street].Kind != RoadKind.Street)
        {
            throw new ScenarioException($"{pr.ChildPath("street")}: profile '{profiles.Profiles[street].Id}' is not of kind \"street\"");
        }

        if (profiles.Profiles[arterial].Kind != RoadKind.Arterial)
        {
            throw new ScenarioException($"{pr.ChildPath("arterial")}: profile '{profiles.Profiles[arterial].Id}' is not of kind \"arterial\"");
        }

        pr.Finish();
        c.StreetProfile = street + 1;
        c.ArterialProfile = arterial + 1;
    }

    /// <summary>
    /// Поля сегмента явной сети при каталоге: <c>profile</c> (id) обязателен, <c>mirrored</c> по умолчанию false; <c>lanes_ab</c>/<c>lanes_ba</c>/<c>kind</c> выводятся из профиля,
    /// а если заданы - обязаны совпасть. Возвращает false, если каталога нет (поля читает прежний код).
    /// </summary>
    private static bool ParseSegmentProfile(JsonObjectReader s, RoadProfilesDesc profiles, ref SegmentDesc seg)
    {
        if (!profiles.Enabled)
        {
            if (s.Has("profile") || s.Has("mirrored"))
            {
                throw new ScenarioException($"{s.ChildPath(s.Has("profile") ? "profile" : "mirrored")}: requires $.road_profiles_file");
            }

            return false;
        }

        int p = ProfileRef(profiles, s.Str("profile"), s.ChildPath("profile"));
        bool mirrored = s.Bool("mirrored", false);
        RoadProfileDesc pd = profiles.Profiles[p];
        int fwd = 0;
        int bwd = 0;
        foreach (RoadElemDesc e in pd.Elements)
        {
            if (e.Kind == RoadElemKind.Lane)
            {
                if (e.Dir == RoadLaneDir.Forward)
                {
                    fwd++;
                }
                else
                {
                    bwd++;
                }
            }
        }

        seg.Profile = p + 1;
        seg.Mirrored = mirrored;
        seg.LanesAB = mirrored ? bwd : fwd;
        seg.LanesBA = mirrored ? fwd : bwd;
        seg.Kind = pd.Kind;
        if (s.Has("lanes_ab") && s.Int("lanes_ab") != seg.LanesAB)
        {
            throw new ScenarioException($"{s.ChildPath("lanes_ab")}: does not match profile '{pd.Id}' ({seg.LanesAB})");
        }

        if (s.Has("lanes_ba") && s.Int("lanes_ba") != seg.LanesBA)
        {
            throw new ScenarioException($"{s.ChildPath("lanes_ba")}: does not match profile '{pd.Id}' ({seg.LanesBA})");
        }

        if (s.Has("kind"))
        {
            string kind = s.Str("kind");
            if (kind != (pd.Kind == RoadKind.Street ? "street" : "arterial"))
            {
                throw new ScenarioException($"{s.ChildPath("kind")}: does not match profile '{pd.Id}'");
            }
        }

        return true;
    }
}
