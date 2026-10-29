using System.Globalization;

namespace TrafficCity.Ui;

/// <summary>Названия вех M1-M6 (progression.md §3.2; ядро имён не хранит - таблица клиента). Одно место для тостов и панели города.</summary>
public static class UiMilestones
{
    private static readonly string[] _names =
    {
        "Город растёт",
        "Час пик",
        "Пробки — дело мэра",
        "Транспортный город",
        "Магистраль",
        "Большой город",
    };

    /// <summary>Название вехи по индексу (0 = M1, как <c>SimEvent.A</c>); неизвестный индекс - «Веха N».</summary>
    public static string Name(int index) =>
        (uint)index < (uint)_names.Length ? _names[index] : "Веха " + (index + 1).ToString(CultureInfo.InvariantCulture);
}
