using System;

namespace Sim.Core.Routing;

/// <summary>
/// Индексная 4-арная куча с decrease-key (routing.md 9.4): ключ <c>float</c>, значение - идентификатор из <c>[0, idRange)</c>
/// (DirSeg). Порядок - лексикографически <c>(ключ, идентификатор)</c>: равные ключи выдаются по меньшему идентификатору
/// (детерминированный тай-брейк). Дубликатов нет, переполнение невозможно (ёмкость = idRange). Массивы выделены один раз.
/// </summary>
internal sealed class IndexedHeap4
{
    private readonly float[] _key;   // [capacity] ключ по позиции в куче
    private readonly int[] _val;     // [capacity] идентификатор по позиции в куче
    private readonly int[] _pos;     // [idRange] позиция идентификатора в куче или -1

    public IndexedHeap4(int idRange)
    {
        int n = Math.Max(1, idRange);
        _key = new float[n];
        _val = new int[n];
        _pos = new int[n];
        Array.Fill(_pos, -1);
    }

    public int Count { get; private set; }

    public bool Contains(int id) => _pos[id] >= 0;

    /// <summary>Пустая куча; O(Count).</summary>
    public void Clear()
    {
        for (int i = 0; i < Count; i++)
        {
            _pos[_val[i]] = -1;
        }

        Count = 0;
    }

    /// <summary>Добавляет идентификатор или уменьшает его ключ (больший ключ игнорируется).</summary>
    public void PushOrDecrease(int id, float key)
    {
        int p = _pos[id];
        if (p < 0)
        {
            p = Count++;
            _val[p] = id;
            _key[p] = key;
            _pos[id] = p;
            SiftUp(p);
            return;
        }

        if (key < _key[p])
        {
            _key[p] = key;
            SiftUp(p);
        }
    }

    /// <summary>Извлекает минимум; куча не пуста.</summary>
    public int Pop(out float key)
    {
        int top = _val[0];
        key = _key[0];
        _pos[top] = -1;
        int last = --Count;
        if (last > 0)
        {
            _val[0] = _val[last];
            _key[0] = _key[last];
            _pos[_val[0]] = 0;
            SiftDown(0);
        }

        return top;
    }

    private static bool Less(float k1, int v1, float k2, int v2) => k1 < k2 || (k1 == k2 && v1 < v2);

    private void SiftUp(int i)
    {
        int v = _val[i];
        float k = _key[i];
        while (i > 0)
        {
            int parent = (i - 1) >> 2;
            if (!Less(k, v, _key[parent], _val[parent]))
            {
                break;
            }

            _val[i] = _val[parent];
            _key[i] = _key[parent];
            _pos[_val[i]] = i;
            i = parent;
        }

        _val[i] = v;
        _key[i] = k;
        _pos[v] = i;
    }

    private void SiftDown(int i)
    {
        int v = _val[i];
        float k = _key[i];
        int n = Count;
        while (true)
        {
            int first = (i << 2) + 1;
            if (first >= n)
            {
                break;
            }

            int best = first;
            int end = Math.Min(first + 4, n);
            for (int c = first + 1; c < end; c++)
            {
                if (Less(_key[c], _val[c], _key[best], _val[best]))
                {
                    best = c;
                }
            }

            if (!Less(_key[best], _val[best], k, v))
            {
                break;
            }

            _val[i] = _val[best];
            _key[i] = _key[best];
            _pos[_val[i]] = i;
            i = best;
        }

        _val[i] = v;
        _key[i] = k;
        _pos[v] = i;
    }
}
