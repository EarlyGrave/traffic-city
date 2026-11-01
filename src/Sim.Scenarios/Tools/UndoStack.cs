using System;
using System.Collections.Generic;
using Sim.Core;
using Sim.Core.Api;

namespace Sim.Scenarios.Tools;

/// <summary>Итог действия стека: сколько команд применено/отклонено, первая причина отказа, записан ли пункт стека.</summary>
public readonly record struct UndoResult(int Applied, int Rejected, RejectReason FirstReason, bool Recorded, bool Truncated, CommandKind FirstKind = 0)
{
    public bool Ok => Rejected == 0 && !Truncated;
}

/// <summary>
/// Стек undo/redo хоста (player-tools.md §8.3). Пункт = группа обратных команд ядра (порядок записей группы - обратный прямому); действие игрока = группа прямых команд
/// (протяжка цепочки дорог - одна группа). Ядро про undo не знает: обратные - обычные команды, они проходят валидацию и журнал. Группа ставится в очередь подряд и
/// применяется одним <c>Step(1)</c> (scratch-перенумерация ядра живёт одну фазу 1). Handle'ы во всех пунктах правятся по событиям <c>*Remapped</c> (<see cref="CommandRefs.Remap"/>).
/// Обратная, которую ядро отклонило, снимает пункт, остальное из группы применяется (undo логический: сеть и настройки, не мгновенное состояние).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Имя закреплено спекой player-tools.md §8.3; это не System.Collections.Stack.")]
public sealed class UndoStack
{
    public const int DefaultDepth = 100;

    private readonly int _depth;
    private readonly List<Command[]> _undo = new();
    private readonly List<Command[]> _redo = new();
    private readonly List<RemapEntry> _remaps = new();
    private uint _tag;

    public UndoStack(int depth = DefaultDepth)
    {
        _depth = Math.Max(1, depth);
    }

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Выполнить действие игрока (группа прямых команд, один <c>Step(1)</c>); обратные записи ядра становятся пунктом undo, redo очищается.</summary>
    public UndoResult Execute(Simulation sim, ReadOnlySpan<Command> forward)
    {
        UndoResult r = Run(sim, forward, out Command[]? inverse);
        _redo.Clear();
        if (inverse != null)
        {
            Push(_undo, inverse);
        }

        return r with { Recorded = inverse != null };
    }

    /// <summary>Отменить последнее действие; обратные от обратных (записи ядра) становятся пунктом redo.</summary>
    public UndoResult Undo(Simulation sim) => Pop(sim, _undo, _redo);

    /// <summary>Повторить отменённое; симметрично <see cref="Undo"/>.</summary>
    public UndoResult Redo(Simulation sim) => Pop(sim, _redo, _undo);

    private UndoResult Pop(Simulation sim, List<Command[]> from, List<Command[]> to)
    {
        if (from.Count == 0)
        {
            return default;
        }

        Command[] group = from[^1];
        from.RemoveAt(from.Count - 1);
        UndoResult r = Run(sim, group, out Command[]? inverse);
        if (inverse != null)
        {
            Push(to, inverse);
        }

        return r with { Recorded = inverse != null };
    }

    private void Push(List<Command[]> list, Command[] g)
    {
        list.Add(g);
        if (list.Count > _depth)
        {
            list.RemoveAt(0);
        }
    }

    private UndoResult Run(Simulation sim, ReadOnlySpan<Command> cmds, out Command[]? inverse)
    {
        inverse = null;
        uint firstTag = _tag + 1;
        int queued = 0;
        foreach (Command c0 in cmds)
        {
            Command c = c0;
            c.Tag = ++_tag;
            if (!sim.Enqueue(in c))
            {
                break;
            }

            queued++;
        }

        uint lastTag = firstTag + (uint)queued - 1;
        int notQueued = cmds.Length - queued;
        sim.Step(1);

        int applied = 0;
        int rejected = notQueued;
        RejectReason first = notQueued > 0 ? RejectReason.Capacity : RejectReason.None;
        CommandKind firstKind = 0;
        _remaps.Clear();
        foreach (SimEvent e in sim.Events)
        {
            if (e.Tag >= firstTag && e.Tag <= lastTag && queued > 0)
            {
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
            }

            CollectRemap(sim, in e, cmds, firstTag);
        }

        // Пункты стеков, уже лежащие там, правятся до добавления нового (новый построен по handle'ам после пересоздания).
        ApplyRemaps(_undo);
        ApplyRemaps(_redo);

        bool truncated = false;
        ReadOnlySpan<InverseRecord> recs = sim.Inverses;
        ReadOnlySpan<Command> ic = sim.InverseCommands;
        int total = 0;
        for (int i = 0; i < recs.Length; i++)
        {
            truncated |= (recs[i].Flags & (InverseFlags.Truncated | InverseFlags.NoInverse)) != 0;
            total += recs[i].Count;
        }

        if (!truncated && total > 0)
        {
            var g = new Command[total];
            int at = 0;
            for (int i = recs.Length - 1; i >= 0; i--)
            {
                ic.Slice(recs[i].First, recs[i].Count).CopyTo(g.AsSpan(at));
                at += recs[i].Count;
            }

            inverse = g;
        }

        return new UndoResult(applied, rejected, first, false, truncated, firstKind);
    }

    // Событие перенумерации -> запись таблицы. Поколение нового - из снимка сети, старое - из команды-источника (по Tag события).
    private void CollectRemap(Simulation sim, in SimEvent e, ReadOnlySpan<Command> cmds, uint firstTag)
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
        // Старое поколение берём из команды, вызвавшей пересоздание (событие несёт её Tag): индекс слота мог быть занят и освобождён несколько раз.
        int oldGen = 0;
        long at = (long)e.Tag - firstTag;
        if (at >= 0 && at < cmds.Length)
        {
            oldGen = OldGeneration(in cmds[(int)at], kind, e.A);
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
            case CommandKind.SplitRoad:
                if (kind == EntityKind.Segment)
                {
                    return c.SplitRoad.RestoreSeg1.Index == index ? c.SplitRoad.RestoreSeg1.Generation : c.SplitRoad.RestoreSeg2.Index == index ? c.SplitRoad.RestoreSeg2.Generation : 0;
                }

                return kind == EntityKind.Node && c.SplitRoad.RestoreNode.Index == index ? c.SplitRoad.RestoreNode.Generation : 0;
            case CommandKind.MergeAtNode:
                return kind == EntityKind.Segment && c.MergeAtNode.RestoreSeg.Index == index ? c.MergeAtNode.RestoreSeg.Generation : 0;
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
