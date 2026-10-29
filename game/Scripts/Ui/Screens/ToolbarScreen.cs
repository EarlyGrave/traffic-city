using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Sim.Core.Api;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Нижний тулбар на <see cref="UiToolbar"/> (UI-кит, 12 пиктограмм): инструменты с клавишами и замками по вехам (progression.md §4.2),
/// слои («Инфо»), панель города, ↶ ↷, пауза, скорость, настройки. Кнопка или клавиша включает инструмент, повторное нажатие выключает.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class ToolbarScreen : UiScreen
{
    /// <summary>Высота зоны тулбара: кнопка 52 px + поля панели + твёрдая тень.</summary>
    public const float Height = UiMetrics.ToolbarHeight;

    private const int IdxInfo = 5;
    private const int IdxCity = 6;
    private const int IdxUndo = 7;
    private const int IdxRedo = 8;
    private const int IdxPause = 9;
    private const int IdxFast = 10;
    private const int IdxSettings = 11;

    /// <summary>Инструменты по порядку пиктограмм кита: id, подпись, клавиша (подсказка), действие клавиши (null - клавишу ведёт хост), признаки-замки (открыт любой из двух).</summary>
    private static readonly (string Id, string Glyph, string Title, string Key, StringName? Action, Feature? A, Feature? B)[] _tools =
    {
        ("roads", "road", "Дороги", "R", UiActions.ToolRoads, null, null),
        ("nodes", "intersection", "Узлы", "J", null, Feature.SignalsFixed, Feature.TurnRules),
        ("zones", "zones", "Зоны", "Z", UiActions.ToolZones, Feature.Zoning, Feature.Zoning),
        ("transit", "bus", "Транспорт", "T", UiActions.ToolTransit, Feature.BusLines, Feature.BusLanes),
        ("bulldoze", "bulldoze", "Снос", "X", UiActions.ToolBulldoze, null, null),
    };

    private readonly Dictionary<string, UiButton> _buttons = new();
    private Control _bar = null!;
    private UiToolbar _toolbar = null!;
    private HBoxContainer _row = null!;
    private UiButton _infoButton = null!;
    private UiButton _cityButton = null!;
    private UiButton _undoButton = null!;
    private UiButton _redoButton = null!;
    private UiButton _pauseButton = null!;
    private bool _capsKnown;
    private int _lockVersion = -1;
    private bool _shownPaused;
    private float _shownScale = -1f;

    /// <summary>Кнопка ↶ (Ctrl+Z обрабатывает хост мира).</summary>
    public event Action? UndoRequested;

    /// <summary>Кнопка ↷ (Ctrl+Y обрабатывает хост мира).</summary>
    public event Action? RedoRequested;

    public override UiDock Dock => UiDock.Root;

    public override bool IsOpen => _bar.Visible;

    protected override void Build(Control parent)
    {
        // полоса-прозрачка на всю ширину (мышь пропускает), панель кита по центру: мир по бокам кликабелен
        _bar = new CenterContainer { Name = "Toolbar", MouseFilter = Control.MouseFilterEnum.Ignore };
        var c = (Control)_bar;
        c.AnchorLeft = 0f;
        c.AnchorRight = 1f;
        c.AnchorTop = 1f;
        c.AnchorBottom = 1f;
        c.OffsetTop = -Height;
        c.OffsetBottom = 0f;
        parent.AddChild(c);

        _toolbar = new UiToolbar();
        foreach ((_, string glyph, string title, string key, _, _, _) in _tools)
        {
            _toolbar.Add(glyph, title, key);
        }

        _toolbar.Separator();
        _toolbar.Add("layers", "Инфо-режим: слои трафика, пешеходов и зон", "F1");
        _toolbar.Add("stats", "Показатели города и вехи: нужно: сценарий с жителями (residents)", "C");
        _toolbar.Separator();
        _toolbar.Add("undo", "Отменить: пока нечего", "Ctrl+Z");
        _toolbar.Add("redo", "Повторить: пока нечего", "Ctrl+Y");
        _toolbar.Separator();
        _toolbar.Add("pause", "Пауза", "Space");
        _toolbar.Add("fast", "Быстрее: ×1 → ×2 → ×4", "+");
        _toolbar.Add("settings", "Настройки", "F10");
        _row = _toolbar[0].GetParent<HBoxContainer>();
        _bar.AddChild(_toolbar);
        _toolbar[0].ButtonGroup!.AllowUnpress = true; // повторное нажатие выключает инструмент
        _toolbar[0].SetPressedNoSignal(false);        // UiToolbar выбирает первую кнопку сам

        for (int i = 0; i < _tools.Length; i++)
        {
            string id = _tools[i].Id;
            UiButton b = _toolbar[i];
            b.FocusMode = Control.FocusModeEnum.None; // иначе Space/Enter нажимали бы кнопку и «съедали» клавиши времени
            b.Toggled += on => Ui.Router.SetTool(on ? MakeTool(id) : null);
            _buttons[id] = b;
        }

        // остальные кнопки - не из группы инструментов
        for (int i = IdxInfo; i <= IdxSettings; i++)
        {
            UiButton b = _toolbar[i];
            b.ButtonGroup = null;
            b.FocusMode = Control.FocusModeEnum.None;
            b.SetPressedNoSignal(false);
        }

        _infoButton = _toolbar[IdxInfo];
        _infoButton.Toggled += on =>
        {
            if (on != Ui.Router.InfoActive)
            {
                Ui.Router.SetInfo(on);
            }
        };
        _cityButton = _toolbar[IdxCity];
        _cityButton.Disabled = true;
        _cityButton.Toggled += on => Ui.Screen<CityScreen>().SetOpen(on);
        _undoButton = _toolbar[IdxUndo];
        _undoButton.ToggleMode = false;
        _undoButton.Disabled = true;
        _undoButton.Pressed += () => UndoRequested?.Invoke();
        _redoButton = _toolbar[IdxRedo];
        _redoButton.ToggleMode = false;
        _redoButton.Disabled = true;
        _redoButton.Pressed += () => RedoRequested?.Invoke();
        _pauseButton = _toolbar[IdxPause];
        _pauseButton.Toggled += on => Ui.Host?.SetPaused(on);
        UiButton fast = _toolbar[IdxFast];
        fast.ToggleMode = false;
        fast.Pressed += () =>
        {
            float s = Ui.Host?.TimeScale ?? 1f;
            Ui.Host?.SetSpeed(s < 2f ? 2f : s < 4f ? 4f : 1f);
        };
        UiButton settings = _toolbar[IdxSettings];
        settings.ToggleMode = false;
        settings.Pressed += () => Ui.Screen<MenuScreen>().ToggleSettings();
    }

    /// <summary>Кнопка экрана (прежний API): панель «Город» теперь открывается пиктограммой «stats», поэтому её собственная кнопка спрятана, но остаётся в дереве.</summary>
    public void AddScreenButton(Button b)
    {
        b.Visible = false;
        _row.AddChild(b);
    }

    /// <summary>Состояние кнопок ↶ ↷ и подсказки с глубиной стека.</summary>
    public void SetUndoRedo(int undoDepth, int redoDepth, string undoWhat, string redoWhat)
    {
        _undoButton.Disabled = undoDepth <= 0;
        _redoButton.Disabled = redoDepth <= 0;
        _undoButton.WithTooltip(undoDepth > 0 ? "Отменить: " + undoWhat + ", действий в стеке: " + undoDepth.ToString(CultureInfo.InvariantCulture) : "Отменить: пока нечего", "Ctrl+Z");
        _redoButton.WithTooltip(redoDepth > 0 ? "Повторить: " + redoWhat + ", действий в стеке: " + redoDepth.ToString(CultureInfo.InvariantCulture) : "Повторить: пока нечего", "Ctrl+Y");
    }

    public override void SetOpen(bool open) => _bar.Visible = open;

    public override void Ready()
    {
        if (ArgValue("--ui-state") == "build")
        {
            Ui.Router.SetTool(MakeTool("roads"));
        }
    }

    public override bool HandleInput(InputEvent e)
    {
        foreach ((string id, _, _, _, StringName? action, _, _) in _tools)
        {
            if (action == null || !UiActions.Pressed(e, action))
            {
                continue;
            }

            // клавиша подчиняется тому же замку, что и серая кнопка (веха не открыта / нет жителей): молча игнорируем
            if (_buttons[id].Disabled || (id == "zones" && !(Ui.Host?.HasCity ?? false)))
            {
                return false;
            }

            ToggleTool(id);
            return true;
        }

        return false;
    }

    public override void Refresh()
    {
        IUiTool? tool = Ui.Router.Tool;
        foreach ((string id, UiButton b) in _buttons)
        {
            b.SetPressedNoSignal(tool != null && tool.Id == id);
        }

        _infoButton.SetPressedNoSignal(Ui.Router.InfoActive);
        _cityButton.SetPressedNoSignal(Ui.Screen<CityScreen>().IsOpen);
        SetOpen(!Ui.MainMenu);
    }

    public override void Process(double delta)
    {
        if (Ui.Host is not { IsReady: true } h)
        {
            return;
        }

        if (!_capsKnown)
        {
            // категории, которым нужен режим жителей, серые с причиной в тултипе, пока сценарий без жителей (ui-spec.md §4, P2)
            _capsKnown = true;
            if (!h.HasCity)
            {
                _buttons["zones"].Disabled = true;
                _buttons["zones"].WithTooltip("Зоны: нужно: сценарий с жителями (residents)", "Z");
            }
        }

        bool canCity = Ui.Screen<CityScreen>().CanOpen;
        if (_cityButton.Disabled == canCity)
        {
            _cityButton.Disabled = !canCity;
            _cityButton.WithTooltip(canCity ? "Показатели города и вехи" : "Показатели города и вехи: нужно: сценарий с жителями (residents)", "C");
        }

        _cityButton.SetPressedNoSignal(Ui.Screen<CityScreen>().IsOpen);
        if (h.TimePaused != _shownPaused || h.TimeScale != _shownScale)
        {
            _shownPaused = h.TimePaused;
            _shownScale = h.TimeScale;
            _pauseButton.SetPressedNoSignal(h.TimePaused);
        }

        if (h.HasCity && h.CityPanel is { HasProgression: true } d && d.Version != _lockVersion && d.Version > 0)
        {
            _lockVersion = d.Version;
            ApplyLocks(d);
        }
    }

    private IUiTool MakeTool(string id)
    {
        IUiTool? real = Ui.ToolFactory?.Invoke(id);
        if (real != null)
        {
            return real;
        }

        foreach ((string tid, _, string title, _, _, _, _) in _tools)
        {
            if (tid == id)
            {
                return new UiPlaceholderTool(id, title);
            }
        }

        return new UiPlaceholderTool(id, id);
    }

    private void ToggleTool(string id) => Ui.Router.SetTool(Ui.Router.Tool?.Id == id ? null : MakeTool(id));

    /// <summary>Кнопки категорий с закрытыми признаками - серые, в подсказке - веха, которая откроет (принцип 2 ui-spec.md: замок виден до клика).</summary>
    private void ApplyLocks(CityPanelData d)
    {
        foreach ((string id, _, string name, string key, _, Feature? fa, Feature? fb) in _tools)
        {
            if (fa is not { } a || fb is not { } b)
            {
                continue;
            }

            UiButton btn = _buttons[id];
            bool open = d.FeatureOpen(a) || d.FeatureOpen(b);
            string title = name;
            if (open == !btn.Disabled)
            {
                continue;
            }

            btn.Disabled = !open;
            if (open)
            {
                btn.WithTooltip(title, key);
                continue;
            }

            int m = d.MilestoneFor(a);
            if (m < 0 && b != a)
            {
                m = d.MilestoneFor(b);
            }

            btn.WithTooltip(
                m >= 0
                    ? title + ": ещё не открыто — откроется на вехе " + (m + 1).ToString(CultureInfo.InvariantCulture) + " (" + UiCityPanel.FeatureName(a) + "). Вехи — панель «Город»"
                    : title + ": ещё не открыто в этом сценарии",
                key);
            if (Ui.Router.Tool?.Id == id)
            {
                Ui.Router.SetTool(null);
            }
        }
    }
}
