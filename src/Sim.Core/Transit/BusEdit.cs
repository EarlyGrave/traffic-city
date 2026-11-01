using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Routing;
using Sim.Core.Tools;
using Sim.Core.Util;

namespace Sim.Core.Transit;

/// <summary>
/// Автобусные инструменты игрока (T9, player-tools.md §8.7): структурные правки линий в фазе 1 (проверки - <see cref="ToolValidator"/>, до вызова). Все правки «на месте»:
/// индексы живых остановок и линий не меняются, список остановок линии - блок арены <c>BusRouteStops</c> (по лестнице; при смене ступеньки - новый блок, старый - в free-list).
/// <list type="bullet">
/// <item>Вставка остановки: автобусы линии сохраняют цель (позиция цели сдвигается).</item>
/// <item>Удаление остановки: автобус, ехавший к ней (или стоявший на ней), получает следующую достижимую остановку линии (маршрут освобождается, перемаршрутизация - фаза 3);
/// достижимых нет - доезжает до прежней цели и завершает рейс (<see cref="BusStopPhase"/>). Автобус в кармане удалённой остановки снимается (как при сносе кармана).</item>
/// <item>Снятие линии: автобусы на остановках завершают рейс сразу, идущие - отвязываются от линии (<c>BusRoute = -1</c>), доезжают до цели и исчезают там; ожидающие пешеходы
/// и пассажиры выходят (пешеходы - на точке доступа остановки).</item>
/// </list>
/// Ничего не аллоцирует (связность - scratch инструментов, выделяется один раз).
/// </summary>
internal static class BusEdit
{
    /// <summary>Новая линия без остановок; -1 - пул полон (валидатор проверяет заранее).</summary>
    public static int CreateRoute(World w, int headway, int type, bool suspended, uint tag)
    {
        NetworkStore n = w.Net;
        if (!n.BusRoutePool.TryAllocate(out int r, out int gen))
        {
            return -1;
        }

        n.ToolsTouched = 1;
        n.BusRoutes[r] = new BusRouteData
        {
            FirstStop = 0,
            StopCount = 0,
            HeadwayTicks = suspended ? -headway : headway,
            VehicleType = type,
            ActiveBuses = 0,
            LastDispatchTick = -1,
            Generation = gen,
        };
        w.BusRouteStats[r] = new BusRouteStats { LastDispatchTick = -1 };
        n.Version++;
        w.Emit(SimEventKind.BusRouteCreated, tag, r, gen);
        return r;
    }

    /// <summary>Блок списка остановок нужно переложить при смене длины <paramref name="n"/> -> <paramref name="newN"/> (иначе правка на месте).</summary>
    public static bool NeedsNewBlock(NetworkStore net, int first, int n, int newN) =>
        newN > 0 && !(n > 0 && net.BusRouteStopArena.IsLadder(first) && BlockArena.CapFor(n) == BlockArena.CapFor(newN));

    /// <summary>Новая остановка на позиции <paramref name="pos"/> линии <paramref name="r"/> (0..StopCount); -1 - нет места (валидатор проверяет заранее).</summary>
    public static int InsertStop(World w, int r, int pos, int lane, float s, BusStopKind kind, uint tag)
    {
        NetworkStore n = w.Net;
        n.ToolsTouched = 1;
        ref BusRouteData br = ref n.BusRoutes[r];
        int cnt = br.StopCount;
        int first = br.FirstStop;
        if (NeedsNewBlock(n, first, cnt, cnt + 1))
        {
            int nf = n.AllocBlock(n.BusRouteStopArena, cnt + 1);
            if (nf < 0)
            {
                return -1;
            }

            System.Array.Copy(n.BusRouteStops, first, n.BusRouteStops, nf, cnt);
            n.FreeBusRouteStopBlock(first, cnt);
            first = nf;
        }

        if (!n.BusStopPool.TryAllocate(out int si, out int gen))
        {
            br.FirstStop = first;   // блок уже переложен - список остаётся прежним (валидатор этого не допускает)
            return -1;
        }

        for (int k = cnt; k > pos; k--)
        {
            n.BusRouteStops[first + k] = n.BusRouteStops[first + k - 1];
        }

        n.BusRouteStops[first + pos] = si;
        br.FirstStop = first;
        br.StopCount = cnt + 1;
        n.BusStops[si] = new BusStopData
        {
            Lane = lane,
            S = s,
            Kind = kind,
            Route = r,
            Waiting = 0,
            LastArrivalTick = -1,
            LastDepartTick = -1,
            Generation = gen,
            BayExitFrom = -1,
        };
        w.BayBus[si] = -1;
        ClearQueue(w, si);

        // Автобусы линии сохраняют свою цель: её позиция сдвигается, если остановку вставили не позже неё.
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (IsBusOf(v, i, r) && v.BusNextStop[i] >= pos)
            {
                v.BusNextStop[i]++;
            }
        }

        n.Version++;
        w.Emit(SimEventKind.BusStopCreated, tag, si, gen);
        return si;
    }

    /// <summary>Убирает остановку с линии и освобождает слот (поколение +1); автобусы линии переводятся на следующую достижимую остановку.</summary>
    public static void RemoveStop(World w, int si, uint tag)
    {
        NetworkStore n = w.Net;
        n.ToolsTouched = 1;
        int r = n.BusStops[si].Route;
        int pos = n.StopPos(si);
        VehicleStore v = w.Vehicles;
        DropQueue(w, si);

        // Автобус в кармане этой остановки вставить некуда - завершает рейс (пассажиры выходят), как при сносе кармана.
        for (int i = 0; i < v.HighWater; i++)
        {
            if (IsBusOf(v, i, r) && (v.Flags[i] & (byte)VehicleFlags.InBay) != 0 && BusStopRules.TargetStop(n, v, i) == si)
            {
                BusStopPhase.Remove(w, i);
            }
        }

        if (pos >= 0)
        {
            ref BusRouteData br = ref n.BusRoutes[r];
            int cnt = br.StopCount;
            int first = br.FirstStop;
            for (int k = pos; k < cnt - 1; k++)
            {
                n.BusRouteStops[first + k] = n.BusRouteStops[first + k + 1];
            }

            n.BusRouteStops[first + cnt - 1] = 0;
            if (cnt - 1 == 0)
            {
                n.FreeBusRouteStopBlock(first, cnt);
                br.FirstStop = 0;
            }
            else if (NeedsNewBlock(n, first, cnt, cnt - 1))
            {
                int nf = n.AllocBlock(n.BusRouteStopArena, cnt - 1);
                if (nf >= 0)
                {
                    System.Array.Copy(n.BusRouteStops, first, n.BusRouteStops, nf, cnt - 1);
                    n.FreeBusRouteStopBlock(first, cnt);
                    br.FirstStop = nf;
                }
                else
                {
                    // Нет места под меньший блок (валидатор этого не пропускает): откат удаления.
                    for (int k = cnt - 1; k > pos; k--)
                    {
                        n.BusRouteStops[first + k] = n.BusRouteStops[first + k - 1];
                    }

                    n.BusRouteStops[first + pos] = si;
                    return;
                }
            }

            br.StopCount = cnt - 1;
            Connectivity? conn = null;
            for (int i = 0; i < v.HighWater; i++)
            {
                if (!IsBusOf(v, i, r))
                {
                    continue;
                }

                int k = v.BusNextStop[i];
                if (k > pos)
                {
                    v.BusNextStop[i] = k - 1;
                }
                else if (k == pos && (v.Flags[i] & (byte)VehicleFlags.InBay) == 0)
                {
                    // Цель исчезла (или автобус стоял на ней): дальше - к следующей достижимой остановке.
                    v.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.AtStop);
                    v.DwellTicks[i] = 0;
                    conn ??= w.ToolScratch.Conn ??= new Connectivity(n);
                    Retarget(w, conn, i);
                }
            }
        }

        FreeStop(w, si, tag);
        n.Version++;
    }

    /// <summary>Снимает линию: автобусы на остановках завершают рейс, идущие отвязываются и исчезают у своей цели; остановки и маршрут освобождаются.</summary>
    public static void RemoveRoute(World w, int r, uint tag)
    {
        NetworkStore n = w.Net;
        n.ToolsTouched = 1;
        ref BusRouteData br = ref n.BusRoutes[r];
        for (int k = 0; k < br.StopCount; k++)
        {
            DropQueue(w, n.BusRouteStops[br.FirstStop + k]);
        }

        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!IsBusOf(v, i, r))
            {
                continue;
            }

            if ((v.Flags[i] & (byte)(VehicleFlags.AtStop | VehicleFlags.InBay)) != 0 || v.Lane[i] < 0)
            {
                BusStopPhase.Remove(w, i);   // стоит на остановке: конечная здесь
                continue;
            }

            int target = BusStopRules.TargetStop(n, v, i);
            if (w.Peds != null && w.Peds.PaxHead[i] >= 0 && target >= 0)
            {
                Stops.DropAll(w, i, target);
            }

            v.Passengers[i] = 0;
            v.BusRoute[i] = -1;   // доезжает до цели (DestLane/DestS) и завершает рейс там (BusStopPhase)
            v.BusNextStop[i] = 0;
            v.DwellTicks[i] = 0;
        }

        for (int k = 0; k < br.StopCount; k++)
        {
            int si = n.BusRouteStops[br.FirstStop + k];
            n.BusRouteStops[br.FirstStop + k] = 0;
            FreeStop(w, si, tag);
        }

        n.FreeBusRouteStopBlock(br.FirstStop, br.StopCount);
        int gen = br.Generation;
        n.BusRoutePool.Free(r);
        n.BusRoutes[r] = NetworkStore.DeadBusRoute(n.BusRoutePool.GenerationOf(r));
        w.BusRouteStats[r] = new BusRouteStats { LastDispatchTick = -1 };
        n.Version++;
        w.Emit(SimEventKind.BusRouteRemoved, tag, r, gen);
    }

    /// <summary>Интервал, тип ТС и приостановка (<see cref="BusRouteFlags"/>); 0 / -1 / 0 - не менять. Идущие автобусы не трогаются.</summary>
    public static void SetParams(World w, int r, int headway, int type, int flags)
    {
        NetworkStore n = w.Net;
        n.ToolsTouched = 1;
        ref BusRouteData br = ref n.BusRoutes[r];
        int abs = headway > 0 ? headway : (br.HeadwayTicks < 0 ? -br.HeadwayTicks : br.HeadwayTicks);
        bool suspended = (flags & BusRouteFlags.Suspend) != 0 || ((flags & BusRouteFlags.Resume) == 0 && br.HeadwayTicks < 0);
        br.HeadwayTicks = suspended ? -abs : abs;
        if (type >= 0)
        {
            br.VehicleType = type;
        }

        n.Version++;
    }

    /// <summary>Следующая остановка линии с позиции <c>BusNextStop</c>, до которой автобус доедет; маршрут освобождается (новый строит фаза 3). Нет такой - цели нет.</summary>
    private static void Retarget(World w, Connectivity conn, int i)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        ref readonly BusRouteData br = ref n.BusRoutes[v.BusRoute[i]];
        int lane = v.Lane[i];
        for (int k = v.BusNextStop[i]; k < br.StopCount; k++)
        {
            ref readonly BusStopData st = ref n.BusStops[n.BusRouteStops[br.FirstStop + k]];
            if (lane >= 0 && conn.Reaches(lane, v.S[i], st.Lane, st.S))
            {
                v.BusNextStop[i] = k;
                v.DestLane[i] = st.Lane;
                v.DestS[i] = st.S;
                RouteNav.ReleaseRoute(w, i);
                return;
            }
        }

        v.BusNextStop[i] = br.StopCount;   // цели нет: доезжает до прежней и завершает рейс
    }

    private static bool IsBusOf(VehicleStore v, int i, int r) => v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 && v.BusRoute[i] == r;

    private static void FreeStop(World w, int si, uint tag)
    {
        NetworkStore n = w.Net;
        int gen = n.BusStops[si].Generation;
        n.BusStopPool.Free(si);
        n.BusStops[si] = NetworkStore.DeadBusStop(n.BusStopPool.GenerationOf(si));
        w.BayBus[si] = -1;
        ClearQueue(w, si);
        w.Emit(SimEventKind.BusStopRemoved, tag, si, gen);
    }

    /// <summary>Ожидающие на остановке (модель пассажиров-агентов) уходят: нога завершается, как у пешей (житель продолжит день).</summary>
    private static void DropQueue(World w, int si)
    {
        PedWorld? pw = w.Peds;
        if (pw == null)
        {
            return;
        }

        PedStore ps = pw.P;
        int p = pw.StopQHead[si];
        while (p >= 0)
        {
            int next = ps.Next[p];
            ps.Next[p] = -1;
            pw.Arrive(w, p);
            p = next;
        }

        ClearQueue(w, si);
    }

    private static void ClearQueue(World w, int si)
    {
        PedWorld? pw = w.Peds;
        if (pw == null)
        {
            return;
        }

        pw.StopQHead[si] = -1;
        pw.StopQTail[si] = -1;
        pw.StopQCount[si] = 0;
        pw.StopWaiting[si] = 0;
    }
}
