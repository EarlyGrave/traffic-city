using System;
using System.Globalization;
using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Корень интерфейса (ui-spec.md §12): <see cref="CanvasLayer"/> «Ui» (слой 10) с полноэкранным <see cref="Control"/>, одной темой
/// (<see cref="UiTheme"/>), масштабом (<see cref="UiScale"/>), состоянием и роутером (<see cref="UiRouter"/>).
/// UI1 - каркас: верхняя полоса, тулбар, строка опций, правая док-панель (легенда, инспектор), тосты, модальное меню - пока пустые
/// заглушки с рабочей видимостью по состоянию; наполняют UI2 (полоса, время, тулбар), UI3 (инспекторы), UI4 (инструменты), UI6 (тосты), UI7 (легенда), UI12 (меню).
/// Аргументы запуска (после <c>--</c>): <c>--ui-state overview|build|info|inspect</c>, <c>--ui-scale 75..150</c> (пользовательский множитель, %), <c>--no-ui</c> (скрыть слой: замер вклада UI),
/// <c>--ui-shot файл.png [--ui-shot-frame K]</c> (сохранить кадр окна в его настоящем разрешении на кадре K, по умолчанию 30, и выйти: <c>--write-movie</c> пишет только 1152x648).
/// </summary>
public sealed partial class UiRoot : CanvasLayer
{
    private readonly UiRouter _router = new();
    private readonly UiScale _scale = new();

    private Control _root = null!;
    private PanelContainer _topBar = null!;
    private PanelContainer _toolbar = null!;
    private PanelContainer _toolOptions = null!;
    private VBoxContainer _rightDock = null!;
    private PanelContainer _legend = null!;
    private UiInspector _inspector = null!;
    private VBoxContainer _toasts = null!;
    private Control _modal = null!;
    private Label _toolOptionsLabel = null!;
    private Label _legendTitle = null!;
    private int _legendLayer = 1;
    private Button _infoButton = null!;
    private readonly Button[] _layerButtons = new Button[3];
    private readonly System.Collections.Generic.Dictionary<string, Button> _toolButtons = new();
    private UiTopBar _topBarView = null!;
    private UiMenus _menus = null!;
    private bool _mainMode;          // показано главное меню: игры нет, панели скрыты
    private bool _menuShown;         // страница меню игры (Esc) уже построена для текущего открытия
    private string _startMenu = string.Empty; // --ui-menu pause|load: открыть меню на старте (скриншоты)
    private SimHost? _host;
    private bool _zonesKnown;
    private Window? _window;
    private string _shotPath = string.Empty;
    private int _shotFrame = 30;
    private int _frames;
    private UiNotifyView _notify = null!;
    private bool _demoToasts;
    private bool _demoList;
    private bool _timeSet;
    private UiConfirm _confirm = null!;
    private Control _cursorLayer = null!;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private readonly System.Collections.Generic.Dictionary<IUiTool, Control> _toolStrips = new();
    private static readonly (string Id, string Title, string Key)[] _toolDefs =
    {
        ("roads", "Дороги", "R"), ("zones", "Зоны", "Z"), ("transit", "Транспорт", "T"), ("nodes", "Узлы", "J"), ("bulldoze", "Снос", "X"),
    };

    /// <summary>Уведомления: тосты, список «События», источник событий (<see cref="UiNotifyView.Source"/>).</summary>
    public UiNotifyView Notify => _notify;

    /// <summary>Содержимое легенды инфо-слоя (UI7): SimHost подаёт данные ~2 Гц.</summary>
    public UiLegend Legend { get; private set; } = null!;

    /// <summary>Состояние и роутер событий.</summary>
    public UiRouter Router => _router;

    /// <summary>Масштаб UI.</summary>
    public UiScale UiScaling => _scale;

    /// <summary>Режим, заданный аргументом <c>--ui-state</c> (Overview, если не задан).</summary>
    public UiMode StartMode { get; private set; } = UiMode.Overview;

    // Точки подключения следующих задач (дети корня; размеры и якоря заданы здесь, содержимое - у задачи).

    /// <summary>Верхняя полоса 40 px: деньги, население, время, счётчики (UI2).</summary>
    public PanelContainer TopBar => _topBar;

    /// <summary>Нижний тулбар 56 px: категории инструментов, инфо, undo/redo (UI2).</summary>
    public PanelContainer Toolbar => _toolbar;

    /// <summary>Строка опций инструмента 40 px над тулбаром (UI4).</summary>
    public PanelContainer ToolOptions => _toolOptions;

    /// <summary>Правая док-панель 320 px под верхней полосой: сюда добавляются легенда, инспекторы, списки (контейнер, дети идут друг под другом).</summary>
    public VBoxContainer RightDock => _rightDock;

    /// <summary>Инспектор в правой док-панели (UI3): наполняет SimHost по выбору в мире.</summary>
    public UiInspector Inspector => _inspector;

    /// <summary>Тосты уведомлений справа снизу над тулбаром (UI6).</summary>
    public VBoxContainer Toasts => _toasts;

    /// <summary>Модальный слой (меню, бюджет, настройки): полноэкранный, блокирует клики в мир, пока виден.</summary>
    public Control Modal => _modal;

    /// <summary>Главное меню, меню игры (Esc) и список сейвов: события и данные подключает SimHost.</summary>
    public UiMenus Menus => _menus;

    /// <summary>Показано главное меню (игры нет).</summary>
    public bool InMainMenu => _mainMode;

    /// <summary>Подтверждение опасного действия (P13): снос с потерей участков и т.п.</summary>
    public UiConfirm Confirm => _confirm;

    /// <summary>Слой для меток у курсора (полноэкранный, события мыши пропускает): метка инструмента, подсказки.</summary>
    public Control CursorLayer => _cursorLayer;

    /// <summary>Открыто что-то, что забирает ввод у мира: меню игры или подтверждение.</summary>
    public bool WorldInputBlocked => _router.MenuOpen || _confirm.Visible;

    /// <summary>Создаёт настоящий инструмент по id кнопки или клавиши (<c>roads</c>, <c>bulldoze</c>); null - остаётся заглушка. Задаёт SimHost.</summary>
    public Func<string, IUiTool?>? ToolFactory { get; set; }

    /// <summary>Просьба кнопок ↶ ↷ (Ctrl+Z / Ctrl+Y обрабатывает хост мира).</summary>
    public event Action? UndoRequested;

    public event Action? RedoRequested;

    /// <summary>Состояние кнопок ↶ ↷ и подсказки с глубиной стека.</summary>
    public void SetUndoRedo(int undoDepth, int redoDepth, string undoWhat = "", string redoWhat = "")
    {
        _undoButton.Disabled = undoDepth <= 0;
        _redoButton.Disabled = redoDepth <= 0;
        _undoButton.TooltipText = undoDepth > 0 ? "Отменить: " + undoWhat + " (Ctrl+Z), действий в стеке: " + undoDepth.ToString(CultureInfo.InvariantCulture) : "Отменить (Ctrl+Z): пока нечего";
        _redoButton.TooltipText = redoDepth > 0 ? "Повторить: " + redoWhat + " (Ctrl+Y), действий в стеке: " + redoDepth.ToString(CultureInfo.InvariantCulture) : "Повторить (Ctrl+Y): пока нечего";
    }

    /// <summary>Строка опций настоящего инструмента: показывается вместо подписи-заглушки, пока инструмент активен.</summary>
    public void RegisterToolOptions(IUiTool tool, Control strip)
    {
        _toolStrips[tool] = strip;
        _toolOptions.AddChild(strip);
        strip.Visible = false;
    }

    public override void _EnterTree()
    {
        Layer = 10;
        Name = "Ui";
        UiActions.Register();
        UiSettings saved = UiSettings.Current;
        UiPalette.SetColorblind(saved.Colorblind);
        HeatPalette.SetMode(saved.Colorblind);
        _scale.SetUserPercentNoApply(saved.ScalePercent);
        ParseArgs(OS.GetCmdlineUserArgs());
        Build();
        _window = GetWindow();
        _scale.Apply(_window);
        _window.SizeChanged += OnWindowSizeChanged;
        _router.StateChanged += Refresh;
    }

    public override void _Ready()
    {
        switch (StartMode)
        {
            case UiMode.Build:
                _router.SetTool(MakeTool("roads"));
                break;
            case UiMode.Inspect:
                _router.SetInspector(InspectKind.Demo);
                break;
            default:
                break; // info: состояние мира выставляет SimHost (Snap) и зеркалит в роутер
        }

        _topBarView.SettingsButton.Pressed += OpenSettings;
        if (_startMenu.Length > 0 && !_mainMode)
        {
            _router.OpenMenu(); // скриншот меню игры: страница строится в Refresh
            if (_startMenu == "load")
            {
                _menus.ShowLoad();
            }
            else if (_startMenu == "settings")
            {
                _menus.ShowSettings();
            }
        }

        if (_demoList)
        {
            _notify.SetListOpen(true);
        }

        Refresh();
        LogScale();
    }

    /// <summary>
    /// Применить <see cref="UiSettings.Current"/>: палитра (UI и heatmap), тема с непрозрачностью панелей, масштаб, пресет рендера.
    /// Вызывается из страницы настроек и F10; разово, не в кадре. Тема пересобирается целиком (UI1), своих копий стилей нет.
    /// </summary>
    public void ApplySettings()
    {
        UiSettings s = UiSettings.Current;
        UiPalette.SetColorblind(s.Colorblind);
        HeatPalette.SetMode(s.Colorblind);
        _root.Theme = UiTheme.Build(s.PanelAlpha);
        _scale.SetUserPercent(s.ScalePercent, _window!);
        (_host ??= GetNodeOrNull<SimHost>("../SimHost"))?.SetRenderPreset(s.RenderPreset);
        Refresh();
        _menus.Rebuild();
    }

    private void OpenSettings()
    {
        if (_menus.SettingsOpen)
        {
            _menus.HandleCancel();
            if (!_mainMode)
            {
                _router.CloseMenu();
            }

            return;
        }

        if (!_mainMode)
        {
            _router.OpenMenu();
        }

        _menus.ShowSettings();
    }

    private void LogScale()
    {
        Vector2I size = _window!.Size;
        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[ui] window {size.X}x{size.Y}, scale base {_scale.Base:0.###} x user {_scale.UserPercent}% = {_scale.Factor:0.###}, state {StartMode}"));
    }

    public override void _ExitTree()
    {
        _router.StateChanged -= Refresh;
        if (_window != null)
        {
            _window.SizeChanged -= OnWindowSizeChanged;
        }
    }

    public override void _Process(double delta)
    {
        _topBarView.Update(this);
        _menus.Update(delta);
        UpdateCapabilities();
        _notify.Update(delta);
        UpdateCity();
        if (_host is { IsReady: true } h && !_timeSet && h.DayTicks > 0)
        {
            _timeSet = true;
            int day = h.DayTicks;
            _notify.TimeText = tick => string.Create(CultureInfo.InvariantCulture, $"день {tick / day + 1}, {tick % day * 1440L / day / 60:00}:{tick % day * 1440L / day % 60:00}");
        }

        if (_shotPath.Length > 0 && ++_frames == _shotFrame)
        {
            RenderingServer.FramePostDraw += OnShotDraw;
        }
    }

    private void OnShotDraw()
    {
        RenderingServer.FramePostDraw -= OnShotDraw;
        Image img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(_shotPath);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui-shot] {_shotPath} {img.GetWidth()}x{img.GetHeight()} {err}"));
        GetTree().Quit();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Esc живёт здесь, а не в SimHost: порядок «протяжка -> инструмент -> инспектор -> инфо -> меню» знает только роутер
        if (@event.IsActionPressed(UiActions.Cancel))
        {
            if (_confirm.Visible)
            {
                _confirm.Cancel(); // подтверждение закрывается первым: оно поверх всего
            }
            else if (_menus.HandleCancel())
            {
                // страница меню израсходовала Esc (назад со списка / главное меню)
            }
            else if (!_router.MenuOpen && CloseCity())
            {
                // панель «Город» закрывается раньше списка уведомлений и роутера
            }
            else if (!_router.MenuOpen && _notify.ListOpen)
            {
                _notify.SetListOpen(false); // список уведомлений закрывается первым: он поверх всего, кроме модального меню
            }
            else
            {
                _router.Cancel();
            }

            GetViewport().SetInputAsHandled();
        }
        else if (_confirm.Visible)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
            {
                _confirm.Accept();
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event.IsActionPressed(UiActions.Settings))
        {
            OpenSettings();
            GetViewport().SetInputAsHandled();
        }
        else if (_mainMode || _router.MenuOpen)
        {
            // под меню (главным или Esc) горячие клавиши времени и инфо-режима молчат
        }
        else if (UiActions.Pressed(@event, UiActions.CityPanel) && (_host?.HasCity ?? false))
        {
            ToggleCity();
        }
        else if (UiActions.Pressed(@event, UiActions.ToolRoads))
        {
            ToggleTool("roads");
        }
        else if (UiActions.Pressed(@event, UiActions.ToolBulldoze))
        {
            ToggleTool("bulldoze");
        }
        else if (UiActions.Pressed(@event, UiActions.ToolZones) && (_host?.HasCity ?? false))
        {
            ToggleTool("zones");
        }
        else if (@event.IsActionPressed(UiActions.InfoToggle))
        {
            _router.ToggleInfo();
        }
        else if (!_router.InfoActive)
        {
            // вне инфо-режима 1/2/3 - скорость ×1/×2/×4 (ui-spec.md §14 п. 7, рекомендация); в инфо-режиме те же клавиши - слои
            float speed = @event.IsActionPressed(UiActions.InfoLayer1) ? 1f
                : @event.IsActionPressed(UiActions.InfoLayer2) ? 2f
                : @event.IsActionPressed(UiActions.InfoLayer3) ? 4f : 0f;
            if (speed > 0f && _host != null)
            {
                _host.SetSpeed(speed);
                GetViewport().SetInputAsHandled();
            }
        }
        else
        {
            // слои 1-3 переключаются только в инфо-режиме (вне него эти клавиши зарезервированы под скорость, ui-spec.md §14 п. 7)
            if (@event.IsActionPressed(UiActions.InfoLayer1))
            {
                _router.SetInfoLayer(1);
            }
            else if (@event.IsActionPressed(UiActions.InfoLayer2))
            {
                _router.SetInfoLayer(2);
            }
            else if (@event.IsActionPressed(UiActions.InfoLayer3))
            {
                _router.SetInfoLayer(3);
            }
        }
    }

    /// <summary>Категории, которым нужен режим жителей (сейчас «Зоны»), серые с причиной в тултипе, пока сценарий без жителей (ui-spec.md §4, P2).</summary>
    private void UpdateCapabilities()
    {
        _host ??= GetNodeOrNull<SimHost>("../SimHost");
        if (_zonesKnown || _host == null || !_host.IsReady)
        {
            return;
        }

        _zonesKnown = true;
        if (!_host.HasCity && _toolButtons.TryGetValue("zones", out Button? zones))
        {
            zones.Disabled = true;
            zones.TooltipText = "Зоны (Z): нужно: сценарий с жителями (residents)";
        }
    }

    private void OnWindowSizeChanged() => _scale.Apply(_window!);

    private void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ui-state" && i + 1 < args.Length)
            {
                StartMode = args[++i] switch
                {
                    "build" => UiMode.Build,
                    "info" => UiMode.Info,
                    "inspect" => UiMode.Inspect,
                    _ => UiMode.Overview,
                };
            }
            else if (args[i] == "--ui-scale" && i + 1 < args.Length
                && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
            {
                _scale.SetUserPercentNoApply(pct);
            }
            else if (args[i] == "--ui-cb" && i + 1 < args.Length
                && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cb) && cb is >= 0 and <= 2)
            {
                // палитра для дальтоников только на этот запуск (скриншоты), в settings.cfg не пишется
                UiPalette.SetColorblind((ColorblindMode)cb);
                HeatPalette.SetMode((ColorblindMode)cb);
            }
            else if (args[i] == "--ui-shot" && i + 1 < args.Length)
            {
                _shotPath = args[++i];
            }
            else if (args[i] == "--ui-shot-frame" && i + 1 < args.Length
                && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
            {
                _shotFrame = frame;
            }
            else if (args[i] == "--ui-menu" && i + 1 < args.Length)
            {
                _startMenu = args[++i];
            }
            else if (args[i] == "--ui-demo-toasts")
            {
                _demoToasts = true;
            }
            else if (args[i] == "--ui-demo-list")
            {
                _demoToasts = true;
                _demoList = true;
            }
            else if (args[i] == "--no-ui")
            {
                Visible = false;
            }
        }
    }

    // ---- дерево ----

    private void Build()
    {
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Build() };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        _topBar = Bar("TopBar", UiTheme.BarTop, top: true, UiMetrics.TopBarHeight, 0f);
        _topBarView = new UiTopBar(_topBar);

        _toolbar = Bar("Toolbar", UiTheme.BarBottom, top: false, UiMetrics.ToolbarHeight, 0f);
        BuildToolbar();

        _toolOptions = Bar("ToolOptions", UiTheme.BarBottom, top: false, UiMetrics.OptionsHeight, UiMetrics.ToolbarHeight);
        _toolOptionsLabel = Secondary(string.Empty, Control.SizeFlags.ExpandFill, HorizontalAlignment.Left);
        _toolOptions.AddChild(_toolOptionsLabel);

        _rightDock = new VBoxContainer { Name = "RightDock", MouseFilter = Control.MouseFilterEnum.Ignore };
        _rightDock.AnchorLeft = 1f;
        _rightDock.AnchorRight = 1f;
        _rightDock.AnchorTop = 0f;
        _rightDock.AnchorBottom = 1f;
        _rightDock.OffsetLeft = -(UiMetrics.DockWidth + UiMetrics.Margin);
        _rightDock.OffsetRight = -UiMetrics.Margin;
        _rightDock.OffsetTop = UiMetrics.TopBarHeight + UiMetrics.Margin;
        _rightDock.OffsetBottom = -(UiMetrics.ToolbarHeight + UiMetrics.OptionsHeight + UiMetrics.Margin);
        _rightDock.AddThemeConstantOverride("separation", (int)UiMetrics.Margin);
        _root.AddChild(_rightDock);
        BuildLegend();
        BuildInspector();
        BuildCity();

        _toasts = new VBoxContainer { Name = "Toasts", MouseFilter = Control.MouseFilterEnum.Ignore };
        _toasts.AnchorLeft = 1f;
        _toasts.AnchorRight = 1f;
        _toasts.AnchorTop = 1f;
        _toasts.AnchorBottom = 1f;
        _toasts.OffsetLeft = -(UiMetrics.DockWidth + UiMetrics.Margin);
        _toasts.OffsetRight = -UiMetrics.Margin;
        _toasts.OffsetTop = -(UiMetrics.ToolbarHeight + UiMetrics.OptionsHeight + 320f);
        _toasts.OffsetBottom = -(UiMetrics.ToolbarHeight + UiMetrics.OptionsHeight + UiMetrics.Margin);
        _toasts.GrowVertical = Control.GrowDirection.Begin;
        _root.AddChild(_toasts);

        _notify = new UiNotifyView(this, _toasts, _topBarView.NotesButton, _root);
        if (_demoToasts)
        {
            // демо: 5 тостов сразу и без угасания (скриншоты); обычные пороги - в значениях по умолчанию UiNotifyView/UiNotifyCenter
            _notify.Source = new UiDemoEventSource();
            _notify.MaxVisible = 5;
            _notify.LifeSec = 3600.0;
            _notify.Center.ToastIntervalSec = 0.0;
            _notify.Center.MaxPending = 20;
        }

        _cursorLayer = new Control { Name = "CursorLayer", MouseFilter = Control.MouseFilterEnum.Ignore }.WithFullRect();
        _root.AddChild(_cursorLayer);
        BuildModal();
        _confirm = new UiConfirm(_root);
    }

    private IUiTool MakeTool(string id)
    {
        IUiTool? real = ToolFactory?.Invoke(id);
        if (real != null)
        {
            return real;
        }

        foreach ((string tid, string title, _) in _toolDefs)
        {
            if (tid == id)
            {
                return new UiPlaceholderTool(id, title);
            }
        }

        return new UiPlaceholderTool(id, id);
    }

    /// <summary>Горячая клавиша инструмента: включает его, повторное нажатие (того же режима) выключает.</summary>
    private void ToggleTool(string id)
    {
        if (_router.Tool != null && _router.Tool.Id == id)
        {
            _router.SetTool(null);
        }
        else
        {
            _router.SetTool(MakeTool(id));
        }
    }

    /// <summary>Полоса на всю ширину у верхнего или нижнего края; <paramref name="lift"/> - подъём над нижним краем.</summary>
    private PanelContainer Bar(string name, string variation, bool top, float height, float lift)
    {
        var p = new PanelContainer
        {
            Name = name,
            ThemeTypeVariation = variation,
            CustomMinimumSize = new Vector2(0f, height),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        p.AnchorLeft = 0f;
        p.AnchorRight = 1f;
        p.AnchorTop = top ? 0f : 1f;
        p.AnchorBottom = top ? 0f : 1f;
        p.OffsetLeft = 0f;
        p.OffsetRight = 0f;
        p.OffsetTop = top ? 0f : -(height + lift);
        p.OffsetBottom = top ? height : -lift;
        _root.AddChild(p);
        return p;
    }

    private static Label Secondary(string text, Control.SizeFlags h, HorizontalAlignment align) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        SizeFlagsHorizontal = h,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        HorizontalAlignment = align,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static Button MakeButton(string text, bool toggle, bool disabled, float minWidth = 0f) => new()
    {
        Text = text,
        ToggleMode = toggle,
        Disabled = disabled,
        // фокус на кнопке заставил бы Space/Enter нажимать её и «съедал» бы клавиши времени (действия обрабатываются в _UnhandledInput)
        FocusMode = Control.FocusModeEnum.None,
        CustomMinimumSize = new Vector2(minWidth, 36f),
    };

    private void BuildToolbar()
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        _toolbar.AddChild(row);

        // настоящие инструменты - UI4 и T13; пока кнопка включает заглушку (строка опций) - так роутер и Esc проверяются руками.
        // Клавиши R/Z/J/X в UI1 не заняты (T - время суток, §9 ui-spec: перенос на UI2/UI4)
        foreach ((string id, string title, string key) in _toolDefs)
        {
            Button b = MakeButton(title, toggle: true, disabled: false, 96f);
            b.TooltipText = title + " (" + key + ")";
            b.Toggled += on => _router.SetTool(on ? MakeTool(id) : null);
            row.AddChild(b);
            _toolButtons[id] = b;
        }

        row.AddChild(new VSeparator());
        _infoButton = MakeButton("Инфо  F1", toggle: true, disabled: false, 96f);
        _infoButton.TooltipText = "Инфо-режим: слои трафика, пешеходов и зон (F1)";
        _infoButton.Toggled += on =>
        {
            if (on != _router.InfoActive)
            {
                _router.SetInfo(on);
            }
        };
        row.AddChild(_infoButton);
        _undoButton = MakeButton("↶", toggle: false, disabled: true, 40f);
        _undoButton.TooltipText = "Отменить (Ctrl+Z): пока нечего";
        _undoButton.Pressed += () => UndoRequested?.Invoke();
        row.AddChild(_undoButton);
        _redoButton = MakeButton("↷", toggle: false, disabled: true, 40f);
        _redoButton.TooltipText = "Повторить (Ctrl+Y): пока нечего";
        _redoButton.Pressed += () => RedoRequested?.Invoke();
        row.AddChild(_redoButton);
    }

    private void BuildLegend()
    {
        _legend = new PanelContainer { Name = "Legend", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _rightDock.AddChild(_legend);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _legend.AddChild(box);
        _legendTitle = new Label { Text = "Инфо-слой", ThemeTypeVariation = UiTheme.LabelHeader, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_legendTitle);

        var layers = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(layers);
        var group = new ButtonGroup();
        string[] names = { "1 Трафик", "2 Пешеходы", "3 Зоны" };
        for (int i = 0; i < 3; i++)
        {
            int layer = i + 1;
            Button b = MakeButton(names[i], toggle: true, disabled: false);
            b.ButtonGroup = group;
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.Pressed += () => _router.SetInfoLayer(layer);
            layers.AddChild(b);
            _layerButtons[i] = b;
        }

        Legend = new UiLegend(box);
    }

    private void BuildInspector()
    {
        _inspector = new UiInspector(_rightDock);
        _inspector.CloseRequested += _router.CloseInspector;
    }

    /// <summary>Макет инспектора без выбранного объекта (<c>--ui-state inspect</c>): строки по ui-spec.md §3.3.</summary>
    private void FillDemoInspector()
    {
        _inspector.SetTitle("Инспектор (макет)");
        _inspector.SetRow(0, "Скорость", "—");
        _inspector.SetRow(1, "Цель", "—");
        _inspector.SetRow(2, "В пути", "—");
        _inspector.SetRow(3, "Состояние", "—");
        _inspector.SetRowCount(4);
        _inspector.SetArmRows(0);
        _inspector.SetNote("Щёлкните по машине, пешеходу, узлу или дороге");
        _inspector.SetAction(0, "Маршрут", toggle: true, pressed: false, enabled: false, "Показать маршрут");
        _inspector.SetAction(1, "Следовать", toggle: true, pressed: false, enabled: false, "Камера следует за объектом");
        _inspector.SetActionCount(2);
    }

    private void BuildModal()
    {
        _modal = new Control { Name = "Modal", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _modal.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_modal);
        _menus = new UiMenus(_root, _modal);
        _menus.ResumeRequested += _router.CloseMenu;
        _menus.SettingsChanged += ApplySettings;
    }

    /// <summary>
    /// Главное меню вместо игры (SimHost при старте без сценария): панели игры скрыты, Esc ничего не закрывает. Выход - только перезагрузкой сцены.
    /// Для скриншотов страница списка открывается аргументом <c>--ui-menu load</c>.
    /// </summary>
    public void EnterMainMenu()
    {
        _mainMode = true;
        _menus.ShowMain();
        if (_startMenu == "load")
        {
            _menus.ShowLoad();
        }
        else if (_startMenu == "settings")
        {
            _menus.ShowSettings();
        }

        Refresh();
    }

    // ---- видимость по состоянию ----

    private void Refresh()
    {
        IUiTool? tool = _router.Tool;
        _toolOptions.Visible = tool != null;
        Control? strip = null;
        if (tool != null)
        {
            _toolOptionsLabel.Text = "Инструмент «" + tool.DisplayName + "»";
            _toolStrips.TryGetValue(tool, out strip);
        }

        foreach (Control s in _toolStrips.Values)
        {
            s.Visible = ReferenceEquals(s, strip);
        }

        _toolOptionsLabel.Visible = strip == null;

        foreach ((string id, Button b) in _toolButtons)
        {
            b.SetPressedNoSignal(tool != null && tool.Id == id);
        }

        _infoButton.SetPressedNoSignal(_router.InfoActive);
        _legend.Visible = _router.InfoActive;
        for (int i = 0; i < _layerButtons.Length; i++)
        {
            _layerButtons[i].SetPressedNoSignal(_router.InfoLayer == i + 1);
        }

        if (_router.InfoLayer != _legendLayer)
        {
            _legendLayer = _router.InfoLayer;
            Legend.SetLayer(_legendLayer);
        }

        Legend.SyncPalette();
        _legendTitle.Text = "Инфо-слой: " + (_router.InfoLayer switch
        {
            2 => "Пешеходы",
            3 => "Зоны",
            _ => "Трафик",
        });

        // содержимое инспектора по объекту наполняет SimHost (UI3); здесь - видимость и макет
        _inspector.Visible = _router.Inspected != InspectKind.None;
        if (_router.Inspected == InspectKind.Demo)
        {
            FillDemoInspector();
        }

        bool menu = _router.MenuOpen && !_mainMode;
        if (menu && !_menuShown)
        {
            _menus.ShowPause();
        }

        _menuShown = menu;
        _modal.Visible = menu || _mainMode;

        // главное меню: игровых панелей нет (мира тоже)
        bool hud = !_mainMode;
        _topBar.Visible = hud;
        _toolbar.Visible = hud;
        _rightDock.Visible = hud;
        _toasts.Visible = hud;
        _toolOptions.Visible = hud && tool != null;
    }
}

internal static class UiControlExtensions
{
    public static T WithFullRect<T>(this T c)
        where T : Control
    {
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return c;
    }
}
