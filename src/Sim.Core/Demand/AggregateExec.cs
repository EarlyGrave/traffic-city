using Sim.Core.Api;

namespace Sim.Core.Demand;

/// <summary>
/// Агрегатное исполнение ног и «10 лет» (zoning-demand.md §9). Z1: заглушка с финальной сигнатурой - <c>CommandKind.SetTripExecution</c> (24) отклоняется
/// <see cref="RejectReason.NotSupported"/>. Тело (переключение на границе суток, <c>TimerWheel.AdvanceTo</c>, калибровка <c>K</c>) - задача Z5.
/// </summary>
internal static class AggregateExec
{
    /// <summary>Применяет <c>SetTripExecution</c>; <see cref="RejectReason.None"/> - принята.</summary>
    public static RejectReason ApplySwitch(World w, in Command cmd) => RejectReason.NotSupported;
}
