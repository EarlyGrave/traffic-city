using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Routing;
using Sim.Core.Util;

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
        bool agents = IsAgents(w);
        if (agents)
        {
            Stops.CheckStuck(w);
        }
        else
        {
            GeneratePassengers(w);
        }

        VehicleStore v = w.Vehicles;
        if (w.Net.BusRouteCount == 0)
        {
            return;
        }

        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0 || (v.Lane[i] < 0 && (v.Flags[i] & (byte)VehicleFlags.InBay) == 0))
            {
                continue;
            }

            if (agents)
            {
                w.Counters.BusPaxOnBoardTicks += v.Passengers[i];
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

    /// <summary>Модель пассажиров <c>agents</c> (ADR-0020): очереди и пассажиры - пешеходы (<c>Peds/Stops.cs</c>), счётчик ADR-0008 не работает.</summary>
    private static bool IsAgents(World w) => w.Peds != null && w.TransitCfg.PassengerModel == PassengerModel.Agents;

    private static void GeneratePassengers(World w)
    {
        float rate = w.TransitCfg.PassengerRatePerMin;
        NetworkStore net = w.Net;
        int n = net.BusStopCount;
        if (n == 0 || !(rate > 0f))
        {
            return;
        }

        int period = DetMath.ToIntSat((TicksPerMinute / rate) + 0.5f);
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
                    Advance(w, i, v.BusRoute[i], ref net.BusRoutes[v.BusRoute[i]], v.Lane[i], v.S[i]);
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
        int alight;
        int board = 0;
        if (IsAgents(w))
        {
            // Агенты (pedestrians.md §7): выходят все с остановкой высадки не дальше текущей (на конечной - все), затем садятся из головы очереди.
            alight = Stops.Alight(w, i, si, terminal);
            pax -= alight;
            if (!terminal)
            {
                board = Stops.Board(w, i, si, BusCapacity - pax);
            }

            v.Passengers[i] = (ushort)(pax + board);
        }
        else
        {
            alight = terminal ? pax : (pax + 2) / 3;
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
        }

        float oldSpeed = v.Speed[i];
        v.Speed[i] = 0f;
        w.Masks.Retune(v, i, oldSpeed, v.Lod.Length != 0 && (v.Lod[i] & Lod.LodBits.Meso) != 0);   // накопители метрик фазы 9 (ADR-0019, A4)
        v.Flags[i] |= (byte)VehicleFlags.AtStop;
        TransitDesc cfg = w.TransitCfg;
        int dwell = cfg.DwellBaseTicks + DetMath.ToIntSat(cfg.DwellPerPassengerTicks * (alight + board));
        dwell = dwell < 1 ? 1 : dwell > MaxDwellTicks ? MaxDwellTicks : dwell;
        dwell += HoldTicks(cfg, in stop, in br, v.BusNextStop[i], terminal, w.Tick + dwell);
        v.DwellTicks[i] = (ushort)(dwell > ushort.MaxValue ? ushort.MaxValue : dwell);

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
        if (stop.Kind == BusStopKind.Bay && BusStopRules.BayVirtual(w))
        {
            EnterBay(w, i, si, ref stop);
        }
    }

    /// <summary>
    /// Въезд в виртуальный карман (bus-bays.md §2 п. 1): карман свободен и точка остановки вне горизонта ворот - автобус снимается со списка полосы (<c>Lane = -1</c>),
    /// стоит «вне сети» в <c>S = stop.S</c> с флагами <c>AtStop | InBay</c>. Иначе обслуживание у обочины, как раньше (<c>BayFallbacks++</c>).
    /// Метрики фазы 9 карман не трогает: автобус остаётся живым и учтённым фазой 6 (скорость 0, <c>StillTicks</c> не растёт - <c>Move</c> для <c>Lane &lt; 0</c> не вызывается).
    /// </summary>
    private static void EnterBay(World w, int i, int si, ref BusStopData stop)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        if (stop.BayUsed != 0 || net.LaneLength[stop.Lane] - stop.S < BusStopRules.BayHorizon(w))
        {
            w.Counters.BayFallbacks++;
            return;
        }

        w.Occupancy.Unlink(i);
        v.S[i] = stop.S;
        v.Speed[i] = 0f;
        v.LaneChangeDir[i] = 0;
        v.Flags[i] |= (byte)VehicleFlags.InBay;
        stop.BayUsed = 1;
        stop.BayExitFrom = -1;
        w.BayBus[si] = i;
        w.Counters.BayEntries++;
    }

    /// <summary>
    /// Конец стоянки автобуса в кармане (bus-bays.md §2 п. 3-4): ждёт зазор в полосе остановки (<see cref="BusStopRules.ExitGap"/>); на конечной зазор не нужен (автобус исчезает).
    /// Как только зазор есть, строится маршрут к следующей остановке от точки остановки (отложенное построение - повтор на следующем тике) и автобус вставляется в полосу.
    /// </summary>
    private static void ExitBay(World w, int i)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        int si = BusStopRules.TargetStop(net, v, i);
        if (si < 0)
        {
            return;   // инвариант InBay нарушен (валидатор сейва не пропускает): ждём, не падаем
        }

        ref BusStopData stop = ref net.BusStops[si];
        int r = v.BusRoute[i];
        ref BusRouteData br = ref net.BusRoutes[r];
        if (stop.Lane < 0)
        {
            Remove(w, i, r, ref br);   // остановку снесли инструментом, пока автобус стоял в кармане: вставлять некуда (полное сопровождение правок сети - BB2)
            return;
        }

        if (stop.BayExitFrom < 0)
        {
            stop.BayExitFrom = w.Tick;
        }

        int ahead = -1;
        int behind = -1;
        bool terminal = v.BusNextStop[i] >= br.StopCount - 1;
        if (!terminal && !BusStopRules.ExitGap(w, i, in stop, out ahead, out behind))
        {
            return;
        }

        int lane = stop.Lane;
        long waited = w.Tick - stop.BayExitFrom;
        if (Advance(w, i, r, ref br, lane, stop.S) != AdvanceResult.Advanced)
        {
            return;   // отложено (повтор на следующем тике) либо автобус исчез вместе с карманом (ReleaseBay в Remove)
        }

        ReleaseBay(w, i, si, ref stop, waited);
        w.Occupancy.InsertBetween(lane, i, ahead, behind);
        v.NextConn[i] = TransitionPhase.ChooseConnector(w, i, lane);
    }

    /// <summary>Карман остановки <paramref name="si"/> освобождён: автобус <paramref name="i"/> больше не <c>InBay</c>.</summary>
    private static void ReleaseBay(World w, int i, int si, ref BusStopData stop, long waitedTicks)
    {
        w.Vehicles.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.InBay);
        stop.BayUsed = 0;
        stop.BayFlags = 0;
        stop.BayExitFrom = -1;
        w.BayBus[si] = -1;
        w.Counters.BayExitWaitTicksTotal += waitedTicks;
    }

    /// <summary>После загрузки сейва: <see cref="World.BayBus"/> по автобусам <c>InBay</c> (производное).</summary>
    internal static void RebuildBay(World w)
    {
        System.Array.Fill(w.BayBus, -1);
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.InBay) != 0)
            {
                int si = BusStopRules.TargetStop(w.Net, v, i);
                if (si >= 0)
                {
                    w.BayBus[si] = i;
                }
            }
        }
    }

    /// <summary>
    /// Удержание по интервалу (ADR-0008): автобус на контрольной остановке не уходит раньше <c>LastDepartTick + HoldAlpha * Headway</c>
    /// (<paramref name="departAt"/> - тик отправления без удержания); добавка не больше <c>HoldMaxTicks</c>. 0 - без удержания.
    /// </summary>
    private static int HoldTicks(TransitDesc cfg, in BusStopData stop, in BusRouteData br, int k, bool terminal, long departAt)
    {
        if (!(cfg.HoldAlpha > 0f) || terminal || stop.LastDepartTick < 0 || cfg.HoldEveryN < 1 || k % cfg.HoldEveryN != 0)
        {
            return 0;
        }

        long earliest = stop.LastDepartTick + DetMath.ToLongSat(cfg.HoldAlpha * br.HeadwayTicks);
        long extra = earliest - departAt;
        if (extra <= 0)
        {
            return 0;
        }

        return (int)(extra > cfg.HoldMaxTicks ? cfg.HoldMaxTicks : extra);
    }

    private static void Dwell(World w, int i)
    {
        VehicleStore v = w.Vehicles;
        if (v.DwellTicks[i] > 0 && --v.DwellTicks[i] > 0)
        {
            return;
        }

        if ((v.Flags[i] & (byte)VehicleFlags.InBay) != 0)
        {
            ExitBay(w, i);
            return;
        }

        int r = v.BusRoute[i];
        ref BusRouteData br = ref w.Net.BusRoutes[r];
        Advance(w, i, r, ref br, v.Lane[i], v.S[i]);
    }

    private enum AdvanceResult
    {
        Deferred,   // построение маршрута отложено: автобус стоит, повтор на следующем тике
        Advanced,   // маршрут к следующей остановке назначен
        Removed,    // дальше остановок нет - автобус деспаунен
    }

    /// <summary>
    /// Едет к следующей остановке после целевой (Build от точки <paramref name="lane"/>/<paramref name="s"/>: полоса и позиция автобуса или, для кармана, его остановки);
    /// без пути остановки пропускаются; дальше нет - деспаун.
    /// </summary>
    private static AdvanceResult Advance(World w, int i, int r, ref BusRouteData br, int lane, float s)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        for (int k = v.BusNextStop[i] + 1; k < br.StopCount; k++)
        {
            ref readonly BusStopData next = ref net.BusStops[br.FirstStop + k];
            BuildStatus st = w.Router.Build(lane, s, next.Lane, next.S, w.Tick, out RouteHandle h);
            if (st == BuildStatus.Deferred)
            {
                return AdvanceResult.Deferred; // стоим и повторяем на следующем тике (DwellTicks уже 0)
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
            v.MarkRouteDirty(i);
            v.NextConn[i] = -1;
            v.DestLane[i] = next.Lane;
            v.DestS[i] = next.S;
            if ((v.Flags[i] & (byte)VehicleFlags.AtStop) != 0)
            {
                net.BusStops[br.FirstStop + v.BusNextStop[i]].LastDepartTick = w.Tick;
            }

            v.BusNextStop[i] = k;
            v.DwellTicks[i] = 0;
            v.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.AtStop);
            return AdvanceResult.Advanced;
        }

        Remove(w, i, r, ref br);
        return AdvanceResult.Removed;
    }

    /// <summary>Остановка, на которой высаживаются оставшиеся пассажиры исчезающего автобуса: целевая (или последняя маршрута).</summary>
    private static int TargetStopOrLast(World w, int i, in BusRouteData br)
    {
        int k = w.Vehicles.BusNextStop[i];
        k = (uint)k < (uint)br.StopCount ? k : br.StopCount - 1;
        return br.FirstStop + k;
    }

    private static void Remove(World w, int i, int r, ref BusRouteData br)
    {
        VehicleStore v = w.Vehicles;
        br.ActiveBuses--;
        w.BusRouteStats[r].ActiveBuses = br.ActiveBuses;
        if (w.Peds != null && w.Peds.PaxHead[i] >= 0)
        {
            Stops.DropAll(w, i, TargetStopOrLast(w, i, in br));
        }

        if ((v.Flags[i] & (byte)VehicleFlags.InBay) != 0)
        {
            int si = BusStopRules.TargetStop(w.Net, v, i);
            if (si >= 0)
            {
                ref BusStopData bs = ref w.Net.BusStops[si];
                ReleaseBay(w, i, si, ref bs, bs.BayExitFrom >= 0 ? w.Tick - bs.BayExitFrom : 0L);
            }
        }

        w.Occupancy.UnlinkAll(i);
        if (v.Route[i] >= 0)
        {
            w.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
        }

        w.Masks.Remove(v, i);   // метрики фазы 9 считают только оставшихся (ADR-0019, A4)
        v.Despawn(v.HandleOf(i));
    }
}
