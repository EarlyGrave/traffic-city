using System;
using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Страница «Настройки» (ui-spec.md §11, UI12): масштаб UI 75-150 %, палитра для дальтоников, непрозрачность панелей, пресет рендера (F4).
/// Строится в контейнер меню; изменения применяются сразу (<see cref="Changed"/>) и пишутся в <see cref="UiSettings"/>. Слайдеры применяют
/// при отпускании, чтобы масштаб не двигался под курсором во время протяжки; подпись значения обновляется живьём.
/// </summary>
public sealed class UiSettingsPage
{
    private static readonly string[] _modeNames = { "Обычная", "Дейтеранопия / протанопия", "Тританопия" };

    private readonly UiSettings _s;

    public UiSettingsPage(UiSettings settings) => _s = settings;

    /// <summary>Настройки изменены и сохранены: перерисовать UI.</summary>
    public event Action? Changed;

    public void Build(VBoxContainer into, Action back)
    {
        into.AddChild(new Label { Text = "Настройки", ThemeTypeVariation = UiTheme.LabelHeader });

        AddSlider(into, "Масштаб интерфейса", UiScale.UserMinPercent, UiScale.UserMaxPercent, UiScale.UserStepPercent, _s.ScalePercent, v => _s.ScalePercent = UiScale.Snap(v));
        AddSlider(into, "Непрозрачность панелей", UiSettings.AlphaMinPercent, UiSettings.AlphaMaxPercent, UiSettings.AlphaStepPercent, _s.PanelAlphaPercent, v => _s.PanelAlphaPercent = UiSettings.SnapAlpha(v));

        into.AddChild(new Label { Text = "Палитра для дальтоников", ThemeTypeVariation = UiTheme.LabelSecondary });
        var group = new ButtonGroup();
        var modes = new HBoxContainer();
        modes.AddThemeConstantOverride("separation", 4);
        var swatches = new HBoxContainer();
        swatches.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < _modeNames.Length; i++)
        {
            var mode = (ColorblindMode)i;
            Button b = Toggle(_modeNames[i], group, _s.Colorblind == mode);
            b.Pressed += () =>
            {
                _s.Colorblind = mode;
                Commit();
                FillSwatches(swatches);
            };
            modes.AddChild(b);
        }

        into.AddChild(modes);
        FillSwatches(swatches);
        into.AddChild(swatches);

        into.AddChild(new Label { Text = "Пресет рендера (F4)", ThemeTypeVariation = UiTheme.LabelSecondary });
        var presets = new HBoxContainer();
        presets.AddThemeConstantOverride("separation", 4);
        var pGroup = new ButtonGroup();
        foreach ((string id, string name) in new[] { ("high", "Высокий"), ("low", "Низкий") })
        {
            string captured = id;
            Button b = Toggle(name, pGroup, _s.RenderPreset == id);
            b.Pressed += () =>
            {
                _s.RenderPreset = captured;
                Commit();
            };
            presets.AddChild(b);
        }

        into.AddChild(presets);
        into.AddChild(new Label { Text = "Тени, разрешение и сглаживание; пороги LOD - после перезапуска", ThemeTypeVariation = UiTheme.LabelSecondary, AutowrapMode = TextServer.AutowrapMode.WordSmart });

        var backBtn = new Button { Text = "Назад  Esc", FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0f, 44f) };
        backBtn.Pressed += back;
        into.AddChild(backBtn);
    }

    private static Button Toggle(string text, ButtonGroup group, bool on) => new()
    {
        Text = text,
        ToggleMode = true,
        ButtonGroup = group,
        ButtonPressed = on,
        FocusMode = Control.FocusModeEnum.None,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        ThemeTypeVariation = UiTheme.ButtonCompact,
        CustomMinimumSize = new Vector2(0f, 34f),
    };

    private void AddSlider(VBoxContainer into, string title, int min, int max, int step, int value, Action<int> set)
    {
        var head = new HBoxContainer();
        head.AddChild(new Label { Text = title, ThemeTypeVariation = UiTheme.LabelSecondary, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var val = new Label { Text = value + " %", HorizontalAlignment = HorizontalAlignment.Right };
        head.AddChild(val);
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 24f),
        };
        slider.ValueChanged += v => val.Text = (int)v + " %";
        slider.DragEnded += changed =>
        {
            if (changed)
            {
                set((int)slider.Value);
                Commit();
            }
        };
        into.AddChild(head);
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
