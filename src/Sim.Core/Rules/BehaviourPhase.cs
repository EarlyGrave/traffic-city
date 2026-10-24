using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;

namespace Sim.Core.Rules;

/// <summary>
/// Фаза 5 (core-api §7): поведение. Read-only по позициям/скоростям/спискам; пишет только
/// <c>Accel[]</c>, <c>LaneChangeDir[]</c> (намерение смены полосы, исполняет фаза 7) и зазоры клэмпа I6 для фазы 6
/// (<c>ClampLeadGap</c>, <c>ClampFlags</c>, <c>ClampGateGap</c>, ADR-0016 D2).
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
            bool atStop = (v.Flags[i] & (byte)VehicleFlags.AtStop) != 0;
            if (!atStop)
            {
                vehicles++;
            }

            // Зазоры клэмпа I6 для фазы 6 (ADR-0016, D2): по позициям на начало тика, для каждой машины фазы 6, включая автобус на остановке.
            LeaderInfo li = LeaderSearch.FindFor(e, v.S, i);
            hops += li.Hops;
            v.ClampLeadGap[i] = li.Gap;
            v.ClampFlags[i] = (byte)((li.Vehicle != -1 || li.Virtual ? IntegratePhase.ClampLeaderPresent : 0) | (li.Virtual ? IntegratePhase.ClampLeaderVirtual : 0));
            float vi = v.Speed[i];
            float gateGap = Idm.FreeGap;
            float gateSpeed = 0f;
            bool beyondDecision = false;
            if (connectors != null)
            {
                obstacleCalls++;
                gateGap = GateRules.ObstacleGap(net, v, occ, connectors, ref work, v.S, i, t.ComfortDecel, out beyondDecision, out gateSpeed);
            }

            v.ClampGateGap[i] = gateGap;
            if (atStop)
            {
                v.Accel[i] = 0f; // автобус стоит на остановке (посадка)
                v.LaneChangeDir[i] = 0;
                continue;
            }

            float a = Idm.Accel(t, vi, v.DesiredSpeed[i], li.Gap, vi - li.Speed);
            if (gateGap < Idm.FreeGap && !beyondDecision)
            {
                float ag = Idm.Accel(t, vi, v.DesiredSpeed[i], gateGap, vi - gateSpeed);
                if (ag < a)
                {
                    a = ag;
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
