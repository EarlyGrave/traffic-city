using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
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
        Run(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w), w.Types, PhysicsParams.From(w), w.NodeThroughput, ref w.Counters, w.Masks);
        AssignConnectors(w);
    }

    /// <summary>
    /// Фаза 7. Без <paramref name="masks"/> - проход по всем слотам (тесты, микробенчмарки); с масками (ADR-0019, A3) - по маске
    /// <see cref="TickMasks.Trans"/> фазы 6 по возрастанию индекса: остальным машинам цикл ничего бы не сделал (нет смены полосы,
    /// нет намерения, <c>S &lt; Length</c>), а чужие машины эта фаза для условия машины не меняет. Debug-сверка маски с полным проходом.
    /// </summary>
    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        Span<uint> nodeThroughput,
        ref SimCounters c,
        TickMasks? masks = null)
    {
        var e = new RuleEnv(net, v, occ, routes);
        int hw = v.HighWater;
        if (masks == null)
        {
            for (int i = 0; i < hw; i++)
            {
                Vehicle(e, types, p, nodeThroughput, i, ref c, null);
            }

            return;
        }

#if DEBUG
        DebugCheckMask(net, v, hw, masks);
#endif
        ulong[] trans = masks.Trans;
        int words = TickMasks.WordsFor(hw);
        for (int k = 0; k < words; k++)
        {
            ulong m = trans[k];
            while (m != 0UL)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(m);
                m &= m - 1;
                Vehicle(e, types, p, nodeThroughput, (k << 6) + b, ref c, masks);
            }
        }
    }

#if DEBUG
    // Эталон маски фазы 7: предикат прежнего прохода по всем слотам.
    private static void DebugCheckMask(NetworkStore net, VehicleStore v, int hw, TickMasks masks)
    {
        for (int i = 0; i < hw; i++)
        {
            bool expected = v.Pool.IsAlive(i) && v.Lane[i] >= 0
                && (v.GhostLane[i] != -1 || v.LaneChangeDir[i] != 0 || v.S[i] >= net.Lanes[v.Lane[i]].Length);
            System.Diagnostics.Debug.Assert(TickMasks.Test(masks.Trans, i) == expected, "Trans mask differs from full scan");
        }
    }
#endif

    private static void Vehicle(in RuleEnv e, ReadOnlySpan<VehicleTypeDesc> types, in PhysicsParams p, Span<uint> nodeThroughput, int i, ref SimCounters c, TickMasks? masks)
    {
        VehicleStore v = e.V;
        if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
        {
            return;
        }

        float speed0 = v.Speed[i];
        bool meso0 = v.Lod.Length != 0 && (v.Lod[i] & Lod.LodBits.Meso) != 0;
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
                    if (v.Lod.Length != 0 && (v.Lod[i] & Lod.LodBits.Meso) != 0)
                    {
                        v.Lod[i] |= Lod.LodBits.Fresh; // новый лидер/ведомый: поведение пересчитывается в ближайшей фазе 5 (sim-lod.md §2.5)
                        c.MesoLaneChanges++;
                    }
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

        if (masks != null)
        {
            // Полоса могла смениться (переход, смена полосы): бит прибытия - по состоянию после фазы 7.
            TickMasks.Set(masks.Arrive, i, v.DestLane[i] == v.Lane[i] && v.S[i] >= v.DestS[i] && (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0);
            // Переходный клэмп меняет Speed, въезд в ячейку другого уровня - бит Meso: поправка накопителей метрик (A4).
            masks.Retune(v, i, speed0, meso0);
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
        v.Lateral[i] = LateralOffset(e, i, ticks, dur);
    }

    // Смещение относительно оси целевой полосы: машина стартует на оси исходной полосы (справа/слева от целевой).
    // Расстояние между осями соседних полос - полусумма их ширин (ADR-0030); у полос одной ширины это она сама.
    private static float LateralOffset(in RuleEnv e, int i, int ticks, int dur)
    {
        VehicleStore v = e.V;
        ref readonly LaneData target = ref e.Net.Lanes[v.GhostLane[i]];
        float sign = target.Left == v.Lane[i] ? 1f : -1f;
        float width = (target.Width + e.Net.Lanes[v.Lane[i]].Width) * 0.5f;
        return -sign * width * (1f - ((float)ticks / dur));
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
        if (tl.Kind != LaneKind.Normal || (v.Access(i) & ~tl.Deny) == 0)
        {
            return false;   // маска запрета полосы (vehicle-classes.md §2.2): автобусная и т. п.
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
        v.Lateral[i] = LateralOffset(e, i, 0, dur);
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
        if (ld.Kind == LaneKind.Normal && GateRules.GateNodeOfNormal(e.Net, lane) >= 0)
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
        Lod.QueueMeso? q = e.Occ.Queue;
        if (tail != -1 && tail != i)
        {
            // Q-meso: хвост-машина очереди стоит на позиции въезда - берём оценку хвоста «сейчас» (O(1)).
            float tailS = q != null ? q.TailS(next) : v.S[tail];
            float sMax = tailS - v.Length[tail] - p.MinPhysicalGap;
            if (sNew > sMax)
            {
                if (sMax < 0f)
                {
                    return false;
                }

                sNew = sMax;
                float tailSpeed = q != null ? q.TailSpeed(next) : v.Speed[tail];
                if (tailSpeed < v.Speed[i])
                {
                    v.Speed[i] = tailSpeed;
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
            v.GateState[i] &= unchecked((byte)~(GateRules.StoppedAtLine | GateRules.StoppedBeforeSignal)); // въезд на connector: Granted и Ticket сохраняются
        }

        e.Occ.Unlink(i);
        v.S[i] = sNew;
        e.Occ.PushTail(next, i);
        v.NextConn[i] = -1; // новый connector выберет AssignConnectors
        RouteNav.Advance(e, i, lane, next); // курсор маршрута / отметка промаха полосы (routing.md 6.3)

        v.DesiredSpeed[i] = Idm.DesiredSpeed(types[v.Type[i]], e.Net.Lanes[next].SpeedLimit);
        v.PointHint[i] = 0;
        if (q != null && e.Net.Lanes[next].Kind == LaneKind.Normal)
        {
            EnterLaneChoice(e, types, p, i, next, ref c);
        }

        return true;
    }

    /// <summary>
    /// Q-meso (sim-lod.md §13.5 п. 3): смены полосы на полосе нет, выбор - при въезде. Если маршрут хочет параллельную полосу того же направленного сегмента
    /// и в ней есть место (<c>Σ(L + s0) + L + s0 &lt;= Length - 25 м</c>, за оценкой хвоста), машина ставится в её хвост; иначе остаётся.
    /// </summary>
    private static void EnterLaneChoice(in RuleEnv e, ReadOnlySpan<VehicleTypeDesc> types, in PhysicsParams p, int i, int lane, ref SimCounters c)
    {
        VehicleStore v = e.V;
        Lod.QueueMeso q = e.Occ.Queue!;
        int desired = e.RouteDesiredLane(i);
        if (desired < 0 || desired == lane || (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
        {
            return;
        }

        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        ref readonly LaneData dl = ref e.Net.Lanes[desired];
        if (dl.Kind != LaneKind.Normal || dl.Segment != ld.Segment || dl.Direction != ld.Direction || (v.Access(i) & ~dl.Deny) == 0)
        {
            return;
        }

        ref readonly VehicleTypeDesc t = ref types[v.Type[i]];
        float span = v.Length[i] + t.MinGap;
        if ((q.LaneSpanCm[desired] * 0.01f) + span > dl.Length - Lod.QueueMeso.EntryChoiceReserveM)
        {
            return;
        }

        float s = v.S[i];
        int tail = e.Occ.LaneTail[desired];
        if (tail != -1)
        {
            float sMax = q.TailS(desired) - v.Length[tail] - p.MinPhysicalGap;
            if (sMax < 0f)
            {
                return;
            }

            if (s > sMax)
            {
                s = sMax;
            }
        }

        e.Occ.Unlink(i);
        v.S[i] = s;
        e.Occ.PushTail(desired, i);
        v.DesiredSpeed[i] = Idm.DesiredSpeed(t, dl.SpeedLimit);
        c.MesoLaneChanges++;
    }

    /// <summary>
    /// Конец фазы 7: обработка промахов полосы (маршрут освобождается, машина встаёт в очередь перемаршрутизации) и выбор connector'а
    /// ближайшей развилки для машин, сменивших полосу (<see cref="VehicleStore.NextConn"/>). Выбор - <see cref="ChooseConnector"/>.
    /// </summary>
    public static void AssignConnectors(World w) => RouteNav.AssignConnectors(w);

    /// <summary>
    /// Выбор connector'а для машины <paramref name="i"/> на полосе <paramref name="lane"/> по курсору маршрута (routing.md 6.2);
    /// без маршрута или при промахе - connector по умолчанию. -1 - выбирать не из чего.
    /// </summary>
    public static int ChooseConnector(World w, int i, int lane) => RouteNav.NextConnector(w, i, lane);
}
