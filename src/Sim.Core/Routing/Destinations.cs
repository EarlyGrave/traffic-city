using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Точка прибытия в зоне (city-generator 5.2, routing.md 6): берётся случайная полоса спавна зоны, полоса прибытия - полоса
/// <c>LaneNo 0</c> встречного направления того же сегмента (если встречных полос нет - сама полоса спавна), <c>DestS</c> -
/// <see cref="ArrivalFraction"/> длины полосы: машина заведомо успевает занять полосу до точки, а не стоит в очереди на узел.
/// </summary>
internal static class Destinations
{
    /// <summary>Доля длины полосы прибытия, на которой засчитывается прибытие.</summary>
    public const float ArrivalFraction = 0.6f;

    /// <summary>false = у зоны нет полос (поездку не к чему маршрутизировать). Одно обращение к <paramref name="rng"/>, если полосы есть.</summary>
    public static bool Pick(NetworkStore net, int zone, ref Pcg32 rng, out int lane, out float s)
    {
        lane = -1;
        s = 0f;
        if ((uint)zone >= (uint)net.ZoneCount)
        {
            return false;
        }

        int open = net.OpenZoneLaneCount(zone);   // 4.3, T4: полосы, закрытые дренажем, не годятся; у сети без правок это SpawnLaneCount
        if (open <= 0)
        {
            return false;
        }

        int spawn = net.OpenZoneLane(zone, (int)rng.NextBelow((uint)open));
        lane = OppositeLane(net, spawn);
        s = net.Lanes[lane].Length * ArrivalFraction;
        return true;
    }

    /// <summary>Полоса <c>LaneNo 0</c> встречного направления сегмента полосы <paramref name="lane"/> (или сама <paramref name="lane"/>, если встречных нет).</summary>
    public static int OppositeLane(NetworkStore net, int lane)
    {
        ref readonly LaneData ld = ref net.Lanes[lane];
        if (ld.Segment < 0)
        {
            return lane;
        }

        ref readonly SegmentData sg = ref net.Segments[ld.Segment];
        int count = ld.Direction == 0 ? sg.LanesBA : sg.LanesAB;
        int first = ld.Direction == 0 ? sg.FirstLaneBA : sg.FirstLaneAB;
        return count > 0 ? first : lane;
    }
}
