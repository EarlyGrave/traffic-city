using System.Collections.Generic;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Scenarios.Mods;
using AssetId = Sim.Scenarios.Mods.AssetId;
using ModAssetKind = Sim.Scenarios.Mods.AssetKind;

namespace Sim.Scenarios;

// Секция city.services (services.md §2.1, §6): режим служб поверх city.lifecycle. Записи - из каталога пакетов (v3, services.json), в v2 - встроенная запись utility ядра.
public static partial class ScenarioLoader
{
    private const string ServicesKey = "services";

    private static void ParseServices(JsonObjectReader? r, ref CityDesc town, ContentCatalog? catalog)
    {
        if (r == null)
        {
            return;
        }

        CityServicesDesc d = CityServicesDesc.Default();
        d.Enabled = r.Bool("enabled", true);
        d.MaxServiceSites = r.Int("max_service_sites", d.MaxServiceSites, min: 1, max: 65_536);
        d.MaxCalls = r.Int("max_calls", 0, min: 0, max: 1 << 20);
        d.MaxFires = r.Int("max_fires", d.MaxFires, min: 0, max: 65_536);
        d.DisorderIllPct = r.Int("disorder_ill_pct", d.DisorderIllPct, min: 0, max: 1_000);
        d.FireRiskPct = r.Int("fire_risk_pct", d.FireRiskPct, min: 0, max: 1_000);
        d.SmolderS = r.Int("smolder_s", d.SmolderS, min: 0, max: 86_400);
        d.FireCap = r.Int("fire_cap", d.FireCap, min: 0, max: 65_536);
        d.FireSpreadPpm = r.Int("fire_spread_ppm", d.FireSpreadPpm, min: 0, max: 1_000_000);
        d.AnnexBuyPct = r.Int("annex_buy_pct", d.AnnexBuyPct, min: 0, max: 1_000);
        JsonObjectReader? inc = r.OptObject("incidence_ppm");
        if (inc != null)
        {
            int[] adult = IntTriple(inc, "adult", d.IllLightPpm, d.IllInpatientPpm, d.IllCriticalPpm, 0, 1_000_000);
            (d.IllLightPpm, d.IllInpatientPpm, d.IllCriticalPpm) = (adult[0], adult[1], adult[2]);
            inc.Finish();
        }

        int[] days = IntTriple(r, "ill_days", d.IllDaysLight, d.IllDaysInpatient, d.IllDaysCritical, 1, 31);
        (d.IllDaysLight, d.IllDaysInpatient, d.IllDaysCritical) = (days[0], days[1], days[2]);
        HashSet<AssetId>? off = null;
        JsonElement[]? offA = r.OptArray("services_off");
        if (offA != null)
        {
            off = new HashSet<AssetId>();
            for (int i = 0; i < offA.Length; i++)
            {
                string p = JsonObjectReader.Index(r.Path + ".services_off", i);
                if (offA[i].ValueKind != JsonValueKind.String || !AssetId.TryParse(offA[i].GetString(), out AssetId id) || id.Kind != ModAssetKind.Service)
                {
                    throw new ScenarioException($"{p}: expected a service id");
                }

                if (catalog == null || catalog.FindService(id) < 0)
                {
                    throw new ScenarioException($"{p}: service \"{id}\" is not loaded");
                }

                off.Add(id);
            }
        }

        r.Finish();
        if (!d.Enabled)
        {
            return;
        }

        if (!town.Lifecycle.Enabled)
        {
            throw new ScenarioException($"{r.Path}: requires city.lifecycle");
        }

        if (catalog != null)
        {
            catalog.FillServices(off, ref d);
        }
        else
        {
            // v2: типы зданий ядра, запись utility встроенная (= content/base/services.json, тест сверяет)
            BuildingTypeDesc[] types = town.BuildingTypes is { Length: > 0 } bt ? bt : CityDesc.BaseBuildingTypes();
            int depot = -1;
            for (int i = 0; i < types.Length && depot < 0; i++)
            {
                depot = types[i].ServiceKind == ServiceKind.Utility ? i : -1;
            }

            if (depot >= 0)
            {
                CityServicesDesc.BaseRecords(depot, types[depot].VehicleType, out d.Records, out d.Levels, out d.Calls);
            }
        }

        town.Services = d;
    }

    private static int[] IntTriple(JsonObjectReader r, string key, int a, int b, int c, int min, int max)
    {
        JsonElement[]? arr = r.OptArray(key);
        if (arr == null)
        {
            return new[] { a, b, c };
        }

        string p = r.Path + "." + key;
        var v = new int[3];
        if (arr.Length != 3)
        {
            throw new ScenarioException($"{p}: expected 3 numbers [light, inpatient, critical]");
        }

        for (int i = 0; i < 3; i++)
        {
            if (!arr[i].TryGetInt32(out v[i]) || v[i] < min || v[i] > max)
            {
                throw new ScenarioException($"{JsonObjectReader.Index(p, i)}: value out of range [{min}, {max}]");
            }
        }

        return v;
    }
}
