using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Metrics;
using Sim.Core.Routing;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;

namespace Sim.Core;

/// <summary>
/// Порядок фаз тика (core-api 7). Единственное место, знающее последовательность; сами фазы - в своих папках.
/// </summary>
internal static class PhaseList
{
    public static void RunTick(World w)
    {
        CommandDispatcher.Run(w);                 // 1. команды
        Spawner.Run(w);                           // 2. спавн
        RoutePhase.Run(w);                        // 3. маршруты: перемаршрутизация и поездки без маршрута
        SignalPhase.Run(w);                       // 4a. сигналы: таймеры, LaneSignals, SignalStates
        GatePhase.Run(w);                         // 4b. ворота перекрёстков: допуски, билеты, OutReserved
        BehaviourPhase.Run(w);                    // 5. поведение: IDM/MOBIL
        IntegratePhase.Run(w);                    // 6. интеграция
        TransitionPhase.Run(w);                   // 7. переходы между полосами
        ArrivalPhase.Run(w);                      // 8. прибытие/деспаун
        MetricsPhase.Run(w);                      // 9. метрики
        SnapshotPhase.Run(w);                     // 10. снапшот (SnapshotTick = Tick + 1)

        w.Tick++;
        w.Counters.Ticks++;
        WorldBuilder.SyncStructuralCounters(w);
    }
}
