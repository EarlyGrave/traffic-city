using System;
using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Подтверждение опасного действия (P13, ui-spec §5): затемнённый экран, текст, две кнопки. Esc или «Отмена» закрывают без действия, Enter или основная кнопка выполняют.
/// Пока открыто, клики не доходят до мира (затемнение забирает мышь), а <see cref="UiRoot"/> не пускает в мир и клавиши.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class UiConfirm
{
    private readonly Control _scrim;
    private readonly Label _text;
    private readonly Button _ok;
    private Action? _onOk;

    public UiConfirm(Control parent)
    {
        _scrim = new ColorRect { Name = "Confirm", Color = UiPalette.Scrim, MouseFilter = Control.MouseFilterEnum.Stop, Visible = false }.WithFullRect();
        parent.AddChild(_scrim);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore }.WithFullRect();
        _scrim.AddChild(center);
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        center.AddChild(panel);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 12);
        panel.AddChild(box);

        _text = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(340f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        box.AddChild(_text);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        box.AddChild(row);
        Button cancel = Btn("Отмена");
        cancel.Pressed += () => Cancel();
        row.AddChild(cancel);
        _ok = Btn("Да");
        _ok.Pressed += () => Accept();
        row.AddChild(_ok);
    }

    public bool Visible => _scrim.Visible;

    public string Text => _text.Text;

    public void Ask(string text, string okText, Action onOk)
    {
        _text.Text = text;
        _ok.Text = okText;
        _onOk = onOk;
        _scrim.Visible = true;
    }

    /// <summary>Закрыть без действия; true - было открыто.</summary>
    public bool Cancel()
    {
        if (!_scrim.Visible)
        {
            return false;
        }

        _scrim.Visible = false;
        _onOk = null;
        return true;
    }

    /// <summary>Закрыть и выполнить; true - было открыто.</summary>
    public bool Accept()
    {
        if (!_scrim.Visible)
        {
            return false;
        }

        Action? run = _onOk;
        _scrim.Visible = false;
        _onOk = null;
        run?.Invoke();
        return true;
    }

    private static Button Btn(string text) => new()
    {
        Text = text,
        FocusMode = Control.FocusModeEnum.None,
        CustomMinimumSize = new Vector2(96f, 36f),
    };
}
