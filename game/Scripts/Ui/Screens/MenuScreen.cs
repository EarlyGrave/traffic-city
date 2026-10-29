using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Модальный слой (UI12): главное меню, меню игры (Esc), список сейвов, настройки (F10, шестерёнка). Пока открыт - горячие клавиши мира молчат.
/// Для скриншотов страница открывается аргументом <c>--ui-menu pause|load|settings</c>.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class MenuScreen : UiScreen
{
    private Control _modal = null!;
    private bool _shown;          // страница меню игры уже построена для текущего открытия
    private string _startMenu = string.Empty;

    /// <summary>Страницы меню: события и данные подключает SimHost.</summary>
    public UiMenus View { get; private set; } = null!;

    public override UiDock Dock => UiDock.Root;

    public override int EscPriority => 90;

    public override bool Modal => true;

    public override bool IsOpen => Ui.Router.MenuOpen;

    protected override void Build(Control parent)
    {
        _modal = new Control { Name = "Modal", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _modal.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        parent.AddChild(_modal);
        View = new UiMenus(parent, _modal);
        View.ResumeRequested += Ui.Router.CloseMenu;
        View.SettingsChanged += Ui.ApplySettings;
        _startMenu = ArgValue("--ui-menu");
    }

    public override void SetOpen(bool open)
    {
        if (open)
        {
            Ui.Router.OpenMenu();
        }
        else
        {
            Ui.Router.CloseMenu();
        }
    }

    /// <summary>Шестерёнка и F10: открыть настройки (под меню игры), повторно - закрыть.</summary>
    public void ToggleSettings()
    {
        if (View.SettingsOpen)
        {
            View.HandleCancel();
            if (!Ui.MainMenu)
            {
                Close();
            }

            return;
        }

        if (!Ui.MainMenu)
        {
            Open();
        }

        View.ShowSettings();
    }

    /// <summary>Главное меню вместо игры (SimHost при старте без сценария).</summary>
    public void ShowMain()
    {
        View.ShowMain();
        ShowStartPage();
    }

    public override void Ready()
    {
        if (_startMenu.Length > 0 && !Ui.MainMenu)
        {
            Open(); // скриншот меню игры: страница строится в Refresh
            ShowStartPage();
        }
    }

    /// <summary>Esc: страница меню израсходовала его (назад со списка / главное меню); иначе - дальше по порядку.</summary>
    public override bool HandleEsc() => View.HandleCancel();

    public override bool HandleInput(InputEvent e)
    {
        if (e.IsActionPressed(UiActions.Settings))
        {
            ToggleSettings();
            Ui.MarkHandled();
            return true;
        }

        return Ui.MainMenu || Ui.Router.MenuOpen; // под меню (главным или Esc) горячие клавиши времени и инфо-режима молчат
    }

    public override void Refresh()
    {
        bool menu = Ui.Router.MenuOpen && !Ui.MainMenu;
        if (menu && !_shown)
        {
            View.ShowPause();
        }

        _shown = menu;
        _modal.Visible = menu || Ui.MainMenu;
    }

    public override void Process(double delta) => View.Update(delta);

    private void ShowStartPage()
    {
        if (_startMenu == "load")
        {
            View.ShowLoad();
        }
        else if (_startMenu == "settings")
        {
            View.ShowSettings();
        }
    }
}
