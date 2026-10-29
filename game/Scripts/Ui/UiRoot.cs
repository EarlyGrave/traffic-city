using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Корень интерфейса (ui-spec.md §12): <see cref="CanvasLayer"/> «Ui» (слой 10) с полноэкранным <see cref="Control"/>, одной темой
/// (<see cref="UiTheme"/>), масштабом (<see cref="UiScale"/>) и роутером состояния (<see cref="UiRouter"/>).
/// Экраны (полосы, док, меню) создаются по <see cref="UiScreenRegistry"/>; корень раздаёт им ввод, Esc (по <see cref="UiScreen.EscPriority"/>),
/// обновления и смену состояния, а наружу (SimHost) отдаёт узкий фасад. Новый экран сюда не дописывается.
/// Аргументы запуска (после <c>--</c>): <c>--ui-state overview|build|info|inspect</c>, <c>--ui-scale 75..150</c> (пользовательский множитель, %),
/// <c>--ui-cb 0..2</c> (палитра для дальтоников на запуск), <c>--no-ui</c> (скрыть слой: замер вклада UI), <c>--ui-shot</c> (<see cref="UiShot"/>);
/// аргументы экранов - у самих экранов.
/// </summary>
public sealed partial class UiRoot : CanvasLayer, IUiContext
{
    private readonly UiRouter _router = new();
    private readonly UiScale _scale = new();
    private readonly List<UiScreen> _screens = new();
    private readonly List<UiScreen> _byPriority = new();
    private Control _root = null!;
    private VBoxContainer? _rightDock;
    private VBoxContainer? _toasts;
    private SimHost? _host;
    private Window? _window;
    private UiShot? _shot;
    private bool _mainMode;

    /// <summary>Состояние и роутер событий.</summary>
    public UiRouter Router => _router;

    /// <summary>Режим, заданный аргументом <c>--ui-state</c> (Overview, если не задан).</summary>
    public UiMode StartMode { get; private set; } = UiMode.Overview;

    /// <summary>Уведомления: тосты, список «События», источник событий.</summary>
    public UiNotifyView Notify => Screen<NotifyScreen>().View;

    /// <summary>Содержимое легенды инфо-слоя (UI7): SimHost подаёт данные ~2 Гц.</summary>
    public UiLegend Legend => Screen<LegendScreen>().View;

    /// <summary>Инспектор в правой док-панели (UI3): наполняет SimHost по выбору в мире.</summary>
    public UiInspector Inspector => Screen<InspectorScreen>().View;

    /// <summary>Главное меню, меню игры (Esc) и список сейвов: события и данные подключает SimHost.</summary>
    public UiMenus Menus => Screen<MenuScreen>().View;

    /// <summary>Подтверждение опасного действия (P13): снос с потерей участков и т.п.</summary>
    public UiConfirm Confirm => Screen<ConfirmScreen>().View;

    /// <summary>Слой для меток у курсора (полноэкранный, события мыши пропускает): метка инструмента, подсказки.</summary>
    public Control CursorLayer => Screen<CursorLayerScreen>().Layer;

    /// <summary>Тулбар: кнопки ↶ ↷ (события и состояние).</summary>
    public ToolbarScreen Toolbar => Screen<ToolbarScreen>();

    /// <summary>Показано главное меню (игры нет).</summary>
    public bool InMainMenu => _mainMode;

    bool IUiContext.MainMenu => _mainMode;

    /// <summary>Открыто что-то, что забирает ввод у мира: меню игры или подтверждение.</summary>
    public bool WorldInputBlocked => _router.MenuOpen || Confirm.Visible;

    /// <summary>Создаёт настоящий инструмент по id кнопки или клавиши (<c>roads</c>, <c>bulldoze</c>); null - остаётся заглушка. Задаёт SimHost.</summary>
    public Func<string, IUiTool?>? ToolFactory { get; set; }

    /// <summary>Хост симуляции - сосед по сцене (null, пока его нет).</summary>
    public SimHost? Host => _host ??= GetNodeOrNull<SimHost>("../SimHost");

    /// <summary>Аргументы запуска после <c>--</c>.</summary>
    public string[] Args { get; private set; } = Array.Empty<string>();

    /// <summary>Строка опций настоящего инструмента: показывается вместо подписи-заглушки, пока инструмент активен.</summary>
    public void RegisterToolOptions(IUiTool tool, Control strip) => Screen<ToolOptionsScreen>().Register(tool, strip);

    /// <summary>Зарегистрированный экран типа <typeparamref name="T"/>.</summary>
    public T Screen<T>()
        where T : UiScreen
    {
        foreach (UiScreen s in _screens)
        {
            if (s is T t)
            {
                return t;
            }
        }

        throw new InvalidOperationException("UI: экран не зарегистрирован: " + typeof(T).Name);
    }

    public Control Slot(UiDock dock) => dock switch
    {
        // места создаются по первому запросу: порядок детей корня (отрисовка) = порядок реестра
        UiDock.Right => _rightDock ??= Column("RightDock", 0f, UiMetrics.TopBarHeight + UiMetrics.Margin),
        UiDock.Toasts => _toasts ??= Column("Toasts", 1f, -(UiMetrics.ToolbarHeight + UiMetrics.OptionsHeight + 320f)),
        _ => _root,
    };

    public void MarkHandled() => GetViewport().SetInputAsHandled();

    public override void _EnterTree()
    {
        Layer = 10;
        Name = "Ui";
        UiActions.Register();
        UiSettings saved = UiSettings.Current;
        UiPalette.SetColorblind(saved.Colorblind);
        HeatPalette.SetMode(saved.Colorblind);
        _scale.SetUserPercentNoApply(saved.ScalePercent);
        Args = OS.GetCmdlineUserArgs();
        ParseArgs(Args);
        _shot = UiShot.FromArgs(this, Args);
        Build();
        _window = GetWindow();
        _scale.Apply(_window);
        _window.SizeChanged += OnWindowSizeChanged;
        _router.StateChanged += Refresh;
    }

    public override void _Ready()
    {
        foreach (UiScreen s in _screens)
        {
            s.Ready();
        }

        Refresh();
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
        foreach (UiScreen s in _screens)
        {
            s.Process(delta);
        }

        _shot?.Tick();
    }

    /// <summary>
    /// Esc - по убыванию <see cref="UiScreen.EscPriority"/> (подтверждение -> страница меню -> «Город» -> список уведомлений), немодальные - только без меню игры;
    /// никто не закрылся - роутер («протяжка -> инструмент -> инспектор -> инфо -> меню»). Остальной ввод - экранам в том же порядке, до первого «моё».
    /// </summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed(UiActions.Cancel))
        {
            bool used = false;
            foreach (UiScreen s in _byPriority)
            {
                if (s.EscPriority >= 0 && (s.Modal || !_router.MenuOpen) && s.HandleEsc())
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                _router.Cancel();
            }

            MarkHandled();
            return;
        }

        foreach (UiScreen s in _byPriority)
        {
            if (s.HandleInput(@event))
            {
                return;
            }
        }
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
        Host?.SetRenderPreset(s.RenderPreset);
        Refresh();
        Menus.Rebuild();
    }

    /// <summary>
    /// Главное меню вместо игры (SimHost при старте без сценария): панели игры скрыты. Выход - только перезагрузкой сцены.
    /// Для скриншотов страница списка открывается аргументом <c>--ui-menu load</c>.
    /// </summary>
    public void EnterMainMenu()
    {
        _mainMode = true;
        Screen<MenuScreen>().ShowMain();
        Refresh();
    }

    private void OnWindowSizeChanged() => _scale.Apply(_window!);

    private void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            bool next = i + 1 < args.Length;
            if (args[i] == "--ui-state" && next)
            {
                StartMode = args[++i] switch { "build" => UiMode.Build, "info" => UiMode.Info, "inspect" => UiMode.Inspect, _ => UiMode.Overview };
            }
            else if (args[i] == "--ui-scale" && next && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
            {
                _scale.SetUserPercentNoApply(pct);
            }
            else if (args[i] == "--ui-cb" && next && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cb) && cb is >= 0 and <= 2)
            {
                // палитра для дальтоников только на этот запуск (скриншоты), в settings.cfg не пишется
                UiPalette.SetColorblind((ColorblindMode)cb);
                HeatPalette.SetMode((ColorblindMode)cb);
            }
            else if (args[i] == "--no-ui")
            {
                Visible = false;
            }
        }
    }

    private void Build()
    {
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Build() };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        foreach (Func<UiScreen> make in UiScreenRegistry.All)
        {
            UiScreen s = make();
            _screens.Add(s);
            s.Attach(this);
        }

        _byPriority.AddRange(_screens);
        _byPriority.Sort((a, b) => b.EscPriority != a.EscPriority ? b.EscPriority.CompareTo(a.EscPriority) : _screens.IndexOf(a).CompareTo(_screens.IndexOf(b)));
    }

    /// <summary>Колонка у правого края шириной с док до строки опций (правая док-панель, тосты); <paramref name="anchorTop"/> 0 - от верха, 1 - от низа.</summary>
    private VBoxContainer Column(string name, float anchorTop, float top)
    {
        var c = new VBoxContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        c.AnchorLeft = 1f;
        c.AnchorRight = 1f;
        c.AnchorTop = anchorTop;
        c.AnchorBottom = 1f;
        c.OffsetLeft = -(UiMetrics.DockWidth + UiMetrics.Margin);
        c.OffsetRight = -UiMetrics.Margin;
        c.OffsetTop = top;
        c.OffsetBottom = -(UiMetrics.ToolbarHeight + UiMetrics.OptionsHeight + UiMetrics.Margin);
        c.GrowVertical = anchorTop > 0f ? Control.GrowDirection.Begin : Control.GrowDirection.End;
        c.AddThemeConstantOverride("separation", (int)UiMetrics.Margin); // тосты задают свой (UiNotifyView)
        _root.AddChild(c);
        return c;
    }

    private void Refresh()
    {
        foreach (UiScreen s in _screens)
        {
            s.Refresh();
        }

        // главное меню: игровых панелей нет (мира тоже)
        Slot(UiDock.Right).Visible = !_mainMode;
        Slot(UiDock.Toasts).Visible = !_mainMode;
    }
}
