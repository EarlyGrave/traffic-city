using System;
using Sim.Core.Api;

namespace Sim.Core.Agents;

/// <summary>
/// Фаза 8 (core-api §7): прибытие. <c>Lane == DestLane &amp;&amp; S &gt;= DestS</c> -> событие <see cref="SimEventKind.TripCompleted"/>
/// (A = индекс слота, B = поколение), счётчики <c>TripsCompleted/TripTimeTicksTotal</c>, <see cref="LaneOccupancy.UnlinkAll"/> и
/// <see cref="VehicleStore.Despawn"/>. Это единственный путь деспауна: машина без прибытия никогда не исчезает.
/// <c>DestS</c> должно быть не больше <c>Length - MinGap</c> полосы-цели: у конца полосы без продолжения машина останавливается
/// раньше. Событийный буфер принадлежит вызывающему: пишем, пока есть место, последний слот резервируем под
/// <see cref="SimEventKind.EventsOverflow"/>; потерянные события - в <c>EventsDropped</c>.
/// </summary>
internal static class ArrivalPhase
{
    public static void Run(World w)
    {
        // Буфер событий мира: пишем через общий счётчик; переполнение помечает Emit мира, поэтому здесь отдельный путь не нужен.
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int dest = v.DestLane[i];
            if (dest < 0 || v.Lane[i] != dest || v.S[i] < v.DestS[i])
            {
                continue;
            }

            VehicleHandle h = v.HandleOf(i);
            w.Counters.TripsCompleted++;
            w.Counters.TripTimeTicksTotal += w.Tick - v.SpawnTick[i];
            w.Emit(SimEventKind.TripCompleted, 0, h.Index, h.Generation);
            w.Occupancy.UnlinkAll(i);
            v.Despawn(h);
        }
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
            if (dest < 0 || v.Lane[i] != dest || v.S[i] < v.DestS[i])
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
