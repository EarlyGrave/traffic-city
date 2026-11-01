using System;
using System.Collections.Generic;
using System.Text.Json;
using Sim.Core.Api;

namespace Sim.Scenarios;

// Секция сценария progression (docs/design/progression.md §4.1, ADR-0034, задача PG1): вехи, ворота по показателям, закрытые признаки и профили, области карты.
// Строки (id вех, имена признаков, профилей и показателей) живут только здесь: ядро получает индексы и POD. Пороги показателей - целые ×10 (минуты ×10, проценты ×10 = ‰).
public static partial class ScenarioLoader
{
    private const string ProgressionKey = "progression";

    /// <summary>Имена признаков <see cref="Feature"/> в JSON; индекс = номер признака.</summary>
    internal static readonly string[] FeatureNames =
    {
        "roads_basic", "arterial_upgrade", "curves", "signals_fixed", "signals_adaptive", "turn_rules", "bus_lines", "bus_lanes", "zoning", "taxes", "parking_fees",
    };

    /// <summary>Число имён признаков (сверка с <see cref="Feature"/> в тестах).</summary>
    public static int FeatureNamesCount => FeatureNames.Length;

    /// <summary>Имена показателей ворот <see cref="KpiKind"/> в JSON; индекс = номер (0 - нет).</summary>
    internal static readonly string[] KpiNames = { string.Empty, "travel_min", "idle_pct", "bus_share_pct", "satisfaction_pct", "work_min" };

    private const int MaxMilestones = 32;
    private const int MaxAreas = 8;
    private const int MaxGates = 2;

    /// <summary>
    /// Секция <c>progression</c>: нет секции или <c>enabled: false</c> - прогрессии нет (нулевая структура, сценарий побитово прежний). Есть - только при <c>demand.model = "residents"</c>.
    /// Ошибки (опечатки): неизвестный признак, профиль, показатель или ключ; пороги населения не растут строго; вех больше 32, ворот больше 2, областей больше 8; <c>area</c> вне областей;
    /// признак или профиль, открываемый вехой, не закрыт в <c>locked_features</c>/<c>locked_profiles</c>; <c>roads_basic</c> закрыт; повтор <c>id</c>.
    /// </summary>
    private static ProgressionDesc ParseProgression(JsonObjectReader? r, bool residents, RoadProfilesDesc profiles, out string[] milestoneIds)
    {
        milestoneIds = Array.Empty<string>();
        if (r == null)
        {
            return default;
        }

        if (!residents)
        {
            throw new ScenarioException($"{r.Path}: allowed only with demand.model = \"residents\"");
        }

        bool enabled = r.Bool("enabled", true);
        var d = new ProgressionDesc { Enabled = enabled, MinLegs = r.Int("min_legs", 500, min: 0, max: 100_000_000) };

        ulong locked = 0;
        foreach (string name in StrArray(r, "locked_features"))
        {
            int f = FeatureOf(name, r.ChildPath("locked_features"));
            if (f == (int)Feature.RoadsBasic)
            {
                throw new ScenarioException($"{r.ChildPath("locked_features")}: \"roads_basic\" is always open");
            }

            if ((locked & (1UL << f)) != 0)
            {
                throw new ScenarioException($"{r.ChildPath("locked_features")}: duplicate \"{name}\"");
            }

            locked |= 1UL << f;
        }

        d.LockedFeatures = locked;
        var lockedProfiles = new List<int>();
        foreach (string id in StrArray(r, "locked_profiles"))
        {
            int p = ProfileOf(profiles, id, r.ChildPath("locked_profiles"));
            if (lockedProfiles.Contains(p))
            {
                throw new ScenarioException($"{r.ChildPath("locked_profiles")}: duplicate \"{id}\"");
            }

            lockedProfiles.Add(p);
        }

        d.LockedProfiles = lockedProfiles.ToArray();
        d.Areas = ParseAreas(r);
        (d.Milestones, d.ProfileIdx, milestoneIds) = ParseMilestones(r, locked, lockedProfiles, d.Areas.Length, profiles);
        r.Finish();
        if (!enabled)
        {
            milestoneIds = Array.Empty<string>();
            return default;
        }

        return d;
    }

    private static AreaRect[] ParseAreas(JsonObjectReader r)
    {
        JsonElement[]? arr = r.OptArray("areas");
        if (arr == null)
        {
            return Array.Empty<AreaRect>();
        }

        if (arr.Length > MaxAreas)
        {
            throw new ScenarioException($"{r.ChildPath("areas")}: at most {MaxAreas} areas");
        }

        var areas = new AreaRect[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var a = new JsonObjectReader(arr[i], JsonObjectReader.Index(r.ChildPath("areas"), i));
            areas[i] = new AreaRect
            {
                MinX = (float)a.Num("min_x"),
                MinZ = (float)a.Num("min_z"),
                MaxX = (float)a.Num("max_x"),
                MaxZ = (float)a.Num("max_z"),
            };
            a.Finish();
            if (!(areas[i].MinX <= areas[i].MaxX && areas[i].MinZ <= areas[i].MaxZ))
            {
                throw new ScenarioException($"{a.Path}: expected min_x <= max_x and min_z <= max_z");
            }
        }

        return areas;
    }

    private static (MilestoneDesc[] Milestones, int[] ProfileIdx, string[] Ids) ParseMilestones(
        JsonObjectReader r, ulong locked, List<int> lockedProfiles, int areaCount, RoadProfilesDesc profiles)
    {
        JsonElement[] arr = r.OptArray("milestones") ?? Array.Empty<JsonElement>();
        if (arr.Length > MaxMilestones)
        {
            throw new ScenarioException($"{r.ChildPath("milestones")}: at most {MaxMilestones} milestones");
        }

        var ms = new MilestoneDesc[arr.Length];
        var ids = new string[arr.Length];
        var profileIdx = new List<int>();
        int prev = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var m = new JsonObjectReader(arr[i], JsonObjectReader.Index(r.ChildPath("milestones"), i));
            string id = m.Str("id");
            if (id.Length == 0 || Array.IndexOf(ids, id, 0, i) >= 0)
            {
                throw new ScenarioException($"{m.ChildPath("id")}: empty or duplicate milestone id \"{id}\"");
            }

            ids[i] = id;
            int pop = m.Int("population", min: 1);
            if (pop <= prev)
            {
                throw new ScenarioException($"{m.ChildPath("population")}: populations must strictly increase ({pop} after {prev})");
            }

            prev = pop;
            MilestoneDesc md = default;
            md.Population = pop;
            md.Grant = m.Int("grant", 0, min: 0);
            md.CreditDays = (short)m.Int("credit_days", 0, min: 0, max: short.MaxValue);
            md.Area = (sbyte)m.Int("area", -1, min: -1, max: areaCount - 1);
            if (m.Bool("gates_any", false))
            {
                md.Flags |= MilestoneFlags.GatesAny;
            }

            JsonElement[] gates = m.OptArray("gates") ?? Array.Empty<JsonElement>();
            if (gates.Length > MaxGates)
            {
                throw new ScenarioException($"{m.ChildPath("gates")}: at most {MaxGates} gates");
            }

            if (gates.Length > 0)
            {
                md.G0 = ParseGate(gates[0], JsonObjectReader.Index(m.ChildPath("gates"), 0));
            }

            if (gates.Length > 1)
            {
                md.G1 = ParseGate(gates[1], JsonObjectReader.Index(m.ChildPath("gates"), 1));
            }

            foreach (string name in StrArray(m, "unlock"))
            {
                int f = FeatureOf(name, m.ChildPath("unlock"));
                if ((locked & (1UL << f)) == 0)
                {
                    throw new ScenarioException($"{m.ChildPath("unlock")}: \"{name}\" is not in locked_features (typo?)");
                }

                md.Features |= 1UL << f;
            }

            md.ProfileFirst = profileIdx.Count;
            foreach (string pid in StrArray(m, "unlock_profiles"))
            {
                int p = ProfileOf(profiles, pid, m.ChildPath("unlock_profiles"));
                if (!lockedProfiles.Contains(p))
                {
                    throw new ScenarioException($"{m.ChildPath("unlock_profiles")}: \"{pid}\" is not in locked_profiles (typo?)");
                }

                profileIdx.Add(p);
            }

            md.ProfileCount = profileIdx.Count - md.ProfileFirst;
            m.Finish();
            ms[i] = md;
        }

        return (ms, profileIdx.ToArray(), ids);
    }

    private static KpiGate ParseGate(JsonElement e, string path)
    {
        var g = new JsonObjectReader(e, path);
        string name = g.Str("kpi");
        int kpi = Array.IndexOf(KpiNames, name);
        if (kpi <= 0)
        {
            throw new ScenarioException($"{g.ChildPath("kpi")}: unknown kpi \"{name}\" (expected {string.Join(", ", KpiNames[1..])})");
        }

        bool hasMin = g.Has("min");
        bool hasMax = g.Has("max");
        if (hasMin == hasMax)
        {
            throw new ScenarioException($"{path}: exactly one of \"min\" or \"max\" is required");
        }

        double v = g.Num(hasMin ? "min" : "max", min: 0, max: 1_000_000);
        g.Finish();
        return new KpiGate { Kpi = (KpiKind)kpi, IsMin = hasMin ? (byte)1 : (byte)0, Value = (int)Math.Round(v * 10.0, MidpointRounding.AwayFromZero) };
    }

    private static int FeatureOf(string name, string path)
    {
        int f = Array.IndexOf(FeatureNames, name);
        return f >= 0 ? f : throw new ScenarioException($"{path}: unknown feature \"{name}\" (expected {string.Join(", ", FeatureNames)})");
    }

    private static int ProfileOf(RoadProfilesDesc profiles, string id, string path)
    {
        RoadProfileDesc[]? ps = profiles.Profiles;
        for (int i = 0; ps != null && i < ps.Length; i++)
        {
            if (ps[i].Id == id)
            {
                return i;
            }
        }

        throw new ScenarioException($"{path}: unknown road profile \"{id}\"" + (ps == null ? " (no road_profiles catalog)" : string.Empty));
    }

    private static string[] StrArray(JsonObjectReader r, string key)
    {
        JsonElement[]? arr = r.OptArray(key);
        if (arr == null)
        {
            return Array.Empty<string>();
        }

        var s = new string[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].ValueKind != JsonValueKind.String)
            {
                throw new ScenarioException($"{JsonObjectReader.Index(r.ChildPath(key), i)}: expected string");
            }

            s[i] = arr[i].GetString()!;
        }

        return s;
    }
}
