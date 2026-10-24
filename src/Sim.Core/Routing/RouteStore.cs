using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Хранилище маршрутов (routing.md 9.5, ADR-0009): <b>план</b> (стек <c>DirSeg | level &lt;&lt; 28</c> в общей арене, вершина стека -
/// ближайший ещё не развёрнутый элемент) и <b>окно</b> (до <see cref="WindowSteps"/> полосовых шагов <see cref="RouteStep"/> в фиксированном
/// слоте <c>Window[slot * WindowSteps ..]</c>, окно не двигается и не компактифицируется). Слот = handle маршрута.
/// Арена плана - bump-аллокатор с переездом блока в хвост при нехватке <see cref="PlanCap"/> и компактификацией по <see cref="PlanFirst"/>.
/// Только хранение: поиск и развёртывание - <see cref="Router"/>. Все массивы выделены в конструкторе, методы не аллоцируют.
/// </summary>
internal sealed class RouteStore
{
    public readonly HandlePool Pool;
    public readonly int WindowSteps;
    public readonly int PlanReserve;         // запас стека сверх длины при выдаче блока (8 · уровней)
    public readonly RouteStep[] Window;      // [max_routes · WindowSteps]
    public readonly byte[] WinCount;         // шагов в окне слота
    public readonly int[] PlanFirst;         // начало блока слота в PlanArena
    public readonly int[] PlanCount;         // элементов стека
    public readonly int[] PlanCap;           // ёмкость блока
    public readonly int[] PlanArena;         // [max_plan_steps]
    public readonly float[] PlanCost;        // стоимость плана при построении (или после принятия дрейфа), с
    public readonly long[] PlanTick;         // тик, к которому относится PlanCost
    private readonly int[] _compactKeys;     // служебные буферы Compact
    private readonly int[] _compactItems;

    public RouteStore(int maxRoutes, int maxPlanSteps, int windowSteps, int levels)
    {
        int routes = Math.Max(0, maxRoutes);
        WindowSteps = Math.Max(1, windowSteps);
        PlanReserve = 8 * Math.Max(1, levels);
        Pool = new HandlePool(routes);
        Window = new RouteStep[checked(routes * WindowSteps)];
        WinCount = new byte[routes];
        PlanFirst = new int[routes];
        PlanCount = new int[routes];
        PlanCap = new int[routes];
        PlanCost = new float[routes];
        PlanTick = new long[routes];
        PlanArena = new int[Math.Max(0, maxPlanSteps)];
        _compactKeys = new int[routes];
        _compactItems = new int[routes];
    }

    /// <summary>Занято в арене плана (включая мёртвые блоки до компактификации), элементов.</summary>
    public int PlanUsed { get; private set; }

    /// <summary>Сколько раз выполнялась компактификация арены плана (<c>PlanCompactions</c>).</summary>
    public int Compactions { get; private set; }

    public int AliveCount => Pool.AliveCount;

    /// <summary>Выдаёт слот с пустым окном и пустым планом. false = слотов нет.</summary>
    public bool TryAllocate(out RouteHandle handle)
    {
        handle = RouteHandle.Null;
        if (!Pool.TryAllocate(out int index, out int generation))
        {
            return false;
        }

        WinCount[index] = 0;
        PlanFirst[index] = 0;
        PlanCount[index] = 0;
        PlanCap[index] = 0;
        PlanCost[index] = 0f;
        PlanTick[index] = 0;
        handle = new RouteHandle(index, generation);
        return true;
    }

    public bool IsAlive(RouteHandle h) => !h.IsNull && Pool.IsAlive(h.Index, h.Generation);

    /// <summary>Освобождает слот (generation++). false = handle невалиден. Блок плана остаётся мёртвым до компактификации.</summary>
    public bool Free(RouteHandle h)
    {
        if (!IsAlive(h))
        {
            return false;
        }

        WinCount[h.Index] = 0;
        PlanCount[h.Index] = 0;
        PlanCap[h.Index] = 0;
        Pool.Free(h.Index);
        return true;
    }

    /// <summary>Окно маршрута (срез массива, валиден до следующего Step); пусто, если handle невалиден.</summary>
    public ReadOnlySpan<RouteStep> GetWindow(RouteHandle h) =>
        IsAlive(h) ? new ReadOnlySpan<RouteStep>(Window, h.Index * WindowSteps, WinCount[h.Index]) : ReadOnlySpan<RouteStep>.Empty;

    /// <summary>Стек плана слота: <c>[0]</c> - дно, последний элемент - вершина (ближайший).</summary>
    public ReadOnlySpan<int> GetPlan(int slot) => new(PlanArena, PlanFirst[slot], PlanCount[slot]);

    /// <summary>
    /// Записывает стек плана слота (дно - первый элемент). Блок переезжает в хвост арены, если не помещается в
    /// <see cref="PlanCap"/>; при нехватке места арена уплотняется. false = места нет (вызывающий считает <c>Deferred</c>).
    /// </summary>
    public bool SetPlan(int slot, ReadOnlySpan<int> stack)
    {
        int n = stack.Length;
        if (n > PlanCap[slot])
        {
            int cap = n + PlanReserve;
            if (cap > PlanArena.Length - PlanUsed)
            {
                // Освободить место: слот, который пишем, не копируется (данные приходят в stack).
                PlanCount[slot] = 0;
                PlanCap[slot] = 0;
                Compact();
                if (cap > PlanArena.Length - PlanUsed)
                {
                    return false;
                }
            }

            PlanFirst[slot] = PlanUsed;
            PlanCap[slot] = cap;
            PlanUsed += cap;
        }

        stack.CopyTo(new Span<int>(PlanArena, PlanFirst[slot], n));
        PlanCount[slot] = n;
        return true;
    }

    /// <summary>
    /// Компактификация арены плана: живые блоки переносятся вплотную в порядке расположения в арене (порядок по <c>PlanFirst</c>),
    /// ёмкость блока = min(прежняя, длина + запас). Handle'ы остаются валидными. Возвращает освобождённое число элементов. Без аллокаций.
    /// </summary>
    public int Compact()
    {
        int n = 0;
        for (int i = 0; i < Pool.HighWater; i++)
        {
            if (Pool.IsAlive(i) && PlanCap[i] > 0)
            {
                _compactKeys[n] = PlanFirst[i];
                _compactItems[n] = i;
                n++;
            }
        }

        new Span<int>(_compactKeys, 0, n).Sort(new Span<int>(_compactItems, 0, n));
        int dst = 0;
        for (int k = 0; k < n; k++)
        {
            int i = _compactItems[k];
            int len = PlanCount[i];
            if (PlanFirst[i] != dst)
            {
                Array.Copy(PlanArena, PlanFirst[i], PlanArena, dst, len);
                PlanFirst[i] = dst;
            }

            int cap = Math.Min(PlanCap[i], len + PlanReserve); // не больше прежней: dst не обгоняет ещё не перенесённые блоки
            PlanCap[i] = cap;
            dst += cap;
        }

        int freed = PlanUsed - dst;
        PlanUsed = dst;
        Compactions++;
        return freed;
    }
}
