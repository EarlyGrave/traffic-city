using System.Globalization;
using Godot;

namespace TrafficCity.Ui;

/// <summary>Уведомления (UI6): тосты справа снизу и список «События» (кнопка верхней полосы); Esc закрывает список после панели «Город».</summary>
public sealed class NotifyScreen : UiScreen
{
    private bool _timeSet;

    /// <summary>Тосты, список, источник событий (<see cref="UiNotifyView.Source"/>).</summary>
    public UiNotifyView View { get; private set; } = null!;

    public override UiDock Dock => UiDock.Toasts;

    public override int EscPriority => 40;

    public override bool IsOpen => View.ListOpen;

    protected override void Build(Control parent)
    {
        View = new UiNotifyView(Ui, (VBoxContainer)parent, Ui.Screen<TopBarScreen>().View.NotesButton, Ui.Slot(UiDock.Root));
        if (HasArg("--ui-demo-toasts") || HasArg("--ui-demo-list"))
        {
            // демо: 5 тостов сразу и без угасания (скриншоты); обычные пороги - в значениях по умолчанию UiNotifyView/UiNotifyCenter
            View.Source = new UiDemoEventSource();
            View.MaxVisible = 5;
            View.LifeSec = 3600.0;
            View.Center.ToastIntervalSec = 0.0;
            View.Center.MaxPending = 20;
        }
    }

    public override void SetOpen(bool open) => View.SetListOpen(open);

    public override void Ready()
    {
        if (HasArg("--ui-demo-list"))
        {
            Open();
        }
    }

    public override void Process(double delta)
    {
        View.Update(delta);
        if (Ui.Host is { IsReady: true } h && !_timeSet && h.DayTicks > 0)
        {
            _timeSet = true;
            int day = h.DayTicks;
            View.TimeText = tick => string.Create(CultureInfo.InvariantCulture, $"день {tick / day + 1}, {tick % day * 1440L / day / 60:00}:{tick % day * 1440L / day % 60:00}");
        }
    }
}
