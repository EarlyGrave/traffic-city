using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Rules.Intersections;

/// <summary>Код отказа ворот (диагностика, intersections.md §10; хранится в <see cref="GateScratch.DenyReason"/>, не в хэше).</summary>
internal enum GateDeny : byte
{
    None = 0,
    Signal = 1,
    Stop = 2,
    Exit = 3,
    Box = 4,
    Yield = 5,
    Tie = 6,
}

/// <summary>
/// Константы и чистые функции правил перекрёстков (intersections.md §5, §7), общие для фаз 4, 5, 6, 7.
/// Ничего не аллоцируют; арифметика - скалярный float и <see cref="MathF.Sqrt"/>.
/// </summary>
internal static class GateRules
{
    public const byte Granted = 1;
    public const byte StoppedAtLine = 2;

    public const float StopLineOffset = 0.5f;      // м: виртуальное препятствие перед концом полосы (§7.1)
    public const float ClearMargin = 1.0f;         // м: точка освобождена, когда задний бампер ушёл за неё на столько (§5.3)
    public const float ConflictMargin = 1.0f;      // м: остановка перед конфликтной точкой (§7.2)
    public const float BoxClearMargin = 0.5f;      // с (§5.4 п. 4)
    public const float CreepSpeed = 0.3f;          // м/с: v_creep (§5.3)
    public const float NoLaneChangeZone = 30f;     // м до конца полосы (§7.1)
    public const float ConflictLookahead = 60f;    // м (§7.2)
    public const float MinDecisionDistance = 30f;  // м (§5.1)
    public const float StopLineTolerance = 1.5f;   // м: StoppedAtLine при Length - S <= MinGap + 1.5 (§5.4 п. 2)
    public const float InversionGapTime = 1.0f;    // с: зазор при инверсии приоритета (§5.4 п. 4c)
    public const float DefaultYieldGapTime = 3f;   // с
    public const int DefaultMaxYieldWaitTicks = 400;
    public const int StarvationDivisor = 2;       // правило A (§5.4 п. 4b-bis): голова, ждущая >= MaxYieldWaitTicks / 2 (10 с), старшая по (WaitTicks, -Index), отказывает младшим через свою точку

    public static bool IsGranted(VehicleStore v, int i) => (v.GateState[i] & Granted) != 0;

    /// <summary>Узел с воротами (<c>Control != None</c>) в конце Normal-полосы или -1 (connector, тупик, узел без правил).</summary>
    public static int GateNode(NetworkStore net, int lane)
    {
        ref readonly LaneData ld = ref net.Lanes[lane];
        if (ld.Kind != LaneKind.Normal || ld.NextCount <= 0)
        {
            return -1;
        }

        int node = net.Lanes[net.LaneNext[ld.FirstNext]].Node;
        return node >= 0 && net.Nodes[node].Control != NodeControl.None ? node : -1;
    }

    /// <summary>true: connector принадлежит узлу с воротами.</summary>
    public static bool IsGatedConnector(NetworkStore net, in LaneData ld) =>
        ld.Kind == LaneKind.Connector && ld.Node >= 0 && net.Nodes[ld.Node].Control != NodeControl.None;

    /// <summary>
    /// Дистанция решения (§5.1, уточнение реализации): <c>max(30 м, 3 с · v, v²/(2b) + 1.5 с · v)</c>. С неё машина становится
    /// кандидатом и с неё же видит стоп-линию как препятствие (§7.1), поэтому допущенная машина не тормозит вовсе, а недопущенная
    /// успевает остановиться с комфортным замедлением b.
    /// </summary>
    public static float DecisionDistance(float v, float comfortDecel)
    {
        float b = comfortDecel > 0.1f ? comfortDecel : 0.1f;
        float d = (v * v / (2f * b)) + (1.5f * v);
        float d3 = 3f * v;
        if (d3 > d)
        {
            d = d3;
        }

        return d > MinDecisionDistance ? d : MinDecisionDistance;
    }

    /// <summary>Время проехать <paramref name="d"/> с текущей <paramref name="v"/>, разгоном <paramref name="a"/> до <paramref name="v0"/> (§5.3).</summary>
    public static float Eta(float d, float v, float a, float v0)
    {
        if (d <= 0f)
        {
            return 0f;
        }

        if (a < 0.1f)
        {
            a = 0.1f;
        }

        if (v0 < 0.1f)
        {
            v0 = 0.1f;
        }

        if (v0 <= v)
        {
            return d / v;
        }

        float dAcc = ((v0 * v0) - (v * v)) / (2f * a);
        if (d <= dAcc)
        {
            return (MathF.Sqrt((v * v) + (2f * a * d)) - v) / a;
        }

        return ((v0 - v) / a) + ((d - dAcc) / v0);
    }

    /// <summary>Сравнение билетов одного узла с учётом переполнения uint: true, если <paramref name="a"/> выдан раньше <paramref name="b"/>.</summary>
    public static bool TicketLess(uint a, uint b) => (int)(a - b) < 0;

    /// <summary>Позиция машины вдоль полосы <paramref name="lane"/>, где она состоит (основная или ghost): следующая к хвосту.</summary>
    public static int BehindOn(VehicleStore v, int x, int lane) => v.Lane[x] == lane ? v.Behind[x] : v.GhostBehind[x];

    /// <summary>
    /// Зазор (м) от текущего положения машины <paramref name="i"/> до ближайшего виртуального препятствия ворот (§7.1, §7.2) либо
    /// <see cref="Idm.FreeGap"/>; <paramref name="speed"/> - скорость препятствия (0 для стоп-линии и конфликтной точки, скорость партнёра
    /// при следовании за ним в горловине слияния). <paramref name="s"/> - массив позиций (S либо SOld для клэмпа I6). Для недопущенной
    /// машины на Normal-полосе к узлу с воротами - стоп-линия (с <paramref name="visibilityLimited"/> - только в пределах дистанции
    /// решения); для допущенной и для машины на connector'е - конфликтные точки, занятые/ожидаемые машинами с меньшим билетом.
    /// </summary>
    public static float ObstacleGap(NetworkStore net, VehicleStore v, LaneOccupancy occ, ConnectorStore cs, float[] s, int i, float comfortDecel, bool visibilityLimited, out float speed)
    {
        speed = 0f;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref net.Lanes[lane];
        float si = s[i];
        if (ld.Kind == LaneKind.Connector)
        {
            if (!IsGatedConnector(net, in ld))
            {
                return Idm.FreeGap;
            }

            return ConflictGap(net, v, occ, cs, s, i, lane, si, out speed);
        }

        if (GateNode(net, lane) < 0)
        {
            return Idm.FreeGap;
        }

        float d = ld.Length - si;
        if (!IsGranted(v, i))
        {
            if (visibilityLimited && d > DecisionDistance(v.Speed[i], comfortDecel))
            {
                return Idm.FreeGap;
            }

            return d - StopLineOffset;
        }

        int conn = v.GrantConnector[i];
        if (conn < 0 || net.Lanes[conn].FromLane != lane)
        {
            return Idm.FreeGap;
        }

        return ConflictGap(net, v, occ, cs, s, i, conn, si - ld.Length, out speed);
    }

    /// <summary>
    /// Ближайшая конфликтная точка connector'а <paramref name="conn"/> впереди позиции <paramref name="sOnConn"/> (отрицательная - машина ещё
    /// на входной полосе), которую не освободила машина с меньшим билетом (§7.2). Cross: стоящее препятствие в <c>SMine - ConflictMargin</c>.
    /// Merge: партнёр с меньшим билетом - лидер в координатах общего пути (следуем за ним со его скоростью), но не ближе точки слияния
    /// минус его длина, пока он её не прошёл (zipper без остановки и без «лидера позади»). Записи отсортированы по SMine - первая
    /// блокирующая Cross-точка ближайшая; Merge-точка лежит в конце connector'а.
    /// </summary>
    public static float ConflictGap(NetworkStore net, VehicleStore v, LaneOccupancy occ, ConnectorStore cs, float[] s, int i, int conn, float sOnConn, out float speed)
    {
        speed = 0f;
        int g = net.Lanes[conn].ConflictGroup;
        if (g < 0 || cs.ConflictCount[g] == 0)
        {
            return Idm.FreeGap;
        }

        int firstConn = net.Nodes[cs.Node[g]].FirstConnector;
        uint myTicket = v.Ticket[i];
        int end = cs.FirstConflict[g] + cs.ConflictCount[g];
        float best = Idm.FreeGap;
        for (int e = cs.FirstConflict[g]; e < end; e++)
        {
            ref readonly ConflictEntry ce = ref cs.Conflicts[e];
            float sMine = ce.SMineCm * 0.01f;
            if (sMine < sOnConn)
            {
                continue;
            }

            if (sMine - sOnConn > ConflictLookahead || sMine - sOnConn >= best)
            {
                break;
            }

            int other = firstConn + ce.OtherLocal;
            if (ce.Kind == ConflictKind.Merge)
            {
                float mg = MergeGap(net, v, occ, s, i, myTicket, other, sMine, sOnConn, out float ms);
                if (mg < best)
                {
                    best = mg;
                    speed = ms;
                }
            }
            else if (CrossBlocked(v, occ, s, i, myTicket, other, ce.SOtherCm * 0.01f, net.Lanes[other].FromLane))
            {
                return sMine - ConflictMargin - sOnConn;
            }
        }

        return best;
    }

    /// <summary>true: на connector'е <paramref name="other"/> или среди допущенных на его входной полосе есть машина с меньшим билетом, не освободившая точку.</summary>
    private static bool CrossBlocked(VehicleStore v, LaneOccupancy occ, float[] s, int i, uint myTicket, int other, float sOther, int inLane)
    {
        for (int x = occ.LaneTail[other]; x != -1; x = v.Ahead[x])
        {
            if (x != i && TicketLess(v.Ticket[x], myTicket) && s[x] - v.Length[x] < sOther + ClearMargin)
            {
                return true;
            }
        }

        for (int x = occ.LaneHead[inLane]; x != -1 && IsGranted(v, x); x = BehindOn(v, x, inLane))
        {
            if (x != i && v.GrantConnector[x] == other && TicketLess(v.Ticket[x], myTicket))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Зазор до самого ограничивающего партнёра слияния с меньшим билетом (см. <see cref="ConflictGap"/>) или FreeGap.</summary>
    private static float MergeGap(NetworkStore net, VehicleStore v, LaneOccupancy occ, float[] s, int i, uint myTicket, int other, float sMine, float sOnConn, out float speed)
    {
        speed = 0f;
        float best = Idm.FreeGap;
        float lenOther = net.Lanes[other].Length;
        for (int x = occ.LaneTail[other]; x != -1; x = v.Ahead[x])
        {
            if (x != i && TicketLess(v.Ticket[x], myTicket))
            {
                Consider(v, x, sMine - (lenOther - s[x]), sMine, sOnConn, ref best, ref speed);
            }
        }

        int inLane = net.Lanes[other].FromLane;
        float lenIn = net.Lanes[inLane].Length;
        for (int x = occ.LaneHead[inLane]; x != -1 && IsGranted(v, x); x = BehindOn(v, x, inLane))
        {
            if (x != i && v.GrantConnector[x] == other && TicketLess(v.Ticket[x], myTicket))
            {
                Consider(v, x, sMine - lenOther - (lenIn - s[x]), sMine, sOnConn, ref best, ref speed);
            }
        }

        return best;
    }

    // Партнёр x с передним бампером в pX (в координатах моего connector'а): следование за ним (не ближе ClearMargin за его задним
    // бампером), но не ближе точки слияния минус его длина, пока он позади неё.
    private static void Consider(VehicleStore v, int x, float pX, float sMine, float sOnConn, ref float best, ref float speed)
    {
        float lx = v.Length[x];
        float follow = pX - lx - ClearMargin - sOnConn;
        float wait = sMine - ConflictMargin - lx - sOnConn;
        bool following = follow >= wait;
        float gap = following ? follow : wait;
        if (gap < best)
        {
            best = gap;
            speed = following ? v.Speed[x] : 0f;
        }
    }
}
