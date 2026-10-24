using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;

namespace Sim.Core.Agents;

/// <summary>
/// Фаза 6 (core-api §7): интеграция. <c>v' = clamp(v + a*dt, 0, MaxSpeed)</c>, <c>s' = s + v'*dt</c>, затем жёсткий клэмп I6 (§7.6):
/// <c>s' &lt;= S_old[лидер] - L_лидер - MinPhysicalGap</c> по позициям на начало тика (копия в <see cref="VehicleStore.SOld"/>),
/// поэтому результат не зависит от порядка обхода. Лидер - сосед по списку основной полосы и (во время смены полосы)
/// по списку ghost-полосы, иначе хвост следующей полосы; у полосы без продолжения - конец полосы. Пишет S, Speed, StillTicks.
/// Анимацию <c>Lateral</c> ведёт фаза 7 (она владеет счётчиком манёвра).
/// Перекрёстки (intersections.md §7, при переданном <see cref="ConnectorStore"/>): тот же жёсткий клэмп по виртуальным препятствиям
/// ворот - стоп-линия без допуска (машина не пересекает её без допуска ни при каких IDM-параметрах) и конфликтная точка, занятая
/// машиной с меньшим билетом (I6 на пересекающихся connector'ах); позиции чужих машин берутся на начало тика (<c>SOld</c>), поэтому
/// результат не зависит от порядка обхода.
/// </summary>
internal static class IntegratePhase
{
    public static void Run(World w) =>
        Run(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w), w.Types, PhysicsParams.From(w), ref w.Counters, ref w.Work, w.Connectors);

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        ref SimCounters c,
        ConnectorStore? connectors = null)
    {
        SimWork scratch = default;
        Run(net, v, occ, routes, types, p, ref c, ref scratch, connectors);
    }

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        ref SimCounters c,
        ref SimWork work,
        ConnectorStore? connectors)
    {
        int hops = 0;
        int obstacleCalls = 0;
        long conflictCallsBefore = work.ConflictGapCallsRaw;
        int hw = v.HighWater;
        Array.Copy(v.S, v.SOld, hw);
        var e = new RuleEnv(net, v, occ, routes);
        float dt = p.Dt;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            ref readonly VehicleTypeDesc t = ref types[v.Type[i]];
            float s = v.SOld[i];
            float vNew = v.Speed[i] + (v.Accel[i] * dt);
            if (vNew < 0f)
            {
                vNew = 0f;
            }
            else if (vNew > t.MaxSpeed)
            {
                vNew = t.MaxSpeed;
            }

            float sNew = s + (vNew * dt);
            LeaderInfo li = LeaderSearch.FindFor(e, v.SOld, i);
            hops += li.Hops;
            if (li.Vehicle != -1 || li.Virtual)
            {
                float sMax = s + li.Gap - (li.Virtual ? 0f : p.MinPhysicalGap);
                if (sNew > sMax)
                {
                    sNew = sMax > s ? sMax : s;
                    vNew = (sNew - s) / dt;
                    c.HardClamps++;
                }
            }

            if (connectors != null)
            {
                obstacleCalls++;
                float gateGap = GateRules.ObstacleGap(net, v, occ, connectors, ref work, v.SOld, i, t.ComfortDecel, visibilityLimited: false, out _);
                if (gateGap < Idm.FreeGap)
                {
                    float sMax = s + gateGap;
                    if (sNew > sMax)
                    {
                        sNew = sMax > s ? sMax : s;
                        vNew = (sNew - s) / dt;
                        c.HardClamps++;
                    }
                }
            }

            v.S[i] = sNew;
            v.Speed[i] = vNew;
            if (vNew < p.StuckSpeedThreshold && !BusStopRules.IsServing(net, v, i))
            {
                if (v.StillTicks[i] < ushort.MaxValue)
                {
                    v.StillTicks[i]++;
                }
            }
            else
            {
                v.StillTicks[i] = 0;
            }
        }

        work.LeaderLookaheadHops += hops;
        work.ObstacleGapCalls += obstacleCalls;
        work.ConflictGapCallsIntegrate += work.ConflictGapCallsRaw - conflictCallsBefore;
    }
}
