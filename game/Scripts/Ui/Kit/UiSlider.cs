using System;
using System.Globalization;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Слайдер с подписью и значением справа: <c>new UiSlider("Громкость", 0, 100, 5, 70, "{0} %").OnChanged(v => ...)</c>.
/// Текст значения форматируется только при изменении (не в кадре).
/// </summary>
public sealed partial class UiSlider : HBoxContainer
{
    private readonly string _format;
    private readonly Label _value;
    private Action<double>? _changed;

    public UiSlider()
        : this(string.Empty, 0, 100, 1, 0)
    {
    }

    public UiSlider(string label, double min, double max, double step, double value, string format = "{0}")
    {
        _format = format;
        if (label.Length > 0)
        {
            AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(96, 0) });
        }

        Slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(160, 24),
        };
        AddChild(Slider);
        _value = new Label { CustomMinimumSize = new Vector2(52, 0), HorizontalAlignment = HorizontalAlignment.Right };
        AddChild(_value);
        Slider.ValueChanged += OnValue;
        OnValue(value);
    }

    public HSlider Slider { get; }

    public double Value => Slider.Value;

    public UiSlider OnChanged(Action<double> handler)
    {
        _changed += handler;
        return this;
    }

    public UiSlider Disable(bool off = true)
    {
        Slider.Editable = !off;
        Slider.FocusMode = off ? FocusModeEnum.None : FocusModeEnum.All;
        Modulate = off ? new Color(1f, 1f, 1f, 0.6f) : Colors.White;
        return this;
    }

    private void OnValue(double v)
    {
        _value.Text = string.Format(CultureInfo.InvariantCulture, _format, v);
        _changed?.Invoke(v);
    }
}
