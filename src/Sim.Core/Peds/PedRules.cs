using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Peds;

/// <summary>
/// Общие правила переходов для машин (pedestrians.md §5.2, §5.6): один предикат <see cref="CanStop"/> для фаз 4p, 4b и 5 и препятствие перехода
/// на перегоне. Без аллокаций; читает только данные, которых фаза 5 не пишет (позиции, скорости, состояние переходов конца фазы 4p).
/// </summary>
internal static class PedRules
{
    /// <summary>Препятствие стоит на столько метров раньше начала полосы перехода: остановившаяся машина (d &gt;= 1.5 &gt; 1) остаётся <c>CanStop</c> и не обязывается.</summary>
    public const float StopMargin = 1.5f;

    /// <summary>Машина, стоящая на полосе перехода столько тиков (<c>StillTicks</c>) и медленнее <see cref="GateRules.CreepSpeed"/>, не обязывается: пешеходы её обходят (3 с).</summary>
    public const int StandTicks = 60;

    /// <summary>
    /// <c>CanStop(d, v) = d &gt; v²/(2·b) + 1 м</c>: <paramref name="d"/> - расстояние от переднего бампера до начала полосы перехода (для машины на Normal-полосе перед
    /// узлом - <c>Length - S - StopLineOffset</c>), <paramref name="comfortDecel"/> - комфортное замедление типа ТС. Тот же предикат, что в снятии допуска на красном.
    /// </summary>
    public static bool CanStop(float d, float v, float comfortDecel)
    {
        float b = comfortDecel > 0.1f ? comfortDecel : 0.1f;
        return d > (v * v / (2f * b)) + 1f;
    }

    /// <summary>Машина стоит достаточно долго, чтобы пешеходы её обходили (§5.3, исключение).</summary>
    public static bool IsStanding(Sim.Core.Agents.VehicleStore v, int i) => v.Speed[i] < GateRules.CreepSpeed && v.StillTicks[i] >= StandTicks;

    /// <summary>
    /// Зазор (м) до препятствия закрытого перехода на перегоне впереди машины на полосе <paramref name="lane"/> (позиция <paramref name="s"/>, скорость
    /// <paramref name="v"/>): ближайший закрытый переход цепочки (не больше двух), перед которым машина ещё <see cref="CanStop"/>; <see cref="Idm.FreeGap"/>, если такого нет.
    /// Препятствие стоит в <c>начало полосы перехода - StopMargin</c>. Машина, которая не может остановиться, едет (она обязалась, §5.3).
    /// </summary>
    public static float MidblockGap(NetworkStore net, PedNetwork pn, CrossingState xs, int lane, float s, float v, float comfortDecel)
    {
        int m = pn.LaneMidX[lane];
        if (m < 0)
        {
            return Idm.FreeGap;
        }

        int dir = net.Lanes[lane].Direction;
        float len = net.LaneLength[lane];
        for (int hop = 0; hop < 2 && m >= 0; hop++)
        {
            pn.MidLaneRange(m, dir, len, out float s0, out _);
            if (s <= s0)
            {
                if (xs.MidClosed[m] != 0 && CanStop(s0 - s, v, comfortDecel))
                {
                    return s0 - StopMargin - s;
                }
            }

            m = dir == 0 ? pn.MidNextFwd[m] : pn.MidNextBwd[m];
        }

        return Idm.FreeGap;
    }
}
