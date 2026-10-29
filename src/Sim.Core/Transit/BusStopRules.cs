using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Transit;

/// <summary>
/// Чистые правила остановок автобуса (только чтение). Остановка у обочины (<c>Curb</c>) и «карман» (<c>Bay</c>) при <c>bay_model != virtual</c> моделируются одинаково:
/// автобус стоит на полосе остановки и блокирует её, обгон - обычный MOBIL (см. ADR-0008). При <c>bay_model = virtual</c> автобус на остановке <c>Bay</c> уходит в виртуальный карман
/// (ADR-0029, <c>docs/design/bus-bays.md</c>): правила въезда и выезда здесь, переходы состояния - в <see cref="BusStopPhase"/>.
/// </summary>
internal static class BusStopRules
{
    /// <summary>Автобус «прибыл», если до точки остановки не больше этого, м, и скорость ниже <see cref="ArriveSpeed"/>.</summary>
    public const float ArriveDistance = 3f;

    public const float ArriveSpeed = 0.5f;

    /// <summary>Автобус в очереди к остановке (не на ней) в пределах этого расстояния не копит StillTicks (не считается stuck), м.</summary>
    public const float QueueZone = 60f;

    /// <summary>true = мир с виртуальным карманом: <c>bay_model = virtual</c> и LOD выключен (в LOD кармана нет - остановка как <c>Curb</c>).</summary>
    public static bool BayVirtual(World w) => w.TransitCfg.BayModel == BayModel.Virtual && w.Lod == null;

    /// <summary>
    /// Горизонт ворот для кармана: в тех же единицах, что <c>farDistance</c> фазы 4 (<c>max(30 м, MinGap + допуск метки)</c> по типам). Ближе этой дистанции до конца полосы
    /// автобус в карман не уходит (рядом стоп-линия: ворота и допуск ведёт сама полоса).
    /// </summary>
    public static float BayHorizon(World w)
    {
        float far = GateRules.MinDecisionDistance;
        for (int k = 0; k < w.Types.Length; k++)
        {
            far = Math.Max(far, w.Types[k].MinGap + GateRules.StopLineTolerance);
        }

        return far;
    }

    /// <summary>
    /// Выезд из кармана (gap acceptance, bus-bays.md §2 п. 4): можно ли вставить автобус <paramref name="i"/> в полосу остановки на <c>s = stop.S</c>.
    /// Впереди зазор не меньше <c>s0</c> автобуса; сзади (<c>b</c>) зазор не меньше <c>s0_b + v_b * T_b</c> и торможение до остановки у автобуса не жёстче <c>b_comf</c> типа <c>b</c>;
    /// <c>b</c> не допущен воротами; полоса не закрыта и не дренируется. <paramref name="ahead"/>/<paramref name="behind"/> - соседи для вставки (-1 - край).
    /// </summary>
    public static bool ExitGap(World w, int i, in BusStopData stop, out int ahead, out int behind)
    {
        ahead = -1;
        behind = -1;
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        ref readonly LaneData ld = ref net.Lanes[stop.Lane];
        if (ld.Kind != LaneKind.Normal || (ld.Flags & (LaneFlags.Closed | LaneFlags.Draining)) != 0 || stop.S > ld.Length)
        {
            return false;
        }

        float s = stop.S;
        ref readonly VehicleTypeDesc t = ref w.Types[v.Type[i]];
        w.Occupancy.FindNeighbours(stop.Lane, s, out ahead, out behind);
        if (ahead != -1 && v.S[ahead] - v.Length[ahead] - s < t.MinGap)
        {
            return false;
        }

        if (behind != -1)
        {
            if (GateRules.IsGranted(v, behind))
            {
                return false;
            }

            ref readonly VehicleTypeDesc tb = ref w.Types[v.Type[behind]];
            float vb = v.Speed[behind];
            float gap = s - v.Length[i] - v.S[behind];
            if (gap < tb.MinGap + (vb * tb.HeadwayTime))
            {
                return false;
            }

            if (vb > 0f && (vb * vb) / (2f * (gap - tb.MinGap)) > tb.ComfortDecel)
            {
                return false;
            }
        }

        return true;
    }

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
        return (uint)k < (uint)br.StopCount ? net.BusRouteStops[br.FirstStop + k] : -1;
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
