using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Rules;

/// <summary>
/// Упрощённый MOBIL (Kesting, Treiber, Helbing 2007) - решение о смене полосы. Чистая функция по состоянию, ничего не пишет.
/// Критерий безопасности: новый ведомый на целевой полосе не тормозит сильнее <c>SafeDecel</c>; плюс геометрический
/// зазор не меньше <c>MinPhysicalGap</c> спереди и сзади (иначе нарушился бы I6).
/// Стимул: <c>(aNew - aCur) + p * [(aNewFollower - aOldFollower) + (aNewOldFollower - aOldOldFollower)] +- RightBias</c>
/// (бонус за смену вправо, штраф за смену влево) должен превысить <c>LaneChangeThreshold</c>.
/// Цели: (1) подготовка к повороту - если маршрут требует другую полосу (вход в connector с соседней полосы), смена
/// принудительная и проверяется только безопасность; (2) обгон - добровольная смена по стимулу.
/// </summary>
internal static class Mobil
{
    /// <summary>Если маршрут требует конкретную полосу, добровольные манёвры разрешены только пока до конца полосы не меньше этого, м.</summary>
    public const float MinOvertakeRemaining = 120f;

    /// <summary>
    /// Смена полосы запрещена, пока задний бампер ближе этого расстояния к началу полосы: идущий сзади на предыдущей полосе
    /// (connector'е) не входит в списки целевой полосы, и вставка перед ним нарушила бы I6.
    /// </summary>
    public const float LaneEntryClearance = 25f;

    private const float Infeasible = float.NegativeInfinity;

    /// <summary>-1 вправо, +1 влево, 0 - остаться. <paramref name="aCur"/> - IDM-ускорение машины на текущей полосе.</summary>
    public static int Decide(in RuleEnv e, ReadOnlySpan<VehicleTypeDesc> types, in PhysicsParams p, int i, float aCur)
    {
        VehicleStore v = e.V;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        if (ld.Kind != LaneKind.Normal || v.GhostLane[i] != -1)
        {
            return 0;
        }

        // intersections.md §7.1: в зоне NoLaneChangeZone до конца полосы и после выдачи допуска смена полосы запрещена -
        // порядок у стоп-линии после допуска не меняется.
        if (GateRules.IsGranted(v, i) || ld.Length - v.S[i] < GateRules.NoLaneChangeZone)
        {
            return 0;
        }

        ref readonly VehicleTypeDesc t = ref types[v.Type[i]];
        int desired = e.RouteDesiredLane(i);
        int forcedDir = 0;
        if (desired >= 0 && desired != lane)
        {
            ref readonly LaneData dl = ref e.Net.Lanes[desired];
            if (dl.Segment == ld.Segment && dl.Direction == ld.Direction)
            {
                forcedDir = dl.LaneNo > ld.LaneNo ? 1 : -1;
            }
        }

        if (forcedDir == 0 && (ld.Flags & LaneFlags.Draining) != 0)
        {
            // Убираемая полоса (4.3, T4; player-tools.md §4.3): вынужденная смена к открытой полосе, как «полоса кончается»: вправо, если справа есть полоса не из убираемых, иначе влево.
            if (ld.Right >= 0 && (e.Net.Lanes[ld.Right].Flags & LaneFlags.Draining) == 0)
            {
                forcedDir = -1;
            }
            else if (ld.Left >= 0 && (e.Net.Lanes[ld.Left].Flags & LaneFlags.Draining) == 0)
            {
                forcedDir = 1;
            }
        }

        if (forcedDir == 0 && desired >= 0 && ld.Length - v.S[i] < MinOvertakeRemaining)
        {
            return 0;
        }

        int bestDir = 0;
        float best = forcedDir != 0 ? Infeasible : t.LaneChangeThreshold;
        for (int dir = -1; dir <= 1; dir += 2)
        {
            if (forcedDir != 0 && dir != forcedDir)
            {
                continue;
            }

            float inc = Evaluate(e, types, p, i, aCur, dir);
            if (inc > best)
            {
                best = inc;
                bestDir = dir;
            }
        }

        return bestDir;
    }

    /// <summary>Стимул смены полосы в направлении <paramref name="dir"/> или <see cref="float.NegativeInfinity"/>, если манёвр небезопасен/невозможен.</summary>
    private static float Evaluate(in RuleEnv e, ReadOnlySpan<VehicleTypeDesc> types, in PhysicsParams p, int i, float aCur, int dir)
    {
        VehicleStore v = e.V;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        int target = dir > 0 ? ld.Left : ld.Right;
        if (target < 0 || ((ld.Flags & LaneFlags.NoLaneChangeOut) != 0 && (ld.Flags & LaneFlags.Draining) == 0))
        {
            return Infeasible;
        }

        ref readonly LaneData tl = ref e.Net.Lanes[target];
        if (tl.Kind != LaneKind.Normal || (v.Access(i) & ~tl.Deny) == 0 ||
            ((tl.Flags & (LaneFlags.Closed | LaneFlags.Draining)) != 0 && (ld.Flags & (LaneFlags.Closed | LaneFlags.Draining)) == 0))
        {
            return Infeasible;   // маска запрета полосы (vehicle-classes.md §2.2: автобусная - только автобусам и спецмашинам на вызове); в закрытую и убираемую полосы (4.3, T4) с открытой не перестраиваются (между закрытыми полосами одного сегмента - можно)
        }

        ref readonly VehicleTypeDesc t = ref types[v.Type[i]];
        float si = v.S[i];
        float vi = v.Speed[i];
        float durSec = MathF.Max(1, t.LaneChangeDurationTicks) * p.Dt;
        if (ld.Length - si < (vi * durSec) + 0.5f || si - v.Length[i] < LaneEntryClearance)
        {
            return Infeasible;
        }

        e.Occ.FindNeighbours(target, si, out int ahead, out int behind);

        // intersections.md 7.1: порядок допущенных у стоп-линии не меняется. Вставка перед допущенным (билет уже выдан) дала бы лидеру
        // билет позже, чем у ведомого, и пару «ведомый ждёт лидера физически, лидер ждёт ведомого по билету» (взаимная блокировка).
        if (behind != -1 && GateRules.IsGranted(v, behind))
        {
            return Infeasible;
        }

        LeaderInfo front = LeaderSearch.Find(e, v.S, i, target, si, ahead, useRoute: false);
        if (front.Gap < p.MinPhysicalGap)
        {
            return Infeasible;
        }

        float v0 = v.DesiredSpeed[i];
        float aNew = Idm.Accel(t, vi, v0, front.Gap, vi - front.Speed);
        if (aNew < -t.SafeDecel)
        {
            return Infeasible;
        }

        float followers = 0f;
        if (behind != -1)
        {
            float g = si - v.Length[i] - v.S[behind];
            if (g < p.MinPhysicalGap)
            {
                return Infeasible;
            }

            ref readonly VehicleTypeDesc tb = ref types[v.Type[behind]];
            float vb = v.Speed[behind];
            float v0b = v.DesiredSpeed[behind];
            float aBNew = Idm.Accel(tb, vb, v0b, g, vb - vi);
            if (aBNew < -t.SafeDecel)
            {
                return Infeasible;
            }

            float aBOld = ahead != -1
                ? Idm.Accel(tb, vb, v0b, v.S[ahead] - v.Length[ahead] - v.S[behind], vb - v.Speed[ahead])
                : Idm.FreeAccel(vb, v0b, tb.Accel, tb.Delta);
            followers += aBNew - aBOld;
        }

        int o = v.Behind[i];
        if (o != -1)
        {
            ref readonly VehicleTypeDesc to = ref types[v.Type[o]];
            float vo = v.Speed[o];
            float v0o = v.DesiredSpeed[o];
            float aOOld = Idm.Accel(to, vo, v0o, si - v.Length[i] - v.S[o], vo - vi);
            int lead = v.Ahead[i];
            float aONew = lead != -1
                ? Idm.Accel(to, vo, v0o, v.S[lead] - v.Length[lead] - v.S[o], vo - v.Speed[lead])
                : Idm.FreeAccel(vo, v0o, to.Accel, to.Delta);
            followers += aONew - aOOld;
        }

        float bias = dir < 0 ? t.RightBias : -t.RightBias;
        return (aNew - aCur) + (t.Politeness * followers) + bias;
    }
}
