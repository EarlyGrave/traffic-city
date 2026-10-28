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
    public static readonly Color Accent = Palette.Srgb("#2F6FE0");

    /// <summary>Семантика: никогда не только цветом (рядом всегда текст или значок).</summary>
    public static readonly Color Warning = Palette.Srgb("#C77A12");

    public static readonly Color Error = Palette.Srgb("#C93A3A");

    /// <summary>Сигналы светофора в таблице инспектора узла (UI3): текст на светлой панели, рядом всегда буква («З», «Ж», «К»).</summary>
    public static readonly Color SignalGreen = Palette.Srgb("#1E7A34");

    public static readonly Color SignalYellow = Palette.Srgb("#9A6200");

    public static readonly Color SignalRed = Error;

    /// <summary>Затемнение под модальным окном.</summary>
    public static readonly Color Scrim = Palette.Srgb("#262A30") with { A = 0.35f };

    public static Color PanelBg(float alpha) => PanelSolid with { A = alpha };
}
