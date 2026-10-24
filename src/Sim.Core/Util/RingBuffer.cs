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

    /// <summary>Заменяет элемент по порядку очереди (0 = голова).</summary>
    public void Set(int offset, in T item)
    {
        Debug.Assert((uint)offset < (uint)_count);
        int i = _head + offset;
        if (i >= _items.Length)
        {
            i -= _items.Length;
        }

        _items[i] = item;
    }

    /// <summary>Удаляет элемент по порядку очереди, сдвигая хвост к голове (O(Count - offset)); порядок остальных сохраняется.</summary>
    public void RemoveAt(int offset)
    {
        Debug.Assert((uint)offset < (uint)_count);
        int cap = _items.Length;
        int i = _head + offset;
        if (i >= cap)
        {
            i -= cap;
        }

        for (int k = offset; k < _count - 1; k++)
        {
            int next = i + 1;
            if (next == cap)
            {
                next = 0;
            }

            _items[i] = _items[next];
            i = next;
        }

        _count--;
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
