using System;
using System.Collections.Generic;
using Godot;
using TrafficCity.Saves;

namespace TrafficCity.Ui;

/// <summary>
/// Меню на модальном слое (ui-spec.md §2, §12): главное меню при старте («Новая игра», «Продолжить», «Загрузить», «Выход»), меню по Esc в игре
/// («Продолжить», «Сохранить», «Загрузить», «Главное меню», «Выход») и список сейвов. Только рисует и сообщает событиями; сохранение, загрузку и паузу
/// делает <see cref="SimHost"/>. Страница одна на экране: <see cref="HandleCancel"/> по Esc возвращает со списка на предыдущую страницу.
/// </summary>
#pragma warning disable CA1001 // Godot-узлы-поля освобождает дерево сцены
public sealed class UiMenus
{
    private enum Page
    {
        None,
        Main,
        Pause,
        Load,
        Settings,
        Busy,
        Confirm,
    }

    private const float ButtonHeight = 44f;
    private const float PanelWidth = 380f;
    private const double FlashSeconds = 3.0;

    private readonly Control _modal;
    private readonly ColorRect _scrim;
    private readonly ColorRect _backdrop;
    private readonly PanelContainer _panel;
    private readonly VBoxContainer _content;
    private readonly PanelContainer _flashPanel;
    private readonly Label _flashLabel;
    private Label? _pauseStatus;
    private Page _page;
    private Page _loadReturn;
    private Page _settingsReturn;
    private double _flashLeft;

    public UiMenus(Control root, Control modal)
    {
        _modal = modal;
        _scrim = new ColorRect { Name = "Scrim", Color = UiPalette.Scrim, MouseFilter = Control.MouseFilterEnum.Ignore }.WithFullRect();
        _backdrop = new ColorRect { Name = "Backdrop", Color = UiPalette.PanelSolid.Darkened(0.12f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false }.WithFullRect();
        modal.AddChild(_backdrop);
        modal.AddChild(_scrim);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        modal.AddChild(center);
        _panel = new PanelContainer { CustomMinimumSize = new Vector2(PanelWidth, 0f) };
        center.AddChild(_panel);
        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(_content);

        // короткое сообщение «Сохранено» и т.п. по центру под верхней полосой; пока нет тостов (UI6), живёт здесь
        _flashPanel = new PanelContainer { Name = "SaveFlash", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _flashPanel.AnchorLeft = 0.5f;
        _flashPanel.AnchorRight = 0.5f;
        _flashPanel.AnchorTop = 0f;
        _flashPanel.AnchorBottom = 0f;
        _flashPanel.OffsetLeft = -200f;
        _flashPanel.OffsetRight = 200f;
        _flashPanel.OffsetTop = UiMetrics.TopBarHeight + UiMetrics.Margin;
        _flashPanel.GrowHorizontal = Control.GrowDirection.Both;
        _flashLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        _flashPanel.AddChild(_flashLabel);
        root.AddChild(_flashPanel);
    }

    public event Action? NewGameRequested;

    public event Action? ContinueRequested;

    public event Action<SaveInfo>? LoadRequested;

    public event Action? SaveRequested;

    public event Action? MainMenuRequested;

    public event Action? QuitRequested;

    public event Action? ResumeRequested;

    /// <summary>Настройки изменены на странице: UiRoot применяет масштаб, тему, палитру и пресет.</summary>
    public event Action? SettingsChanged;

    /// <summary>Свежий список сейвов (читает каталог при каждом показе страницы).</summary>
    public Func<List<SaveInfo>>? ListSaves { get; set; }

    /// <summary>Перестроить текущую страницу (данные списка появились после её показа).</summary>
    public void Rebuild()
    {
        switch (_page)
        {
            case Page.Main:
                ShowMain();
                break;
            case Page.Pause:
                ShowPause();
                break;
            case Page.Load:
                ShowLoad();
                break;
            case Page.Settings:
                ShowSettings();
                break;
            default:
                break;
        }
    }

    public void ShowMain()
    {
        _page = Page.Main;
        _backdrop.Visible = true;
        _scrim.Visible = false;
        Clear();
        List<SaveInfo> saves = Saves();
        _content.AddChild(new Label { Text = "Traffic City", HorizontalAlignment = HorizontalAlignment.Center }.WithSize(32));
        _content.AddChild(Secondary("Симулятор городского трафика", HorizontalAlignment.Center));
        _content.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 8f) });
        _content.AddChild(Btn("Новая игра", () => NewGameRequested?.Invoke()));
        Button cont = Btn("Продолжить", () => ContinueRequested?.Invoke(), saves.Count == 0);
        cont.TooltipText = saves.Count == 0 ? "Сохранений пока нет" : saves[0].Title + ": " + saves[0].Subtitle;
        _content.AddChild(cont);
        if (saves.Count > 0)
        {
            _content.AddChild(Secondary(saves[0].Title + "  ·  " + saves[0].Subtitle, HorizontalAlignment.Center));
        }

        _content.AddChild(Btn("Загрузить", () => ShowLoad(), saves.Count == 0));
        _content.AddChild(Btn("Настройки", () => ShowSettings()));
        _content.AddChild(Btn("Выход", () => QuitRequested?.Invoke()));
        if (GameFlow.Notice.Length > 0)
        {
            Label err = Secondary(GameFlow.Notice, HorizontalAlignment.Center);
            err.AddThemeColorOverride("font_color", UiPalette.Error);
            err.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _content.AddChild(err);
            GameFlow.Notice = string.Empty;
        }
    }

    public void ShowPause()
    {
        _page = Page.Pause;
        _backdrop.Visible = false;
        _scrim.Visible = true;
        Clear();
        _content.AddChild(new Label { Text = "Меню", ThemeTypeVariation = UiTheme.LabelHeader });
        _content.AddChild(Btn("Продолжить", () => ResumeRequested?.Invoke()));
        _content.AddChild(Btn("Сохранить", () => SaveRequested?.Invoke()));
        _content.AddChild(Btn("Загрузить", () => ShowLoad())); // список может пополниться сохранением из этого же меню - кнопка всегда живая
        _content.AddChild(Btn("Настройки", () => ShowSettings()));
        _content.AddChild(Btn("Главное меню", () => ShowConfirm("Выйти в главное меню?", () => MainMenuRequested?.Invoke())));
        _content.AddChild(Btn("Выход", () => ShowConfirm("Выйти из игры?", () => QuitRequested?.Invoke())));
        _pauseStatus = Secondary("Игра на паузе, пока открыто меню", HorizontalAlignment.Left);
        _content.AddChild(_pauseStatus);
    }

    /// <summary>Подтверждение выхода из паузы. Отслеживания несохранённых изменений нет, поэтому спрашиваем всегда.</summary>
    private void ShowConfirm(string title, Action exit)
    {
        _page = Page.Confirm;
        _pauseStatus = null;
        Clear();
        _content.AddChild(new Label { Text = title, ThemeTypeVariation = UiTheme.LabelHeader });
        _content.AddChild(Secondary("Несохранённый прогресс будет потерян.", HorizontalAlignment.Left));
        _content.AddChild(Btn("Сохранить и выйти", () =>
        {
            SaveRequested?.Invoke();
            exit();
        }));
        _content.AddChild(Btn("Выйти без сохранения", exit));
        _content.AddChild(Btn("Отмена  Esc", ShowPause));
    }

    /// <summary>Список сейвов; назад (кнопка, Esc) - на ту страницу, откуда открыт.</summary>
    public void ShowLoad()
    {
        if (_page is Page.Main or Page.Pause)
        {
            _loadReturn = _page;
        }

        _page = Page.Load;
        Clear();
        _content.AddChild(new Label { Text = "Загрузить игру", ThemeTypeVariation = UiTheme.LabelHeader });
        List<SaveInfo> saves = Saves();
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(PanelWidth + 180f, 330f), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(rows);
        _content.AddChild(scroll);
        if (saves.Count == 0)
        {
            rows.AddChild(Secondary("Сохранений нет", HorizontalAlignment.Left));
        }

        foreach (SaveInfo s in saves)
        {
            SaveInfo captured = s;
            Button row = Btn(s.Title + "\n" + s.Subtitle, () => LoadRequested?.Invoke(captured), false, 58f);
            row.Alignment = HorizontalAlignment.Left;
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.TooltipText = s.Path;
            rows.AddChild(row);
        }

        _content.AddChild(Btn("Назад  Esc", Back));
    }

    /// <summary>Настройки (F10, кнопка в верхней полосе, меню); назад - на ту страницу, откуда открыты.</summary>
    public void ShowSettings()
    {
        if (_page is Page.Main or Page.Pause)
        {
            _settingsReturn = _page;
        }

        _page = Page.Settings;
        Clear();
        var page = new UiSettingsPage(UiSettings.Current);
        page.Changed += () => SettingsChanged?.Invoke();
        page.Build(_content, BackFromSettings);
    }

    public bool SettingsOpen => _page == Page.Settings;

    private void BackFromSettings()
    {
        if (_settingsReturn is Page.Main)
        {
            ShowMain();
        }
        else
        {
            ShowPause();
        }
    }

    /// <summary>Заглушка на время пересоздания сцены (генерация и загрузка занимают секунды, кадры не идут).</summary>
    public void ShowBusy(string text)
    {
        _page = Page.Busy;
        Clear();
        _content.AddChild(new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = UiTheme.LabelHeader });
    }

    /// <summary>Esc: со списка - назад; в главном меню - поглощается (выходить нечем). false - пусть решает роутер (закрыть меню игры).</summary>
    public bool HandleCancel()
    {
        switch (_page)
        {
            case Page.Load:
                Back();
                return true;
            case Page.Settings:
                BackFromSettings();
                return true;
            case Page.Confirm:
                ShowPause();
                return true;
            case Page.Main:
            case Page.Busy:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Сообщение на 3 секунды; ошибка красным. В открытом меню паузы дублируется в его строке состояния.</summary>
    public void Flash(string text, bool error = false)
    {
        _flashLabel.Text = text;
        _flashLabel.AddThemeColorOverride("font_color", error ? UiPalette.Error : UiPalette.Text);
        _flashPanel.Visible = true;
        _flashLeft = FlashSeconds;
        if (_pauseStatus != null && GodotObject.IsInstanceValid(_pauseStatus) && _page == Page.Pause)
        {
            _pauseStatus.Text = text;
        }
    }

    public void Update(double delta)
    {
        if (_flashLeft > 0 && (_flashLeft -= delta) <= 0)
        {
            _flashPanel.Visible = false;
        }
    }

    private void Back()
    {
        if (_loadReturn is Page.Main)
        {
            ShowMain();
        }
        else
        {
            ShowPause();
        }
    }

    private List<SaveInfo> Saves() => ListSaves?.Invoke() ?? new List<SaveInfo>();

    private void Clear()
    {
        _pauseStatus = null;
        foreach (Node c in _content.GetChildren())
        {
            _content.RemoveChild(c);
            c.QueueFree();
        }
    }

    private static Label Secondary(string text, HorizontalAlignment align) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        HorizontalAlignment = align,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static Button Btn(string text, Action onPress, bool disabled = false, float height = ButtonHeight)
    {
        var b = new Button
        {
            Text = text,
            Disabled = disabled,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0f, height),
        };
        b.Pressed += onPress;
        return b;
    }
}

internal static class UiLabelExtensions
{
    public static Label WithSize(this Label l, int size)
    {
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }
}
