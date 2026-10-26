using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>Вид длительной операции инструмента (player-tools.md §4.3). T4: <c>RemoveSegment</c>; <c>ShrinkLanes</c>, <c>NodeRebuild</c>, <c>SplitBox</c>, <c>MergeNode</c> - T5.</summary>
internal enum ToolOpKind : byte
{
    None = 0,
    RemoveSegment = 1,
}

/// <summary>Биты <c>ToolOps.Flags</c>.</summary>
internal static class ToolOpFlags
{
    /// <summary>Не удалять осиротевшие узлы (<c>RemoveRoadCmd.Flags</c> бит0 <c>KeepNodes</c>).</summary>
    public const byte KeepNodes = 1;
}

/// <summary>
/// Операции дренажа (4.3, T4; player-tools.md §4.3): SoA на <see cref="Capacity"/> записей, порядок - по возрастанию <see cref="Id"/> (монотонный). Элемент-цель закрыт для въезда
/// (<c>Closed</c>/<c>Closing</c>), операция ждёт, пока он опустеет, и тогда удаляет его (<c>ToolOpsPhase</c>, фаза 1а). Застрявший дренаж не форсируется: событие
/// <c>DrainStalled</c> раз в <c>drain_stall_s</c>, цель остаётся закрытой. Состояние - в хэше и сейве при <c>ToolsTouched = 1</c>; <see cref="Remaining"/> - производное (считается в 1а).
/// </summary>
internal sealed class ToolOps
{
    public const int DefaultCapacity = 256;

    /// <summary>Период без убыли <see cref="Remaining"/>, после которого выдаётся <c>DrainStalled</c> (60 с при 20 тиках/с).</summary>
    public const int StallTicks = 1200;

    public readonly int Capacity;
    public readonly int[] Id;
    public readonly byte[] Kind;
    public readonly int[] TargetIndex;
    public readonly int[] TargetGen;
    public readonly byte[] Flags;
    public readonly long[] StartTick;
    public readonly long[] LastProgressTick;
    public readonly long[] LastStallTick;
    public readonly int[] BestRemaining;
    public readonly int[] Remaining;   // производное (пересчитывает 1а); в хэше и сейве нет

    public int Count;
    public int NextId = 1;

    public ToolOps(int capacity)
    {
        Capacity = Math.Max(0, capacity);
        int c = Capacity;
        Id = new int[c];
        Kind = new byte[c];
        TargetIndex = new int[c];
        TargetGen = new int[c];
        Flags = new byte[c];
        StartTick = new long[c];
        LastProgressTick = new long[c];
        LastStallTick = new long[c];
        BestRemaining = new int[c];
        Remaining = new int[c];
    }

    /// <summary>Запись операции над сегментом <paramref name="seg"/> или -1.</summary>
    public int FindSegment(int seg)
    {
        for (int k = 0; k < Count; k++)
        {
            if (Kind[k] == (byte)ToolOpKind.RemoveSegment && TargetIndex[k] == seg)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>Добавляет операцию в конец (id растёт); false - нет места.</summary>
    public bool TryAdd(ToolOpKind kind, int targetIndex, int targetGen, byte flags, long tick, int remaining, out int at)
    {
        at = -1;
        if (Count >= Capacity)
        {
            return false;
        }

        at = Count++;
        Id[at] = NextId++;
        Kind[at] = (byte)kind;
        TargetIndex[at] = targetIndex;
        TargetGen[at] = targetGen;
        Flags[at] = flags;
        StartTick[at] = tick;
        LastProgressTick[at] = tick;
        LastStallTick[at] = tick;
        BestRemaining[at] = remaining;
        Remaining[at] = remaining;
        return true;
    }

    /// <summary>Убирает запись <paramref name="at"/>, сохраняя порядок остальных.</summary>
    public void RemoveAt(int at)
    {
        for (int k = at; k + 1 < Count; k++)
        {
            Id[k] = Id[k + 1];
            Kind[k] = Kind[k + 1];
            TargetIndex[k] = TargetIndex[k + 1];
            TargetGen[k] = TargetGen[k + 1];
            Flags[k] = Flags[k + 1];
            StartTick[k] = StartTick[k + 1];
            LastProgressTick[k] = LastProgressTick[k + 1];
            LastStallTick[k] = LastStallTick[k + 1];
            BestRemaining[k] = BestRemaining[k + 1];
            Remaining[k] = Remaining[k + 1];
        }

        Count--;
        Id[Count] = 0;
        Kind[Count] = 0;
        TargetIndex[Count] = 0;
        TargetGen[Count] = 0;
        Flags[Count] = 0;
        StartTick[Count] = 0;
        LastProgressTick[Count] = 0;
        LastStallTick[Count] = 0;
        BestRemaining[Count] = 0;
        Remaining[Count] = 0;
    }

    private ToolOpView[]? _views;

    /// <summary>Все активные операции для клиента (массив выделяется при первом чтении и живёт дальше; без аллокаций в установившемся режиме).</summary>
    public ReadOnlySpan<ToolOpView> Views()
    {
        _views ??= new ToolOpView[Capacity];
        for (int k = 0; k < Count; k++)
        {
            _views[k] = ViewOf(k);
        }

        return new ReadOnlySpan<ToolOpView>(_views, 0, Count);
    }

    /// <summary>Представление записи для клиента (индикатор на дороге).</summary>
    public ToolOpView ViewOf(int at) => new()
    {
        Id = Id[at],
        Kind = CommandKind.RemoveRoad,
        Target = new EntityRef(TargetIndex[at], TargetGen[at]),
        Remaining = Remaining[at],
        StartTick = StartTick[at],
    };

    /// <summary>Состояние операций в хэше (только при <c>ToolsTouched = 1</c>): порядок и поля фиксированы.</summary>
    internal void AddToHash(ref Fnv1a64 h)
    {
        h.Add(Count);
        h.Add(NextId);
        for (int k = 0; k < Count; k++)
        {
            h.Add(Id[k]);
            h.Add(Kind[k]);
            h.Add(TargetIndex[k]);
            h.Add(TargetGen[k]);
            h.Add(Flags[k]);
            h.Add(StartTick[k]);
            h.Add(LastProgressTick[k]);
            h.Add(LastStallTick[k]);
            h.Add(BestRemaining[k]);
        }
    }
}
