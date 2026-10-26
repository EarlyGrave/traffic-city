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

        st = ValidateCityLists(c, city);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        return ValidateCityMisc(c, w, city);
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
        }

        return LoadStatus.Ok;
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

            if (work != -1 && (!b.Pool.IsAlive(work) || city.Types.Rows[b.Type[work]].Kind == LotZone.Residential))
            {
                return Bad(c, i, 6);
            }

            if (r.Flags[i] > 15 || r.State[i] > (byte)ResidentState.InLeg || r.EvKind[i] > ResidentEvent.Arrive || r.EvTick[i] < -1 || r.StateTick[i] < 0 || r.StateTick[i] > w.Tick)
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
                if (r.PlanKind[pb + k] > TourKind.Shop || r.PlanMode[pb + k] > (byte)TravelMode.Walk || r.PlanSlot[pb + k] >= slots || (dest != -1 && !b.Pool.IsAlive(dest)))
                {
                    return Bad(c, i, 16);
                }
            }

            bool inLeg = r.State[i] == (byte)ResidentState.InLeg;
            if (!inLeg && r.Agent[i] != -1)
            {
                return Bad(c, i, 27);
            }

            if (inLeg)
            {
                if (r.Agent[i] < 0)
                {
                    return Bad(c, i, 27);
                }

                // Машина: жива, поколение совпало, обратная ссылка указывает на того же жителя. Пешеходы: индексы заготовок/пешеходов связывает Z2 (проверка расширяется там же).
                if (r.LegMode[i] == (byte)TravelMode.Car &&
                    (!veh.IsAlive(r.Agent[i], r.AgentGen[i]) || veh.ResidentOf(r.Agent[i]) != i))
                {
                    return Bad(c, i, 28);
                }
            }
        }

        // Обратные ссылки машин: у каждой машины с Resident >= 0 живой житель в пути на этой машине.
        for (int v = 0; v < veh.HighWater; v++)
        {
            int res = veh.ResidentOf(v);
            if (res == -1 || !veh.Alive(v))
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
                wantWork += r.Work[i] != -1 ? 1 : 0;
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
            city.CursorResidents < 0 || city.CursorBuildings < 0 || city.CursorAccess < 0 || city.Ledger.SumAll() != 0)
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

        ref readonly SimCounters k = ref w.Counters;
        bool ok = k.CityEvents >= 0 && k.ResToursPlanned >= 0 && k.ResLegsDone >= 0 && k.ResStuck >= 0 && k.Immigrants >= 0 && k.Emigrants >= 0 && k.CityDays >= 0 && k.SkimObserved >= 0;
        return ok ? LoadStatus.Ok : Bad(c, 0, 118);
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
