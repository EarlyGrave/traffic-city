using Sim.Core.Api;

namespace Sim.Scenarios;

// Секция city.lifecycle (building-lifecycle.md §2, §6.1): износ зданий, заброшка стоит, рассыпание, снос игроком. Требует economy.city_budget.
public static partial class ScenarioLoader
{
    private const string LifecycleKey = "lifecycle";

    private static void ParseLifecycle(JsonObjectReader? r, bool residents, ref CityDesc town)
    {
        if (r == null)
        {
            return;
        }

        if (!residents)
        {
            throw new ScenarioException($"{r.Path}: allowed only with demand.model = \"residents\"");
        }

        CityLifecycleDesc d = CityLifecycleDesc.Default();
        d.Enabled = r.Bool("enabled", true);
        d.AbandonedDecayPct = r.Int("abandoned_decay_pct", d.AbandonedDecayPct, min: 0, max: 10_000);
        d.LowlandCm = DetRound(r.Num("lowland_m", 2.0, min: 0, max: 100) * 100.0);
        d.WornRelocateQ16 = CityDesc.Q16(r.Num("worn_relocate", 0.05, min: 0, max: 1));
        d.RubbleCostPct = r.Int("rubble_cost_pct", d.RubbleCostPct, min: 0, max: 100);
        d.CellM = r.Int("cell_m", d.CellM, min: 8, max: 4096);
        d.SigShift = r.Int("sig_shift", d.SigShift, min: 1, max: 16);
        d.HeritageLossRadiusM = r.Int("heritage_loss_radius_m", d.HeritageLossRadiusM, min: 0, max: 10_000);
        d.LvBlightPermille = r.Int("lv_blight", d.LvBlightPermille, min: 0, max: 100);

        // ЖКХ (§2.10, BL3)
        d.RepairRequestBp = DetRound(r.Num("repair_request", 0.85, min: 0.5, max: 1) * 10_000.0);
        d.RepairPerUnit = r.Int("repair_per_unit", d.RepairPerUnit, min: 0, max: 1_000_000);
        d.RepairMinutes = r.Int("repair_minutes", d.RepairMinutes, min: 1, max: 14_400);
        d.CrewDayCost = r.Int("crew_day_cost", d.CrewDayCost, min: 0, max: 1_000_000);
        d.MaxServiceVehicles = r.Int("max_service_vehicles", d.MaxServiceVehicles, min: 0, max: 65_536);
        d.MaxDispatchPerTick = r.Int("max_dispatch_per_tick", d.MaxDispatchPerTick, min: 1, max: 64);
        d.TariffBurdenPct = r.Int("tariff_burden_pct", d.TariffBurdenPct, min: 0, max: 100);
        d.TariffLeaveQ16 = CityDesc.Q16(r.Num("tariff_leave", 0.02, min: 0, max: 1));
        d.MaxUtilityTariff = r.Int("max_utility_tariff", d.MaxUtilityTariff, min: 0, max: ushort.MaxValue);
        d.UtilityTariff = r.Int("utility_tariff", d.UtilityTariff, min: 0, max: d.MaxUtilityTariff);
        d.RestorePerUnit = r.Int("restore_per_unit", d.RestorePerUnit, min: 0, max: 1_000_000);
        r.Finish();
        if (d.Enabled && !town.Economy.CityBudget)
        {
            throw new ScenarioException($"{r.Path}: requires economy.city_budget = true (demolition is paid from the city budget)");
        }

        town.Lifecycle = d.Enabled ? d : default;
    }

    private static int DetRound(double v) => (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
}
