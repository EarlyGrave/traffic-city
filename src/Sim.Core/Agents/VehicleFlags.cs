using System;

namespace Sim.Core.Agents;

/// <summary>Биты <see cref="VehicleStore.Flags"/> (core-api 6.3).</summary>
[Flags]
internal enum VehicleFlags : byte
{
    None = 0,
    Stuck = 1,
    AtStop = 2,
    Selected = 4,
    IsBus = 8,
    RefillQueued = 32,  // машина стоит в очереди дозаполнения окна маршрута (routing.md 9.5): защита от дублей
    RouteMissed = 16,   // промах полосы в фазе 7: маршрут уже недействителен, освобождение и постановка в перемаршрутизацию - в конце фазы 7
}
