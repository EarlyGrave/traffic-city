using System;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Модальный диалог: затемнение мира + панель по центру с заголовком, текстом и двумя кнопками.
/// Enter — подтвердить, Esc — отменить; клики мимо панели поглощаются. Создаётся один раз и переиспользуется:
/// <c>dialog.Open("Снести район?", "Действие нельзя отменить.", "Снести", "Отмена", danger: true)</c>, ответ — события
/// <see cref="Confirmed"/>/<see cref="Cancelled"/>. Появление/скрытие — прозрачность за <see cref="Tokens.AnimBase"/> в <c>_Process</c>,
/// который выключен вне анимации.
/// </summary>
public sealed partial class UiDialog : Control
{
    private readonly UiPanel _panel;
    private readonly Label _message;
    private readonly UiButton _confirm;
    private readonly UiButton _cancel;
    private float _alpha;
    private float _target;

    public UiDialog()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        AddChild(new ColorRect { Color = ThemeBuilder.Scrim, AnchorRight = 1f, AnchorBottom = 1f, MouseFilter = MouseFilterEnum.Ignore });
        var center = new CenterContainer { AnchorRight = 1f, AnchorBottom = 1f, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(center);
        _panel = new UiPanel(" ", ThemeBuilder.Dialog).MinWidth(320);
        center.AddChild(_panel);
        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(288, 0) };
        _panel.Add(_message);
        _confirm = new UiButton("Ок", UiButtonKind.Primary).OnPressed(Confirm);
        _cancel = new UiButton("Отмена", UiButtonKind.Secondary).OnPressed(Cancel);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", Tokens.Space2);
        row.AddChild(_cancel);
        row.AddChild(_confirm);
        _panel.Add(row);
        SetProcess(false);
    }

    public event Action? Confirmed;

    public event Action? Cancelled;

    public bool IsOpen => _target > 0f;

    /// <summary>Панель диалога — чтобы показать её статично (витрина) или добавить свои элементы.</summary>
    public UiPanel Panel => _panel;

    public void Open(string title, string message, string confirm = "Ок", string cancel = "Отмена", bool danger = false)
    {
        _panel.Title!.Text = title;
        _message.Text = message;
        _confirm.Text = confirm;
        _confirm.ThemeTypeVariation = UiButton.VariationOf(danger ? UiButtonKind.Danger : UiButtonKind.Primary);
        _cancel.Text = cancel;
        _cancel.Visible = cancel.Length > 0;
        Visible = true;
        Fade(1f);
        _confirm.GrabFocus();
    }

    public void Close() => Fade(0f);

    public override void _Input(InputEvent @event)
    {
        if (!IsOpen || @event is not InputEventKey { Pressed: true, Echo: false } k)
        {
            return;
        }

        if (k.Keycode == Key.Escape)
        {
            Cancel();
            GetViewport().SetInputAsHandled();
        }
        else if (k.Keycode is Key.Enter or Key.KpEnter)
        {
            Confirm();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        _alpha = Mathf.MoveToward(_alpha, _target, (float)delta / Tokens.AnimBase);
        Modulate = new Color(1f, 1f, 1f, _alpha);
        if (_alpha == _target)
        {
            SetProcess(false);
            Visible = _target > 0f;
        }
    }

    private void Confirm()
    {
        Close();
        Confirmed?.Invoke();
    }

    private void Cancel()
    {
        Close();
        Cancelled?.Invoke();
    }

    private void Fade(float target)
    {
        _target = target;
        SetProcess(true);
    }
}
