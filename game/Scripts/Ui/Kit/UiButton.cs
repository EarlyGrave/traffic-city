using System;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>Вид кнопки = вариант темы (<see cref="ThemeBuilder"/>).</summary>
public enum UiButtonKind
{
    Primary,
    Secondary,
    Ghost,
    Icon,
    Toggle,
    Danger,
}

/// <summary>
/// Кнопка кита: тонкая обёртка над <see cref="Button"/> с вариантом темы, необязательной иконкой и подсказкой с клавишей.
/// <c>new UiButton("Ок", UiButtonKind.Primary).OnPressed(Save)</c>; иконка-инструмент — <c>UiButton.ForIcon("road", "Дороги", "R")</c>.
/// У <see cref="UiButtonKind.Icon"/> — пиктограмма без плашки (<see cref="UiIcons.Glyph"/>), плашку и её цвет по состоянию рисует стиль.
/// Наведение/нажатие — короткое масштабирование (<see cref="Tokens.AnimFast"/>/<see cref="Tokens.AnimPress"/>) вручную в
/// <c>_Process</c>, который работает только пока идёт анимация: в стационаре 0 байт и 0 вызовов в кадр.
/// </summary>
public sealed partial class UiButton : Button
{
    private const float HoverScale = 1.04f;
    private const float PressScale = 0.97f;

    private string? _hint;
    private float _scale = 1f;
    private float _target = 1f;
    private float _speed;

    public UiButton()
        : this(string.Empty)
    {
    }

    public UiButton(string text, UiButtonKind kind = UiButtonKind.Secondary, Texture2D? icon = null)
    {
        Text = text;
        Kind = kind;
        ThemeTypeVariation = VariationOf(kind);
        ToggleMode = kind == UiButtonKind.Toggle;
        if (icon != null)
        {
            Icon = icon; // рядом с текстом размер ограничивает icon_max_width темы
        }

        if (kind == UiButtonKind.Icon)
        {
            ExpandIcon = true;
            IconAlignment = HorizontalAlignment.Center;
            CustomMinimumSize = new Vector2(Tokens.ToolSize, Tokens.ToolSize);
        }

        MouseEntered += () => Animate(HoverScale, Tokens.AnimFast);
        MouseExited += () => Animate(1f, Tokens.AnimFast);
        ButtonDown += () => Animate(PressScale, Tokens.AnimPress);
        ButtonUp += () => Animate(IsHovered() ? HoverScale : 1f, Tokens.AnimFast);
        Resized += () => PivotOffset = Size / 2f;
        SetProcess(false);
    }

    public UiButtonKind Kind { get; }

    /// <summary>Квадратная кнопка-инструмент с пиктограммой; для переключения инструментов — <paramref name="toggle"/>.</summary>
    public static UiButton ForIcon(string glyph, string tooltip, string? hint = null, bool toggle = false)
    {
        var b = new UiButton(string.Empty, UiButtonKind.Icon, UiIcons.Glyph(glyph)) { ToggleMode = toggle };
        return b.WithTooltip(tooltip, hint);
    }

    public static string VariationOf(UiButtonKind kind) => kind switch
    {
        UiButtonKind.Primary => ThemeBuilder.Primary,
        UiButtonKind.Ghost => ThemeBuilder.Ghost,
        UiButtonKind.Icon => ThemeBuilder.Icon,
        UiButtonKind.Toggle => ThemeBuilder.Toggle,
        UiButtonKind.Danger => ThemeBuilder.Danger,
        _ => ThemeBuilder.Secondary,
    };

    public UiButton WithTooltip(string text, string? hint = null)
    {
        TooltipText = text;
        _hint = hint;
        return this;
    }

    public UiButton OnPressed(Action handler)
    {
        Pressed += handler;
        return this;
    }

    public UiButton OnToggled(Action<bool> handler)
    {
        Toggled += on => handler(on);
        return this;
    }

    public UiButton Selected(bool on = true)
    {
        ToggleMode = true;
        SetPressedNoSignal(on);
        return this;
    }

    public UiButton Disable(bool off = true)
    {
        Disabled = off;
        return this;
    }

    public override GodotObject _MakeCustomTooltip(string forText) => UiTooltip.Build(forText, _hint);

    public override void _Process(double delta)
    {
        _scale = Mathf.MoveToward(_scale, _target, _speed * (float)delta);
        Scale = new Vector2(_scale, _scale);
        if (_scale == _target)
        {
            SetProcess(false);
        }
    }

    private void Animate(float target, float duration)
    {
        if (Disabled)
        {
            target = 1f;
        }

        _target = target;
        _speed = Mathf.Max(Mathf.Abs(target - _scale), 0.001f) / duration;
        SetProcess(true);
    }
}
