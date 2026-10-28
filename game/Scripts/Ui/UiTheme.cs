using Godot;

namespace TrafficCity.Ui;

/// <summary>Размеры каркаса в логических пикселях (до масштаба UI; ui-spec.md §3, §11).</summary>
public static class UiMetrics
{
    public const float TopBarHeight = 40f;
    public const float ToolbarHeight = 56f;
    public const float OptionsHeight = 40f;
    public const float DockWidth = 320f;
    public const float Margin = 8f;
    public const float Radius = 4f;

    /// <summary>Кегль: основной, вторичный, заголовок (ui-spec.md §11).</summary>
    public const int FontBody = 15;

    public const int FontSecondary = 13;
    public const int FontHeader = 18;
}

/// <summary>
/// Единая тема UI (ui-spec.md §12): <see cref="StyleBoxFlat"/> с контуром 1 px, скругление 4 px, без теней; один шрифт (встроенный Godot).
/// Собирается в коде из <see cref="UiPalette"/>, чтобы палитра оставалась единственным источником цветов (файл .tres хексы дублировал бы).
/// Варианты типов: <c>LabelSecondary</c>, <c>LabelHeader</c>.
/// </summary>
public static class UiTheme
{
    public const string LabelSecondary = "LabelSecondary";
    public const string LabelHeader = "LabelHeader";

    /// <summary>Полоса сверху (верхняя панель) и снизу (строка опций, тулбар).</summary>
    public const string BarTop = "BarTop";

    public const string BarBottom = "BarBottom";

    public static Theme Build(float panelAlpha = UiPalette.DefaultPanelAlpha)
    {
        var t = new Theme { DefaultFontSize = UiMetrics.FontBody };

        t.SetStylebox("panel", "PanelContainer", Flat(UiPalette.PanelBg(panelAlpha), UiPalette.PanelBorder, 8f));
        t.SetStylebox("panel", "Panel", Flat(UiPalette.PanelBg(panelAlpha), UiPalette.PanelBorder, 0f));

        t.SetColor("font_color", "Label", UiPalette.Text);
        t.SetFontSize("font_size", "Label", UiMetrics.FontBody);
        t.SetTypeVariation(LabelSecondary, "Label");
        t.SetColor("font_color", LabelSecondary, UiPalette.TextSecondary);
        t.SetFontSize("font_size", LabelSecondary, UiMetrics.FontSecondary);
        t.SetTypeVariation(LabelHeader, "Label");
        t.SetFontSize("font_size", LabelHeader, UiMetrics.FontHeader);

        t.SetColor("default_color", "RichTextLabel", UiPalette.Text);
        t.SetFontSize("normal_font_size", "RichTextLabel", UiMetrics.FontBody);

        foreach (string type in new[] { "Button", "CheckButton" })
        {
            t.SetStylebox("normal", type, Flat(UiPalette.Button, UiPalette.PanelBorder, 10f));
            t.SetStylebox("hover", type, Flat(UiPalette.ButtonHover, UiPalette.PanelBorder, 10f));
            t.SetStylebox("pressed", type, Flat(UiPalette.Accent, UiPalette.Accent, 10f));
            t.SetStylebox("hover_pressed", type, Flat(UiPalette.Accent.Lightened(0.1f), UiPalette.Accent, 10f));
            t.SetStylebox("disabled", type, Flat(UiPalette.Button with { A = 0.5f }, UiPalette.PanelBorder with { A = 0.5f }, 10f));
            t.SetStylebox("focus", type, new StyleBoxEmpty());
            t.SetColor("font_color", type, UiPalette.Text);
            t.SetColor("font_hover_color", type, UiPalette.Text);
            t.SetColor("font_pressed_color", type, UiPalette.TextOnAccent);
            t.SetColor("font_hover_pressed_color", type, UiPalette.TextOnAccent);
            t.SetColor("font_disabled_color", type, UiPalette.TextDisabled);
            t.SetFontSize("font_size", type, UiMetrics.FontBody);
        }

        // полосы на всю ширину: контур только со стороны мира, без скругления
        t.SetTypeVariation(BarTop, "PanelContainer");
        t.SetStylebox("panel", BarTop, Bar(UiPalette.PanelBg(panelAlpha), UiPalette.PanelBorder, bottomEdge: true));
        t.SetTypeVariation(BarBottom, "PanelContainer");
        t.SetStylebox("panel", BarBottom, Bar(UiPalette.PanelBg(panelAlpha), UiPalette.PanelBorder, bottomEdge: false));

        var line = new StyleBoxLine { Color = UiPalette.PanelBorder, Thickness = 1 };
        t.SetStylebox("separator", "HSeparator", line);
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = UiPalette.PanelBorder, Thickness = 1, Vertical = true });
        t.SetConstant("separation", "HBoxContainer", 6);
        t.SetConstant("separation", "VBoxContainer", 6);
        return t;
    }

    /// <summary>Тёмный фон для старых отладочных панелей, чей текст рассчитан на тёмное (до UI3).</summary>
    public static StyleBoxFlat DebugPanelStyle() => Flat(UiPalette.DebugPanel, UiPalette.PanelBorder, 8f);

    private static StyleBoxFlat Bar(Color bg, Color border, bool bottomEdge)
    {
        var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border, AntiAliasing = false };
        if (bottomEdge)
        {
            sb.BorderWidthBottom = 1;
        }
        else
        {
            sb.BorderWidthTop = 1;
        }

        sb.ContentMarginLeft = UiMetrics.Margin;
        sb.ContentMarginRight = UiMetrics.Margin;
        sb.ContentMarginTop = 4f;
        sb.ContentMarginBottom = 4f;
        return sb;
    }

    private static StyleBoxFlat Flat(Color bg, Color border, float pad)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            AntiAliasing = false,
        };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll((int)UiMetrics.Radius);
        sb.ContentMarginLeft = pad;
        sb.ContentMarginRight = pad;
        sb.ContentMarginTop = pad > 6f ? 6f : pad;
        sb.ContentMarginBottom = sb.ContentMarginTop;
        return sb;
    }
}
