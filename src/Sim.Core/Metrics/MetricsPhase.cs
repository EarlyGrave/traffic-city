using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Metrics;

/// <summary>
/// Фаза 9 (core-api 7, 11): метрики. По возрастанию индекса машины: путь (<c>DistanceTotalMm</c> += round(v*dt*1000)),
/// <c>VehicleTicksTotal/MovingVehicleTicksTotal</c>, stuck (<c>StillTicks &gt;= StuckThresholdTicks</c> -> флаг
/// <see cref="VehicleFlags.Stuck"/>, события <c>VehicleStuck/VehicleUnstuck</c>), нагрузка сегментов (<c>SegmentLoads</c>:
/// число машин, плотность на полосо-км, средняя скорость / лимит). Затем gridlock: условие
/// <c>Stuck &gt;= max(GridlockMinStuck, GridlockStuckShare * Active)</c>; флаг включается, когда условие держится
/// <c>GridlockHoldTicks</c> подряд, и выключается, когда оно ложно столько же подряд (<c>World.GridlockStreak</c>).
/// Все накопители целочисленные; float - только при расчёте порога и отношения скорости (порядок суммирования фиксирован).
/// <c>NodeThroughput</c> ведёт фаза 7 (при выходе с connector'а). STAGE2-TEMP: исключение «Route == -1 не stuck» (core-api 11)
/// отключено, пока маршрутов нет - иначе stuck не считался бы вовсе.
/// </summary>
internal static class MetricsPhase
{
    private const float Dt = 0.05f;
    private const float MmPerSecondTick = 1000f * Dt; // мм за тик на 1 м/с

    public static void Run(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        ref SimCounters c = ref w.Counters;
        float stuckSpeed = w.Rules.StuckSpeedThreshold;
        int stuckTicks = w.Rules.StuckThresholdTicks;
        SegmentLoad[] loads = w.SegmentLoads;
        int segCount = net.SegmentCount;
        for (int s = 0; s < segCount; s++)
        {
            loads[s].Count = 0;
            loads[s].SpeedRatio = 0f; // накопитель суммы скоростей (м/с) до финализации
            loads[s].Density = 0f;
        }

        int active = 0;
        int moving = 0;
        int stuck = 0;
        long distMm = 0;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            active++;
            float speed = v.Speed[i];
            distMm += (long)((speed * MmPerSecondTick) + 0.5f);
            if (speed > stuckSpeed)
            {
                moving++;
            }

            bool isStuck = v.StillTicks[i] >= stuckTicks;
            bool wasStuck = (v.Flags[i] & (byte)VehicleFlags.Stuck) != 0;
            if (isStuck != wasStuck)
            {
                VehicleHandle h = v.HandleOf(i);
                if (isStuck)
                {
                    v.Flags[i] |= (byte)VehicleFlags.Stuck;
                    c.StuckEventsTotal++;
                    w.Emit(SimEventKind.VehicleStuck, 0, h.Index, h.Generation);
                }
                else
                {
                    v.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.Stuck);
                    w.Emit(SimEventKind.VehicleUnstuck, 0, h.Index, h.Generation);
                }
            }

            if (isStuck)
            {
                stuck++;
            }

            int lane = v.Lane[i];
            if ((uint)lane < (uint)net.LaneCount)
            {
                int seg = net.Lanes[lane].Segment;
                if ((uint)seg < (uint)segCount)
                {
                    loads[seg].Count++;
                    loads[seg].SpeedRatio += speed;
                }
            }
        }

        c.ActiveVehicles = active;
        c.StuckVehicles = stuck;
        c.VehicleTicksTotal += active;
        c.MovingVehicleTicksTotal += moving;
        c.DistanceTotalMm += distMm;
        c.StuckTicksTotal += stuck;

        for (int s = 0; s < segCount; s++)
        {
            ref SegmentLoad l = ref loads[s];
            if (l.Count == 0)
            {
                l.SpeedRatio = 1f;
                continue;
            }

            ref readonly SegmentData sg = ref net.Segments[s];
            float limit = sg.SpeedLimit > 0.1f ? sg.SpeedLimit : 0.1f;
            l.SpeedRatio = l.SpeedRatio / (l.Count * limit);
            float laneKm = (sg.LanesAB + sg.LanesBA) * sg.Length * 0.001f;
            l.Density = laneKm > 0f ? l.Count / laneKm : 0f;
        }

        UpdateGridlock(w, active, stuck);
    }

    private static void UpdateGridlock(World w, int active, int stuck)
    {
        ref SimCounters c = ref w.Counters;
        RulesDesc r = w.Rules;
        bool condition = stuck >= r.GridlockMinStuck && stuck >= r.GridlockStuckShare * active;
        bool flag = c.GridlockFlag != 0;
        if (condition == flag)
        {
            w.GridlockStreak = 0;
        }
        else if (++w.GridlockStreak >= r.GridlockHoldTicks)
        {
            w.GridlockStreak = 0;
            flag = condition;
            c.GridlockFlag = flag ? 1 : 0;
            w.Emit(flag ? SimEventKind.GridlockOn : SimEventKind.GridlockOff, 0, stuck, active);
        }

        if (flag)
        {
            c.GridlockTicksTotal++;
        }
    }
}
