using System;
using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>Легенда инфо-слоя (UI7) в правой док-панели: открыта, пока включён инфо-режим (F1); в нём клавиши 1/2/3 и кнопки - слои.</summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class LegendScreen : UiScreen
{
    private UiPanel _panel = null!;
    private UiSegmented _layers = null!;
    private int _layer = 1;

    /// <summary>Содержимое легенды: SimHost подаёт данные ~2 Гц.</summary>
    public UiLegend View { get; private set; } = null!;

    public override StringName? Hotkey => UiActions.InfoToggle;

    public override bool IsOpen => Ui.Router.InfoActive;

    protected override void Build(Control parent)
    {
        _panel = new UiPanel("Инфо-слой") { Name = "Legend", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        parent.AddChild(_panel);
        _layers = new UiSegmented("1 Трафик", "2 Пешеходы", "3 Зоны") { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _layers.OnChanged(i => Ui.Router.SetInfoLayer(i + 1));
        _panel.Add(_layers);

        View = new UiLegend(_panel.Body);
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
        _layers.Select(Math.Clamp(Ui.Router.InfoLayer - 1, 0, 2));

        if (Ui.Router.InfoLayer != _layer)
        {
            _layer = Ui.Router.InfoLayer;
            View.SetLayer(_layer);
        }

        View.SyncPalette();
        _panel.Title!.Text = "Инфо-слой: " + (Ui.Router.InfoLayer switch
        {
            2 => "Пешеходы",
            3 => "Зоны",
            _ => "Трафик",
        });
    }
}
