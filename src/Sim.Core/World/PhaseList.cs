using Sim.Core.Api;

namespace Sim.Core;

/// <summary>
/// Порядок фаз тика (core-api 7). Единственное место, знающее последовательность; сами фазы - в своих папках.
/// Фазы 2-9 пока закомментированы: оркестратор раскомментирует строку при слиянии соответствующей задачи.
/// </summary>
internal static class PhaseList
{
    public static void RunTick(World w)
    {
        CommandDispatcher.Run(w);                 // 1. команды
        // TODO(phase 2): Spawner.Run(w);         // 2. спавн (задача H)
        // TODO(phase 3): RoutePhase.Run(w);      // 3. маршруты (шаг 3)
        // TODO(phase 4): SignalPhase.Run(w);     // 4. перекрёстки и сигналы (шаг 3)
        // TODO(phase 5): BehaviourPhase.Run(w);  // 5. поведение: IDM/MOBIL (задача F)
        // TODO(phase 6): IntegratePhase.Run(w);  // 6. интеграция (задача F)
        // TODO(phase 7): TransitionPhase.Run(w); // 7. переходы между полосами (задача F)
        // TODO(phase 8): ArrivalPhase.Run(w);    // 8. прибытие/деспаун (задача F)
        // TODO(phase 9): MetricsPhase.Run(w);    // 9. метрики (задача H)
        SnapshotPhase.Run(w);                     // 10. снапшот (SnapshotTick = Tick + 1)

        w.Tick++;
        w.Counters.Ticks++;
        WorldBuilder.SyncStructuralCounters(w);
    }
}
