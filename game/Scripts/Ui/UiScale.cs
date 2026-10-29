using System;
using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Масштаб UI (ui-spec.md §11): <c>content_scale_mode = canvas_items</c>, множитель = база x пользовательский.
/// Фиксированный пиксельный размер (решение владельца 2026-10-29): база = 1.0 при любом разрешении окна, эталон — размер UI
/// на высоте <see cref="RefHeight"/> = 768 px; на большом экране UI занимает меньшую долю. Пользовательский 50-150 % с шагом 5 %.
/// Масштабируется только 2D-канва (UI), 3D-мир рендерится в полном разрешении окна.
/// </summary>
public sealed class UiScale
{
    /// <summary>Эталонная высота: размер UI на ней (база 1.0) сохраняется при любом разрешении.</summary>
    public const float RefHeight = 768f;
    public const int UserMinPercent = 50;
    public const int UserMaxPercent = 150;
    public const int UserStepPercent = 5;

    /// <summary>Пользовательский множитель, % (50-150).</summary>
    public int UserPercent { get; private set; } = 100;

    /// <summary>База: всегда 1.0 (UI не растёт с разрешением).</summary>
    public float Base { get; private set; } = 1f;

    /// <summary>Итоговый множитель, который получает <see cref="Window.ContentScaleFactor"/>.</summary>
    public float Factor => Base * (UserPercent / 100f);

    /// <summary>База не зависит от высоты окна (фиксированный пиксельный размер).</summary>
    public static float BaseFor(int windowHeightPx) => 1f;

    /// <summary>Ограничить процент диапазоном и шагом 5 %.</summary>
    public static int Snap(int percent)
    {
        int p = Math.Clamp(percent, UserMinPercent, UserMaxPercent);
        return (int)(MathF.Round(p / (float)UserStepPercent) * UserStepPercent);
    }

    /// <summary>Применить к окну: режим canvas_items и множитель по текущей высоте окна.</summary>
    public void Apply(Window window)
    {
        Base = BaseFor(window.Size.Y);
        window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        window.ContentScaleFactor = Factor;
    }

    /// <summary>Задать множитель до применения к окну (аргументы запуска).</summary>
    public void SetUserPercentNoApply(int percent) => UserPercent = Snap(percent);

    public void SetUserPercent(int percent, Window window)
    {
        UserPercent = Snap(percent);
        Apply(window);
    }
}
