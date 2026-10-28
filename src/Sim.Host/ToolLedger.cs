using System;
using System.Collections.Generic;
using Sim.Core;
using Sim.Core.Api;

namespace Sim.Host;

/// <summary>Группа команд действия, поставленная в ядро и ждущая первого <c>Step</c> (поток симуляции).</summary>
internal sealed class ToolInFlight
{
    public uint ActionId;
    public ToolActionKind Kind;
    public uint FirstTag;
    public int Queued;
    public int NotQueued;
    public long Tick;
    public Command[] Forward = Array.Empty<Command>();

    /// <summary>Описание пункта стека (для тултипа и тоста): у undo/redo берётся из снятого пункта, у прямого действия считается по группе.</summary>
    public ActionMeta Meta;

    /// <summary>Прямое действие дописывается в предыдущий пункт undo, если тот создан действием с этим id (цепочка дорог = одна группа).</summary>
    public uint MergeInto;
}

/// <summary>Что делает пункт стека undo/redo: тип первой команды прямого действия и число команд в группе.</summary>
public readonly record struct ActionMeta(CommandKind Kind, int Pieces);

/// <summary>
/// Стек undo/redo и разбор итогов действий инструментов на потоке симуляции (player-tools.md §8.3, T13). Логика повторяет <c>Sim.Scenarios.Tools.UndoStack</c>,
/// но без собственного <c>Step</c>: группы ставит хост между шагами, итог читается из <c>Events</c>/<c>Inverses</c> после шага (записи живут весь <c>Step(n)</c>).
/// Handle'ы в пунктах стеков правятся по событиям <c>*Remapped</c> (<see cref="CommandRefs.Remap"/>). Не потокобезопасен: только поток симуляции.
/// </summary>
internal sealed class ToolLedger
{
    public const int Depth = 100;

    private readonly List<Command[]> _undo = new();
    private readonly List<Command[]> _redo = new();
    private readonly List<ActionMeta> _undoMeta = new();
    private readonly List<ActionMeta> _redoMeta = new();
    private uint _lastDoId;
    private readonly List<RemapEntry> _remaps = new();

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>Пункт, который отменит следующий undo (default, если стек пуст).</summary>
    public ActionMeta NextUndo => _undoMeta.Count > 0 ? _undoMeta[^1] : default;

    public ActionMeta NextRedo => _redoMeta.Count > 0 ? _redoMeta[^1] : default;

    /// <summary>Группа обратных команд для undo/redo (снимается со стека сразу); null - стек пуст.</summary>
    public Command[]? Take(ToolActionKind kind, out ActionMeta meta)
    {
        List<Command[]> from = kind == ToolActionKind.Undo ? _undo : _redo;
        List<ActionMeta> fromMeta = kind == ToolActionKind.Undo ? _undoMeta : _redoMeta;
        if (from.Count == 0)
        {
            meta = default;
            return null;
        }

        Command[] g = from[^1];
        meta = fromMeta[^1];
        from.RemoveAt(from.Count - 1);
        fromMeta.RemoveAt(fromMeta.Count - 1);
        return g;
    }

    /// <summary>Разбирает итоги шага для всех действий в полёте и дописывает acks. Пункты стеков сначала правятся перенумерацией этого шага.</summary>
    public void Harvest(Simulation sim, List<ToolInFlight> flight, Action<ToolAck> emit)
    {
        ReadOnlySpan<SimEvent> events = sim.Events;
        _remaps.Clear();
        foreach (SimEvent e in events)
        {
            CollectRemap(sim, in e, flight);
        }

        ApplyRemaps(_undo);
        ApplyRemaps(_redo);

        ReadOnlySpan<InverseRecord> recs = sim.Inverses;
        ReadOnlySpan<Command> ic = sim.InverseCommands;
        int version = sim.Network.Version;
        foreach (ToolInFlight a in flight)
        {
            int applied = 0;
            int rejected = a.NotQueued;
            int lots = 0;
            RejectReason first = a.NotQueued > 0 ? RejectReason.Capacity : RejectReason.None;
            CommandKind firstKind = 0;
            uint lastTag = a.FirstTag + (uint)a.Queued - 1;
            foreach (SimEvent e in events)
            {
                if (a.Queued == 0 || e.Tag < a.FirstTag || e.Tag > lastTag)
                {
                    continue;
                }

                if (e.Kind == SimEventKind.CommandApplied)
                {
                    applied++;
                }
                else if (e.Kind == SimEventKind.CommandRejected)
                {
                    rejected++;
                    if (first == RejectReason.None)
                    {
                        first = (RejectReason)e.A;
                        firstKind = (CommandKind)e.B;
                    }
                }
                else if (e.Kind == SimEventKind.ZoneLotsLost)
                {
                    lots += e.B;
                }
            }

            int unknown = a.Queued - (applied + rejected - a.NotQueued);
            if (unknown > 0)
            {
                // событий не хватило (EventsOverflow): считаем неподтверждёнными
                rejected += unknown;
                if (first == RejectReason.None)
                {
                    first = RejectReason.Capacity;
                }
            }

            bool truncated = false;
            int total = 0;
            for (int i = 0; i < recs.Length; i++)
            {
                if (recs[i].Tag >= a.FirstTag && recs[i].Tag <= lastTag && a.Queued > 0)
                {
                    truncated |= (recs[i].Flags & (InverseFlags.Truncated | InverseFlags.NoInverse)) != 0;
                    total += recs[i].Count;
                }
            }

            Command[]? inverse = null;
            if (!truncated && total > 0)
            {
                inverse = new Command[total];
                int at = 0;
                for (int i = recs.Length - 1; i >= 0; i--)
                {
                    if (recs[i].Tag >= a.FirstTag && recs[i].Tag <= lastTag)
                    {
                        ic.Slice(recs[i].First, recs[i].Count).CopyTo(inverse.AsSpan(at));
                        at += recs[i].Count;
                    }
                }
            }

            ActionMeta meta = a.Kind == ToolActionKind.Do ? new ActionMeta(a.Forward.Length > 0 ? a.Forward[0].Kind : CommandKind.None, a.Forward.Length) : a.Meta;
            switch (a.Kind)
            {
                case ToolActionKind.Do:
                    _redo.Clear();
                    _redoMeta.Clear();
                    if (inverse != null && a.MergeInto != 0 && a.MergeInto == _lastDoId && _undo.Count > 0)
                    {
                        // цепочка: обратная нового куска идёт первой (откатываем с конца), затем обратная накопленного
                        Command[] prev = _undo[^1];
                        var joined = new Command[inverse.Length + prev.Length];
                        inverse.CopyTo(joined, 0);
                        prev.CopyTo(joined, inverse.Length);
                        _undo[^1] = joined;
                        _undoMeta[^1] = new ActionMeta(_undoMeta[^1].Kind, _undoMeta[^1].Pieces + meta.Pieces);
                    }
                    else
                    {
                        Push(_undo, _undoMeta, inverse, meta);
                    }

                    _lastDoId = inverse != null ? a.ActionId : 0;
                    break;
                case ToolActionKind.Undo:
                    Push(_redo, _redoMeta, inverse, meta);
                    _lastDoId = 0;
                    break;
                default:
                    Push(_undo, _undoMeta, inverse, meta);
                    _lastDoId = 0;
                    break;
            }

            emit(new ToolAck
            {
                ActionId = a.ActionId,
                Kind = a.Kind,
                Tick = a.Tick,
                Applied = applied,
                Rejected = rejected,
                FirstReason = first,
                FirstKind = firstKind,
                Recorded = inverse != null,
                Truncated = truncated,
                ZoneLotsLost = lots,
                NetworkVersion = version,
                UndoDepth = _undo.Count,
                RedoDepth = _redo.Count,
                Subject = meta.Kind,
                Pieces = meta.Pieces,
            });
        }
    }

    private static void Push(List<Command[]> list, List<ActionMeta> metas, Command[]? g, ActionMeta meta)
    {
        if (g == null)
        {
            return;
        }

        list.Add(g);
        metas.Add(meta);
        if (list.Count > Depth)
        {
            list.RemoveAt(0);
            metas.RemoveAt(0);
        }
    }

    // Событие перенумерации -> запись таблицы. Поколение нового - из снимка сети, старое - из команды-источника (по Tag события).
    private void CollectRemap(Simulation sim, in SimEvent e, List<ToolInFlight> flight)
    {
        EntityKind kind;
        switch (e.Kind)
        {
            case SimEventKind.SegmentRemapped:
                kind = EntityKind.Segment;
                break;
            case SimEventKind.NodeRemapped:
                kind = EntityKind.Node;
                break;
            case SimEventKind.BusStopRemapped:
                kind = EntityKind.BusStop;
                break;
            case SimEventKind.BusRouteRemapped:
                kind = EntityKind.BusRoute;
                break;
            default:
                return;
        }

        NetworkView v = sim.Network;
        EntityRef now = kind switch
        {
            EntityKind.Segment => v.SegmentRef(e.B),
            EntityKind.Node => v.NodeRef(e.B),
            EntityKind.BusStop => v.BusStopRef(e.B),
            _ => v.BusRouteRef(e.B),
        };
        int oldGen = 0;
        foreach (ToolInFlight a in flight)
        {
            long at = (long)e.Tag - a.FirstTag;
            if (at >= 0 && at < a.Queued)
            {
                oldGen = OldGeneration(in a.Forward[(int)at], kind, e.A);
                break;
            }
        }

        _remaps.Add(new RemapEntry { Kind = kind, OldIndex = e.A, OldGeneration = oldGen, NewIndex = now.Index, NewGeneration = now.Generation });
    }

    private static int OldGeneration(in Command c, EntityKind kind, int index)
    {
        switch (c.Kind)
        {
            case CommandKind.BuildRoad:
                if (kind == EntityKind.Segment && c.BuildRoad.RestoreOf.Index == index)
                {
                    return c.BuildRoad.RestoreOf.Generation;
                }

                if (kind == EntityKind.Node)
                {
                    return c.BuildRoad.RestoreNodeA.Index == index ? c.BuildRoad.RestoreNodeA.Generation : c.BuildRoad.RestoreNodeB.Index == index ? c.BuildRoad.RestoreNodeB.Generation : 0;
                }

                break;
            case CommandKind.InsertBusStop:
                return kind == EntityKind.BusStop && c.InsertBusStop.RestoreOf.Index == index ? c.InsertBusStop.RestoreOf.Generation : 0;
            case CommandKind.CreateBusRoute:
                return kind == EntityKind.BusRoute && c.CreateBusRoute.RestoreOf.Index == index ? c.CreateBusRoute.RestoreOf.Generation : 0;
        }

        return 0;
    }

    private void ApplyRemaps(List<Command[]> list)
    {
        if (_remaps.Count == 0)
        {
            return;
        }

        foreach (Command[] g in list)
        {
            for (int i = 0; i < g.Length; i++)
            {
                for (int k = 0; k < _remaps.Count; k++)
                {
                    RemapEntry one = _remaps[k];
                    CommandRefs.Remap(ref g[i], new RemapTable(new ReadOnlySpan<RemapEntry>(in one)));
                }
            }
        }
    }
}
