using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Ворота перекрёстков (intersections.md §5, §8) - вторая половина фазы 4 (после <see cref="SignalPhase"/>). Единственное
/// место, выдающее допуски: <c>GateState.Granted</c>, <c>GrantConnector</c>, <c>Ticket</c>; ведёт <c>WaitTicks</c>, <c>NodeTicket</c>
/// и пересобирает <c>OutReserved</c> (Σ длин машин, допущенных или находящихся в узле с выездом на полосу).
/// Порядок: (0) OutReserved по допущенным; (1) один проход по машинам по возрастанию индекса - кандидаты в intrusive-списки узлов;
/// (2) узлы в порядке первого касания, внутри узла кандидаты по ключу (WaitTicks desc, Index asc); условия §5.4 по очереди.
/// PRNG не используется, аллокаций нет (сортировка в stackalloc).
/// Уточнения реализации: кандидатом может быть не только голова полосы, но и машина, у которой все впереди на полосе уже
/// допущены (очередь на зелёный получает билеты по порядку и трогается без задержки «по одному»); дистанция решения -
/// <see cref="GateRules.DecisionDistance"/>; на зелёном допуск только если машина успевает к стоп-линии до конца жёлтого.
/// </summary>
internal static class GatePhase
{
    private const int MaxCandidatesPerNode = 128;

    public static void Run(World w)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        GateScratch g = w.Gate;
        var e = new RuleEnv(net, v, occ, RouteAccess.From(w));
        float stuckSpeed = w.Rules.StuckSpeedThreshold;

        // 0. OutReserved: обнулить затронутые в прошлом тике полосы; заново соберём по допущенным и находящимся в узле.
        for (int k = 0; k < g.OutTouchedCount; k++)
        {
            net.OutReserved[g.OutTouched[k]] = 0f;
        }

        g.OutTouchedCount = 0;

        // 1. Сбор кандидатов.
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int lane = v.Lane[i];
            if (lane < 0)
            {
                v.WaitTicks[i] = 0;
                continue;
            }

            ref readonly LaneData ld = ref net.Lanes[lane];
            if (ld.Kind == LaneKind.Connector)
            {
                v.WaitTicks[i] = 0;
                if (GateRules.IsGranted(v, i))
                {
                    AddReserved(net, g, ld.ToLane, v.Length[i]);
                    if (v.Speed[i] < stuckSpeed)
                    {
                        w.Counters.BoxWaitTicks++;
                    }
                }

                continue;
            }

            int node = GateRules.GateNode(net, lane);
            if (node < 0)
            {
                v.WaitTicks[i] = 0;
                v.GateState[i] = 0;
                v.GrantConnector[i] = -1;
                continue;
            }

            if (GateRules.IsGranted(v, i))
            {
                v.WaitTicks[i] = 0;
                int gc = v.GrantConnector[i];
                if (gc < 0 || net.Lanes[gc].FromLane != lane)
                {
                    v.GateState[i] = 0;   // допуск от другой полосы (смена полосы после допуска): сброс
                    v.GrantConnector[i] = -1;
                    continue;
                }

                AddReserved(net, g, net.Lanes[gc].ToLane, v.Length[i]);
                continue;
            }

            ref readonly VehicleTypeDesc t = ref w.Types[v.Type[i]];
            float d = ld.Length - v.S[i];
            if (v.Speed[i] < 0.1f && d <= t.MinGap + GateRules.StopLineTolerance)
            {
                v.GateState[i] |= GateRules.StoppedAtLine;
            }

            int ahead = v.Ahead[i];
            if ((ahead != -1 && !GateRules.IsGranted(v, ahead)) || d > GateRules.DecisionDistance(v.Speed[i], t.ComfortDecel))
            {
                v.WaitTicks[i] = 0;
                g.DenyReason[i] = (byte)GateDeny.None;
                continue;
            }

            int conn = e.NextLane(i, lane, 1, useRoute: true);
            if (conn < 0 || net.Lanes[conn].Kind != LaneKind.Connector || net.Lanes[conn].FromLane != lane)
            {
                v.WaitTicks[i] = 0;   // маршрут требует другую полосу - connector'а с этой полосы нет (routing.md §6.2)
                g.DenyReason[i] = (byte)GateDeny.None;
                continue;
            }

            if (v.WaitTicks[i] < ushort.MaxValue)
            {
                v.WaitTicks[i]++;
            }

            g.CandConn[i] = conn;
            if (g.NodeCandHead[node] == -1)
            {
                g.TouchedNodes[g.TouchedCount++] = node;
            }

            g.CandNext[i] = g.NodeCandHead[node];
            g.NodeCandHead[node] = i;
        }

        // 2. Разрешение по узлам.
        Span<int> buf = stackalloc int[MaxCandidatesPerNode];
        for (int k = 0; k < g.TouchedCount; k++)
        {
            int node = g.TouchedNodes[k];
            int count = 0;
            for (int i = g.NodeCandHead[node]; i != -1; i = g.CandNext[i])
            {
                if (count == MaxCandidatesPerNode)
                {
                    break;
                }

                // Вставка по ключу (WaitTicks desc, Index asc).
                int pos = count;
                while (pos > 0 && Before(v, i, buf[pos - 1]))
                {
                    buf[pos] = buf[pos - 1];
                    pos--;
                }

                buf[pos] = i;
                count++;
            }

            for (int q = 0; q < count; q++)
            {
                int i = buf[q];
                GateDeny deny = TryGrant(w, in e, node, i, g.CandConn[i]);
                g.DenyReason[i] = (byte)deny;
                if (deny == GateDeny.Exit)
                {
                    w.Counters.GateDeniedExit++;
                }
            }

            g.NodeCandHead[node] = -1;
        }

        g.TouchedCount = 0;
    }

    /// <summary>
    /// Сброс допусков узла (intersections.md §5.7) при смене <c>Control</c> / перестройке connector'ов: машины на входных
    /// полосах теряют допуск; машины уже на connector'ах узла доезжают (билеты сохраняются). <c>OutReserved</c> пересоберёт
    /// следующий тик; <c>NodeTicket</c> не сбрасывается.
    /// </summary>
    public static void ResetNode(World w, int node)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0 || !GateRules.IsGranted(v, i))
            {
                continue;
            }

            if (net.Lanes[v.Lane[i]].Kind != LaneKind.Normal)
            {
                continue;
            }

            int gc = v.GrantConnector[i];
            if (gc >= 0 && net.Lanes[gc].Node == node)
            {
                v.GateState[i] = 0;
                v.GrantConnector[i] = -1;
            }
        }
    }

    private static bool Before(VehicleStore v, int a, int b)
    {
        ushort wa = v.WaitTicks[a];
        ushort wb = v.WaitTicks[b];
        return wa > wb || (wa == wb && a < b);
    }

    private static void AddReserved(NetworkStore net, GateScratch g, int lane, float length)
    {
        if ((uint)lane >= (uint)net.OutReserved.Length)
        {
            return;
        }

        if (net.OutReserved[lane] == 0f)
        {
            g.OutTouched[g.OutTouchedCount++] = lane;
        }

        net.OutReserved[lane] += length;
    }

    /// <summary>Условия допуска §5.4 по порядку; первое нарушенное - код отказа. <see cref="GateDeny.None"/> = допуск выдан.</summary>
    private static GateDeny TryGrant(World w, in RuleEnv e, int node, int i, int conn)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        ConnectorStore cs = w.Connectors;
        ref readonly NodeData nd = ref net.Nodes[node];
        ref readonly LaneData cl = ref net.Lanes[conn];
        ref readonly VehicleTypeDesc t = ref w.Types[v.Type[i]];
        int lane = v.Lane[i];
        float lenIn = net.Lanes[lane].Length;
        float dLine = lenIn - v.S[i];
        float vi = v.Speed[i];
        float v0 = v.DesiredSpeed[i];
        float li = v.Length[i];
        int g = cl.ConflictGroup;

        // 1. Сигнал.
        int slot = nd.Signal;
        bool fixedSignal = nd.Control == NodeControl.Signal && slot >= 0 && slot < w.Signals.Count && w.Signals.Mode[slot] == SignalMode.Fixed;
        if (fixedSignal)
        {
            byte sig = w.LaneSignals[conn];
            bool cannotStop = dLine < vi * vi / (2f * t.ComfortDecel);
            if (sig == (byte)LaneSignal.Green)
            {
                float window = (w.SignalStates[slot].TicksToChange + w.Signals.YellowTicks[slot]) * PhysicsParams.DefaultDt;
                if (!cannotStop && GateRules.Eta(dLine, vi, t.Accel, v0) > window)
                {
                    return GateDeny.Signal;
                }
            }
            else if (sig == (byte)LaneSignal.Yellow)
            {
                float window = w.SignalStates[slot].TicksToChange * PhysicsParams.DefaultDt;
                if (!cannotStop && GateRules.Eta(dLine, vi, t.Accel, v0) > window)
                {
                    return GateDeny.Signal;
                }
            }
            else
            {
                return GateDeny.Signal;
            }
        }

        // 2. STOP на второстепенном рукаве: полная остановка у линии.
        if (nd.Control == NodeControl.Stop && g >= 0 && (cs.Flags[g] & 1) != 0 && (v.GateState[i] & GateRules.StoppedAtLine) == 0)
        {
            return GateDeny.Stop;
        }

        // 3. Выезд свободен (don't-block-the-box) и место на самом connector'е.
        int outLane = cl.ToLane;
        if (!w.DebugDisableExitCheck)
        {
            float need = li + t.MinGap;
            int tail = occ.LaneTail[outLane];
            if (tail != -1 && v.S[tail] - v.Length[tail] - net.OutReserved[outLane] < need)
            {
                return GateDeny.Exit;
            }

            if (tail == -1 && net.OutReserved[outLane] > 0f && net.Lanes[outLane].Length - net.OutReserved[outLane] < need)
            {
                return GateDeny.Exit;
            }

            int ctail = occ.LaneTail[conn];
            if (ctail != -1 && v.S[ctail] - v.Length[ctail] < need)
            {
                return GateDeny.Exit;
            }
        }

        // 4. Конфликты.
        bool inversion = false;
        if (g >= 0 && cs.ConflictCount[g] > 0)
        {
            int firstConn = nd.FirstConnector;
            int end = cs.FirstConflict[g] + cs.ConflictCount[g];
            float yieldGap = w.Rules.YieldGapTime;
            int maxWait = w.Rules.MaxYieldWaitTicks;
            int wait = v.WaitTicks[i];
            for (int k = cs.FirstConflict[g]; k < end; k++)
            {
                ref readonly ConflictEntry ce = ref cs.Conflicts[k];
                int other = firstConn + ce.OtherLocal;
                float sMine = ce.SMineCm * 0.01f;
                float sOther = ce.SOtherCm * 0.01f;
                bool merge = ce.Kind == ConflictKind.Merge;
                float dMe = dLine + sMine;
                float tMe = GateRules.Eta(dMe, vi, t.Accel, v0);
                float tMeClear = GateRules.Eta(dMe + li + GateRules.ClearMargin, vi, t.Accel, v0);
                int inLane = net.Lanes[other].FromLane;

                // 4a. Внутри узла / допущенные: проезжают первыми независимо от приоритета.
                if (!merge)
                {
                    for (int x = occ.LaneTail[other]; x != -1; x = v.Ahead[x])
                    {
                        if (BlocksBox(w, x, v.S[x], sOther, tMe))
                        {
                            return GateDeny.Box;
                        }
                    }

                    float lenOther = net.Lanes[inLane].Length;
                    for (int x = occ.LaneHead[inLane]; x != -1 && GateRules.IsGranted(v, x); x = GateRules.BehindOn(v, x, inLane))
                    {
                        if (x != i && v.GrantConnector[x] == other && BlocksBox(w, x, v.S[x] - lenOther, sOther, tMe))
                        {
                            return GateDeny.Box;
                        }
                    }
                }

                // 4b. Подъезжающие: первая недопущенная на входной полосе чужого connector'а.
                ConflictRel rel = ce.Rel;
                if (rel == ConflictRel.Priority)
                {
                    continue;
                }

                int y = occ.LaneHead[inLane];
                while (y != -1 && GateRules.IsGranted(v, y))
                {
                    y = GateRules.BehindOn(v, y, inLane);
                }

                if (y == -1 || y == i)
                {
                    continue;
                }

                if (e.NextLane(y, inLane, 1, useRoute: true) != other)
                {
                    continue;
                }

                if (fixedSignal)
                {
                    byte sy = w.LaneSignals[other];
                    if (sy != (byte)LaneSignal.Green && sy != (byte)LaneSignal.Yellow)
                    {
                        continue;
                    }
                }

                float vy = v.Speed[y];
                bool stationary = vy < GateRules.CreepSpeed;
                bool senior = v.WaitTicks[i] > v.WaitTicks[y] || (v.WaitTicks[i] == v.WaitTicks[y] && i < y);
                if (stationary && senior)
                {
                    continue; // (a) тай-брейк: стоящий, ждущий меньше меня, пропускает
                }

                if (stationary && w.Gate.DenyReason[y] == (byte)GateDeny.Exit)
                {
                    continue; // стоящий перед занятым выездом (don't-block-the-box) не угроза: он не въедет, пока его выезд не освободится
                }

                ref readonly VehicleTypeDesc ty = ref w.Types[v.Type[y]];
                float tY = GateRules.Eta(sOther + net.Lanes[inLane].Length - v.S[y], vy, ty.Accel, v.DesiredSpeed[y]);
                if (rel == ConflictRel.Yield)
                {
                    if (tY >= tMeClear + yieldGap)
                    {
                        continue; // (b) gap acceptance
                    }
                }
                else if (!(tY < tMe || (tY == tMe && y < i)))
                {
                    continue; // ничья: я раньше
                }

                if (wait >= maxWait && tY >= tMeClear + GateRules.InversionGapTime)
                {
                    inversion = true; // (c)
                    continue;
                }

                if (wait >= 2 * maxWait)
                {
                    inversion = true; // (d)
                    continue;
                }

                return rel == ConflictRel.Yield ? GateDeny.Yield : GateDeny.Tie;
            }
        }

        // 5. Допуск.
        v.GateState[i] = GateRules.Granted;
        v.GrantConnector[i] = conn;
        v.Ticket[i] = net.NodeTicket[node]++;
        AddReserved(net, w.Gate, outLane, li);
        w.Counters.GateWaitTicksTotal += v.WaitTicks[i];
        v.WaitTicks[i] = 0;
        w.Counters.Grants++;
        if (inversion)
        {
            w.Counters.PriorityInversions++;
        }

        return GateDeny.None;
    }

    /// <summary>Машина <paramref name="x"/> (позиция <paramref name="sx"/> в координатах своего connector'а) ещё занимает точку и освободит её позже, чем я к ней подъеду.</summary>
    private static bool BlocksBox(World w, int x, float sx, float sOther, float tMe)
    {
        VehicleStore v = w.Vehicles;
        float lx = v.Length[x];
        if (sx - lx >= sOther + GateRules.ClearMargin)
        {
            return false;
        }

        float vx = v.Speed[x];
        if (vx < GateRules.CreepSpeed)
        {
            return true;
        }

        ref readonly VehicleTypeDesc tx = ref w.Types[v.Type[x]];
        float tClear = GateRules.Eta(sOther + lx + GateRules.ClearMargin - sx, vx, tx.Accel, v.DesiredSpeed[x]);
        return tClear + GateRules.BoxClearMargin > tMe;
    }
}
