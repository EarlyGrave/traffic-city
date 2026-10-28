using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sim.Core.Api;
using Sim.Host;

namespace TrafficCity.Tools;

/// <summary>Цвет метки у курсора: белый (ок), жёлтый (предупреждение), красный (ошибка). Цвет всегда дублирует текст (ui-spec §6.1).</summary>
public enum LabelSeverity
{
    Ok,
    Warning,
    Error,
}

public readonly record struct LabelContent(string Text, LabelSeverity Severity);

/// <summary>
/// Текст метки у курсора инструмента «Дороги» (ui-spec §5.1, §6.1-6.3): длина и цена, причина отказа с подсказкой, не больше двух предупреждений по тяжести
/// («ещё N» - остальные), подсказка про паузу. Чистая функция состояния инструмента и ответа превью, собирает строки - звать при смене превью, не каждый кадр.
/// </summary>
public static class RoadsLabel
{
    public const int MaxIssueLines = 2;

    public static LabelContent Compose(RoadsTool tool, ToolCommandService svc)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(svc);
        IRoadsWorld world = tool.World;

        switch (tool.Mode)
        {
            case RoadsMode.Build when !tool.CursorValid:
                return default;
            case RoadsMode.Build when !tool.HasAnchor:
                return new LabelContent("Начать здесь" + SnapText(tool.CursorSnap) + "\n" + tool.Choice.Name, LabelSeverity.Ok);
            case RoadsMode.Build when !tool.HasCommand:
                return new LabelContent("Тяните дальше: от " + RoadsTool.MinStepM.ToString("0", CultureInfo.InvariantCulture) + " м" + SnapText(tool.CursorSnap), LabelSeverity.Ok);
            case RoadsMode.Upgrade or RoadsMode.Remove when !tool.HoverValid:
                return new LabelContent(tool.Mode == RoadsMode.Upgrade ? "Наведите на дорогу, которую заменить" : "Наведите на дорогу, которую снести", LabelSeverity.Ok);
            case RoadsMode.Upgrade when tool.HoverSame:
                return new LabelContent("Здесь уже такая дорога", LabelSeverity.Ok);
            default:
                break;
        }

        if (!svc.PreviewMatches(in tool.WantedCommand))
        {
            return new LabelContent("…", LabelSeverity.Ok);
        }

        PreviewReply r = svc.Preview;
        PreviewResult res = r.Result;
        var sb = new StringBuilder();
        if (res.Verdict != RejectReason.None)
        {
            sb.Append(res.Verdict == RejectReason.InsufficientFunds ? InsufficientText(world, res.Cost) : RejectMessages.Label(res.Verdict));
            string hint = RejectMessages.Hint(res.Verdict);
            if (hint.Length > 0)
            {
                sb.Append('\n').Append(hint);
            }

            return new LabelContent(sb.ToString(), LabelSeverity.Error);
        }

        string cost = res.Cost != 0 ? "  " + RejectMessages.Money(res.Cost) : string.Empty;
        switch (tool.Mode)
        {
            case RoadsMode.Build:
                sb.Append(Meters(res.LengthM)).Append(cost).Append(SnapText(tool.CursorSnap));
                break;
            case RoadsMode.Upgrade:
                sb.Append("Заменить на: ").Append(tool.Choice.Name).Append(cost);
                break;
            default:
                sb.Append("Снести").Append(cost);
                break;
        }

        LabelSeverity worst = LabelSeverity.Ok;
        AppendIssues(sb, r, ref worst);
        if (tool.Mode == RoadsMode.Remove)
        {
            Count(sb, res.BusStopsLost, "Остановок потеряется: ", r, IssueCode.StopAccessLost, ref worst);
            Count(sb, res.ZonesLosingAccess, "Зон останется без проезда: ", r, IssueCode.ZoneAccessLost, ref worst);
        }

        if (world.Paused)
        {
            sb.Append("\nПрименится после снятия паузы");
        }

        return new LabelContent(sb.ToString(), worst);
    }

    private static string SnapText(PointSnap s) => s switch
    {
        PointSnap.Node => "  ● узел",
        PointSnap.Grid => "  # сетка",
        _ => string.Empty,
    };

    private static string Meters(float m) => string.Create(CultureInfo.InvariantCulture, $"{m:0} м");

    private static string InsufficientText(IRoadsWorld world, long cost)
    {
        long missing = cost - world.Balance;
        return world.HasBudget && missing > 0 ? RejectMessages.Shortfall(missing) : RejectMessages.Label(RejectReason.InsufficientFunds);
    }

    private static void Count(StringBuilder sb, int n, string prefix, PreviewReply r, IssueCode alreadyIssue, ref LabelSeverity worst)
    {
        if (n <= 0)
        {
            return;
        }

        foreach (PreviewIssue i in r.IssueSpan)
        {
            if (i.Code == alreadyIssue)
            {
                return;
            }
        }

        sb.Append('\n').Append(prefix).Append(n.ToString(CultureInfo.InvariantCulture));
        worst = LabelSeverity.Warning;
    }

    // Не больше MaxIssueLines по тяжести (Error > Warning > Info), порядок ядра при равенстве; остальные - «ещё N»
    private static void AppendIssues(StringBuilder sb, PreviewReply r, ref LabelSeverity worst)
    {
        ReadOnlySpan<PreviewIssue> all = r.IssueSpan;
        int total = Math.Max(all.Length, r.Result.IssueCount);
        if (all.IsEmpty)
        {
            return;
        }

        var order = new List<int>(all.Length);
        for (int sev = (int)IssueSeverity.Error; sev >= (int)IssueSeverity.Info; sev--)
        {
            for (int i = 0; i < all.Length; i++)
            {
                if ((int)all[i].Severity == sev)
                {
                    order.Add(i);
                }
            }
        }

        for (int k = 0; k < order.Count && k < MaxIssueLines; k++)
        {
            PreviewIssue it = all[order[k]];
            sb.Append('\n').Append(RejectMessages.Issue(it.Code, it.Value));
        }

        if (total > MaxIssueLines)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"\nещё {total - MaxIssueLines}"));
        }

        foreach (PreviewIssue it in all)
        {
            if (it.Severity == IssueSeverity.Error)
            {
                worst = LabelSeverity.Error;
                break;
            }

            if (it.Severity == IssueSeverity.Warning)
            {
                worst = LabelSeverity.Warning;
            }
        }
    }
}
