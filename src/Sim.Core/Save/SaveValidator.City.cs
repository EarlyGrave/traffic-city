using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Demand;

namespace Sim.Core.Save;

// Проверки города после загрузки (zoning-demand.md §15): после Ok Step безопасен - ссылки в границах, списки (дом, работа) совпадают с полями жителей,
// Occupants = длина списка, Σ счетов = 0 (CZ2), агенты жителей InLeg живы, а у агента обратная ссылка указывает на того же жителя.
internal static partial class SaveValidator
{
    /// <summary>Параметры режима жителей из PARM/BTYP (до создания мира): пределы секций, ёмкостей и сетки ячеек.</summary>
    public static LoadStatus ValidateCityParams(LoadContext c)
    {
        if (c.Desc.Demand.Model != DemandModel.Residents)
        {
            return c.Desc.Peds.Source == PedSource.Preform ? LoadStatus.Ok : LoadStatus.InvalidValue;
        }

        long cells = (long)c.CityGridCellsX * c.CityGridCellsZ;
        if (CityBuild.Validate(in c.Desc) != CreateStatus.Ok || c.CityGridCellsX < 1 || c.CityGridCellsZ < 1 || cells > CityGrid.MaxCells ||
            !Fin(c.CityGridOriginX) || !Fin(c.CityGridOriginZ) || Skims.EntriesFor((int)cells, c.Desc.Town.Choice.SkimPeriods) > Skims.MaxSkimEntries)
        {
            return LoadStatus.InvalidValue;
        }

        return LoadStatus.Ok;
    }

    /// <summary>Состояние города (без производных): пулы, ссылки, списки, деньги, скимы, счётчики.</summary>
    public static LoadStatus ValidateCity(LoadContext c)
    {
        World w = c.W;
        CityWorld? city = w.City;
        if (city == null)
        {
            return LoadStatus.Ok;
        }

        LoadStatus st = ValidateLots(c, w, city);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        st = ValidatePool(c, city.Buildings.Pool);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        st = ValidatePool(c, city.Residents.Pool);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        st = ValidateBuildings(c, city);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        st = ValidateCityResidents(c, w, city);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        st = ValidateUtility(c, w, city);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        st = ValidateCityLists(c, city);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        st = ValidateCityMisc(c, w, city);
        return st != LoadStatus.Ok ? st : ValidateParking(c);
    }

    private static LoadStatus ValidateLots(LoadContext c, World w, CityWorld city)
    {
        LotStore l = city.Lots;
        BuildingStore b = city.Buildings;
        for (int i = 0; i < l.Count; i++)
        {
            bool lost = (l.Flags[i] & LotFlags.Lost) != 0;
            bool segOk = (uint)l.Seg[i] < (uint)w.Net.SegmentCount && (lost || w.Net.Segments[l.Seg[i]].Generation == l.SegGen[i]);
            if (!segOk && !lost)
            {
                return Bad(c, i, 2);
            }

            if (l.Side[i] > 1 || l.S0Mm[i] < 0 || l.S1Mm[i] <= l.S0Mm[i] || l.Zone[i] > (byte)LotZone.Industrial || l.Density[i] > 1 || l.Flags[i] > 3)
            {
                return Bad(c, i, 4);
            }

            // Сетка участков (w8, road-profiles.md §4.4): legacy-участок (Orient = 255) клеток не несёт; участок на сетке - ось 0..3 и размер не меньше клетки.
            if (l.Orient[i] == LotOrient.Legacy
                ? (l.CellX[i] != 0 || l.CellZ[i] != 0 || l.CellsF[i] != 0 || l.CellsD[i] != 0)
                : (l.Orient[i] > LotOrient.Max || l.CellsF[i] == 0 || l.CellsD[i] == 0 || w.Net.Profiles == null || l.CellsF[i] > 16 || l.CellsD[i] > 16
                    || Math.Abs((long)l.CellX[i]) > 1_000_000 || Math.Abs((long)l.CellZ[i]) > 1_000_000))
            {
                return Bad(c, i, 10);
            }

            int bi = l.Building[i];
            if (bi != -1 && (!b.Pool.IsAlive(bi) || b.Lot[bi] != i))
            {
                return Bad(c, i, 9);
            }
        }

        return LoadStatus.Ok;
    }

    private static LoadStatus ValidateBuildings(LoadContext c, CityWorld city)
    {
        BuildingStore b = city.Buildings;
        LotStore l = city.Lots;
        int rows = city.Types.Count;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (!b.Pool.IsAlive(i))
            {
                continue;
            }

            int lot = b.Lot[i];
            int type = b.Type[i];
            if ((uint)lot >= (uint)l.Count || l.Building[lot] != i || (uint)type >= (uint)rows || b.State[i] > (byte)BuildingState.Abandoned)
            {
                return Bad(c, i, 5);
            }

            ref readonly BuildingTypeDesc t = ref city.Types.Rows[type];
            if (b.Occupants[i] < 0 || b.Jobs[i] < 0 || b.Jobs[i] > t.Capacity || b.StateTick[i] < 0 || b.StateTick[i] > c.W.Tick ||
                (t.Kind == LotZone.Residential && b.Jobs[i] != 0) || b.LossDays[i] < 0 || b.VacantDays[i] < 0)
            {
                return Bad(c, i, 9);
            }

            if (t.Kind == LotZone.Residential ? b.Occupants[i] > t.Capacity * 2 : b.Occupants[i] > b.Jobs[i] * 2)
            {
                return Bad(c, i, 9);   // разумный предел занятости (>= 2 x вместимости - порча, а не штатное превышение)
            }

            if (!LifeOk(city, b, i))
            {
                return Bad(c, i, 22);
            }
        }

        NeighborhoodFields? nf = city.Fields;
        if (nf != null)
        {
            for (int i = 0; i < nf.Cur.Length; i++)
            {
                if (nf.Cur[i] < 0 || nf.Next[i] < 0)
                {
                    return Bad(c, i, 1);   // LIFE: поля квартала неотрицательны
                }
            }
        }

        return SimCountersLifeOk(c) ? LoadStatus.Ok : Bad(c, 0, 176);
    }

    // building-lifecycle.md §5: Cond <= Ceil <= 10 000; Demolishing => Abandoned; биты - только известные (Monument, Boost - отложены); без режима все поля нули.
    private static bool LifeOk(CityWorld city, BuildingStore b, int i)
    {
        if (!city.Cfg.Lifecycle.Enabled)
        {
            return b.Cond[i] == 0 && b.Ceil[i] == 0 && b.BornDay[i] == 0 && b.LifeFlags[i] == 0 && b.Signif[i] == 0 && b.ArrivalsDay[2 * i] == 0 && b.ArrivalsDay[(2 * i) + 1] == 0;
        }

        byte f = b.LifeFlags[i];
        return b.Signif[i] >= 0 && b.ArrivalsDay[2 * i] >= 0 && b.ArrivalsDay[(2 * i) + 1] >= 0 && b.Cond[i] <= b.Ceil[i] && b.Ceil[i] <= CityLife.Full && b.BornDay[i] >= 0 && b.BornDay[i] <= city.Day
            && (f & ~(BuildingLifeFlags.Demolishing | BuildingLifeFlags.WornNotified | BuildingLifeFlags.Requested)) == 0
            && ((f & BuildingLifeFlags.Demolishing) == 0 || b.State[i] == (byte)BuildingState.Abandoned);
    }

    private static bool SimCountersLifeOk(LoadContext c)
    {
        ref readonly SimCounters k = ref c.W.Counters;
        return k.BuildingsCrumbled >= 0 && k.BuildingsDemolished >= 0 && k.BuildingsResettled >= 0 && k.Restorations >= 0;
    }

    private static LoadStatus ValidateCityResidents(LoadContext c, World w, CityWorld city)
    {
        CityResidents r = city.Residents;
        BuildingStore b = city.Buildings;
        VehicleStoreView veh = new(w);
        int cells = city.Grid.Cells;
        int slots = CityWorld.SlotsPerDay;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            int home = r.Home[i];
            int work = r.Work[i];
            if (!b.Pool.IsAlive(home) || city.Types.Rows[b.Type[home]].Kind != LotZone.Residential)
            {
                return Bad(c, i, 5);
            }

            if (OutsideRef.Is(work) ? city.Outside == null || OutsideRef.Conn(work) >= city.Outside.Count
                : work != -1 && (!b.Pool.IsAlive(work) || city.Types.Rows[b.Type[work]].Kind == LotZone.Residential))
            {
                return Bad(c, i, 6);
            }

            if (r.Flags[i] > (city.Outside != null ? 63 : 31) || r.State[i] > (byte)ResidentState.InLeg || r.EvKind[i] > ResidentEvent.AccessWalkDone || r.EvTick[i] < -1 || r.StateTick[i] < 0 || r.StateTick[i] > w.Tick)
            {
                return Bad(c, i, 9);
            }

            if (r.PlanCount[i] > CityResidents.MaxTours || r.PlanCursor[i] > r.PlanCount[i] || r.LegMode[i] > (byte)TravelMode.Walk || r.LegO[i] >= cells || r.LegD[i] >= cells ||
                r.LegWalk[i] < 0 || r.LegWait[i] < 0 || r.LegRide[i] < 0 || r.SeekDays[i] < 0 || r.EvictDays[i] < 0 || r.Seq[i] < 0 || r.Seq[i] >= r.NextSeq)
            {
                return Bad(c, i, 14);
            }

            int pb = i * CityResidents.MaxTours;
            for (int k = 0; k < CityResidents.MaxTours; k++)
            {
                int dest = r.PlanDest[pb + k];
                if (r.PlanKind[pb + k] > TourKind.Shop || r.PlanMode[pb + k] > (byte)TravelMode.Walk || r.PlanSlot[pb + k] >= slots || !PlaceOk(city, b, dest))
                {
                    return Bad(c, i, 16);
                }
            }

            bool inLeg = r.State[i] == (byte)ResidentState.InLeg;
            if (!inLeg && r.Agent[i] != -1)
            {
                return Bad(c, i, 27);
            }

            if (inLeg && r.Agent[i] < 0)
            {
                // нога агрегата (§9.1): агента нет, прибытие - событие колеса. Пеший участок у машины (parking.md §6): таймер AccessWalkDone и стадия ходьбы.
                bool walkStage = r.EvKind[i] == ResidentEvent.AccessWalkDone && r.LegMode[i] == (byte)TravelMode.Car && city.Parking != null &&
                                 (r.LegStage[i] == LegStages.WalkToCar || r.LegStage[i] == LegStages.WalkFromCar);
                if (!walkStage && r.EvKind[i] != ResidentEvent.Arrive)
                {
                    return Bad(c, i, 27);
                }

                if (r.EvTick[i] < w.Tick)
                {
                    return Bad(c, i, 27);
                }
            }
            else if (inLeg)
            {

                // Машина: жива, поколение совпало, обратная ссылка указывает на того же жителя. Пешеход (peds.source = city): жив, поколение совпало, PedStore.Resident - этот житель.
                if (r.LegMode[i] == (byte)TravelMode.Car &&
                    (!veh.IsAlive(r.Agent[i], r.AgentGen[i]) || veh.ResidentOf(r.Agent[i]) != i))
                {
                    return Bad(c, i, 28);
                }

                if (r.LegMode[i] != (byte)TravelMode.Car &&
                    (w.Peds == null || !w.Peds.CitySource || !w.Peds.P.Pool.IsAlive(r.Agent[i], r.AgentGen[i]) || w.Peds.P.Resident[r.Agent[i]] != i))
                {
                    return Bad(c, i, 29);
                }

                if (r.EvTick[i] != -1)
                {
                    return Bad(c, i, 30);   // в пути события нет: ногу закрывает прибытие агента
                }
            }

            int stops = w.Net.BusStopCount;
            if ((r.EvTick[i] != -1 && r.EvTick[i] < w.Tick) || (r.EvTick[i] == -1) != (r.EvKind[i] == ResidentEvent.None) ||
                !PlaceOk(city, b, r.LegDest[i]) || r.LegStopA[i] < -1 || r.LegStopA[i] >= stops || r.LegStopB[i] < -1 || r.LegStopB[i] >= stops ||
                (r.State[i] == (byte)ResidentState.WaitDepart && r.LegDest[i] == -1) || (r.State[i] == (byte)ResidentState.AtHome && r.Agent[i] != -1))
            {
                return Bad(c, i, 31);
            }
        }

        // Обратные ссылки машин: у каждой машины с Resident >= 0 живой житель в пути на этой машине.
        for (int v = 0; v < veh.HighWater; v++)
        {
            int res = veh.ResidentOf(v);
            if (res < 0 || !veh.Alive(v))   // -1 - без жителя; <= -2 - бригада ЖКХ (ValidateUtility)
            {
                continue;
            }

            if (!r.Pool.IsAlive(res) || r.State[res] != (byte)ResidentState.InLeg || r.LegMode[res] != (byte)TravelMode.Car || r.Agent[res] != v || r.AgentGen[res] != veh.Generation(v))
            {
                return Bad(c, v, 41);
            }
        }

        return LoadStatus.Ok;
    }

    // Службы (building-lifecycle.md §2.10, services.md §5; BL3, SR1): фонд только при службах; станции; вызовы колец; состояния бригад; машины бригад <-> бригады;
    // Requested у цели бригады в пути/на работе.
    private static LoadStatus ValidateUtility(LoadContext c, World w, CityWorld city)
    {
        CityServices? sv = city.Services;
        VehicleStoreView veh = new(w);
        if (sv == null)
        {
            if (city.Ledger.Utility != 0)
            {
                return Bad(c, 0, 60);
            }

            for (int v = 0; v < veh.HighWater; v++)
            {
                if (veh.Alive(v) && veh.ResidentOf(v) <= -2)
                {
                    return Bad(c, v, 61);
                }
            }

            return LoadStatus.Ok;
        }

        BuildingStore b = city.Buildings;
        int bcap = b.Pool.Capacity;
        LoadStatus st0 = ValidateSites(c, city, sv);
        if (st0 != LoadStatus.Ok)
        {
            return st0;
        }

        for (int r = 0; r < sv.Table.Count; r++)
        {
            for (int u2 = 0; u2 < 2; u2++)
            {
                Util.RingBuffer<ServiceCall> ring = u2 == 0 ? sv.Normal[r] : sv.Urgent[r];
                for (int i = 0; i < ring.Count; i++)
                {
                    ref readonly ServiceCall q = ref ring.At(i);
                    if ((uint)q.Target >= (uint)bcap || q.TargetGen < 0 || q.Tick < 0 || q.Tick > w.Tick || q.Svc != r || !sv.Table.HasCall(r, (Api.ServiceCallKind)q.Kind))
                    {
                        return Bad(c, i, 62);
                    }
                }
            }
        }

        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity; k++)
        {
            byte st = u.State[k];
            if (st > CrewState.OffDuty)
            {
                return Bad(c, k, 63);
            }

            if (st == CrewState.Free)
            {
                if (u.Site[k] != -1 || u.Target[k] != -1 || u.Vehicle[k] != -1 || u.Kind[k] != 0)
                {
                    return Bad(c, k, 63);
                }

                continue;
            }

            bool busy = st is CrewState.ToSite or CrewState.Working;
            bool idle = st is CrewState.Idle or CrewState.OffDuty;
            int rec = (uint)u.Site[k] < (uint)sv.Sites.Capacity ? sv.Sites.Svc[u.Site[k]] : -1;
            if ((uint)u.Site[k] >= (uint)sv.Sites.Capacity || sv.Sites.Building[u.Site[k]] < 0 || u.Target[k] < -1 || u.Target[k] >= bcap || u.TargetGen[k] < 0 ||
                u.SiteLot[k] < -1 || u.SiteLot[k] >= city.Lots.Count || u.DueTick[k] < 0 || u.ReqTick[k] < 0 || u.ReqTick[k] > w.Tick ||
                (idle && (u.Target[k] != -1 || u.Vehicle[k] != -1 || u.Kind[k] != 0)) || (busy && (u.Target[k] == -1 || u.Kind[k] == 0)) ||
                (u.Kind[k] != 0 && !sv.Table.HasCall(rec, (Api.ServiceCallKind)u.Kind[k])) || (st == CrewState.OffDuty && !sv.Table.Explicit))
            {
                return Bad(c, k, 64);
            }

            int t = u.Target[k];
            // флаг Requested - только у заявок ремонта ЖКХ; прочие вызовы (SR3/SR4) цель не помечают
            if (busy && u.Kind[k] == (byte)Api.ServiceCallKind.Repair && b.Pool.IsAlive(t, u.TargetGen[k]) && (b.LifeFlags[t] & BuildingLifeFlags.Requested) == 0)
            {
                return Bad(c, k, 65);
            }

            int vi = u.Vehicle[k];
            // машину могли убрать (клапан перегрузки, стоп-кран): бригада дождётся таймаута; живая машина обязана ссылаться на бригаду
            if (vi != -1 && (idle || st == CrewState.Working || vi >= veh.HighWater || (veh.IsAlive(vi, u.VehicleGen[k]) && veh.ResidentOf(vi) != -2 - k)))
            {
                return Bad(c, k, 66);
            }
        }

        // SR2: ползунки 50..150 % шагом 10 (без режима - 100), RespEwma только в режиме
        for (int r = 0; r < sv.Table.Count; r++)
        {
            if (!PctOk(sv.Pct[r], sv.Table.Explicit) || !PctOk(sv.PctPending[r], sv.Table.Explicit))
            {
                return Bad(c, r, 70);
            }
        }

        for (int v = 0; v < veh.HighWater; v++)
        {
            int res = veh.ResidentOf(v);
            if (res > -2 || !veh.Alive(v))
            {
                continue;
            }

            int k = -2 - res;
            if (k >= u.Capacity || u.Vehicle[k] != v || u.VehicleGen[k] != veh.Generation(v))
            {
                return Bad(c, v, 67);
            }
        }

        return LoadStatus.Ok;
    }

    private static bool PctOk(byte pct, bool mode) => mode ? pct is >= 50 and <= 150 && pct % 10 == 0 : pct == 100;

    // Станции (services.md §5): свободный слот пуст; занятый - здание в пределах пула, живое здание станции - вида Service своей записи и уровня; Duty <= UnitsMax;
    // BedsUsed = сумма BedRelease (так же Icu); живое здание - не больше одной станции.
    private static LoadStatus ValidateSites(LoadContext c, CityWorld city, CityServices sv)
    {
        SiteStore s = sv.Sites;
        BuildingStore b = city.Buildings;
        for (int i = 0; i < s.Capacity; i++)
        {
            int bi = s.Building[i];
            if (bi < 0)
            {
                if (bi != -1 || s.Gen[i] != 0 || s.UnitsMax[i] != 0 || s.Duty[i] != 0 || s.BedsUsed[i] != 0 || s.IcuUsed[i] != 0)
                {
                    return Bad(c, i, 68);
                }

                continue;
            }

            long beds = 0;
            long icu = 0;
            for (int d = 0; d < SiteStore.ReleaseDays; d++)
            {
                beds += s.BedRelease[(i * SiteStore.ReleaseDays) + d];
                icu += s.IcuRelease[(i * SiteStore.ReleaseDays) + d];
            }

            if (bi >= b.Pool.Capacity || s.Gen[i] < 0 || s.Svc[i] >= sv.Table.Count || s.Duty[i] > s.UnitsMax[i] || beds != s.BedsUsed[i] || icu != s.IcuUsed[i])
            {
                return Bad(c, i, 68);
            }

            if (b.Pool.IsAlive(bi, s.Gen[i]))
            {
                int type = b.Type[bi];
                if (city.Types.Rows[type].Kind != LotZone.Service || sv.Table.RecOf(type) != s.Svc[i] || sv.Table.LevelOf(type) != s.Level[i])
                {
                    return Bad(c, i, 69);
                }

                for (int j = 0; j < i; j++)
                {
                    if (s.Building[j] == bi && s.Gen[j] == s.Gen[i])
                    {
                        return Bad(c, i, 69);
                    }
                }
            }
        }

        return LoadStatus.Ok;
    }

    /// <summary>«Место» в поле жителя: -1, живое здание или код существующего внешнего подключения (ADR-0037).</summary>
    private static bool PlaceOk(CityWorld city, BuildingStore b, int place) =>
        place == -1 || (place >= 0 ? b.Pool.IsAlive(place) : city.Outside != null && OutsideRef.Conn(place) < city.Outside.Count);

    /// <summary>
    /// Блок <c>OUTS</c> (ADR-0037): места не меньше занятых, занятые = число жителей с работой у подключения, счётчики неотрицательны. Узлы подключений - живые с флагом <c>Outside</c>.
    /// </summary>
    internal static LoadStatus ValidateOutside(LoadContext c)
    {
        World w = c.W;
        CityWorld? city = w.City;
        OutsideState? o = city?.Outside;
        if (city == null || o == null)
        {
            return LoadStatus.InvalidValue;
        }

        CityResidents r = city.Residents;
        for (int k = 0; k < o.Count; k++)
        {
            int node = o.Node[k];
            if (!w.Net.NodeAlive(node) || (w.Net.Nodes[node].Flags & Network.NodeFlags.Outside) == 0 || o.Taken[k] < 0 || o.Jobs[k] < o.Taken[k] ||
                o.ArrivalsDay[k] < 0 || o.ArrivalsHour[k] < 0)
            {
                return Bad(c, k, 1);
            }

            int code = OutsideRef.Of(k);
            int taken = 0;
            for (int i = 0; i < r.HighWater; i++)
            {
                taken += r.Pool.IsAlive(i) && r.Work[i] == code ? 1 : 0;
            }

            if (taken != o.Taken[k])
            {
                return Bad(c, k, 2);
            }
        }

        return o.Arrivals < 0 || o.ArrivalTimeouts < 0 ? Bad(c, 0, 3) : LoadStatus.Ok;
    }

    /// <summary>Списки жителей дома и работников: цепочки без циклов, члены ссылаются на здание, длина = Occupants, охвачены все жители.</summary>
    private static LoadStatus ValidateCityLists(LoadContext c, CityWorld city)
    {
        CityResidents r = city.Residents;
        BuildingStore b = city.Buildings;
        int hw = r.HighWater;
        long housed = 0;
        long employed = 0;
        for (int bi = 0; bi < b.HighWater; bi++)
        {
            if (!b.Pool.IsAlive(bi))
            {
                continue;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                int head = pass == 0 ? b.HeadRes[bi] : b.HeadWork[bi];
                int[] next = pass == 0 ? r.NextHome : r.NextWork;
                int[] owner = pass == 0 ? r.Home : r.Work;
                int len = 0;
                for (int m = head; m != -1; m = next[m])
                {
                    if ((uint)m >= (uint)hw || !r.Pool.IsAlive(m) || owner[m] != bi || ++len > hw)
                    {
                        return Bad(c, bi, pass == 0 ? 11u : 12u);
                    }
                }

                bool residential = city.Types.Rows[b.Type[bi]].Kind == LotZone.Residential;
                bool relevant = (pass == 0) == residential;   // жилое здание считает жителей, фирма - работников; в другом списке пусто
                if (relevant ? len != b.Occupants[bi] : len != 0)
                {
                    return Bad(c, bi, 9);
                }

                if (pass == 0)
                {
                    housed += len;
                }
                else
                {
                    employed += len;
                }
            }
        }

        long wantHome = 0;
        long wantWork = 0;
        for (int i = 0; i < hw; i++)
        {
            if (r.Pool.IsAlive(i))
            {
                wantHome++;
                wantWork += r.Work[i] >= 0 ? 1 : 0;   // работники внешних фирм (код подключения) - в OUTS.Taken, не в списках зданий
            }
        }

        if (housed != wantHome || employed != wantWork)
        {
            return Bad(c, 0, 11);
        }

        for (int k = 0; k < r.SeekQueue.Count; k++)
        {
            ref readonly SeekEntry e = ref r.SeekQueue.At(k);
            if ((uint)e.Index >= (uint)hw || e.Generation < 0)
            {
                return Bad(c, k, 36);
            }
        }

        return LoadStatus.Ok;
    }

    private static LoadStatus ValidateCityMisc(LoadContext c, World w, CityWorld city)
    {
        if (city.Day < 0 || (int)city.Exec > (int)TripExecution.Aggregate || city.ExecPending < -1 || city.ExecPending > (int)TripExecution.Aggregate ||
            city.CursorResidents < 0 || city.CursorBuildings < 0 || city.CursorAccess < 0 || (uint)city.DayStage > DayStages.Idle || city.Ledger.SumAll() != 0)
        {
            return Bad(c, 0, 16);
        }

        Skims s = city.Skims;
        if (s.CurPeriod < 0 || s.FrozenN < 0 || s.FrozenN > s.FrozenKey.Length)
        {
            return Bad(c, 0, 5);
        }

        for (int i = 0; i < s.FrozenN; i++)
        {
            if ((uint)s.FrozenKey[i] >= (uint)s.Skim.Length || s.FrozenCount[i] < 1 || (i > 0 && s.FrozenKey[i] <= s.FrozenKey[i - 1]))
            {
                return Bad(c, i, 10);
            }
        }

        for (int i = 0; i < s.K.Length; i++)
        {
            if (s.K[i] < 0)
            {
                return Bad(c, i, 2);
            }
        }

        for (int i = 0; i < city.LegsDay.Length; i++)
        {
            if (city.LegsDay[i] < 0 || city.LegCostDay[i] < 0)
            {
                return Bad(c, i, 13);
            }
        }

        for (int i = 0; i < city.Loads.L.Length; i++)
        {
            if (city.Loads.L[i] < 0)
            {
                return Bad(c, i, 15);
            }
        }

        LoadStatus bs = ValidateBudget(c, w, city);
        if (bs != LoadStatus.Ok)
        {
            return bs;
        }

        ref readonly SimCounters k = ref w.Counters;
        bool ok = k.CityEvents >= 0 && k.ResToursPlanned >= 0 && k.ResLegsDone >= 0 && k.ResStuck >= 0 && k.Immigrants >= 0 && k.Emigrants >= 0 && k.CityDays >= 0 && k.SkimObserved >= 0;
        return ok ? LoadStatus.Ok : Bad(c, 0, 118);
    }

    /// <summary>
    /// Казна города (city-economy.md §12): политики в пределах параметров, ожидающая согласована с флагом, цена земли в [lv_min, lv_max], сглаженные величины и переносы остатков в диапазоне,
    /// записи undo - нули либо живые и не истёкшие, <c>Insolvent</c> согласован с долгом. Без бюджета: фонд оплаты зданий нулевой.
    /// </summary>
    private static LoadStatus ValidateBudget(LoadContext c, World w, CityWorld city)
    {
        BuildingStore bl = city.Buildings;
        CityBudget? b = city.Budget;
        for (int i = 0; i < bl.HighWater; i++)
        {
            if (bl.PayrollEwma[i] < 0 || (b == null && bl.PayrollEwma[i] != 0))
            {
                return Bad(c, i, 21);
            }
        }

        if (b == null)
        {
            return LoadStatus.Ok;
        }

        CityEconomyDesc e = city.Cfg.Economy;
        if (CityBudget.CheckPolicy(in e, in city.Cfg.Lifecycle, in b.Policy) != RejectReason.None || b.HasPending > 1 ||
            (b.HasPending == 1 ? CityBudget.CheckPolicy(in e, in city.Cfg.Lifecycle, in b.PendingPolicy) != RejectReason.None : !CityBudget.Same(in b.PendingPolicy, default(BudgetPolicy))))
        {
            return Bad(c, 0, 9);
        }

        for (int i = 0; i < b.LandValue.Length; i++)
        {
            if (b.LandValue[i] < e.LvMin || b.LandValue[i] > e.LvMax)
            {
                return Bad(c, i, 18);
            }
        }

        if (b.IncomeEwma < 0 || b.ExpenseEwma < 0 || b.DebtDays < 0 || b.OverLimitDays < 0 || b.Insolvent > 1 || (b.Insolvent == 1 && b.DebtDays < 1))
        {
            return Bad(c, 0, 19);
        }

        for (int i = 0; i < b.Carry.Length; i++)
        {
            if (b.Carry[i] < 0 || b.Carry[i] >= Ledger.Bp)
            {
                return Bad(c, i, 21);
            }
        }

        for (int i = 0; i < b.UndoCredits.Length; i++)
        {
            ref readonly UndoCredit u = ref b.UndoCredits[i];
            bool dead = u.TargetGeneration == 0 && u.TargetIndex == 0 && u.Amount == 0 && u.ExpireTick == 0;
            if (!dead && (u.TargetIndex < 0 || u.TargetGeneration <= 0 || u.ExpireTick < w.Tick))
            {
                return Bad(c, i, 25);
            }
        }

        for (int i = 0; i < 2; i++)
        {
            if (b.FareEvadedDay[i] < 0 || b.DebtEvictionsDay[i] < 0)
            {
                return Bad(c, i, 29);
            }
        }

        ref readonly SimCounters k = ref w.Counters;
        return k.FareEvaded >= 0 && k.DebtEvictions >= 0 && k.InsufficientFunds >= 0 ? LoadStatus.Ok : Bad(c, 0, 154);
    }

    /// <summary>Узкий доступ к машинам для проверок города (индекс, живость, поколение, ссылка на жителя).</summary>
    private readonly struct VehicleStoreView
    {
        private readonly Agents.VehicleStore _v;

        public VehicleStoreView(World w) => _v = w.Vehicles;

        public int HighWater => _v.HighWater;

        public bool Alive(int i) => _v.Pool.IsAlive(i);

        public bool IsAlive(int i, int gen) => _v.Pool.IsAlive(i, gen);

        public int Generation(int i) => _v.Pool.GenerationOf(i);

        public int ResidentOf(int i) => _v.Resident[i];
    }
}
