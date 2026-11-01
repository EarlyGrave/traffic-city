using Godot;

namespace TrafficCity.Ui;

/// <summary>Верхняя полоса 52 px (UI2): деньги, население, время, «События», настройки; вне инфо-режима клавиши 1/2/3 - скорость ×1/×2/×4.</summary>
public sealed class TopBarScreen : UiScreen
{
    /// <summary>Высота полосы: кнопки кита 40 px + поля + контур (40 px из UiMetrics не хватало).</summary>
    public const float Height = UiMetrics.TopBarHeight;

    private PanelContainer _bar = null!;

    /// <summary>Содержимое полосы.</summary>
    public UiTopBar View { get; private set; } = null!;

    public override UiDock Dock => UiDock.Root;

    public override bool IsOpen => _bar.Visible;

    protected override void Build(Control parent)
    {
        _bar = Bar(parent, "TopBar", UiTheme.BarTop, top: true, Height, 0f);
        View = new UiTopBar(_bar);
    }

    public override void SetOpen(bool open) => _bar.Visible = open;

    public override void Ready() => View.SettingsButton.Pressed += Ui.Screen<MenuScreen>().ToggleSettings;

    public override void Refresh() => SetOpen(!Ui.MainMenu);

    public override void Process(double delta) => View.Update(Ui.Host);

    public override bool HandleInput(InputEvent e)
    {
        if (Ui.Router.InfoActive)
        {
            return false; // в инфо-режиме 1/2/3 - слои (LegendScreen)
        }

        // вне инфо-режима 1/2/3 - скорость ×1/×2/×4 (ui-spec.md §14 п. 7, рекомендация)
        float speed = e.IsActionPressed(UiActions.InfoLayer1) ? 1f
            : e.IsActionPressed(UiActions.InfoLayer2) ? 2f
            : e.IsActionPressed(UiActions.InfoLayer3) ? 4f : 0f;
        if (speed <= 0f || Ui.Host == null)
        {
            return false;
        }

        Ui.Host.SetSpeed(speed);
        Ui.MarkHandled();
        return true;
    }
}
