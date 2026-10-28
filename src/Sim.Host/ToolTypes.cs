using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Host;

/// <summary>Вид действия инструмента в хосте (T13, player-tools.md §8.3).</summary>
public enum ToolActionKind : byte
{
    None = 0,
    Do = 1,
    Undo = 2,
    Redo = 3,
}

/// <summary>
/// Итог действия инструмента (прямого, undo или redo): приходит через <see cref="SimThreadHost.TryReadToolAck"/>, когда ядро применило группу команд
/// (после первого <c>Step</c> с ней). Стек undo/redo ведёт хост (<c>ToolLedger</c>, поток симуляции); клиент видит только глубины.
/// </summary>
public struct ToolAck
{
    /// <summary>Идентификатор из <see cref="SimThreadHost.TrySubmitAction"/> / <see cref="SimThreadHost.TryUndo"/> / <see cref="SimThreadHost.TryRedo"/>.</summary>
    public uint ActionId { get; set; }

    public ToolActionKind Kind { get; set; }

    /// <summary>Тик ядра, на границе которого применена группа (<c>Simulation.Tick</c> перед <c>Step</c>).</summary>
    public long Tick { get; set; }

    public int Applied { get; set; }

    public int Rejected { get; set; }

    /// <summary>Причина первого отказа (<see cref="RejectReason.None"/>, если отказов нет).</summary>
    public RejectReason FirstReason { get; set; }

    public CommandKind FirstKind { get; set; }

    /// <summary>Undo/redo при пустом стеке: ничего не поставлено.</summary>
    public bool Nothing { get; set; }

    /// <summary>Группа записана в стек (у неё есть обратная); иначе отменить это действие нельзя.</summary>
    public bool Recorded { get; set; }

    /// <summary>Обратная не влезла в буфер ядра или команда без обратной (<c>InverseFlags</c>).</summary>
    public bool Truncated { get; set; }

    /// <summary>Сумма <c>ZoneLotsLost</c> по командам группы (участки, потерянные сносом).</summary>
    public int ZoneLotsLost { get; set; }

    /// <summary>Версия сети после применения (<c>NetworkView.Version</c>).</summary>
    public int NetworkVersion { get; set; }

    public int UndoDepth { get; set; }

    public int RedoDepth { get; set; }

    public readonly bool Ok => Rejected == 0 && !Nothing;
}

/// <summary>
/// Ответ превью (T13): результат ядра, предупреждения и ось будущей дороги. Класс с буферами фиксированной ёмкости - клиент создаёт один раз и переиспользует
/// в <see cref="SimThreadHost.TryReadPreview"/> (0 аллокаций).
/// </summary>
public sealed class PreviewReply
{
    public const int IssueCapacity = 16;
    public const int AxisCapacity = 256;

    public long Id { get; internal set; }

    /// <summary>Тик ядра, по состоянию которого считано превью.</summary>
    public long Tick { get; internal set; }

    public int NetworkVersion { get; internal set; }

    public PreviewResult Result { get; internal set; }

    public PreviewIssue[] Issues { get; } = new PreviewIssue[IssueCapacity];

    /// <summary>Записано предупреждений (<c>min(Result.IssuesWritten, IssueCapacity)</c>); всего - <c>Result.IssueCount</c>.</summary>
    public int IssueCount { get; internal set; }

    public Vec2[] Axis { get; } = new Vec2[AxisCapacity];

    public int AxisCount { get; internal set; }

    public RejectReason Verdict => Result.Verdict;

    public ReadOnlySpan<PreviewIssue> IssueSpan => new(Issues, 0, IssueCount);

    public ReadOnlySpan<Vec2> AxisSpan => new(Axis, 0, AxisCount);

    /// <summary>Копия содержимого (без аллокаций; буферы одинаковой ёмкости).</summary>
    public void CopyFrom(PreviewReply src)
    {
        Id = src.Id;
        Tick = src.Tick;
        NetworkVersion = src.NetworkVersion;
        Result = src.Result;
        IssueCount = src.IssueCount;
        AxisCount = src.AxisCount;
        Array.Copy(src.Issues, Issues, src.IssueCount);
        Array.Copy(src.Axis, Axis, src.AxisCount);
    }
}
