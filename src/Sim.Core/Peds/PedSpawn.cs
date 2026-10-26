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
            TryBusLeg(w, pw, p, r, toWork);
            ps.T0[p] = w.Tick;
            ps.TripTick[p] = w.Tick;
            ps.VMmT[p] = pw.Cfg.SpeedMinMmPerTick + (int)w.RngPeds.NextBelow((uint)(pw.Cfg.SpeedMaxMmPerTick - pw.Cfg.SpeedMinMmPerTick + 1));
            w.Counters.PedSpawned++;
            pw.EnqueuePath(p);
        }
    }

    /// <summary>
    /// Автобусная нога (§3): если у жителя на это направление выбран автобус, нога идёт к точке доступа остановки посадки (<c>ToStop</c>). Запасной режим: очередь остановки не меньше вместимости
    /// автобуса, а путь пешком (по прямой между точками) не больше <c>max_walk_m</c> - нога остаётся пешей (<c>PedModeFallback++</c>). Нет точки доступа - пешком без счёта.
    /// </summary>
    private static void TryBusLeg(World w, PedWorld pw, int p, int r, bool toWork)
    {
        ResidentStore rs = pw.R;
        if ((toWork ? rs.ModeOut[r] : rs.ModeBack[r]) == 0)
        {
            return;
        }

        int stop = toWork ? rs.StopAOut[r] : rs.StopABack[r];
        if (!Stops.AccessPoint(w, stop, out int edge, out int sMm))
        {
            return;
        }

        PedStore ps = pw.P;
        if (pw.StopQCount[stop] >= Transit.BusStopPhase.BusCapacity)
        {
            int hint = 0;
            pw.Net.Evaluate(rs.HomeEdge[r], rs.HomeSMm[r], false, ref hint, out float hx, out float hz, out _);
            hint = 0;
            pw.Net.Evaluate(rs.WorkEdge[r], rs.WorkSMm[r], false, ref hint, out float wx, out float wz, out _);
            float dx = wx - hx;
            float dz = wz - hz;
            if (((dx * dx) + (dz * dz)) <= (pw.Cfg.MaxWalkMm * 0.001f * (pw.Cfg.MaxWalkMm * 0.001f)))
            {
                w.Counters.PedModeFallback++;
                return;
            }
        }

        ps.Leg[p] = (byte)PedLeg.ToStop;
        ps.GoalEdge[p] = edge;
        ps.GoalSMm[p] = sMm;
    }
}
