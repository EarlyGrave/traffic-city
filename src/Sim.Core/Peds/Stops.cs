using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;

namespace Sim.Core.Peds;

/// <summary>
/// Остановки и пассажиры-агенты (pedestrians.md §7, <c>transit.passenger_model = "agents"</c>). Жители с автобусной ногой (<c>ResidentStore.ModeOut/ModeBack = 1</c>) идут к точке доступа
/// остановки посадки (нога <c>ToStop</c>), встают в FIFO остановки, садятся при прибытии автобуса (<c>BusStopPhase</c>), едут и выходят на своей остановке высадки либо на первой
/// достигнутой после неё (пропущенная остановка не теряет людей), затем идут к назначению (нога <c>FromStop</c>).
/// <para>
/// Контракт: пешеход в очереди остановки: <c>State = AtStop</c>, <c>T0</c> = начало ожидания, <c>EvTick = -1</c>, список через <c>PedStore.Next</c> (<c>StopQHead/StopQTail/StopQCount</c>), цель ноги
/// (<c>GoalEdge</c>, <c>GoalSMm</c>) остаётся точкой доступа остановки (по ней снапшот ставит очередь); пассажир: <c>State = OnBus</c>, <c>Bus</c> = индекс автобуса, <c>AlightStop</c> = глобальный
/// индекс остановки высадки (он же запись <c>BusRouteStops</c>), список <c>PaxHead[bus]</c> упорядочен по возрастанию индекса пешехода (порядок высадки, §7).
/// Остановка принадлежит ровно одному маршруту (<c>BusStopData.Route</c>), остановки маршрута идут подряд, поэтому порядок остановок на маршруте - порядок глобальных индексов.
/// Точка доступа остановки - тротуар той же стороны, что полоса остановки (правая по ходу), ось <c>s</c> остановки; нет рёбер на этой стороне - противоположная.
/// </para>
/// </summary>
internal static class Stops
{
    /// <summary>Точка доступа остановки <paramref name="stop"/> на тротуаре: ребро и смещение от его <c>A</c>, мм. false - у сегмента остановки нет тротуаров.</summary>
    public static bool AccessPoint(World w, int stop, out int edge, out int sMm)
    {
        edge = -1;
        sMm = 0;
        NetworkStore net = w.Net;
        PedNetwork pn = net.PedNet!;
        if ((uint)stop >= (uint)net.BusStopCount)
        {
            return false;
        }

        ref readonly BusStopData bs = ref net.BusStops[stop];
        if ((uint)bs.Lane >= (uint)net.LaneCount)
        {
            return false;
        }

        ref readonly LaneData ld = ref net.Lanes[bs.Lane];
        if (ld.Segment < 0)
        {
            return false;
        }

        float len = net.Segments[ld.Segment].Length;
        float s = ld.Direction == 0 ? bs.S : len - bs.S;
        s = s < 0f ? 0f : (s > len ? len : s);
        return pn.PointOnSide(ld.Segment, ld.Direction, s, out edge, out sMm) || pn.PointOnSide(ld.Segment, 1 - ld.Direction, s, out edge, out sMm);
    }

    /// <summary>Нога <c>ToStop</c> закончилась у остановки посадки: пешеход встаёт в хвост очереди остановки. Нет остановки у жителя - прибытие как у пешей ноги.</summary>
    public static void OnArrive(World w, int p)
    {
        PedWorld pw = w.Peds!;
        PedStore ps = pw.P;
        ResidentStore rs = pw.R;
        int r = ps.Resident[p];
        int stop = -1;
        if (pw.CitySource)
        {
            stop = TripLegs.StopA(w, r);
        }
        else if ((uint)r < (uint)rs.Count)
        {
            stop = rs.Where[r] == ResidentStore.ToWork ? rs.StopAOut[r] : rs.StopABack[r];
        }

        if ((uint)stop >= (uint)w.Net.BusStopCount)
        {
            pw.Arrive(w, p);
            return;
        }

        ps.State[p] = (byte)PedState.AtStop;
        ps.T0[p] = w.Tick;
        ps.EvTick[p] = -1;
        ps.Next[p] = -1;
        int tail = pw.StopQTail[stop];
        if (tail >= 0)
        {
            ps.Next[tail] = p;
        }
        else
        {
            pw.StopQHead[stop] = p;
        }

        pw.StopQTail[stop] = p;
        pw.StopQCount[stop]++;
        SyncWaiting(w, stop);
    }

    /// <summary>
    /// Высадка в момент прибытия автобуса <paramref name="bus"/> на остановку <paramref name="stop"/>: выходят все с <c>AlightStop &lt;= stop</c> (на конечной - все), по возрастанию индекса пешехода.
    /// Вышедший - <c>PendingPath</c> в точке доступа остановки, нога <c>FromStop</c> к назначению. Возвращает число вышедших.
    /// </summary>
    public static int Alight(World w, int bus, int stop, bool all)
    {
        PedWorld pw = w.Peds!;
        PedStore ps = pw.P;
        int n = 0;
        int prev = -1;
        int p = pw.PaxHead[bus];
        bool access = AccessPoint(w, stop, out int edge, out int sMm);
        while (p >= 0)
        {
            int next = ps.Next[p];
            if (all || ps.AlightStop[p] <= stop)
            {
                if (prev >= 0)
                {
                    ps.Next[prev] = next;
                }
                else
                {
                    pw.PaxHead[bus] = next;
                }

                ps.Next[p] = -1;
                ps.Bus[p] = -1;
                ps.AlightStop[p] = -1;
                n++;
                w.Counters.BusPassengersAlighted++;
                Release(w, pw, p, access, edge, sMm);
            }
            else
            {
                prev = p;
            }

            p = next;
        }

        return n;
    }

    /// <summary>Пассажир вышел: <c>PendingPath</c> к назначению поездки либо (нет точки доступа) мгновенное прибытие.</summary>
    private static void Release(World w, PedWorld pw, int p, bool access, int edge, int sMm)
    {
        PedStore ps = pw.P;
        ResidentStore rs = pw.R;
        int r = ps.Resident[p];
        if (pw.CitySource)
        {
            TripLegs.OnAlighted(w, r, w.Tick - ps.T0[p]);
            if (!access || !TripLegs.DestPoint(w, r, out int destEdge, out int destSMm))
            {
                pw.Arrive(w, p);
                return;
            }

            ps.State[p] = (byte)PedState.PendingPath;
            ps.Leg[p] = (byte)PedLeg.FromStop;
            ps.Edge[p] = edge;
            ps.S0Mm[p] = sMm;
            ps.T0[p] = w.Tick;
            ps.GoalEdge[p] = destEdge;
            ps.GoalSMm[p] = destSMm;
            pw.EnqueuePath(p);
            return;
        }

        if (!access || (uint)r >= (uint)rs.Count)
        {
            pw.Arrive(w, p);
            return;
        }

        bool toWork = rs.Where[r] == ResidentStore.ToWork;
        ps.State[p] = (byte)PedState.PendingPath;
        ps.Leg[p] = (byte)PedLeg.FromStop;
        ps.Edge[p] = edge;
        ps.S0Mm[p] = sMm;
        ps.T0[p] = w.Tick;
        ps.GoalEdge[p] = toWork ? rs.WorkEdge[r] : rs.HomeEdge[r];
        ps.GoalSMm[p] = toWork ? rs.WorkSMm[r] : rs.HomeSMm[r];
        pw.EnqueuePath(p);
    }

    /// <summary>Автобус исчезает (<c>Remove</c>) с пассажирами - все выходят на остановке <paramref name="stop"/> (страховка: на конечной список уже пуст).</summary>
    public static void DropAll(World w, int bus, int stop)
    {
        PedWorld pw = w.Peds!;
        if (pw.PaxHead[bus] >= 0)
        {
            w.Vehicles.Passengers[bus] = (ushort)(w.Vehicles.Passengers[bus] - Alight(w, bus, stop, true));
        }
    }

    /// <summary>Посадка из головы очереди остановки <paramref name="stop"/> в автобус <paramref name="bus"/>, не больше <paramref name="max"/> человек. Возвращает число севших.</summary>
    public static int Board(World w, int bus, int stop, int max)
    {
        PedWorld pw = w.Peds!;
        PedStore ps = pw.P;
        ResidentStore rs = pw.R;
        int n = 0;
        while (n < max && pw.StopQHead[stop] >= 0)
        {
            int p = pw.StopQHead[stop];
            int next = ps.Next[p];
            pw.StopQHead[stop] = next;
            if (next < 0)
            {
                pw.StopQTail[stop] = -1;
            }

            pw.StopQCount[stop]--;
            long wait = w.Tick - ps.T0[p];
            w.Counters.PedStopWaitTicksTotal += wait;
            w.Counters.PedStopBoardings++;
            pw.StopWaitHist[wait >= PedWorld.HistBuckets * 20L ? PedWorld.HistBuckets - 1 : (int)(wait / 20)]++;

            int r = ps.Resident[p];
            ps.State[p] = (byte)PedState.OnBus;
            ps.Bus[p] = bus;
            if (pw.CitySource)
            {
                ps.AlightStop[p] = TripLegs.StopB(w, r);
                TripLegs.OnBoarded(w, r, wait);
            }
            else
            {
                ps.AlightStop[p] = rs.Where[r] == ResidentStore.ToWork ? rs.StopBOut[r] : rs.StopBBack[r];
            }

            ps.T0[p] = w.Tick;
            LinkPassenger(pw, bus, p);
            n++;
        }

        SyncWaiting(w, stop);
        return n;
    }

    /// <summary>Вставка в список пассажиров с сохранением порядка по возрастанию индекса пешехода.</summary>
    private static void LinkPassenger(PedWorld pw, int bus, int p)
    {
        PedStore ps = pw.P;
        int prev = -1;
        int cur = pw.PaxHead[bus];
        while (cur >= 0 && cur < p)
        {
            prev = cur;
            cur = ps.Next[cur];
        }

        ps.Next[p] = cur;
        if (prev >= 0)
        {
            ps.Next[prev] = p;
        }
        else
        {
            pw.PaxHead[bus] = p;
        }
    }

    /// <summary>Производное для API: <c>StopWaiting</c> и <c>BusStopData.Waiting</c> = min(очередь, 65535).</summary>
    public static void SyncWaiting(World w, int stop)
    {
        int c = w.Peds!.StopQCount[stop];
        ushort v = (ushort)(c > ushort.MaxValue ? ushort.MaxValue : c);
        w.Peds.StopWaiting[stop] = v;
        w.Net.BusStops[stop].Waiting = v;
    }

    /// <summary>
    /// Раз в 20 тиков: ожидающие на остановках, чьё ожидание вошло в окно <c>[stuck_stop_s, stuck_stop_s + 1 с)</c>, засчитываются в <c>PedStuckEvents</c> (раз на человека).
    /// </summary>
    public static void CheckStuck(World w)
    {
        PedWorld pw = w.Peds!;
        if (w.Tick % 20 != 0)
        {
            return;
        }

        long thr = pw.Cfg.StuckStopTicks;
        PedStore ps = pw.P;
        for (int s = 0; s < w.Net.BusStopCount; s++)
        {
            for (int p = pw.StopQHead[s]; p >= 0; p = ps.Next[p])
            {
                long wait = w.Tick - ps.T0[p];
                if (wait >= thr && wait < thr + 20)
                {
                    w.Counters.PedStuckEvents++;
                }
            }
        }
    }

    /// <summary>После сброса агентов (<c>BuildRoad</c>): очереди и списки пассажиров пусты - обнулить производные счётчики автобусов и остановок.</summary>
    public static void ResetDerived(World w)
    {
        for (int s = 0; s < w.Net.BusStopCount; s++)
        {
            SyncWaiting(w, s);
        }

        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if ((v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
            {
                v.Passengers[i] = 0;
            }
        }
    }

    /// <summary>После <c>Load</c>: пересчитать <c>StopWaiting</c> и <c>BusStopData.Waiting</c> из очередей.</summary>
    public static void RestoreDerived(World w)
    {
        for (int s = 0; s < w.Net.BusStopCount; s++)
        {
            SyncWaiting(w, s);
        }
    }
}
