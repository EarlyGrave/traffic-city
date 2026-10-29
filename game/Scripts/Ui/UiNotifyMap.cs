using System;
using System.Globalization;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>Важность уведомления (по возрастанию): порог тостов сравнивается с этим порядком.</summary>
public enum NotifySeverity
{
    Info = 0,
    Good = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>Тип объекта, к которому ведёт «Показать».</summary>
public enum NotifySubjectKind
{
    None = 0,
    Vehicle,
    Segment,
    Node,
    Zone,
    Point,      // место события ядра без своего инспектора у клиента (линия, здание, остановка): только точка X/Z
}

/// <summary>
/// Объект уведомления: индекс (и поколение, если есть). Привязка к миру - на стороне <see cref="IUiEventSource"/>; <see cref="HasPoint"/> - точка мира
/// из места события ядра (<see cref="EventPlace"/>, EV1), запасной вариант для «Показать».
/// </summary>
public readonly record struct NotifySubject(NotifySubjectKind Kind, int Index, int Generation = 0, float X = 0f, float Z = 0f, bool HasPoint = false)
{
    public bool IsNone => Kind == NotifySubjectKind.None;

    /// <summary>Объект из места события ядра: сегмент, узел, зона - как раньше, остальное - точка.</summary>
    public static NotifySubject FromPlace(in EventPlace p) => p.ObjKind switch
    {
        EntityKind.None => default,
        EntityKind.Segment => new(NotifySubjectKind.Segment, p.Index, p.Generation, p.X, p.Z, true),
        EntityKind.Node => new(NotifySubjectKind.Node, p.Index, p.Generation, p.X, p.Z, true),
        EntityKind.Zone => new(NotifySubjectKind.Zone, p.Index, 0, p.X, p.Z, true),
        _ => new(NotifySubjectKind.Point, p.Index, p.Generation, p.X, p.Z, true),
    };
}

/// <summary>
/// Результат разбора одного <see cref="SimEvent"/>: ключ агрегации, важность, текст одного события и формы для «×N» (ui-spec.md §6.4).
/// <see cref="Key"/> одинаков у «одинаковых» событий (вид + причина), и только они сливаются за окно 10 с.
/// </summary>
public readonly record struct NotifyRaw(
    int Key,
    NotifySeverity Severity,
    string Text1,
    string One,
    string Few,
    string Many,
    NotifySubject Subject);

/// <summary>
/// Таблица «событие ядра -> короткий русский текст» (чистый C#, без Godot: юнит-тестируется). Только события, которые ядро уже выдаёт.
/// Не показываются (шум, не для игрока): VehicleSpawned, TripCompleted, VehicleUnstuck, BusStopArrived, CommandApplied, *Created/*Removed/*Remapped, NodeRebuilt, ToolOpFinished.
/// Номера 59-62 зарезервированы облаком под прогрессию: сюда не попадают, пока не появятся в <see cref="SimEventKind"/>.
/// </summary>
public static class UiNotifyMap
{
    /// <summary>Разобрать событие; false - событие игроку не показывается.</summary>
    public static bool TryMap(in SimEvent e, out NotifyRaw raw)
    {
        int key = (int)e.Kind;
        switch (e.Kind)
        {
            case SimEventKind.GridlockOn:
                raw = Make(key, NotifySeverity.Warning, "Затор по сети: застряло машин - " + e.A.ToString(CultureInfo.InvariantCulture), "затор", "затора", "заторов", NotifySubject.FromPlace(in e.Place));
                return true;
            case SimEventKind.GridlockOff:
                raw = Make(key, NotifySeverity.Good, "Затор рассосался", "рассасывание затора", "рассасывания затора", "рассасываний затора", default);
                return true;
            case SimEventKind.VehicleStuck:
                raw = Make(key, NotifySeverity.Warning, "Машина застряла", "машина застряла", "машины застряли", "машин застряли", new NotifySubject(NotifySubjectKind.Vehicle, e.A, e.B));
                return true;
            case SimEventKind.DrainStalled:
                raw = Make(key, NotifySeverity.Warning, "Дорога не пустеет: машины не могут уехать", "дорога не пустеет", "дороги не пустеют", "дорог не пустеют", NotifySubject.FromPlace(in e.Place));
                return true;
            case SimEventKind.ZoneAccessLost:
                raw = Make(key, NotifySeverity.Warning, "Район потерял доступ к дорогам", "район без доступа к дорогам", "района без доступа к дорогам", "районов без доступа к дорогам", e.Place.HasPlace ? NotifySubject.FromPlace(in e.Place) : new NotifySubject(NotifySubjectKind.Zone, e.A));
                return true;
            case SimEventKind.JamAt:
                raw = Make(key, NotifySeverity.Warning, "Пробка: стоят машины - " + e.A.ToString(CultureInfo.InvariantCulture), "пробка", "пробки", "пробок", NotifySubject.FromPlace(in e.Place));
                return true;
            case SimEventKind.BusLineProblem:
                string bus = BusIssueText((BusLineIssue)e.A, e.B);
                raw = Make((key * 1000) + e.A, NotifySeverity.Warning, bus, "проблема линии", "проблемы линий", "проблем линий", NotifySubject.FromPlace(in e.Place));
                return true;
            case SimEventKind.ParkingFull:
                raw = Make(key, NotifySeverity.Warning, "Негде припарковаться у здания", "нехватка парковки", "нехватки парковки", "нехваток парковки", NotifySubject.FromPlace(in e.Place));
                return true;
            case SimEventKind.ZoneLotsLost:
                if (e.B <= 0)
                {
                    raw = default;
                    return false;
                }

                raw = Make(key, NotifySeverity.Warning, "Снос дороги: потеряно участков - " + e.B.ToString(CultureInfo.InvariantCulture), "снос с потерей участков", "сноса с потерей участков", "сносов с потерей участков", new NotifySubject(NotifySubjectKind.Segment, e.A));
                return true;
            case SimEventKind.SignalPlanReset:
                raw = Make(key, NotifySeverity.Info, "План светофора сброшен перестройкой", "сброс плана светофора", "сброса плана светофора", "сбросов плана светофора", new NotifySubject(NotifySubjectKind.Node, e.A));
                return true;
            case SimEventKind.CommandRejected:
                string why = RejectText((RejectReason)e.A);
                raw = Make((key * 1000) + e.A, NotifySeverity.Error, "Не удалось: " + why, "отказ: " + why, "отказа: " + why, "отказов: " + why, default);
                return true;
            case SimEventKind.CapacityExhausted:
                string cap = CapacityText((CapacityKind)e.A);
                raw = Make((key * 1000) + e.A, NotifySeverity.Error, "Достигнут предел симуляции: " + cap, "предел: " + cap, "предела: " + cap, "пределов: " + cap, default);
                return true;
            case SimEventKind.EventsOverflow:
                raw = Make(key, NotifySeverity.Error, "Достигнут предел симуляции: часть событий потеряна", "потеря событий", "потери событий", "потерь событий", default);
                return true;
            case SimEventKind.BudgetNegative:
                raw = Make(key, NotifySeverity.Warning, "Казна ушла в минус: " + Money(e.B), "уход казны в минус", "ухода казны в минус", "уходов казны в минус", default);
                return true;
            case SimEventKind.BudgetLimitReached:
                raw = Make(key, NotifySeverity.Error, "Долг у кредитного лимита: казна пуста", "упор в кредитный лимит", "упора в кредитный лимит", "упоров в кредитный лимит", default);
                return true;
            case SimEventKind.BudgetRecovered:
                raw = Make(key, NotifySeverity.Good, "Казна вернулась в лимит: " + Money(e.B), "возврат казны в лимит", "возврата казны в лимит", "возвратов казны в лимит", default);
                return true;
            case SimEventKind.PolicyApplied:
                raw = Make(key, NotifySeverity.Info, "Новая политика вступила в силу", "смена политики", "смены политики", "смен политики", default);
                return true;
            case SimEventKind.BusRouteCreated:
                raw = Make(key, NotifySeverity.Info, "Создана автобусная линия", "новая линия", "новые линии", "новых линий", default);
                return true;
            default:
                raw = default;
                return false;
        }
    }

    /// <summary>Текст записи: один раз - как есть, иначе «×N форма» по склонению числа.</summary>
    public static string Render(in NotifyRaw raw, int count) =>
        count <= 1
            ? raw.Text1
            : "×" + count.ToString(CultureInfo.InvariantCulture) + " " + Plural(count, raw.One, raw.Few, raw.Many);

    /// <summary>Деньги города: «$ 1 200», минус - «−$ 1 200» (валюта - доллар, решение владельца 2026-10-28).</summary>
    public static string Money(int v)
    {
        string digits = Math.Abs((long)v).ToString(CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                sb.Append(' ');
            }

            sb.Append(digits[i]);
        }

        return (v < 0 ? "−$ " : "$ ") + sb;
    }

    /// <summary>Русское склонение: 1 затор, 2-4 затора, 5-20 заторов, 21 затор...</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int m100 = Math.Abs(n) % 100;
        int m10 = m100 % 10;
        if (m100 is >= 11 and <= 14)
        {
            return many;
        }

        return m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
    }

    /// <summary>Причина отказа команды (ui-spec.md §6.3): коротко, без «Не удалось».</summary>
    public static string RejectText(RejectReason r) => r switch
    {
        RejectReason.Capacity => "достигнут предел количества объектов",
        RejectReason.InvalidHandle => "объект уже исчез",
        RejectReason.InvalidArgument => "неверные параметры",
        RejectReason.NotSupported => "пока недоступно",
        RejectReason.TooShort => "слишком короткий участок",
        RejectReason.TooSharp => "слишком крутой поворот",
        RejectReason.Overlap => "пересекает дорогу",
        RejectReason.NodeLimit => "слишком много дорог в узле",
        RejectReason.Busy => "объект занят предыдущей операцией",
        RejectReason.CreatesTrap => "получится тупик",
        RejectReason.InsufficientFunds => "не хватает денег",
        RejectReason.Unreachable => "нет пути от соседней остановки",
        RejectReason.OutOfBounds => "за границей карты",
        RejectReason.BudgetRule => "ставки вне допустимых",
        _ => "отказ ядра",
    };

    /// <summary>Проблема автобусной линии (EV1): <paramref name="value"/> - <c>SimEvent.B</c>.</summary>
    public static string BusIssueText(BusLineIssue i, int value) => i switch
    {
        BusLineIssue.NoBuses => "На автобусной линии нет автобусов",
        BusLineIssue.BusesStuck => "Автобусы линии застряли: " + value.ToString(CultureInfo.InvariantCulture),
        BusLineIssue.Overloaded => "Переполнена остановка: ждут " + value.ToString(CultureInfo.InvariantCulture),
        _ => "Проблема автобусной линии",
    };

    /// <summary>Какая ёмкость исчерпана (для текста «предел симуляции»).</summary>
    public static string CapacityText(CapacityKind k) => k switch
    {
        CapacityKind.Vehicles => "машины",
        CapacityKind.PendingTrips => "очередь поездок",
        CapacityKind.Nodes => "узлы",
        CapacityKind.Segments => "сегменты дорог",
        CapacityKind.Lanes => "полосы",
        CapacityKind.LanePoints => "точки полос",
        CapacityKind.Routes => "маршруты",
        CapacityKind.RouteSteps => "шаги маршрутов",
        CapacityKind.CommandQueue => "очередь команд",
        CapacityKind.Events => "события",
        CapacityKind.Zones => "зоны",
        CapacityKind.BusRoutes => "автобусные линии",
        CapacityKind.BusStops => "остановки",
        _ => "ёмкость",
    };

    private static NotifyRaw Make(int key, NotifySeverity s, string single, string one, string few, string many, NotifySubject subj) =>
        new(key, s, single, one, few, many, subj);
}
