using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Метка у курсора инструмента на Kit: карточка (обычно) / акцентная (предупреждение) / красная (ошибка) плашка темы с текстом.
/// Смысл дублирует сам текст (палитры для дальтоников); вариант меняется только при смене серьёзности.
/// </summary>
public sealed class CursorTag
{
    private int _severity = -1;

    public CursorTag(string name, Control cursorLayer)
    {
        Panel = new PanelContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        Text = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, ThemeTypeVariation = ThemeBuilder.LabelCaption };
        Panel.AddChild(Text);
        cursorLayer.AddChild(Panel);
        SetSeverity(0);
    }

    public PanelContainer Panel { get; }

    public Label Text { get; }

    /// <summary>0 - обычная, 1 - предупреждение, 2 - ошибка.</summary>
    public void SetSeverity(int severity)
    {
        if (severity == _severity)
        {
            return;
        }

        _severity = severity;
        Panel.ThemeTypeVariation = severity switch
        {
            2 => ThemeBuilder.BadgeDanger,
            1 => ThemeBuilder.BadgeAccent,
            _ => ThemeBuilder.Card,
        };
    }
}
