using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;

namespace Sim.Core.Routing;

/// <summary>Запись очереди перемаршрутизации: машина (индекс + поколение) и тик, раньше которого её не обслуживать.</summary>
internal struct RerouteEntry
{
    public int Index;
    public int Generation;
    public long ReadyTick;

    /// <summary>0 - промах мимо полосы (маршрута нет); 1 - плановая перемаршрутизация по времени (маршрут есть, ADR-0006).</summary>
    public int Kind;
}

/// <summary>
/// Движение по маршруту (routing.md 6): выбор connector'а по курсору, продвижение курсора при переходах фазы 7 и обработка промахов.
/// Статический класс без состояния; без аллокаций.
/// </summary>
internal static class RouteNav
{
    /// <summary>Через сколько тиков повторять неудавшуюся перемаршрутизацию (routing.md 3, RerouteRetryTicks).</summary>
    public const int RerouteRetryTicks = 100;

    public const int KindMiss = 0;
    public const int KindTimed = 1;

    /// <summary>
    /// Connector, который машина <paramref name="i"/> на обычной полосе <paramref name="lane"/> возьмёт на ближайшей развилке
    /// (routing.md 6.2): по маршруту, иначе по умолчанию. -1 - выбирать не из чего / полоса не обычная.
    /// </summary>
    public static int NextConnector(World w, int i, int lane)
    {
        if (w.Net.Lanes[lane].Kind != LaneKind.Normal)
        {
            return -1;
        }

        var e = new RuleEnv(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w));
        return e.NextLane(i, lane, 1, useRoute: true);
    }

    /// <summary>
    /// Фаза 7: машина <paramref name="i"/> перешла с <paramref name="fromLane"/> на <paramref name="toLane"/> (подряд идущие полосы цепочки).
    /// Маршрута нет - ничего. С connector'а на обычную полосу - курсор++. С обычной полосы на connector: курсор++, если connector из
    /// маршрута либо ведёт в тот же направленный сегмент, что и шаг маршрута (маршрут продолжается); иначе промах - флаг
    /// <see cref="VehicleFlags.RouteMissed"/> (маршрут освободит <see cref="AssignConnectors"/> в конце фазы 7).
    /// </summary>
    public static void Advance(in RuleEnv e, int i, int fromLane, int toLane)
    {
        if (!e.HasRoute(i))
        {
            return;
        }

        VehicleStore v = e.V;
        if (e.Net.Lanes[fromLane].Kind == LaneKind.Connector)
        {
            v.RouteCursor[i]++;
            return;
        }

        int idx1 = e.RouteStepIndex(i, 1);
        if (idx1 >= 0)
        {
            if (e.Routes.Steps![idx1].Lane == toLane)
            {
                v.RouteCursor[i]++;
                return;
            }

            int idx2 = e.RouteStepIndex(i, 2);
            if (idx2 >= 0 && e.Net.Lanes[toLane].Kind == LaneKind.Connector &&
                RuleEnv.DirSegOf(e.Net, e.Net.Lanes[toLane].ToLane) == RuleEnv.DirSegOf(e.Net, e.Routes.Steps[idx2].Lane))
            {
                v.RouteCursor[i]++;
                return;
            }
        }

        v.Flags[i] |= (byte)VehicleFlags.RouteMissed;
    }

    /// <summary>
    /// Конец фазы 7. Для каждой живой машины: промах -> маршрут освобождается, <c>Route = -1</c>, машина в очередь перемаршрутизации
    /// (<c>Reroutes++</c>); затем <c>NextConn</c> пересчитывается, если он не от текущей полосы (въезд на новую полосу, смена полосы).
    /// Обход по возрастанию индекса - детерминирован.
    /// </summary>
    public static void AssignConnectors(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            int lane = v.Lane[i];
            if (lane < 0 || !v.Pool.IsAlive(i))
            {
                continue;
            }

            if ((v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0)
            {
                ProcessMiss(w, i);
            }

            int cur = v.NextConn[i];
            if (cur >= 0 && net.Lanes[cur].FromLane == lane && net.Lanes[lane].Kind == LaneKind.Normal)
            {
                continue;
            }

            v.NextConn[i] = NextConnector(w, i, lane);
        }
    }

    private static void ProcessMiss(World w, int i)
    {
        VehicleStore v = w.Vehicles;
        if (v.Route[i] >= 0)
        {
            w.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
        }

        v.Route[i] = -1;
        v.RouteGen[i] = 0;
        v.RouteCursor[i] = 0;
        v.NextConn[i] = -1;
        v.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.RouteMissed);
        w.Counters.Reroutes++;
        var entry = new RerouteEntry { Index = i, Generation = v.Pool.GenerationOf(i), ReadyTick = w.Tick };
        w.Rerouting.TryEnqueue(in entry);
    }
}
