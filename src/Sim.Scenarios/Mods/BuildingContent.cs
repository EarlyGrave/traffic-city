using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sim.Core.Api;

namespace Sim.Scenarios.Mods;

/// <summary>
/// Разбор <c>buildings.json</c> пакета (<c>format_version</c> 1, zoning-demand.md §17, modding.md §3.3): <c>name</c> (id <c>&lt;пакет&gt;:building/&lt;имя&gt;</c>), <c>rev</c>, <c>kind</c>
/// (<c>residential | commercial | industrial | parking | service</c>), <c>density</c> (<c>low | high</c>), <c>capacity</c>, <c>shop_capacity</c>, <c>shift_start_slot</c>, <c>shift_slots</c>,
/// <c>construct_days</c>, <c>attract</c>, жизнь здания <c>decay_bp_day</c>, <c>ceil_drop_bp_day</c>, <c>demolish_cost</c> (building-lifecycle.md §2.2, §2.5; дефолты - по виду и плотности), служба (§2.10, BL3: <c>service</c> = <c>utility</c>, <c>build_cost</c>,
/// <c>vehicles</c>, <c>vehicle_type</c> - id типа ТС или имя в своём пакете) и <c>visual.model</c>. Ядру уходят только симуляционные поля (<see cref="BuildingTypeDesc"/>); строка визуала - индекс в каталоге.
/// </summary>
internal static class BuildingContent
{
    public const int CurrentFormatVersion = 1;

    public static List<BuildingEntry> Load(LoadedPackage pkg, string file, List<ModIssue> issues, Func<AssetId, int> findVehicle)
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
                    result.Add(ParseBuilding(pkg, arr[i], p, findVehicle));
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

    private static BuildingEntry ParseBuilding(LoadedPackage pkg, JsonElement el, string p, Func<AssetId, int> findVehicle)
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
            "parking" => LotZone.Parking,
            "service" => LotZone.Service,
            _ => throw new ScenarioException($"{p}.kind: expected \"residential\", \"commercial\", \"industrial\", \"parking\" or \"service\""),
        };
        string densityText = r.Str("density");
        byte density = densityText switch
        {
            "low" => 0,
            "high" => 1,
            _ => throw new ScenarioException($"{p}.density: expected \"low\" or \"high\""),
        };
        bool firm = kind is LotZone.Commercial or LotZone.Industrial;
        string accessText = r.Str("parking_access", "private");
        ParkingAccess access = accessText switch
        {
            "private" => ParkingAccess.Private,
            "public" => ParkingAccess.Public,
            _ => throw new ScenarioException($"{p}.parking_access: expected \"private\" or \"public\""),
        };
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
            ParkingCapacity = r.Int("parking", 0, min: 0, max: ModLimits.MaxBuildingCapacity),
            ParkingAccess = access,
            ParkingFee = r.Int("parking_fee", 0, min: 0, max: 1_000_000),
        };
        BuildingTypeDesc.LifeDefaults(kind, density, out ushort decay, out ushort ceilDrop, out int demolish);
        t.DecayBpDay = (ushort)r.Int("decay_bp_day", decay, min: 0, max: 10_000);
        t.CeilDropBpDay = (ushort)r.Int("ceil_drop_bp_day", ceilDrop, min: 0, max: 10_000);
        t.DemolishCost = r.Int("demolish_cost", demolish, min: 0, max: 100_000_000);
        t.BuildCost = r.Int("build_cost", 0, min: 0, max: 100_000_000);
        if (kind == LotZone.Service)
        {
            string svc = r.Str("service");
            t.ServiceKind = svc == "utility" ? ServiceKind.Utility : throw new ScenarioException($"{p}.service: expected \"utility\"");
            t.Vehicles = (byte)r.Int("vehicles", min: 1, max: 64);
            string vt = r.Str("vehicle_type");
            AssetId vid;
            if (!AssetId.TryParse(vt, out vid))
            {
                if (!AssetId.IsValidName(vt))
                {
                    throw new ScenarioException($"{p}.vehicle_type: expected a vehicle id or a vehicle name of this package");
                }

                vid = new AssetId(pkg.Id, AssetKind.Vehicle, vt);
            }

            int vi = vid.Kind == AssetKind.Vehicle ? findVehicle(vid) : -1;
            if (vi < 0 || vi > byte.MaxValue)
            {
                throw new ScenarioException($"{p}.vehicle_type: vehicle type \"{vt}\" is not loaded before this building");
            }

            t.VehicleType = (byte)vi;
        }

        string? model = VehicleContent.ReadVisual(r, "model");
        r.Finish();
        if (kind == LotZone.Service && t.Density != 0)
        {
            throw new ScenarioException($"{p}.density: service buildings are \"low\" only");
        }

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
