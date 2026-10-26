using System;
using Sim.Core.Api;

namespace Sim.Core.Peds;

/// <summary>
/// Фаза 2d (pedestrians.md §3, §8): запуск поездок. Цель - <c>TargetPeds · min(1, Tick / spawn_ramp)</c>; пока активных меньше цели и очередь готовых
/// жителей не пуста (не больше 64 за тик), спавнер берёт голову очереди, выделяет пешехода в точке выхода (<c>Home</c> при <c>Where = 0</c>, иначе <c>Work</c>),
/// ставит <c>PendingPath</c>, скорость <c>U(speed_min, speed_max)</c> из PRNG <c>Peds</c>, цель ноги - противоположная точка, и запрос пути в <c>PedPathQueue</c>.
/// P2: все ноги пешие (<c>Leg = Direct</c>); выбор автобусной ноги и запасной режим - P4.
/// </summary>
internal static class PedSpawn
{
    public const int PerTickLimit = 64;

    public static void Run(World w)
    {
        PedWorld pw = w.Peds!;
        ResidentStore rs = pw.R;
        if (rs.ReadyHead < 0)
        {
            return;
        }

        PedStore ps = pw.P;
        long ramp = pw.Cfg.SpawnRampTicks;
        long target = ramp > 0 ? (long)w.TargetPeds * Math.Min(w.Tick, ramp) / ramp : w.TargetPeds;
        int budget = PerTickLimit;
        while (budget-- > 0 && ps.AliveCount < target && rs.ReadyHead >= 0)
        {
            if (!ps.TryAllocate(out int p))
            {
                w.Counters.PedAllocFailed++;
                break;
            }

            int r = rs.DequeueReady();
            bool toWork = rs.Where[r] == ResidentStore.AtHome;
            rs.Where[r] = toWork ? ResidentStore.ToWork : ResidentStore.ToHome;
            rs.Ped[r] = p;
            ps.Resident[p] = r;
            ps.State[p] = (byte)PedState.PendingPath;
            ps.Leg[p] = (byte)PedLeg.Direct;
            ps.Edge[p] = toWork ? rs.HomeEdge[r] : rs.WorkEdge[r];
            ps.S0Mm[p] = toWork ? rs.HomeSMm[r] : rs.WorkSMm[r];
            ps.GoalEdge[p] = toWork ? rs.WorkEdge[r] : rs.HomeEdge[r];
            ps.GoalSMm[p] = toWork ? rs.WorkSMm[r] : rs.HomeSMm[r];
            ps.T0[p] = w.Tick;
            ps.TripTick[p] = w.Tick;
            ps.VMmT[p] = pw.Cfg.SpeedMinMmPerTick + (int)w.RngPeds.NextBelow((uint)(pw.Cfg.SpeedMaxMmPerTick - pw.Cfg.SpeedMinMmPerTick + 1));
            w.Counters.PedSpawned++;
            pw.EnqueuePath(p);
        }
    }
}
