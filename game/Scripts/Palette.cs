using Godot;

namespace TrafficCity;

/// <summary>
/// ЕДИНСТВЕННОЕ место цветов мира (docs/design/visual-style.md §2). Значения стартовые: подбор по скриншотам - только числами здесь.
/// Два вида полей: <c>Srgb</c>-цвета идут в <c>AlbedoColor</c>/<c>source_color</c>-uniform/окружение (Godot сам переводит в линейное);
/// остальные - цвета вершин и инстансов (<c>COLOR</c> в шейдере, <c>VertexColorUseAsAlbedo</c>), там Godot не конвертирует,
/// поэтому они переведены в линейное пространство при создании (<see cref="Lin"/>).
/// </summary>
public static class Palette
{
    // ---- sRGB: материалы, uniform'ы с source_color, окружение ----
    public static readonly Color Ground = Srgb("#EEEAE2");
    public static readonly Color SkyTop = Srgb("#8EC5F2");
    public static readonly Color SkyHorizon = Srgb("#DCEBF7");
    public static readonly Color SkyGroundHorizon = Srgb("#8C9494");
    public static readonly Color SkyGroundBottom = Srgb("#404545");
    public static readonly Color Fog = Srgb("#DCEBF7");
    public static readonly Color Sun = Srgb("#FFF7E6");
    public static readonly Color Ambient = Srgb("#D9E6FF");

    /// <summary>Heatmap дорог: свободно - средне - затор (HeatPalette.cs берёт отсюда).</summary>
    public static readonly Color HeatRoadLow = Srgb("#1AD933");
    public static readonly Color HeatRoadMid = Srgb("#FFE61A");
    public static readonly Color HeatRoadHigh = Srgb("#F21A1A");

    /// <summary>Heatmap пешеходов: мало - средне - много.</summary>
    public static readonly Color HeatPedLow = Srgb("#33B3FF");
    public static readonly Color HeatPedMid = Srgb("#FFE61A");
    public static readonly Color HeatPedHigh = Srgb("#F21A1A");

    /// <summary>Тонирование инфо-режима: беж «тёмный» и «светлый» (по яркости исходного цвета), visual-style.md §7.</summary>
    public static readonly Color InfoBgDark = Srgb("#D8CCB4");
    public static readonly Color InfoBgLight = Srgb("#F6F1E6");

    // служебные цвета отладочных материалов и UI
    public static readonly Color NodePick = Srgb("#4DCCFF");
    public static readonly Color RouteLine = Srgb("#FFF233");
    public static readonly Color HudText = Srgb("#E6FFE6");
    public static readonly Color InspectorText = Srgb("#FFFFD9");
    public static readonly Color TextShadow = new(0f, 0f, 0f, 0.9f);

    // ---- линейные: цвета вершин и инстансов ----
    public static readonly Color Street = Lin("#FFFFFF");
    public static readonly Color Arterial = Lin("#FCE6A6");
    public static readonly Color Connector = Street; // площадка перекрёстка - как дорога рукава (VS3 уточнит по классу)
    public static readonly Color Closing = Lin("#D8A699");
    public static readonly Color Sidewalk = Lin("#E4E1DA");
    public static readonly Color Zebra = Lin("#FFFFFF");

    /// <summary>Легковая: набор по <c>slot % 5</c> (решение владельца 2026-10-27).</summary>
    public static readonly Color[] CarSlots = { Lin("#E5484D"), Lin("#3E7BFA"), Lin("#F4F4F2"), Lin("#3A3F47"), Lin("#F2B33D") };
    public static readonly Color Bus = Lin("#F28C28");
    public static readonly Color CarPoint = Lin("#2F6FE0");
    public static readonly Color BusPoint = Lin("#E07A1F");
    public static readonly Color Selected = Lin("#FFFFFF");
    public static readonly Color Stuck = Lin("#FF2E2E"); // только в инфо-слое «Трафик»

    /// <summary>Оверлеи без тонирования в беж: a = 0 (шейдер ТС умножает долю тонирования на a).</summary>
    public static readonly Color StuckOverlay = new(Stuck, 0f);
    public static readonly Color SelectedOverlay = new(Selected, 0f);

    // пешеходы: на светлой земле читаются тёмным (плейсхолдер; прежние значения, кроме «идёт»)
    public static readonly Color PedWalking = Lin("#5B6573");
    public static readonly Color PedAtCrossing = Lin("#FFD933");
    public static readonly Color PedCrossing = Lin("#FF801A");
    public static readonly Color PedAtStop = Lin("#CC66FF");
    public static readonly Color PedPassenger = Lin("#73CCFF");

    // фазы переходов/маркеры
    public static readonly Color SignalDontWalk = Lin("#FF3333");
    public static readonly Color SignalWalk = Lin("#3DE659");
    public static readonly Color SignalClear = Lin("#FFDB26");
    public static readonly Color StopQueue = Lin("#CC66FF");

    // BBCode-теги RichTextLabel панели узла (строка-литерал, поэтому не Color)
    public const string BbWalk = "[color=#3ee65a]";
    public const string BbClear = "[color=#ffdb26]";
    public const string BbStop = "[color=#ff4040]";

    public static Color Srgb(string hex) => Color.FromHtml(hex);

    public static Color Lin(string hex) => Color.FromHtml(hex).SrgbToLinear();
}
