using System;
using System.Collections.Generic;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Тулбар: панель с рядом кнопок-инструментов (<see cref="UiButton.ForIcon"/>), выбран ровно один (<see cref="ButtonGroup"/>).
/// Клавиша инструмента показывается в подсказке. <c>new UiToolbar().Add("road", "Дороги", "R").Add("bus", "Автобусы", "B")</c>;
/// смена инструмента — событие <see cref="ToolSelected"/> с индексом.
/// </summary>
public sealed partial class UiToolbar : PanelContainer
{
    private readonly HBoxContainer _row;
    private readonly ButtonGroup _group = new() { AllowUnpress = false };
    private readonly List<UiButton> _buttons = new();

    public UiToolbar()
    {
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _row = new HBoxContainer();
        _row.AddThemeConstantOverride("separation", Tokens.Space1);
        AddChild(_row);
    }

    public event Action<int>? ToolSelected;

    public int Selected { get; private set; } = -1;

    public UiButton this[int index] => _buttons[index];

    public int Count => _buttons.Count;

    public UiToolbar Add(string glyph, string tooltip, string? hint = null)
    {
        int index = _buttons.Count;
        UiButton b = UiButton.ForIcon(glyph, tooltip, hint, toggle: true);
        b.ButtonGroup = _group;
        b.Pressed += () => OnPressed(index);
        _buttons.Add(b);
        _row.AddChild(b);
        if (index == 0)
        {
            Select(0);
        }

        return this;
    }

    /// <summary>Разделитель между группами инструментов.</summary>
    public UiToolbar Separator()
    {
        _row.AddChild(new VSeparator());
        return this;
    }

    /// <summary>Выбрать инструмент без события (синхронизация с горячей клавишей или состоянием игры).</summary>
    public void Select(int index)
    {
        Selected = index;
        _buttons[index].SetPressedNoSignal(true);
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (i != index)
            {
                _buttons[i].SetPressedNoSignal(false);
            }
        }
    }

    private void OnPressed(int index)
    {
        if (index == Selected)
        {
            return;
        }

        Selected = index;
        ToolSelected?.Invoke(index);
    }
}
