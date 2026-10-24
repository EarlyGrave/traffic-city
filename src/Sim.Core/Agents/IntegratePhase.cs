using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;

namespace Sim.Core.Agents;

/// <summary>
/// Фаза 6 (core-api §7): интеграция. <c>v' = clamp(v + a*dt, 0, MaxSpeed)</c>, <c>s' = s + v'*dt</c>, затем жёсткий клэмп I6 (§7.6):
/// <c>s' &lt;= S_old[лидер] - L_лидер - MinPhysicalGap</c> по позициям на начало тика (копия в <see cref="VehicleStore.SOld"/>),
/// поэтому результат не зависит от порядка обхода. Лидер - сосед по списку основной полосы и (во время смены полосы)
/// по списку ghost-полосы, иначе хвост следующей полосы; у полосы без продолжения - конец полосы. Пишет S, Speed, StillTicks.
/// Анимацию <c>Lateral</c> ведёт фаза 7 (она владеет счётчиком манёвра).
/// </summary>
internal static class IntegratePhase
{
    public static void Run(World w) =>
        Run(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w), w.Types, PhysicsParams.From(w), ref w.Counters);

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        ref SimCounters c)
    {
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

            v.S[i] = sNew;
            v.Speed[i] = vNew;
            if (vNew < p.StuckSpeedThreshold)
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
    }
}
