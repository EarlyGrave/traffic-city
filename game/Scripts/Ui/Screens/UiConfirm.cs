using System;
using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Подтверждение опасного действия (P13, ui-spec §5): обёртка над <see cref="UiDialog"/> кита (затемнение, панель, две кнопки).
/// Esc или «Отмена» закрывают без действия, Enter или основная кнопка выполняют (Esc/Enter ловит сам диалог, <see cref="ConfirmScreen"/> - запасной путь).
/// Пока открыто, клики не доходят до мира (диалог забирает мышь), а <see cref="UiRoot"/> не пускает в мир и клавиши.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class UiConfirm
{
    private readonly UiDialog _dialog;
    private Action? _onOk;
    private string _text = string.Empty;

    public UiConfirm(Control parent)
    {
        _dialog = new UiDialog { Name = "Confirm" };
        parent.AddChild(_dialog);
        _dialog.Confirmed += () => Finish(true);
        _dialog.Cancelled += () => Finish(false);
    }

    public bool Visible => _dialog.IsOpen;

    public string Text => _text;

    public void Ask(string text, string okText, Action onOk)
    {
        _text = text;
        _onOk = onOk;
        _dialog.Open("Подтверждение", text, okText, "Отмена", danger: true);
    }

    /// <summary>Закрыть без действия; true - было открыто.</summary>
    public bool Cancel()
    {
        if (!_dialog.IsOpen)
        {
            return false;
        }

        _dialog.Close();
        Finish(false);
        return true;
    }

    /// <summary>Закрыть и выполнить; true - было открыто.</summary>
    public bool Accept()
    {
        if (!_dialog.IsOpen)
        {
            return false;
        }

        _dialog.Close();
        Finish(true);
        return true;
    }

    private void Finish(bool ok)
    {
        Action? run = ok ? _onOk : null;
        _onOk = null;
        run?.Invoke();
    }
}
