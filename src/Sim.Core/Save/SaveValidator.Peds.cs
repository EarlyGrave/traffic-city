using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;

namespace Sim.Core.Save;

// Проверки состояния пешеходов после загрузки (pedestrians.md §11): после Ok Step безопасен - ни одна ссылка не выходит за массив,
// списки и очереди согласованы, события колеса восстановимы из EvTick/ReadyTick.
internal static partial class SaveValidator
{
    private const int MaxPedSpeedMmPerTick = 100_000;

    /// <summary>Параметры секции peds из PARM (до создания мира): без переполнений и нулевых делителей.</summary>
    public static LoadStatus ValidatePedParams(LoadContext c)
    {
        ref readonly PedDesc p = ref c.Desc.Peds;
        if (c.Desc.Transit.PassengerModel < PassengerModel.Counter || c.Desc.Transit.PassengerModel > PassengerModel.Agents)
        {
            return LoadStatus.InvalidValue;
        }

        if (!p.Enabled)
        {
            return c.Desc.Transit.PassengerModel == PassengerModel.Agents ? LoadStatus.InvalidValue : LoadStatus.Ok;
        }

        const int big = 1 << 28;
        bool ok = p.Count >= 0 && p.Count <= big && p.Residents >= 0 && p.Residents <= big &&
                  p.SpawnRampTicks >= 0 && p.SpawnRampTicks <= big && p.SpeedMinMmPerTick >= 1 && p.SpeedMaxMmPerTick <= MaxPedSpeedMmPerTick &&
                  p.ActivityMinTicks >= 1 && p.ActivityMaxTicks <= big && p.WalkOnlyMm >= 0 && p.WalkOnlyMm <= big && p.MaxWalkToStopMm >= 0 && p.MaxWalkToStopMm <= big &&
                  p.MaxWalkMm >= 1 && p.MaxWalkMm <= big && p.WalkTicks >= 0 && p.WalkTicks <= big && p.ClearSpeedMmPerTick >= 1 && p.ClearSpeedMmPerTick <= MaxPedSpeedMmPerTick &&
                  p.MaxCloseTicks >= 0 && p.MaxCloseTicks <= big && p.CarWindowTicks >= 0 && p.CarWindowTicks <= big &&
                  p.PathfindBudgetPerTick >= 1 && p.PathfindBudgetPerTick <= big && p.WorkBudgetPerTick >= 1 && p.WorkBudgetPerTick <= big &&
                  p.StuckCrossTicks >= 0 && p.StuckCrossTicks <= big && p.StuckStopTicks >= 0 && p.StuckStopTicks <= big;
        return ok && WorldBuilder.ValidatePeds(in c.Desc) == CreateStatus.Ok ? LoadStatus.Ok : LoadStatus.InvalidValue;
    }

    /// <summary>
    /// Владелец пешехода <paramref name="p"/>: житель-заготовка в пути (<c>peds.source = preform</c>) либо житель города в <c>InLeg</c> с этим пешеходом (<c>city</c>, zoning-demand.md §4.1).
    /// </summary>
    private static bool PedOwnerOk(World w, PedWorld pw, int p, int r)
    {
        if (!pw.CitySource)
        {
            ResidentStore rs = pw.R;
            return (uint)r < (uint)rs.Count && rs.Ped[r] == p && rs.Where[r] >= ResidentStore.ToWork;
        }

        City.CityWorld? city = w.City;
        return city != null && (uint)r < (uint)city.Residents.HighWater && city.Residents.Pool.IsAlive(r) && city.Residents.State[r] == (byte)Api.ResidentState.InLeg &&
               city.Residents.Agent[r] == p && city.Residents.AgentGen[r] == pw.P.Pool.GenerationOf(p) && city.Residents.LegMode[r] != (byte)Api.TravelMode.Car;
    }

    /// <summary>Пешеходы, жители, переходы, очереди; после пересборки сети пешеходов (индексы рёбер известны).</summary>
    public static LoadStatus ValidatePeds(LoadContext c)
    {
        World w = c.W;
        PedWorld? pw = w.Peds;
        if (pw == null)
        {
            return LoadStatus.Ok;
        }

        PedNetwork net = pw.Net;
        PedStore ps = pw.P;
        ResidentStore rs = pw.R;
        CrossingState xs = pw.X;
        long tick = w.Tick;
        int hw = ps.HighWater;
        int edges = net.EdgeCount;
        int walkways = net.SidewalkEdgeCount;
        LoadStatus st = ValidatePool(c, ps.Pool);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        if (w.TargetPeds < 0)
        {
            return Bad(c, 0, 120);
        }

        // ---- жители ----
        int active = 0;
        for (int r = 0; r < rs.Count; r++)
        {
            int where = rs.Where[r];
            int he = rs.HomeEdge[r];
            int we = rs.WorkEdge[r];
            if (where > ResidentStore.ToHome || (uint)he >= (uint)walkways || (uint)we >= (uint)walkways ||
                (uint)rs.HomeSMm[r] > (uint)net.Edges[he].LenMm || (uint)rs.WorkSMm[r] > (uint)net.Edges[we].LenMm ||
                rs.HomeZone[r] < -1 || rs.HomeZone[r] >= w.Net.ZoneCount || rs.WorkZone[r] < -1 || rs.WorkZone[r] >= w.Net.ZoneCount ||
                rs.ModeOut[r] > 1 || rs.ModeBack[r] > 1 || rs.ReadyTick[r] < 0 ||
                !RouteStopOk(w, rs.RouteOut[r], rs.StopAOut[r], rs.StopBOut[r]) || !RouteStopOk(w, rs.RouteBack[r], rs.StopABack[r], rs.StopBBack[r]))
            {
                return Bad(c, r, 121);
            }

            int rq = rs.ReadyNext[r];
            if (rq < ResidentStore.NotQueued || rq >= rs.Count)
            {
                return Bad(c, r, 122);
            }

            int p = rs.Ped[r];
            if (where <= ResidentStore.AtWork)
            {
                if (p != -1 || (rq == ResidentStore.NotQueued && rs.ReadyTick[r] < tick))
                {
                    return Bad(c, r, 123);
                }
            }
            else
            {
                active++;
                if ((uint)p >= (uint)hw || !ps.Pool.IsAlive(p) || ps.Resident[p] != r || rq != ResidentStore.NotQueued)
                {
                    return Bad(c, r, 124);
                }
            }
        }

        if (!pw.CitySource && active != ps.AliveCount)
        {
            return Bad(c, active, 125);
        }

        // Очередь готовых: цепочка от головы, только простаивающие, без петель, хвост совпадает.
        var queued = new ulong[(rs.Count + 63) >> 6];
        int walked = 0;
        int last = -1;
        for (int r = rs.ReadyHead; r != -1; r = rs.ReadyNext[r])
        {
            if ((uint)r >= (uint)rs.Count || rs.Where[r] > ResidentStore.AtWork || (queued[r >> 6] & (1UL << (r & 63))) != 0 || ++walked > rs.Count)
            {
                return Bad(c, walked, 126);
            }

            queued[r >> 6] |= 1UL << (r & 63);
            last = r;
        }

        int marked = 0;
        for (int r = 0; r < rs.Count; r++)
        {
            if (rs.ReadyNext[r] != ResidentStore.NotQueued)
            {
                marked++;
            }
        }

        if (marked != walked || last != rs.ReadyTail || (rs.ReadyHead == -1) != (rs.ReadyTail == -1) || (last != -1 && rs.ReadyNext[last] != -1))
        {
            return Bad(c, walked, 127);
        }

        // ---- пешеходы ----
        int pendingPath = 0;
        int atCrossing = 0;
        int atStop = 0;
        int onBus = 0;
        for (int p = 0; p < hw; p++)
        {
            if (!ps.Pool.IsAlive(p))
            {
                continue;
            }

            int r = ps.Resident[p];
            int count = ps.PathCount[p];
            int cursor = ps.PathCursor[p];
            int ge = ps.GoalEdge[p];
            byte state = ps.State[p];
            if (state > (byte)PedState.OnBus || ps.Leg[p] > (byte)PedLeg.FromStop || !PedOwnerOk(w, pw, p, r) ||
                ps.VMmT[p] < 1 || ps.VMmT[p] > MaxPedSpeedMmPerTick || count < 0 || count > ps.MaxSteps || cursor < 0 || (count > 0 && cursor >= count) || (count == 0 && cursor != 0) ||
                (uint)ge >= (uint)walkways || (uint)ps.GoalSMm[p] > (uint)net.Edges[ge].LenMm || ps.T0[p] < 0 || ps.T0[p] > tick || ps.TripTick[p] < 0 || ps.TripTick[p] > tick)
            {
                return Bad(c, p, 130);
            }

            int b = p * ps.MaxSteps;
            for (int k = 0; k < count; k++)
            {
                int step = ps.Path[b + k];
                int e = step & PedStore.StepMask;
                if ((uint)e >= (uint)edges || ((step & PedStore.StepCross) != 0) != (net.Edges[e].Kind >= PedEdgeKind.Crossing))
                {
                    return Bad(c, p, 131);
                }
            }

            int cur = ps.Edge[p];
            var ped = (PedState)state;
            switch (ped)
            {
                case PedState.PendingPath:
                    pendingPath++;
                    if (count != 0 || (uint)cur >= (uint)walkways || (uint)ps.S0Mm[p] > (uint)net.Edges[cur].LenMm || ps.EvTick[p] != -1)
                    {
                        return Bad(c, p, 132);
                    }

                    break;
                case PedState.Walking:
                case PedState.Crossing:
                    {
                        bool crossing = ped == PedState.Crossing;
                        int e = cur & PedStore.StepMask;
                        if (count == 0 || cur != ps.Path[b + cursor] || (uint)e >= (uint)edges || ((cur & PedStore.StepCross) != 0) != crossing ||
                            (uint)ps.S0Mm[p] > (uint)net.Edges[e].LenMm)
                        {
                            return Bad(c, p, 133);
                        }

                        int rem = pw.EndMm(p, cur) - ps.S0Mm[p];
                        int dur = rem <= 0 ? 1 : (rem + ps.VMmT[p] - 1) / ps.VMmT[p];
                        if (ps.EvTick[p] != ps.T0[p] + dur || ps.EvTick[p] < tick)
                        {
                            return Bad(c, p, 134);
                        }

                        break;
                    }

                case PedState.AtCrossing:
                    atCrossing++;
                    if (count == 0 || cur != ps.Path[b + cursor] || (cur & PedStore.StepCross) == 0 || ps.EvTick[p] != -1)
                    {
                        return Bad(c, p, 135);
                    }

                    break;
                case PedState.AtStop:
                    atStop++;
                    if (ps.EvTick[p] != -1 || ps.AlightStop[p] < -1 || ps.AlightStop[p] >= w.Net.BusRouteStopCount)
                    {
                        return Bad(c, p, 136);
                    }

                    break;
                default:
                    onBus++;
                    if (ps.EvTick[p] != -1 || (uint)ps.Bus[p] >= (uint)w.Vehicles.HighWater || !w.Vehicles.Pool.IsAlive(ps.Bus[p]) ||
                        ps.AlightStop[p] < -1 || ps.AlightStop[p] >= w.Net.BusRouteStopCount)
                    {
                        return Bad(c, p, 137);
                    }

                    break;
            }
        }

        // ---- очередь путей: ровно PendingPath, каждый один раз ----
        if (pw.PathQueue.Count != pendingPath)
        {
            return Bad(c, pw.PathQueue.Count, 140);
        }

        var inQueue = new ulong[(hw + 63) >> 6];
        for (int q = 0; q < pw.PathQueue.Count; q++)
        {
            PathRequest req = pw.PathQueue.At(q);
            if ((uint)req.Index >= (uint)hw || !ps.Pool.IsAlive(req.Index, req.Generation) || ps.State[req.Index] != (byte)PedState.PendingPath ||
                (inQueue[req.Index >> 6] & (1UL << (req.Index & 63))) != 0)
            {
                return Bad(c, q, 141);
            }

            inQueue[req.Index >> 6] |= 1UL << (req.Index & 63);
        }

        // ---- переходы и очереди ----
        int xn = PedWorld.XCount(w);
        if (xn > xs.Capacity)
        {
            return Bad(c, xn, 150);
        }

        var seen = new ulong[(hw + 63) >> 6];
        int queuedCrossing = 0;
        for (int x = 0; x < xs.Capacity; x++)
        {
            if (x >= xn)
            {
                if (xs.XState[x] != 0 || xs.QHead[x] != -1 || xs.QTail[x] != -1 || xs.QCount[x] != 0 || xs.OnCross[x] != 0)
                {
                    return Bad(c, x, 151);
                }

                continue;
            }

            if (xs.XState[x] > XStateCode.Busy || xs.OnCross[x] < 0 || xs.Releases[x] < 0 || xs.WaitSum[x] < 0)
            {
                return Bad(c, x, 152);
            }

            st = WalkQueue(c, pw, xs.QHead[x], xs.QTail[x], xs.QCount[x], PedState.AtCrossing, x, -1, seen);
            if (st != LoadStatus.Ok)
            {
                return st;
            }

            queuedCrossing += xs.QCount[x];
        }

        if (queuedCrossing != atCrossing)
        {
            return Bad(c, queuedCrossing, 153);
        }

        int stops = w.Net.BusStopCount;
        int queuedStops = 0;
        for (int s = 0; s < pw.StopQCount.Length; s++)
        {
            if (s >= stops)
            {
                if (pw.StopQHead[s] != -1 || pw.StopQTail[s] != -1 || pw.StopQCount[s] != 0)
                {
                    return Bad(c, s, 154);
                }

                continue;
            }

            st = WalkQueue(c, pw, pw.StopQHead[s], pw.StopQTail[s], pw.StopQCount[s], PedState.AtStop, -1, s, seen);
            if (st != LoadStatus.Ok)
            {
                return st;
            }

            queuedStops += pw.StopQCount[s];
        }

        if (queuedStops != atStop)
        {
            return Bad(c, queuedStops, 155);
        }

        int passengers = 0;
        for (int v = 0; v < pw.PaxHead.Length; v++)
        {
            int head = pw.PaxHead[v];
            if (v >= w.Vehicles.HighWater || !w.Vehicles.Pool.IsAlive(v))
            {
                if (head != -1)
                {
                    return Bad(c, v, 156);
                }

                continue;
            }

            int steps = 0;
            for (int p = head; p != -1; p = ps.Next[p])
            {
                if ((uint)p >= (uint)hw || !ps.Pool.IsAlive(p) || ps.State[p] != (byte)PedState.OnBus || ps.Bus[p] != v ||
                    (seen[p >> 6] & (1UL << (p & 63))) != 0 || ++steps > hw)
                {
                    return Bad(c, v, 157);
                }

                seen[p >> 6] |= 1UL << (p & 63);
                passengers++;
            }
        }

        if (passengers != onBus)
        {
            return Bad(c, passengers, 158);
        }

        for (int k = 0; k < PedWorld.HistBuckets; k++)
        {
            if (pw.CrossWaitHist[k] < 0 || pw.StopWaitHist[k] < 0)
            {
                return Bad(c, k, 159);
            }
        }

        return LoadStatus.Ok;
    }

    private static bool RouteStopOk(World w, int route, int a, int b) =>
        route >= -1 && route < w.Net.BusRouteCount && a >= -1 && a < w.Net.BusRouteStopCount && b >= -1 && b < w.Net.BusRouteStopCount;

    /// <summary>Очередь через <c>Next</c>: длина, хвост, состояние и принадлежность (переход <paramref name="x"/> или остановка <paramref name="stop"/>), каждый пешеход в одной очереди.</summary>
    private static LoadStatus WalkQueue(LoadContext c, PedWorld pw, int head, int tail, int count, PedState state, int x, int stop, ulong[] seen)
    {
        PedStore ps = pw.P;
        int hw = ps.HighWater;
        int row = x >= 0 ? x : stop;
        if (count < 0 || (count == 0) != (head == -1) || (count == 0) != (tail == -1))
        {
            return Bad(c, row, 160);
        }

        int n = 0;
        int last = -1;
        for (int p = head; p != -1; p = ps.Next[p])
        {
            if ((uint)p >= (uint)hw || !ps.Pool.IsAlive(p) || ps.State[p] != (byte)state || (seen[p >> 6] & (1UL << (p & 63))) != 0 || ++n > count)
            {
                return Bad(c, row, 161);
            }

            if (x >= 0)
            {
                int e = ps.Edge[p] & PedStore.StepMask;
                if ((uint)e >= (uint)pw.Net.EdgeCount || pw.Net.Edges[e].Kind < PedEdgeKind.Crossing || pw.Net.XOfEdge(e) != x)
                {
                    return Bad(c, row, 162);
                }
            }

            seen[p >> 6] |= 1UL << (p & 63);
            last = p;
        }

        return n == count && last == tail && (last == -1 || ps.Next[last] == -1) ? LoadStatus.Ok : Bad(c, row, 163);
    }
}
