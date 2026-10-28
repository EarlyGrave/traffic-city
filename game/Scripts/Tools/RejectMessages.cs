using System.Globalization;
using Sim.Core.Api;
using Sim.Host;

namespace TrafficCity.Tools;

/// <summary>
/// Тексты для игрока: причины отказа ядра (<see cref="RejectReason"/>) и предупреждения превью (<see cref="IssueCode"/>). Таблица - ui-spec.md §6.3
/// (метка короткая, подсказка «что сделать»), локализация позже уйдёт в ресурс. Чистый C# без Godot: проверяется юнит-тестами (<c>tests/Sim.Host.Tests</c>).
/// Строки-константы не аллоцируют; <see cref="Issue"/> форматирует число - звать при смене превью, не каждый кадр.
/// </summary>
public static class RejectMessages
{
    private static readonly NumberFormatInfo _thousands = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

    /// <summary>Короткая метка у курсора. Для <see cref="RejectReason.None"/> - пустая строка.</summary>
    public static string Label(RejectReason reason) => reason switch
    {
        RejectReason.None => string.Empty,
        RejectReason.Capacity => "Достигнут предел объектов",
        RejectReason.InvalidHandle => "Объект уже удалён",
        RejectReason.InvalidArgument => "Недопустимые параметры",
        RejectReason.UnknownKind => "Неизвестная команда",
        RejectReason.NotSupported => "Пока недоступно",
        RejectReason.TooShort => "Слишком короткий участок",
        RejectReason.TooSharp => "Слишком острый угол",
        RejectReason.Overlap => "Пересекает другую дорогу",
        RejectReason.NodeLimit => "Слишком много рукавов у узла",
        RejectReason.Busy => "Дорога ещё перестраивается",
        RejectReason.CreatesTrap => "Тупик для одностороннего движения",
        RejectReason.InsufficientFunds => "Не хватает денег",
        RejectReason.Unreachable => "Автобус не доедет до остановки",
        RejectReason.ZoneRule => "Здесь нельзя эту зону",
        RejectReason.OutOfBounds => "За границей карты",
        RejectReason.BudgetRule => "Бюджет не допускает такой политики",
        _ => "Не получилось",
    };

    /// <summary>Подсказка «что сделать»; пустая, если добавить нечего.</summary>
    public static string Hint(RejectReason reason) => reason switch
    {
        RejectReason.TooShort => "Тяните дальше: от 20 м",
        RejectReason.TooSharp => "Разведите рукава или увеличьте радиус",
        RejectReason.Overlap => "Ведите до узла или через перекрёсток",
        RejectReason.NodeLimit => "Не больше 6",
        RejectReason.Busy => "Дождитесь окончания",
        RejectReason.CreatesTrap => "Добавьте выезд или сделайте двустороннюю",
        RejectReason.Unreachable => "Проверьте связь дорог",
        _ => string.Empty,
    };

    /// <summary>Нужно ли молча обновить выделение, а не показывать ошибку (объект уже исчез).</summary>
    public static bool IsSilent(RejectReason reason) => reason == RejectReason.InvalidHandle;

    /// <summary>Метка и подсказка одной строкой: «Слишком короткий участок. Тяните дальше: от 20 м».</summary>
    public static string Full(RejectReason reason)
    {
        string hint = Hint(reason);
        return hint.Length == 0 ? Label(reason) : Label(reason) + ". " + hint;
    }

    /// <summary>Метка денег: «не хватает $ N» - для отказа по деньгам с известной нехваткой (экономика, ui-spec §6.2).</summary>
    public static string Shortfall(long missing) => "Не хватает $ " + missing.ToString("N0", _thousands);

    /// <summary>Цена действия для метки у курсора: трата - «$ 4 000», возврат - «+$ 3 100» (ui-spec §6.2).</summary>
    public static string Money(long cost) => cost < 0 ? "+$ " + (-cost).ToString("N0", _thousands) : "$ " + cost.ToString("N0", _thousands);

    /// <summary>Текст предупреждения превью; <paramref name="value"/> - <c>PreviewIssue.Value</c> (число зданий, зон, машин). Ошибки (код &lt; 100) - как <see cref="Label"/>.</summary>
    public static string Issue(IssueCode code, int value)
    {
        if ((int)code is > 0 and < 100)
        {
            return Label((RejectReason)(int)code);
        }

        return code switch
        {
            IssueCode.DeadEnd => "Дорога никуда не ведёт",
            IssueCode.ZoneAccessLost => "Часть зон останется без проезда",
            IssueCode.StopAccessLost => "Остановка останется без проезда",
            IssueCode.PedAccessLost => "Пешеходам будет не пройти",
            IssueCode.DrainNeeded => string.Create(CultureInfo.InvariantCulture, $"Дорога освободится не сразу: сейчас на ней {value}"),
            IssueCode.RoutesAffected => "Изменятся маршруты",
            IssueCode.BusRouteShortened => "Маршрут автобуса сократится",
            IssueCode.SignalPlanReset => "План светофора сбросится",
            IssueCode.NodeRebuildDelayed => "Перекрёсток перестроится позже",
            IssueCode.OverlayRebuild => "Карта слоёв перестроится",
            IssueCode.NearCapacity => "Близок предел объектов",
            IssueCode.UndoUnavailable => "Отменить это действие будет нельзя",
            IssueCode.ZoneLotsLost => string.Create(CultureInfo.InvariantCulture, $"Участков под застройку потеряется: {value}"),
            IssueCode.SetbackShort => "Мало места от соседней дороги до перекрёстка",
            _ => "Есть замечание",
        };
    }

    /// <summary>Тост для пустого стека.</summary>
    /// <summary>Что будет отменено/повторено: «постройка дороги (3 куска)», «снос улицы (2 сегмента)». Для тултипа ↶ ↷; строит строку - звать при смене пункта, не каждый кадр.</summary>
    public static string ActionLabel(Sim.Host.ActionMeta m)
    {
        string name = m.Kind switch
        {
            CommandKind.BuildRoad => "постройка дороги",
            CommandKind.RemoveRoad => "снос улицы",
            CommandKind.UpgradeRoad => "смена профиля дороги",
            CommandKind.SplitRoad => "разделение дороги",
            CommandKind.MergeAtNode => "слияние дорог",
            CommandKind.SetNodeControl or CommandKind.SetArmControl or CommandKind.SetTurnBan or CommandKind.SetSignalPlan or CommandKind.SetLaneFlags => "настройка перекрёстка",
            CommandKind.InsertBusStop or CommandKind.RemoveBusStop or CommandKind.CreateBusRoute or CommandKind.RemoveBusRoute or CommandKind.AppendBusStop or CommandKind.SetBusRouteParams => "правка автобусного маршрута",
            _ => "действие",
        };
        return m.Pieces > 1 ? string.Create(CultureInfo.InvariantCulture, $"{name} ({m.Pieces})") : name;
    }

    public static string UndoNothing(ToolActionKind kind) => kind == ToolActionKind.Redo ? "Нечего повторять" : "Нечего отменять";
}
