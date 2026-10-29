using Godot;

namespace TrafficCity.Ui;

/// <summary>Легенда инфо-слоя (UI7) в правой док-панели: открыта, пока включён инфо-режим (F1); в нём клавиши 1/2/3 и кнопки - слои.</summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class LegendScreen : UiScreen
{
    private readonly Button[] _layerButtons = new Button[3];
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private int _layer = 1;

    /// <summary>Содержимое легенды: SimHost подаёт данные ~2 Гц.</summary>
    public UiLegend View { get; private set; } = null!;

    public override StringName? Hotkey => UiActions.InfoToggle;

    public override bool IsOpen => Ui.Router.InfoActive;

    protected override void Build(Control parent)
    {
        _panel = new PanelContainer { Name = "Legend", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        parent.AddChild(_panel);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddChild(box);
        _title = new Label { Text = "Инфо-слой", ThemeTypeVariation = UiTheme.LabelHeader, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_title);

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
            b.Pressed += () => Ui.Router.SetInfoLayer(layer);
            layers.AddChild(b);
            _layerButtons[i] = b;
        }

        View = new UiLegend(box);
    }

    public override void SetOpen(bool open)
    {
        if (open != Ui.Router.InfoActive)
        {
            Ui.Router.SetInfo(open);
        }
    }

    public override bool HandleInput(InputEvent e)
    {
        if (e.IsActionPressed(UiActions.InfoToggle))
        {
            Ui.Router.ToggleInfo();
            return true;
        }

        if (!Ui.Router.InfoActive)
        {
            return false;
        }

        // слои 1-3 переключаются только в инфо-режиме (вне него эти клавиши - скорость, ui-spec.md §14 п. 7)
        int layer = e.IsActionPressed(UiActions.InfoLayer1) ? 1
            : e.IsActionPressed(UiActions.InfoLayer2) ? 2
            : e.IsActionPressed(UiActions.InfoLayer3) ? 3 : 0;
        if (layer > 0)
        {
            Ui.Router.SetInfoLayer(layer);
        }

        return layer > 0;
    }

    public override void Refresh()
    {
        _panel.Visible = Ui.Router.InfoActive;
        for (int i = 0; i < _layerButtons.Length; i++)
        {
            _layerButtons[i].SetPressedNoSignal(Ui.Router.InfoLayer == i + 1);
        }

        if (Ui.Router.InfoLayer != _layer)
        {
            _layer = Ui.Router.InfoLayer;
            View.SetLayer(_layer);
        }

        View.SyncPalette();
        _title.Text = "Инфо-слой: " + (Ui.Router.InfoLayer switch
        {
            2 => "Пешеходы",
            3 => "Зоны",
            _ => "Трафик",
        });
    }
}
