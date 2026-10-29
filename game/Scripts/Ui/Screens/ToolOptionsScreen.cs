using System.Collections.Generic;
using Godot;

namespace TrafficCity.Ui;

/// <summary>Строка опций инструмента 52 px над тулбаром (UI4): полоса настоящего инструмента или подпись-заглушка, пока инструмент активен.</summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class ToolOptionsScreen : UiScreen
{
    /// <summary>Высота строки опций: кнопки и списки кита ~40 px + поля.</summary>
    public const float Height = UiMetrics.OptionsHeight;

    private readonly Dictionary<IUiTool, Control> _strips = new();
    private PanelContainer _bar = null!;
    private Label _label = null!;

    public override UiDock Dock => UiDock.Root;

    public override bool IsOpen => _bar.Visible;

    protected override void Build(Control parent)
    {
        _bar = Bar(parent, "ToolOptions", UiTheme.BarBottom, top: false, Height, ToolbarScreen.Height);
        _label = new Label
        {
            ThemeTypeVariation = UiTheme.LabelSecondary,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            HorizontalAlignment = HorizontalAlignment.Left,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bar.AddChild(_label);
    }

    /// <summary>Полоса опций настоящего инструмента: показывается вместо подписи-заглушки, пока инструмент активен.</summary>
    public void Register(IUiTool tool, Control strip)
    {
        _strips[tool] = strip;
        _bar.AddChild(strip);
        strip.Visible = false;
    }

    /// <summary>Видимость полосы ведёт роутер (активный инструмент).</summary>
    public override void SetOpen(bool open)
    {
    }

    public override void Refresh()
    {
        IUiTool? tool = Ui.Router.Tool;
        Control? strip = null;
        if (tool != null)
        {
            _label.Text = "Инструмент «" + tool.DisplayName + "»";
            _strips.TryGetValue(tool, out strip);
        }

        foreach (Control s in _strips.Values)
        {
            s.Visible = ReferenceEquals(s, strip);
        }

        _label.Visible = strip == null;
        _bar.Visible = !Ui.MainMenu && tool != null;
    }
}
