using System.Runtime.InteropServices;

namespace Sim.Core.Api;

// Цели сценария (scenarios-mode.md §2.2-§2.3, ADR-0038; задача YS1). Плоские POD фиксированного размера без строк и массивов (FFI, будущий Rust-порт).
// Номера перечислений закреплены (сейв, Rust-порт): новые значения - только в конец.

/// <summary>Вид условия цели (<c>goals.win[i].kind</c> / <c>goals.lose[i].kind</c>).</summary>
public enum GoalKind : byte
{
    None = 0,
    PopulationGe = 1,   // живых жителей >= Value
    PopulationLt = 2,   // живых жителей < Value
    TreasuryLt = 3,     // баланс казны (счёт City) < Value
    Insolvent = 4,      // флаг мягкого банкротства казны (city_budget); Value не используется
    KpiLe = 5,          // показатель Kpi за последние 24 ч (×10) <= Value; нет данных - не выполнено
    KpiGe = 6,          // показатель Kpi за последние 24 ч (×10) >= Value; нет данных - не выполнено
    PrestigeGe = 7,     // престиж ‰ >= Value; до YS2 (outflow) не вычисляется - никогда не выполнено
    Milestone = 8,      // взята веха с индексом Value (progression)
    DayGe = 9,          // наступили сутки № Value (счётчик суток города)
}

/// <summary>Исход сценария (<see cref="GoalsInfo.Outcome"/>, <see cref="SimEventKind.ScenarioOutcome"/>.A).</summary>
public static class GoalOutcome
{
    public const byte Running = 0;
    public const byte Won = 1;
    public const byte Lost = 2;
}

/// <summary>Условие цели: вид, показатель (для KPI), значение и сколько часов подряд оно должно держаться (0 - сразу). 12 Б.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public struct GoalCondDesc
{
    public GoalKind Kind;
    public KpiKind Kpi;     // только для KpiLe/KpiGe, иначе None
    public short R;
    public int Value;
    public int HoldHours;   // for_hours / for_days * 24
}

/// <summary>
/// Секция <c>goals</c> сценария (scenarios-mode.md §2.2): победа - все условия <c>W*</c> одновременно, поражение - любое из <c>L*</c>. Сутки переведены в часы загрузчиком.
/// <c>WinCount + LoseCount = 0</c> (нулевая структура) - секции нет: поведение, хэш и сейв побитово прежние. Только при <c>demand.model = "residents"</c>.
/// </summary>
public struct GoalsDesc
{
    public const int MaxConds = 8;

    public int GraceHours;  // поражение не проверяется первые N игровых часов от старта
    public byte AfterWin;   // 0 - continue, 1 - end (только подсказка клиенту: симуляция идёт дальше в обоих случаях)
    public byte WinCount, LoseCount;
    public GoalCondDesc W0, W1, W2, W3, W4, W5, W6, W7;
    public GoalCondDesc L0, L1, L2, L3, L4, L5, L6, L7;

    public readonly bool Enabled => WinCount + LoseCount > 0;

    /// <summary>Условие по сквозному индексу: 0..7 - победа, 8..15 - поражение.</summary>
    public readonly GoalCondDesc Get(int i) => i switch
    {
        0 => W0,
        1 => W1,
        2 => W2,
        3 => W3,
        4 => W4,
        5 => W5,
        6 => W6,
        7 => W7,
        8 => L0,
        9 => L1,
        10 => L2,
        11 => L3,
        12 => L4,
        13 => L5,
        14 => L6,
        _ => L7,
    };

    /// <summary>Записывает условие по сквозному индексу (0..7 - победа, 8..15 - поражение).</summary>
    public void Set(int i, in GoalCondDesc v)
    {
        switch (i)
        {
            case 0: W0 = v; break;
            case 1: W1 = v; break;
            case 2: W2 = v; break;
            case 3: W3 = v; break;
            case 4: W4 = v; break;
            case 5: W5 = v; break;
            case 6: W6 = v; break;
            case 7: W7 = v; break;
            case 8: L0 = v; break;
            case 9: L1 = v; break;
            case 10: L2 = v; break;
            case 11: L3 = v; break;
            case 12: L4 = v; break;
            case 13: L5 = v; break;
            case 14: L6 = v; break;
            default: L7 = v; break;
        }
    }
}

/// <summary>Сводка целей для клиента (<see cref="Simulation.TryGetGoals"/>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct GoalsInfo
{
    public long OutcomeTick;    // тик исхода; -1 - партия идёт
    public byte Outcome;        // GoalOutcome
    public byte AfterWin;
    public byte WinCount, LoseCount;
    public int LoseIndex;       // индекс сработавшего условия поражения (0..LoseCount-1); -1 - нет
    public int GraceHours;
    public int HoursElapsed;    // игровых часов от старта (на сдвигах часа)
}

/// <summary>
/// Условие для панели цели (<see cref="Simulation.GetGoalCond"/>): индекс 0..WinCount-1 - победа, 8..8+LoseCount-1 - поражение. Значения - на последней проверке (сдвиг часа).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GoalCondInfo
{
    public GoalKind Kind;
    public KpiKind Kpi;
    public byte Met;            // 1 - условие выполнено на последней проверке
    public byte IsLose;
    public int Value;
    public int Current;         // текущее значение (население, казна, KPI ×10, сутки...); -1 - нет данных / не поддержано
    public int HeldHours;       // сколько часов подряд выполнено
    public int HoldHours;       // сколько нужно
}
