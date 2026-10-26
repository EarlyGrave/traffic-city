using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Зонирование игроком (zoning-demand.md §14). Z1: заглушка с финальной сигнатурой - <c>CommandKind.PaintZone</c> (23) отклоняется <see cref="RejectReason.NotSupported"/>
/// даже в режиме жителей. Тело (участки под прямоугольник по центру, <c>ZoneRule</c>, здание другого вида в <c>Abandoned</c>) - задача Z6; диспетчер менять не нужно.
/// </summary>
internal static class Zoning
{
    /// <summary>Применяет <c>PaintZone</c>; <see cref="RejectReason.None"/> - применена.</summary>
    public static RejectReason Apply(World w, in Command cmd) => RejectReason.NotSupported;
}
