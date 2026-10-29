using System;
using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Страница «Настройки» (ui-spec.md §11, UI12): масштаб UI 75-150 %, палитра для дальтоников, непрозрачность панелей, пресет рендера (F4).
/// Строится в контейнер меню; изменения применяются сразу (<see cref="Changed"/>) и пишутся в <see cref="UiSettings"/>. Слайдеры применяют
/// при отпускании, чтобы масштаб не двигался под курсором во время протяжки; подпись значения обновляется живьём.
/// </summary>
public sealed class UiSettingsPage
{
    private static readonly string[] _modeNames = { "Обычная", "Дейтеранопия / протанопия", "Тританопия" };

    private static int _lastTimeOfDay;

    private readonly UiSettings _s;

    public UiSettingsPage(UiSettings settings) => _s = settings;

    /// <summary>Настройки изменены и сохранены: перерисовать UI.</summary>
    public event Action? Changed;

    /// <summary>Час суток выбран в настройках: null - «Авто» (по игровому времени), иначе час 0..24.</summary>
    public static event Action<float?>? TimeOfDayRequested;

    public void Build(VBoxContainer into, Action back)
    {
        into.AddChild(new Label { Text = "Настройки", ThemeTypeVariation = UiTheme.LabelHeader });

        AddSlider(into, "Масштаб интерфейса", UiScale.UserMinPercent, UiScale.UserMaxPercent, UiScale.UserStepPercent, _s.ScalePercent, v => _s.ScalePercent = UiScale.Snap(v));
        AddSlider(into, "Непрозрачность панелей", UiSettings.AlphaMinPercent, UiSettings.AlphaMaxPercent, UiSettings.AlphaStepPercent, _s.PanelAlphaPercent, v => _s.PanelAlphaPercent = UiSettings.SnapAlpha(v));

        into.AddChild(new Label { Text = "Палитра для дальтоников", ThemeTypeVariation = UiTheme.LabelSecondary });
        var swatches = new HBoxContainer();
        swatches.AddThemeConstantOverride("separation", 4);
        var modes = new UiDropdown(_modeNames) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        modes.Choose((int)_s.Colorblind).OnChanged(i =>
        {
            _s.Colorblind = (ColorblindMode)i;
            Commit();
            FillSwatches(swatches);
        });
        into.AddChild(modes);
        FillSwatches(swatches);
        into.AddChild(swatches);

        into.AddChild(new Label { Text = "Пресет рендера (F4)", ThemeTypeVariation = UiTheme.LabelSecondary });
        string[] presetIds = { "high", "low" };
        var presets = new UiSegmented("Высокий", "Низкий");
        presets.Select(Math.Max(0, Array.IndexOf(presetIds, _s.RenderPreset)));
        presets.OnChanged(i =>
        {
            _s.RenderPreset = presetIds[i];
            Commit();
        });
        into.AddChild(presets);
        into.AddChild(new Label { Text = "Тени, разрешение и сглаживание; пороги LOD - после перезапуска", ThemeTypeVariation = UiTheme.LabelSecondary, AutowrapMode = TextServer.AutowrapMode.WordSmart });

        into.AddChild(new Label { Text = "Время суток", ThemeTypeVariation = UiTheme.LabelSecondary });
        float?[] hours = { null, 8f, 12f, 19f, 1f };
        var tod = new UiSegmented("Авто", "Утро", "День", "Вечер", "Ночь");
        tod.Select(_lastTimeOfDay);
        tod.OnChanged(i =>
        {
            _lastTimeOfDay = i;
            TimeOfDayRequested?.Invoke(hours[i]);
        });
        into.AddChild(tod);
        into.AddChild(new Label { Text = "«Авто» - по игровому времени; остальное фиксирует час", ThemeTypeVariation = UiTheme.LabelSecondary, AutowrapMode = TextServer.AutowrapMode.WordSmart });

        into.AddChild(new UiButton("Назад  Esc", UiButtonKind.Secondary) { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0f, 44f) }.OnPressed(back));
    }

    private void AddSlider(VBoxContainer into, string title, int min, int max, int step, int value, Action<int> set)
    {
        into.AddChild(new Label { Text = title, ThemeTypeVariation = UiTheme.LabelSecondary });
        var slider = new UiSlider(string.Empty, min, max, step, value, "{0} %");
        slider.Slider.FocusMode = Control.FocusModeEnum.None;
        slider.Slider.DragEnded += changed =>
        {
            if (changed)
            {
                set((int)slider.Slider.Value);
                Commit();
            }
        };
        into.AddChild(slider);
    }

    private void FillSwatches(HBoxContainer row)
    {
        foreach (Node c in row.GetChildren())
        {
            row.RemoveChild(c);
            c.QueueFree();
        }

        (Color lo, Color mid, Color hi) = Palette.HeatRoad(_s.Colorblind);
        AddSwatch(row, lo, "Свободно");
        AddSwatch(row, mid, "Средне");
        AddSwatch(row, hi, "Затор");
        AddSwatch(row, UiPalette.WarningFor(_s.Colorblind), "Внимание");
        AddSwatch(row, UiPalette.ErrorFor(_s.Colorblind), "Ошибка");
    }

    private static void AddSwatch(HBoxContainer row, Color c, string text)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 2);
        box.AddChild(new ColorRect { Color = c, CustomMinimumSize = new Vector2(0f, 14f) });
        box.AddChild(new Label { Text = text, ThemeTypeVariation = UiTheme.LabelSecondary, HorizontalAlignment = HorizontalAlignment.Center }.WithSize(11));
        row.AddChild(box);
    }

    private void Commit()
    {
        _s.Save();
        Changed?.Invoke();
    }
}
