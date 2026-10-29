using System.Globalization;
using Godot;
using Sim.Core.Network;
using TrafficCity.Tools;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Строка опций инструмента «Узлы» (ui-spec §3.2, §5.4): режим (Узел / Рукава / Запреты / Полосы), режим управления узлом, пресеты светофора и цикл
/// ползунком (протяжка - один пункт undo). <see cref="Sync"/> зовётся по <see cref="NodesTool.OptionsChanged"/> и раз в кадр, но меняет узлы только при смене.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class NodesOptionsStrip
{
    private static readonly string[] _modeNames = { "Узел", "Рукава", "Запреты", "Полосы" };
    private static readonly string[] _controlNames = { "Без светофора", "Главная", "Светофор", "Адаптивный" };
    private static readonly string[] _presetNames = { "Адапт.", "Фикс.", "Мигает" };

    private readonly NodesTool _tool;
    private readonly HBoxContainer _row;
    private readonly Button[] _mode = new Button[4];
    private readonly Button[] _control = new Button[4];
    private readonly Button[] _preset = new Button[3];
    private readonly UiSlider _cycle;
    private readonly Control _controlBox;
    private readonly Control _signalBox;
    private readonly Label _hint;
    private string _hintShown = string.Empty;
    private bool _dragging;
    private bool _syncing;

    public NodesOptionsStrip(NodesTool tool)
    {
        _tool = tool;
        _row = new HBoxContainer { Name = "NodesOptions", MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 8);

        var modes = new ButtonGroup();
        for (int i = 0; i < _mode.Length; i++)
        {
            var m = (NodesMode)i;
            Button b = Btn(_modeNames[i], toggle: true);
            b.ButtonGroup = modes;
            b.Pressed += () => _tool.SetMode(m);
            _mode[i] = b;
            _row.AddChild(b);
        }

        _mode[0].TooltipText = "Режим управления узлом и план светофора";
        _mode[1].TooltipText = "Знаки на рукавах: клик по иконке - авто → главная → уступи → стоп";
        _mode[2].TooltipText = "Запреты манёвров: клик по стрелке над рукавом выключает или включает манёвр";
        _mode[3].TooltipText = "Флаги полос: клик - автобусная полоса, Shift+клик - запрет перестроения";
        _row.AddChild(new VSeparator());

        var ctl = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        ctl.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < _control.Length; i++)
        {
            var c = (NodeControlChoice)i;
            Button b = Btn(_controlNames[i], toggle: true);
            b.Pressed += () => _tool.ApplyControl(c);
            _control[i] = b;
            ctl.AddChild(b);
        }

        _control[0].TooltipText = "Нерегулируемый узел: правило правой руки";
        _control[1].TooltipText = "Главная и второстепенная дороги: второстепенные уступают";
        _control[2].TooltipText = "Светофор с фиксированным циклом";
        _control[3].TooltipText = "Светофор по спросу, со стрелкой";
        _controlBox = ctl;
        _row.AddChild(ctl);

        var sig = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        sig.AddThemeConstantOverride("separation", 4);
        sig.AddChild(new VSeparator());
        for (int i = 0; i < _preset.Length; i++)
        {
            var p = (SignalPresetChoice)i;
            Button b = Btn(_presetNames[i], toggle: true);
            b.Pressed += () => _tool.ApplyPreset(p);
            _preset[i] = b;
            sig.AddChild(b);
        }

        sig.AddChild(Secondary("Цикл"));
        _cycle = new UiSlider(string.Empty, NodesTool.MinCycleS, NodesTool.MaxCycleS, 2, NodesTool.DefaultCycleS, "{0:0} с")
        {
            TooltipText = "Длительность цикла фиксированного светофора",
        };
        _cycle.Slider.FocusMode = Control.FocusModeEnum.None;
        _cycle.Slider.CustomMinimumSize = new Vector2(140f, 24f);
        _cycle.Slider.DragStarted += () => _dragging = true;
        _cycle.Slider.DragEnded += _ =>
        {
            _dragging = false;
            _tool.EndCycleDrag();
        };
        _cycle.Slider.ValueChanged += v =>
        {
            if (!_syncing)
            {
                _tool.SetCycle((int)v, continuing: _dragging);
            }
        };
        sig.AddChild(_cycle);
        _signalBox = sig;
        _row.AddChild(sig);

        _hint = Secondary(string.Empty);
        _hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _hint.ClipText = true;
        _row.AddChild(_hint);
        Sync();
    }

    public Control Root => _row;

    /// <summary>Подтянуть кнопки из состояния инструмента без сигналов.</summary>
    public void Sync()
    {
        _syncing = true;
        for (int i = 0; i < _mode.Length; i++)
        {
            _mode[i].SetPressedNoSignal((int)_tool.Mode == i);
        }

        bool sel = _tool.HasSelection;
        NodeLayout l = _tool.Layout;
        bool signal = sel && l.Control == NodeControl.Signal;
        bool controlMode = _tool.Mode == NodesMode.Control;
        _controlBox.Visible = controlMode;
        _signalBox.Visible = controlMode && signal;
        int active = !sel ? -1 : l.Control switch
        {
            NodeControl.Signal => _tool.Preset == SignalPresetChoice.Adaptive ? 3 : 2,
            NodeControl.Yield or NodeControl.Stop => 1,
            _ => 0,
        };
        for (int i = 0; i < _control.Length; i++)
        {
            _control[i].Disabled = !sel;
            _control[i].SetPressedNoSignal(i == active);
        }

        for (int i = 0; i < _preset.Length; i++)
        {
            _preset[i].SetPressedNoSignal(signal && (int)_tool.Preset == i);
        }

        bool fixedPlan = _tool.Preset != SignalPresetChoice.Adaptive;
        _cycle.Disable(!fixedPlan);
        _cycle.Slider.FocusMode = Control.FocusModeEnum.None;
        if (!_dragging && (int)_cycle.Value != _tool.CycleS)
        {
            _cycle.Slider.Value = _tool.CycleS; // сигнал обновит и подпись кита; свои обработчики глушит _syncing
        }

        string hint = _tool.Mode switch
        {
            NodesMode.Lanes => "Клик по полосе - автобусная; Shift+клик - запрет перестроения",
            _ when !sel => "Клик по узлу - выбрать",
            NodesMode.Arms => "Клик по иконке рукава - сменить знак; ПКМ - снять выбор",
            NodesMode.Bans => "Клик по стрелке - запретить или разрешить манёвр; ПКМ - снять выбор",
            _ when !signal => "Выберите режим узла",
            _ => fixedPlan ? "Цикл - ползунком; протяжка отменяется одним Ctrl+Z" : "Адаптивный план подстраивается под очередь сам",
        };
        if (hint != _hintShown)
        {
            _hintShown = hint;
            _hint.Text = hint;
        }

        _syncing = false;
    }

    private static Label Secondary(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static UiButton Btn(string text, bool toggle) => new UiButton(text, toggle ? UiButtonKind.Toggle : UiButtonKind.Secondary)
    {
        FocusMode = Control.FocusModeEnum.None,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
    };
}
