using Sim.Core.City;

namespace Sim.Core.Api;

// Цели сценария (scenarios-mode.md §2.3, ADR-0038; задача YS1): сводка исхода и условия для панели цели. Без секции goals запросы возвращают false/default.
public sealed partial class Simulation
{
    /// <summary>Есть ли у сценария цели (секция <c>goals</c>).</summary>
    public bool HasGoals => !_disposed && _w.City?.Goals != null;

    /// <summary>Сводка целей: исход, тик исхода, сработавшее условие поражения, число условий; false - целей нет.</summary>
    public bool TryGetGoals(out GoalsInfo info)
    {
        info = default;
        GoalsState? g = _disposed ? null : _w.City?.Goals;
        if (g == null)
        {
            return false;
        }

        info.Outcome = g.Outcome;
        info.OutcomeTick = g.OutcomeTick;
        info.LoseIndex = g.LoseIndex;
        info.AfterWin = g.AfterWin;
        info.WinCount = g.WinCount;
        info.LoseCount = g.LoseCount;
        info.GraceHours = g.GraceHours;
        info.HoursElapsed = g.HoursElapsed;
        return true;
    }

    /// <summary>
    /// Условие <paramref name="i"/>: 0..WinCount-1 - победа, 8..8+LoseCount-1 - поражение. Текущее значение - живое (население, казна, KPI по 24 полным часам),
    /// <c>Met</c> и <c>HeldHours</c> - на последней проверке часа. Индекс вне условий или целей нет - <c>default</c> (<c>Kind = None</c>).
    /// </summary>
    public GoalCondInfo GetGoalCond(int i)
    {
        GoalsState? g = _disposed ? null : _w.City?.Goals;
        if (g == null || (uint)i >= GoalsState.Slots)
        {
            return default;
        }

        bool lose = i >= GoalsState.LoseBase;
        if (lose ? i - GoalsState.LoseBase >= g.LoseCount : i >= g.WinCount)
        {
            return default;
        }

        ref readonly GoalCondDesc d = ref g.Conds[i];
        bool kpiReady = false;
        KpiHour sum = default;
        return new GoalCondInfo
        {
            Kind = d.Kind,
            Kpi = d.Kpi,
            Value = d.Value,
            HoldHours = d.HoldHours,
            HeldHours = g.HeldHours[i],
            Met = (byte)(g.HeldHours[i] > 0 ? 1 : 0),
            IsLose = (byte)(lose ? 1 : 0),
            Current = GoalsState.Current(_w, _w.City!, in d, ref kpiReady, ref sum),
        };
    }
}
