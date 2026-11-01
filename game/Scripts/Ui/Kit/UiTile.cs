using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Плитка KPI: подпись, крупное значение и изменение бейджем (рост — <see cref="UiTone.Info"/>, падение — <see cref="UiTone.Danger"/>,
/// знак +/− в тексте дублирует цвет). <c>new UiTile("Население", "8 096", "+120", UiTone.Info)</c>.
/// <see cref="Set"/> меняет только текст: строки форматирует вызывающий и только при изменении значения.
/// </summary>
public sealed partial class UiTile : PanelContainer
{
    private readonly Label _caption;
    private readonly Label _value;
    private readonly UiBadge _delta;

    public UiTile()
        : this(string.Empty, string.Empty)
    {
    }

    public UiTile(string caption, string value, string delta = "", UiTone deltaTone = UiTone.Neutral)
    {
        ThemeTypeVariation = ThemeBuilder.Tile;
        CustomMinimumSize = new Vector2(132, 0);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        AddChild(col);
        _caption = new Label { Text = caption, ThemeTypeVariation = UiTheme.LabelSecondary };
        col.AddChild(_caption);
        var row = new HBoxContainer();
        col.AddChild(row);
        _value = new Label { ThemeTypeVariation = ThemeBuilder.LabelValue, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_value);
        _delta = new UiBadge();
        row.AddChild(_delta);
        Set(value, delta, deltaTone);
    }

    public void Set(string value, string delta, UiTone deltaTone)
    {
        _value.Text = value;
        _delta.Visible = delta.Length > 0;
        _delta.Set(delta, deltaTone);
    }
}
