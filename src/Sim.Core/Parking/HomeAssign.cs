using Sim.Core.City;

namespace Sim.Core.Parking;

/// <summary>
/// Домашнее место жителя (parking.md §4): кандидаты - окрестность участка дома по порядку <c>(WalkMm, индекс)</c>, у которых есть <see cref="ParkFlags.Permit"/>, нет
/// <see cref="ParkFlags.Closing"/> и <c>WalkMm &lt;= home_walk_max_m</c>; первый со <c>Free &gt; 0</c> закрепляется (<c>Assigned++</c>), машина становится туда же. Стоянка своего здания идёт первой
/// (<c>WalkMm = 0</c>). Ежедневный проход по жителям <c>Overflow</c>-дома - K3.
/// </summary>
internal static class HomeAssign
{
    /// <summary>Закрепляет место жителю с машиной: <c>HomeFac</c>, <c>CarFac</c> и <c>CarWalkMm</c>. false - места нет (поля жителя не тронуты).</summary>
    public static bool TryAssign(CityWorld c, int r)
    {
        ParkingStore p = c.Parking!;
        CityResidents res = c.Residents;
        int home = res.Home[r];
        if (home < 0)
        {
            return false;
        }

        int lot = c.Buildings.Lot[home];
        if (lot < 0)
        {
            return false;
        }

        Catchment cat = p.Catch;
        int b0 = lot * cat.Stride;
        int n = cat.Count[lot];
        for (int k = 0; k < n; k++)
        {
            int f = cat.Fac[b0 + k];
            int walk = cat.WalkMm[b0 + k];
            if (walk > p.Cfg.HomeWalkMaxMm)
            {
                break;   // дальше только дальше (отсортировано по WalkMm)
            }

            if ((p.Flags[f] & ParkFlags.Permit) == 0 || (p.Flags[f] & ParkFlags.Closing) != 0 || p.Free(f) <= 0)
            {
                continue;
            }

            int gen = p.Pool.GenerationOf(f);
            p.Assigned[f]++;
            res.HomeFac[r] = f;
            res.HomeFacGen[r] = gen;
            res.CarFac[r] = f;
            res.CarFacGen[r] = gen;
            res.CarLot[r] = -1;
            res.CarWalkMm[r] = walk;
            return true;
        }

        return false;
    }

    /// <summary>Начальное население (<c>CityInit</c>, после выпадения <c>HasCar</c>): жители по возрастанию индекса; кому не хватило места - <c>HasCar</c> снимается, <c>parking_car_shed++</c>.</summary>
    public static void AssignInitial(World w, CityWorld c)
    {
        CityResidents r = c.Residents;
        for (int ri = 0; ri < r.HighWater; ri++)
        {
            if (r.Pool.IsAlive(ri) && (r.Flags[ri] & ResidentFlags.HasCar) != 0 && !TryAssign(c, ri))
            {
                r.Flags[ri] &= unchecked((byte)~ResidentFlags.HasCar);
                w.Counters.ParkCarShed++;
            }
        }
    }

    /// <summary>Въезд и переезд (после того, как известны <c>HasCar</c> и дом): нет места - машина не въезжает (<c>HasCar</c> снимается), <c>parking_car_shed++</c>.</summary>
    public static void OnMoveIn(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        if (c.Parking != null && (res.Flags[r] & ResidentFlags.HasCar) != 0 && !TryAssign(c, r))
        {
            res.Flags[r] &= unchecked((byte)~ResidentFlags.HasCar);
            w.Counters.ParkCarShed++;
        }
    }

    /// <summary>
    /// Житель покидает дом (переезд, отъезд из города): закрепление снимается (<c>Assigned--</c>), машина уезжает вместе с жителем (всегда дома: отъезд только из <c>AtHome</c>); у стоявшего
    /// на <c>Overflow</c> снимается <c>CarLot</c>. Поля жителя возвращаются к «нет места, нет машины на объекте».
    /// </summary>
    public static void Release(CityWorld c, int r)
    {
        ParkingStore? p = c.Parking;
        if (p == null)
        {
            return;
        }

        CityResidents res = c.Residents;
        int hf = res.HomeFac[r];
        int cf = res.CarFac[r];
        if (hf > 0 && p.Pool.IsAlive(hf, res.HomeFacGen[r]))
        {
            p.Assigned[hf]--;
        }

        if (cf == 0)
        {
            p.Occupied[0]--;
        }
        else if (cf > 0 && cf != hf && p.Pool.IsAlive(cf, res.CarFacGen[r]))
        {
            p.Occupied[cf]--;   // гость на чужом объекте (K2+): место освобождается
        }

        res.HomeFac[r] = -1;
        res.HomeFacGen[r] = 0;
        res.CarFac[r] = -1;
        res.CarFacGen[r] = 0;
        res.CarLot[r] = -1;
        res.CarWalkMm[r] = 0;
    }
}
