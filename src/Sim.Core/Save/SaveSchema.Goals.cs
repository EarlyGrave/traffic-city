using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Save;

// world_version 15 (scenarios-mode.md §2.3, ADR-0038, задача YS1): цели сценария.
//   Блок GOAL (есть <=> секция goals; при жителях наличие блока в файле и есть признак секции - столбца PARM нет, сейвы без целей прежние кроме номера версии):
//   определения 16 слотов условий (0..7 победа, 8..15 поражение), грейс, after_win, состояние отсчёта и исход.
internal static partial class SaveSchema
{
    private static readonly Func<World, int> _goalSlots = static _ => GoalsState.Slots;

    // ---- GOAL: цели сценария ----
    private static BlockDef GoalsBlock() => new("GOAL", "цели сценария: условия победы (слоты 0..7) и поражения (8..15), грейс, after_win, часы подряд, исход (scenarios-mode.md §2.3)", new[]
    {
        Col.Scalar<int>(1, "GraceHours", w => w.City!.Goals!.GraceHours, (c, v) =>
        {
            c.W.City!.Goals = new GoalsState { GraceHours = v };
            return Range(v, 0, int.MaxValue);
        }),
        Col.Scalar<byte>(2, "AfterWin", w => w.City!.Goals!.AfterWin, (c, v) => c.W.City!.Goals!.AfterWin = v),
        Col.Scalar<byte>(3, "WinCount", w => w.City!.Goals!.WinCount, (c, v) => c.W.City!.Goals!.WinCount = v),
        Col.Scalar<byte>(4, "LoseCount", w => w.City!.Goals!.LoseCount, (c, v) => c.W.City!.Goals!.LoseCount = v),
        Col.Field<GoalCondDesc>(5, "CondKind", w => w.City!.Goals!.Conds, _goalSlots, "16", nameof(GoalCondDesc.Kind), 0, ColumnType.U8),
        Col.Field<GoalCondDesc>(6, "CondKpi", w => w.City!.Goals!.Conds, _goalSlots, "16", nameof(GoalCondDesc.Kpi), 0, ColumnType.U8),
        Col.Field<GoalCondDesc>(7, "CondValue", w => w.City!.Goals!.Conds, _goalSlots, "16", nameof(GoalCondDesc.Value)),
        Col.Field<GoalCondDesc>(8, "CondHoldHours", w => w.City!.Goals!.Conds, _goalSlots, "16", nameof(GoalCondDesc.HoldHours)),
        Col.Array<int>(9, "HeldHours", w => w.City!.Goals!.HeldHours, _goalSlots, "16"),
        Col.Scalar<int>(10, "HoursElapsed", w => w.City!.Goals!.HoursElapsed, (c, v) => c.W.City!.Goals!.HoursElapsed = v),
        Col.Scalar<byte>(11, "Outcome", w => w.City!.Goals!.Outcome, (c, v) => c.W.City!.Goals!.Outcome = v),
        Col.Scalar<long>(12, "OutcomeTick", w => w.City!.Goals!.OutcomeTick, (c, v) => c.W.City!.Goals!.OutcomeTick = v, "-1"),
        Col.Scalar<int>(13, "LoseIndex", w => w.City!.Goals!.LoseIndex, (c, v) => c.W.City!.Goals!.LoseIndex = v, "-1"),
    })
    {
        After = c => SaveValidator.ValidateGoals(c),
        PresentText = "секция goals",
        Present = static w => w.City?.Goals != null,
    };
}
