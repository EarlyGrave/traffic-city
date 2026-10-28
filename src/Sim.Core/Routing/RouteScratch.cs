using System;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Routing;

/// <summary>
/// Рабочая память одного воркера запросов маршрутов (routing-parallel.md 5): свой <see cref="OverlaySearcher"/> (без буферов плана),
/// буферы плана, стека, DirSeg, шагов окна и развёртывания. Всё выделено в конструкторе; <see cref="RouteCompute.Run"/> пишет только сюда.
/// </summary>
internal sealed class RouteScratch
{
    /// <summary>Ёмкость арены плана воркера (int): стеки результатов пакета (RouteBatch); scratch 0 получает арену в <see cref="Router.EnsureWorkers"/>.</summary>
    public const int PlanArenaCapacity = 16_384;

    public readonly OverlaySearcher Searcher;
    public readonly int MaxDirs;
    public readonly int[] Plan;
    public readonly int[] Stk;
    public readonly int[] Dirs;
    public readonly int[] Ubuf = new int[OverlaySearcher.UnpackCap];
    public readonly RouteStep[] Steps;
    public int[]? PlanArena;
    public int ArenaUsed;   // занято в PlanArena текущим пакетом (RouteBatch), сбрасывает главный поток
    public int Sp;
    public int Nd;

    public RouteScratch(NetworkStore net, RouteGraph graph, Overlay? overlay, int windowSteps, int queryWorkCap, bool planArena)
    {
        Searcher = new OverlaySearcher(net, graph, overlay, planBuffers: false);
        if (queryWorkCap > 0)
        {
            Searcher.WorkCap = queryWorkCap;
        }

        int n = graph.DirSegCapacity;
        MaxDirs = Math.Max(1, (windowSteps + 1) / 2);
        Plan = new int[n + 8];
        Stk = new int[n + (OverlaySearcher.UnpackCap * Overlay.MaxLevels) + 16];
        Dirs = new int[MaxDirs + 2];
        Steps = new RouteStep[(2 * MaxDirs) + 2];
        PlanArena = planArena ? new int[PlanArenaCapacity] : null;
    }
}
