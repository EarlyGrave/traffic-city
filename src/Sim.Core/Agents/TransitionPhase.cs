using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;

namespace Sim.Core.Agents;

/// <summary>
/// Фаза 7 (core-api §7): переходы. По возрастанию индекса машины:
/// (a) ведёт идущий манёвр смены полосы (счётчик, <c>Lateral</c>, снятие ghost по окончании), затем исполняет намерение
/// <c>LaneChangeDir</c> из фазы 5: заново проверяет зазоры по текущим позициям (иначе манёвр отменяется) и вставляет машину
/// в целевую полосу с ghost в исходной (<see cref="LaneOccupancy.BeginLaneChange"/>);
/// (b) <c>s &gt;= Length</c> - перенос на следующую полосу (по маршруту, иначе первый connector / выход connector'а):
/// <c>s -= Length</c>, <c>PushTail</c>, <c>RouteCursor++</c>, пересчёт <c>DesiredSpeed</c>, <c>PointHint = 0</c>; при выходе с connector'а
/// <c>nodeThroughput[node]++</c>. Нет продолжения - машина остаётся в конце полосы (клэмп фазы 6 держит <c>s &lt;= Length</c>).
/// Манёвр, не завершённый к концу полосы, обрывается (снимается ghost, <c>LaneChangesAborted++</c>).
/// </summary>
internal static class TransitionPhase
{
    private const int MaxHopsPerTick = 4;

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        Span<uint> nodeThroughput,
        ref SimCounters c)
    {
        var e = new RuleEnv(net, v, occ, routes);
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            ref readonly VehicleTypeDesc t = ref types[v.Type[i]];
            if (v.GhostLane[i] != -1)
            {
                AdvanceLaneChange(e, t, p, i);
            }

            int dir = v.LaneChangeDir[i];
            if (dir != 0)
            {
                v.LaneChangeDir[i] = 0;
                if (v.GhostLane[i] == -1)
                {
                    if (TryBeginLaneChange(e, t, p, i, dir))
                    {
                        c.LaneChanges++;
                    }
                    else
                    {
                        c.LaneChangesAborted++;
                    }
                }
            }

            for (int hop = 0; hop < MaxHopsPerTick; hop++)
            {
                if (!TryAdvanceLane(e, types, nodeThroughput, i, ref c))
                {
                    break;
                }
            }
        }
    }

    private static void AdvanceLaneChange(in RuleEnv e, in VehicleTypeDesc t, in PhysicsParams p, int i)
    {
        VehicleStore v = e.V;
        int dur = t.LaneChangeDurationTicks < 1 ? 1 : t.LaneChangeDurationTicks;
        int ticks = v.LaneChangeTicks[i] + 1;
        if (ticks >= dur)
        {
            e.Occ.GhostUnlink(i);
            v.LaneChangeTicks[i] = 0;
            v.Lateral[i] = 0f;
            return;
        }

        v.LaneChangeTicks[i] = (ushort)ticks;
        v.Lateral[i] = LateralOffset(e, p, i, ticks, dur);
    }

    // Смещение относительно оси целевой полосы: машина стартует на оси исходной полосы (справа/слева от целевой).
    private static float LateralOffset(in RuleEnv e, in PhysicsParams p, int i, int ticks, int dur)
    {
        VehicleStore v = e.V;
        float sign = e.Net.Lanes[v.GhostLane[i]].Left == v.Lane[i] ? 1f : -1f;
        return -sign * p.LaneWidth * (1f - ((float)ticks / dur));
    }

    private static bool TryBeginLaneChange(in RuleEnv e, in VehicleTypeDesc t, in PhysicsParams p, int i, int dir)
    {
        VehicleStore v = e.V;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        int target = dir > 0 ? ld.Left : ld.Right;
        if (target < 0 || (ld.Flags & 2) != 0 || ld.Kind != LaneKind.Normal)
        {
            return false;
        }

        ref readonly LaneData tl = ref e.Net.Lanes[target];
        if (tl.Kind != LaneKind.Normal || ((tl.Flags & 1) != 0 && (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0))
        {
            return false;
        }

        int dur = t.LaneChangeDurationTicks < 1 ? 1 : t.LaneChangeDurationTicks;
        float si = v.S[i];
        if (ld.Length - si < (v.Speed[i] * dur * p.Dt) + 0.5f || si - v.Length[i] < Mobil.LaneEntryClearance)
        {
            return false;
        }

        e.Occ.FindNeighbours(target, si, out int ahead, out int behind);
        if (LeaderSearch.Find(e, v.S, i, target, si, ahead, useRoute: false).Gap < p.MinPhysicalGap)
        {
            return false;
        }

        if (behind != -1 && si - v.Length[i] - v.S[behind] < p.MinPhysicalGap)
        {
            return false;
        }

        e.Occ.BeginLaneChange(i, target, ahead, behind);
        v.LaneChangeTicks[i] = 0;
        v.DesiredSpeed[i] = Idm.DesiredSpeed(t, tl.SpeedLimit);
        v.Lateral[i] = LateralOffset(e, p, i, 0, dur);
        return true;
    }

    private static bool TryAdvanceLane(in RuleEnv e, ReadOnlySpan<VehicleTypeDesc> types, Span<uint> nodeThroughput, int i, ref SimCounters c)
    {
        VehicleStore v = e.V;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        if (v.S[i] < ld.Length)
        {
            return false;
        }

        int next = e.NextLane(i, lane, 1, useRoute: true);
        if (next < 0)
        {
            return false;
        }

        if (v.GhostLane[i] != -1)
        {
            e.Occ.GhostUnlink(i);
            v.LaneChangeTicks[i] = 0;
            v.Lateral[i] = 0f;
            c.LaneChangesAborted++;
        }

        if (ld.Kind == LaneKind.Connector)
        {
            if ((uint)ld.Node < (uint)nodeThroughput.Length)
            {
                nodeThroughput[ld.Node]++;
            }

            c.ConnectorEntries++;
        }

        e.Occ.Unlink(i);
        v.S[i] -= ld.Length;
        e.Occ.PushTail(next, i);
        if (v.Route[i] >= 0)
        {
            v.RouteCursor[i]++;
        }

        v.DesiredSpeed[i] = Idm.DesiredSpeed(types[v.Type[i]], e.Net.Lanes[next].SpeedLimit);
        v.PointHint[i] = 0;
        return true;
    }
}
