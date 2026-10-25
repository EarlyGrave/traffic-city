using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Metrics;

/// <summary>
/// Фаза 9 (core-api 7, 11): метрики. Путь (<c>DistanceTotalMm</c> += round(v*dt*1000)), <c>VehicleTicksTotal/MovingVehicleTicksTotal</c>,
/// число «застрявших» (<c>StillTicks &gt;= StuckThresholdTicks</c>) берутся из целых накопителей, которые собрала фаза 6 (<see cref="TickMasks"/>,
/// ADR-0019, A4) и поправили фазы 7, 8, 8b (переходный клэмп скорости, смена уровня, деспаун) - сумма целых не зависит от порядка, поэтому
/// результат побитово равен прежнему проходу по всем машинам. События <c>VehicleStuck/VehicleUnstuck</c> и флаг <see cref="VehicleFlags.Stuck"/> -
/// по маске <see cref="TickMasks.StuckEv"/> (признак расходится с флагом) по возрастанию индекса, как прежде. Затем gridlock: условие
/// <c>Stuck &gt;= max(GridlockMinStuck, GridlockStuckShare * Active)</c>; флаг включается, когда условие держится
/// <c>GridlockHoldTicks</c> подряд, и выключается, когда оно ложно столько же подряд (<c>World.GridlockStreak</c>).
/// Нагрузка сегментов (<c>SegmentLoads</c>: число машин, плотность на полосо-км, средняя скорость / лимит) считается проходом по машинам только
/// в тик, после которого ей нужен читатель (пересчёт весов маршрутов следующим тиком, <see cref="NeedsLoadsNow"/>), и по требованию
/// (<see cref="World.EnsureSegmentLoads"/>: команды, API) - по состоянию конца тика, то есть теми же значениями, что считал каждый тик.
/// <c>NodeThroughput</c> ведёт фаза 7 (при выходе с connector'а). Машина с <c>Route == -1</c> (промах полосы, ждёт перемаршрутизации)
/// едет по умолчанию, а не стоит, поэтому исключения из stuck для неё нет: стоящая машина считается stuck при любом маршруте.
/// </summary>
internal static class MetricsPhase
{
    private const float Dt = 0.05f;
    internal const float MmPerSecondTick = 1000f * Dt; // мм за тик на 1 м/с

    public static void Run(World w)
    {
        VehicleStore v = w.Vehicles;
        TickMasks m = w.Masks;
        ref SimCounters c = ref w.Counters;
        int stuckTicks = w.Rules.StuckThresholdTicks;
        int hw = v.HighWater;
#if DEBUG
        DebugVerify(w);
#endif
        ulong[] ev = m.StuckEv;
        int words = TickMasks.WordsFor(hw);
        for (int k = 0; k < words; k++)
        {
            ulong bits = ev[k];
            while (bits != 0UL)
            {
                int i = (k << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (!v.Pool.IsAlive(i))
                {
                    continue;   // прибыла/исчезла после фазы 6: в прежнем проходе её уже не было
                }

                bool isStuck = v.StillTicks[i] >= stuckTicks;
                bool wasStuck = (v.Flags[i] & (byte)VehicleFlags.Stuck) != 0;
                if (isStuck == wasStuck)
                {
                    continue;
                }

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
        }

        w.Queue?.StuckPass();   // Q-meso: stuck машин очереди - по полосе (sim-lod.md §13.9)

        int active = (int)m.Active;
        int stuck = (int)m.Stuck;
        long distMm = m.DistMm;
        int mesoNow = (int)m.MesoNow;
        long mesoDistMm = m.MesoDistMm;
        c.ActiveVehicles = active;
        c.StuckVehicles = stuck;
        c.VehicleTicksTotal += active;
        c.MovingVehicleTicksTotal += m.Moving;
        c.DistanceTotalMm += distMm;
        c.StuckTicksTotal += stuck;
        if (v.Lod.Length != 0)
        {
            c.MesoVehicles = mesoNow;
            c.MicroVehicles = active - mesoNow;
            c.MesoVehicleTicks += mesoNow;
            c.MicroVehicleTicks += active - mesoNow;
            c.MesoDistanceMm += mesoDistMm;
            c.MicroDistanceMm += distMm - mesoDistMm;
        }

        if (NeedsLoadsNow(w))
        {
            ComputeSegmentLoads(w);
            w.SegmentLoadsTick = w.Tick;
        }

        UpdateGridlock(w, active, stuck);
        QualityMetrics.Sample(w);
    }

    /// <summary>
    /// Нужны ли нагрузки сегментов после этого тика: веса маршрутов пересчитает фаза 3 следующего тика (тик кратен <c>CostRecalcTicks</c>
    /// либо пересчёт отложен до конца волны кастомизации, <c>RecalcPending</c>) - она читает значения конца этого тика.
    /// </summary>
    public static bool NeedsLoadsNow(World w) => w.RecalcPending || (w.Tick + 1) % w.Routing.CostRecalcTicks == 0;

    /// <summary>Нагрузка сегментов по текущему состоянию машин (прежняя часть прохода фазы 9): число, средняя скорость к лимиту, плотность.</summary>
    public static void ComputeSegmentLoads(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        SegmentLoad[] loads = w.SegmentLoads;
        int segCount = net.SegmentCount;
        for (int s = 0; s < segCount; s++)
        {
            loads[s].Count = 0;
            loads[s].SpeedRatio = 0f; // накопитель суммы скоростей (м/с) до финализации
            loads[s].Density = 0f;
        }

        int hw = v.HighWater;
        Lod.QueueMeso? q = w.Queue;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int lane = v.Lane[i];
            if ((uint)lane < (uint)net.LaneCount)
            {
                int seg = net.Lanes[lane].Segment;
                if ((uint)seg < (uint)segCount)
                {
                    loads[seg].Count++;
                    loads[seg].SpeedRatio += q != null && q.IsQ(i) ? q.SpeedFor(i) : v.Speed[i];   // Q-meso: S/Speed очереди - на момент въезда
                }
            }
        }

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
    }

#if DEBUG
    // Эталон: прежний проход фазы 9 по всем машинам; накопители и состав событий stuck обязаны совпасть побитово.
    private static void DebugVerify(World w)
    {
        VehicleStore v = w.Vehicles;
        TickMasks m = w.Masks;
        float stuckSpeed = w.Rules.StuckSpeedThreshold;
        int stuckTicks = w.Rules.StuckThresholdTicks;
        long active = 0;
        long moving = 0;
        long dist = 0;
        long mesoNow = 0;
        long mesoDist = 0;
        long stuck = 0;
        int hw = v.HighWater;
        if (w.Queue != null)
        {
            // Q-meso: накопители - сумма прохода фазы 6 по кинематическим и агрегатов очереди (QueueMeso.AddTallies); машины, вставшие в очередь
            // в фазе 7, учтены фазой 6 - полный проход по состоянию конца тика с ними не сопоставим. Сверка - тесты QueueMesoTests (метрики, I6).
            return;
        }

        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            active++;
            float speed = v.Speed[i];
            long step = (long)((speed * MmPerSecondTick) + 0.5f);
            dist += step;
            if (v.Lod.Length != 0 && (v.Lod[i] & Lod.LodBits.Meso) != 0)
            {
                mesoNow++;
                mesoDist += step;
            }

            if (speed > stuckSpeed)
            {
                moving++;
            }

            bool isStuck = v.StillTicks[i] >= stuckTicks;
            if (isStuck)
            {
                stuck++;
            }

            if (isStuck != ((v.Flags[i] & (byte)VehicleFlags.Stuck) != 0))
            {
                System.Diagnostics.Debug.Assert(TickMasks.Test(m.StuckEv, i), "StuckEv mask misses a transition");
            }
        }

        System.Diagnostics.Debug.Assert(active == m.Active, "metrics: Active differs from full scan");
        System.Diagnostics.Debug.Assert(moving == m.Moving, "metrics: Moving differs from full scan");
        System.Diagnostics.Debug.Assert(dist == m.DistMm, "metrics: DistMm differs from full scan");
        System.Diagnostics.Debug.Assert(mesoNow == m.MesoNow, "metrics: MesoNow differs from full scan");
        System.Diagnostics.Debug.Assert(mesoDist == m.MesoDistMm, "metrics: MesoDistMm differs from full scan");
        System.Diagnostics.Debug.Assert(stuck == m.Stuck, "metrics: Stuck differs from full scan");
    }
#endif

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
