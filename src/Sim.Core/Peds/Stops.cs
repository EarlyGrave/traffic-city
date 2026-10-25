using Sim.Core.Api;

namespace Sim.Core.Peds;

/// <summary>
/// Остановки и пассажиры (pedestrians.md §7): точки расширения. <b>P2: пассажиров нет</b> - жители ходят пешком, ноги к остановке не создаются;
/// очереди остановок (<c>PedWorld.StopQHead/StopQTail/StopQCount</c>), списки пассажиров (<c>PaxHead</c>), поля <c>PedStore</c> (<c>Bus</c>, <c>AlightStop</c>, <c>Leg</c>) и
/// гистограмма <c>StopWaitHist</c> объявлены и сохраняются. Посадка и высадка, выбор способа и ноги жителей - P4: он правит тела методов этого файла,
/// <c>Transit/BusStopPhase</c> и <c>ResidentStore.Create</c>, не сигнатуры и не общие файлы.
/// <para>
/// Контракт для P4: пешеход в очереди остановки: <c>State = AtStop</c>, <c>T0</c> = начало ожидания, <c>EvTick = -1</c>, список через <c>PedStore.Next</c>; пассажир:
/// <c>State = OnBus</c>, <c>Bus</c> = индекс автобуса, <c>AlightStop</c> = индекс остановки высадки в <c>BusRouteStops</c>, список <c>PaxHead[bus]</c>.
/// Вышедший пассажир - <c>PendingPath</c> в точке доступа остановки с новой ногой (<c>PedWorld.EnqueuePath</c>). Точка доступа остановки - цель ноги
/// <c>ToStop</c> (<c>GoalEdge</c>, <c>GoalSMm</c>) на тротуаре: по ней снапшот ставит очередь (<c>AtStop</c>) и отбирает остановки по виду.
/// </para>
/// </summary>
internal static class Stops
{
    /// <summary>Нога <c>ToStop</c> закончилась у остановки: P2 не создаёт таких ног; на случай чужой записи пешеход считается прибывшим.</summary>
    public static void OnArrive(World w, int p)
    {
        w.Peds!.Arrive(w, p);
    }

    /// <summary>После <c>Load</c>: пересчитать <c>StopWaiting</c> и <c>BusStopData.Waiting</c> (режим agents) из очередей.</summary>
    public static void RestoreDerived(World w)
    {
    }
}
