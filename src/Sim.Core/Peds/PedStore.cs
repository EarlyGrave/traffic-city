using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Пешеходы (pedestrians.md §4.1): SoA по слотам пула <see cref="HandlePool"/> (index + generation), все массивы выделены по <c>max_peds</c>.
/// Человек - запись: ребро, тик входа на него, скорость в целых мм/тик, тик следующего события. Позиция - аналитическая функция
/// <c>d = S0Mm + VMmT * (t - T0)</c> и считается только для снапшота. Состояние (в хэше и сейве) - всё, кроме помеченного «производное».
/// <para>
/// Ребро и шаги пути кодируются одним <c>int</c>: <c>id | StepCross | StepBwd</c>. <see cref="StepCross"/> (бит 31) - ребро-переход,
/// <see cref="StepBwd"/> (бит 30) - идёт от <c>B</c> к <c>A</c>. Расстояние <c>d</c> считается вдоль хода от узла входа на ребро.
/// У <c>PendingPath</c> в <c>Edge</c> лежит стартовая точка ноги (ребро тротуара без битов), в <c>S0Mm</c> - смещение от <c>A</c>.
/// </para>
/// </summary>
internal sealed class PedStore
{
    public const int StepCross = unchecked((int)0x80000000);
    public const int StepBwd = 0x40000000;
    public const int StepMask = 0x3FFFFFFF;

    public readonly HandlePool Pool;
    public readonly int Capacity;
    public readonly int MaxSteps;

    public readonly byte[] State;          // PedState
    public readonly byte[] Leg;            // PedLeg
    public readonly int[] Resident;        // житель-владелец
    public readonly int[] Edge;            // текущее ребро (кодирование выше); AtCrossing - ребро перехода, который ждёт
    public readonly long[] T0;             // тик входа на ребро (Walking, Crossing) или начала ожидания (PendingPath, AtCrossing, AtStop)
    public readonly int[] S0Mm;            // смещение входа на ребро вдоль хода, мм
    public readonly int[] VMmT;            // скорость, мм/тик
    public readonly long[] EvTick;         // тик конца ребра или -1
    public readonly int[] PathCursor;      // индекс текущего шага
    public readonly int[] PathCount;       // шагов в пути
    public readonly int[] GoalEdge;        // конец ноги: ребро тротуара и смещение от A, мм
    public readonly int[] GoalSMm;
    public readonly int[] Next;            // интрузивный список: очередь перехода, очередь остановки или пассажиры автобуса
    public readonly int[] Bus;             // индекс машины-автобуса (OnBus) или -1
    public readonly int[] AlightStop;      // индекс остановки высадки в BusRouteStops или -1
    public readonly long[] TripTick;       // старт поездки
    public readonly int[] Path;            // арена путей: блок [MaxSteps] на слот

    // ---- производные (вне хэша и сейва) ----
    public readonly int[] EdgePrev;        // списки пешеходов Walking/Crossing по рёбрам
    public readonly int[] EdgeNext;
    public readonly int[] EdgeHead;        // [рёбер]
    public readonly ushort[] EdgeCount;    // [рёбер] число пешеходов в списке ребра (насыщение 65535) - heatmap «вдали»
    public readonly int[] RenderHint;      // подсказка куска полилинии (снапшот)
    public readonly int[] QueueRank;       // номер в очереди перехода/остановки (снапшот)

    public PedStore(int capacity, int maxSteps, int edgeCapacity)
    {
        Capacity = capacity;
        MaxSteps = maxSteps;
        Pool = new HandlePool(capacity);
        State = new byte[capacity];
        Leg = new byte[capacity];
        Resident = new int[capacity];
        Edge = new int[capacity];
        T0 = new long[capacity];
        S0Mm = new int[capacity];
        VMmT = new int[capacity];
        EvTick = new long[capacity];
        PathCursor = new int[capacity];
        PathCount = new int[capacity];
        GoalEdge = new int[capacity];
        GoalSMm = new int[capacity];
        Next = new int[capacity];
        Bus = new int[capacity];
        AlightStop = new int[capacity];
        TripTick = new long[capacity];
        Path = new int[checked(capacity * maxSteps)];
        EdgePrev = new int[capacity];
        EdgeNext = new int[capacity];
        EdgeHead = new int[Math.Max(1, edgeCapacity)];
        EdgeCount = new ushort[EdgeHead.Length];
        RenderHint = new int[capacity];
        QueueRank = new int[capacity];
        Array.Fill(EdgeHead, -1);
        Array.Fill(EdgePrev, -1);
        Array.Fill(EdgeNext, -1);
        Array.Fill(Resident, -1);
        Array.Fill(Edge, -1);
        Array.Fill(EvTick, -1L);
        Array.Fill(GoalEdge, -1);
        Array.Fill(Next, -1);
        Array.Fill(Bus, -1);
        Array.Fill(AlightStop, -1);
    }

    public int HighWater => Pool.HighWater;

    public int AliveCount => Pool.AliveCount;

    public bool IsAlive(PedHandle h) => h.Generation != 0 && Pool.IsAlive(h.Index, h.Generation);

    public PedHandle HandleOf(int i) => new(i, Pool.GenerationOf(i));

    /// <summary>Выдаёт слот и приводит поля к канону «новый»; false - пул исчерпан.</summary>
    public bool TryAllocate(out int index)
    {
        if (!Pool.TryAllocate(out index, out _))
        {
            return false;
        }

        ResetSlot(index);
        return true;
    }

    /// <summary>Освобождает слот (список ребра и очереди пешеход уже покинул) и приводит поля к канону «мёртвый» (ссылки -1, остальное 0).</summary>
    public void Release(int i)
    {
        Pool.Free(i);
        ResetSlot(i);
    }

    private void ResetSlot(int i)
    {
        State[i] = 0;
        Leg[i] = 0;
        Resident[i] = -1;
        Edge[i] = -1;
        T0[i] = 0;
        S0Mm[i] = 0;
        VMmT[i] = 0;
        EvTick[i] = -1;
        PathCursor[i] = 0;
        PathCount[i] = 0;
        GoalEdge[i] = -1;
        GoalSMm[i] = 0;
        Next[i] = -1;
        Bus[i] = -1;
        AlightStop[i] = -1;
        TripTick[i] = 0;
        EdgePrev[i] = -1;
        EdgeNext[i] = -1;
        RenderHint[i] = 0;
        QueueRank[i] = 0;
        Array.Clear(Path, i * MaxSteps, MaxSteps);
    }

    // ---- списки пешеходов по рёбрам (производное; Walking и Crossing) ----

    public void AddToEdge(int p, int edge)
    {
        int head = EdgeHead[edge];
        EdgePrev[p] = -1;
        EdgeNext[p] = head;
        if (head >= 0)
        {
            EdgePrev[head] = p;
        }

        EdgeHead[edge] = p;
        if (EdgeCount[edge] < ushort.MaxValue)
        {
            EdgeCount[edge]++;
        }
    }

    public void RemoveFromEdge(int p, int edge)
    {
        int prev = EdgePrev[p];
        int next = EdgeNext[p];
        if (prev >= 0)
        {
            EdgeNext[prev] = next;
        }
        else
        {
            EdgeHead[edge] = next;
        }

        if (next >= 0)
        {
            EdgePrev[next] = prev;
        }

        EdgePrev[p] = -1;
        EdgeNext[p] = -1;
        if (EdgeCount[edge] > 0 && EdgeCount[edge] < ushort.MaxValue)
        {
            EdgeCount[edge]--;
        }
    }

    /// <summary>Сбрасывает списки рёбер (перед пересборкой из состояния).</summary>
    public void ClearEdgeLists()
    {
        Array.Fill(EdgeHead, -1);
        Array.Clear(EdgeCount);
        Array.Fill(EdgePrev, -1);
        Array.Fill(EdgeNext, -1);
    }
}
