using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Metrics;
using Sim.Core.Routing;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;

namespace Sim.Core;

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
        RoutePhase.Run(w);                        // 3. маршруты: перемаршрутизация и поездки без маршрута
        probe?.Invoke(3);
        SignalPhase.Run(w);                       // 4a. сигналы: таймеры, LaneSignals, SignalStates
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
