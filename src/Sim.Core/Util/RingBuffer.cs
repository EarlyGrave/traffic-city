using System.Diagnostics;

namespace Sim.Core.Util;

/// <summary>FIFO кольцевой буфер фиксированной ёмкости. Не растёт; полный буфер отвергает запись.</summary>
internal sealed class RingBuffer<T>
    where T : unmanaged
{
    private readonly T[] _items;
    private int _head;
    private int _count;

    public RingBuffer(int capacity)
    {
        Debug.Assert(capacity > 0);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count => _count;

    public bool IsEmpty => _count == 0;

    public bool IsFull => _count == _items.Length;

    /// <summary>false = буфер полон, элемент отброшен.</summary>
    public bool TryEnqueue(in T item)
    {
        if (_count == _items.Length)
        {
            return false;
        }

        int tail = _head + _count;
        if (tail >= _items.Length)
        {
            tail -= _items.Length;
        }

        _items[tail] = item;
        _count++;
        return true;
    }

    public bool TryDequeue(out T item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }

        item = _items[_head];
        Advance();
        return true;
    }

    public bool TryPeek(out T item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }

        item = _items[_head];
        return true;
    }

    /// <summary>Элемент по порядку очереди (0 = голова), для хэша и дампа.</summary>
    public ref readonly T At(int offset)
    {
        Debug.Assert((uint)offset < (uint)_count);
        int i = _head + offset;
        if (i >= _items.Length)
        {
            i -= _items.Length;
        }

        return ref _items[i];
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    private void Advance()
    {
        _head++;
        if (_head == _items.Length)
        {
            _head = 0;
        }

        _count--;
    }
}
