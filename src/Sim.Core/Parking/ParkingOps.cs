using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Parking;

/// <summary>
/// Операции над местами (parking.md §5-§7). K2: <see cref="TryReserve"/> (резерв при заявке машинной ноги), <see cref="Release"/> (единственная точка снятия резерва, PK2), <see cref="Park"/> (постановка гостя
/// при прибытии), <see cref="OnDeparture"/> (машина покидает место при постановке на дорогу), <see cref="OnArrival"/> (общая точка прибытия micro и meso: <c>TripLegs.OnVehicleArrived</c> зовут и
/// <c>ArrivalPhase</c>, и <c>QueueMeso</c>). Сигнатуры <c>TryReserve/Release/Park/Remove</c> заморожены. <see cref="Remove"/> - снос объекта (K1; K3 добавит машины в пути, заявки и поиск, §7 п. 1-2).
/// Без режима (<c>c.Parking == null</c>) ничего из этого не вызывается.
/// </summary>
internal static class ParkingOps
{
    // ------------------------------------------------------------------ резерв

    /// <summary>
    /// Резерв места у цели машинной ноги жителя <paramref name="r"/> (§5). Обратная нога домой: <c>fac = HomeFac</c> (закреплено, <c>Reserved</c> не меняется), нет домашнего места - <c>Overflow</c> (<c>fac = 0</c>).
    /// Иначе - окрестность участка цели: допустим живой не <c>Closing</c> объект со <c>Free &gt; 0</c> и точкой въезда, <c>Public</c> либо стоянка здания цели; минимум стоимости
    /// <c>walk · w_walk + fee · fee_ds_per_money</c> (дс, целые), ничьи - по порядку окрестности (индекс объекта). Успех для гостя: <c>Reserved++</c>. Состояние жителя не меняется.
    /// </summary>
    public static bool TryReserve(World w, int r, int destBuilding, out int fac)
    {
        fac = -1;
        CityWorld? c = w.City;
        ParkingStore? p = c?.Parking;
        if (c == null || p == null || destBuilding < 0)
        {
            return false;
        }

        CityResidents res = c.Residents;
        if (destBuilding == res.Home[r])
        {
            int hf = res.HomeFac[r];
            fac = hf > 0 && p.Pool.IsAlive(hf, res.HomeFacGen[r]) && p.AccessLane[hf] >= 0 ? hf : 0;
            return true;
        }

        int lot = c.Buildings.Lot[destBuilding];
        if (lot < 0)
        {
            return false;
        }

        Catchment cat = p.Catch;
        int b0 = lot * cat.Stride;
        int n = cat.Count[lot];
        long best = long.MaxValue;
        int speed = p.Cfg.WalkSpeedMmS;
        int wWalk = c.Cfg.Choice.WWalkQ8;
        for (int k = 0; k < n; k++)
        {
            int f = cat.Fac[b0 + k];
            byte flags = p.Flags[f];
            if ((flags & ParkFlags.Closing) != 0 || p.Free(f) <= 0 || p.AccessLane[f] < 0)
            {
                continue;
            }

            if ((flags & ParkFlags.Public) == 0 && p.Building[f] != destBuilding)
            {
                continue;
            }

            long walkDs = (long)cat.WalkMm[b0 + k] * 10 / speed;
            long cost = ((walkDs * wWalk) >> 8) + ((long)p.Fee[f] * p.Cfg.FeeDsPerMoney);
            if (cost < best)
            {
                best = cost;
                fac = f;
            }
        }

        if (fac < 0)
        {
            return false;
        }

        p.Reserved[fac]++;
        return true;
    }

    /// <summary>Освобождение резерва <paramref name="fac"/> (§5, единственная точка для PK2): поездка удалена, заявка снята, прибытие. Мёртвый или нулевой объект игнорируется.</summary>
    public static void Release(World w, int fac)
    {
        ParkingStore? p = w.City?.Parking;
        if (p != null && fac > 0 && p.Pool.IsAlive(fac) && p.Reserved[fac] > 0)
        {
            p.Reserved[fac]--;
        }
    }

    /// <summary>Освобождение резерва заявки или машины по ссылке <c>ParkRef = объект + 1</c> и поколению (мёртвый объект: резервы обнулил снос).</summary>
    internal static void ReleaseRef(World w, int parkRef, int parkGen)
    {
        ParkingStore? p = w.City?.Parking;
        if (p != null && parkRef > 1 && p.Pool.IsAlive(parkRef - 1, parkGen))
        {
            Release(w, parkRef - 1);
        }
    }

    // ------------------------------------------------------------------ положение машины

    /// <summary>Полоса и позиция выезда машины жителя из её текущего места (<c>AccessLane/AccessS</c> объекта; <c>Overflow</c> - въезд участка <c>CarLot</c>). false - места нет (нет машины, объект потерян).</summary>
    internal static bool TryOrigin(World w, int r, out int lane, out float s)
    {
        CityWorld c = w.City!;
        ParkingStore p = c.Parking!;
        CityResidents res = c.Residents;
        lane = -1;
        s = 0f;
        int cf = res.CarFac[r];
        if (cf > 0 && p.Pool.IsAlive(cf, res.CarFacGen[r]))
        {
            lane = p.AccessLane[cf];
            s = p.AccessS[cf];
        }
        else if (cf == 0 && (uint)res.CarLot[r] < (uint)c.Lots.Count)
        {
            lane = c.Lots.CarLane[res.CarLot[r]];
            s = lane >= 0 ? ParkingBuild.GateSafeS(w, lane, c.Lots.CarS[res.CarLot[r]]) : 0f;
        }

        return lane >= 0;
    }

    /// <summary>Полоса и позиция прибытия к месту <paramref name="fac"/>; <c>Overflow</c> (0) - въезд участка <paramref name="lotOfDest"/>.</summary>
    internal static bool TryAccess(World w, int fac, int lotOfDest, out int lane, out float s)
    {
        CityWorld c = w.City!;
        ParkingStore p = c.Parking!;
        lane = -1;
        s = 0f;
        if (fac > 0)
        {
            lane = p.AccessLane[fac];
            s = p.AccessS[fac];
        }
        else if (lotOfDest >= 0)
        {
            lane = c.Lots.CarLane[lotOfDest];
            s = lane >= 0 ? ParkingBuild.GateSafeS(w, lane, c.Lots.CarS[lotOfDest]) : 0f;
        }

        return lane >= 0;
    }

    /// <summary>Ходьба, мм, между объектом <paramref name="fac"/> и зданием участка <paramref name="lot"/> (по окрестности участка; <c>Overflow</c> и объект вне окрестности - 0).</summary>
    internal static int WalkTo(ParkingStore p, int lot, int fac)
    {
        if (fac <= 0 || lot < 0)
        {
            return 0;
        }

        Catchment cat = p.Catch;
        int b0 = lot * cat.Stride;
        int n = cat.Count[lot];
        for (int k = 0; k < n; k++)
        {
            if (cat.Fac[b0 + k] == fac)
            {
                return cat.WalkMm[b0 + k];
            }
        }

        return 0;
    }

    /// <summary>Житель вернулся домой не на своей машине, а машина стоит у дома (после эвакуации, <c>CarWalkMm = -1</c>): ходьба от двери до места снова определена.</summary>
    internal static void OnReturnedHome(CityWorld c, int r)
    {
        ParkingStore p = c.Parking!;
        CityResidents res = c.Residents;
        int homeLot = res.Home[r] >= 0 ? c.Buildings.Lot[res.Home[r]] : -1;
        int cf = res.CarFac[r];
        if (cf > 0 && cf == res.HomeFac[r])
        {
            res.CarWalkMm[r] = WalkTo(p, homeLot, cf);
        }
        else if (cf == 0 && res.CarLot[r] == homeLot)
        {
            res.CarWalkMm[r] = 0;
        }
    }

    /// <summary>Длительность ходьбы в тиках: <c>ceil(мм · 20 / walk_speed_mm_s)</c>.</summary>
    internal static int WalkTicks(ParkingStore p, int mm)
    {
        if (mm <= 0)
        {
            return 0;
        }

        long speed = p.Cfg.WalkSpeedMmS;
        return (int)Math.Min(int.MaxValue / 2, (((long)mm * 20) + speed - 1) / speed);
    }

    // ------------------------------------------------------------------ выезд и прибытие

    /// <summary>
    /// Машина жителя <paramref name="r"/> поставлена на дорогу (§6, стадия <c>Drive</c>): место освобождается в момент выезда (гость: <c>Occupied--</c>; <c>Overflow</c>: <c>Occupied[0]--</c>, <c>CarLot = -1</c>;
    /// домашнее: <c>HomeAway++</c>), <c>CarFac = -1</c>.
    /// </summary>
    internal static void OnDeparture(World w, int r)
    {
        CityWorld c = w.City!;
        ParkingStore p = c.Parking!;
        CityResidents res = c.Residents;
        int cf = res.CarFac[r];
        if (cf == 0)
        {
            p.Occupied[0]--;
            res.CarLot[r] = -1;
        }
        else if (cf > 0 && p.Pool.IsAlive(cf, res.CarFacGen[r]))
        {
            if (cf == res.HomeFac[r] && res.HomeFacGen[r] == res.CarFacGen[r])
            {
                p.HomeAway[cf]++;
            }
            else
            {
                p.Occupied[cf]--;
            }
        }

        res.CarFac[r] = -1;
        res.CarFacGen[r] = 0;
        res.LegStage[r] = LegStages.Drive;
    }

    /// <summary>
    /// Прибытие машины жителя (фаза 8 micro и <c>QueueMeso.Arrive</c>, до деспауна) - единственная точка постановки: резерв гостя (<c>ParkFac</c> машины) либо дом (<c>HomeFac</c> / <c>Overflow</c>).
    /// Возвращает ходьбу от места до двери, мм (0 - стадии нет). Дальше <c>TripLegs</c> запускает <c>WalkFromCar</c> либо кладёт жителя в кольцо прибытий.
    /// </summary>
    internal static int OnArrival(World w, int vehicle, int r)
    {
        CityWorld c = w.City!;
        ParkingStore p = c.Parking!;
        VehicleStore v = w.Vehicles;
        CityResidents res = c.Residents;
        int fac = v.ParkFac[vehicle];
        if (fac > 0 && p.Pool.IsAlive(fac, v.ParkFacGen[vehicle]))
        {
            Park(w, r, fac);
        }
        else
        {
            ParkHome(w, r);
        }

        if (v.ParkSearchTick[vehicle] >= 0)
        {
            res.LegParkWait[r] += (int)Math.Min(int.MaxValue / 2, w.Tick - v.ParkSearchTick[vehicle]);   // поиск после потери резерва (K3) входит в ожидание ноги
        }

        res.LegStage[r] = LegStages.None;
        return Math.Max(0, res.CarWalkMm[r]);
    }

    /// <summary>
    /// Машина переезжает на место без выезда на дорогу (цель позади на том же направленном сегменте, маршрута «назад» нет): <see cref="OnDeparture"/> и постановка. <paramref name="guestFac"/> -
    /// зарезервированное место гостя либо -1 (дом: <c>HomeFac</c> / <c>Overflow</c>).
    /// </summary>
    internal static void Hop(World w, int r, int guestFac)
    {
        OnDeparture(w, r);
        if (guestFac > 0)
        {
            Park(w, r, guestFac);
        }
        else
        {
            ParkHome(w, r);
        }

        w.City!.Residents.LegStage[r] = LegStages.None;
    }

    /// <summary>
    /// Единая точка прибытия для meso (<c>Lod/QueueMeso.Arrive</c>): сегодня <c>QueueMeso</c> уже зовёт <c>TripLegs.OnVehicleArrived</c>, который идёт в <see cref="OnArrival"/>, поэтому правка <c>Lod/**</c>
    /// не нужна; метод оставлен как документированный вход, если после слияния N3 прибытие meso станет отдельным путём.
    /// </summary>
    internal static int OnMesoArrival(World w, int vehicle, int r) => OnArrival(w, vehicle, r);

    /// <summary>
    /// Постановка гостя на зарезервированное место <paramref name="fac"/> (§6): <c>Reserved--</c>, <c>Occupied++</c> (закреплённое место самого жителя: только <c>Reserved--</c>, закрепление уже учтено),
    /// <c>CarFac = fac</c>, ходьба до здания цели ноги. Плата (§8) - K5.
    /// </summary>
    public static void Park(World w, int r, int fac)
    {
        CityWorld c = w.City!;
        ParkingStore p = c.Parking!;
        CityResidents res = c.Residents;
        Release(w, fac);
        int gen = p.Pool.GenerationOf(fac);
        if (fac == res.HomeFac[r] && res.HomeFacGen[r] == gen)
        {
            p.HomeAway[fac]--;   // пришла на своё закреплённое место
        }
        else
        {
            p.Occupied[fac]++;
        }

        res.CarFac[r] = fac;
        res.CarFacGen[r] = gen;
        res.CarLot[r] = -1;
        int dest = res.LegDest[r];
        res.CarWalkMm[r] = WalkTo(p, dest >= 0 ? c.Buildings.Lot[dest] : -1, fac);
    }

    /// <summary>Прибытие домой без резерва (<c>Reserved</c> не менялся): на закреплённое место либо на <c>Overflow</c> у дома (<c>parking_overflow++</c>).</summary>
    private static void ParkHome(World w, int r)
    {
        CityWorld c = w.City!;
        ParkingStore p = c.Parking!;
        CityResidents res = c.Residents;
        int homeLot = res.Home[r] >= 0 ? c.Buildings.Lot[res.Home[r]] : -1;
        int hf = res.HomeFac[r];
        if (hf > 0 && p.Pool.IsAlive(hf, res.HomeFacGen[r]))
        {
            p.HomeAway[hf]--;
            res.CarFac[r] = hf;
            res.CarFacGen[r] = res.HomeFacGen[r];
            res.CarLot[r] = -1;
            res.CarWalkMm[r] = WalkTo(p, homeLot, hf);
            return;
        }

        res.CarFac[r] = 0;
        res.CarFacGen[r] = p.Pool.GenerationOf(0);
        res.CarLot[r] = homeLot;
        res.CarWalkMm[r] = 0;
        p.Occupied[0]++;
        w.Counters.ParkOverflow++;
    }

    // ------------------------------------------------------------------ метрики

    /// <summary><c>parking_search_time</c> закрытой машинной ноги, тики (§11): сумма, число и корзины 0, &lt;= 20 с, &lt;= 60 с, &lt;= 300 с, &lt;= 600 с, &gt; 600 с.</summary>
    internal static void RecordSearchTime(World w, int ticks)
    {
        ref SimCounters k = ref w.Counters;
        k.ParkSearchTimeSum += ticks;
        k.ParkSearchTimeCount++;
        if (ticks <= 0)
        {
            k.ParkSearchHist0++;
        }
        else if (ticks <= 400)
        {
            k.ParkSearchHist1++;
        }
        else if (ticks <= 1200)
        {
            k.ParkSearchHist2++;
        }
        else if (ticks <= 6000)
        {
            k.ParkSearchHist3++;
        }
        else if (ticks <= 12000)
        {
            k.ParkSearchHist4++;
        }
        else
        {
            k.ParkSearchHist5++;
        }
    }

    // ------------------------------------------------------------------ снос

    /// <summary>
    /// Снос объекта <paramref name="f"/> (§7): закрепившие переназначаются (§4; нет места - машина на <c>Overflow</c> у дома, <c>HomeFac = -1</c>), стоящие гости эвакуируются на домашнее место
    /// (<c>CarWalkMm = -1</c>, §7 п. 3), слот освобождается, окрестности пересобираются локально, <c>HomeAway</c> пересчитывается. Машины в пути с резервом на объекте и заявки (K3) в K1-K2 при сносе
    /// не обрабатываются.
    /// </summary>
    public static void Remove(World w, int f)
    {
        CityWorld? c = w.City;
        ParkingStore? p = c?.Parking;
        if (p == null || f <= 0 || !p.Pool.IsAlive(f))
        {
            return;
        }

        p.Flags[f] |= ParkFlags.Closing;   // новые закрепления сюда не попадут
        CityResidents res = c!.Residents;
        int gen = p.Pool.GenerationOf(f);
        for (int r = 0; r < res.HighWater; r++)
        {
            if (!res.Pool.IsAlive(r))
            {
                continue;
            }

            bool home = res.HomeFac[r] == f && res.HomeFacGen[r] == gen;
            bool car = res.CarFac[r] == f && res.CarFacGen[r] == gen;
            if (!home && !car)
            {
                continue;
            }

            bool flying = res.CarFac[r] == -1;   // машина в пути: её место определит прибытие (§6), CarFac остаётся -1

            if (home)
            {
                p.Assigned[f]--;
                res.HomeFac[r] = -1;
                res.HomeFacGen[r] = 0;
            }

            if (car && !home)
            {
                p.Occupied[f]--;   // гость
            }

            res.CarFac[r] = -1;
            res.CarFacGen[r] = 0;
            if ((res.Flags[r] & ResidentFlags.HasCar) == 0)
            {
                continue;
            }

            if (flying)
            {
                if (res.Home[r] >= 0)
                {
                    HomeAssign.TryAssign(c, r);
                }

                res.CarFac[r] = -1;
                res.CarFacGen[r] = 0;
                continue;
            }

            if (res.HomeFac[r] >= 0 || (res.Home[r] >= 0 && HomeAssign.TryAssign(c, r)))
            {
                if (!home)
                {
                    res.CarFac[r] = res.HomeFac[r];   // эвакуация гостя: машина дома, пешком до неё далеко
                    res.CarFacGen[r] = res.HomeFacGen[r];
                    res.CarWalkMm[r] = -1;
                }

                continue;
            }

            // Места нет: Overflow у дома (§4); CarLot - участок дома.
            int homeLot = res.Home[r] >= 0 ? c.Buildings.Lot[res.Home[r]] : -1;
            res.CarFac[r] = 0;
            res.CarFacGen[r] = p.Pool.GenerationOf(0);
            res.CarLot[r] = homeLot;
            res.CarWalkMm[r] = 0;
            p.Occupied[0]++;
        }

        float x = p.X[f];
        float z = p.Z[f];
        bool indexed = p.RoadComp[f] >= 0;   // объект мёртвого участка в окрестности не входил
        p.Destroy(f);
        ParkingBuild.RecountHomeAway(c, p);
        if (indexed)
        {
            p.Catch.RebuildNear(w, c, x, z);
        }
    }
}
