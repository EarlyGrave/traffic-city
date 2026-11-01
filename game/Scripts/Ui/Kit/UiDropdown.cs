using System;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Выпадающий список: <see cref="OptionButton"/> в стиле вторичной кнопки, попап — панель кита (тема).
/// <c>new UiDropdown("Обычная", "Дейтеранопия", "Тританопия").OnChanged(i => ...)</c>.
/// </summary>
public sealed partial class UiDropdown : OptionButton
{
    private Action<int>? _changed;

    public UiDropdown()
        : this(Array.Empty<string>())
    {
    }

    public UiDropdown(params string[] items)
    {
        foreach (string s in items)
        {
            AddItem(s);
        }

        if (items.Length > 0)
        {
            Selected = 0;
        }

        ItemSelected += i => _changed?.Invoke((int)i);
    }

    public UiDropdown OnChanged(Action<int> handler)
    {
        _changed += handler;
        return this;
    }

    public UiDropdown Choose(int index)
    {
        Selected = index;
        return this;
    }

    public UiDropdown Disable(bool off = true)
    {
        Disabled = off;
        return this;
    }
}
