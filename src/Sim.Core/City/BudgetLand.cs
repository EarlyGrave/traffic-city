using System;
using Sim.Core.Api;
using Sim.Core.Demand;

namespace Sim.Core.City;

/// <summary>
/// Цена земли (city-economy.md §4): линейная функция доступности к местам, сглаженная за неделю. Земля не зависит от заселённости, налога и цены соседей (урок E1): игрок поднимает цену
/// только транспортом. Тот же массив <c>CityBudget.LandValue</c> берёт налог и показывает heatmap.
/// </summary>
internal static class BudgetLand
{
    /// <summary>
    /// Пересчёт <c>LV</c> на границе суток после <c>Accessibility</c> дня (§4): цель <c>T[c] = clamp(lv_ref + lv_slope * (LsRef - LS_land) / 600, lv_min, lv_max)</c>, где
    /// <c>LS_land = min(LsJobs, min_m LsC)</c>; нет доступа (<c>NoLs</c>) или нет опорного <c>LsRef</c> - <c>lv_min</c> / <c>lv_ref</c> соответственно. Сглаживание <c>LV += (T - LV) &gt;&gt; lv_shift</c>;
    /// <paramref name="reset"/> (Create, Enable) - <c>LV = T</c>. Целые числа, O(NC).
    /// </summary>
    public static void Update(World w, CityWorld c, bool reset)
    {
        CityBudget? b = c.Budget;
        if (b == null)
        {
            return;
        }

        CityEconomyDesc e = c.Cfg.Economy;
        Skims s = c.Skims;
        int cells = b.LandValue.Length;
        for (int cell = 0; cell < cells; cell++)
        {
            int target = Target(in e, s, cell);
            if (reset)
            {
                b.LandValue[cell] = target;
            }
            else
            {
                b.LandValue[cell] += (target - b.LandValue[cell]) >> e.LvShift;
            }
        }
    }

    /// <summary>Цель <c>T[cell]</c> (§4); всегда в <c>[lv_min, lv_max]</c>.</summary>
    internal static int Target(in CityEconomyDesc e, Skims s, int cell)
    {
        int ls = s.LsJobs[cell];
        for (int m = 0; m < Skims.Modes; m++)
        {
            ls = Math.Min(ls, s.LsC[(cell * Skims.Modes) + m]);
        }

        if (ls == Skims.NoLs)
        {
            return e.LvMin;
        }

        if (!s.LsRefSet)
        {
            return e.LvRef;
        }

        long t = e.LvRef + ((long)e.LvSlopePerMin * ((long)s.LsRef - ls) / 600);
        return (int)Math.Clamp(t, e.LvMin, e.LvMax);
    }
}
