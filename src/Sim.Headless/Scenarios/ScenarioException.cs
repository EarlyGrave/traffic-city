using System;

namespace Sim.Headless.Scenarios;

/// <summary>Ошибка загрузки сценария. Сообщение содержит JSON-путь вида <c>$.city.scale: expected number</c>.</summary>
public sealed class ScenarioException : Exception
{
    public ScenarioException(string message)
        : base(message)
    {
    }

    public ScenarioException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
