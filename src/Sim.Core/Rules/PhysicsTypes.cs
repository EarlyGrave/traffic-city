using System.Diagnostics;
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
/// Доступ фаз к окнам маршрутов (<see cref="Routing.RouteStore"/>, routing.md 9.5). Окно машины i - диапазон
/// <c>Steps[r * Width .. + Count[r])</c>, r = <c>VehicleStore.Route[i]</c>, шаг <c>RouteCursor[i]</c> - текущая полоса
/// (обычная и connector чередуются). <c>PlanCount[r] &gt; 0</c> - за окном есть ещё не развёрнутый хвост маршрута: последний шаг окна не финиш.
/// <see cref="None"/> - маршрутов нет, все машины едут «по умолчанию».
/// </summary>
internal readonly struct RouteAccess
{
    public readonly byte[]? Count;
    public readonly int[]? PlanCount;
    public readonly RouteStep[]? Steps;
    public readonly int Width;

    public RouteAccess(byte[] count, int[] planCount, RouteStep[] steps, int width)
    {
        Count = count;
        PlanCount = planCount;
        Steps = steps;
        Width = width;
    }

    public static RouteAccess None => default;

    public static RouteAccess From(World w) => new(w.Routes.WinCount, w.Routes.PlanCount, w.Routes.Window, w.Routes.WindowSteps);

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

    /// <summary>Действителен ли маршрут машины <paramref name="i"/> для навигации (есть запись и не отмечен промах).</summary>
    public bool HasRoute(int i) => V.Route[i] >= 0 && Routes.Steps is not null && (V.Flags[i] & (byte)VehicleFlags.RouteMissed) == 0;

    /// <summary>Индекс шага маршрута машины <paramref name="i"/> + <paramref name="k"/> в <c>Routes.Steps</c> или -1 (нет маршрута / вышли за конец окна).</summary>
    public int RouteStepIndex(int i, int k)
    {
        if (!HasRoute(i))
        {
            return -1;
        }

        int r = V.Route[i];
        int c = V.RouteCursor[i] + k;
        return c >= 0 && c < Routes.Count![r] ? (r * Routes.Width) + c : -1;
    }

    /// <summary>
    /// Первый переход <c>NextLane(i, lane, 1, useRoute: true)</c> для машины на её текущей полосе (ADR-0016, D6): берётся готовый
    /// <see cref="VehicleStore.NextConn"/>, если он посчитан (&gt;= 0), иначе полный расчёт. В Debug - сверка с полным расчётом.
    /// </summary>
    public int FirstHop(int i, int lane)
    {
        int nc = V.NextConn[i];
        if (nc >= 0)
        {
            Debug.Assert(nc == NextLane(i, lane, 1, useRoute: true), "NextConn out of sync with NextLane");
            return nc;
        }

        return NextLane(i, lane, 1, useRoute: true);
    }

    /// <summary>
    /// Следующая полоса после <paramref name="lane"/>. На connector'е - его <c>ToLane</c>. На обычной полосе - connector по маршруту
    /// (routing.md 6.2): на целевой полосе тот, что в маршруте; на другой полосе того же сегмента - connector в тот же направленный
    /// сегмент, что и у маршрута (на следующем сегменте MOBIL доведёт до нужной полосы); иначе (промах, маршрут кончился, маршрута
    /// нет) - connector по умолчанию (<see cref="DefaultConnector"/>). -1 только если у полосы нет исходящих connector'ов.
    /// <paramref name="k"/> - сколько шагов маршрута вперёд от текущего (1 = ближайший следующий); <paramref name="useRoute"/> = false:
    /// игнорировать маршрут.
    /// </summary>
    public int NextLane(int i, int lane, int k, bool useRoute)
    {
        ref readonly LaneData l = ref Net.Lanes[lane];
        if (l.Kind == LaneKind.Connector)
        {
            return l.ToLane;
        }

        if (useRoute)
        {
            int idx = RouteStepIndex(i, k);
            if (idx >= 0)
            {
                int c = Routes.Steps![idx].Lane;
                ref readonly LaneData cd = ref Net.Lanes[c];
                if (cd.Kind == LaneKind.Connector)
                {
                    if (cd.FromLane == lane)
                    {
                        return c;
                    }

                    int idxNext = RouteStepIndex(i, k + 1);
                    if (idxNext >= 0)
                    {
                        int want = DirSegOf(Net, Routes.Steps[idxNext].Lane);
                        for (int n = 0; n < l.NextCount; n++)
                        {
                            int cc = Net.LaneNext[l.FirstNext + n];
                            if (DirSegOf(Net, Net.Lanes[cc].ToLane) == want)
                            {
                                return cc;
                            }
                        }
                    }
                }
            }
        }

        return DefaultConnector(Net, lane);
    }

    /// <summary>Направленный сегмент полосы: <c>segment * 2 + direction</c> (как вершина графа маршрутизации).</summary>
    public static int DirSegOf(NetworkStore net, int lane) => (net.Lanes[lane].Segment * 2) + net.Lanes[lane].Direction;

    /// <summary>
    /// Connector по умолчанию (машина без маршрута или после промаха): первый по порядку <c>LaneNext</c> среди лучшего класса -
    /// не разворот и не тупик, затем не разворот, затем любой. Детерминирован, PRNG не использует. -1 - исходящих нет.
    /// </summary>
    public static int DefaultConnector(NetworkStore net, int lane)
    {
        ref readonly LaneData l = ref net.Lanes[lane];
        if (l.NextCount <= 0)
        {
            return -1;
        }

        int bestTier = 4;
        int best = net.LaneNext[l.FirstNext];
        for (int n = 0; n < l.NextCount && bestTier > 0; n++)
        {
            int c = net.LaneNext[l.FirstNext + n];
            int to = net.Lanes[c].ToLane;
            int tier = 2;
            if (to >= 0)
            {
                ref readonly LaneData t = ref net.Lanes[to];
                tier = t.Segment == l.Segment ? 2 : (t.NextCount <= 0 ? 1 : 0);
                if ((t.Flags & LaneFlags.Closed) != 0 || (net.Lanes[c].Flags & LaneFlags.Closed) != 0)
                {
                    tier = 3;   // закрыто дренажем (4.3, T4): выбирается, только если открытых выездов нет
                }
            }

            if (tier < bestTier)
            {
                bestTier = tier;
                best = c;
            }
        }

        return best;
    }

    /// <summary>
    /// Желаемая полоса для MOBIL (routing.md 6.4): полоса, с которой маршрут входит в ближайший connector; на последнем сегменте
    /// (connector'а дальше нет и план пуст) - полоса прибытия. -1 - маршрута нет / курсор не на обычной полосе / окно кончилось, а план нет
    /// (последний шаг окна - не финиш, желаемой полосы нет).
    /// </summary>
    public int RouteDesiredLane(int i)
    {
        int idx = RouteStepIndex(i, 1);
        if (idx >= 0)
        {
            int next = Routes.Steps![idx].Lane;
            return Net.Lanes[next].Kind == LaneKind.Connector ? Net.Lanes[next].FromLane : -1;
        }

        int cur = RouteStepIndex(i, 0);
        if (cur >= 0 && RouteStepIndex(i, 1) < 0 && Routes.PlanCount![V.Route[i]] == 0)
        {
            int lane = Routes.Steps![cur].Lane;
            return Net.Lanes[lane].Kind == LaneKind.Normal ? lane : -1;
        }

        return -1;
    }
}
