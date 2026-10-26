using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sim.Core.Api;

namespace Sim.Scenarios.Mods;

/// <summary>Разбор <c>vehicles.json</c> пакета (<c>format_version</c> 2; 1 поднимается, modding.md §3.1, §8).</summary>
internal static class VehicleContent
{
    public const int CurrentFormatVersion = 2;

    public static List<VehicleEntry> Load(LoadedPackage pkg, string file, List<ModIssue> issues)
    {
        var result = new List<VehicleEntry>();
        JsonDocument? opened = ContentCatalog.OpenFile(pkg, file, issues);
        if (opened == null)
        {
            return result;
        }

        using JsonDocument doc = opened;
        JsonElement rootEl = doc.RootElement;
        try
        {
            int fv = ReadVersion(rootEl);
            if (fv is < 1 or > CurrentFormatVersion)
            {
                issues.Add(new ModIssue(pkg.Id, file, "$.format_version", ModError.UnsupportedFormatVersion, fv.ToString(CultureInfo.InvariantCulture)));
                return result;
            }

            if (fv == 1)
            {
                JsonNode up = FormatUpgrades.VehiclesV1ToV2(JsonNode.Parse(rootEl.GetRawText())!);
                using JsonDocument upDoc = JsonDocument.Parse(up.ToJsonString());
                Parse(pkg, file, upDoc.RootElement, result, issues);
            }
            else
            {
                Parse(pkg, file, rootEl, result, issues);
            }
        }
        catch (ScenarioException e)
        {
            issues.Add(ContentCatalog.FromException(pkg, file, e));
        }

        return result;
    }

    private static int ReadVersion(JsonElement root)
    {
        var r = new JsonObjectReader(root, "$");
        if (!r.Has("format_version"))
        {
            throw new ScenarioException("$.format_version: required key is missing");
        }

        return r.Int("format_version");
    }

    private static void Parse(LoadedPackage pkg, string file, JsonElement root, List<VehicleEntry> result, List<ModIssue> issues)
    {
        var top = new JsonObjectReader(root, "$");
        top.Int("format_version");
        JsonElement[] arr = top.ReqArray("types");
        top.Finish();
        for (int i = 0; i < arr.Length; i++)
        {
            string p = JsonObjectReader.Index("$.types", i);
            try
            {
                var r = new JsonObjectReader(arr[i], p);
                string name = r.Str("name");
                if (!AssetId.IsValidName(name))
                {
                    throw new ScenarioException($"{p}.name: must match [a-z0-9_]{{1,64}}");
                }

                int rev = r.Int("rev", 1, min: 1);
                string? model = ReadVisual(r, "model");
                VehicleTypeDesc t = ScenarioLoader.ParseVehicleFields(r, p);
                r.Finish();
                CheckRanges(p, t);
                var id = new AssetId(pkg.Id, AssetKind.Vehicle, name);
                if (System.Text.Encoding.UTF8.GetByteCount(id.ToString()) > ModLimits.MaxAssetIdBytes)
                {
                    throw new ScenarioException($"{p}.name: asset id longer than {ModLimits.MaxAssetIdBytes} bytes");
                }

                result.Add(new VehicleEntry(id, rev, t, model));
            }
            catch (ScenarioException e)
            {
                issues.Add(ContentCatalog.FromException(pkg, file, e));
            }
        }
    }

    internal static string? ReadVisual(JsonObjectReader r, string key)
    {
        JsonObjectReader? v = r.OptObject("visual");
        if (v == null)
        {
            return null;
        }

        string? value = v.Has(key) ? v.Str(key) : null;
        v.Finish();
        if (value != null && !SafePath.IsSafeRelative(value))
        {
            throw new ScenarioException($"{v.ChildPath(key)}: unsafe path");
        }

        return value;
    }

    // modding.md §3.1: IDM/MOBIL проверены ParseVehicleFields.
    private static void CheckRanges(string p, in VehicleTypeDesc t)
    {
        Range($"{p}.length_m", t.Length, 2, 30);
        Range($"{p}.width_m", t.Width, 1, 3.5);
        Range($"{p}.max_speed_kmh", t.MaxSpeed * 3.6, 5 - 1e-4, 200 + 1e-4);
        Range($"{p}.speed_factor", t.SpeedFactor, 0.5, 1.5);
    }

    private static void Range(string path, double v, double min, double max)
    {
        if (v < min || v > max)
        {
            throw new ScenarioException($"{path}: value {v.ToString("R", CultureInfo.InvariantCulture)} out of range [{min.ToString(CultureInfo.InvariantCulture)}, {max.ToString(CultureInfo.InvariantCulture)}]");
        }
    }
}
