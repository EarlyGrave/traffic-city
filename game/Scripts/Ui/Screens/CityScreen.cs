using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Панель «Город» (UI16) в правой док-панели: кнопка «Город  C» в тулбаре, клавиша C, Esc закрывает раньше списка уведомлений.
/// Данные - <see cref="SimHost.CityPanel"/> (1 Гц); нужен сценарий с жителями.
/// </summary>
public sealed class CityScreen : UiScreen
{
    private UiCityPanel _panel = null!;
    private UiButton _button = null!;
    private int _bottomFrames;   // --ui-city-bottom: через несколько кадров прокрутить к вехам
    private bool _atStart;       // --ui-city: открыть панель, как только город готов (скриншоты)

    public override StringName? Hotkey => UiActions.CityPanel;

    public override int EscPriority => 50;

    public override bool IsOpen => _panel.Visible;

    public override bool CanOpen => Ui.Host?.HasCity ?? false;

    protected override void Build(Control parent)
    {
        _panel = new UiCityPanel(parent);
        _panel.CloseRequested += Close;
        _atStart = HasArg("--ui-city");
        _bottomFrames = HasArg("--ui-city-bottom") ? 20 : 0;
        _button = new UiButton("Город", UiButtonKind.Toggle).WithTooltip("Показатели города и вехи: нужен сценарий с жителями (residents)", "C").Disable();
        _button.FocusMode = Control.FocusModeEnum.None;
        _button.CustomMinimumSize = new Vector2(96f, 36f);
        _button.Toggled += SetOpen;
    }

    public override void Ready() => Ui.Screen<ToolbarScreen>().AddScreenButton(_button);

    public override void SetOpen(bool open)
    {
        _panel.Visible = open;
        _button.SetPressedNoSignal(open);
        if (Ui.Host != null)
        {
            Ui.Host.CityPanelWanted = open;
        }
    }

    public override void Process(double delta)
    {
        if (Ui.Host is not { IsReady: true } h || !h.HasCity)
        {
            return;
        }

        if (_button.Disabled)
        {
            _button.Disabled = false;
            _button.WithTooltip("Показатели города и вехи", "C");
        }

        if (_atStart)
        {
            _atStart = false;
            Open();
        }

        if (_bottomFrames > 0 && --_bottomFrames == 0)
        {
            _panel.ScrollToEnd();
        }

        _panel.Update(h.CityPanel, h.DayTicks);
    }
}
