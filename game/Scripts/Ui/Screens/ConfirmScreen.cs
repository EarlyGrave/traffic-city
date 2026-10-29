using Godot;

namespace TrafficCity.Ui;

/// <summary>Подтверждение опасного действия (P13) поверх всего: Esc закрывает его первым, Enter - соглашается; пока открыто, остальной ввод молчит.</summary>
public sealed class ConfirmScreen : UiScreen
{
    /// <summary>Диалог подтверждения.</summary>
    public UiConfirm View { get; private set; } = null!;

    public override UiDock Dock => UiDock.Root;

    public override int EscPriority => 100;

    public override bool Modal => true;

    public override bool IsOpen => View.Visible;

    protected override void Build(Control parent) => View = new UiConfirm(parent);

    /// <summary>Открывается только через <see cref="UiConfirm.Ask"/> (текст и действие); закрыть - как «Отмена».</summary>
    public override void SetOpen(bool open)
    {
        if (!open)
        {
            View.Cancel();
        }
    }

    public override bool HandleInput(InputEvent e)
    {
        if (!View.Visible)
        {
            return false;
        }

        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
        {
            View.Accept();
            Ui.MarkHandled();
        }

        return true;
    }
}
