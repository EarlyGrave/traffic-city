using System;
using System.Collections.Generic;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Сегментированный переключатель: дорожка с контуром, выбранный сегмент залит <c>selected</c>.
/// <c>new UiSegmented("День", "Неделя", "Месяц").OnChanged(i => ...)</c>.
/// </summary>
public sealed partial class UiSegmented : PanelContainer
{
    private readonly ButtonGroup _group = new() { AllowUnpress = false };
    private readonly List<Button> _items = new();
    private Action<int>? _changed;

    public UiSegmented()
        : this(Array.Empty<string>())
    {
    }

    public UiSegmented(params string[] items)
    {
        ThemeTypeVariation = ThemeBuilder.SegmentTrack;
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", Tokens.Space1);
        AddChild(row);
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var b = new Button { Text = items[i], ThemeTypeVariation = ThemeBuilder.Segment, ToggleMode = true, ButtonGroup = _group };
            b.Pressed += () => OnPressed(index);
            _items.Add(b);
            row.AddChild(b);
        }

        if (_items.Count > 0)
        {
            Select(0);
        }
    }

    public int Selected { get; private set; }

    public UiSegmented OnChanged(Action<int> handler)
    {
        _changed += handler;
        return this;
    }

    public UiSegmented Select(int index)
    {
        Selected = index;
        _items[index].SetPressedNoSignal(true);
        for (int i = 0; i < _items.Count; i++)
        {
            if (i != index)
            {
                _items[i].SetPressedNoSignal(false);
            }
        }

        return this;
    }

    public UiSegmented Disable(bool off = true)
    {
        foreach (Button b in _items)
        {
            b.Disabled = off;
        }

        return this;
    }

    private void OnPressed(int index)
    {
        if (index != Selected)
        {
            Selected = index;
            _changed?.Invoke(index);
        }
    }
}
