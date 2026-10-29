using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>Тон бейджа/тоста. Смысл всегда дублируется текстом (палитры для дальтоников).</summary>
public enum UiTone
{
    Neutral,
    Accent,
    Info,
    Danger,
}

/// <summary>Бейдж-таблетка: короткий статус или счётчик. <c>new UiBadge("3", UiTone.Danger)</c>; <see cref="Set"/> не пересоздаёт узлы.</summary>
public sealed partial class UiBadge : PanelContainer
{
    private readonly Label _label;

    public UiBadge()
        : this(string.Empty)
    {
    }

    public UiBadge(string text, UiTone tone = UiTone.Neutral)
    {
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
        _label = new Label { ThemeTypeVariation = ThemeBuilder.LabelCaption, HorizontalAlignment = HorizontalAlignment.Center };
        AddChild(_label);
        Set(text, tone);
    }

    public static string VariationOf(UiTone tone) => tone switch
    {
        UiTone.Accent => ThemeBuilder.BadgeAccent,
        UiTone.Info => ThemeBuilder.BadgeInfo,
        UiTone.Danger => ThemeBuilder.BadgeDanger,
        _ => ThemeBuilder.BadgeNeutral,
    };

    public void Set(string text, UiTone tone)
    {
        _label.Text = text;
        ThemeTypeVariation = VariationOf(tone);
    }
}
