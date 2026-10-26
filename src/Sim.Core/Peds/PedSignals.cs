namespace Sim.Core.Peds;

/// <summary>
/// Пешеходная фаза светофора (pedestrians.md §6): публикация сигнала перехода (<c>CrossingSignals</c>) и вызов. Логика <c>Walk/Clear</c> по слотам светофора - в <c>SignalPhase</c>.
/// </summary>
internal static class PedSignals
{
    /// <summary>Сигнал перехода: биты 2-3 <c>CrossingSignals</c>.</summary>
    public const byte DontWalk = 0;
    public const byte Walk = 1;
    public const byte Clear = 2;

    /// <summary>Пешеходная фаза включена (коммит 2); пока false, переходы узлов <c>Signal</c> работают как нерегулируемые.</summary>
    public static bool Enabled => false;

    public static bool IsWalk(CrossingState xs, int x) => ((xs.CrossingSignals[x] >> 2) & 3) == Walk;

    /// <summary>Публикует состояние автомата перехода (биты 0-1), сохраняя сигнал.</summary>
    public static void PublishState(CrossingState xs, int x) =>
        xs.CrossingSignals[x] = (byte)((xs.CrossingSignals[x] & 0xC) | xs.XState[x]);
}
