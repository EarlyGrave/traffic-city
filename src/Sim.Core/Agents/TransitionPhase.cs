using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;

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
/// Перекрёстки (intersections.md §7.3): с Normal-полосы к узлу с воротами машина переходит только с допуском и только на
/// <c>GrantConnector</c>; при въезде на connector допуск и билет сохраняются; при выходе с connector'а <c>Granted</c> снимается
/// (<c>OutReserved</c> пересобирает фаза 4). Переходный клэмп: при входе на полосу с хвостом <c>T</c> позиция ограничивается
/// <c>S[T] - Length[T] - MinPhysicalGap</c> (<c>HardClamps++</c>); если и это место занято - машина ждёт в конце полосы.
/// </summary>
internal static class TransitionPhase
{
    private const int MaxHopsPerTick = 4;

    public static void Run(World w)
    {
        Run(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w), w.Types, PhysicsParams.From(w), w.NodeThroughput, ref w.Counters);
        AssignConnectors(w); // STAGE2-TEMP
    }

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
                if (!TryAdvanceLane(e, types, p, nodeThroughput, i, ref c))
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

        if (GateRules.IsGranted(v, i))
        {
            return false; // intersections.md §7.1: после допуска полоса не меняется
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
        v.GateState[i] = 0;
        v.LaneChangeTicks[i] = 0;
        v.DesiredSpeed[i] = Idm.DesiredSpeed(t, tl.SpeedLimit);
        v.Lateral[i] = LateralOffset(e, p, i, 0, dur);
        return true;
    }

    private static bool TryAdvanceLane(in RuleEnv e, ReadOnlySpan<VehicleTypeDesc> types, in PhysicsParams p, Span<uint> nodeThroughput, int i, ref SimCounters c)
    {
        VehicleStore v = e.V;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        if (v.S[i] < ld.Length)
        {
            return false;
        }

        int next = e.NextLane(i, lane, 1, useRoute: true);
        if (ld.Kind == LaneKind.Normal && GateRules.GateNode(e.Net, lane) >= 0)
        {
            // Узел с воротами: только с допуском и только на connector допуска (intersections.md §5, §7.3).
            if (!GateRules.IsGranted(v, i))
            {
                return false;
            }

            int gc = v.GrantConnector[i];
            if (gc < 0 || e.Net.Lanes[gc].FromLane != lane)
            {
                v.GateState[i] = 0;
                v.GrantConnector[i] = -1;
                return false;
            }

            next = gc;
        }

        if (next < 0)
        {
            return false;
        }

        // Переходный клэмп (intersections.md §7.3): хвост полосы-приёмника близко к её началу - позиция ограничивается, чтобы не создать
        // перекрытие в списке (инвариант зазора >= MinPhysicalGap); если места нет вовсе - ждём в конце полосы.
        float sNew = v.S[i] - ld.Length;
        int tail = e.Occ.LaneTail[next];
        if (tail != -1 && tail != i)
        {
            float sMax = v.S[tail] - v.Length[tail] - p.MinPhysicalGap;
            if (sNew > sMax)
            {
                if (sMax < 0f)
                {
                    return false;
                }

                sNew = sMax;
                if (v.Speed[tail] < v.Speed[i])
                {
                    v.Speed[i] = v.Speed[tail];
                }

                c.HardClamps++;
            }
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
            v.GateState[i] = 0;        // выход из узла: допуск снят, билет остаётся (intersections.md §7.3)
            v.GrantConnector[i] = -1;
        }
        else
        {
            v.GateState[i] &= unchecked((byte)~GateRules.StoppedAtLine); // въезд на connector: Granted и Ticket сохраняются
        }

        e.Occ.Unlink(i);
        v.S[i] = sNew;
        e.Occ.PushTail(next, i);
        v.NextConn[i] = -1; // STAGE2-TEMP: новый connector выберет AssignConnectors
        v.RouteCursor[i]++; // STAGE2-TEMP: без маршрута - число переходов между полосами (ArrivalPhase: не прибывать на полосе спавна)

        v.DesiredSpeed[i] = Idm.DesiredSpeed(types[v.Type[i]], e.Net.Lanes[next].SpeedLimit);
        v.PointHint[i] = 0;
        return true;
    }

    /// <summary>
    /// STAGE2-TEMP (замена маршрутов до шага 3, core-api 7): машина без маршрута на обычной полосе заранее выбирает connector
    /// развилки в конце полосы - равновероятно среди connector'ов <c>LaneNext</c> из стрима <c>RngBehaviour</c>, предпочитая
    /// (по убыванию) не-разворот и не-тупик (полоса-цель имеет продолжение либо принадлежит зоне назначения), затем не-разворот,
    /// затем любой. Выбор хранится в <see cref="VehicleStore.NextConn"/>, чтобы поиск лидера (фаза 5/6) видел ту же полосу,
    /// куда машина реально поедет. Перевыбор - при въезде на новую полосу и при смене полосы (старый connector не от этой полосы).
    /// Обход по возрастанию индекса, поэтому результат детерминирован.
    /// </summary>
    public static void AssignConnectors(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            int lane = v.Lane[i];
            if (lane < 0 || !v.Pool.IsAlive(i) || v.Route[i] >= 0)
            {
                continue;
            }

            int cur = v.NextConn[i];
            if (cur >= 0 && net.Lanes[cur].FromLane == lane)
            {
                continue;
            }

            v.NextConn[i] = ChooseConnector(w, i, lane);
        }
    }

    /// <summary>Выбор connector'а для машины <paramref name="i"/> на полосе <paramref name="lane"/> (-1 - выбирать не из чего).</summary>
    public static int ChooseConnector(World w, int i, int lane)
    {
        NetworkStore net = w.Net;
        ref readonly LaneData ld = ref net.Lanes[lane];
        if (ld.Kind != LaneKind.Normal || ld.NextCount <= 0)
        {
            return -1;
        }

        int destZone = w.Vehicles.DestZone[i];
        int[] laneZone = w.Spawn.LaneZone;
        int best = 0;
        int count0 = 0;
        int count1 = 0;
        int count2 = ld.NextCount;
        for (int k = 0; k < ld.NextCount; k++)
        {
            int tier = ConnectorTier(net, laneZone, destZone, ld.Segment, net.LaneNext[ld.FirstNext + k]);
            if (tier == 0)
            {
                count0++;
            }

            if (tier <= 1)
            {
                count1++;
            }
        }

        int pool;
        if (count0 > 0)
        {
            best = 0;
            pool = count0;
        }
        else if (count1 > 0)
        {
            best = 1;
            pool = count1;
        }
        else
        {
            best = 2;
            pool = count2;
        }

        int pick = (int)w.RngBehaviour.NextBelow((uint)pool);
        for (int k = 0; k < ld.NextCount; k++)
        {
            int c = net.LaneNext[ld.FirstNext + k];
            if (ConnectorTier(net, laneZone, destZone, ld.Segment, c) <= best)
            {
                if (pick == 0)
                {
                    return c;
                }

                pick--;
            }
        }

        return net.LaneNext[ld.FirstNext];
    }

    // 0 - не разворот и не тупик, 1 - не разворот, 2 - разворот.
    private static int ConnectorTier(NetworkStore net, int[] laneZone, int destZone, int fromSegment, int connector)
    {
        int to = net.Lanes[connector].ToLane;
        if (to < 0)
        {
            return 2;
        }

        ref readonly LaneData t = ref net.Lanes[to];
        if (t.Segment == fromSegment)
        {
            return 2;
        }

        bool deadEnd = t.NextCount <= 0 && !(destZone >= 0 && laneZone[to] == destZone);
        return deadEnd ? 1 : 0;
    }
}
