using System;
using Sim.Core.Api;

namespace Sim.Core.Agents;

/// <summary>
/// Фаза 8 (core-api §7): прибытие. <c>Lane == DestLane &amp;&amp; S &gt;= DestS</c> -> событие <see cref="SimEventKind.TripCompleted"/>
/// (A = индекс слота, B = поколение), счётчики <c>TripsCompleted/TripTimeTicksTotal</c>, <see cref="LaneOccupancy.UnlinkAll"/> и
/// <see cref="VehicleStore.Despawn"/>. Это единственный путь деспауна: машина без прибытия никогда не исчезает.
/// <c>DestS</c> должно быть не больше <c>Length - MinGap</c> полосы-цели: у конца полосы без продолжения машина останавливается
/// раньше. Прибытие - строго по <c>DestLane/DestS</c> (в том числе у машины без маршрута, оказавшейся на <c>DestLane</c>); маршрут прибывшей
/// машины освобождается. Событийный буфер принадлежит вызывающему: пишем, пока есть место, последний слот резервируем под
/// <see cref="SimEventKind.EventsOverflow"/>; потерянные события - в <c>EventsDropped</c>.
/// </summary>
internal static class ArrivalPhase
{
    public static void Run(World w)
    {
        // Буфер событий мира: пишем через общий счётчик; переполнение помечает Emit мира, поэтому здесь отдельный путь не нужен.
        // Обход по маске прибывших (ADR-0019, A3): фаза 6 кладёт кандидатов, фаза 7 пересчитывает бит у обработанных; порядок - по возрастанию индекса.
        VehicleStore v = w.Vehicles;
        ulong[] arrive = w.Masks.Arrive;
        int hw = v.HighWater;
#if DEBUG
        for (int i = 0; i < hw; i++)
        {
            System.Diagnostics.Debug.Assert(TickMasks.Test(arrive, i) == (v.Pool.IsAlive(i) && Arrived(v, i)), "Arrive mask differs from full scan");
        }
#endif
        int words = TickMasks.WordsFor(hw);
        for (int k = 0; k < words; k++)
        {
            ulong m = arrive[k];
            while (m != 0UL)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(m);
                m &= m - 1;
                int i = (k << 6) + b;
                if (!v.Pool.IsAlive(i) || !Arrived(v, i))
                {
                    continue;
                }

                VehicleHandle h = v.HandleOf(i);
                w.Counters.TripsCompleted++;
                w.Counters.TripTimeTicksTotal += w.Tick - v.SpawnTick[i];
                w.Emit(SimEventKind.TripCompleted, 0, h.Index, h.Generation);
                if (v.Resident[i] >= 0)
                {
                    City.TripLegs.OnVehicleArrived(w, i);   // машина жителя: в кольцо прибытий до деспауна (фаза 9r закроет ногу)
                }
                else if (v.Resident[i] <= -2)
                {
                    City.Utilities.OnVehicleArrived(w, i);   // машина бригады ЖКХ (Resident = -2 - crew): в кольцо CrewArrivals, разбор в 9r
                }

                w.Occupancy.UnlinkAll(i);
                if (v.Route[i] >= 0)
                {
                    w.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
                }

                w.Masks.Remove(v, i);   // метрики фазы 9 считают только оставшихся (A4)
                v.Despawn(h);
            }
        }
    }

    // Условие прибытия: полоса назначения и позиция достигнуты, не автобус (автобусы: BusStopPhase).
    private static bool Arrived(VehicleStore v, int i)
    {
        int dest = v.DestLane[i];
        return dest >= 0 && v.Lane[i] == dest && v.S[i] >= v.DestS[i] && (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0;
    }

    public static void Run(
        VehicleStore v,
        LaneOccupancy occ,
        long tick,
        Span<SimEvent> events,
        ref int eventCount,
        ref SimCounters c)
    {
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int dest = v.DestLane[i];
            if (dest < 0 || v.Lane[i] != dest || v.S[i] < v.DestS[i] || (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
            {
                continue;
            }

            VehicleHandle h = v.HandleOf(i);
            c.TripsCompleted++;
            c.TripTimeTicksTotal += tick - v.SpawnTick[i];
            Emit(events, ref eventCount, ref c, new SimEvent { Kind = SimEventKind.TripCompleted, Tick = tick, A = h.Index, B = h.Generation });
            occ.UnlinkAll(i);
            v.Despawn(h);
        }
    }

    private static void Emit(Span<SimEvent> events, ref int count, ref SimCounters c, in SimEvent ev)
    {
        if (count < events.Length - 1)
        {
            events[count++] = ev;
        }
        else
        {
            if (count == events.Length - 1)
            {
                events[count++] = new SimEvent { Kind = SimEventKind.EventsOverflow, Tick = ev.Tick };
            }

            c.EventsDropped++;
        }
    }
}
