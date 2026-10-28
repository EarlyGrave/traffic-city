using System;
using System.Threading;

namespace Sim.Host;

/// <summary>
/// Кольцо «один писатель - один читатель» без блокировок и аллокаций (ADR-0024 п. 4). Ёмкость - степень двойки; переполнение - <c>false</c>, элемент не теряется
/// молча. Писатель и читатель - по одному потоку каждый (какой именно, определяет владелец кольца).
/// </summary>
internal sealed class SpscRing<T>
    where T : struct
{
    private readonly T[] _buf;
    private readonly long _mask;
    private long _head; // читатель
    private long _tail; // писатель

    public SpscRing(int capacity)
    {
        int cap = 2;
        while (cap < capacity)
        {
            cap <<= 1;
        }

        _buf = new T[cap];
        _mask = cap - 1;
    }

    public int Capacity => _buf.Length;

    public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    /// <summary>Только поток-писатель.</summary>
    public bool TryWrite(in T value)
    {
        long t = _tail;
        if (t - Volatile.Read(ref _head) >= _buf.Length)
        {
            return false;
        }

        _buf[t & _mask] = value;
        Volatile.Write(ref _tail, t + 1);
        return true;
    }

    /// <summary>Только поток-читатель.</summary>
    public bool TryRead(out T value)
    {
        long h = _head;
        if (h == Volatile.Read(ref _tail))
        {
            value = default;
            return false;
        }

        value = _buf[h & _mask];
        Volatile.Write(ref _head, h + 1);
        return true;
    }
}
