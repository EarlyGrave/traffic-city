using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Подсказка в стиле кита: текст + клавиша-подсказка в рамке (<see cref="ThemeBuilder.Kbd"/>). Godot сам оборачивает её
/// в <c>TooltipPanel</c> (кремовая панель из темы). Компонент подключает её из <c>_MakeCustomTooltip</c>:
/// <c>public override GodotObject _MakeCustomTooltip(string forText) => UiTooltip.Build(forText, hint);</c>.
/// Строится при показе подсказки (по наведению), не в кадре.
/// </summary>
public static class UiTooltip
{
    /// <summary>Содержимое подсказки (без панели — её даёт Godot).</summary>
    public static Control Build(string text, string? hint = null)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(new Label { Text = text, ThemeTypeVariation = "TooltipLabel" });
        if (!string.IsNullOrEmpty(hint))
        {
            var key = new PanelContainer { ThemeTypeVariation = ThemeBuilder.Kbd, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            key.AddChild(new Label { Text = hint, ThemeTypeVariation = ThemeBuilder.LabelCaption });
            row.AddChild(key);
        }

        return row;
    }

    /// <summary>Подсказка вместе с панелью — для витрины, где её нужно показать статично.</summary>
    public static PanelContainer BuildPanel(string text, string? hint = null)
    {
        var panel = new PanelContainer { ThemeTypeVariation = "TooltipPanel", SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        panel.AddChild(Build(text, hint));
        return panel;
    }
}
