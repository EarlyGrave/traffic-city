using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;

namespace Sim.Core.Rules;

/// <summary>
/// Фаза 5 (core-api §7): поведение. Read-only по позициям/скоростям/спискам; пишет только
/// <c>Accel[]</c> и <c>LaneChangeDir[]</c> (намерение смены полосы, исполняет фаза 7).
/// IDM считается каждый тик; MOBIL - раз в <c>MobilEveryTicks</c> на машину, разнесено по индексу: <c>(tick + i) % N == 0</c>.
/// Перекрёстки (intersections.md §7, при переданном <see cref="ConnectorStore"/>): IDM берёт минимум ускорения по обычному
/// лидеру и по виртуальному препятствию ворот - стоп-линии без допуска (в пределах дистанции решения) либо конфликтной точке,
/// занятой машиной с меньшим билетом (<see cref="GateRules.ObstacleGap"/>).
/// </summary>
internal static class BehaviourPhase
{
    public static void Run(World w) =>
        Run(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w), w.Types, PhysicsParams.From(w), w.Tick, ref w.Work, w.Connectors);

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        long tick,
        ConnectorStore? connectors = null)
    {
        SimWork scratch = default;
        Run(net, v, occ, routes, types, p, tick, ref scratch, connectors);
    }

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        long tick,
        ref SimWork work,
        ConnectorStore? connectors)
    {
        var e = new RuleEnv(net, v, occ, routes);
        int hw = v.HighWater;
        int vehicles = 0;
        int hops = 0;
        int obstacleCalls = 0;
        long conflictCallsBefore = work.ConflictGapCallsRaw;
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
            if ((v.Flags[i] & (byte)VehicleFlags.AtStop) != 0)
            {
                v.Accel[i] = 0f; // автобус стоит на остановке (посадка)
                v.LaneChangeDir[i] = 0;
                continue;
            }

            vehicles++;
            LeaderInfo li = LeaderSearch.FindFor(e, v.S, i);
            hops += li.Hops;
            float vi = v.Speed[i];
            float a = Idm.Accel(t, vi, v.DesiredSpeed[i], li.Gap, vi - li.Speed);
            if (connectors != null)
            {
                obstacleCalls++;
                float gateGap = GateRules.ObstacleGap(net, v, occ, connectors, ref work, v.S, i, t.ComfortDecel, visibilityLimited: true, out float gateSpeed);
                if (gateGap < Idm.FreeGap)
                {
                    float ag = Idm.Accel(t, vi, v.DesiredSpeed[i], gateGap, vi - gateSpeed);
                    if (ag < a)
                    {
                        a = ag;
                    }
                }
            }

            if ((v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
            {
                float stopGap = BusStopRules.ObstacleGap(net, v, i, t.MinGap);
                if (stopGap < Idm.FreeGap)
                {
                    float ab = Idm.Accel(t, vi, v.DesiredSpeed[i], stopGap, vi);
                    if (ab < a)
                    {
                        a = ab;
                    }
                }
            }

            v.Accel[i] = a;

            int dir = 0;
            if ((tick + i) % p.MobilEveryTicks == 0)
            {
                dir = Mobil.Decide(e, types, p, i, a);
            }

            v.LaneChangeDir[i] = (sbyte)dir;
        }

        work.BehaviourVehicles += vehicles;
        work.LeaderLookaheadHops += hops;
        work.ObstacleGapCalls += obstacleCalls;
        work.ConflictGapCallsBehaviour += work.ConflictGapCallsRaw - conflictCallsBefore;
    }
}
