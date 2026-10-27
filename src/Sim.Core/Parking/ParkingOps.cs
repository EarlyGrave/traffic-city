using Sim.Core.City;

namespace Sim.Core.Parking;

/// <summary>
/// Операции над местами (parking.md §5-§7). K1 замораживает сигнатуры и даёт заглушки <see cref="TryReserve"/>, <see cref="Release"/>, <see cref="Park"/> (их реализует K2: резерв при заявке
/// машинной ноги, освобождение, постановка при прибытии); пока их никто не вызывает, а счётчики <c>Reserved</c> и <c>Occupied</c> гостей остаются нулевыми. <see cref="Remove"/> - рабочая
/// версия уровня K1 (снос объекта: переназначение закрепивших и эвакуация стоящих); K3 добавляет машины в пути, заявки и поиск (§7 п. 1-2).
/// </summary>
internal static class ParkingOps
{
    /// <summary>K2: резерв места у цели машинной ноги жителя <paramref name="r"/> (§5). Заглушка K1: резерва нет (<c>false</c>), состояние не меняется.</summary>
    public static bool TryReserve(World w, int r, int destBuilding, out int fac)
    {
        fac = -1;
        return false;
    }

    /// <summary>K2: освобождение резерва <paramref name="fac"/> (§5, единственная точка для PK2). Заглушка K1: ничего не делает.</summary>
    public static void Release(World w, int fac)
    {
    }

    /// <summary>K2: постановка машины жителя <paramref name="r"/> на зарезервированное место при прибытии (§6). Заглушка K1: ничего не делает.</summary>
    public static void Park(World w, int r, int fac)
    {
    }

    /// <summary>
    /// Снос объекта <paramref name="f"/> (§7): закрепившие переназначаются (§4; нет места - машина на <c>Overflow</c> у дома, <c>HomeFac = -1</c>), стоящие гости эвакуируются на домашнее место
    /// (<c>CarWalkMm = -1</c>, §7 п. 3), слот освобождается, окрестности пересобираются локально. Машины в пути с резервом на объекте и заявки (K3) в K1 невозможны: резервов нет.
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
        if (indexed)
        {
            p.Catch.RebuildNear(w, c, x, z);
        }
    }
}
