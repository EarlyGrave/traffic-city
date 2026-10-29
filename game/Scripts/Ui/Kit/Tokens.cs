using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>Цвета, которые различаются между палитрами для дальтоников (ui-kit.md: только accent / selected / tool-active).</summary>
public readonly record struct KitPalette(Color Accent, Color Selected, Color ToolActive);

/// <summary>
/// Токены UI-кита «Пластилин» (docs/design/ui-kit.md) — единственный источник цветов, отступов, радиусов, контура, тени,
/// кеглей и длительностей анимаций. Значения в логических пикселях при масштабе 100 %; масштаб 50–150 % даёт
/// <c>content_scale_factor</c> окна (<see cref="UiScale"/>). Цвета UI — sRGB.
/// </summary>
public static class Tokens
{
    // ---- цвета, общие для всех палитр ----
    public static readonly Color Ink = Color.FromHtml("#2E2A35");
    public static readonly Color Surface = Color.FromHtml("#FFF6E6");
    public static readonly Color SurfaceAlt = Color.FromHtml("#FFFFFF");
    public static readonly Color WorldBg = Color.FromHtml("#BFE3D0");
    public static readonly Color DisabledFill = Color.FromHtml("#E6E0D6");
    public static readonly Color DisabledInk = Ink with { A = 0.5f };
    public static readonly Color InkSecondary = Color.FromHtml("#5E5866");

    /// <summary>Опасное действие (снос, удаление). В ui-kit.md не задан — добавлен на витрине F1, смысл всегда дублируется текстом.</summary>
    public static readonly Color Danger = Color.FromHtml("#F4796B");

    /// <summary>Три палитры: обычная и две для дальтоников (индексы = <see cref="ColorblindMode"/>).</summary>
    public static readonly KitPalette[] Palettes =
    {
        new(Color.FromHtml("#FFD36E"), Color.FromHtml("#7CC6FF"), Color.FromHtml("#FF9F8A")), // обычная
        new(Color.FromHtml("#F5E07A"), Color.FromHtml("#56B4E9"), Color.FromHtml("#E8A3C9")), // дейтеранопия/протанопия (Okabe–Ito)
        new(Color.FromHtml("#FFB3A7"), Color.FromHtml("#6FD3C9"), Color.FromHtml("#E85C7A")), // тританопия: красный против бирюзы
    };

    public static ColorblindMode Mode { get; private set; } = ColorblindMode.Normal;

    public static KitPalette Current => Palettes[(int)Mode];

    public static Color Accent => Current.Accent;

    public static Color Selected => Current.Selected;

    public static Color ToolActive => Current.ToolActive;

    public static void SetPalette(ColorblindMode mode) => Mode = mode;

    // ---- геометрия ----

    /// <summary>Шаг сетки отступов.</summary>
    public const int SpaceStep = 4;

    public const int Space1 = 4;
    public const int Space2 = 8;
    public const int Space3 = 12;
    public const int Space4 = 16;
    public const int Space5 = 20;
    public const int Space6 = 24;

    public const int Stroke = 3;
    public const int FocusStroke = 2;
    public const int RadiusButton = 14;
    public const int RadiusPanel = 18;

    /// <summary>Твёрдая тень: смещение вниз без размытия.</summary>
    public const int ShadowButton = 4;

    public const int ShadowPanel = 5;

    /// <summary>Нажатая кнопка проседает на 3 px, тень остаётся 1 px.</summary>
    public const int PressSink = 3;

    public const int ShadowPressed = 1;
    public const int ToolSize = 52;

    // ---- шрифт ----
    public const string FontPath = "res://assets/ui/fonts/Fredoka-Variable.ttf";
    public const int FontWeight = 600;
    public const int FontCaption = 12;
    public const int FontSecondary = 13;
    public const int FontBody = 15;
    public const int FontHeader = 18;
    public const int FontTitle = 24;

    // ---- анимации, с ----
    public const float AnimPress = 0.06f;
    public const float AnimFast = 0.1f;
    public const float AnimBase = 0.16f;
    public const float AnimSlow = 0.25f;

    /// <summary>Наведение: заливка светлее на ~8 % (белая — чуть темнее, светлее некуда).</summary>
    public static Color Hover(Color fill) => fill.V > 0.97f && fill.S < 0.05f ? fill.Darkened(0.05f) : fill.Lerp(Colors.White, 0.08f * 3f);
}
