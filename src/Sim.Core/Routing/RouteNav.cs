using System.Diagnostics;
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

/// <summary>Запись очереди дозаполнения окон (routing.md 9.5): машина (индекс + поколение).</summary>
internal struct RefillEntry
{
    public int Index;
    public int Generation;
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
            v.MarkRouteDirty(i);
            return;
        }

        int idx1 = e.RouteStepIndex(i, 1);
        if (idx1 >= 0)
        {
            if (e.Routes.Steps![idx1].Lane == toLane)
            {
                v.RouteCursor[i]++;
                v.MarkRouteDirty(i);
                return;
            }

            int idx2 = e.RouteStepIndex(i, 2);
            if (idx2 >= 0 && e.Net.Lanes[toLane].Kind == LaneKind.Connector &&
                RuleEnv.DirSegOf(e.Net, e.Net.Lanes[toLane].ToLane) == RuleEnv.DirSegOf(e.Net, e.Routes.Steps[idx2].Lane))
            {
                v.RouteCursor[i]++;
                v.MarkRouteDirty(i);
                return;
            }
        }

        v.Flags[i] |= (byte)VehicleFlags.RouteMissed;
        v.MarkRouteDirty(i);
    }

    /// <summary>
    /// Конец фазы 7. Для каждой живой машины с флагом <see cref="VehicleStore.RouteDirtyBits"/> (ADR-0016, D7): промах -> маршрут освобождается, <c>Route = -1</c>, машина в очередь перемаршрутизации
    /// (<c>Reroutes++</c>); затем <c>NextConn</c> пересчитывается, если он не от текущей полосы (въезд на новую полосу, смена полосы).
    /// Обход по маске грязных машин по возрастанию индекса (ADR-0019, A3) - тот же порядок, что у прежнего прохода по всем; слово маски
    /// перечитывается после каждой машины, поэтому бит, выставленный по ходу обработки у большего индекса, не теряется.
    /// </summary>
    public static void AssignConnectors(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int hw = v.HighWater;
        int refillChecks = 0;
        int recomputes = 0;
        ulong[] bits = v.RouteDirtyBits;
        int words = (hw + 63) >> 6;
#if DEBUG
        for (int i = 0; i < hw; i++)
        {
            // Debug-сверка D7: для машины без флага полный расчёт ничего бы не изменил (мёртвые и без полосы не считаются).
            if (!v.IsRouteDirty(i) && v.Lane[i] >= 0 && v.Pool.IsAlive(i))
            {
                Debug.Assert(UnchangedByFullRecalc(w, i, v.Lane[i]), "RouteDirty flag missed: AssignConnectors result differs");
            }
        }
#endif
        for (int k = 0; k < words; k++)
        {
            int pos = 0;
            while (pos < 64)
            {
                ulong m = bits[k] & (ulong.MaxValue << pos);   // слово перечитывается: бит, выставленный по ходу обработки, не теряется
                if (m == 0UL)
                {
                    break;
                }

                int b = System.Numerics.BitOperations.TrailingZeroCount(m);
                int i = (k << 6) + b;
                pos = b + 1;
                if (i >= hw)
                {
                    break;
                }

                if (!v.Pool.IsAlive(i))
                {
                    v.ClearRouteDirty(i);   // слот свободен: Reset при спавне выставит бит заново
                    continue;
                }

                int lane = v.Lane[i];
                if (lane < 0)
                {
                    continue;
                }

                v.ClearRouteDirty(i);
                if ((v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0)
                {
                    ProcessMiss(w, i);
                }

                refillChecks++;
                if (!QueueRefill(w, i))
                {
                    v.MarkRouteDirty(i); // очередь дозаполнения полна: повторить на следующем тике
                }

                int cur = v.NextConn[i];
                if (cur >= 0 && net.Lanes[cur].FromLane == lane && net.Lanes[lane].Kind == LaneKind.Normal)
                {
                    continue;
                }

                recomputes++;
                v.NextConn[i] = NextConnector(w, i, lane);
            }
        }

        w.Work.RouteRefillChecks += refillChecks;
        w.Work.NextConnRecomputes += recomputes;
    }

    /// <summary>
    /// Ставит машину в очередь дозаполнения окна (routing.md 9.5), если у плана есть хвост, а впереди в окне осталось
    /// не больше <see cref="Router.RefillAhead"/> направленных сегментов. Флаг <see cref="VehicleFlags.RefillQueued"/> защищает от дублей.
    /// </summary>
    private static bool QueueRefill(World w, int i)
    {
        if (!NeedsRefill(w, i))
        {
            return true;
        }

        VehicleStore v = w.Vehicles;
        var entry = new RefillEntry { Index = i, Generation = v.Pool.GenerationOf(i) };
        if (w.RefillQueue.TryEnqueue(in entry))
        {
            v.Flags[i] |= (byte)VehicleFlags.RefillQueued;
            return true;
        }

        return false;
    }

    /// <summary>Нужно ли ставить машину в очередь дозаполнения (чистая функция состояния, без побочных эффектов).</summary>
    private static bool NeedsRefill(World w, int i)
    {
        VehicleStore v = w.Vehicles;
        int r = v.Route[i];
        if (r < 0 || (v.Flags[i] & (byte)(VehicleFlags.RefillQueued | VehicleFlags.RouteMissed)) != 0 || w.Routes.PlanCount[r] == 0)
        {
            return false;
        }

        int cur = v.RouteCursor[i];
        int wc = w.Routes.WinCount[r];
        int ahead;
        if (cur < 0)
        {
            ahead = (wc + 1) / 2;
        }
        else if (cur >= wc)
        {
            ahead = 0;
        }
        else
        {
            bool onConnector = w.Net.Lanes[w.Routes.Window[(r * w.Routes.WindowSteps) + cur].Lane].Kind == LaneKind.Connector;
            ahead = (wc - 1 - cur + (onConnector ? 1 : 0)) / 2;
        }

        return ahead <= Router.RefillAhead;
    }

    /// <summary>
    /// Debug-сверка D7: для машины без флага полный расчёт <see cref="AssignConnectors"/> ничего бы не изменил - промаха нет,
    /// в очередь дозаполнения не встала бы, а <c>NextConn</c> либо валиден, либо совпадает с пересчётом.
    /// </summary>
    private static bool UnchangedByFullRecalc(World w, int i, int lane)
    {
        VehicleStore v = w.Vehicles;
        if ((v.Flags[i] & (byte)VehicleFlags.RouteMissed) != 0 || NeedsRefill(w, i))
        {
            return false;
        }

        int cur = v.NextConn[i];
        if (cur >= 0 && w.Net.Lanes[cur].FromLane == lane && w.Net.Lanes[lane].Kind == LaneKind.Normal)
        {
            return true;
        }

        return cur == NextConnector(w, i, lane);
    }

    /// <summary>
    /// Правка сети (4.3, T3/T4): маршрут машины <paramref name="i"/> больше не годится - освобождается сразу (как при промахе), машина едет по умолчанию и встаёт в очередь
    /// перемаршрутизации. Пересчёт <c>NextConn</c> - на конце ближайшей фазы 7 (бит грязи).
    /// </summary>
    internal static void ReleaseRoute(World w, int i)
    {
        ProcessMiss(w, i);
        w.Vehicles.MarkRouteDirty(i);
    }

    private static void ProcessMiss(World w, int i)
    {
        VehicleStore v = w.Vehicles;
        if (v.Route[i] >= 0)
        {
            int r = v.Route[i];
            if (w.Routes.PlanCount[r] > 0 && v.RouteCursor[i] >= w.Routes.WinCount[r] - 1)
            {
                w.Counters.RefillLate++; // дошли до конца непустого окна без дозаполнения (цель 0)
            }

            w.Routes.Free(new RouteHandle(r, v.RouteGen[i]));
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
