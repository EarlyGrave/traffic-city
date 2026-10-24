using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Хранилище маршрутов (core-api 6.5): диапазоны <c>(First, Count)</c> в общей арене <see cref="Steps"/>.
/// Только хранение: алгоритм поиска, кэш OD-пар и компактификация арены - шаг 3 (TODO).
/// Арена - bump-аллокатор; <see cref="Free"/> освобождает слот маршрута, но не возвращает шаги в арену.
/// </summary>
internal sealed class RoutePool
{
    public readonly HandlePool Pool;
    public readonly int[] First;
    public readonly int[] Count;
    public readonly int[] RefCount;
    public readonly int[] FromZone;
    public readonly int[] ToZone;
    public readonly int[] NetworkVersion;
    public readonly long[] LastUsedTick;   // routing.md 4.1: вытеснение магистралей кэша
    public readonly RouteStep[] Steps;
    private readonly int[] _compactKeys;   // служебные буферы Compact (выделены один раз)
    private readonly int[] _compactItems;

    public RoutePool(int maxRoutes, int maxRouteSteps)
    {
        int routes = Math.Max(0, maxRoutes);
        Pool = new HandlePool(routes);
        First = new int[routes];
        Count = new int[routes];
        RefCount = new int[routes];
        FromZone = new int[routes];
        ToZone = new int[routes];
        NetworkVersion = new int[routes];
        LastUsedTick = new long[routes];
        _compactKeys = new int[routes];
        _compactItems = new int[routes];
        Steps = new RouteStep[Math.Max(0, maxRouteSteps)];
    }

    public int StepsUsed { get; private set; }

    public int AliveCount => Pool.AliveCount;

    /// <summary>
    /// Копирует шаги в арену и выдаёт слот маршрута. false = нет слота или не хватает арены
    /// (вызывающий считает <c>RouteDeferred</c>; вытеснение/компактификация - шаг 3).
    /// </summary>
    public bool TryAdd(ReadOnlySpan<RouteStep> steps, int fromZone, int toZone, int networkVersion, out RouteHandle handle)
    {
        handle = RouteHandle.Null;
        if (steps.Length > Steps.Length - StepsUsed || !Pool.TryAllocate(out int index, out int generation))
        {
            return false;
        }

        steps.CopyTo(new Span<RouteStep>(Steps, StepsUsed, steps.Length));
        First[index] = StepsUsed;
        Count[index] = steps.Length;
        RefCount[index] = 0;
        FromZone[index] = fromZone;
        ToZone[index] = toZone;
        NetworkVersion[index] = networkVersion;
        LastUsedTick[index] = 0;
        StepsUsed += steps.Length;
        handle = new RouteHandle(index, generation);
        return true;
    }

    /// <summary>Сколько раз выполнялась компактификация арены.</summary>
    public int Compactions { get; private set; }

    /// <summary>
    /// Компактификация арены (routing.md 4.3): живые маршруты переносятся вплотную в порядке их расположения
    /// в арене (порядок по <c>First</c> - иначе перенос затёр бы ещё не перенесённые данные). Handle'ы остаются
    /// валидными (меняется только <c>First</c>). Возвращает освобождённое число шагов. Без аллокаций.
    /// </summary>
    public int Compact()
    {
        int n = 0;
        for (int i = 0; i < Pool.HighWater; i++)
        {
            if (Pool.IsAlive(i))
            {
                _compactKeys[n] = First[i];
                _compactItems[n] = i;
                n++;
            }
        }

        new Span<int>(_compactKeys, 0, n).Sort(new Span<int>(_compactItems, 0, n));
        int dst = 0;
        for (int k = 0; k < n; k++)
        {
            int i = _compactItems[k];
            int len = Count[i];
            if (First[i] != dst)
            {
                Array.Copy(Steps, First[i], Steps, dst, len);
                First[i] = dst;
            }

            dst += len;
        }

        int freed = StepsUsed - dst;
        StepsUsed = dst;
        Compactions++;
        return freed;
    }

    public bool IsAlive(RouteHandle h) => !h.IsNull && Pool.IsAlive(h.Index, h.Generation);

    /// <summary>Шаги маршрута (срез арены, валиден до следующей компактификации); пусто, если handle невалиден.</summary>
    public ReadOnlySpan<RouteStep> GetSteps(RouteHandle h) =>
        IsAlive(h) ? new ReadOnlySpan<RouteStep>(Steps, First[h.Index], Count[h.Index]) : ReadOnlySpan<RouteStep>.Empty;

    /// <summary>Освобождает слот (generation++). false = handle невалиден.</summary>
    public bool Free(RouteHandle h)
    {
        if (!IsAlive(h))
        {
            return false;
        }

        Count[h.Index] = 0;
        RefCount[h.Index] = 0;
        Pool.Free(h.Index);
        return true;
    }
}
