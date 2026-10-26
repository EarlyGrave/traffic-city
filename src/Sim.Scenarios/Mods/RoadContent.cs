using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sim.Core.Network;

namespace Sim.Scenarios.Mods;

/// <summary>Разбор <c>roads.json</c> пакета (<c>format_version</c> 1, modding.md §3.2). Ядру типы дорог не передаются.</summary>
internal static class RoadContent
{
    public const int CurrentFormatVersion = 1;

    public static List<RoadEntry> Load(LoadedPackage pkg, string file, List<ModIssue> issues)
    {
        var result = new List<RoadEntry>();
        JsonDocument? opened = ContentCatalog.OpenFile(pkg, file, issues);
        if (opened == null)
        {
            return result;
        }

        using JsonDocument doc = opened;
        try
        {
            var top = new JsonObjectReader(doc.RootElement, "$");
            if (!top.Has("format_version"))
            {
                throw new ScenarioException("$.format_version: required key is missing");
            }

            int fv = top.Int("format_version");
            if (fv != CurrentFormatVersion)
            {
                issues.Add(new ModIssue(pkg.Id, file, "$.format_version", ModError.UnsupportedFormatVersion, fv.ToString(CultureInfo.InvariantCulture)));
                return result;
            }

            JsonElement[] arr = top.ReqArray("roads");
            top.Finish();
            for (int i = 0; i < arr.Length; i++)
            {
                string p = JsonObjectReader.Index("$.roads", i);
                try
                {
                    result.Add(ParseRoad(pkg, arr[i], p));
                }
                catch (ScenarioException e)
                {
                    issues.Add(ContentCatalog.FromException(pkg, file, e));
                }
            }
        }
        catch (ScenarioException e)
        {
            issues.Add(ContentCatalog.FromException(pkg, file, e));
        }

        return result;
    }

    private static RoadEntry ParseRoad(LoadedPackage pkg, JsonElement el, string p)
    {
        var r = new JsonObjectReader(el, p);
        string name = r.Str("name");
        if (!AssetId.IsValidName(name))
        {
            throw new ScenarioException($"{p}.name: must match [a-z0-9_]{{1,64}}");
        }

        int rev = r.Int("rev", 1, min: 1);
        string kind = r.Str("kind");
        RoadKind k = kind switch
        {
            "street" => RoadKind.Street,
            "arterial" => RoadKind.Arterial,
            _ => throw new ScenarioException($"{p}.kind: expected \"street\" or \"arterial\""),
        };
        int ab = r.Int("lanes_ab", min: 0, max: 4);
        int ba = r.Int("lanes_ba", min: 0, max: 4);
        if (ab + ba == 0)
        {
            throw new ScenarioException($"{p}: lanes_ab + lanes_ba must be > 0");
        }

        float speed = ScenarioLoader.Mps(r.Num("speed_kmh", min: 10, max: 130));
        string? profile = VehicleContent.ReadVisual(r, "profile");
        r.Finish();
        var id = new AssetId(pkg.Id, AssetKind.Road, name);
        return new RoadEntry(id, rev, k, ab, ba, speed, profile);
    }
}
