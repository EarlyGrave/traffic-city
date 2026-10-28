using System;
using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Worlds;
using Sim.Scenarios;

namespace Sim.Core.Tests.Save;

/// <summary>Миры в режиме профилей дорог для тестов сейва v8 и корпуса <c>w8-*-prof</c> (каталог - замороженная фикстура, не игровой).</summary>
internal static class ProfileSaveFixtures
{
    public static string FixturePath => Path.Combine(GenFixture.RepoRoot(), "tests", "fixtures", "road-profiles.fixture.json");

    public static RoadProfilesDesc Catalog() => ScenarioLoader.ParseRoadProfiles(File.ReadAllText(FixturePath));

    public static int IndexOf(in RoadProfilesDesc d, string id)
    {
        for (int i = 0; i < d.Profiles.Length; i++)
        {
            if (d.Profiles[i].Id == id)
            {
                return i;
            }
        }

        throw new ArgumentException(id);
    }

    /// <summary>Перекрёсток из четырёх сегментов: главная (arterial_4) по X, поперечная - асимметричный профиль, второй полусегмент нарисован против профиля (Mirrored).</summary>
    public static ScenarioDesc Cross()
    {
        RoadProfilesDesc cat = Catalog();
        int pm = IndexOf(in cat, "arterial_4");
        int ps = IndexOf(in cat, "asym_1_2");
        var store = new RoadProfileStore(in cat);
        SegmentDesc Seg(int a, int b, int p, bool mir) => new()
        {
            A = a,
            B = b,
            Profile = p + 1,
            Mirrored = mir,
            LanesAB = store.LanesAB(p, mir),
            LanesBA = store.LanesBA(p, mir),
            SpeedLimit = 13.9f,
            Kind = store.Kind[p],
        };

        ScenarioDesc d = TestScenarios.Line();
        d.Network = new NetworkDesc
        {
            Nodes =
            [
                new NodeDesc { X = 0, Z = 0, Control = NodeControl.None },
                new NodeDesc { X = 200, Z = 0, Control = NodeControl.None },
                new NodeDesc { X = -200, Z = 0, Control = NodeControl.None },
                new NodeDesc { X = 0, Z = 200, Control = NodeControl.None },
                new NodeDesc { X = 0, Z = -200, Control = NodeControl.None },
            ],
            Segments =
            [
                Seg(0, 1, pm, false),
                Seg(2, 0, pm, false),
                Seg(0, 3, ps, false),
                Seg(4, 0, ps, true),
            ],
        };
        d.Demand.Zones =
        [
            new ZoneDesc { CenterX = 200, CenterZ = 0, HalfSize = 50, Weight = 1 },
            new ZoneDesc { CenterX = -200, CenterZ = 0, HalfSize = 50, Weight = 1 },
        ];
        d.RoadProfiles = cat;
        d.WorldGridOriginX = -4f;
        d.WorldGridOriginZ = 12f;
        return d;
    }

    /// <summary>Сценарий <c>scenarios/&lt;name&gt;.json</c> режима жителей по профилям фикстуры (улицы <c>street_2</c>, артерии <c>arterial_4</c>): участки на мировой сетке (RP5).</summary>
    public static LoadedScenario Res(string name = "s1k-res")
    {
        string root = GenFixture.RepoRoot();
        JsonNode n = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", name + ".json")))!;
        n["road_profiles_file"] = "road-profiles.json";
        n["city"]!["profiles"] = new JsonObject { ["street"] = "street_2", ["arterial"] = "arterial_4" };
        n["capacities"]!["max_lots"] = 12_000;   // участки на сетке мельче и плотнее прежних: запас по ёмкости
        n["capacities"]!["max_buildings"] = 9_000;
        return ScenarioLoader.Parse(
            n.ToJsonString(),
            rel => File.ReadAllText(Path.IsPathRooted(rel) ? rel : Path.Combine(root, "scenarios", rel)),
            name + "-prof",
            null,
            new ScenarioOverrides { Profiles = FixturePath });
    }

    /// <summary>
    /// Сценарий <c>*-res</c> в режиме legacy (участки вдоль дороги): из файла убраны <c>road_profiles_file</c> и <c>city.profiles</c>. Для тестов, чьи допуски
    /// откалиброваны на прежних участках (RP8: после перехода файлов на профили это отдельный открытый вопрос, см. road-profiles.md «RP8: замеры»).
    /// </summary>
    public static ScenarioDesc LegacyRes(string name = "s1k-res")
    {
        string root = GenFixture.RepoRoot();
        JsonNode n = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", name + ".json")))!;
        n.AsObject().Remove("road_profiles_file");
        n["city"]!.AsObject().Remove("profiles");
        return ScenarioLoader.Parse(
            n.ToJsonString(),
            rel => File.ReadAllText(Path.IsPathRooted(rel) ? rel : Path.Combine(root, "scenarios", rel)),
            name,
            null,
            new ScenarioOverrides()).Desc;
    }

    /// <summary>s1k с профилями фикстуры: улицы <c>street_2</c>, артерии <c>arterial_4</c>.</summary>
    public static LoadedScenario S1k()
    {
        string root = GenFixture.RepoRoot();
        JsonNode n = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", "s1k.json")))!;
        n["road_profiles_file"] = "road-profiles.json";
        n["city"]!["profiles"] = new JsonObject { ["street"] = "street_2", ["arterial"] = "arterial_4" };
        return ScenarioLoader.Parse(
            n.ToJsonString(),
            rel => File.ReadAllText(Path.IsPathRooted(rel) ? rel : Path.Combine(root, "scenarios", rel)),
            "s1k-prof",
            null,
            new ScenarioOverrides { Profiles = FixturePath });
    }
}
