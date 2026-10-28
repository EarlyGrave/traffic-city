using System;
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
        if (c.Exec == TripExecution.Aggregate && w.Tick % c.DayTicks == 0 && w.Peds != null && w.Peds.CitySource && w.Peds.P.AliveCount > 0)
        {
            w.Peds.DropAgentsForAggregate(w);   // остатки micro-пешеходов: ноги переходят к агрегату (§9.2)
        }

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
                case ResidentEvent.Arrive:
                    TripLegs.OnAggregateArrived(w, c, r);   // нога агрегата закончена (§9.1)
                    break;
                case ResidentEvent.AccessWalkDone:
                    TripLegs.OnAccessWalkDone(w, c, r);   // пеший участок у машины закончен (parking.md §6)
                    break;
                default:
                    break;
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

        c.Market.MatchStep(w, c);   // рынок труда (Z4): до match_per_tick ищущих за тик
        c.Skims.Advance(w);   // граница периода и порция обновления скима (Z3), каждый тик
        int cells = c.Skims.Cells;
        if (w.Tick % Math.Max(1, c.DayTicks / Math.Max(1, cells)) == 0)
        {
            Demand.Accessibility.Step(w, 1);   // поле доступности: ячейки-источники порциями, полный проход за сутки
        }

        DayPass(w, c, false);
        if ((w.Tick + 1) % c.DayTicks == 0)
        {
            c.Day++;
            w.Counters.CityDays++;
            BudgetFlows.OnDayBoundary(w, c);   // казна (city-economy.md §6.2): применение ожидающей политики; E2 - цена земли, содержание, проценты, лимит
            c.Ledger.RollDay();
            c.Loads.ClearDay(c.Day);   // «сегодня» новых суток: позавчерашние числа L больше не нужны
            c.DayStage = DayStages.Buildings;
            c.CursorBuildings = 0;
            c.CursorResidents = 0;
            c.Market.InvalidateSupplier();
            int idx = c.Day & 1;
            c.ToursDay[idx] = 0;
            c.VisitsDayTotal[idx] = 0;
            c.UnservedDayTotal[idx] = 0;
            for (int m = 0; m < Demand.Skims.Modes; m++)
            {
                c.LegsDay[(idx * Demand.Skims.Modes) + m] = 0;
                c.LegCostDay[(idx * Demand.Skims.Modes) + m] = 0;
            }

            Demand.AggregateExec.OnDayBoundary(w, c);   // отложенное SetTripExecution: micro -> aggregate калибрует K по только что законченным суткам
        }

#if DEBUG
        if (w.Tick % 100 == 0)
        {
            CheckInvariants(w, c);
        }
#endif
    }

    /// <summary>
    /// Дневной проход (Z4): стадия 0 - здания (стройка, прибыль, найм, закрытие, товары, экспорт), стадия 1 - жители (дедлайны, переезды, смена работы), стадия 2 - спрос, снос, въезд, рост.
    /// Каждая из первых двух стадий уложена в четверть суток порциями по курсору (<see cref="CityWorld.CursorBuildings"/>, <see cref="CityWorld.CursorResidents"/>); <paramref name="all"/> - целиком
    /// за один вызов (агрегат при прыжке по дням, тесты). Первые сутки (Day = 0) проход не идёт: вчера не было.
    /// </summary>
    internal static void DayPass(World w, CityWorld c, bool all)
    {
        if (c.DayStage >= DayStages.Idle)
        {
            return;
        }

        all |= c.Exec == TripExecution.Aggregate;   // агрегат: дневные проходы целиком в первый тик суток (§9.2), бюджета p95 нет

        int quarter = Math.Max(1, c.DayTicks / 4);
        if (c.DayStage == DayStages.Buildings)
        {
            BuildingStore b = c.Buildings;
            int quota = all ? int.MaxValue : Math.Max(1, (b.HighWater + quarter - 1) / quarter);
            int end = (int)Math.Min((long)c.CursorBuildings + quota, b.HighWater);
            for (int i = c.CursorBuildings; i < end; i++)
            {
                Economy.BuildingDay(w, c, i);
            }

            c.CursorBuildings = end;
            if (end >= b.HighWater)
            {
                c.DayStage = DayStages.Residents;
                c.CursorResidents = 0;
            }

            if (!all || c.DayStage != DayStages.Residents)
            {
                return;
            }
        }

        if (c.DayStage == DayStages.Residents)
        {
            CityResidents res = c.Residents;
            int quota = all ? int.MaxValue : Math.Max(1, (res.HighWater + quarter - 1) / quarter);
            int end = (int)Math.Min((long)c.CursorResidents + quota, res.HighWater);
            for (int r = c.CursorResidents; r < end; r++)
            {
                if (res.Pool.IsAlive(r))
                {
                    if (c.Budget != null)
                    {
                        BudgetFlows.ResidentDay(c, r);   // земля и утечка (city-economy.md §3, §7.2); до рынка: выезд жителя забирает уже остаток
                    }

                    c.Market.ResidentDay(w, c, r);
                }
            }

            c.CursorResidents = end;
            if (end >= res.HighWater)
            {
                c.DayStage = DayStages.Growth;
            }

            if (!all || c.DayStage != DayStages.Growth)
            {
                return;
            }
        }

        if (c.DayStage == DayStages.Growth)
        {
            Growth.Daily(w, c);
            c.DayStage = DayStages.Idle;
        }
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

        if ((res.Flags[r] & ResidentFlags.Leaving) != 0)
        {
            c.Market.Emigrate(w, c, r);   // дедлайн поиска работы или выселения истёк: отъезд из дома
            return;
        }

        if ((res.Flags[r] & ResidentFlags.Evicted) != 0 && c.Market.TryRelocate(w, c, r, true))
        {
            res.Flags[r] = (byte)(res.Flags[r] & ~ResidentFlags.Evicted);
            res.EvictDays[r] = 0;
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
            if (state == (byte)ResidentState.AtWork)
            {
                Economy.PayWage(w, c, r);
            }

            if (!TripLegs.StartLeg(w, c, r, false, (TravelMode)res.PlanMode[tour]))
            {
                TripLegs.ArriveWithoutLeg(w, c, r);
            }
        }
    }

#if DEBUG
    /// <summary>CZ3 и CZ2 (debug, раз в 100 тиков): состояния согласованы с агентами и колесом, Σ по состояниям = число живых, Σ счетов = 0.</summary>
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
            System.Diagnostics.Debug.Assert((state == (byte)ResidentState.InLeg) == (res.Agent[r] >= 0 || res.EvKind[r] == ResidentEvent.Arrive || res.EvKind[r] == ResidentEvent.AccessWalkDone), "agent only in leg");
            System.Diagnostics.Debug.Assert(c.Wheel.Contains(r) == (res.EvTick[r] >= 0), "wheel entry iff event");
            if (state == (byte)ResidentState.InLeg && res.Agent[r] >= 0)
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
        System.Diagnostics.Debug.Assert(c.Ledger.SumAll() == 0, "CZ2: сумма счетов");
    }
#endif
}
