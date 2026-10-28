using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Цвета интерфейса (ui-spec.md §12; стиль B: светлые плотные панели с контуром 1 px, продолжение карты). Нейтральные берутся из мира
/// (<see cref="Palette"/>: беж инфо-режима, земля, асфальт), чтобы панели не спорили с картой; акцент и семантика заданы здесь.
/// Цвета UI - sRGB (канва Godot не конвертирует). Хексы вне <see cref="Palette"/> и этого класса в UI запрещены.
/// </summary>
public static class UiPalette
{
    /// <summary>Непрозрачность панелей по умолчанию (ui-spec.md §11: 70-100 %, настройка - UI12).</summary>
    public const float DefaultPanelAlpha = 0.95f;

    /// <summary>Фон панели: беж инфо-режима (светлый) - тот же тон, что у мира в инфо-режиме.</summary>
    public static readonly Color PanelSolid = Palette.InfoBgLight;

    public static readonly Color PanelBorder = Palette.Asphalt.Lerp(Palette.Ground, 0.45f);

    /// <summary>Заливка кнопки в покое / при наведении / нажатой.</summary>
    public static readonly Color Button = Palette.Ground;

    public static readonly Color ButtonHover = Palette.LaneLine;
    public static readonly Color ButtonPressed = Palette.Asphalt.Lerp(Palette.Ground, 0.55f);

    /// <summary>Текст: основной ≥ 4.5:1 к <see cref="PanelSolid"/> (проверка скриншотами, ui-spec.md §11).</summary>
    public static readonly Color Text = Palette.Srgb("#262A30");

    public static readonly Color TextSecondary = Palette.Srgb("#5A5E66");
    public static readonly Color TextDisabled = TextSecondary with { A = 0.55f };
    public static readonly Color TextOnAccent = Palette.Srgb("#FFFFFF");

    /// <summary>Акцент: активный инструмент, включённый переключатель.</summary>
    public static Color Accent { get; private set; } = AccentFor(ColorblindMode.Normal);

    /// <summary>Семантика: никогда не только цветом (рядом всегда текст или значок).</summary>
    public static Color Warning { get; private set; } = WarningFor(ColorblindMode.Normal);

    public static Color Error { get; private set; } = ErrorFor(ColorblindMode.Normal);

    /// <summary>Сигналы светофора в таблице инспектора узла (UI3): текст на светлой панели, рядом всегда буква («З», «Ж», «К»).</summary>
    public static Color SignalGreen { get; private set; } = Palette.Srgb("#1E7A34");

    public static Color SignalYellow { get; private set; } = Palette.Srgb("#9A6200");

    public static Color SignalRed { get; private set; } = Error;

    /// <summary>Затемнение под модальным окном.</summary>
    public static readonly Color Scrim = Palette.Srgb("#262A30") with { A = 0.35f };

    /// <summary>Режим палитры для дальтоников (UI12): подменяет акцент и семантику; смысл всегда дублируется текстом или буквой.</summary>
    public static void SetColorblind(ColorblindMode mode)
    {
        Accent = AccentFor(mode);
        Warning = WarningFor(mode);
        Error = ErrorFor(mode);
        SignalRed = Error;
        SignalGreen = mode == ColorblindMode.DeuteranopiaProtanopia ? Palette.Srgb("#0B5FA5") : Palette.Srgb("#1E7A34");
        SignalYellow = mode == ColorblindMode.DeuteranopiaProtanopia ? Palette.Srgb("#7A5A00") : Palette.Srgb("#9A6200");
    }

    public static Color AccentFor(ColorblindMode mode) => mode == ColorblindMode.Tritanopia ? Palette.Srgb("#B0306E") : Palette.Srgb("#2F6FE0");

    public static Color WarningFor(ColorblindMode mode) => mode switch
    {
        ColorblindMode.DeuteranopiaProtanopia => Palette.Srgb("#7A5A00"),
        ColorblindMode.Tritanopia => Palette.Srgb("#9A4A00"),
        _ => Palette.Srgb("#C77A12"),
    };

    public static Color ErrorFor(ColorblindMode mode) => mode == ColorblindMode.DeuteranopiaProtanopia ? Palette.Srgb("#D55E00") : Palette.Srgb("#C93A3A");

    public static Color PanelBg(float alpha) => PanelSolid with { A = alpha };
}
