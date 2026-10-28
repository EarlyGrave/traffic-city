using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Parking;

/// <summary>
/// Создание объектов стоянки и их производные (parking.md §3): обочины участков (<c>Create</c>, <c>BuildRoad</c>), стоянки и гаражи зданий (переход в <c>Active</c>, <c>Abandoned</c> -> <c>Closing</c>,
/// снос), пересчёт ёмкости обочины, производные положения и окрестности. Все обходы - по возрастанию индекса участка и здания, ничьих и PRNG нет. Без режима (<c>c.Parking == null</c>) каждый вход
/// возвращается сразу: ни одна ветка парковки не исполняется (PK1).
/// </summary>
internal static class ParkingBuild
{
    /// <summary>Создание города: обочины всех участков по возрастанию индекса, затем стоянки активных зданий по возрастанию индекса; производные и окрестности.</summary>
    public static void CreateAll(World w, CityWorld c)
    {
        ParkingStore p = c.Parking!;
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            TryCreateCurb(w, c, p, lot);
        }

        BuildingStore b = c.Buildings;
        for (int bi = 0; bi < b.HighWater; bi++)
        {
            if (b.Pool.IsAlive(bi) && b.State[bi] == (byte)BuildingState.Active)
            {
                TryCreateBuilding(w, c, p, bi);
            }
        }

        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            RecomputeCurb(w, c, p, lot);   // слот въезда стоянки здания учтён только теперь (обочины созданы раньше стоянок)
        }

        RefreshAll(w, c);
    }

    /// <summary>Хук <c>BuildRoad</c> (Z6): обочины участков с индексами от <paramref name="firstLot"/> (дописанные сегментом); затем производные и окрестности целиком.</summary>
    public static void OnLotsAdded(World w, CityWorld c, int firstLot)
    {
        if (c.Parking == null)
        {
            return;
        }

        for (int lot = firstLot; lot < c.Lots.Count; lot++)
        {
            TryCreateCurb(w, c, c.Parking, lot);
        }

        RefreshAll(w, c);
    }

    /// <summary>Хук финала <c>RemoveSegment</c>: участок получил <c>Lost</c> - обочина снесена (здание уже заброшено, его стоянка закрывается).</summary>
    public static void OnLotLost(World w, CityWorld c, int lot)
    {
        ParkingStore? p = c.Parking;
        if (p == null || (uint)lot >= (uint)p.CurbOfLot.Length)
        {
            return;
        }

        int f = p.CurbOfLot[lot];
        if (f > 0)
        {
            ParkingOps.Remove(w, f);
        }
    }

    /// <summary>Хук <c>Growth.Complete</c>: здание стало <c>Active</c> - создаётся его стоянка (или гараж), обочина участка теряет слот въезда.</summary>
    public static void OnBuildingActive(World w, CityWorld c, int bi)
    {
        ParkingStore? p = c.Parking;
        if (p == null || p.FacOfBuilding[bi] >= 0)
        {
            return;
        }

        if (TryCreateBuilding(w, c, p, bi))
        {
            int lot = c.Buildings.Lot[bi];
            RefreshObject(w, c, p, p.FacOfBuilding[bi]);
            RecomputeCurb(w, c, p, lot);
            p.Catch.RebuildNear(w, c, p.X[p.FacOfBuilding[bi]], p.Z[p.FacOfBuilding[bi]]);
        }
    }

    /// <summary>Хук <c>Growth.Abandon</c>: стоянка здания закрывается (<c>Closing</c>): новых резервов и закреплений нет, стоящие и закрепившие уйдут сами.</summary>
    public static void OnBuildingAbandoned(World w, CityWorld c, int bi)
    {
        ParkingStore? p = c.Parking;
        if (p == null)
        {
            return;
        }

        int f = p.FacOfBuilding[bi];
        if (f > 0)
        {
            p.Flags[f] |= ParkFlags.Closing;
            p.Version++;
        }
    }

    /// <summary>Хук сноса здания (<c>Growth.RemoveAbandoned</c>, до <c>Buildings.Destroy</c>): стоянка здания снесена, обочина участка получает слот въезда назад.</summary>
    public static void OnBuildingRemoved(World w, CityWorld c, int bi)
    {
        ParkingStore? p = c.Parking;
        if (p == null)
        {
            return;
        }

        int f = p.FacOfBuilding[bi];
        int lot = c.Buildings.Lot[bi];
        if (f > 0)
        {
            ParkingOps.Remove(w, f);
        }

        if (lot >= 0)
        {
            RecomputeCurb(w, c, p, lot);
        }
    }

    // ------------------------------------------------------------------ создание

    private static void TryCreateCurb(World w, CityWorld c, ParkingStore p, int lot)
    {
        LotStore l = c.Lots;
        if (p.CurbOfLot[lot] >= 0 || LotBuilder.IsDead(w, c, lot) || l.CarLane[lot] < 0)
        {
            return;
        }

        CurbMode mode = w.Net.Segments[l.Seg[lot]].Kind == RoadKind.Arterial ? p.Cfg.CurbArterial : p.Cfg.CurbStreet;
        if (mode == CurbMode.Off)
        {
            return;
        }

        byte flags = ParkFlags.Public;
        if (p.Cfg.CurbPermits)
        {
            flags |= ParkFlags.Permit;
        }

        int fee = mode == CurbMode.Paid ? p.Cfg.CurbFee : 0;
        if (p.TryCreate(ParkKind.Curb, flags, lot, -1, 0, 0, fee, out int f))
        {
            p.Capacity[f] = CurbCapacity(w, c, p, lot);
        }
    }

    private static bool TryCreateBuilding(World w, CityWorld c, ParkingStore p, int bi)
    {
        BuildingStore b = c.Buildings;
        int lot = b.Lot[bi];
        if (lot < 0 || p.FacOfBuilding[bi] >= 0 || LotBuilder.IsDead(w, c, lot))
        {
            return false;
        }

        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        byte kind;
        byte flags;
        int capacity;
        int fee;
        if (t.Kind == LotZone.Parking)
        {
            kind = ParkKind.Garage;
            flags = ParkFlags.Public;
            if (p.Cfg.GaragePermits)
            {
                flags |= ParkFlags.Permit;
            }

            capacity = t.Capacity;
            fee = t.ParkingFee > 0 ? t.ParkingFee : p.Cfg.GarageFee;
        }
        else
        {
            if (t.ParkingCapacity <= 0)
            {
                return false;
            }

            kind = ParkKind.Building;
            flags = t.ParkingAccess == ParkingAccess.Public ? ParkFlags.Public : (byte)0;
            if (t.Kind == LotZone.Residential)
            {
                flags |= ParkFlags.Permit;
            }

            capacity = t.ParkingCapacity;
            fee = t.ParkingFee;
        }

        return p.TryCreate(kind, flags, lot, bi, b.Pool.GenerationOf(bi), capacity, fee, out _);
    }

    /// <summary>
    /// Ёмкость обочины участка (parking.md §3): <c>floor(фасад / slot)</c> минус слот въезда, если у здания участка своя стоянка, минус запретные зоны остановок
    /// (<c>ceil(пересечение отрезка фасада с [S - stop_clear, S + stop_clear] / slot)</c> на каждую остановку той же полосы). Переходы на перегоне не учитываются (K2+).
    /// </summary>
    internal static int CurbCapacity(World w, CityWorld c, ParkingStore p, int lot)
    {
        LotStore l = c.Lots;
        long slot = p.Cfg.CurbSlotMm;
        long frontage = l.S1Mm[lot] - (long)l.S0Mm[lot];
        long cap = frontage / slot;
        int bi = l.Building[lot];
        if (bi >= 0 && p.FacOfBuilding[bi] >= 0)
        {
            cap--;
        }

        int lane = l.CarLane[lot];
        if (lane >= 0 && cap > 0)
        {
            long center = DetMath.ToLongSat(MathF.Round(l.CarS[lot] * 1000f));
            long lo = center - (frontage / 2);
            long hi = lo + frontage;
            NetworkStore net = w.Net;
            for (int s = 0; s < net.BusStopCount; s++)
            {
                ref readonly BusStopData st = ref net.BusStops[s];
                if (st.Lane != lane)
                {
                    continue;
                }

                long sMm = DetMath.ToLongSat(MathF.Round(st.S * 1000f));
                long overlap = Math.Min(hi, sMm + p.Cfg.CurbStopClearMm) - Math.Max(lo, sMm - p.Cfg.CurbStopClearMm);
                if (overlap > 0)
                {
                    cap -= (overlap + slot - 1) / slot;
                }
            }
        }

        return (int)Math.Max(0L, cap);
    }

    private static void RecomputeCurb(World w, CityWorld c, ParkingStore p, int lot)
    {
        if ((uint)lot >= (uint)p.CurbOfLot.Length)
        {
            return;
        }

        int f = p.CurbOfLot[lot];
        if (f > 0)
        {
            int cap = CurbCapacity(w, c, p, lot);
            if (cap != p.Capacity[f])
            {
                p.Capacity[f] = cap;   // уменьшение резервы и стоящих не трогает: Free уходит в 0, лишние уйдут сами (parking.md §3)
                p.Version++;
            }
        }
    }

    // ------------------------------------------------------------------ производные

    /// <summary>Производные всех объектов (положение, точка въезда и пешехода, компоненты, ходьба до двери, <c>HomeAway</c>), корзины и окрестности целиком. Зовут <see cref="LotBuilder.RestoreDerived"/> и загрузка.</summary>
    public static void RefreshAll(World w, CityWorld c)
    {
        ParkingStore? p = c.Parking;
        if (p == null)
        {
            return;
        }

        for (int f = 1; f < p.Pool.HighWater; f++)
        {
            if (p.Pool.IsAlive(f))
            {
                RefreshObject(w, c, p, f);
            }
        }

        RecountHomeAway(c, p);
        p.Catch.RebuildAll(w, c);
    }

    private static void RefreshObject(World w, CityWorld c, ParkingStore p, int f)
    {
        LotStore l = c.Lots;
        int lot = p.Lot[f];
        if ((uint)lot >= (uint)l.Count || LotBuilder.IsDead(w, c, lot))
        {
            p.X[f] = 0f;
            p.Z[f] = 0f;
            p.AccessLane[f] = -1;
            p.AccessS[f] = 0f;
            p.PedEdge[f] = -1;
            p.PedSMm[f] = 0;
            p.RoadComp[f] = -1;
            p.PedComp[f] = -1;
            p.WalkMm[f] = 0;
            return;
        }

        LotBuilder.FrontPoint(w, c, lot, out float x, out float z);
        p.X[f] = x;
        p.Z[f] = z;
        int lane = l.CarLane[lot];
        p.AccessLane[f] = lane;
        if (lane >= 0)
        {
            float max = MathF.Max(0f, w.Net.LaneLength[lane] - w.Rules.MinPhysicalGap);
            p.AccessS[f] = Math.Clamp(l.CarS[lot], 0f, max);
        }
        else
        {
            p.AccessS[f] = 0f;
        }

        p.PedEdge[f] = l.PedEdge[lot];
        p.PedSMm[f] = l.PedSMm[lot];
        p.RoadComp[f] = l.RoadComp[lot];
        p.PedComp[f] = l.PedComp[lot];
        p.WalkMm[f] = p.Kind[f] == ParkKind.Curb ? Catchment.Manhattan(x - l.X[lot], z - l.Z[lot]) : 0;
    }

    /// <summary><c>HomeAway</c> каждого объекта: закрепивших, чья машина стоит не здесь (производное жителей; инкрементально ведут K2+).</summary>
    internal static void RecountHomeAway(CityWorld c, ParkingStore p)
    {
        Array.Clear(p.HomeAway, 0, p.Pool.HighWater);
        CityResidents r = c.Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            int hf = r.HomeFac[i];
            if (r.Pool.IsAlive(i) && hf > 0 && hf < p.Pool.HighWater && r.CarFac[i] != hf)
            {
                p.HomeAway[hf]++;
            }
        }
    }
}
