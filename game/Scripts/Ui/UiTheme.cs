using Godot;

namespace TrafficCity.Ui;

/// <summary>Размеры каркаса в логических пикселях (до масштаба UI; ui-spec.md §3, §11).</summary>
public static class UiMetrics
{
    public const float TopBarHeight = 40f;
    public const float ToolbarHeight = 56f;
    public const float OptionsHeight = 40f;
    public const float DockWidth = 320f;
    public const float Margin = 8f;
    public const float Radius = Kit.Tokens.RadiusButton;

    /// <summary>Кегль: основной, вторичный, заголовок (из <see cref="Kit.Tokens"/>).</summary>
    public const int FontBody = Kit.Tokens.FontBody;

    public const int FontSecondary = Kit.Tokens.FontSecondary;
    public const int FontHeader = Kit.Tokens.FontHeader;
}

/// <summary>
/// Единая тема UI: стиль «Пластилин» (docs/design/ui-kit.md), собирается <see cref="Kit.ThemeBuilder"/> из <see cref="Kit.Tokens"/>.
/// Здесь остались имена вариантов типов для старых экранов (до переписывания на кит, F4).
/// Варианты типов: <c>LabelSecondary</c>, <c>LabelHeader</c>.
/// </summary>
public static class UiTheme
{
    public const string LabelSecondary = "LabelSecondary";
    public const string LabelHeader = "LabelHeader";

    /// <summary>Полоса сверху (верхняя панель) и снизу (строка опций, тулбар).</summary>
    public const string BarTop = "BarTop";

    public const string BarBottom = "BarBottom";

    /// <summary>Узкая кнопка для верхней полосы (отступ 5 px вместо 10), чтобы деньги, время и кнопки умещались в 1366 px.</summary>
    public const string ButtonCompact = "ButtonCompact";

    public static Theme Build(float panelAlpha = UiPalette.DefaultPanelAlpha) => Kit.ThemeBuilder.Build(panelAlpha);
}
