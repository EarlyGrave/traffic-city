using Sim.Core.Api;

namespace Sim.Core.Peds;

/// <summary>
/// Фазы пешеходов в порядке тика (pedestrians.md §8): 3b <see cref="Route"/> (запросы путей в бюджете), 4p <see cref="Run"/> (события колеса, автоматы переходов).
/// Фаза 2d - <see cref="PedSpawn"/>. Все последовательные: общие очереди и колесо.
/// </summary>
internal static class PedPhase
{
    /// <summary>Фаза 3b: до <c>pathfind_budget_per_tick</c> запросов в порядке постановки, пока релаксаций меньше <c>work_budget_per_tick</c>; готовый путь -> <c>Walking</c>.</summary>
    public static void Route(World w)
    {
        PedWorld pw = w.Peds!;
        if (pw.PathQueue.IsEmpty)
        {
            return;
        }

        PedStore ps = pw.P;
        int budget = pw.Cfg.PathfindBudgetPerTick;
        long workBudget = pw.Cfg.WorkBudgetPerTick;
        float limit = PedRouter.LimitTicks(in pw.Cfg);
        long work = 0;
        while (budget > 0 && work < workBudget && pw.PathQueue.TryDequeue(out PathRequest req))
        {
            int p = req.Index;
            if (!ps.Pool.IsAlive(p, req.Generation) || ps.State[p] != (byte)PedState.PendingPath)
            {
                continue;
            }

            budget--;
            w.Counters.PedPathQueries++;
            long before = work;
            bool ok = pw.Router.Find(ps.Edge[p], ps.S0Mm[p], ps.GoalEdge[p], ps.GoalSMm[p], ps.Path, p * ps.MaxSteps, ps.MaxSteps, limit, ref work, out int count, out int s0);
            w.Counters.PedPathWork += work - before;
            if (!ok)
            {
                pw.NoPath(w, p);
                continue;
            }

            ps.PathCount[p] = count;
            ps.PathCursor[p] = 0;
            pw.StartEdge(w, p, ps.Path[p * ps.MaxSteps], PedState.Walking, s0);
        }
    }

    /// <summary>Фаза 4p: события колеса (конец ребра, пробуждение жителя) и автоматы переходов.</summary>
    public static void Run(World w) => w.Peds!.RunEvents(w);
}
