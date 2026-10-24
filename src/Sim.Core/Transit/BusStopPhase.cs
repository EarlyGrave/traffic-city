using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;

namespace Sim.Core.Transit;

/// <summary>
/// Фаза 8b: остановки автобусов. (1) Пассажиры: на каждой остановке счётчик ожидающих растёт детерминированно, без PRNG:
/// +1 каждые <c>1200 / PassengerRatePerMin</c> тиков со сдвигом по индексу остановки (потолок <see cref="MaxWaiting"/>).
/// (2) Прибытие: автобус на полосе остановки, не дальше <see cref="BusStopRules.ArriveDistance"/> до её точки и почти стоит
/// -> <c>AtStop</c>, высаживается треть пассажиров (на конечной - все), садятся ожидающие в пределах вместимости; стоянка =
/// <c>DwellBase + DwellPerPassenger * (вышло + село)</c>, не больше <see cref="MaxDwellTicks"/>. (3) Отправление по окончании стоянки:
/// строится маршрут к следующей остановке (<c>Router.Build</c>, тот же иерархический запрос, что у машин; при отложенном построении автобус ждёт), остановки без пути пропускаются;
/// на конечной (или если дальше пути нет) автобус деспаунится. Метрики: <c>BusStopArrivals</c>, отклонение интервала между последовательными
/// прибытиями на остановке от планового (<c>HeadwayTicks</c>).
/// </summary>
internal static class BusStopPhase
{
    public const int BusCapacity = 80;
    public const int MaxWaiting = 200;
    public const int MaxDwellTicks = 900;
    public const int GiveUpTicks = 400;
    private const float TicksPerMinute = 1200f;

    public static void Run(World w)
    {
        GeneratePassengers(w);
        VehicleStore v = w.Vehicles;
        if (w.Net.BusRouteCount == 0)
        {
            return;
        }

        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0 || v.Lane[i] < 0)
            {
                continue;
            }

            if ((v.Flags[i] & (byte)VehicleFlags.AtStop) != 0)
            {
                Dwell(w, i);
            }
            else
            {
                TryArrive(w, i);
            }
        }
    }

    private static void GeneratePassengers(World w)
    {
        float rate = w.TransitCfg.PassengerRatePerMin;
        NetworkStore net = w.Net;
        int n = net.BusStopCount;
        if (n == 0 || !(rate > 0f))
        {
            return;
        }

        int period = (int)((TicksPerMinute / rate) + 0.5f);
        if (period < 1)
        {
            period = 1;
        }

        for (int s = 0; s < n; s++)
        {
            if ((w.Tick + ((long)s * 7)) % period == 0 && net.BusStops[s].Waiting < MaxWaiting)
            {
                net.BusStops[s].Waiting++;
            }
        }
    }

    private static void TryArrive(World w, int i)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        int si = BusStopRules.TargetStop(net, v, i);
        if (si < 0 || v.GhostLane[i] != -1)
        {
            return;
        }

        ref BusStopData stop = ref net.BusStops[si];
        if (v.Lane[i] != stop.Lane || stop.S - v.S[i] > BusStopRules.ArriveDistance || v.Speed[i] > BusStopRules.ArriveSpeed)
        {
            // Автобус стоит у «стены» остановки на соседней полосе (перестроиться не даёт занятая полоса остановки): через GiveUpTicks отказывается от остановки.
            // DwellTicks вне стоянки - счётчик такого ожидания.
            if (v.Lane[i] != stop.Lane && v.Speed[i] <= BusStopRules.ArriveSpeed && BusStopRules.OnStopSegment(net, v, i, stop.Lane))
            {
                if (++v.DwellTicks[i] >= GiveUpTicks)
                {
                    Advance(w, i, v.BusRoute[i], ref net.BusRoutes[v.BusRoute[i]]);
                }
            }
            else
            {
                v.DwellTicks[i] = 0;
            }

            return;
        }

        int r = v.BusRoute[i];
        ref BusRouteData br = ref net.BusRoutes[r];
        bool terminal = v.BusNextStop[i] >= br.StopCount - 1;
        int pax = v.Passengers[i];
        int alight = terminal ? pax : (pax + 2) / 3;
        int board = 0;
        if (!terminal)
        {
            board = BusCapacity - (pax - alight);
            if (board > stop.Waiting)
            {
                board = stop.Waiting;
            }
        }

        stop.Waiting = (ushort)(stop.Waiting - board);
        v.Passengers[i] = (ushort)(pax - alight + board);
        v.Speed[i] = 0f;
        v.Flags[i] |= (byte)VehicleFlags.AtStop;
        TransitDesc cfg = w.TransitCfg;
        int dwell = cfg.DwellBaseTicks + (int)(cfg.DwellPerPassengerTicks * (alight + board));
        v.DwellTicks[i] = (ushort)(dwell < 1 ? 1 : dwell > MaxDwellTicks ? MaxDwellTicks : dwell);

        ref SimCounters c = ref w.Counters;
        ref BusRouteStats rs = ref w.BusRouteStats[r];
        c.BusStopArrivals++;
        c.BusPassengersBoarded += board;
        rs.Arrivals++;
        if (stop.LastArrivalTick >= 0)
        {
            long dev = w.Tick - stop.LastArrivalTick - br.HeadwayTicks;
            if (dev < 0)
            {
                dev = -dev;
            }

            c.BusHeadwayDeviationTicksTotal += dev;
            c.BusHeadwaySamples++;
            rs.HeadwayDeviationTicksTotal += dev;
        }

        stop.LastArrivalTick = w.Tick;
        w.Emit(SimEventKind.BusStopArrived, 0, r, si);
    }

    private static void Dwell(World w, int i)
    {
        VehicleStore v = w.Vehicles;
        if (v.DwellTicks[i] > 0 && --v.DwellTicks[i] > 0)
        {
            return;
        }

        int r = v.BusRoute[i];
        ref BusRouteData br = ref w.Net.BusRoutes[r];
        Advance(w, i, r, ref br);
    }

    /// <summary>Едет к следующей остановке после целевой (Build от текущей позиции); без пути остановки пропускаются; дальше нет - деспаун.</summary>
    private static void Advance(World w, int i, int r, ref BusRouteData br)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        for (int k = v.BusNextStop[i] + 1; k < br.StopCount; k++)
        {
            ref readonly BusStopData next = ref net.BusStops[br.FirstStop + k];
            BuildStatus st = w.Router.Build(v.Lane[i], v.S[i], next.Lane, next.S, w.Tick, out RouteHandle h);
            if (st == BuildStatus.Deferred)
            {
                return; // стоим и повторяем на следующем тике (DwellTicks уже 0)
            }

            if (st != BuildStatus.Ok)
            {
                continue; // нет пути до этой остановки - пропускаем
            }

            if (v.Route[i] >= 0)
            {
                w.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
            }

            v.Route[i] = h.Index;
            v.RouteGen[i] = h.Generation;
            v.RouteCursor[i] = 0;
            v.NextConn[i] = -1;
            v.DestLane[i] = next.Lane;
            v.DestS[i] = next.S;
            v.BusNextStop[i] = k;
            v.DwellTicks[i] = 0;
            v.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.AtStop);
            return;
        }

        Remove(w, i, r, ref br);
    }

    private static void Remove(World w, int i, int r, ref BusRouteData br)
    {
        VehicleStore v = w.Vehicles;
        br.ActiveBuses--;
        w.BusRouteStats[r].ActiveBuses = br.ActiveBuses;
        w.Occupancy.UnlinkAll(i);
        if (v.Route[i] >= 0)
        {
            w.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
        }

        v.Despawn(v.HandleOf(i));
    }
}
