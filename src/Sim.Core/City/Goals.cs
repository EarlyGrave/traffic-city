using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Цели сценария (scenarios-mode.md §2.3, ADR-0038; задача YS1): определения условий (POD) и состояние отсчёта. Существует только при секции <c>goals</c> и режиме жителей
/// (<see cref="CityWorld.Goals"/>, иначе <c>null</c> - ни одна ветка не исполняется, хэш и сейв прежние). Проверка - раз в игровой час в фазе 9r (однопоточная), O(16), только целые, 0 Б.
/// Условия: индексы 0..7 - победа (все одновременно), 8..15 - поражение (любое). Сначала поражение (после <see cref="GraceHours"/>), потом победа; после исхода проверок нет.
/// </summary>
internal sealed class GoalsState
{
    public const int Slots = 2 * GoalsDesc.MaxConds;
    public const int LoseBase = GoalsDesc.MaxConds;

    // ---- определения (сценарий; в сейве и хэше) ----
    public readonly GoalCondDesc[] Conds = new GoalCondDesc[Slots];
    public int GraceHours;
    public byte AfterWin;
    public byte WinCount;
    public byte LoseCount;

    // ---- состояние ----
    public readonly int[] HeldHours = new int[Slots];   // сколько проверок (часов) подряд условие истинно
    public int HoursElapsed;                            // сдвигов часа от старта
    public byte Outcome;                                // GoalOutcome
    public long OutcomeTick = -1;
    public int LoseIndex = -1;

    public static GoalsState FromDesc(in GoalsDesc d)
    {
        var s = new GoalsState { GraceHours = d.GraceHours, AfterWin = d.AfterWin, WinCount = d.WinCount, LoseCount = d.LoseCount };
        for (int i = 0; i < Slots; i++)
        {
            s.Conds[i] = d.Get(i);
        }

        return s;
    }

    /// <summary>Проверка описания (Create и загрузка сейва): счётчики &lt;= 8, условия в пределах счётчиков - известного вида, остальные слоты нулевые.</summary>
    public static bool Validate(ReadOnlySpan<GoalCondDesc> conds, int winCount, int loseCount, int graceHours, byte afterWin)
    {
        if (conds.Length != Slots || (uint)winCount > GoalsDesc.MaxConds || (uint)loseCount > GoalsDesc.MaxConds || winCount + loseCount == 0 || graceHours < 0 || afterWin > 1)
        {
            return false;
        }

        for (int i = 0; i < Slots; i++)
        {
            ref readonly GoalCondDesc g = ref conds[i];
            bool used = i < LoseBase ? i < winCount : i - LoseBase < loseCount;
            if (!used)
            {
                if (g.Kind != GoalKind.None || g.Kpi != KpiKind.None || g.R != 0 || g.Value != 0 || g.HoldHours != 0)
                {
                    return false;
                }

                continue;
            }

            bool kpi = g.Kind is GoalKind.KpiLe or GoalKind.KpiGe;
            if (g.Kind == GoalKind.None || g.Kind > GoalKind.DayGe || g.R != 0 || g.HoldHours < 0 || (kpi ? g.Kpi == KpiKind.None || g.Kpi > KpiKind.WorkMin : g.Kpi != KpiKind.None))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Фаза 9r, последний тик игрового часа (после показателей, прогрессии и границы суток). После исхода - ничего.</summary>
    public void OnHourEnd(World w, CityWorld c)
    {
        if (Outcome != GoalOutcome.Running)
        {
            return;
        }

        HoursElapsed++;
        bool kpiReady = false;
        KpiHour sum = default;
        bool graceOver = HoursElapsed > GraceHours;

        int lose = -1;
        for (int k = 0; k < LoseCount; k++)
        {
            int i = LoseBase + k;
            bool met = graceOver && IsMet(w, c, in Conds[i], ref kpiReady, ref sum);   // в грейс поражение не проверяется и не копится
            HeldHours[i] = met ? HeldHours[i] + 1 : 0;
            if (lose < 0 && met && HeldHours[i] >= Math.Max(1, Conds[i].HoldHours))
            {
                lose = k;
            }
        }

        bool win = WinCount > 0;
        for (int i = 0; i < WinCount; i++)
        {
            bool met = IsMet(w, c, in Conds[i], ref kpiReady, ref sum);
            HeldHours[i] = met ? HeldHours[i] + 1 : 0;
            win &= met && HeldHours[i] >= Math.Max(1, Conds[i].HoldHours);
        }

        if (lose >= 0)
        {
            Outcome = GoalOutcome.Lost;
            OutcomeTick = w.Tick;
            LoseIndex = lose;
            w.Emit(SimEventKind.ScenarioOutcome, 0, GoalOutcome.Lost, lose);
        }
        else if (win)
        {
            Outcome = GoalOutcome.Won;
            OutcomeTick = w.Tick;
            w.Emit(SimEventKind.ScenarioOutcome, 0, GoalOutcome.Won, -1);
        }
    }

    private static bool IsMet(World w, CityWorld c, in GoalCondDesc g, ref bool kpiReady, ref KpiHour sum)
    {
        int cur = Current(w, c, in g, ref kpiReady, ref sum);
        return g.Kind switch
        {
            GoalKind.PopulationGe => cur >= g.Value,
            GoalKind.PopulationLt => cur < g.Value,
            GoalKind.TreasuryLt => c.Ledger.City < g.Value,
            GoalKind.Insolvent => cur == 1,
            GoalKind.KpiLe => cur >= 0 && cur <= g.Value,
            GoalKind.KpiGe => cur >= 0 && cur >= g.Value,
            GoalKind.Milestone => cur > g.Value,
            GoalKind.DayGe => cur >= g.Value,
            _ => false,   // PrestigeGe - до YS2 (outflow) не вычисляется
        };
    }

    /// <summary>
    /// Текущее значение условия (целое): население, казна (насыщение до int), флаг банкротства 0/1, KPI ×10 по 24 полным часам кольца (-1 - нет данных), взято вех, сутки.
    /// <c>PrestigeGe</c> - -1 (не поддержано до YS2). KPI считается один раз на проверку (<paramref name="kpiReady"/>).
    /// </summary>
    internal static int Current(World w, CityWorld c, in GoalCondDesc g, ref bool kpiReady, ref KpiHour sum)
    {
        switch (g.Kind)
        {
            case GoalKind.PopulationGe:
            case GoalKind.PopulationLt:
                return c.Residents.AliveCount;
            case GoalKind.TreasuryLt:
                return (int)Math.Clamp(c.Ledger.City, int.MinValue, int.MaxValue);
            case GoalKind.Insolvent:
                return c.Budget != null && c.Budget.Insolvent != 0 ? 1 : 0;
            case GoalKind.KpiLe:
            case GoalKind.KpiGe:
                if (!kpiReady)
                {
                    sum = c.Kpi.SumWindow(in w.Counters, false);
                    kpiReady = true;
                }

                return c.Kpi.Value(g.Kpi, in sum);
            case GoalKind.Milestone:
                return w.Progression?.Reached ?? 0;
            case GoalKind.DayGe:
                return c.Day;
            default:
                return -1;
        }
    }

    /// <summary>Раздел хэша целей (только при секции): определения и состояние.</summary>
    public void AddToHash(ref Fnv1a64 h)
    {
        h.Add(GraceHours);
        h.Add(AfterWin);
        h.Add(WinCount);
        h.Add(LoseCount);
        for (int i = 0; i < Slots; i++)
        {
            ref readonly GoalCondDesc g = ref Conds[i];
            h.Add((byte)g.Kind);
            h.Add((byte)g.Kpi);
            h.Add(g.Value);
            h.Add(g.HoldHours);
            h.Add(HeldHours[i]);
        }

        h.Add(HoursElapsed);
        h.Add(Outcome);
        h.Add(OutcomeTick);
        h.Add(LoseIndex);
    }
}
