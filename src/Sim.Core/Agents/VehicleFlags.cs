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
    InBay = 64,         // автобус в виртуальном кармане остановки (bus-bays.md §3): Lane == -1, вне списков полос; инвариант проверяет валидатор сейва
    OnCall = 128,       // спецмашина на вызове (vehicle-classes.md §2.4): только у типа класса special; поведение - в VC3, пока лишь флаг и валидатор сейва
    RouteMissed = 16,   // промах полосы в фазе 7: маршрут уже недействителен, освобождение и постановка в перемаршрутизацию - в конце фазы 7
}
