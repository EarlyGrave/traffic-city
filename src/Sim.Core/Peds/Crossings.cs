using Sim.Core.Api;

namespace Sim.Core.Peds;

/// <summary>
/// Переходы (pedestrians.md §5): точки расширения фазы 4p. <b>P2: переходы «прозрачные»</b> - пешеход сразу выходит на полосу, машин переход не трогает;
/// всё состояние (<see cref="CrossingState"/>), столбцы сейва <c>XING</c>, счётчики и гистограммы уже объявлены и сохраняются. Реальный автомат, очереди,
/// маски узлов и ворота - P3: он правит только тела методов этого файла (и <c>Rules/Intersections</c>), не сигнатуры и не общие файлы.
/// <para>
/// Контракт для P3: <c>x</c> - индекс перехода (<c>PedNetwork.XOfEdge</c>): слот рукава или <c>MidXBase + m</c>. Очередь у бордюра - FIFO через
/// <c>PedStore.Next</c> с <c>QHead/QTail/QCount</c>; пешеход в очереди: <c>State = AtCrossing</c>, <c>Edge</c> = шаг-переход (с битами), <c>T0</c> = начало ожидания,
/// <c>EvTick = -1</c> (в колесе его нет), в списки рёбер не входит. Выпуск - <c>PedWorld.StartEdge(w, p, step, PedState.Crossing, 0)</c>.
/// Метрики ожидания пишутся в <c>Counters.PedCrossWaitTicksTotal/PedCrossReleases</c> и <c>PedWorld.CrossWaitHist</c>.
/// </para>
/// </summary>
internal static class Crossings
{
    /// <summary>Пешеход дошёл до конца тротуара и следующий шаг пути - переход <paramref name="step"/> (с битами <c>StepCross</c>, <c>StepBwd</c>).</summary>
    public static void OnArrive(World w, int p, int step)
    {
        // P2: переход прозрачен - выходим сразу.
        w.Peds!.StartEdge(w, p, step, PedState.Crossing, 0);
    }

    /// <summary>Пешеход сошёл с полосы перехода <paramref name="x"/> (событие конца ребра-перехода, до следующего шага).</summary>
    public static void OnLeave(World w, int p, int x)
    {
    }

    /// <summary>Шаг автоматов переходов (<c>XActive</c> по возрастанию индекса), конец фазы 4p после событий колеса.</summary>
    public static void Step(World w)
    {
    }

    /// <summary>После <c>Load</c>: пересчитать производные (<c>NodeXIn/NodeXOut</c>, <c>MidClosed</c>, <c>XActive</c>, <c>CrossingSignals</c>) из <c>XState</c> и очередей.</summary>
    public static void RestoreDerived(World w)
    {
    }
}
