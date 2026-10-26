using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Demand;

/// <summary>
/// Выбор жителя (zoning-demand.md §6): частота дискреционных туров, цель, способ, слот выезда. Z1 - финальные сигнатуры и <b>фиксированные заглушки без обращений
/// к PRNG</b> (чтобы Z2 строил расписание и ноги, не дожидаясь Z3): одна дискреционная поездка в день, первый подходящий C, <c>car</c> при <c>HasCar</c> и общей дорожной
/// компоненте, иначе <c>walk</c>, выезд за длительность ноги до желаемого прибытия. Z3 заменяет тела на логит ADR-0021, сигнатуры не меняются.
/// </summary>
internal static class Choice
{
    /// <summary>Число дискреционных туров на сегодня, <c>0..freq_max</c> (§6.3). Заглушка: 1.</summary>
    public static int ChooseFrequency(World w, int resident) => w.City!.Cfg.Choice.FreqMax >= 1 ? 1 : 0;

    /// <summary>Здание-цель дискреционного тура C или -1 (§6.4). Заглушка: первое <c>Active</c> C с той же дорожной компонентой, обход со сдвигом по номеру жителя.</summary>
    public static int ChooseDestination(World w, int resident)
    {
        CityWorld c = w.City!;
        int home = c.Residents.Home[resident];
        if (home < 0)
        {
            return -1;
        }

        int homeComp = c.Lots.RoadComp[c.Buildings.Lot[home]];
        int hw = c.Buildings.HighWater;
        for (int k = 0; k < hw; k++)
        {
            int b = (resident + k) % hw;
            if (!c.Buildings.IsAlive(b) || c.Buildings.State[b] != (byte)BuildingState.Active || c.Types.Rows[c.Buildings.Type[b]].Kind != LotZone.Commercial)
            {
                continue;
            }

            if (c.Lots.RoadComp[c.Buildings.Lot[b]] == homeComp)
            {
                return b;
            }
        }

        return -1;
    }

    /// <summary>Способ тура к зданию <paramref name="dest"/> в период <paramref name="period"/> (§6.5). Заглушка: car / walk.</summary>
    public static TravelMode ChooseMode(World w, int resident, int dest, int period)
    {
        CityWorld c = w.City!;
        bool car = (c.Residents.Flags[resident] & ResidentFlags.HasCar) != 0 &&
                   c.Lots.RoadComp[c.Buildings.Lot[c.Residents.Home[resident]]] == c.Lots.RoadComp[c.Buildings.Lot[dest]];
        return car ? TravelMode.Car : TravelMode.Walk;
    }

    /// <summary>Слот выезда для желаемого прибытия <paramref name="desiredArriveSlot"/> при времени ноги <paramref name="travelSlots"/> (§6.6). Заглушка: прямой расчёт без логита.</summary>
    public static int ChooseDepartSlot(World w, int resident, int desiredArriveSlot, int travelSlots) => desiredArriveSlot > travelSlots ? desiredArriveSlot - travelSlots : 0;
}
