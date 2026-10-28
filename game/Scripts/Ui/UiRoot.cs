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
    private PanelContainer _inspector = null!;
    private VBoxContainer _toasts = null!;
    private Control _modal = null!;
    private Label _toolOptionsLabel = null!;
    private Label _legendTitle = null!;
    private Label _inspectorTitle = null!;
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

    /// <summary>Тосты уведомлений справа снизу над тулбаром (UI6).</summary>
    public VBoxContainer Toasts => _toasts;

    /// <summary>Модальный слой (меню, бюджет, настройки): полноэкранный, блокирует клики в мир, пока виден.</summary>
    public Control Modal => _modal;

    /// <summary>Главное меню, меню игры (Esc) и список сейвов: события и данные подключает SimHost.</summary>
    public UiMenus Menus => _menus;

    /// <summary>Показано главное меню (игры нет).</summary>
    public bool InMainMenu => _mainMode;

    public override void _EnterTree()
    {
        Layer = 10;
        Name = "Ui";
        UiActions.Register();
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
                _router.SetTool(new UiPlaceholderTool("roads", "Дороги"));
                break;
            case UiMode.Inspect:
                _router.SetInspector(InspectKind.Demo);
                break;
            default:
                break; // info: состояние мира выставляет SimHost (Snap) и зеркалит в роутер
        }

        if (_startMenu.Length > 0 && !_mainMode)
        {
            _router.OpenMenu(); // скриншот меню игры: страница строится в Refresh
            if (_startMenu == "load")
            {
                _menus.ShowLoad();
            }
        }

        Refresh();
        LogScale();
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
            if (!_menus.HandleCancel())
            {
                _router.Cancel();
            }

            GetViewport().SetInputAsHandled();
        }
        else if (_mainMode || _router.MenuOpen)
        {
            // под меню (главным или Esc) горячие клавиши времени и инфо-режима молчат
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

        _toasts = new VBoxContainer { Name = "Toasts", MouseFilter = Control.MouseFilterEnum.Ignore };
        _toasts.AnchorLeft = 1f;
        _toasts.AnchorRight = 1f;
        _toasts.AnchorTop = 1f;
        _toasts.AnchorBottom = 1f;
        _toasts.OffsetLeft = -(UiMetrics.DockWidth + UiMetrics.Margin);
        _toasts.OffsetRight = -UiMetrics.Margin;
        _toasts.OffsetTop = -(UiMetrics.ToolbarHeight + UiMetrics.OptionsHeight + 160f);
        _toasts.OffsetBottom = -(UiMetrics.ToolbarHeight + UiMetrics.OptionsHeight + UiMetrics.Margin);
        _toasts.GrowVertical = Control.GrowDirection.Begin;
        _root.AddChild(_toasts);

        BuildModal();
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
        foreach ((string id, string title, string key) in new[] { ("roads", "Дороги", "R"), ("zones", "Зоны", "Z"), ("transit", "Транспорт", "T"), ("nodes", "Узлы", "J"), ("bulldoze", "Снос", "X") })
        {
            Button b = MakeButton(title, toggle: true, disabled: false, 96f);
            b.TooltipText = title + " (" + key + ")";
            b.Toggled += on => _router.SetTool(on ? new UiPlaceholderTool(id, title) : null);
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
        Button undo = MakeButton("↶", toggle: false, disabled: true, 40f);
        undo.TooltipText = "Отменить (Ctrl+Z): появится вместе с инструментами";
        row.AddChild(undo);
        Button redo = MakeButton("↷", toggle: false, disabled: true, 40f);
        redo.TooltipText = "Повторить (Ctrl+Y): появится вместе с инструментами";
        row.AddChild(redo);
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

        box.AddChild(Secondary("Шкала и числа слоя (UI7)", Control.SizeFlags.ExpandFill, HorizontalAlignment.Left));
    }

    private void BuildInspector()
    {
        _inspector = new PanelContainer { Name = "Inspector", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _rightDock.AddChild(_inspector);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _inspector.AddChild(box);

        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(head);
        _inspectorTitle = new Label { ThemeTypeVariation = UiTheme.LabelHeader, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddChild(_inspectorTitle);
        Button close = MakeButton("×", toggle: false, disabled: false, 36f);
        close.Pressed += _router.CloseInspector;
        head.AddChild(close);
        box.AddChild(new HSeparator());

        var grid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 16);
        box.AddChild(grid);
        foreach (string key in new[] { "Скорость", "Цель", "В пути", "Состояние" })
        {
            grid.AddChild(Secondary(key, Control.SizeFlags.Fill, HorizontalAlignment.Left));
            grid.AddChild(new Label { Text = "—", MouseFilter = Control.MouseFilterEnum.Ignore });
        }

        box.AddChild(new HSeparator());
        var actions = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(actions);
        actions.AddChild(MakeButton("Маршрут", toggle: false, disabled: true));
        actions.AddChild(MakeButton("Следовать", toggle: false, disabled: true));
    }

    private void BuildModal()
    {
        _modal = new Control { Name = "Modal", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _modal.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_modal);
        _menus = new UiMenus(_root, _modal);
        _menus.ResumeRequested += _router.CloseMenu;
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

        Refresh();
    }

    // ---- видимость по состоянию ----

    private void Refresh()
    {
        IUiTool? tool = _router.Tool;
        _toolOptions.Visible = tool != null;
        if (tool != null)
        {
            _toolOptionsLabel.Text = "Инструмент «" + tool.DisplayName + "»";
        }

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

        _legendTitle.Text = "Инфо-слой: " + (_router.InfoLayer switch
        {
            2 => "Пешеходы",
            3 => "Зоны",
            _ => "Трафик",
        });

        // пешеход и узел показывают прежние панели (NodePanel, текст пешехода) до UI3; здесь - ТС и макет
        bool showInspector = _router.Inspected is InspectKind.Vehicle or InspectKind.Demo;
        _inspector.Visible = showInspector;
        _inspectorTitle.Text = _router.Inspected == InspectKind.Demo ? "Инспектор (макет)" : "Транспорт";
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
