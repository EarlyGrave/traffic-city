using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Demand;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Секции хэша состояния города (core-api §9 п. 12, zoning-demand.md §15): добавляются только при <c>residents</c> (<c>hash_version</c> 3), поэтому хэш прежних сценариев не меняется.
/// Порядок фиксирован: PRNG, сутки и курсоры, участки (состояние), здания (живые слоты), жители (живые слоты, планы, ноги), очередь ищущих, деньги, скимы (данные, накопители),
/// загрузки клеток, ссылки машин на жителей, счётчики города. Производные (геометрия участков, колесо жителей, сетка занятости) не хэшируются.
/// </summary>
internal static class CityHash
{
    public static void Add(ref Fnv1a64 h, World w)
    {
        CityWorld c = w.City!;
        h.Add(w.RngDemand.State);
        h.Add(w.RngDemand.Inc);
        h.Add(c.Day);
        h.Add((int)c.Exec);
        h.Add(c.ExecPending);
        h.Add(c.CursorResidents);
        h.Add(c.CursorBuildings);
        h.Add(c.CursorAccess);
        h.Add(c.DayStage);
        h.Add(c.DemandR);
        h.Add(c.DemandC);
        h.Add(c.DemandI);
        for (int i = 0; i < c.ToursDay.Length; i++)
        {
            h.Add(c.ToursDay[i]);
        }

        for (int i = 0; i < c.VisitsDayTotal.Length; i++)
        {
            h.Add(c.VisitsDayTotal[i]);
            h.Add(c.UnservedDayTotal[i]);
        }

        for (int i = 0; i < c.LegsDay.Length; i++)
        {
            h.Add(c.LegsDay[i]);
            h.Add(c.LegCostDay[i]);
        }

        AddLots(ref h, c.Lots);
        AddBuildings(ref h, c.Buildings);
        AddResidents(ref h, c.Residents);

        Ledger lg = c.Ledger;
        h.Add(lg.External);
        h.Add(lg.City);
        for (int k = 0; k < Ledger.LegacyFlowCount; k++)
        {
            h.Add(lg.FlowDay[k]);
            h.Add(lg.FlowYesterday[k]);
            h.Add(lg.FlowTotal[k]);
        }

        AddSkims(ref h, c.Skims);
        for (int i = 0; i < c.Loads.L.Length; i++)
        {
            h.Add(c.Loads.L[i]);
        }

        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i))
            {
                h.Add(v.Resident[i]);
            }
        }

        AddCounters(ref h, in w.Counters);
        if (c.Budget != null)
        {
            AddBudget(ref h, c, in w.Counters);
        }

        if (c.Parking != null)
        {
            Parking.ParkingHash.Add(ref h, w);   // раздел парковки (parking.md §13): только в режиме, хэш прочих миров не меняется (PK1)
        }

        if (w.Progression != null)
        {
            // раздел прогрессии (progression.md §2.4, §4.4): вехи, открытое, показатели города и счётчик замков - только при прогрессии; без неё кольцо KPI - наблюдатель, хэш прежний
            w.Progression.AddToHash(ref h);
            c.Kpi.AddToHash(ref h, c.Cfg.Lifecycle.Enabled);
            h.Add(w.Counters.LockedRejects);
        }

        if (c.Outside != null)
        {
            // раздел внешних подключений (ADR-0037): только при секции outside, хэш прочих миров не меняется
            OutsideState o = c.Outside;
            for (int k = 0; k < o.Count; k++)
            {
                h.Add(o.Jobs[k]);
                h.Add(o.Taken[k]);
                h.Add(o.ArrivalsDay[k]);
                h.Add(o.ArrivalsHour[k]);
            }

            h.Add(o.Arrivals);
            h.Add(o.ArrivalTimeouts);
        }

        // раздел целей сценария (ADR-0038): только при секции goals, хэш прочих миров не меняется
        c.Goals?.AddToHash(ref h);
        if (c.Cfg.Lifecycle.Enabled)
        {
            AddLifecycle(ref h, c, in w.Counters);
        }
    }

    /// <summary>Раздел жизни зданий (building-lifecycle.md §5): только в режиме <c>city.lifecycle</c>, без него ни одного нового байта. Поля живых слотов, затем счётчики.</summary>
    private static void AddLifecycle(ref Fnv1a64 h, CityWorld c, in SimCounters k)
    {
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.Pool.IsAlive(i))
            {
                h.Add((int)b.Cond[i]);
                h.Add((int)b.Ceil[i]);
                h.Add(b.BornDay[i]);
                h.Add(b.LifeFlags[i]);
                h.Add(b.Signif[i]);
                h.Add(b.ArrivalsDay[2 * i]);
                h.Add(b.ArrivalsDay[(2 * i) + 1]);
            }
        }

        NeighborhoodFields? f = c.Fields;
        if (f != null)
        {
            for (int i = 0; i < f.Cur.Length; i++)
            {
                h.Add(f.Cur[i]);
                h.Add(f.Next[i]);
            }
        }

        h.Add(k.BuildingsCrumbled);
        h.Add(k.BuildingsDemolished);
        h.Add(k.BuildingsResettled);

        // ЖКХ (BL3, §2.10): фонд, потоки и переносы 17..21, тариф (R0) и флаг субсидии, очередь заявок, бригады, счётчики
        Ledger lg = c.Ledger;
        h.Add(lg.Utility);
        for (int fl = Ledger.BudgetFlowCount; fl < Ledger.LifecycleFlowCount; fl++)
        {
            h.Add(lg.FlowDay[fl]);
            h.Add(lg.FlowYesterday[fl]);
            h.Add(lg.FlowTotal[fl]);
        }

        CityBudget? bud = c.Budget;
        if (bud != null)
        {
            for (int fl = Ledger.BudgetFlowCount; fl < Ledger.LifecycleFlowCount; fl++)
            {
                h.Add(bud.Carry[fl]);
            }

            h.Add((int)bud.Policy.R0);
            h.Add((int)bud.Policy.R1);
            h.Add((int)bud.PendingPolicy.R0);
            h.Add((int)bud.PendingPolicy.R1);
        }

        CityServices? sv = c.Services;
        if (sv != null)
        {
            if (sv.Table.Explicit)
            {
                AddServices(ref h, sv);   // режим city.services (services.md §5): станции, все кольца, бригады со станцией
                AddServiceBudget(ref h, sv, lg, bud, in k);   // SR2: поток ServiceUpkeep, ползунки, расходы, отклик
            }
            else
            {
                AddUtilityBl(ref h, sv);   // без режима - ровно как BL3: очередь заявок и бригады с депо-зданием
            }
        }

        h.Add(k.RepairsDone);
        h.Add(k.RepairDelayTicksTotal);
        h.Add(k.RepairNoDepot);
        h.Add(k.CrewTimeouts);
        h.Add(k.TariffLeaves);
        h.Add(k.Restorations);
    }

    /// <summary>ЖКХ без режима служб (BL3, побитово прежний порядок): очередь заявок (здание, поколение, тик), бригады (депо-здание станции, поколение, цель, машина, сроки).</summary>
    private static void AddUtilityBl(ref Fnv1a64 h, CityServices sv)
    {
        RingBuffer<ServiceCall> queue = sv.UtilityQueue!;
        CrewStore u = sv.Crews;
        h.Add(queue.Count);
        for (int i = 0; i < queue.Count; i++)
        {
            ref readonly ServiceCall q = ref queue.At(i);
            h.Add(q.Target);
            h.Add(q.TargetGen);
            h.Add(q.Tick);
        }

        for (int i = 0; i < u.Capacity; i++)
        {
            h.Add(u.State[i]);
            if (u.State[i] == CrewState.Free)
            {
                continue;
            }

            h.Add(sv.SiteBuilding(i));
            h.Add(sv.SiteGen(i));
            AddCrewTail(ref h, u, i);
        }
    }

    /// <summary>Службы в режиме <c>city.services</c> (services.md §5, блок SERV): станции, кольца вызовов по записям (обычные, срочные), бригады со станцией.</summary>
    private static void AddServices(ref Fnv1a64 h, CityServices sv)
    {
        SiteStore s = sv.Sites;
        for (int i = 0; i < s.Capacity; i++)
        {
            h.Add(s.Building[i]);
            if (s.Building[i] < 0)
            {
                continue;
            }

            h.Add(s.Gen[i]);
            h.Add(s.Svc[i]);
            h.Add(s.Level[i]);
            h.Add(s.UpgradeMask[i]);
            h.Add(s.UnitsMax[i]);
            h.Add(s.Duty[i]);
            h.Add(s.BedsUsed[i]);
            h.Add(s.IcuUsed[i]);
            for (int d = 0; d < SiteStore.ReleaseDays; d++)
            {
                h.Add(s.BedRelease[(i * SiteStore.ReleaseDays) + d]);
                h.Add(s.IcuRelease[(i * SiteStore.ReleaseDays) + d]);
            }
        }

        for (int r = 0; r < sv.Table.Count; r++)
        {
            AddRing(ref h, sv.Normal[r]);
            AddRing(ref h, sv.Urgent[r]);
        }

        CrewStore u = sv.Crews;
        for (int i = 0; i < u.Capacity; i++)
        {
            h.Add(u.State[i]);
            if (u.State[i] == CrewState.Free)
            {
                continue;
            }

            h.Add(u.Site[i]);
            AddCrewTail(ref h, u, i);
        }
    }

    /// <summary>
    /// SR2 (services.md §2.3-§2.4, только в режиме): поток 22 <c>ServiceUpkeep</c> и его перенос, ползунки <c>Pct</c>/<c>PctPending</c>, суточные расходы записей и станций, вид вызова бригад,
    /// счётчики вызовов записей и мира, <c>RespEwma</c>.
    /// </summary>
    private static void AddServiceBudget(ref Fnv1a64 h, CityServices sv, Ledger lg, CityBudget? bud, in SimCounters k)
    {
        for (int fl = Ledger.LifecycleFlowCount; fl < Ledger.FlowCount; fl++)
        {
            h.Add(lg.FlowDay[fl]);
            h.Add(lg.FlowYesterday[fl]);
            h.Add(lg.FlowTotal[fl]);
            h.Add(bud != null ? bud.Carry[fl] : 0L);
        }

        for (int r = 0; r < sv.Table.Count; r++)
        {
            h.Add(sv.Pct[r]);
            h.Add(sv.PctPending[r]);
            h.Add(sv.SvcCostDay[r * 2]);
            h.Add(sv.SvcCostDay[(r * 2) + 1]);
            h.Add(sv.RecCallsQueued[r]);
            h.Add(sv.RecCallsDone[r]);
            h.Add(sv.RecRespTicks[r]);
            h.Add(sv.RecCallsLate[r]);
        }

        for (int i = 0; i < sv.SiteVarDay.Length; i++)
        {
            h.Add(sv.SiteVarDay[i]);
        }

        CrewStore u = sv.Crews;
        for (int i = 0; i < u.Capacity; i++)
        {
            h.Add(u.Kind[i]);
        }

        for (int i = 0; i < sv.RespEwma.Length; i++)
        {
            h.Add(sv.RespEwma[i]);
        }

        h.Add(k.CallsQueued);
        h.Add(k.CallsDone);
        h.Add(k.RespTicksTotal);
        h.Add(k.CallsLate);
    }

    private static void AddRing(ref Fnv1a64 h, RingBuffer<ServiceCall> ring)
    {
        h.Add(ring.Count);
        for (int i = 0; i < ring.Count; i++)
        {
            ref readonly ServiceCall q = ref ring.At(i);
            h.Add(q.Kind);
            h.Add(q.Target);
            h.Add(q.TargetGen);
            h.Add(q.Ref);
            h.Add(q.RefGen);
            h.Add(q.Tick);
        }
    }

    private static void AddCrewTail(ref Fnv1a64 h, CrewStore u, int i)
    {
        h.Add(u.Target[i]);
        h.Add(u.TargetGen[i]);
        h.Add(u.SiteLot[i]);
        h.Add(u.Vehicle[i]);
        h.Add(u.VehicleGen[i]);
        h.Add(u.DueTick[i]);
        h.Add(u.ReqTick[i]);
    }

    /// <summary>
    /// Раздел казны (city-economy.md §12, hash_version 4): только при <c>economy.city_budget</c>, поэтому хэш прежних сценариев не меняется. Порядок: политики, цена земли, сглаженные
    /// доход и расход, переносы, долг, кредиты undo, накопители суток, потоки 13..16, фонд оплаты живых зданий, счётчики казны. Производные (<c>Maint*</c>, <c>VotDsQ8</c>) не хэшируются.
    /// </summary>
    private static void AddBudget(ref Fnv1a64 h, CityWorld c, in SimCounters k)
    {
        CityBudget b = c.Budget!;
        AddPolicy(ref h, in b.Policy);
        AddPolicy(ref h, in b.PendingPolicy);
        h.Add(b.HasPending);
        for (int i = 0; i < b.LandValue.Length; i++)
        {
            h.Add(b.LandValue[i]);
        }

        h.Add(b.IncomeEwma);
        h.Add(b.ExpenseEwma);
        for (int i = 0; i < Ledger.BudgetFlowCount; i++)   // 17..21 - только с режимом city.lifecycle (AddLifecycle)
        {
            h.Add(b.Carry[i]);
        }

        h.Add(b.DebtDays);
        h.Add(b.OverLimitDays);
        h.Add(b.Insolvent);
        h.Add(b.WarnLevel);   // PG5 (progression.md §5): уровень предупреждения - состояние (гистерезис), хэш econ-сценариев сменился осознанно
        for (int i = 0; i < b.UndoCredits.Length; i++)
        {
            ref readonly UndoCredit u = ref b.UndoCredits[i];
            h.Add(u.TargetIndex);
            h.Add(u.TargetGeneration);
            h.Add(u.Amount);
            h.Add(u.ExpireTick);
        }

        for (int i = 0; i < 2; i++)
        {
            h.Add(b.FareEvadedDay[i]);
            h.Add(b.DebtEvictionsDay[i]);
        }

        Ledger lg = c.Ledger;
        for (int f = Ledger.LegacyFlowCount; f < Ledger.BudgetFlowCount; f++)
        {
            h.Add(lg.FlowDay[f]);
            h.Add(lg.FlowYesterday[f]);
            h.Add(lg.FlowTotal[f]);
        }

        BuildingStore bl = c.Buildings;
        for (int i = 0; i < bl.HighWater; i++)
        {
            if (bl.Pool.IsAlive(i))
            {
                h.Add(bl.PayrollEwma[i]);
            }
        }

        h.Add(k.FareEvaded);
        h.Add(k.DebtEvictions);
        h.Add(k.InsufficientFunds);
    }

    private static void AddPolicy(ref Fnv1a64 h, in BudgetPolicy p)
    {
        h.Add((int)p.TaxRBp);
        h.Add((int)p.TaxCBp);
        h.Add((int)p.TaxIBp);
        h.Add((int)p.LandTaxBpDay);
        h.Add((int)p.Fare);
        h.Add((int)p.Flags);
    }

    private static void AddLots(ref Fnv1a64 h, LotStore l)
    {
        h.Add(l.Count);
        for (int i = 0; i < l.Count; i++)
        {
            h.Add(l.Seg[i]);
            h.Add(l.SegGen[i]);
            h.Add(l.Side[i]);
            h.Add(l.S0Mm[i]);
            h.Add(l.S1Mm[i]);
            h.Add(l.Zone[i]);
            h.Add(l.Density[i]);
            h.Add(l.Building[i]);
            h.Add(l.Flags[i]);
            if (l.Orient[i] != LotOrient.Legacy)
            {
                // участок на мировой сетке (RP5): клетки - состояние; legacy-участки хэшируются как раньше
                h.Add(l.CellX[i]);
                h.Add(l.CellZ[i]);
                h.Add(l.CellsF[i]);
                h.Add(l.CellsD[i]);
                h.Add(l.Orient[i]);
            }
        }
    }

    private static void AddBuildings(ref Fnv1a64 h, BuildingStore b)
    {
        int high = b.HighWater;
        h.Add(high);
        for (int i = 0; i < high; i++)
        {
            h.Add(b.Pool.GenerationOf(i));
            if (!b.Pool.IsAlive(i))
            {
                continue;
            }

            h.Add(b.Lot[i]);
            h.Add(b.Type[i]);
            h.Add(b.State[i]);
            h.Add(b.StateTick[i]);
            h.Add(b.Occupants[i]);
            h.Add(b.Jobs[i]);
            h.Add(b.HeadRes[i]);
            h.Add(b.HeadWork[i]);
            h.Add(b.Account[i]);
            h.Add(b.RevenueEwma[i]);
            h.Add(b.ProfitEwma[i]);
            for (int k = 0; k < 2; k++)
            {
                h.Add(b.VisitsDay[(2 * i) + k]);
                h.Add(b.RevenueDay[(2 * i) + k]);
                h.Add(b.CostDay[(2 * i) + k]);
            }

            h.Add((int)b.LossDays[i]);
            h.Add((int)b.VacantDays[i]);
        }
    }

    private static void AddResidents(ref Fnv1a64 h, CityResidents r)
    {
        int high = r.HighWater;
        h.Add(high);
        h.Add(r.NextSeq);
        for (int i = 0; i < high; i++)
        {
            h.Add(r.Pool.GenerationOf(i));
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            h.Add(r.Home[i]);
            h.Add(r.Work[i]);
            h.Add(r.NextHome[i]);
            h.Add(r.NextWork[i]);
            h.Add(r.Flags[i]);
            h.Add(r.State[i]);
            h.Add(r.StateTick[i]);
            h.Add(r.EvTick[i]);
            h.Add(r.EvKind[i]);
            h.Add(r.PlanCount[i]);
            h.Add(r.PlanCursor[i]);
            int pb = i * CityResidents.MaxTours;
            for (int k = 0; k < CityResidents.MaxTours; k++)
            {
                h.Add(r.PlanKind[pb + k]);
                h.Add(r.PlanMode[pb + k]);
                h.Add(r.PlanDest[pb + k]);
                h.Add((int)r.PlanSlot[pb + k]);
            }

            h.Add(r.LegMode[i]);
            h.Add((int)r.LegO[i]);
            h.Add((int)r.LegD[i]);
            h.Add(r.LegReq[i]);
            h.Add(r.LegWalk[i]);
            h.Add(r.LegWait[i]);
            h.Add(r.LegRide[i]);
            h.Add(r.LegDest[i]);
            h.Add(r.LegStopA[i]);
            h.Add(r.LegStopB[i]);
            h.Add(r.Agent[i]);
            h.Add(r.AgentGen[i]);
            h.Add(r.Account[i]);
            h.Add(r.IncomeEwma[i]);
            h.Add((int)r.SeekDays[i]);
            h.Add((int)r.EvictDays[i]);
            h.Add(r.Seq[i]);
        }

        int seek = r.SeekQueue.Count;
        h.Add(seek);
        for (int k = 0; k < seek; k++)
        {
            ref readonly SeekEntry e = ref r.SeekQueue.At(k);
            h.Add(e.Index);
            h.Add(e.Generation);
        }
    }

    private static void AddSkims(ref Fnv1a64 h, Skims s)
    {
        h.Add(s.LsRef);
        h.Add(s.LsRefSet ? 1 : 0);
        h.Add(s.CurPeriod);
        h.Add(s.UpdateCursor);
        h.Add(s.FrozenN);
        for (int i = 0; i < s.Skim.Length; i++)
        {
            h.Add(s.Skim[i]);
        }

        for (int i = 0; i < s.K.Length; i++)
        {
            h.Add(s.K[i]);
        }

        for (int i = 0; i < s.LsC.Length; i++)
        {
            h.Add(s.LsC[i]);
        }

        for (int i = 0; i < s.LsJobs.Length; i++)
        {
            h.Add(s.LsJobs[i]);
        }

        for (int i = 0; i < s.FrozenN; i++)
        {
            h.Add(s.FrozenKey[i]);
            h.Add(s.FrozenSum[i]);
            h.Add(s.FrozenCount[i]);
        }

        // Текущий накопитель - отсортированным слепком (порядок вставки на хэш не влияет). Scratch - только вне горячего цикла (хэш зовут тесты и сейв).
        int n = s.Current.Count;
        h.Add(n);
        if (n > 0)
        {
            var keys = new int[n];
            var sums = new long[n];
            var counts = new int[n];
            s.Current.CopySorted(keys, sums, counts);
            for (int i = 0; i < n; i++)
            {
                h.Add(keys[i]);
                h.Add(sums[i]);
                h.Add(counts[i]);
            }
        }
    }

    private static void AddCounters(ref Fnv1a64 h, in SimCounters c)
    {
        h.Add(c.CityEvents);
        h.Add(c.ResToursPlanned);
        h.Add(c.ResToursDropped);
        h.Add(c.ResToursUnreachable);
        h.Add(c.ResToursCancelled);
        h.Add(c.ResLegsCar);
        h.Add(c.ResLegsBus);
        h.Add(c.ResLegsWalk);
        h.Add(c.ResLegsDone);
        h.Add(c.ResLegCostDsTotal);
        h.Add(c.ResVisits);
        h.Add(c.ResVisitsUnserved);
        h.Add(c.ResLegOverdue);
        h.Add(c.ResStuck);
        h.Add(c.ResWaitDepartTicks);
        h.Add(c.SkimObserved);
        h.Add(c.SkimObsDropped);
        h.Add(c.SkimUpdates);
        h.Add(c.Immigrants);
        h.Add(c.Emigrants);
        h.Add(c.ResRelocations);
        h.Add(c.ResHired);
        h.Add(c.ResFired);
        h.Add(c.ResJobSwitches);
        h.Add(c.BuildingsBuilt);
        h.Add(c.BuildingsAbandoned);
        h.Add(c.BuildingsRemoved);
        h.Add(c.FirmClosures);
        h.Add(c.PaintCommands);
        h.Add(c.ExecSwitches);
        h.Add(c.CityDays);
        h.Add(c.AggregateLegs);
    }
}
