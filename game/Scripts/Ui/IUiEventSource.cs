using System.Collections.Generic;
using Godot;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>
/// Источник событий ядра для уведомлений (UI6). Реализует хост (<c>SimHost</c>): копит <c>Simulation.Events</c> после каждого <c>Step</c>
/// (буфер событий ядра живёт один тик) и умеет привязать объект уведомления к точке мира.
/// Подключение одной строкой: <c>GetNode&lt;UiRoot&gt;("../Ui").Notify.Source = this;</c> (SimHost : IUiEventSource).
/// </summary>
public interface IUiEventSource
{
    /// <summary>Перенести накопленные с прошлого вызова события в <paramref name="sink"/> (и очистить у себя).</summary>
    void DrainEvents(List<SimEvent> sink);

    /// <summary>Точка мира и характерный размер области, м, для «Показать»; false - объект уже исчез.</summary>
    bool TryLocate(in NotifySubject subject, out Vector3 position, out float extent);

    /// <summary>Выделить объект и открыть его инспектор (после подлёта камеры); хост может ничего не делать.</summary>
    void SelectSubject(in NotifySubject subject);
}
