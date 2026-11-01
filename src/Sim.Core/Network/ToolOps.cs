using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>Вид длительной операции инструмента (player-tools.md §4.3). T4: <c>RemoveSegment</c>; T5: <c>ShrinkLanes</c> (правка полос занятого сегмента).</summary>
internal enum ToolOpKind : byte
{
    None = 0,
    RemoveSegment = 1,

    /// <summary>
    /// <c>UpgradeRoad</c> занятого сегмента (T5, player-tools.md §4.5): убираемые полосы <c>Closed | Draining</c>, после их опустения обречённые connector'ы концов закрываются и
    /// ждут опустения, затем <c>ReshapeSegment</c> (перенос машин уцелевших полос). Цель правки упакована в <see cref="ToolOps.TargetGen"/> (<see cref="ToolOps.PackReshape"/>):
    /// поколение не нужно - сегмент под операцией не удаляется и не правится (<c>Busy</c>).
    /// </summary>
    ShrinkLanes = 2,
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

    /// <summary>Запись операции любого вида над сегментом <paramref name="seg"/> или -1.</summary>
    public int FindAny(int seg)
    {
        for (int k = 0; k < Count; k++)
        {
            if (TargetIndex[k] == seg)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>Запись операции <see cref="ToolOpKind.ShrinkLanes"/> над сегментом <paramref name="seg"/> или -1.</summary>
    public int FindShrink(int seg)
    {
        for (int k = 0; k < Count; k++)
        {
            if (Kind[k] == (byte)ToolOpKind.ShrinkLanes && TargetIndex[k] == seg)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>
    /// Цель <see cref="ToolOpKind.ShrinkLanes"/> в одном неотрицательном <c>int</c> (столбец <c>ToolOpTargetGen</c>, без смены формата сейва): биты 0-3 полосы A-&gt;B, 4-7 полосы B-&gt;A,
    /// бит 8 тип дороги, бит 9 <c>Mirrored</c>, биты 10-30 профиль + 1 (0 - без профиля).
    /// </summary>
    public static int PackReshape(int lanesAB, int lanesBA, RoadKind kind, int profile, bool mirrored) =>
        (lanesAB & 15) | ((lanesBA & 15) << 4) | (((int)kind & 1) << 8) | ((mirrored ? 1 : 0) << 9) | ((profile + 1) << 10);

    public static void UnpackReshape(int packed, out int lanesAB, out int lanesBA, out RoadKind kind, out int profile, out bool mirrored)
    {
        lanesAB = packed & 15;
        lanesBA = (packed >> 4) & 15;
        kind = (RoadKind)((packed >> 8) & 1);
        mirrored = ((packed >> 9) & 1) != 0;
        profile = (packed >> 10) - 1;
    }

    /// <summary>Запись операции <see cref="ToolOpKind.RemoveSegment"/> над сегментом <paramref name="seg"/> или -1.</summary>
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
    public ReadOnlySpan<ToolOpView> Views(NetworkStore n)
    {
        _views ??= new ToolOpView[Capacity];
        for (int k = 0; k < Count; k++)
        {
            _views[k] = ViewOf(k, n);
        }

        return new ReadOnlySpan<ToolOpView>(_views, 0, Count);
    }

    /// <summary>Представление записи для клиента (индикатор на дороге): <c>Kind</c> - команда, начавшая операцию (<c>RemoveRoad</c> / <c>UpgradeRoad</c>), <c>Target</c> - сегмент.</summary>
    public ToolOpView ViewOf(int at, NetworkStore n)
    {
        bool shrink = Kind[at] == (byte)ToolOpKind.ShrinkLanes;
        int seg = TargetIndex[at];
        return new ToolOpView
        {
            Id = Id[at],
            Kind = shrink ? CommandKind.UpgradeRoad : CommandKind.RemoveRoad,
            Target = new EntityRef(seg, shrink ? n.Segments[seg].Generation : TargetGen[at]),
            Remaining = Remaining[at],
            StartTick = StartTick[at],
        };
    }

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
