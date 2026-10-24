using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;

namespace Sim.Core.Transit;

/// <summary>
/// Фаза 2b (core-api §7): выпуск автобусов. Маршрут r выпускает автобус с первой остановки каждые <c>HeadwayTicks</c>; первый выпуск маршрута
/// разнесён по времени (<c>r * Headway / RouteCount</c>), чтобы маршруты не стартовали пачкой. Если места на полосе нет или построение
/// пути отложено - повтор на следующем тике (интервал считается от фактического выпуска).
/// Автобус едет «от остановки к остановке»: <c>DestLane/DestS</c> и маршрут ведут к целевой остановке <c>BusNextStop</c>; следующий отрезок
/// строит <see cref="BusStopPhase"/> при отправлении. Рейс односторонний: на конечной остановке автобус высаживает всех и исчезает.
/// </summary>
internal static class BusDispatch
{
    public static void Run(World w)
    {
        NetworkStore net = w.Net;
        int rc = net.BusRouteCount;
        for (int r = 0; r < rc; r++)
        {
            ref BusRouteData br = ref net.BusRoutes[r];
            if (br.StopCount < 1 || br.HeadwayTicks <= 0)
            {
                continue;
            }

            long due = br.LastDispatchTick < 0 ? (long)r * br.HeadwayTicks / rc : br.LastDispatchTick + br.HeadwayTicks;
            if (w.Tick >= due)
            {
                TryDispatch(w, r, ref br);
            }
        }
    }

    private static void TryDispatch(World w, int r, ref BusRouteData br)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        if ((uint)br.VehicleType >= (uint)w.Types.Length || v.AliveCount >= v.Capacity)
        {
            return;
        }

        ref readonly BusStopData s0 = ref net.BusStops[br.FirstStop];
        ref readonly VehicleTypeDesc t = ref w.Types[br.VehicleType];
        float s = t.Length + t.MinGap;
        float destS = s0.S > s ? s0.S : s;
        if (destS > net.Lanes[s0.Lane].Length)
        {
            return;
        }

        if (w.Router.Build(s0.Lane, s, s0.Lane, destS, w.Tick, out RouteHandle h) != BuildStatus.Ok)
        {
            return;
        }

        var trip = new PendingTrip
        {
            FromZone = -1,
            ToZone = -1,
            FromLane = s0.Lane,
            FromS = s,
            Type = (byte)br.VehicleType,
            DestLane = s0.Lane,
            DestS = destS,
            Route = h.Index,
            RouteGen = h.Generation,
        };
        if (!Spawner.TryPlace(w, in trip, in t, s0.Lane, s, false, out int i))
        {
            w.Routes.Free(h);
            return;
        }

        v.Flags[i] |= (byte)VehicleFlags.IsBus;
        v.BusRoute[i] = r;
        v.BusNextStop[i] = 0;
        br.ActiveBuses++;
        br.LastDispatchTick = w.Tick;
        w.Counters.BusDispatched++;
        ref BusRouteStats st = ref w.BusRouteStats[r];
        st.ActiveBuses = br.ActiveBuses;
        st.LastDispatchTick = w.Tick;
    }
}
