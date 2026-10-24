using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Rules;

namespace Sim.Core.Transit;

/// <summary>
/// Чистые правила остановок автобуса (только чтение). Остановка у обочины и «карман» на шаге 4 моделируются одинаково:
/// автобус стоит на полосе остановки и блокирует её, обгон - обычный MOBIL (см. ADR-0008).
/// </summary>
internal static class BusStopRules
{
    /// <summary>Автобус «прибыл», если до точки остановки не больше этого, м, и скорость ниже <see cref="ArriveSpeed"/>.</summary>
    public const float ArriveDistance = 3f;

    public const float ArriveSpeed = 0.5f;

    /// <summary>Автобус в очереди к остановке (не на ней) в пределах этого расстояния не копит StillTicks (не считается stuck), м.</summary>
    public const float QueueZone = 60f;

    /// <summary>Индекс (глобальный, в <c>Net.BusStops</c>) остановки, к которой едет автобус, или -1.</summary>
    public static int TargetStop(NetworkStore net, VehicleStore v, int i)
    {
        int r = v.BusRoute[i];
        if ((uint)r >= (uint)net.BusRouteCount)
        {
            return -1;
        }

        ref readonly BusRouteData br = ref net.BusRoutes[r];
        int k = v.BusNextStop[i];
        return (uint)k < (uint)br.StopCount ? br.FirstStop + k : -1;
    }

    /// <summary>
    /// Виртуальное препятствие на целевой остановке: зазор до стены в <c>stop.S + minGap</c> (IDM останавливает бампер у точки остановки),
    /// пока автобус на любой полосе того же направленного сегмента (если он ещё не на полосе остановки, MOBIL перестроит его).
    /// <see cref="Idm.FreeGap"/> - препятствия нет.
    /// </summary>
    public static float ObstacleGap(NetworkStore net, VehicleStore v, int i, float minGap)
    {
        int si = TargetStop(net, v, i);
        int lane = v.Lane[i];
        if (si < 0 || lane < 0)
        {
            return Idm.FreeGap;
        }

        ref readonly BusStopData stop = ref net.BusStops[si];
        ref readonly LaneData ld = ref net.Lanes[lane];
        ref readonly LaneData sl = ref net.Lanes[stop.Lane];
        if (ld.Kind != LaneKind.Normal || ld.Segment != sl.Segment || ld.Direction != sl.Direction)
        {
            return Idm.FreeGap;
        }

        float gap = stop.S + minGap - v.S[i];
        return gap < 0.1f ? Idm.FreeGap : gap;
    }

    /// <summary>true = автобус на (любой полосе) того же направленного сегмента, что и полоса остановки <paramref name="stopLane"/>.</summary>
    public static bool OnStopSegment(NetworkStore net, VehicleStore v, int i, int stopLane)
    {
        int lane = v.Lane[i];
        if (lane < 0)
        {
            return false;
        }

        ref readonly LaneData ld = ref net.Lanes[lane];
        ref readonly LaneData sl = ref net.Lanes[stopLane];
        return ld.Kind == LaneKind.Normal && ld.Segment == sl.Segment && ld.Direction == sl.Direction;
    }

    /// <summary>true = автобус обслуживает остановку (стоит на ней либо в очереди к ней рядом): не копит StillTicks, не stuck.</summary>
    public static bool IsServing(NetworkStore net, VehicleStore v, int i)
    {
        byte f = v.Flags[i];
        if ((f & (byte)VehicleFlags.IsBus) == 0)
        {
            return false;
        }

        if ((f & (byte)VehicleFlags.AtStop) != 0)
        {
            return true;
        }

        int si = TargetStop(net, v, i);
        int lane = v.Lane[i];
        if (si < 0 || lane < 0)
        {
            return false;
        }

        ref readonly BusStopData stop = ref net.BusStops[si];
        ref readonly LaneData ld = ref net.Lanes[lane];
        ref readonly LaneData sl = ref net.Lanes[stop.Lane];
        float d = stop.S - v.S[i];
        return ld.Kind == LaneKind.Normal && ld.Segment == sl.Segment && ld.Direction == sl.Direction && d >= -ArriveDistance && d <= QueueZone;
    }
}
