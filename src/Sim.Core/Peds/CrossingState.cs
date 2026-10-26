using System;

namespace Sim.Core.Peds;

/// <summary>Состояние перехода (pedestrians.md §5.1): <c>Free -> Claimed -> Busy -> Free</c>. Числа лежат в сейве (<c>XING</c>) - не менять.</summary>
internal static class XStateCode
{
    public const byte Free = 0;
    public const byte Claimed = 1;
    public const byte Busy = 2;
}

/// <summary>
/// Состояние переходов (SoA по индексу перехода <c>x</c>: слот рукава в <c>NodeArms</c>, затем <c>PedNetwork.MidXBase + m</c> для переходов на перегоне).
/// Объявлено и сохраняется в P2 (столбцы <c>XING</c> заморожены); логика автомата - P3 (<c>Crossings.cs</c>). До P3 переходы «прозрачны»: всё остаётся нулевым.
/// Состояние - всё, кроме блока «производные». Очередь у бордюра - интрузивный FIFO по <c>PedStore.Next</c>.
/// </summary>
internal sealed class CrossingState
{
    public readonly int Capacity;

    public readonly byte[] XState;
    public readonly int[] QHead;
    public readonly int[] QTail;
    public readonly int[] QCount;
    public readonly int[] OnCross;          // пешеходов на полосе перехода
    public readonly long[] BusyUntil;       // последний пешеход сходит
    public readonly long[] BusyUntilIn;     // половины перехода (§5.9): входная половина (над полосами въезда в узел) занята пешеходом/идущим на неё до этого тика
    public readonly long[] BusyUntilOut;    // выходная половина (над полосами выезда из узла)
    public readonly long[] ClosedSince;
    public readonly long[] CarWindowUntil;
    public readonly byte[] CarsWaiting;     // пишет фаза 4b
    public readonly long[] WaitSum;         // накопитель ожидания, тики
    public readonly int[] Releases;         // выпусков
    public readonly int[] MeasuredWait;     // измеренная часть стоимости (EWMA), тики; смысл задаёт P3

    // ---- производные (вне хэша и сейва; пересчитываются из XState в Crossings.RestoreDerived, P3) ----
    public readonly ulong[] NodeXIn;        // [узлов сети] OR XIn закрытых (не Free) переходов узла
    public readonly ulong[] NodeXOut;
    public readonly byte[] MidClosed;       // [MidCap]
    public readonly ulong[] XActive;        // битовая маска: QCount > 0 или не Free; обход по возрастанию индекса
    public readonly int[] XClosedNow;       // переходы, у которых в этом тике закрылась половина (для снятия допусков в 4b)
    public readonly byte[] XClosedNowBits;  // параллелен XClosedNow: какие половины закрылись (бит 0 - входная, бит 1 - выходная)
    public readonly byte[] HalfMask;        // [XCount] закрытые половины на конец последнего шага автомата: бит 0 - входная, бит 1 - выходная
    public int XClosedNowCount;
    public int ObservedCount;               // переходов с хотя бы одним выпуском (Releases > 0): знаменатель crossing_closed_share
    public readonly byte[] CrossingSignals; // опубликованное: бит 0-1 XState, бит 2-3 сигнал (0 DontWalk, 1 Walk, 2 Clear); P3 обновляет при смене

    public CrossingState(int capacity, int nodeCapacity, int midCapacity)
    {
        Capacity = capacity;
        XState = new byte[capacity];
        QHead = new int[capacity];
        QTail = new int[capacity];
        QCount = new int[capacity];
        OnCross = new int[capacity];
        BusyUntil = new long[capacity];
        BusyUntilIn = new long[capacity];
        BusyUntilOut = new long[capacity];
        ClosedSince = new long[capacity];
        CarWindowUntil = new long[capacity];
        CarsWaiting = new byte[capacity];
        WaitSum = new long[capacity];
        Releases = new int[capacity];
        MeasuredWait = new int[capacity];
        NodeXIn = new ulong[Math.Max(1, nodeCapacity)];
        NodeXOut = new ulong[Math.Max(1, nodeCapacity)];
        MidClosed = new byte[Math.Max(1, midCapacity)];
        XActive = new ulong[(capacity + 63) >> 6];
        XClosedNow = new int[Math.Max(1, capacity)];
        XClosedNowBits = new byte[Math.Max(1, capacity)];
        HalfMask = new byte[capacity];
        CrossingSignals = new byte[capacity];
        Array.Fill(QHead, -1);
        Array.Fill(QTail, -1);
    }

    /// <summary>Всё в канон «нет пешеходов у переходов» (сброс при <c>BuildRoad</c>); счётчики метрик переходов обнуляются тоже.</summary>
    public void Reset()
    {
        Array.Clear(XState);
        Array.Fill(QHead, -1);
        Array.Fill(QTail, -1);
        Array.Clear(QCount);
        Array.Clear(OnCross);
        Array.Clear(BusyUntil);
        Array.Clear(BusyUntilIn);
        Array.Clear(BusyUntilOut);
        Array.Clear(HalfMask);
        Array.Clear(ClosedSince);
        Array.Clear(CarWindowUntil);
        Array.Clear(CarsWaiting);
        Array.Clear(WaitSum);
        Array.Clear(Releases);
        Array.Clear(MeasuredWait);
        Array.Clear(NodeXIn);
        Array.Clear(NodeXOut);
        Array.Clear(MidClosed);
        Array.Clear(XActive);
        XClosedNowCount = 0;
        ObservedCount = 0;
        Array.Clear(CrossingSignals);
    }

    public void SetActive(int x) => XActive[x >> 6] |= 1UL << (x & 63);

    public void ClearActive(int x) => XActive[x >> 6] &= ~(1UL << (x & 63));

    public bool IsActive(int x) => (XActive[x >> 6] & (1UL << (x & 63))) != 0;
}
