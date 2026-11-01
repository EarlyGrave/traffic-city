using System;
using System.Collections.Generic;
using System.Text.Json;
using Sim.Core.Api;

namespace Sim.Scenarios;

/// <summary>Режим карточки «Новой игры» (<c>meta.mode</c>, scenarios-mode.md §1.2).</summary>
public enum ScenarioMode
{
    EmptyMap = 0,   // "empty_map": обычный старт с пустой карты (Y6)
    Scenario = 1,   // "scenario": старт с подготовкой (ReadyCity)
}

/// <summary>
/// Секция <c>meta</c> сценария (scenarios-mode.md §1.2): только для меню «Новая игра», в ядро не идёт, на хэш и сейв не влияет. Нет секции - сценарий в меню не показывается.
/// </summary>
public sealed record ScenarioMeta(ScenarioMode Mode, string Title, string Summary, int Difficulty, int Order);

// Секции goals и meta (scenarios-mode.md §1.2, §2.2, ADR-0038, задача YS1). Строки (виды условий, имена KPI) - только здесь: ядро получает POD GoalsDesc.
public static partial class ScenarioLoader
{
    private const string GoalsKey = "goals";
    private const string MetaKey = "meta";

    /// <summary>Имена видов условий <see cref="GoalKind"/> в JSON; индекс = номер (0 - нет).</summary>
    internal static readonly string[] GoalKindNames =
    {
        string.Empty, "population_ge", "population_lt", "treasury_lt", "insolvent", "kpi_le", "kpi_ge", "prestige_ge", "milestone", "day_ge",
    };

    /// <summary>
    /// Секция <c>meta</c>: <c>mode</c> (<c>empty_map</c> | <c>scenario</c>), <c>title</c>, <c>summary</c>, <c>difficulty</c> 1..5, <c>order</c>. Нет секции - <c>null</c>.
    /// </summary>
    internal static ScenarioMeta? ParseMeta(JsonObjectReader? r)
    {
        if (r == null)
        {
            return null;
        }

        string mode = r.Str("mode");
        ScenarioMode m = mode switch
        {
            "empty_map" => ScenarioMode.EmptyMap,
            "scenario" => ScenarioMode.Scenario,
            _ => throw new ScenarioException($"{r.ChildPath("mode")}: unknown mode \"{mode}\" (expected empty_map, scenario)"),
        };
        string title = r.Str("title");
        if (title.Length == 0)
        {
            throw new ScenarioException($"{r.ChildPath("title")}: must not be empty");
        }

        var meta = new ScenarioMeta(m, title, r.Str("summary", string.Empty), r.Int("difficulty", 1, min: 1, max: 5), r.Int("order", 0));
        r.Finish();
        return meta;
    }

    /// <summary>
    /// Секция <c>goals</c>: нет секции - нулевая структура (сценарий побитово прежний). Только при <c>demand.model = "residents"</c>. До 8 условий победы (И) и 8 поражения (ИЛИ),
    /// хотя бы одно всего. <c>for_hours</c> / <c>for_days</c> (не оба) - сколько условие держится подряд; <c>grace_days</c> - сутки без проверки поражения; <c>after_win</c>.
    /// Пороги KPI - в единицах показателя (минуты, проценты), в ядро - ×10. <c>insolvent</c> требует <c>economy.city_budget</c>, <c>milestone</c> - прогрессию и веху в списке.
    /// <c>prestige_ge</c> разбирается, но до YS2 (секция <c>outflow</c>) не вычисляется - предупреждение в <paramref name="warnings"/>.
    /// </summary>
    private static GoalsDesc ParseGoals(JsonObjectReader? r, bool residents, bool cityBudget, in ProgressionDesc progression, List<string> warnings)
    {
        if (r == null)
        {
            return default;
        }

        if (!residents)
        {
            throw new ScenarioException($"{r.Path}: allowed only with demand.model = \"residents\"");
        }

        var d = new GoalsDesc { GraceHours = r.Int("grace_days", 0, min: 0, max: 100_000) * 24 };
        string afterWin = r.Str("after_win", "continue");
        d.AfterWin = afterWin switch
        {
            "continue" => 0,
            "end" => 1,
            _ => throw new ScenarioException($"{r.ChildPath("after_win")}: unknown value \"{afterWin}\" (expected continue, end)"),
        };

        JsonElement[] win = r.OptArray("win") ?? Array.Empty<JsonElement>();
        JsonElement[] lose = r.OptArray("lose") ?? Array.Empty<JsonElement>();
        if (win.Length > GoalsDesc.MaxConds || lose.Length > GoalsDesc.MaxConds)
        {
            throw new ScenarioException($"{r.Path}: at most {GoalsDesc.MaxConds} conditions in win and in lose");
        }

        if (win.Length + lose.Length == 0)
        {
            throw new ScenarioException($"{r.Path}: at least one condition in win or lose is required");
        }

        int milestones = progression.Enabled ? progression.Milestones?.Length ?? 0 : 0;
        for (int k = 0; k < win.Length; k++)
        {
            d.Set(k, ParseGoalCond(win[k], JsonObjectReader.Index(r.ChildPath("win"), k), cityBudget, milestones, warnings));
        }

        for (int k = 0; k < lose.Length; k++)
        {
            d.Set(GoalsDesc.MaxConds + k, ParseGoalCond(lose[k], JsonObjectReader.Index(r.ChildPath("lose"), k), cityBudget, milestones, warnings));
        }

        d.WinCount = (byte)win.Length;
        d.LoseCount = (byte)lose.Length;
        r.Finish();
        return d;
    }

    private static GoalCondDesc ParseGoalCond(JsonElement e, string path, bool cityBudget, int milestones, List<string> warnings)
    {
        var c = new JsonObjectReader(e, path);
        string kindName = c.Str("kind");
        int kind = Array.IndexOf(GoalKindNames, kindName);
        if (kind <= 0)
        {
            throw new ScenarioException($"{c.ChildPath("kind")}: unknown kind \"{kindName}\" (expected {string.Join(", ", GoalKindNames[1..])})");
        }

        var g = new GoalCondDesc { Kind = (GoalKind)kind };
        switch (g.Kind)
        {
            case GoalKind.Insolvent:
                if (!cityBudget)
                {
                    throw new ScenarioException($"{path}: \"insolvent\" requires economy.city_budget = true");
                }

                break;
            case GoalKind.KpiLe:
            case GoalKind.KpiGe:
                {
                    string name = c.Str("kpi");
                    int kpi = Array.IndexOf(KpiNames, name);
                    if (kpi <= 0)
                    {
                        throw new ScenarioException($"{c.ChildPath("kpi")}: unknown kpi \"{name}\" (expected {string.Join(", ", KpiNames[1..])})");
                    }

                    g.Kpi = (KpiKind)kpi;
                    g.Value = (int)Math.Round(c.Num("value", min: 0, max: 1_000_000) * 10.0, MidpointRounding.AwayFromZero);
                    break;
                }

            case GoalKind.Milestone:
                if (milestones == 0)
                {
                    throw new ScenarioException($"{path}: \"milestone\" requires an enabled progression section with milestones");
                }

                g.Value = c.Int("value", min: 0, max: milestones - 1);
                break;
            case GoalKind.TreasuryLt:
                g.Value = c.Int("value");
                break;
            case GoalKind.PrestigeGe:
                g.Value = c.Int("value", min: 0, max: 1000);
                warnings.Add($"{path}: \"prestige_ge\" is not evaluated until the outflow section is implemented (YS2); the condition never holds");
                break;
            default:
                g.Value = c.Int("value", min: 0);
                break;
        }

        bool hasHours = c.Has("for_hours");
        bool hasDays = c.Has("for_days");
        if (hasHours && hasDays)
        {
            throw new ScenarioException($"{path}: at most one of \"for_hours\" or \"for_days\"");
        }

        g.HoldHours = hasDays ? c.Int("for_days", min: 0, max: 100_000) * 24 : c.Int("for_hours", 0, min: 0, max: 2_400_000);
        c.Finish();
        return g;
    }
}
