using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sim.Core.Api;

namespace Sim.Scenarios.Mods;

/// <summary>
/// Запись службы каталога (services.md §2.2): id <c>&lt;пакет&gt;:service/&lt;имя&gt;</c>, ревизия, заголовок, счёт и свои таблицы - уровни (апгрейды уровня - диапазоном
/// <see cref="ServiceLevelDesc.FirstUpgrade"/> в <see cref="Upgrades"/> записи), выездные вызовы, поводы визита. Индексы типов зданий и ТС - каталожные.
/// </summary>
public sealed record ServiceEntry(
    AssetId Id, int Rev, string? Title, ServiceFund Fund, ServiceLevelDesc[] Levels, ServiceUpgradeDesc[] Upgrades, ServiceCallDesc[] Calls, ServiceVisitDesc[] Visits);

/// <summary>
/// Разбор <c>services.json</c> пакета (<c>format_version</c> 1, services.md §2.2, modding.md §3): <c>id</c> (имя записи), <c>rev</c>, <c>title</c>, <c>fund</c> (<c>city | utility</c>),
/// <c>levels[]</c> (1-4: <c>type</c> - id здания вида <c>service</c> или имя в своём пакете, <c>role[]</c>, <c>lot_min</c>, цены, <c>milestone</c>, <c>visit</c>, <c>dispatch</c>, <c>upgrades[]</c>),
/// <c>calls[]</c>, <c>visits[]</c>. Правила валидатора: <c>on_call</c> у <c>repair</c> - ошибка; машина выезда - класса <c>special</c>; роли уровня включают роли предыдущего;
/// <c>deadline_s</c> 0 или не меньше <c>onsite_s</c>; вид вызова и повод визита в записи - не больше одного раза.
/// </summary>
internal static class ServiceContent
{
    public const int CurrentFormatVersion = 1;

    private const int MaxCallsPerRecord = 3;
    private const int MaxVisitsPerRecord = 2;

    public static List<ServiceEntry> Load(LoadedPackage pkg, string file, List<ModIssue> issues, ContentCatalog cat)
    {
        var result = new List<ServiceEntry>();
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

            JsonElement[] arr = top.ReqArray("services");
            top.Finish();
            for (int i = 0; i < arr.Length; i++)
            {
                string p = JsonObjectReader.Index("$.services", i);
                try
                {
                    result.Add(ParseService(pkg, arr[i], p, cat));
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

    private static ServiceEntry ParseService(LoadedPackage pkg, JsonElement el, string p, ContentCatalog cat)
    {
        var r = new JsonObjectReader(el, p);
        string name = r.Str("id");
        if (!AssetId.IsValidName(name))
        {
            throw new ScenarioException($"{p}.id: must match [a-z0-9_]{{1,64}}");
        }

        int rev = r.Int("rev", 1, min: 1);
        string? title = r.Has("title") ? r.Str("title") : null;
        if (title != null && System.Text.Encoding.UTF8.GetByteCount(title) > ModLimits.MaxTitleBytes)
        {
            throw new ScenarioException($"{p}.title: longer than {ModLimits.MaxTitleBytes.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        ServiceFund fund = r.Str("fund") switch
        {
            "city" => ServiceFund.City,
            "utility" => ServiceFund.Utility,
            _ => throw new ScenarioException($"{p}.fund: expected \"city\" or \"utility\""),
        };

        var calls = new List<ServiceCallDesc>();
        JsonElement[] ca = r.OptArray("calls") ?? [];
        for (int i = 0; i < ca.Length; i++)
        {
            calls.Add(ParseCall(ca[i], JsonObjectReader.Index(p + ".calls", i), calls));
        }

        var visits = new List<ServiceVisitDesc>();
        JsonElement[] va = r.OptArray("visits") ?? [];
        for (int i = 0; i < va.Length; i++)
        {
            visits.Add(ParseVisit(va[i], JsonObjectReader.Index(p + ".visits", i), visits));
        }

        JsonElement[] la = r.ReqArray("levels");
        if (la.Length is < 1 or > CityServicesDesc.MaxLevels)
        {
            throw new ScenarioException($"{p}.levels: {la.Length.ToString(CultureInfo.InvariantCulture)} levels out of range [1, {CityServicesDesc.MaxLevels.ToString(CultureInfo.InvariantCulture)}]");
        }

        var levels = new ServiceLevelDesc[la.Length];
        var upgrades = new List<ServiceUpgradeDesc>();
        for (int i = 0; i < la.Length; i++)
        {
            string lp = JsonObjectReader.Index(p + ".levels", i);
            levels[i] = ParseLevel(pkg, la[i], lp, cat, upgrades);
            if (i > 0 && (levels[i].Roles & levels[i - 1].Roles) != levels[i - 1].Roles)
            {
                throw new ScenarioException($"{lp}.role: must include the roles of the previous level");
            }
        }

        r.Finish();
        var id = new AssetId(pkg.Id, AssetKind.Service, name);
        if (System.Text.Encoding.UTF8.GetByteCount(id.ToString()) > ModLimits.MaxAssetIdBytes)
        {
            throw new ScenarioException($"{p}.id: asset id longer than {ModLimits.MaxAssetIdBytes.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        return new ServiceEntry(id, rev, title, fund, levels, upgrades.ToArray(), calls.ToArray(), visits.ToArray());
    }

    private static ServiceCallDesc ParseCall(JsonElement el, string p, List<ServiceCallDesc> prev)
    {
        var r = new JsonObjectReader(el, p);
        ServiceCallKind kind = r.Str("kind") switch
        {
            "repair" => ServiceCallKind.Repair,
            "critical" => ServiceCallKind.Critical,
            "fire" => ServiceCallKind.Fire,
            _ => throw new ScenarioException($"{p}.kind: expected \"repair\", \"critical\" or \"fire\""),
        };
        byte flags = 0;
        flags |= r.Bool("urgent", false) ? ServiceCallFlags.Urgent : (byte)0;
        flags |= r.Bool("on_call", false) ? ServiceCallFlags.OnCall : (byte)0;
        flags |= r.Bool("on_call_return", false) ? ServiceCallFlags.OnCallReturn : (byte)0;
        var c = new ServiceCallDesc
        {
            Kind = kind,
            Flags = flags,
            OnsiteS = r.Int("onsite_s", min: 0, max: 86_400),
            DeadlineS = r.Int("deadline_s", 0, min: 0, max: 86_400),
            PerCall = r.Int("per_call", 0, min: 0, max: 100_000_000),
        };
        r.Finish();
        if (kind == ServiceCallKind.Repair && (flags & (ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn)) != 0)
        {
            throw new ScenarioException($"{p}.on_call: not allowed for repair calls (utility crews never get priority)");
        }

        if (c.DeadlineS != 0 && c.DeadlineS < c.OnsiteS)
        {
            throw new ScenarioException($"{p}.deadline_s: must be 0 or not less than onsite_s");
        }

        foreach (ServiceCallDesc q in prev)
        {
            if (q.Kind == kind)
            {
                throw new ScenarioException($"{p}.kind: duplicate call kind in the record");
            }
        }

        if (prev.Count >= MaxCallsPerRecord)
        {
            throw new ScenarioException($"{p}: too many calls in the record");
        }

        return c;
    }

    private static ServiceVisitDesc ParseVisit(JsonElement el, string p, List<ServiceVisitDesc> prev)
    {
        var r = new JsonObjectReader(el, p);
        ServiceVisitReason reason = r.Str("reason") switch
        {
            "light" => ServiceVisitReason.Light,
            "inpatient" => ServiceVisitReason.Inpatient,
            _ => throw new ScenarioException($"{p}.reason: expected \"light\" or \"inpatient\""),
        };
        var v = new ServiceVisitDesc
        {
            Reason = reason,
            MinRole = Role(r.Str("min_role"), p + ".min_role"),
            PerVisit = r.Int("per_visit", 0, min: 0, max: 100_000_000),
            MaxCostS = r.Int("max_cost_s", min: 0, max: 86_400),
        };
        r.Finish();
        foreach (ServiceVisitDesc q in prev)
        {
            if (q.Reason == reason)
            {
                throw new ScenarioException($"{p}.reason: duplicate visit reason in the record");
            }
        }

        if (prev.Count >= MaxVisitsPerRecord)
        {
            throw new ScenarioException($"{p}: too many visits in the record");
        }

        return v;
    }

    private static ServiceLevelDesc ParseLevel(LoadedPackage pkg, JsonElement el, string p, ContentCatalog cat, List<ServiceUpgradeDesc> upgrades)
    {
        var r = new JsonObjectReader(el, p);
        AssetId bid = Ref(pkg, r.Str("type"), AssetKind.Building, p + ".type");
        int type = cat.FindBuilding(bid);
        if (type < 0 || cat.Buildings[type].Desc.Kind != LotZone.Service)
        {
            throw new ScenarioException($"{p}.type: building type \"{bid}\" is not a service building loaded before this record");
        }

        ServiceRole roles = ServiceRole.None;
        JsonElement[] ra = r.ReqArray("role");
        for (int i = 0; i < ra.Length; i++)
        {
            string rp = JsonObjectReader.Index(p + ".role", i);
            if (ra[i].ValueKind != JsonValueKind.String)
            {
                throw new ScenarioException($"{rp}: expected string");
            }

            roles |= Role(ra[i].GetString()!, rp);
        }

        if (roles == ServiceRole.None)
        {
            throw new ScenarioException($"{p}.role: at least one role is required");
        }

        var l = new ServiceLevelDesc
        {
            Type = type,
            Roles = roles,
            LotMinF = 1,
            LotMinD = 1,
            Milestone = (byte)r.Int("milestone", 0, min: 0, max: 15),
            BuildCost = r.Int("build_cost", 0, min: 0, max: 100_000_000),
            UpkeepDay = r.Int("upkeep_day", 0, min: 0, max: 100_000_000),
            DispatchVehicle = -1,
        };
        JsonElement[]? lot = r.OptArray("lot_min");
        if (lot != null)
        {
            if (lot.Length != 2 || !lot[0].TryGetInt32(out int f) || !lot[1].TryGetInt32(out int d) || f is < 1 or > 16 || d is < 1 or > 16)
            {
                throw new ScenarioException($"{p}.lot_min: expected [frontage, depth] in cells, each in [1, 16]");
            }

            l.LotMinF = (byte)f;
            l.LotMinD = (byte)d;
        }

        JsonObjectReader? visit = r.OptObject("visit");
        if (visit != null)
        {
            l.CapacityDay = visit.Int("capacity_day", 0, min: 0, max: 1_000_000);
            l.Beds = visit.Int("beds", 0, min: 0, max: 100_000);
            l.Icu = visit.Int("icu", 0, min: 0, max: 100_000);
            l.OpenSlot = (short)visit.Int("open_slot", 0, min: 0, max: 288);
            l.CloseSlot = (short)visit.Int("close_slot", 288, min: 0, max: 288);
            l.StaySlots = (short)visit.Int("stay_slots", 6, min: 0, max: 288);
            visit.Finish();
        }

        JsonObjectReader? dispatch = r.OptObject("dispatch");
        if (dispatch != null)
        {
            string vp = p + ".dispatch.vehicle";
            AssetId vid = Ref(pkg, dispatch.Str("vehicle"), AssetKind.Vehicle, vp);
            int vi = cat.FindVehicle(vid);
            if (vi < 0 || vi > short.MaxValue)
            {
                throw new ScenarioException($"{vp}: vehicle type \"{vid}\" is not loaded before this record");
            }

            if (cat.Vehicles[vi].Desc.Category != VehicleCategory.Special)
            {
                throw new ScenarioException($"{vp}: vehicle type \"{vid}\" must be of category special");
            }

            l.DispatchVehicle = (short)vi;
            l.Units = dispatch.Int("units", min: 1, max: 64);
            l.PerUnitDay = dispatch.Int("per_unit_day", 0, min: 0, max: 100_000_000);
            dispatch.Finish();
        }

        JsonElement[] ua = r.OptArray("upgrades") ?? [];
        if (ua.Length > CityServicesDesc.MaxUpgrades)
        {
            throw new ScenarioException($"{p}.upgrades: {ua.Length.ToString(CultureInfo.InvariantCulture)} upgrades, limit {CityServicesDesc.MaxUpgrades.ToString(CultureInfo.InvariantCulture)}");
        }

        l.FirstUpgrade = upgrades.Count;
        l.UpgradeCount = ua.Length;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ua.Length; i++)
        {
            string up = JsonObjectReader.Index(p + ".upgrades", i);
            var u = new JsonObjectReader(ua[i], up);
            string uid = u.Str("id");
            if (!AssetId.IsValidName(uid) || !ids.Add(uid))
            {
                throw new ScenarioException($"{up}.id: must match [a-z0-9_]{{1,64}} and be unique in the level");
            }

            upgrades.Add(new ServiceUpgradeDesc
            {
                Cost = u.Int("cost", 0, min: 0, max: 100_000_000),
                UpkeepDay = u.Int("upkeep_day", 0, min: 0, max: 100_000_000),
                Milestone = (byte)u.Int("milestone", 0, min: 0, max: 15),
                Effect = u.Str("effect") switch
                {
                    "units" => ServiceUpgradeEffect.Units,
                    "visit_capacity" => ServiceUpgradeEffect.VisitCapacity,
                    "beds" => ServiceUpgradeEffect.Beds,
                    "icu" => ServiceUpgradeEffect.Icu,
                    "onsite_pct" => ServiceUpgradeEffect.OnsitePct,
                    _ => throw new ScenarioException($"{up}.effect: expected \"units\", \"visit_capacity\", \"beds\", \"icu\" or \"onsite_pct\""),
                },
                Value = u.Int("value", min: 0, max: 1_000_000),
            });
            u.Finish();
        }

        r.Finish();
        return l;
    }

    private static ServiceRole Role(string text, string p) => text switch
    {
        "repair" => ServiceRole.Repair,
        "light" => ServiceRole.Light,
        "inpatient" => ServiceRole.Inpatient,
        "critical" => ServiceRole.Critical,
        "fire" => ServiceRole.Fire,
        _ => throw new ScenarioException($"{p}: expected \"repair\", \"light\", \"inpatient\", \"critical\" or \"fire\""),
    };

    /// <summary>Ссылка на ассет: полный id нужного вида или имя в своём пакете.</summary>
    private static AssetId Ref(LoadedPackage pkg, string text, AssetKind kind, string p)
    {
        if (AssetId.TryParse(text, out AssetId id))
        {
            return id.Kind == kind ? id : throw new ScenarioException($"{p}: expected a {AssetId.KindName(kind)} id");
        }

        return AssetId.IsValidName(text) ? new AssetId(pkg.Id, kind, text) : throw new ScenarioException($"{p}: expected a {AssetId.KindName(kind)} id or a name of this package");
    }

    /// <summary>
    /// Плоские таблицы ядра из записей каталога (порядок записей = порядок каталога, индекс записи = <c>Svc</c>): диапазоны уровней, вызовов, поводов; апгрейды уровней
    /// сдвигаются на начало апгрейдов записи. <paramref name="off"/> - id выключенных записей (<c>services_off</c>).
    /// </summary>
    public static void Flatten(IReadOnlyList<ServiceEntry> entries, ISet<AssetId>? off, ref CityServicesDesc s)
    {
        var rec = new List<ServiceRecordDesc>();
        var lv = new List<ServiceLevelDesc>();
        var up = new List<ServiceUpgradeDesc>();
        var calls = new List<ServiceCallDesc>();
        var vis = new List<ServiceVisitDesc>();
        foreach (ServiceEntry e in entries)
        {
            if (off != null && off.Contains(e.Id))
            {
                continue;
            }

            rec.Add(new ServiceRecordDesc
            {
                Fund = e.Fund,
                Rev = (ushort)Math.Min(e.Rev, ushort.MaxValue),
                FirstLevel = lv.Count,
                LevelCount = e.Levels.Length,
                FirstCall = calls.Count,
                CallCount = e.Calls.Length,
                FirstVisit = vis.Count,
                VisitCount = e.Visits.Length,
            });
            int upBase = up.Count;
            foreach (ServiceLevelDesc l in e.Levels)
            {
                ServiceLevelDesc c = l;
                c.FirstUpgrade += upBase;
                lv.Add(c);
            }

            up.AddRange(e.Upgrades);
            calls.AddRange(e.Calls);
            vis.AddRange(e.Visits);
        }

        s.Records = rec.ToArray();
        s.Levels = lv.ToArray();
        s.Upgrades = up.ToArray();
        s.Calls = calls.ToArray();
        s.Visits = vis.ToArray();
    }
}
