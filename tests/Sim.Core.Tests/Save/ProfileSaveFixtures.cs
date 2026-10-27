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
