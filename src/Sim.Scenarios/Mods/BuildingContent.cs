using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sim.Core.Api;

namespace Sim.Scenarios.Mods;

/// <summary>
/// Разбор <c>buildings.json</c> пакета (<c>format_version</c> 1, zoning-demand.md §17, modding.md §3.3): <c>name</c> (id <c>&lt;пакет&gt;:building/&lt;имя&gt;</c>), <c>rev</c>, <c>kind</c>
/// (<c>residential | commercial | industrial</c>), <c>density</c> (<c>low | high</c>), <c>capacity</c>, <c>shop_capacity</c>, <c>shift_start_slot</c>, <c>shift_slots</c>,
/// <c>construct_days</c>, <c>attract</c> и <c>visual.model</c>. Ядру уходят только симуляционные поля (<see cref="BuildingTypeDesc"/>); строка визуала - индекс в каталоге.
/// </summary>
internal static class BuildingContent
{
    public const int CurrentFormatVersion = 1;

    public static List<BuildingEntry> Load(LoadedPackage pkg, string file, List<ModIssue> issues)
    {
        var result = new List<BuildingEntry>();
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

            JsonElement[] arr = top.ReqArray("buildings");
            top.Finish();
            for (int i = 0; i < arr.Length; i++)
            {
                string p = JsonObjectReader.Index("$.buildings", i);
                try
                {
                    result.Add(ParseBuilding(pkg, arr[i], p));
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

    private static BuildingEntry ParseBuilding(LoadedPackage pkg, JsonElement el, string p)
    {
        var r = new JsonObjectReader(el, p);
        string name = r.Str("name");
        if (!AssetId.IsValidName(name))
        {
            throw new ScenarioException($"{p}.name: must match [a-z0-9_]{{1,64}}");
        }

        int rev = r.Int("rev", 1, min: 1);
        string kindText = r.Str("kind");
        LotZone kind = kindText switch
        {
            "residential" => LotZone.Residential,
            "commercial" => LotZone.Commercial,
            "industrial" => LotZone.Industrial,
            _ => throw new ScenarioException($"{p}.kind: expected \"residential\", \"commercial\" or \"industrial\""),
        };
        string densityText = r.Str("density");
        byte density = densityText switch
        {
            "low" => 0,
            "high" => 1,
            _ => throw new ScenarioException($"{p}.density: expected \"low\" or \"high\""),
        };
        bool firm = kind != LotZone.Residential;
        var t = new BuildingTypeDesc
        {
            Kind = kind,
            Density = density,
            Capacity = r.Int("capacity", min: 1, max: ModLimits.MaxBuildingCapacity),
            ShopCapacity = r.Int("shop_capacity", 0, min: 0, max: 1_000_000),
            ShiftStartSlot = r.Int("shift_start_slot", firm ? 96 : 0, min: 0, max: 287),
            ShiftSlots = r.Int("shift_slots", firm ? 96 : 0, min: 0, max: 288),
            ConstructDays = r.Int("construct_days", 3, min: 0, max: 3650),
            AttractQ8 = CityDesc.Q8(r.Num("attract", kind == LotZone.Commercial ? 1.0 : 0.0, min: 0, max: 4096)),
        };
        string? model = VehicleContent.ReadVisual(r, "model");
        r.Finish();
        if (kind != LotZone.Commercial && t.ShopCapacity != 0)
        {
            throw new ScenarioException($"{p}.shop_capacity: allowed only for commercial buildings");
        }

        if (firm && t.ShiftSlots < 1)
        {
            throw new ScenarioException($"{p}.shift_slots: value {t.ShiftSlots.ToString(CultureInfo.InvariantCulture)} out of range [1, 288] for commercial and industrial buildings");
        }

        var id = new AssetId(pkg.Id, AssetKind.Building, name);
        if (System.Text.Encoding.UTF8.GetByteCount(id.ToString()) > ModLimits.MaxAssetIdBytes)
        {
            throw new ScenarioException($"{p}.name: asset id longer than {ModLimits.MaxAssetIdBytes.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        return new BuildingEntry(id, rev, t, model);
    }
}
