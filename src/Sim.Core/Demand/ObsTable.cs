using System;

namespace Sim.Core.Demand;

/// <summary>
/// Накопитель наблюдений скимов периода (zoning-demand.md §5.1): открытая хэш-таблица <c>int ключ -&gt; (long сумма, int число)</c> фиксированной ёмкости
/// (<c>skim_obs_capacity</c>), без аллокаций после создания. Переполнение - <see cref="Add"/> возвращает false (наблюдение отбрасывается, счётчик <c>SkimObsDropped</c>).
/// В сейве лежит каноническим отсортированным по ключу массивом (<see cref="CopySorted"/>), порядок вставки на состояние не влияет.
/// </summary>
internal sealed class ObsTable
{
    private readonly int[] _keys;
    private readonly long[] _sums;
    private readonly int[] _counts;
    private readonly bool[] _used;
    private readonly int _mask;

    public ObsTable(int maxEntries)
    {
        MaxEntries = maxEntries;
        int size = 4;
        while (size < maxEntries * 2)
        {
            size <<= 1;
        }

        _keys = new int[size];
        _sums = new long[size];
        _counts = new int[size];
        _used = new bool[size];
        _mask = size - 1;
    }

    public int MaxEntries { get; }

    public int Count { get; private set; }

    public void Clear()
    {
        Array.Clear(_used);
        Array.Clear(_sums);
        Array.Clear(_counts);
        Count = 0;
    }

    /// <summary>Добавляет наблюдение к ключу; false - таблица полна и ключа в ней нет.</summary>
    public bool Add(int key, long value)
    {
        int i = Slot(key);
        while (_used[i])
        {
            if (_keys[i] == key)
            {
                _sums[i] += value;
                _counts[i]++;
                return true;
            }

            i = (i + 1) & _mask;
        }

        if (Count >= MaxEntries)
        {
            return false;
        }

        _used[i] = true;
        _keys[i] = key;
        _sums[i] = value;
        _counts[i] = 1;
        Count++;
        return true;
    }

    /// <summary>Вставка готовой записи (загрузка сейва); false - таблица полна или ключ повторён.</summary>
    public bool Insert(int key, long sum, int count)
    {
        int i = Slot(key);
        while (_used[i])
        {
            if (_keys[i] == key)
            {
                return false;
            }

            i = (i + 1) & _mask;
        }

        if (Count >= MaxEntries)
        {
            return false;
        }

        _used[i] = true;
        _keys[i] = key;
        _sums[i] = sum;
        _counts[i] = count;
        Count++;
        return true;
    }

    /// <summary>Копирует записи в массивы по возрастанию ключа (в первые <see cref="Count"/> элементов); возвращает их число. Ключи уникальны, поэтому результат детерминирован.</summary>
    public int CopySorted(int[] keys, long[] sums, int[] counts)
    {
        int n = 0;
        for (int i = 0; i < _used.Length; i++)
        {
            if (_used[i])
            {
                keys[n] = _keys[i];
                counts[n] = i;   // временно: номер слота
                n++;
            }
        }

        Array.Sort(keys, counts, 0, n);
        for (int k = 0; k < n; k++)
        {
            int slot = counts[k];
            sums[k] = _sums[slot];
            counts[k] = _counts[slot];
        }

        return n;
    }

    private int Slot(int key) => (int)(((uint)key * 2654435761u) >> 7) & _mask;
}
