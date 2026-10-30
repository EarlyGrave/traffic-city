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
        r.Finish();
        if (d.Enabled && !town.Economy.CityBudget)
        {
            throw new ScenarioException($"{r.Path}: requires economy.city_budget = true (demolition is paid from the city budget)");
        }

        town.Lifecycle = d.Enabled ? d : default;
    }

    private static int DetRound(double v) => (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
}
