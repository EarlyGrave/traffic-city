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
    public readonly RouteStep[] Steps;

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
        StepsUsed += steps.Length;
        handle = new RouteHandle(index, generation);
        return true;
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
