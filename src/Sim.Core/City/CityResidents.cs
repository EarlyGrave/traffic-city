using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>Биты <see cref="CityResidents.Flags"/>.</summary>
internal static class ResidentFlags
{
    public const byte HasCar = 1;
    public const byte Labour = 2;     // в рабочей силе (ищет или имеет работу)
    public const byte Evicted = 4;    // дом заброшен: переезд с приоритетом (§8.2)
    public const byte Leaving = 8;    // уедет из города при ближайшем AtHome
}

/// <summary>Вид события колеса жителя (<c>EvKind</c>): что произойдёт в <c>EvTick</c>. Числа лежат в сейве.</summary>
internal static class ResidentEvent
{
    public const byte None = 0;
    public const byte Plan = 1;       // составить план дня
    public const byte Depart = 2;     // выезд по плану
    public const byte EndActivity = 3;// конец занятия (смена, визит) -> выезд домой
    public const byte Deadline = 4;   // дедлайн состояния (WaitDepart, поиск, выселение)
    public const byte Arrive = 5;     // прибытие агрегатной ноги
}

/// <summary>Вид тура в плане дня (<c>PlanKind</c>).</summary>
internal static class TourKind
{
    public const byte None = 0;
    public const byte Work = 1;       // Home -> Work -> Home
    public const byte Shop = 2;       // Home -> C -> Home
}

/// <summary>Запись очереди ищущих работу (FIFO по <c>Seq</c>; хранится и в сейве, блок <c>RESD</c>).</summary>
internal struct SeekEntry
{
    public int Index;
    public int Generation;
}

/// <summary>
/// Жители города (zoning-demand.md §2.4): SoA с ёмкостью <c>max_city_residents</c> и <see cref="HandlePool"/>. В пути житель представлен существующим агентом
/// (<see cref="Agent"/>/<see cref="AgentGen"/>: машина или пешеход), между событиями его не трогает ни одна фаза. Мёртвые слоты в сейве - канон.
/// План дня - до <see cref="MaxTours"/> туров в плоских массивах <c>Plan*</c> (индекс <c>i * MaxTours + k</c>).
/// </summary>
internal sealed class CityResidents
{
    public const int MaxTours = 3;

    public readonly HandlePool Pool;

    public readonly int[] Home;
    public readonly int[] Work;
    public readonly int[] NextHome;        // интрузивный список жителей дома (голова BuildingStore.HeadRes)
    public readonly int[] NextWork;        // список работников (BuildingStore.HeadWork)
    public readonly byte[] Flags;          // ResidentFlags
    public readonly byte[] State;          // ResidentState
    public readonly long[] StateTick;
    public readonly long[] EvTick;         // следующее событие колеса (-1 нет)
    public readonly byte[] EvKind;         // ResidentEvent

    // План дня: тур = (вид, способ, цель, слот выезда); курсор указывает на текущий тур.
    public readonly byte[] PlanCount;
    public readonly byte[] PlanCursor;
    public readonly byte[] PlanKind;       // [N * MaxTours] TourKind
    public readonly byte[] PlanMode;       // [N * MaxTours] TravelMode
    public readonly int[] PlanDest;        // [N * MaxTours] здание-цель
    public readonly ushort[] PlanSlot;     // [N * MaxTours] слот выезда (0..287)

    // Текущая нога.
    public readonly byte[] LegMode;        // TravelMode
    public readonly ushort[] LegO;         // ячейка отправления
    public readonly ushort[] LegD;         // ячейка назначения
    public readonly long[] LegReq;         // тик заявки
    public readonly int[] LegWalk;         // накопители, тики
    public readonly int[] LegWait;
    public readonly int[] LegRide;
    public readonly int[] LegDest;         // здание-цель текущей ноги (дом на обратной ноге) или -1 (Z2)
    public readonly int[] LegStopA;        // автобусная нога: остановка посадки и высадки (глобальные индексы) или -1 (Z2)
    public readonly int[] LegStopB;

    public readonly int[] Agent;          // индекс машины или пешехода (-1)
    public readonly int[] AgentGen;
    public readonly long[] Account;
    public readonly long[] IncomeEwma;
    public readonly short[] SeekDays;
    public readonly short[] EvictDays;
    public readonly int[] Seq;             // порядковый номер въезда (ключ FIFO и ничьих)

    /// <summary>Очередь ищущих работу (FIFO по <see cref="Seq"/>; наполняет и разбирает рынок Z4).</summary>
    public readonly RingBuffer<SeekEntry> SeekQueue;

    /// <summary>Следующий <see cref="Seq"/>.</summary>
    public int NextSeq;

    public CityResidents(int capacity)
    {
        Pool = new HandlePool(capacity);
        Home = new int[capacity];
        Work = new int[capacity];
        NextHome = new int[capacity];
        NextWork = new int[capacity];
        Flags = new byte[capacity];
        State = new byte[capacity];
        StateTick = new long[capacity];
        EvTick = new long[capacity];
        EvKind = new byte[capacity];
        PlanCount = new byte[capacity];
        PlanCursor = new byte[capacity];
        PlanKind = new byte[capacity * MaxTours];
        PlanMode = new byte[capacity * MaxTours];
        PlanDest = new int[capacity * MaxTours];
        PlanSlot = new ushort[capacity * MaxTours];
        LegMode = new byte[capacity];
        LegO = new ushort[capacity];
        LegD = new ushort[capacity];
        LegReq = new long[capacity];
        LegWalk = new int[capacity];
        LegWait = new int[capacity];
        LegRide = new int[capacity];
        LegDest = new int[capacity];
        LegStopA = new int[capacity];
        LegStopB = new int[capacity];
        Agent = new int[capacity];
        AgentGen = new int[capacity];
        Account = new long[capacity];
        IncomeEwma = new long[capacity];
        SeekDays = new short[capacity];
        EvictDays = new short[capacity];
        Seq = new int[capacity];
        SeekQueue = new RingBuffer<SeekEntry>(capacity);
        for (int i = 0; i < capacity; i++)
        {
            ResetSlot(i);
        }
    }

    public int Capacity => Pool.Capacity;

    public int HighWater => Pool.HighWater;

    public int AliveCount => Pool.AliveCount;

    public bool IsAlive(int index) => Pool.IsAlive(index);

    /// <summary>Создаёт жителя (все поля - свежий слот, <c>Seq</c> выдаётся). false - ёмкость исчерпана.</summary>
    public bool TryCreate(out int index)
    {
        if (!Pool.TryAllocate(out index, out _))
        {
            index = -1;
            return false;
        }

        ResetSlot(index);
        Seq[index] = NextSeq++;
        return true;
    }

    /// <summary>Освобождает слот (из списков дома и работы житель уже убран).</summary>
    public void Destroy(int index)
    {
        ResetSlot(index);
        Pool.Free(index);
    }

    public void ClearPlan(int i)
    {
        PlanCount[i] = 0;
        PlanCursor[i] = 0;
        int b = i * MaxTours;
        for (int k = 0; k < MaxTours; k++)
        {
            PlanKind[b + k] = 0;
            PlanMode[b + k] = 0;
            PlanDest[b + k] = -1;
            PlanSlot[b + k] = 0;
        }
    }

    private void ResetSlot(int i)
    {
        Home[i] = -1;
        Work[i] = -1;
        NextHome[i] = -1;
        NextWork[i] = -1;
        Flags[i] = 0;
        State[i] = 0;
        StateTick[i] = 0;
        EvTick[i] = -1;
        EvKind[i] = ResidentEvent.None;
        ClearPlan(i);
        LegMode[i] = 0;
        LegO[i] = 0;
        LegD[i] = 0;
        LegReq[i] = 0;
        LegWalk[i] = 0;
        LegWait[i] = 0;
        LegRide[i] = 0;
        LegDest[i] = -1;
        LegStopA[i] = -1;
        LegStopB[i] = -1;
        Agent[i] = -1;
        AgentGen[i] = 0;
        Account[i] = 0;
        IncomeEwma[i] = 0;
        SeekDays[i] = 0;
        EvictDays[i] = 0;
        Seq[i] = 0;
    }
}
