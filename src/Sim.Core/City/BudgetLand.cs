namespace Sim.Core.City;

/// <summary>Цена земли (city-economy.md §4). E1 заморозил сигнатуру и массив <c>CityBudget.LandValue</c> (при создании - <c>lv_ref</c>); тело - E2.</summary>
internal static class BudgetLand
{
    /// <summary>
    /// Пересчёт <c>LV</c> на границе суток после <c>Accessibility</c> дня (§4): цель <c>T[c]</c> по <c>min(LsJobs, LsC)</c> и <c>LsRef</c>, сглаживание <c>&gt;&gt; lv_shift</c>;
    /// <paramref name="reset"/> (Create, Enable) - <c>LV = T</c>. E1 - заглушка, тело - E2.
    /// </summary>
    public static void Update(World w, CityWorld c, bool reset)
    {
    }
}
