using Sim.Core.Api;
using Sim.Core.Demand;

namespace Sim.Core.City;

/// <summary>
/// План дня жителя (zoning-demand.md §3.2, событие <c>Plan</c>): рабочий тур <c>Home -&gt; Work -&gt; Home</c> (желаемое прибытие - начало смены фирмы со сдвигом жителя <c>Seq % 7 - 3</c> слота),
/// затем <c>k</c> дискреционных туров <c>Home -&gt; C -&gt; Home</c>; цель, способ и слот выезда выбирает <see cref="Choice"/> (в Z2 - заглушки Z1, Z3 заменяет тела). Туры не пересекаются
/// (следующий не раньше возвращения плюс <c>home_min_slots</c>), не поместившийся в сутки тур отбрасывается (<c>res_tours_dropped</c>). Без аллокаций.
/// </summary>
internal static class DayPlanner
{
    /// <summary>Желаемое прибытие дискреционного тура неработающего: 11:00 (слот 132), 12 часов на два-три тура.</summary>
    private const int NonWorkerBaseSlot = 132;
    private const int NonWorkerStepSlots = 60;

    public static void Build(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        long now = w.Tick;
        int day = Calendar.DayOf(c, now);
        res.ClearPlan(r);
        int cursor = c.SlotOf(now) + 1;   // не раньше следующего слота: выезд строго в будущем
        int n = 0;
        int seq = res.Seq[r];
        if (c.Services is { Table.Explicit: true } && Illness.PlanDay(w, c, r, day, ref n, ref cursor))
        {
            Finish(w, c, r, day, now, n);   // больной (SR3, services.md §2.5): вместо обычных туров - разве что к врачу
            return;
        }

        bool worker = (res.Flags[r] & ResidentFlags.Labour) != 0 && res.Work[r] >= 0 && c.Buildings.IsAlive(res.Work[r]) && c.Buildings.State[res.Work[r]] == (byte)BuildingState.Active;
        if (worker)
        {
            int work = res.Work[r];
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[c.Buildings.Type[work]];
            int desired = t.ShiftStartSlot + ((seq % 7) - 3);
            AddTour(w, c, r, day, TourKind.Work, work, desired < 0 ? 0 : desired, t.ShiftSlots, ref n, ref cursor);
        }
        else if ((res.Flags[r] & ResidentFlags.Labour) != 0 && OutsideRef.Is(res.Work[r]))
        {
            // Внешняя работа (ADR-0037): тур дом -> узел подключения -> дом, смена 08:00-17:00 со сдвигом жителя.
            worker = true;
            int desired = OutsideState.ShiftStartSlot + ((seq % 7) - 3);
            AddTour(w, c, r, day, TourKind.Work, res.Work[r], desired, OutsideState.ShiftSlots, ref n, ref cursor);
        }

        int k = Choice.ChooseFrequency(w, r);
        if (k > c.Cfg.Choice.FreqMax)
        {
            k = c.Cfg.Choice.FreqMax;
        }

        for (int j = 0; j < k && n < CityResidents.MaxTours; j++)
        {
            int dest = Choice.ChooseDestination(w, r);
            if (dest < 0 || !c.Buildings.IsAlive(dest))
            {
                w.Counters.ResToursUnreachable++;
                break;
            }

            int baseSlot = worker ? 0 : NonWorkerBaseSlot + (j * NonWorkerStepSlots) + (seq % 24);
            AddTour(w, c, r, day, TourKind.Shop, dest, baseSlot, c.Cfg.Residents.ShopSlots, ref n, ref cursor);
        }

        Finish(w, c, r, day, now, n);
    }

    private static void Finish(World w, CityWorld c, int r, int day, long now, int n)
    {
        CityResidents res = c.Residents;
        res.PlanCount[r] = (byte)n;
        res.PlanCursor[r] = 0;
        w.Counters.ResToursPlanned += n;
        c.ToursDay[day & 1] += n;
        if (n > 0)
        {
            long when = c.TickOfSlot(day, res.PlanSlot[r * CityResidents.MaxTours]);
            CityPhase.SetEvent(w, c, r, when > now ? when : now + 1, ResidentEvent.Depart);
        }
        else
        {
            CityPhase.SetEvent(w, c, r, Calendar.NextPlanTick(c, r, now), ResidentEvent.Plan);
        }
    }

    internal static void AddTour(World w, CityWorld c, int r, int day, byte kind, int dest, int desiredBase, int stay, ref int n, ref int cursor)
    {
        CityResidents res = c.Residents;
        int homeLot = c.Buildings.Lot[res.Home[r]];
        int destLot = c.LotOf(dest);
        int baseSlot = desiredBase < CityWorld.SlotsPerDay ? desiredBase : CityWorld.SlotsPerDay - 1;
        int period = c.PeriodOf(c.TickOfSlot(day, baseSlot));
        if (!Choice.TryChooseMode(w, r, dest, period, out TravelMode mode))
        {
            w.Counters.ResToursUnreachable++;   // ни один способ не доступен: тур отменяется
            return;
        }

        int travel = TravelSlots(c, homeLot, destLot, mode, period);
        int desired = desiredBase > cursor + travel ? desiredBase : cursor + travel;
        int depart = Choice.ChooseDepartSlot(w, r, desired, travel, c.Lots.Cell[homeLot], c.Lots.Cell[destLot], mode);   // время ноги кандидата - из скима его периода
        if (depart < cursor)
        {
            depart = cursor;
        }

        int arrive = depart + travel;
        int begin = kind == TourKind.Work && desiredBase > arrive ? desiredBase : arrive;   // смена не начинается раньше своего часа
        int end = begin + stay + travel;
        if (end >= CityWorld.SlotsPerDay || n >= CityResidents.MaxTours)
        {
            w.Counters.ResToursDropped++;
            return;
        }

        int i = (r * CityResidents.MaxTours) + n;
        res.PlanKind[i] = kind;
        res.PlanMode[i] = (byte)mode;
        res.PlanDest[i] = dest;
        res.PlanSlot[i] = (ushort)depart;
        n++;
        cursor = end + c.Cfg.Residents.HomeMinSlots;
    }

    /// <summary>
    /// Время ноги в слотах (не меньше 1): ским пары ячеек и периода, а пока он пуст (0 - «нет данных», априорная оценка §5.4 - Z3) грубая оценка по манхэттенскому расстоянию между участками
    /// <c>x 1.3</c>: машина 8 м/с + 60 с, автобус 5 м/с + 300 с, пешком 1.3 м/с. Только для расписания; в скимы идут наблюдения.
    /// </summary>
    internal static int TravelSlots(CityWorld c, int fromLot, int toLot, TravelMode mode, int period)
    {
        int ds = c.Skims.Get(c.Lots.Cell[fromLot], c.Lots.Cell[toLot], mode, period);
        if (ds <= 0)
        {
            int dm = c.Lots.ManhattanM(fromLot, toLot) * 13 / 10;
            int sec = mode switch
            {
                TravelMode.Car => (dm / 8) + 60,
                TravelMode.Bus => (dm / 5) + 300,
                _ => dm * 10 / 13,
            };
            ds = sec * 10;
        }

        return Calendar.TicksToSlots(c, Calendar.DsToTicks(ds));
    }
}
