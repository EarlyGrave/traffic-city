namespace Sim.Core.City;

/// <summary>
/// Календарь города (zoning-demand.md §3.1): сутки = <c>calendar.day_s * 20</c> тиков, слот = 1/288 суток. Чистые функции над <see cref="CityWorld"/> без состояния
/// (номер суток <c>CityWorld.Day</c> растёт в фазе 9r на границе).
/// </summary>
internal static class Calendar
{
    /// <summary>Тик начала плана следующих суток для жителя: слот <c>plan_slot + Seq % 13</c> (начальный разброс <c>U(0, 12)</c> даёт <c>CityInit</c>); не раньше <paramref name="now"/> + 1.</summary>
    public static long NextPlanTick(CityWorld c, int resident, long now)
    {
        int slot = c.Cfg.Residents.PlanSlot + (c.Residents.Seq[resident] % 13);
        long t = c.TickOfSlot(DayOf(c, now) + 1, slot);
        return t > now ? t : now + 1;
    }

    public static int DayOf(CityWorld c, long tick) => (int)(tick / c.DayTicks);

    /// <summary>Слот ceil(<paramref name="ticks"/> / слот), не меньше 1.</summary>
    public static int TicksToSlots(CityWorld c, long ticks)
    {
        long s = (ticks + c.SlotTicks - 1) / c.SlotTicks;
        return s < 1 ? 1 : (int)s;
    }

    /// <summary>Децисекунды (0.1 с) в тики симуляции (20 Гц): 1 дс = 2 тика.</summary>
    public static long DsToTicks(long ds) => ds * 2;

    /// <summary>Тики в децисекунды, с округлением вверх.</summary>
    public static int TicksToDs(long ticks) => (int)((ticks + 1) >> 1);
}
