using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Util;

namespace Sim.Core.Parking;

/// <summary>
/// Раздел хэша парковки (parking.md §13): только при <c>parking.model = "resource"</c>, поэтому хэш прочих сценариев не меняется (PK1, <c>hash_version</c> не растёт: режима раньше не было).
/// Порядок: объекты (поколение слота, у живых - состояние), поля жителей, поля машин, счётчики §11. Поля заявок (<c>ParkRef</c>, <c>ParkGen</c>) хэширует секция очереди поездок.
/// Производные (положение, точка въезда и пешехода, окрестности, <c>WalkMm</c>, <c>HomeAway</c>, обратные ссылки) не хэшируются.
/// </summary>
internal static class ParkingHash
{
    public static void Add(ref Fnv1a64 h, World w)
    {
        CityWorld c = w.City!;
        ParkingStore p = c.Parking!;
        int high = p.Pool.HighWater;
        h.Add(high);
        for (int f = 0; f < high; f++)
        {
            h.Add(p.Pool.GenerationOf(f));
            if (!p.Pool.IsAlive(f))
            {
                continue;
            }

            h.Add(p.Kind[f]);
            h.Add(p.Flags[f]);
            h.Add(p.Lot[f]);
            h.Add(p.Building[f]);
            h.Add(p.BuildingGen[f]);
            h.Add(p.Capacity[f]);
            h.Add(p.Assigned[f]);
            h.Add(p.Occupied[f]);
            h.Add(p.Reserved[f]);
            h.Add(p.Fee[f]);
        }

        CityResidents r = c.Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (!r.Pool.IsAlive(i))
            {
                continue;
            }

            h.Add(r.HomeFac[i]);
            h.Add(r.HomeFacGen[i]);
            h.Add(r.CarFac[i]);
            h.Add(r.CarFacGen[i]);
            h.Add(r.CarLot[i]);
            h.Add(r.CarWalkMm[i]);
            h.Add(r.LegStage[i]);
            h.Add(r.LegWaitFrom[i]);
            h.Add(r.LegParkWait[i]);
        }

        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i))
            {
                h.Add(v.ParkFac[i]);
                h.Add(v.ParkFacGen[i]);
                h.Add(v.ParkSearchTick[i]);
            }
        }

        ref readonly SimCounters k = ref w.Counters;
        h.Add(k.ParkSearchTimeSum);
        h.Add(k.ParkSearchTimeCount);
        h.Add(k.ParkSearchHist0);
        h.Add(k.ParkSearchHist1);
        h.Add(k.ParkSearchHist2);
        h.Add(k.ParkSearchHist3);
        h.Add(k.ParkSearchHist4);
        h.Add(k.ParkSearchHist5);
        h.Add(k.ParkNoSpotWaits);
        h.Add(k.ParkModeSwitch);
        h.Add(k.ParkToursDropped);
        h.Add(k.ParkOverflow);
        h.Add(k.ParkTowed);
        h.Add(k.ParkCarShed);
        h.Add(k.ParkSearching);
        h.Add(k.ParkWalkTicks);
    }
}
