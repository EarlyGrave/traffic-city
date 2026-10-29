using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>
/// Нижний тулбар 56 px (UI2): категории инструментов с клавишами и замками по вехам (progression.md §4.2), «Инфо», кнопки экранов, ↶ ↷.
/// Кнопка или клавиша включает инструмент, повторное нажатие выключает.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class ToolbarScreen : UiScreen
{
    /// <summary>Инструменты: id, подпись, клавиша (подсказка), действие клавиши (null - клавишу ведёт хост), признаки-замки (открыт любой из двух).</summary>
    private static readonly (string Id, string Title, string Key, StringName? Action, Feature? A, Feature? B)[] _tools =
    {
        ("roads", "Дороги", "R", UiActions.ToolRoads, null, null),
        ("zones", "Зоны", "Z", UiActions.ToolZones, Feature.Zoning, Feature.Zoning),
        ("transit", "Транспорт", "T", UiActions.ToolTransit, Feature.BusLines, Feature.BusLanes),
        ("nodes", "Узлы", "J", null, Feature.SignalsFixed, Feature.TurnRules),
        ("bulldoze", "Снос", "X", UiActions.ToolBulldoze, null, null),
    };

    private readonly Dictionary<string, Button> _buttons = new();
    private PanelContainer _bar = null!;
    private HBoxContainer _row = null!;
    private Button _infoButton = null!;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private int _screenButtons;
    private bool _capsKnown;
    private int _lockVersion = -1;

    /// <summary>Кнопка ↶ (Ctrl+Z обрабатывает хост мира).</summary>
    public event Action? UndoRequested;

    /// <summary>Кнопка ↷ (Ctrl+Y обрабатывает хост мира).</summary>
    public event Action? RedoRequested;

    public override UiDock Dock => UiDock.Root;

    public override bool IsOpen => _bar.Visible;

    protected override void Build(Control parent)
    {
        _bar = Bar(parent, "Toolbar", UiTheme.BarBottom, top: false, UiMetrics.ToolbarHeight, 0f);
        _row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 8);
        _bar.AddChild(_row);
        foreach ((string id, string title, string key, _, _, _) in _tools)
        {
            Button b = MakeButton(title, toggle: true, disabled: false, 96f);
            b.TooltipText = title + " (" + key + ")";
            b.Toggled += on => Ui.Router.SetTool(on ? MakeTool(id) : null);
            _row.AddChild(b);
            _buttons[id] = b;
        }

        _row.AddChild(new VSeparator());
        _infoButton = MakeButton("Инфо  F1", toggle: true, disabled: false, 96f);
        _infoButton.TooltipText = "Инфо-режим: слои трафика, пешеходов и зон (F1)";
        _infoButton.Toggled += on =>
        {
            if (on != Ui.Router.InfoActive)
            {
                Ui.Router.SetInfo(on);
            }
        };
        _row.AddChild(_infoButton);
        _undoButton = MakeButton("↶", toggle: false, disabled: true, 40f);
        _undoButton.TooltipText = "Отменить (Ctrl+Z): пока нечего";
        _undoButton.Pressed += () => UndoRequested?.Invoke();
        _row.AddChild(_undoButton);
        _redoButton = MakeButton("↷", toggle: false, disabled: true, 40f);
        _redoButton.TooltipText = "Повторить (Ctrl+Y): пока нечего";
        _redoButton.Pressed += () => RedoRequested?.Invoke();
        _row.AddChild(_redoButton);
    }

    /// <summary>Кнопка экрана в тулбаре: встаёт после «Инфо» (порядок вставки = порядок реестра).</summary>
    public void AddScreenButton(Button b)
    {
        _row.AddChild(b);
        _row.MoveChild(b, _infoButton.GetIndex() + 1 + _screenButtons++);
    }

    /// <summary>Состояние кнопок ↶ ↷ и подсказки с глубиной стека.</summary>
    public void SetUndoRedo(int undoDepth, int redoDepth, string undoWhat, string redoWhat)
    {
        _undoButton.Disabled = undoDepth <= 0;
        _redoButton.Disabled = redoDepth <= 0;
        _undoButton.TooltipText = undoDepth > 0 ? "Отменить: " + undoWhat + " (Ctrl+Z), действий в стеке: " + undoDepth.ToString(CultureInfo.InvariantCulture) : "Отменить (Ctrl+Z): пока нечего";
        _redoButton.TooltipText = redoDepth > 0 ? "Повторить: " + redoWhat + " (Ctrl+Y), действий в стеке: " + redoDepth.ToString(CultureInfo.InvariantCulture) : "Повторить (Ctrl+Y): пока нечего";
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
        foreach ((string id, _, _, StringName? action, _, _) in _tools)
        {
            if (action != null && UiActions.Pressed(e, action) && (id != "zones" || (Ui.Host?.HasCity ?? false)))
            {
                ToggleTool(id);
                return true;
            }
        }

        return false;
    }

    public override void Refresh()
    {
        IUiTool? tool = Ui.Router.Tool;
        foreach ((string id, Button b) in _buttons)
        {
            b.SetPressedNoSignal(tool != null && tool.Id == id);
        }

        _infoButton.SetPressedNoSignal(Ui.Router.InfoActive);
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
                _buttons["zones"].TooltipText = "Зоны (Z): нужно: сценарий с жителями (residents)";
            }
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

        foreach ((string tid, string title, _, _, _, _) in _tools)
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
        foreach ((string id, string name, string key, _, Feature? fa, Feature? fb) in _tools)
        {
            if (fa is not { } a || fb is not { } b)
            {
                continue;
            }

            Button btn = _buttons[id];
            bool open = d.FeatureOpen(a) || d.FeatureOpen(b);
            string title = name + " (" + key + ")";
            if (open == !btn.Disabled)
            {
                continue;
            }

            btn.Disabled = !open;
            if (open)
            {
                btn.TooltipText = title;
                continue;
            }

            int m = d.MilestoneFor(a);
            if (m < 0 && b != a)
            {
                m = d.MilestoneFor(b);
            }

            btn.TooltipText = m >= 0
                ? title + ": ещё не открыто — откроется на вехе " + (m + 1).ToString(CultureInfo.InvariantCulture) + " (" + UiCityPanel.FeatureName(a) + "). Вехи — панель «Город» (C)"
                : title + ": ещё не открыто в этом сценарии";
            if (Ui.Router.Tool?.Id == id)
            {
                Ui.Router.SetTool(null);
            }
        }
    }
}
