using System.Diagnostics;

namespace Sim.Core.Util;

/// <summary>
/// Открытая хэш-таблица long→int фиксированной ёмкости (линейное пробирование, удаление со сдвигом
/// назад — без надгробий). Размер таблицы = степень двойки &gt;= 2 * maxEntries, поэтому заполнение &lt;= 50 %.
/// Не растёт: <see cref="TrySet"/> возвращает false, когда достигнут maxEntries. Детерминирована.
/// </summary>
internal sealed class FixedHashMap
{
    private readonly long[] _keys;
    private readonly int[] _values;
    private readonly bool[] _used;
    private readonly int _mask;
    private int _count;

    public FixedHashMap(int maxEntries)
    {
        Debug.Assert(maxEntries > 0);
        int size = 4;
        while (size < maxEntries * 2)
        {
            size <<= 1;
        }

        _keys = new long[size];
        _values = new int[size];
        _used = new bool[size];
        _mask = size - 1;
        MaxEntries = maxEntries;
    }

    public int MaxEntries { get; }

    public int Count => _count;

    public bool TryGet(long key, out int value)
    {
        int i = Hash(key) & _mask;
        while (_used[i])
        {
            if (_keys[i] == key)
            {
                value = _values[i];
                return true;
            }

            i = (i + 1) & _mask;
        }

        value = 0;
        return false;
    }

    public bool ContainsKey(long key) => TryGet(key, out _);

    /// <summary>Вставка или обновление. false = таблица полна (новый ключ не добавлен).</summary>
    public bool TrySet(long key, int value)
    {
        int i = Hash(key) & _mask;
        while (_used[i])
        {
            if (_keys[i] == key)
            {
                _values[i] = value;
                return true;
            }

            i = (i + 1) & _mask;
        }

        if (_count >= MaxEntries)
        {
            return false;
        }

        _used[i] = true;
        _keys[i] = key;
        _values[i] = value;
        _count++;
        return true;
    }

    public bool Remove(long key)
    {
        int i = Hash(key) & _mask;
        while (_used[i])
        {
            if (_keys[i] == key)
            {
                RemoveAt(i);
                return true;
            }

            i = (i + 1) & _mask;
        }

        return false;
    }

    public void Clear()
    {
        System.Array.Clear(_used);
        _count = 0;
    }

    // Backward-shift deletion (Knuth, алгоритм R).
    private void RemoveAt(int hole)
    {
        int j = hole;
        while (true)
        {
            j = (j + 1) & _mask;
            if (!_used[j])
            {
                break;
            }

            int ideal = Hash(_keys[j]) & _mask;
            bool movable = hole <= j ? (ideal <= hole || ideal > j) : (ideal <= hole && ideal > j);
            if (movable)
            {
                _keys[hole] = _keys[j];
                _values[hole] = _values[j];
                hole = j;
            }
        }

        _used[hole] = false;
        _count--;
    }

    // Фиксированный смешивающий хэш (SplitMix64 finalizer), не зависит от платформы.
    private static int Hash(long key)
    {
        ulong x = (ulong)key;
        x ^= x >> 30;
        x *= 0xbf58476d1ce4e5b9UL;
        x ^= x >> 27;
        x *= 0x94d049bb133111ebUL;
        x ^= x >> 31;
        return (int)(x & 0x7fffffffUL);
    }
}
