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

    // ---- сейв (Save/): массивы и счётчики пула как есть; free-list в LIFO-порядке ----

    /// <summary>Поколения слотов (длина = ёмкость; значимы <c>[0, HighWater)</c>).</summary>
    internal int[] GenerationArray => _generation;

    /// <summary>Free-list снизу вверх (длина = ёмкость; значимы <c>[0, FreeCount)</c>).</summary>
    internal int[] FreeListArray => _freeList;

    internal int FreeCount => _freeCount;

    /// <summary>Загрузка: задаёт <c>HighWater</c> и длину free-list (значения проверяет загрузчик); <see cref="AliveCount"/> считается <see cref="RecountAlive"/>.</summary>
    internal void RestoreCounts(int highWater, int freeCount)
    {
        _highWater = highWater;
        _freeCount = freeCount;
    }

    /// <summary>Загрузка: <c>AliveCount</c> = число слотов с нечётным поколением.</summary>
    internal void RecountAlive()
    {
        int n = 0;
        for (int i = 0; i < _highWater; i++)
        {
            if ((_generation[i] & 1) == 1)
            {
                n++;
            }
        }

        _aliveCount = n;
    }

    /// <summary>Генератор/построение: слоты <c>[0, count)</c> заняты подряд с поколением 1 (free-list пуст). Для хранилищ, где данные пишутся плотным массивом, минуя <see cref="TryAllocate"/>.</summary>
    internal void AdoptDense(int count)
    {
        Debug.Assert(count >= 0 && count <= Capacity);
        for (int i = 0; i < count; i++)
        {
            _generation[i] = 1;
        }

        _highWater = count;
        _freeCount = 0;
        _aliveCount = count;
    }

    /// <summary>Индекс, который выдаст следующий <see cref="TryAllocate"/> (верх free-list либо <c>HighWater</c>), или -1, если места нет. Состояние не меняет.</summary>
    public int PeekIndex() => _freeCount > 0 ? _freeList[_freeCount - 1] : _highWater < Capacity ? _highWater : -1;

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
