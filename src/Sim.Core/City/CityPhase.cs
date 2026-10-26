using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Фазы города (zoning-demand.md §12). 2r <see cref="Events"/> - события колеса жителей текущего тика по возрастанию индекса жителя (план дня, выезд, конец занятия, дедлайн WaitDepart);
/// 9r <see cref="Periodic"/> - кольцо прибытий по порядку, граница суток. Все фазы последовательные (общие очереди, PRNG, проводки). Между событиями житель не обходится ни одной фазой:
/// стоимость слоя - O(событий за тик). Дневные проходы, скимы порциями, рынки и рост добавляют Z3-Z6 в <see cref="Periodic"/>.
/// </summary>
internal static class CityPhase
{
    /// <summary>Фаза 2r: события жителей тика (до OD-генератора и спавна 2c/2d, чтобы заявки ног попали в спавн того же тика).</summary>
    public static void Events(World w)
    {
        CityWorld c = w.City!;
        CityResidents res = c.Residents;
        int n = c.Wheel.Collect(w.Tick, c.DueScratch);
        for (int k = 0; k < n; k++)
        {
            int r = c.DueScratch[k];
            if (!res.Pool.IsAlive(r) || res.EvTick[r] != w.Tick)
            {
                continue;   // страховка: событие снято (SetEvent отменяет колесо), сюда не попадаем
            }

            byte kind = res.EvKind[r];
            res.EvTick[r] = -1;
            res.EvKind[r] = ResidentEvent.None;
            w.Counters.CityEvents++;
            switch (kind)
            {
                case ResidentEvent.Plan:
                    OnPlan(w, c, r);
                    break;
                case ResidentEvent.Depart:
                    OnDepart(w, c, r);
                    break;
                case ResidentEvent.EndActivity:
                    OnEndActivity(w, c, r);
                    break;
                case ResidentEvent.Deadline:
                    if (res.State[r] == (byte)ResidentState.WaitDepart)
                    {
                        TripLegs.OnWaitTimeout(w, c, r);
                    }

                    break;
                default:
                    break;   // Arrive - агрегат (Z5)
            }
        }
    }

    /// <summary>Фаза 9r: прибытия в порядке кольца, затем граница суток.</summary>
    public static void Periodic(World w)
    {
        CityWorld c = w.City!;
        while (c.Arrivals.TryPop(out int r))
        {
            TripLegs.OnLegArrived(w, c, r);
        }

        if ((w.Tick + 1) % c.DayTicks == 0)
        {
            c.Day++;
            w.Counters.CityDays++;
            int idx = c.Day & 1;
            c.ToursDay[idx] = 0;
            for (int m = 0; m < Demand.Skims.Modes; m++)
            {
                c.LegsDay[(idx * Demand.Skims.Modes) + m] = 0;
                c.LegCostDay[(idx * Demand.Skims.Modes) + m] = 0;
            }
        }

#if DEBUG
        if (w.Tick % 100 == 0)
        {
            CheckInvariants(w, c);
        }
#endif
    }

    /// <summary>Ставит единственное событие жителя (<c>EvTick/EvKind</c>) и обновляет колесо; <paramref name="when"/> &lt; 0 - события нет.</summary>
    internal static void SetEvent(World w, CityWorld c, int r, long when, byte kind)
    {
        CityResidents res = c.Residents;
        if (c.Wheel.Contains(r))
        {
            c.Wheel.Cancel(r);
        }

        if (when < 0)
        {
            res.EvTick[r] = -1;
            res.EvKind[r] = ResidentEvent.None;
            return;
        }

        res.EvTick[r] = when;
        res.EvKind[r] = kind;
        long now = w.Tick < when - 1 ? w.Tick : when - 1;   // команды (фаза 1) могут ставить событие текущего тика, фаза 2r ещё впереди
        c.Wheel.Schedule(r, when, now);
    }

    private static void OnPlan(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if (res.State[r] != (byte)ResidentState.AtHome)
        {
            SetEvent(w, c, r, w.Tick + c.SlotTicks, ResidentEvent.Plan);   // ещё не вернулся: план чуть позже
            return;
        }

        DayPlanner.Build(w, c, r);
    }

    private static void OnDepart(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        byte state = res.State[r];
        if (state == (byte)ResidentState.WaitDepart)
        {
            TripLegs.RetryRequest(w, c, r);
            return;
        }

        if (state != (byte)ResidentState.AtHome || res.PlanCursor[r] >= res.PlanCount[r])
        {
            SetEvent(w, c, r, Calendar.NextPlanTick(c, r, w.Tick), ResidentEvent.Plan);
            return;
        }

        res.LegReq[r] = w.Tick;   // база дня для следующих событий плана, даже если нога не начнётся
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        int dest = res.PlanDest[tour];
        bool ok = c.Buildings.IsAlive(dest) && c.Buildings.State[dest] == (byte)BuildingState.Active && TripLegs.StartLeg(w, c, r, true, (TravelMode)res.PlanMode[tour]);
        if (!ok)
        {
            w.Counters.ResToursUnreachable++;
            TripLegs.FinishTour(w, c, r);
        }
    }

    private static void OnEndActivity(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        byte state = res.State[r];
        if (state == (byte)ResidentState.WaitDepart)
        {
            TripLegs.RetryRequest(w, c, r);
        }
        else if (state == (byte)ResidentState.AtWork || state == (byte)ResidentState.AtShop)
        {
            int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
            if (!TripLegs.StartLeg(w, c, r, false, (TravelMode)res.PlanMode[tour]))
            {
                TripLegs.ArriveWithoutLeg(w, c, r);
            }
        }
    }

#if DEBUG
    /// <summary>CZ3 (debug, раз в 100 тиков): состояния согласованы с агентами и колесом, Σ по состояниям = число живых.</summary>
    private static void CheckInvariants(World w, CityWorld c)
    {
        CityResidents res = c.Residents;
        int alive = 0;
        int inLeg = 0;
        for (int r = 0; r < res.HighWater; r++)
        {
            if (!res.Pool.IsAlive(r))
            {
                continue;
            }

            alive++;
            byte state = res.State[r];
            System.Diagnostics.Debug.Assert(state <= (byte)ResidentState.InLeg, "bad resident state");
            System.Diagnostics.Debug.Assert((state == (byte)ResidentState.InLeg) == (res.Agent[r] >= 0), "agent only in leg");
            System.Diagnostics.Debug.Assert(c.Wheel.Contains(r) == (res.EvTick[r] >= 0), "wheel entry iff event");
            if (state == (byte)ResidentState.InLeg)
            {
                inLeg++;
                if (res.LegMode[r] == (byte)TravelMode.Car)
                {
                    System.Diagnostics.Debug.Assert(w.Vehicles.Pool.IsAlive(res.Agent[r], res.AgentGen[r]) && w.Vehicles.Resident[res.Agent[r]] == r, "car leg without its vehicle");
                }
                else
                {
                    System.Diagnostics.Debug.Assert(w.Peds!.P.Pool.IsAlive(res.Agent[r], res.AgentGen[r]) && w.Peds.P.Resident[res.Agent[r]] == r, "ped leg without its pedestrian");
                }
            }
        }

        System.Diagnostics.Debug.Assert(alive == res.AliveCount && inLeg <= alive, "CZ3: Alive");
    }
#endif
}
