using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Save;

internal static partial class SaveValidator
{
    /// <summary>
    /// Блок <c>GOAL</c> (ADR-0038): определения проходят <see cref="GoalsState.Validate"/>; часы подряд не отрицательны и только у используемых слотов; исход 0..2,
    /// тик исхода есть ровно при исходе и не больше текущего; индекс поражения - только при поражении и в пределах условий.
    /// </summary>
    internal static LoadStatus ValidateGoals(LoadContext c)
    {
        GoalsState? g = c.W.City?.Goals;
        if (g == null)
        {
            return LoadStatus.InvalidValue;
        }

        if (!GoalsState.Validate(g.Conds, g.WinCount, g.LoseCount, g.GraceHours, g.AfterWin))
        {
            return Bad(c, 0, 5);
        }

        for (int i = 0; i < GoalsState.Slots; i++)
        {
            bool used = i < GoalsState.LoseBase ? i < g.WinCount : i - GoalsState.LoseBase < g.LoseCount;
            if (g.HeldHours[i] < 0 || (!used && g.HeldHours[i] != 0) || g.HeldHours[i] > g.HoursElapsed)
            {
                return Bad(c, i, 9);
            }
        }

        if (g.HoursElapsed < 0)
        {
            return Bad(c, 0, 10);
        }

        if (g.Outcome > GoalOutcome.Lost || (g.Outcome == GoalOutcome.Running) != (g.OutcomeTick == -1) || g.OutcomeTick > c.W.Tick)
        {
            return Bad(c, 0, 11);
        }

        bool lost = g.Outcome == GoalOutcome.Lost;
        if (lost ? g.LoseIndex < 0 || g.LoseIndex >= g.LoseCount : g.LoseIndex != -1)
        {
            return Bad(c, 0, 13);
        }

        return LoadStatus.Ok;
    }
}
