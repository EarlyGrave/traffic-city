using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Rules;

/// <summary>
/// Фаза 5 (core-api §7): поведение. Read-only по позициям/скоростям/спискам; пишет только
/// <c>Accel[]</c> и <c>LaneChangeDir[]</c> (намерение смены полосы, исполняет фаза 7).
/// IDM считается каждый тик; MOBIL - раз в <c>MobilEveryTicks</c> на машину, разнесено по индексу: <c>(tick + i) % N == 0</c>.
/// </summary>
internal static class BehaviourPhase
{
    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        long tick)
    {
        var e = new RuleEnv(net, v, occ, routes);
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            if (v.Lane[i] < 0)
            {
                v.Accel[i] = 0f;
                v.LaneChangeDir[i] = 0;
                continue;
            }

            ref readonly VehicleTypeDesc t = ref types[v.Type[i]];
            LeaderInfo li = LeaderSearch.FindFor(e, v.S, i);
            float vi = v.Speed[i];
            float a = Idm.Accel(t, vi, v.DesiredSpeed[i], li.Gap, vi - li.Speed);
            v.Accel[i] = a;

            int dir = 0;
            if ((tick + i) % p.MobilEveryTicks == 0)
            {
                dir = Mobil.Decide(e, types, p, i, a);
            }

            v.LaneChangeDir[i] = (sbyte)dir;
        }
    }
}
