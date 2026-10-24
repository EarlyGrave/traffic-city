using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Rules;

/// <summary>Параметры физики движения, общие для фаз 5-8 (из <see cref="RulesDesc"/> и геометрии сети).</summary>
internal readonly struct PhysicsParams
{
    public const float DefaultDt = 0.05f;
    public const float DefaultLaneWidth = 3.5f;

    public readonly float Dt;
    public readonly float MinPhysicalGap;
    public readonly int MobilEveryTicks;
    public readonly float StuckSpeedThreshold;
    public readonly float LaneWidth;

    public PhysicsParams(float dt, float minPhysicalGap, int mobilEveryTicks, float stuckSpeedThreshold, float laneWidth)
    {
        Dt = dt;
        MinPhysicalGap = minPhysicalGap;
        MobilEveryTicks = mobilEveryTicks < 1 ? 1 : mobilEveryTicks;
        StuckSpeedThreshold = stuckSpeedThreshold;
        LaneWidth = laneWidth;
    }

    public static PhysicsParams From(in RulesDesc rules, float laneWidth = DefaultLaneWidth) =>
        new(DefaultDt, rules.MinPhysicalGap, rules.MobilEveryTicks, rules.StuckSpeedThreshold, laneWidth);

    public static PhysicsParams From(World w) => From(w.Rules, w.Builder.Options.LaneWidth);
}

/// <summary>
/// Доступ фаз к кэшу маршрутов (RoutePool появится в задаче шага 3). Маршрут машины i - диапазон
/// <c>Steps[First[r] .. +Count[r])</c>, r = <c>VehicleStore.Route[i]</c>, шаг <c>RouteCursor[i]</c> - текущая полоса
/// (обычная и connector чередуются). <see cref="None"/> - маршрутов нет, все машины едут «по умолчанию».
/// </summary>
internal readonly struct RouteAccess
{
    public readonly int[]? First;
    public readonly int[]? Count;
    public readonly RouteStep[]? Steps;

    public RouteAccess(int[] first, int[] count, RouteStep[] steps)
    {
        First = first;
        Count = count;
        Steps = steps;
    }

    public static RouteAccess None => default;

    public static RouteAccess From(World w) => new(w.Routes.First, w.Routes.Count, w.Routes.Steps);

    public bool IsEmpty => Steps is null;
}

/// <summary>Ссылки на хранилища, нужные правилам движения. Структура из ссылок - без аллокаций.</summary>
internal readonly struct RuleEnv
{
    public readonly NetworkStore Net;
    public readonly VehicleStore V;
    public readonly LaneOccupancy Occ;
    public readonly RouteAccess Routes;

    public RuleEnv(NetworkStore net, VehicleStore v, LaneOccupancy occ, in RouteAccess routes)
    {
        Net = net;
        V = v;
        Occ = occ;
        Routes = routes;
    }

    /// <summary>Индекс шага маршрута машины <paramref name="i"/> + <paramref name="k"/> в <c>Routes.Steps</c> или -1 (нет маршрута / вышли за конец).</summary>
    public int RouteStepIndex(int i, int k)
    {
        int r = V.Route[i];
        if (r < 0 || Routes.Steps is null)
        {
            return -1;
        }

        int c = V.RouteCursor[i] + k;
        return c >= 0 && c < Routes.Count![r] ? Routes.First![r] + c : -1;
    }

    /// <summary>
    /// Следующая полоса после <paramref name="lane"/>, -1 = продолжения нет (тупик, конец маршрута, либо следующий connector
    /// маршрута начинается не с этой полосы - машине нужно перестроиться). <paramref name="k"/> - сколько шагов маршрута
    /// вперёд от текущего (1 = ближайшая следующая); <paramref name="useRoute"/> = false: игнорировать маршрут.
    /// </summary>
    public int NextLane(int i, int lane, int k, bool useRoute)
    {
        ref readonly LaneData l = ref Net.Lanes[lane];
        if (l.Kind == LaneKind.Connector)
        {
            return l.ToLane;
        }

        if (useRoute && V.Route[i] >= 0 && Routes.Steps is not null)
        {
            int idx = RouteStepIndex(i, k);
            if (idx < 0)
            {
                return -1;
            }

            int next = Routes.Steps[idx].Lane;
            ref readonly LaneData n = ref Net.Lanes[next];
            if (n.Kind == LaneKind.Connector)
            {
                return n.FromLane == lane ? next : -1;
            }

            return next;
        }

        // STAGE2-TEMP: пока нет маршрутов, ближайшая развилка - connector, выбранный TransitionPhase.AssignConnectors (k == 1).
        if (k == 1)
        {
            int chosen = V.NextConn[i];
            if (chosen >= 0 && Net.Lanes[chosen].FromLane == lane)
            {
                return chosen;
            }
        }

        return l.NextCount > 0 ? Net.LaneNext[l.FirstNext] : -1;
    }

    /// <summary>Полоса, с которой маршрут машины входит в ближайший connector (нужная полоса перед поворотом) или -1.</summary>
    public int RouteDesiredLane(int i)
    {
        int idx = RouteStepIndex(i, 1);
        if (idx < 0)
        {
            return -1;
        }

        int next = Routes.Steps![idx].Lane;
        return Net.Lanes[next].Kind == LaneKind.Connector ? Net.Lanes[next].FromLane : -1;
    }
}
