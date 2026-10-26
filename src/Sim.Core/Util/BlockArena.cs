using System;
using System.Diagnostics;
using System.Numerics;

namespace Sim.Core.Util;

/// <summary>
/// Учёт блоков в плотном массиве фиксированной длины (player-tools.md §3 п. 2, ADR-0023). Арена не владеет самим массивом (он у хозяина, у каждого свой тип
/// элементов): она знает только <see cref="Tail"/> (занятая часть), лестницу ёмкостей, LIFO-free-list по классам и признак «блок выделен по лестнице».
/// Освобождённые блоки хозяин обнуляет каноническими значениями сам (ёмкость блока - <see cref="CapacityOf"/>, до <see cref="Free"/>).
/// <list type="bullet">
/// <item>Блок, записанный генератором или <c>Create</c> подряд без запаса (<see cref="AllocExact"/>), имеет физическую ёмкость <c>n</c>.</item>
/// <item>Блок по лестнице (<see cref="Alloc"/>) имеет ёмкость <see cref="CapFor"/>(n) - функцию числа элементов, не истории: сначала берётся верхний блок класса из
/// free-list, иначе новый в хвосте. Рост внутри ступеньки - на месте (<see cref="CanGrow"/>).</item>
/// <item>Освобождённый блок точной ёмкости <c>n</c> раскладывается жадно на ступеньки (largest first), поэтому все дыры в free-list - ровно ступеньки лестницы.</item>
/// </list>
/// Free-list - связные списки в общем пуле записей; нехватка записей - дыра «теряется» (<see cref="LeakedElements"/>), а не ошибка: освобождение не может провалиться.
/// Все массивы выделены в конструкторе; операции без аллокаций и исключений.
/// </summary>
internal sealed class BlockArena
{
    /// <summary>Ступеньки лестницы по возрастанию: 1..8 точно, дальше четыре ступеньки на октаву (как лестница оверлея, без запаса <c>MinSlack</c>).</summary>
    public static readonly int[] Steps = BuildSteps();

    private readonly int _length;
    private readonly ulong[] _ladderBits;   // бит на первый элемент блока, выделенного по лестнице
    private readonly int[] _entFirst;       // записи free-list: первый элемент дыры
    private readonly int[] _entNext;        // следующая запись списка класса / цепочки свободных записей
    private readonly int[] _head;           // [класс] верхняя запись или -1
    private readonly int[] _count;          // [класс] длина списка
    private int _entFreeHead = -1;          // цепочка свободных записей
    private int _entUsed;                   // записей, ни разу не выданных, нет: [0, _entUsed) выдавались
    private int _freeTotal;                 // число дыр во всех классах

    public BlockArena(int length, int entryCapacity = -1)
    {
        _length = Math.Max(0, length);
        _ladderBits = new ulong[(_length >> 6) + 1];
        int e = entryCapacity >= 0 ? entryCapacity : Math.Min(_length, Math.Max(256, _length >> 4));
        _entFirst = new int[e];
        _entNext = new int[e];
        _head = new int[Steps.Length];
        _count = new int[Steps.Length];
        Array.Fill(_head, -1);
    }

    /// <summary>Длина массива хозяина (предел <see cref="Tail"/>).</summary>
    public int Length => _length;

    /// <summary>Занятая часть массива: всё, что левее, - живые блоки и дыры.</summary>
    public int Tail { get; internal set; }

    /// <summary>Сколько записей free-list можно хранить (сверх - дыра теряется).</summary>
    public int EntryCapacity => _entFirst.Length;

    /// <summary>Сумма ёмкостей потерянных дыр (не хватило записей free-list); считается в хэше и сейве.</summary>
    public long LeakedElements { get; internal set; }

    /// <summary>Число дыр во всех классах (при загрузке до чтения столбца дыр - заявленное в файле, см. <see cref="SetPendingFreeCount"/>).</summary>
    public int FreeBlockCount => _freeTotal;

    /// <summary>Загрузка: заявленное число дыр (длина столбца дыр, читается следующим). Списки заполняет <see cref="RestoreFree"/>.</summary>
    internal void SetPendingFreeCount(int count) => _freeTotal = count;

    /// <summary>Слов битовой карты, значимых при текущем <see cref="Tail"/>.</summary>
    public int LadderWordCount => (Tail + 63) >> 6;

    /// <summary>Слова битовой карты «блок выделен по лестнице» (для сейва и хэша; значимы <see cref="LadderWordCount"/> слов).</summary>
    internal ulong[] LadderBits => _ladderBits;

    /// <summary>Наименьшая ступенька, не меньшая <paramref name="n"/>; 0 при <c>n &lt;= 0</c>. Функция числа элементов, не истории.</summary>
    public static int CapFor(int n)
    {
        if (n <= 0)
        {
            return 0;
        }

        if (n <= 8)
        {
            return n;
        }

        int step = 1 << (BitOperations.Log2((uint)(n - 1)) - 2);
        long cap = ((long)n + step - 1) / step * step;
        return cap > int.MaxValue ? int.MaxValue : (int)cap;
    }

    /// <summary>Класс ступеньки <paramref name="cap"/> или -1, если это не ступенька.</summary>
    public static int ClassOf(int cap)
    {
        int idx = Array.BinarySearch(Steps, cap);
        return idx >= 0 ? idx : -1;
    }

    private static int FloorClass(int n)
    {
        int idx = Array.BinarySearch(Steps, n);
        return idx >= 0 ? idx : ~idx - 1;
    }

    private static int[] BuildSteps()
    {
        var list = new System.Collections.Generic.List<int>(96);
        int n = 1;
        while (true)
        {
            int cap = CapFor(n);
            list.Add(cap);
            if (cap >= 1 << 28)
            {
                break;
            }

            n = cap + 1;
        }

        return list.ToArray();
    }

    // ---- выделение ----

    /// <summary>Можно ли выделить <paramref name="n"/> элементов по лестнице (дыра класса или место в хвосте). <c>n == 0</c> - всегда.</summary>
    public bool CanAlloc(int n)
    {
        if (n <= 0)
        {
            return true;
        }

        int cap = CapFor(n);
        int c = ClassOf(cap);
        return c >= 0 && (_head[c] >= 0 || (long)Tail + cap <= _length);
    }

    /// <summary>Можно ли выделить <paramref name="n"/> элементов подряд в хвосте без запаса.</summary>
    public bool CanAllocExact(int n) => n <= 0 || (long)Tail + n <= _length;

    /// <summary>
    /// Блок из <paramref name="n"/> элементов по лестнице: верхняя дыра класса (LIFO) либо хвост с запасом до <see cref="CapFor"/>. -1 - места нет; <c>n == 0</c> - пустой блок (возвращает <see cref="Tail"/>, ничего не занимает).
    /// Ёмкость блока - <see cref="CapFor"/>(n); блок помечается в битовой карте.
    /// </summary>
    public int Alloc(int n)
    {
        if (n <= 0)
        {
            return Tail;
        }

        int cap = CapFor(n);
        int c = ClassOf(cap);
        if (c < 0)
        {
            return -1;
        }

        int first;
        int e = _head[c];
        if (e >= 0)
        {
            first = _entFirst[e];
            _head[c] = _entNext[e];
            _count[c]--;
            _freeTotal--;
            _entNext[e] = _entFreeHead;
            _entFreeHead = e;
        }
        else if ((long)Tail + cap <= _length)
        {
            first = Tail;
            Tail += cap;
        }
        else
        {
            return -1;
        }

        _ladderBits[first >> 6] |= 1UL << (first & 63);
        return first;
    }

    /// <summary>Блок из <paramref name="n"/> элементов в хвосте без запаса (генератор, <c>Create</c>, сеть без правок). -1 - места нет.</summary>
    public int AllocExact(int n)
    {
        if (n <= 0)
        {
            return Tail;
        }

        if ((long)Tail + n > _length)
        {
            return -1;
        }

        int first = Tail;
        Tail += n;
        return first;
    }

    /// <summary>Ёмкость блока, чей первый элемент <paramref name="first"/>, а число элементов <paramref name="n"/>.</summary>
    public int CapacityOf(int first, int n) => n <= 0 ? 0 : IsLadder(first) ? CapFor(n) : n;

    /// <summary>Блок выделен по лестнице (<see cref="Alloc"/>).</summary>
    public bool IsLadder(int first) => (uint)first < (uint)_length && (_ladderBits[first >> 6] & (1UL << (first & 63))) != 0;

    /// <summary>Рост блока с <paramref name="n"/> до <paramref name="newN"/> элементов на месте (внутри ступеньки; блок без запаса растёт только до своей ёмкости).</summary>
    public bool CanGrow(int first, int n, int newN) => newN <= CapacityOf(first, n);

    /// <summary>
    /// Освобождает блок: физическая ёмкость - <see cref="CapacityOf"/>. Хозяин обнуляет элементы <b>до</b> вызова. Блок по лестнице - одна дыра своего класса;
    /// блок без запаса раскладывается жадно на ступеньки. Освобождение не проваливается: нехватка записей - <see cref="LeakedElements"/>.
    /// </summary>
    public void Free(int first, int n)
    {
        if (n <= 0)
        {
            return;
        }

        Debug.Assert(first >= 0 && (long)first + CapacityOf(first, n) <= Tail);
        int cap = CapacityOf(first, n);
        if (IsLadder(first))
        {
            _ladderBits[first >> 6] &= ~(1UL << (first & 63));
            Push(first, ClassOf(cap), cap);
            return;
        }

        int at = first;
        int left = cap;
        while (left > 0)
        {
            int c = FloorClass(left);
            int piece = Steps[c];
            Push(at, c, piece);
            at += piece;
            left -= piece;
        }
    }

    private void Push(int first, int c, int cap)
    {
        int e = _entFreeHead;
        if (e >= 0)
        {
            _entFreeHead = _entNext[e];
        }
        else if (_entUsed < _entFirst.Length)
        {
            e = _entUsed++;
        }
        else
        {
            LeakedElements += cap;
            return;
        }

        _entFirst[e] = first;
        _entNext[e] = _head[c];
        _head[c] = e;
        _count[c]++;
        _freeTotal++;
    }

    // ---- сейв, хэш, сверка ----

    /// <summary>
    /// Дыры в каноническом порядке (класс по возрастанию, внутри класса сверху вниз): запись = <c>класс &lt;&lt; 32 | первый элемент</c>.
    /// <paramref name="dst"/> длиной не меньше <see cref="FreeBlockCount"/>.
    /// </summary>
    public void CopyFree(Span<ulong> dst)
    {
        int k = 0;
        for (int c = 0; c < _head.Length; c++)
        {
            for (int e = _head[c]; e >= 0; e = _entNext[e])
            {
                dst[k++] = ((ulong)c << 32) | (uint)_entFirst[e];
            }
        }
    }

    /// <summary>Сброс состояния (дыры, биты, потери); <see cref="Tail"/> не трогает.</summary>
    internal void ClearState()
    {
        Array.Clear(_ladderBits);
        Array.Fill(_head, -1);
        Array.Clear(_count);
        _entFreeHead = -1;
        _entUsed = 0;
        _freeTotal = 0;
        LeakedElements = 0;
    }

    /// <summary>
    /// Загрузка: дыры в каноническом порядке <see cref="CopyFree"/> (<see cref="ClearState"/> уже вызван). false - не влезло в записи, класс вне лестницы или
    /// дыра вне <c>[0, Tail)</c>; пересечения дыр между собой проверяет валидатор сейва.
    /// </summary>
    internal bool RestoreFree(ReadOnlySpan<ulong> packed)
    {
        if (packed.Length > _entFirst.Length)
        {
            return false;
        }

        Array.Fill(_head, -1);
        Array.Clear(_count);
        _entFreeHead = -1;
        _entUsed = 0;
        _freeTotal = 0;
        for (int k = packed.Length - 1; k >= 0; k--)
        {
            int c = (int)(packed[k] >> 32);
            int first = (int)(uint)packed[k];
            if ((uint)c >= (uint)Steps.Length || first < 0 || (long)first + Steps[c] > Tail)
            {
                return false;
            }

            int e = _entUsed++;
            _entFirst[e] = first;
            _entNext[e] = _head[c];
            _head[c] = e;
            _count[c]++;
            _freeTotal++;
        }

        return true;
    }

    /// <summary>Добавляет состояние арены в хэш: хвост, потери, дыры по классам (сверху вниз), биты лестницы значимой части.</summary>
    internal void AddToHash(ref Fnv1a64 h)
    {
        h.Add(Tail);
        h.Add(LeakedElements);
        for (int c = 0; c < _head.Length; c++)
        {
            if (_count[c] == 0)
            {
                continue;
            }

            h.Add(c);
            h.Add(_count[c]);
            for (int e = _head[c]; e >= 0; e = _entNext[e])
            {
                h.Add(_entFirst[e]);
            }
        }

        int words = LadderWordCount;
        for (int i = 0; i < words; i++)
        {
            h.Add(_ladderBits[i]);
        }
    }
}
