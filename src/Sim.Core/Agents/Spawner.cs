using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Util;

namespace Sim.Core.Agents;

/// <summary>
/// Рабочие данные спавнера, выделенные при создании мира (после этого аллокаций нет). Заполняются лениво на первом тике
/// (<see cref="Spawner.Run"/>): сеть к этому моменту уже построена.
/// </summary>
internal sealed class SpawnerData
{
    /// <summary>Полоса -> зона (-1 = нет). Полосы спавна зоны + (для внешних зон) полосы выезда того же сегмента. STAGE2-TEMP: условие прибытия.</summary>
    public readonly int[] LaneZone;

    /// <summary>Веса зон-кандидатов при выборе пары (размер MaxZones).</summary>
    public readonly float[] Scratch;

    /// <summary>Накопленные доли смеси типов (размер = число типов).</summary>
    public readonly float[] TypeCum;

    public bool Ready;
    public int InternalWithLanes;
    public int ExternalWithLanes;

    public SpawnerData(int maxZones, int maxLanes, int typeCount)
    {
        LaneZone = new int[Math.Max(0, maxLanes)];
        Scratch = new float[Math.Max(0, maxZones)];
        TypeCum = new float[Math.Max(1, typeCount)];
        Array.Fill(LaneZone, -1);
    }
}

/// <summary>
/// Фаза 2 (core-api 7): спавн. (a) генератор OD: пока <c>Alive + Pending &lt; Target(t)</c> (линейная рампа
/// <c>SpawnRampTicks</c>) и очередь не полна - новая поездка из стрима <c>RngSpawn</c>: класс пары (внутр.-внутр. либо, с долей
/// <c>ExternalShare</c>, внешняя-внутренняя в случайную сторону), источник - зона пропорционально весу, сток - гравитационная
/// модель <c>w_j / d^alpha</c> среди зон с учётом <c>MinTripLength</c> (production-constrained gravity); тип по <c>TypeMix</c>.
/// (b) из <see cref="PendingTrips"/> ставит машины на полосу спавна зоны-источника в <c>s = Length + MinGap</c>, если зазор безопасен;
/// иначе поездка не отбрасывается, а уходит в хвост очереди (нет блокировки головы), <c>SpawnDeferredTicks</c> растёт на тик,
/// когда хотя бы одна поездка не стартовала. Маршруты - шаг 3; до него (STAGE2-TEMP) машина едет без маршрута,
/// <c>DestZone</c> задаёт зону прибытия (<see cref="ArrivalPhase"/>), развилки выбирает <see cref="TransitionPhase.AssignConnectors"/>.
/// </summary>
internal static class Spawner
{
    private const int MaxGeneratePerTick = 64;
    private const int MaxExaminePerTick = 64;
    private const int MaxSpawnPerTick = 32;
    private const int MaxLaneAttempts = 6;

    public static void Run(World w)
    {
        if (!w.Spawn.Ready)
        {
            Init(w);
        }

        Generate(w);
        Dispatch(w);
    }

    private static void Init(World w)
    {
        SpawnerData sd = w.Spawn;
        NetworkStore net = w.Net;
        Array.Fill(sd.LaneZone, -1);
        sd.InternalWithLanes = 0;
        sd.ExternalWithLanes = 0;
        for (int z = 0; z < net.ZoneCount; z++)
        {
            ref readonly ZoneData zd = ref net.Zones[z];
            if (zd.SpawnLaneCount > 0)
            {
                if (zd.IsExternal != 0)
                {
                    sd.ExternalWithLanes++;
                }
                else
                {
                    sd.InternalWithLanes++;
                }
            }

            for (int k = 0; k < zd.SpawnLaneCount; k++)
            {
                int lane = net.ZoneLanes[zd.FirstSpawnLane + k];
                if ((uint)lane >= (uint)sd.LaneZone.Length)
                {
                    continue;
                }

                sd.LaneZone[lane] = z;
                if (zd.IsExternal == 0)
                {
                    continue;
                }

                // Внешняя зона: выезд "за карту" - полосы противоположного направления того же сегмента.
                ref readonly LaneData ld = ref net.Lanes[lane];
                if (ld.Segment < 0)
                {
                    continue;
                }

                ref readonly SegmentData sg = ref net.Segments[ld.Segment];
                int first = ld.Direction == 0 ? sg.FirstLaneBA : sg.FirstLaneAB;
                int count = ld.Direction == 0 ? sg.LanesBA : sg.LanesAB;
                for (int q = 0; q < count; q++)
                {
                    int opp = first + q;
                    if (sd.LaneZone[opp] == -1)
                    {
                        sd.LaneZone[opp] = z;
                    }
                }
            }
        }

        // Смесь типов: накопленные веса; пустая смесь = всё тип 0.
        float[]? mix = w.Demand.TypeMix;
        float acc = 0f;
        for (int t = 0; t < sd.TypeCum.Length; t++)
        {
            float wt = mix != null && t < mix.Length ? mix[t] : 0f;
            acc += wt;
            sd.TypeCum[t] = acc;
        }

        if (!(acc > 0f))
        {
            Array.Fill(sd.TypeCum, 1f);
        }

        sd.Ready = true;
    }

    // ---- (a) генерация поездок ----

    private static void Generate(World w)
    {
        long target = w.TargetVehicles;
        long ramp = w.Demand.SpawnRampTicks;
        if (ramp > 0 && w.Tick < ramp)
        {
            target = target * w.Tick / ramp;
        }

        if (target > w.Vehicles.Capacity)
        {
            target = w.Vehicles.Capacity;
        }

        for (int n = 0; n < MaxGeneratePerTick; n++)
        {
            if (w.Vehicles.AliveCount + w.Trips.Count >= target || w.Trips.IsFull)
            {
                return;
            }

            if (!MakeTrip(w, out PendingTrip trip))
            {
                return;
            }

            w.Trips.TryEnqueue(in trip);
        }
    }

    private static bool MakeTrip(World w, out PendingTrip trip)
    {
        trip = default;
        SpawnerData sd = w.Spawn;
        NetworkStore net = w.Net;
        DemandDesc d = w.Demand;
        int from;
        int to;

        OdPairDesc[]? pairs = d.OdPairs;
        if (pairs != null && pairs.Length > 0)
        {
            float total = 0f;
            for (int k = 0; k < pairs.Length; k++)
            {
                total += pairs[k].Weight;
            }

            if (!(total > 0f))
            {
                return false;
            }

            float x = w.RngSpawn.NextFloat01() * total;
            int pick = pairs.Length - 1;
            float acc = 0f;
            for (int k = 0; k < pairs.Length; k++)
            {
                acc += pairs[k].Weight;
                if (acc > x)
                {
                    pick = k;
                    break;
                }
            }

            from = pairs[pick].FromZone;
            to = pairs[pick].ToZone;
            if ((uint)from >= (uint)net.ZoneCount || (uint)to >= (uint)net.ZoneCount || net.Zones[from].SpawnLaneCount <= 0)
            {
                return false;
            }
        }
        else
        {
            int originClass = 0;
            int destClass = 0;
            if (sd.ExternalWithLanes > 0 && sd.InternalWithLanes > 0 && w.RngSpawn.NextFloat01() < d.ExternalShare)
            {
                if (w.RngSpawn.NextFloat01() < 0.5f)
                {
                    originClass = 1;
                }
                else
                {
                    destClass = 1;
                }
            }

            if (!TryPair(w, originClass, destClass, out from, out to) && !TryPair(w, 2, 2, out from, out to))
            {
                return false;
            }
        }

        ref readonly ZoneData zf = ref net.Zones[from];
        int lane = net.ZoneLanes[zf.FirstSpawnLane + (int)w.RngSpawn.NextBelow((uint)zf.SpawnLaneCount)];
        trip = new PendingTrip
        {
            FromZone = from,
            ToZone = to,
            FromLane = lane,
            FromS = 0f,
            Type = PickType(w),
        };
        return true;
    }

    private static byte PickType(World w)
    {
        float[] cum = w.Spawn.TypeCum;
        float x = w.RngSpawn.NextFloat01() * cum[cum.Length - 1];
        for (int t = 0; t < cum.Length; t++)
        {
            if (cum[t] > x)
            {
                return (byte)t;
            }
        }

        return 0;
    }

    // Класс зоны: 0 - внутренняя, 1 - внешняя, 2 - любая.
    private static bool ClassMatches(in ZoneData z, int cls) => cls == 2 || (cls == 1) == (z.IsExternal != 0);

    private static bool TryPair(World w, int originClass, int destClass, out int from, out int to)
    {
        NetworkStore net = w.Net;
        DemandDesc d = w.Demand;
        float[] wt = w.Spawn.Scratch;
        int zc = net.ZoneCount;
        from = -1;
        to = -1;

        float total = 0f;
        for (int z = 0; z < zc; z++)
        {
            ref readonly ZoneData zd = ref net.Zones[z];
            float v = zd.SpawnLaneCount > 0 && ClassMatches(in zd, originClass) ? zd.Weight : 0f;
            wt[z] = v;
            total += v;
        }

        if (!(total > 0f))
        {
            return false;
        }

        from = Pick(wt, zc, w.RngSpawn.NextFloat01() * total);

        for (int pass = 0; pass < 2; pass++)
        {
            float minTrip = pass == 0 ? d.MinTripLength : 0f;
            total = 0f;
            Vec2 c0 = net.Zones[from].Center;
            for (int z = 0; z < zc; z++)
            {
                ref readonly ZoneData zd = ref net.Zones[z];
                float v = 0f;
                if (z != from && zd.SpawnLaneCount > 0 && ClassMatches(in zd, destClass))
                {
                    float dx = zd.Center.X - c0.X;
                    float dz = zd.Center.Z - c0.Z;
                    float dist = MathF.Sqrt((dx * dx) + (dz * dz));
                    if (dist >= minTrip)
                    {
                        v = d.Model == DemandModel.Uniform ? 1f : zd.Weight * Attraction(dist, d.GravityExponent);
                    }
                }

                wt[z] = v;
                total += v;
            }

            if (total > 0f)
            {
                to = Pick(wt, zc, w.RngSpawn.NextFloat01() * total);
                return true;
            }
        }

        return false;
    }

    // 1 / d^alpha; частные случаи alpha = 0, 1, 2 - без libm (портируемость).
    private static float Attraction(float dist, float alpha)
    {
        float d = dist < 1f ? 1f : dist;
        if (alpha == 1f)
        {
            return 1f / d;
        }

        if (alpha == 0f)
        {
            return 1f;
        }

        if (alpha == 2f)
        {
            return 1f / (d * d);
        }

        return 1f / MathF.Pow(d, alpha);
    }

    // Индекс первого накопленного веса > x; нулевые веса не выбираются.
    private static int Pick(float[] weights, int count, float x)
    {
        float acc = 0f;
        int last = 0;
        for (int z = 0; z < count; z++)
        {
            if (weights[z] <= 0f)
            {
                continue;
            }

            last = z;
            acc += weights[z];
            if (acc > x)
            {
                return z;
            }
        }

        return last;
    }

    // ---- (b) выпуск машин ----

    private static void Dispatch(World w)
    {
        int n = w.Trips.Count < MaxExaminePerTick ? w.Trips.Count : MaxExaminePerTick;
        int spawned = 0;
        bool deferred = false;
        for (int k = 0; k < n && spawned < MaxSpawnPerTick; k++)
        {
            if (!w.Trips.TryDequeue(out PendingTrip trip))
            {
                break;
            }

            if (TrySpawn(w, in trip))
            {
                spawned++;
            }
            else
            {
                deferred = true;
                w.Trips.TryEnqueue(in trip); // в хвост: поездка не теряется и не блокирует остальные
            }
        }

        if (deferred)
        {
            w.Counters.SpawnDeferredTicks++;
        }
    }

    /// <summary>true = поездка обработана (машина создана либо поездка некорректна и отброшена); false = ждёт.</summary>
    private static bool TrySpawn(World w, in PendingTrip trip)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        if (v.AliveCount >= v.Capacity)
        {
            return false;
        }

        ref readonly VehicleTypeDesc t = ref w.Types[trip.Type];
        float s = t.Length + t.MinGap;
        if (trip.FromZone >= 0)
        {
            ref readonly ZoneData z = ref net.Zones[trip.FromZone];
            int cnt = z.SpawnLaneCount;
            if (cnt <= 0)
            {
                return false; // зона без полос спавна (явная сеть): поездка ждёт, но не теряется
            }

            int start = 0;
            if (trip.FromLane >= 0)
            {
                for (int q = 0; q < cnt; q++)
                {
                    if (net.ZoneLanes[z.FirstSpawnLane + q] == trip.FromLane)
                    {
                        start = q;
                        break;
                    }
                }
            }
            else
            {
                start = (int)((w.Tick + trip.ToZone) % cnt);
            }

            int attempts = cnt < MaxLaneAttempts ? cnt : MaxLaneAttempts;
            for (int a = 0; a < attempts; a++)
            {
                int lane = net.ZoneLanes[z.FirstSpawnLane + ((start + a) % cnt)];
                if (TryPlace(w, in trip, in t, lane, s, z.IsExternal != 0))
                {
                    return true;
                }
            }

            return false;
        }

        if ((uint)trip.FromLane >= (uint)net.LaneCount)
        {
            w.Counters.TripsDropped++;
            return true;
        }

        float sCmd = trip.FromS > s ? trip.FromS : s;
        float len = net.Lanes[trip.FromLane].Length;
        return TryPlace(w, in trip, in t, trip.FromLane, sCmd < len ? sCmd : len, false);
    }

    private static bool TryPlace(World w, in PendingTrip trip, in VehicleTypeDesc t, int lane, float s, bool moving)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        ref readonly LaneData ld = ref net.Lanes[lane];
        if (ld.Kind != LaneKind.Normal || (ld.Flags & 1) != 0 || s > ld.Length)
        {
            return false;
        }

        w.Occupancy.FindNeighbours(lane, s, out int ahead, out int behind);
        float gapAhead = Idm.FreeGap;
        if (ahead != -1)
        {
            gapAhead = v.S[ahead] - v.Length[ahead] - s;
            if (gapAhead < t.MinGap)
            {
                return false;
            }
        }

        if (behind != -1 && s - t.Length - v.S[behind] < t.MinGap)
        {
            return false;
        }

        if (!v.TrySpawn(out VehicleHandle h))
        {
            return false;
        }

        int i = h.Index;
        float desired = Idm.DesiredSpeed(t, ld.SpeedLimit);
        float speed = 0f;
        if (moving)
        {
            speed = desired;
            if (ahead != -1)
            {
                float vSafe = MathF.Sqrt(2f * t.ComfortDecel * (gapAhead - t.MinGap));
                speed = MathF.Min(speed, MathF.Min(v.Speed[ahead], vSafe));
            }
        }

        v.S[i] = s;
        v.Speed[i] = speed;
        v.Length[i] = t.Length;
        v.Type[i] = trip.Type;
        v.DesiredSpeed[i] = desired;
        v.SpawnTick[i] = w.Tick;
        v.DestZone[i] = trip.ToZone;
        w.Occupancy.InsertBetween(lane, i, ahead, behind);
        v.NextConn[i] = TransitionPhase.ChooseConnector(w, i, lane);
        w.Counters.VehiclesSpawned++;
        w.Emit(SimEventKind.VehicleSpawned, 0, h.Index, h.Generation);
        return true;
    }
}
