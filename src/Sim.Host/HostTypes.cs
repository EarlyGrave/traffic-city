using System;
using Sim.Core.Api;

namespace Sim.Host;

public enum HostState
{
    Running = 0,
    Faulted = 1,
    Stopped = 2,
}

public enum HostRequestKind : byte
{
    None = 0,
    PickVehicle = 1,
    PickPedestrian = 2,
}

/// <summary>Запрос с ответом (почтовый ящик, ADR-0024 п. 4): поток симуляции выполняет его между <c>Step</c> по текущему снапшоту ядра.</summary>
public struct HostRequest
{
    public long Id { get; set; }

    public HostRequestKind Kind { get; set; }

    public float X { get; set; }

    public float Z { get; set; }

    public float Radius { get; set; }
}

/// <summary>Ответ на <see cref="HostRequest"/>: приходит отдельным кольцом (кадры могут перезаписываться, ответы - нет).</summary>
public struct HostResponse
{
    public long RequestId { get; set; }

    public HostRequestKind Kind { get; set; }

    public bool Found { get; set; }

    /// <summary>Тик ядра, на котором выполнен запрос.</summary>
    public long Tick { get; set; }

    public int Index { get; set; }

    public int Generation { get; set; }

    public VehicleHandle Vehicle => new(Index, Generation);

    public PedHandle Pedestrian => new(Index, Generation);
}

/// <summary>Команда и тик ядра, на границе которого она применена (<c>Simulation.Tick</c> перед <c>Step</c>; фаза 1 первого тика пачки). Реплей: дойти до Tick и <c>Enqueue</c>.</summary>
public readonly struct JournalEntry
{
    public JournalEntry(long tick, in Command command)
    {
        Tick = tick;
        Command = command;
    }

    public long Tick { get; }

    public Command Command { get; }
}

public sealed class SimHostOptions
{
    /// <summary>Кольцо команд главный -> симуляция (округляется вверх до степени двойки).</summary>
    public int CommandRingCapacity { get; init; } = 1024;

    public int RequestRingCapacity { get; init; } = 64;

    public int ResponseRingCapacity { get; init; } = 64;

    /// <summary>Предел тиков за один <c>Step</c> (ADR-0024 п. 2: как сейчас в клиенте - 5).</summary>
    public int MaxStepBatch { get; init; } = 5;

    /// <summary>Начальная ёмкость массивов кадра; растут кратно 1 024 при нехватке (редко, ADR-0024 п. 6).</summary>
    public int InitialFrameCapacity { get; init; } = 1024;

    public int JournalCapacity { get; init; } = 4096;

    public string ThreadName { get; init; } = "sim-thread";
}

/// <summary>Эксклюзивный доступ к ядру (ADR-0024 п. 5): пока не вызван <see cref="Dispose"/>, поток симуляции стоит. Не вкладывать и не держать долго.</summary>
public readonly struct ExclusiveAccess : IDisposable
{
    private readonly SimThreadHost? _host;

    internal ExclusiveAccess(SimThreadHost host, Simulation simulation)
    {
        _host = host;
        Simulation = simulation;
    }

    /// <summary>Ядро; пусто у default-значения. Использовать только до <see cref="Dispose"/>.</summary>
    public Simulation? Simulation { get; }

    public void Dispose() => _host?.ReleaseExclusive();
}
