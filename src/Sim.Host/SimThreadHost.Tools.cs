using System;
using System.Collections.Generic;
using System.Threading;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Host;

// Инструменты игрока (T13, ADR-0023/0024, player-tools.md §7-§8): превью и действия (группы команд + undo/redo) через хост.
// Главный поток: TrySubmitAction/TryUndo/TryRedo/TryRequestPreview/TryReadToolAck/TryReadPreview. Поток симуляции: между Step.
public sealed partial class SimThreadHost
{
    /// <summary>Старший бит тега: теги команд инструментов, назначенные хостом. Теги обычных команд (<see cref="TryEnqueue"/>) клиент задаёт сам и не должен ставить этот бит.</summary>
    public const uint ToolTagBase = 0x8000_0000u;

    /// <summary>Сколько действий может ждать поток симуляции (больше - <see cref="TrySubmitAction"/> вернёт 0).</summary>
    public const int ToolQueueCapacity = 64;

    private sealed class ToolItem
    {
        public uint ActionId;
        public ToolActionKind Kind;
        public uint FirstTag;
        public Command[] Cmds = Array.Empty<Command>();
    }

    private readonly Queue<ToolItem> _toolQueue = new();
    private readonly object _toolLock = new();
    private readonly SpscRing<ToolAck> _toolAcks = new(64);
    private readonly ToolLedger _ledger = new();
    private readonly List<ToolInFlight> _inFlight = new();
    private uint _actionId;
    private long _toolTag;
    private int _undoDepth;
    private int _redoDepth;
    private long _toolAcksDropped;

    // превью: почтовый ящик «последний выигрывает» (запрос) и последний ответ; обе стороны держат замок на копию в микросекунды
    private readonly object _previewLock = new();
    private bool _previewPending;
    private long _previewReqId;
    private Command _previewCmd;
    private PreviewOptions _previewFlags;
    private readonly PreviewReply _previewDone = new();
    private readonly PreviewReply _previewWork = new();
    private long _previewTakenId;
    private long _previewNextId;
    private long _previewsServed;

    /// <summary>Глубина стека undo (после последнего <see cref="ToolAck"/>).</summary>
    public int UndoDepth => Volatile.Read(ref _undoDepth);

    public int RedoDepth => Volatile.Read(ref _redoDepth);

    /// <summary>Подтверждения, не влезшие в кольцо (клиент их не читает).</summary>
    public long ToolAcksDropped => Volatile.Read(ref _toolAcksDropped);

    /// <summary>Превью, выполненные потоком симуляции (метрика).</summary>
    public long PreviewsServed => Volatile.Read(ref _previewsServed);

    /// <summary>
    /// Действие игрока - группа команд (протяжка цепочки дорог - одна группа). Хост ставит весь набор в ядро атомарно (один <c>Step</c>), назначает теги
    /// (<see cref="ToolTagBase"/>), пишет журнал, по итогам кладёт обратную в стек undo и сбрасывает redo. Возвращает id действия (&gt; 0) для сопоставления
    /// с <see cref="ToolAck"/>, 0 - очередь полна, группа пуста или хост не работает. Выделяет копию группы (раз на действие игрока, не на кадр).
    /// </summary>
    public uint TrySubmitAction(ReadOnlySpan<Command> forward)
    {
        if (forward.IsEmpty)
        {
            return 0;
        }

        return Submit(ToolActionKind.Do, forward);
    }

    /// <summary>Отменить последнее действие (обратная группа из стека хоста). 0 - очередь полна. Пустой стек даёт <see cref="ToolAck.Nothing"/>.</summary>
    public uint TryUndo() => Submit(ToolActionKind.Undo, default);

    public uint TryRedo() => Submit(ToolActionKind.Redo, default);

    private uint Submit(ToolActionKind kind, ReadOnlySpan<Command> forward)
    {
        if (State != HostState.Running)
        {
            return 0;
        }

        var item = new ToolItem { Kind = kind };
        if (!forward.IsEmpty)
        {
            item.Cmds = forward.ToArray();
        }

        lock (_toolLock)
        {
            if (_toolQueue.Count >= ToolQueueCapacity)
            {
                return 0;
            }

            item.ActionId = ++_actionId;
            if (kind == ToolActionKind.Do)
            {
                item.FirstTag = ReserveTags(item.Cmds.Length);
            }

            _toolQueue.Enqueue(item);
        }

        _wake.Set();
        return item.ActionId;
    }

    private uint ReserveTags(int n) => ToolTagBase | (uint)((Interlocked.Add(ref _toolTag, n) - n + 1) & 0x7FFF_FFFF);

    /// <summary>Следующее подтверждение действия (в порядке применения); false - пока нет.</summary>
    public bool TryReadToolAck(out ToolAck ack) => _toolAcks.TryRead(out ack);

    /// <summary>
    /// Запрос превью: «последний выигрывает» (неотвеченный предыдущий затирается) - клиенту достаточно слать на смену состояния, не чаще одного за тик.
    /// Считается потоком симуляции между <c>Step</c> по применённому состоянию ядра. Возвращает id (&gt; 0).
    /// </summary>
    public long TryRequestPreview(in Command cmd, PreviewOptions flags = PreviewOptions.None)
    {
        if (State != HostState.Running)
        {
            return 0;
        }

        long id;
        lock (_previewLock)
        {
            id = ++_previewNextId;
            _previewCmd = cmd;
            _previewFlags = flags;
            _previewReqId = id;
            _previewPending = true;
        }

        _wake.Set();
        return id;
    }

    /// <summary>
    /// Копирует последний ответ превью в <paramref name="into"/>, если он новее прочитанного раньше. false - нового нет.
    /// Ответ может относиться к более раннему запросу, чем последний (сравнивать <see cref="PreviewReply.Id"/>).
    /// </summary>
    public bool TryReadPreview(PreviewReply into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_previewLock)
        {
            if (_previewDone.Id == 0 || _previewDone.Id <= _previewTakenId)
            {
                return false;
            }

            into.CopyFrom(_previewDone);
            _previewTakenId = _previewDone.Id;
            return true;
        }
    }

    // ---------------------------------------------------------------- поток симуляции

    private void DrainTools(long tick)
    {
        while (true)
        {
            ToolItem item;
            lock (_toolLock)
            {
                if (_toolQueue.Count == 0)
                {
                    return;
                }

                // undo/redo читают стек, который пополняется только по итогам шага: ждём, пока действия в полёте не разобраны
                if (_toolQueue.Peek().Kind != ToolActionKind.Do && _inFlight.Count > 0)
                {
                    return;
                }

                item = _toolQueue.Dequeue();
            }

            Command[] cmds = item.Cmds;
            if (item.Kind != ToolActionKind.Do)
            {
                Command[]? g = _ledger.Take(item.Kind);
                if (g == null)
                {
                    EmitAck(new ToolAck { ActionId = item.ActionId, Kind = item.Kind, Tick = tick, Nothing = true, UndoDepth = _ledger.UndoCount, RedoDepth = _ledger.RedoCount });
                    continue;
                }

                cmds = g;
                item.FirstTag = ReserveTags(cmds.Length);
            }

            var fl = new ToolInFlight { ActionId = item.ActionId, Kind = item.Kind, FirstTag = item.FirstTag, Tick = tick, Forward = cmds };
            for (int i = 0; i < cmds.Length; i++)
            {
                Command c = cmds[i];
                c.Tag = (uint)(item.FirstTag + (uint)i);
                if (_sim.Enqueue(in c))
                {
                    fl.Queued++;
                    _commandsApplied++;
                    lock (_journalLock)
                    {
                        _journal.Add(new JournalEntry(tick, in c));
                    }
                }
                else
                {
                    _commandsRejected++;
                    fl.NotQueued += cmds.Length - i;
                    break;
                }
            }

            _inFlight.Add(fl);
        }
    }

    private void HarvestTools()
    {
        if (_inFlight.Count == 0)
        {
            return;
        }

        _ledger.Harvest(_sim, _inFlight, EmitAck);
        _inFlight.Clear();
        Volatile.Write(ref _undoDepth, _ledger.UndoCount);
        Volatile.Write(ref _redoDepth, _ledger.RedoCount);
    }

    private void EmitAck(ToolAck ack)
    {
        if (!_toolAcks.TryWrite(in ack))
        {
            Interlocked.Increment(ref _toolAcksDropped);
        }

        Volatile.Write(ref _undoDepth, ack.UndoDepth);
        Volatile.Write(ref _redoDepth, ack.RedoDepth);
    }

    private void ServePreview()
    {
        Command cmd;
        PreviewOptions flags;
        long id;
        lock (_previewLock)
        {
            if (!_previewPending)
            {
                return;
            }

            _previewPending = false;
            cmd = _previewCmd;
            flags = _previewFlags;
            id = _previewReqId;
        }

        PreviewReply w = _previewWork;
        _sim.Preview(in cmd, out PreviewResult r, w.Issues, w.Axis, flags);
        w.Result = r;
        w.Id = id;
        w.Tick = _sim.Tick;
        w.NetworkVersion = _sim.Network.Version;
        w.IssueCount = Math.Min(r.IssuesWritten, PreviewReply.IssueCapacity);
        w.AxisCount = Math.Min(r.AxisPointCount, PreviewReply.AxisCapacity);
        lock (_previewLock)
        {
            _previewDone.CopyFrom(w);
        }

        Interlocked.Increment(ref _previewsServed);
    }
}
