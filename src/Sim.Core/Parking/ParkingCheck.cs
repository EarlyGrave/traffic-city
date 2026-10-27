using System;
using Sim.Core.Agents;
using Sim.Core.City;

namespace Sim.Core.Parking;

/// <summary>
/// Проверка инвариантов парковки (parking.md §1, §13, <c>ValidateParking</c>): структура объектов, ссылки жителей, машин и заявок живы с верным поколением, PK2 (<c>Assigned</c>, <c>Occupied</c>,
/// <c>Reserved</c> равны пересчёту по жителям, машинам и заявкам), PK4 в части K1 (машина жителя - на объекте либо в пути/заявке; строгая «ровно в одном месте» после K2), PK6 (у жителя с машиной
/// есть домашнее место либо машина на <c>Overflow</c>). Зовут <c>SaveValidator.ValidateParking</c> (после загрузки), Debug-проверка раз в 100 тиков (K2) и тесты. Выделяет память (рабочие массивы) -
/// только вне горячего цикла.
/// </summary>
internal static class ParkingCheck
{
    /// <summary>0 - всё согласовано; иначе номер нарушенной проверки (он же столбец сообщения загрузчика), <paramref name="row"/> - объект, житель, машина или заявка.</summary>
    public static int Validate(World w, out long row)
    {
        row = 0;
        CityWorld? c = w.City;
        ParkingStore? p = c?.Parking;
        if (c == null || p == null)
        {
            return 0;
        }

        int high = p.Pool.HighWater;
        if (high < 1 || !p.Pool.IsAlive(0) || p.Kind[0] != ParkKind.Overflow || p.Capacity[0] != int.MaxValue)
        {
            return 1;
        }

        LotStore lots = c.Lots;
        BuildingStore bld = c.Buildings;
        var curbSeen = new bool[lots.Count];
        var bldSeen = new bool[bld.HighWater];
        for (int f = 1; f < high; f++)
        {
            if (!p.Pool.IsAlive(f))
            {
                continue;
            }

            row = f;
            byte kind = p.Kind[f];
            if (kind is < ParkKind.Curb or > ParkKind.Garage || p.Flags[f] > 7 || p.Capacity[f] < 0 || p.Assigned[f] < 0 || p.Occupied[f] < 0 || p.Reserved[f] < 0 || p.Fee[f] < 0)
            {
                return 2;
            }

            int lot = p.Lot[f];
            if ((uint)lot >= (uint)lots.Count)
            {
                return 3;
            }

            if (kind == ParkKind.Curb)
            {
                if (p.Building[f] != -1 || p.BuildingGen[f] != 0 || (lots.Flags[lot] & LotFlags.Lost) != 0 || curbSeen[lot])
                {
                    return 4;
                }

                curbSeen[lot] = true;
            }
            else
            {
                int bi = p.Building[f];
                if ((uint)bi >= (uint)bld.HighWater || !bld.Pool.IsAlive(bi, p.BuildingGen[f]) || bld.Lot[bi] != lot || bldSeen[bi])
                {
                    return 4;
                }

                bldSeen[bi] = true;
            }
        }

        // Пересчёт счётчиков по жителям (PK2) и проверка ссылок (PK4, PK6).
        var assigned = new int[high];
        var occupied = new int[high];
        var reserved = new int[high];
        CityResidents r = c.Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            row = i;
            int hf = r.HomeFac[i];
            int cf = r.CarFac[i];
            bool hasCar = (r.Flags[i] & ResidentFlags.HasCar) != 0;
            if (hf != -1 && (hf < 1 || hf >= high || !p.Pool.IsAlive(hf, r.HomeFacGen[i])))
            {
                return 5;
            }

            if (cf < -1 || cf >= high || (cf >= 0 && !p.Pool.IsAlive(cf, r.CarFacGen[i])))
            {
                return 5;
            }

            if ((cf == 0 && ((uint)r.CarLot[i] >= (uint)lots.Count)) || (cf != 0 && r.CarLot[i] != -1))
            {
                return 5;
            }

            if (!hasCar && (hf != -1 || cf != -1))
            {
                return 6;   // нет машины - нет и места
            }

            if (hasCar && hf == -1 && cf != 0)
            {
                return 6;   // PK6: домашнее место либо Overflow у дома
            }

            if (hf > 0)
            {
                assigned[hf]++;
            }

            if (cf == 0)
            {
                occupied[0]++;
            }
            else if (cf > 0 && cf != hf)
            {
                occupied[cf]++;
            }
        }

        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            row = i;
            int pf = v.ParkFac[i];
            if (pf == -1)
            {
                continue;
            }

            if (pf < 1 || pf >= high || !p.Pool.IsAlive(pf, v.ParkFacGen[i]))
            {
                return 8;
            }

            reserved[pf]++;
        }

        for (int i = 0; i < w.Trips.Count; i++)
        {
            ref readonly PendingTrip t = ref w.Trips.At(i);
            if (t.ParkRef == 0)
            {
                continue;
            }

            row = i;
            int pf = t.ParkRef - 1;
            if (pf < 1 || pf >= high || !p.Pool.IsAlive(pf, t.ParkGen))
            {
                return 8;
            }

            reserved[pf]++;
        }

        for (int f = 0; f < high; f++)
        {
            if (p.Pool.IsAlive(f) && (p.Assigned[f] != assigned[f] || p.Occupied[f] != occupied[f] || p.Reserved[f] != reserved[f]))
            {
                row = f;
                return 7;
            }
        }

        row = 0;
        return 0;
    }
}
