using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.Tests.Gen;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>Сценарии прогрессии (PG1/PG2, progression.md §4.1): <c>scenarios/s1k-res-econ.json</c> с секцией <c>progression</c>, вставленной в JSON (через настоящий загрузчик).</summary>
internal static class ProgressionFixtures
{
    /// <summary>Секция по образцу §4.1: три вехи, ворота у второй и третьей, закрытые признаки и профили, две области.</summary>
    public const string Section = """
        {
          "enabled": true,
          "locked_features": ["arterial_upgrade", "bus_lines", "signals_adaptive", "curves", "bus_lanes", "parking_fees", "zoning"],
          "locked_profiles": ["arterial_6", "asym_1_2"],
          "min_legs": 50,
          "areas": [ { "min_x": -1700, "min_z": -1700, "max_x": 1700, "max_z": 1700 }, { "min_x": -2300, "min_z": -2300, "max_x": 2300, "max_z": 2300 } ],
          "milestones": [
            { "id": "m1", "population": 4000, "unlock": ["arterial_upgrade", "bus_lines"], "grant": 20000 },
            { "id": "m2", "population": 5000, "gates": [ { "kpi": "idle_pct", "max": 45 }, { "kpi": "travel_min", "max": 40.5 } ], "gates_any": true,
              "unlock": ["bus_lanes", "signals_adaptive"], "unlock_profiles": ["asym_1_2"], "credit_days": 10, "grant": 40000 },
            { "id": "m3", "population": 6000, "gates": [ { "kpi": "bus_share_pct", "min": 6 }, { "kpi": "satisfaction_pct", "min": 90 } ],
              "unlock": ["parking_fees", "zoning", "curves"], "unlock_profiles": ["arterial_6"], "area": 1, "grant": 60000 }
          ]
        }
        """;

    public static string ScenariosDir => Path.Combine(GenFixture.RepoRoot(), "scenarios");

    /// <summary>Текст <c>s1k-res-econ.json</c> с подменённой (или удалённой при <paramref name="section"/> = null) секцией <c>progression</c>.</summary>
    public static string EconJson(string? section)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(ScenariosDir, "s1k-res-econ.json")))!;
        JsonObject o = root.AsObject();
        o.Remove("progression");
        if (section != null)
        {
            o["progression"] = JsonNode.Parse(section);
        }

        return root.ToJsonString();
    }

    public static LoadedScenario Parse(string json) =>
        ScenarioLoader.Parse(json, rel => File.ReadAllText(Path.Combine(ScenariosDir, rel)), "s1k-res-econ-prog", null, ScenarioLoader.DefaultOverrides);

    /// <summary>s1k-res-econ с секцией <see cref="Section"/>; <paramref name="microDay"/> &gt; 0 - короткие сутки микро-агентов (игровой час = DayTicks / 24).</summary>
    public static LoadedScenario Econ(int microDay = 0, string section = Section)
    {
        LoadedScenario sc = Parse(EconJson(section));
        if (microDay > 0)
        {
            ScenarioDesc d = sc.Desc;
            d.Town.Calendar.DayTicks = microDay;
            return new LoadedScenario(sc.Name, sc.TypeNames, d, sc.Catalog, sc.Packages, sc.Timeline) { MilestoneIds = sc.MilestoneIds };
        }

        return sc;
    }
}
