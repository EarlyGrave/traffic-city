using System;
using Sim.Core.Api;
using Sim.Scenarios.Mods;

namespace Sim.Scenarios;

// Секции режима жителей (scenario-format.md, zoning-demand.md §16): calendar, zoning, residents, economy, choice -> CityDesc в целых единицах ядра (ADR-0022).
// Дробные значения переводятся в Q8/Q16 одной функцией (f64 -> f32, затем округление к целому; то же сверяет тест с Rust), секунды - в тики/децисекунды, метры - в мм.
public static partial class ScenarioLoader
{
    private static readonly string[] _citySectionKeys = { "calendar", "zoning", "residents", "economy", "choice" };
    private static readonly string[] _cityCapacityKeys = { "max_lots", "max_buildings", "max_city_residents", "skim_obs_capacity" };

    /// <summary>Децисекунды из секунд: <c>round((float)s * 10)</c>.</summary>
    private static int Ds(double seconds) => (int)Math.Round((float)seconds * 10.0, MidpointRounding.AwayFromZero);

    /// <summary>Секции режима жителей; вне режима их наличие - ошибка. Таблица зданий: каталог пакетов (v3) либо базовая (v2).</summary>
    private static CityDesc ParseTown(JsonObjectReader root, bool residents, ContentCatalog? catalog)
    {
        if (!residents)
        {
            foreach (string key in _citySectionKeys)
            {
                if (root.Has(key))
                {
                    throw new ScenarioException($"{root.ChildPath(key)}: allowed only with demand.model = \"residents\"");
                }
            }

            return default;
        }

        CityDesc t = CityDesc.Default();
        ParseCalendar(root.OptObject("calendar"), ref t);
        ParseZoning(root.OptObject("zoning"), ref t);
        ParseResidents(root.OptObject("residents"), ref t);
        ParseEconomy(root.OptObject("economy"), ref t);
        ParseChoice(root.OptObject("choice"), ref t);
        if (catalog != null)
        {
            if (catalog.Buildings.Count == 0)
            {
                throw new ScenarioException("$.packages: no building types in the package set (demand.model = \"residents\" needs buildings.json, e.g. package base)");
            }

            t.BuildingTypes = new BuildingTypeDesc[catalog.Buildings.Count];
            for (int i = 0; i < t.BuildingTypes.Length; i++)
            {
                t.BuildingTypes[i] = catalog.Buildings[i].Desc;
            }
        }

        return t;
    }

    private static void ParseCalendar(JsonObjectReader? r, ref CityDesc t)
    {
        if (r == null)
        {
            return;
        }

        t.Calendar.DayTicks = Ticks(r.Num("day_s", 3600, min: 14.4, max: 1e6));
        if (t.Calendar.DayTicks % 288 != 0)
        {
            throw new ScenarioException($"{r.ChildPath("day_s")}: day_s * 20 must be a multiple of 288 (a slot is 1/288 of a day; 3600 s gives 250 ticks)");
        }

        r.Finish();
    }

    private static void ParseZoning(JsonObjectReader? r, ref CityDesc t)
    {
        if (r == null)
        {
            return;
        }

        ref CityZoningDesc z = ref t.Zoning;
        z.LotFrontageMm = Mm(r.Num("lot_frontage_m", 40, min: 5, max: 1000));
        z.LotDepthMm = Mm(r.Num("lot_depth_m", 40, min: 5, max: 1000));
        z.NodeClearMm = Mm(r.Num("node_clear_m", 20, min: 0, max: 1000));
        z.Growth = r.Bool("growth", true);
        z.MaxNewBuildingsPerDay = r.Int("max_new_buildings_per_day", 20, min: 0, max: 1_000_000);
        z.MaxPaintM2 = r.Int("max_paint_m2", 250_000, min: 1);
        if (r.Has("auto"))
        {
            System.Text.Json.JsonValueKind kind = r.Raw("auto").ValueKind;
            if (kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                z.Auto = r.Bool("auto");   // true - доли по умолчанию, false - без автозонирования и начального населения
            }
            else
            {
                JsonObjectReader a = r.ReqObject("auto");
                z.Auto = true;
                z.CCenterShareQ16 = Frac16(a, "c_center_share", 0.15);
                z.IEdgeShareQ16 = Frac16(a, "i_edge_share", 0.15);
                z.UnzonedShareQ16 = Frac16(a, "unzoned_share", 0.2);
                a.Finish();
            }
        }

        r.Finish();
    }

    private static int Frac16(JsonObjectReader r, string key, double def) => CityDesc.Q16(r.Num(key, def, min: 0, max: 1));

    private static void ParseResidents(JsonObjectReader? r, ref CityDesc t)
    {
        if (r == null)
        {
            return;
        }

        ref CityResidentsDesc d = ref t.Residents;
        d.InitialFillQ16 = Frac16(r, "initial_fill", 0.8);
        d.CarOwnershipQ16 = Frac16(r, "car_ownership", 0.7);
        d.LabourShareQ16 = Frac16(r, "labour_share", 0.6);
        string exec = r.Str("trip_execution", "agents");
        d.TripExecution = exec switch
        {
            "agents" => TripExecution.Agents,
            "aggregate" => TripExecution.Aggregate,
            _ => throw new ScenarioException($"{r.ChildPath("trip_execution")}: expected \"agents\" or \"aggregate\""),
        };
        d.SeekMaxDays = r.Int("seek_max_days", 7, min: 1, max: 3650);
        d.EvictMaxDays = r.Int("evict_max_days", 2, min: 1, max: 3650);
        d.WaitMaxTicks = Math.Max(20, Ticks(r.Num("wait_max_s", 600, min: 1, max: 1e6)));
        d.MaxRelocatePerDayQ16 = Frac16(r, "max_relocate_per_day", 0.005);
        d.MaxJobSwitchPerDayQ16 = Frac16(r, "max_jobswitch_per_day", 0.005);
        d.MaxImmigrantsPerDayQ16 = Frac16(r, "max_immigrants_per_day", 0.01);
        d.PlanSlot = r.Int("plan_slot", 36, min: 0, max: 287);
        d.ShopSlots = r.Int("shop_slots", 6, min: 1, max: 288);
        d.HomeMinSlots = r.Int("home_min_slots", 2, min: 0, max: 288);
        d.MatchPerTick = r.Int("match_per_tick", 4, min: 1, max: 1_000_000);
        d.VacateDays = r.Int("vacate_days", 3, min: 0, max: 3650);
        d.SwitchRatioQ8 = CityDesc.Q8(r.Num("switch_ratio", 1.5, min: 0, max: 1000));
        d.RBias = r.Int("r_bias", 0, min: -1_000_000, max: 1_000_000);
        r.Finish();
    }

    private static void ParseEconomy(JsonObjectReader? r, ref CityDesc t)
    {
        if (r == null)
        {
            return;
        }

        ref CityEconomyDesc e = ref t.Economy;
        e.WagePerShift = r.Int("wage_per_shift", 60, min: 0, max: 1_000_000);
        e.BasketPrice = r.Int("basket_price", 30, min: 0, max: 1_000_000);
        e.GoodsPct = r.Int("goods_pct", 40, min: 0, max: 100);
        e.ImportPct = r.Int("import_pct", 50, min: 0, max: 100);
        e.ExportPerShift = r.Int("export_per_shift", 42, min: 0, max: 1_000_000);
        e.InitialResidentBalance = r.Int("initial_resident_balance", 300, min: 0, max: 100_000_000);
        e.InitialFirmBalance = r.Int("initial_firm_balance", 3000, min: 0, max: 100_000_000);
        e.ConstructCost = r.Int("construct_cost", 1000, min: 0, max: 100_000_000);
        e.HireMarginPct = r.Int("hire_margin_pct", 10, min: 0, max: 1000);
        e.LossDaysFire = r.Int("loss_days_fire", 3, min: 1, max: 3650);
        e.LossDaysClose = r.Int("loss_days_close", 14, min: 1, max: 3650);
        e.EwmaShift = r.Int("ewma_shift", 3, min: 0, max: 16);
        e.CTargetPct = r.Int("c_target_pct", 100, min: 0, max: 100_000);
        r.Finish();
    }

    private static void ParseChoice(JsonObjectReader? r, ref CityDesc t)
    {
        if (r == null)
        {
            return;
        }

        ref CityChoiceDesc c = ref t.Choice;
        c.ThetaMode64 = CityDesc.Theta64(r.Num("theta_mode_per_s", 0.01, min: 0, max: 100));
        c.ThetaDest64 = CityDesc.Theta64(r.Num("theta_dest_per_s", 0.008, min: 0, max: 100));
        c.ThetaTime64 = CityDesc.Theta64(r.Num("theta_time_per_s", 0.01, min: 0, max: 100));
        c.ThetaFreq64 = CityDesc.Theta64(r.Num("theta_freq_per_s", 0.005, min: 0, max: 100));
        c.BetaFreqQ8 = CityDesc.Q8(r.Num("beta_freq", 1.0, min: 0, max: 1000));
        c.AscFreqDs = Ds(r.Num("asc_freq_s", -300, min: -1e5, max: 1e5));
        c.GammaFreqDs = Ds(r.Num("gamma_freq_s", 300, min: -1e5, max: 1e5));
        c.AscNonWorkerDs = Ds(r.Num("asc_nonworker_s", 0, min: -1e5, max: 1e5));
        c.FreqMax = r.Int("freq_max", 2, min: 0, max: 3);
        c.WWaitQ8 = CityDesc.Q8(r.Num("w_wait", 2.0, min: 0, max: 1000));
        c.WWalkQ8 = CityDesc.Q8(r.Num("w_walk", 2.0, min: 0, max: 1000));
        c.AscCarDs = Ds(r.Num("asc_car_s", 0, min: -1e5, max: 1e5));
        c.AscBusDs = Ds(r.Num("asc_bus_s", 120, min: -1e5, max: 1e5));
        c.AscWalkDs = Ds(r.Num("asc_walk_s", 0, min: -1e5, max: 1e5));
        c.SdeQ8 = CityDesc.Q8(r.Num("sde", 0.5, min: 0, max: 1000));
        c.SdlQ8 = CityDesc.Q8(r.Num("sdl", 2.0, min: 0, max: 1000));
        c.TimeStepSlots = r.Int("time_step_slots", 2, min: 1, max: 144);
        c.DestCandidates = r.Int("dest_candidates", 8, min: 1, max: 64);
        c.MaxWalkMm = Mm(r.Num("max_walk_m", 1500, min: 0, max: MaxPedDistanceM));
        c.SkimPeriods = r.Int("skim_periods", 6, min: 1, max: 24);
        c.SkimShift = r.Int("skim_shift", 2, min: 0, max: 8);
        c.SkimUpdatePerTick = r.Int("skim_update_per_tick", 256, min: 1, max: 1_000_000);
        r.Finish();
    }
}
