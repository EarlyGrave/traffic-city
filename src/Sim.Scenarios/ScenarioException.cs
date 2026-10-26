using System;
using Sim.Scenarios.Mods;

namespace Sim.Scenarios;

/// <summary>Ошибка загрузки сценария. Сообщение содержит JSON-путь вида <c>$.city.scale: expected number</c>.</summary>
public sealed class ScenarioException : Exception
{
    public ScenarioException(string message)
        : base(message)
    {
    }

    public ScenarioException(string message, ModError? code)
        : base(message)
    {
        ModCode = code;
    }

    public ScenarioException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Код ошибки пакетов/контента (modding.md §9) или <c>null</c> для ошибок формата сценария.</summary>
    public ModError? ModCode { get; }
}
