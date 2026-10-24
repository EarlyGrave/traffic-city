using System.Diagnostics;

namespace Sim.Core.Util;

/// <summary>
/// Free-list пул слотов фиксированной ёмкости. Поколение слота: 0 = никогда не выдавался,
/// нечётное = занят, чётное = свободен. Allocate и Free оба делают generation++.
/// Выдача: сначала LIFO free-list, потом новый слот (_highWater++).
/// Класс, а не struct (отклонение от core-api §2.2): поле <c>readonly HandlePool</c> в хранилищах
/// иначе давало бы защитные копии и терялись бы мутации.
/// </summary>
internal sealed class HandlePool
{
    private readonly int[] _generation;
    private readonly int[] _freeList;
    private int _freeCount;
    private int _highWater;
    private int _aliveCount;

    public HandlePool(int capacity)
    {
        Debug.Assert(capacity >= 0);
        Capacity = capacity;
        _generation = new int[capacity];
        _freeList = new int[capacity];
    }

    public int Capacity { get; }

    public int AliveCount => _aliveCount;

    /// <summary>Верхняя граница для обходов «по всем слотам».</summary>
    public int HighWater => _highWater;

    /// <summary>false = ёмкость исчерпана (вызывающий инкрементирует счётчик и не аллоцирует).</summary>
    public bool TryAllocate(out int index, out int generation)
    {
        if (_freeCount > 0)
        {
            index = _freeList[--_freeCount];
        }
        else if (_highWater < Capacity)
        {
            index = _highWater++;
        }
        else
        {
            index = -1;
            generation = 0;
            return false;
        }

        generation = NextGeneration(_generation[index]);
        _generation[index] = generation;
        _aliveCount++;
        return true;
    }

    public void Free(int index)
    {
        Debug.Assert(IsAlive(index));
        _generation[index] = NextGeneration(_generation[index]);
        _freeList[_freeCount++] = index;
        _aliveCount--;
    }

    public bool IsAlive(int index) => (uint)index < (uint)_highWater && (_generation[index] & 1) == 1;

    public bool IsAlive(int index, int generation) => IsAlive(index) && _generation[index] == generation;

    public int GenerationOf(int index) => _generation[index];

    // Переполнение int: после int.MaxValue (нечётное) идёт 2 — чётность сохраняется, 0 остаётся «Null».
    private static int NextGeneration(int g) => g == int.MaxValue ? 2 : g + 1;
}
