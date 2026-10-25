using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Metrics;
using Sim.Core.Peds;
using Sim.Core.Routing;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;

namespace Sim.Core;

/// <summary>Номера зонда сверх 1..10 (core-api §3): подфазы пешеходов; хост, не знающий их, игнорирует (вызываются только при <c>peds</c>).</summary>
internal static class PhaseIds
{
    public const int PedSpawn = 11;
    public const int PedRoute = 12;
    public const int PedEvents = 13;

    /// <summary>Метка «время с прошлого вызова отнести к фазе 4 (gate)»: пешеходная фаза 4p вклинивается между 4a и 4b.</summary>
    public const int GateLap = 14;
}

/// <summary>
/// Порядок фаз тика (core-api 7). Единственное место, знающее последовательность; сами фазы - в своих папках.
/// </summary>
internal static class PhaseList
{
    public static void RunTick(World w)
    {
        PhaseProbe? probe = w.PhaseProbe;
        CommandDispatcher.Run(w);                 // 1. команды
        probe?.Invoke(1);
        Spawner.Run(w);                           // 2. спавн
        BusDispatch.Run(w);                       // 2b. автобусы: выпуск по интервалу
        probe?.Invoke(2);
        if (w.Peds != null)
        {
            PedSpawn.Run(w);                      // 2d. пешеходы: запуск поездок жителей (pedestrians.md §3)
            probe?.Invoke(PhaseIds.PedSpawn);
        }

        RoutePhase.Run(w);                        // 3. маршруты: перемаршрутизация и поездки без маршрута
        probe?.Invoke(3);
        if (w.Peds != null)
        {
            PedPhase.Route(w);                    // 3b. пути пешеходов в бюджете (pedestrians.md §4.4)
            probe?.Invoke(PhaseIds.PedRoute);
        }

        SignalPhase.Run(w);                       // 4a. сигналы: таймеры, LaneSignals, SignalStates
        w.Queue?.Run();                           // 4a'. Q-meso: синтез голов такта, события колеса (ADR-0019, sim-lod.md §13.9)
        if (w.Peds != null)
        {
            probe?.Invoke(PhaseIds.GateLap);      // время 4a идёт в «gate», как без пешеходов
            PedPhase.Run(w);                      // 4p. события колеса пешеходов, автоматы переходов (pedestrians.md §4.5, §5.4)
            probe?.Invoke(PhaseIds.PedEvents);
        }

        GatePhase.Run(w);                         // 4b. ворота перекрёстков: допуски, билеты, OutReserved
        probe?.Invoke(4);
        BehaviourPhase.Run(w);                    // 5. поведение: IDM/MOBIL
        probe?.Invoke(5);
        IntegratePhase.Run(w);                    // 6. интеграция
        probe?.Invoke(6);
        TransitionPhase.Run(w);                   // 7. переходы между полосами
        probe?.Invoke(7);
        ArrivalPhase.Run(w);                      // 8. прибытие/деспаун
        BusStopPhase.Run(w);                      // 8b. автобусы: остановки, посадка, отправление, конечная
        probe?.Invoke(8);
        MetricsPhase.Run(w);                      // 9. метрики
        probe?.Invoke(9);
        SnapshotPhase.Run(w);                     // 10. снапшот (SnapshotTick = Tick + 1)
        probe?.Invoke(10);

        w.Tick++;
        w.Counters.Ticks++;
        WorldBuilder.SyncStructuralCounters(w);
    }
}
