using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Собирает одну Godot <see cref="Theme"/> из <see cref="Tokens"/> (ui-kit.md, стиль «Пластилин»). Вызывать разово (старт, смена палитры
/// или настроек), не в кадре. Варианты кнопок — через <c>theme_type_variation</c>: <see cref="Primary"/>, <see cref="Secondary"/>,
/// <see cref="Ghost"/>, <see cref="Icon"/>, <see cref="Toggle"/>, <see cref="Danger"/>. Твёрдая тень рисуется утолщённым нижним контуром
/// (без размытия, StyleBoxFlat рисует тень только с размытием); нажатие — верх уходит на 3 px вниз, тень 1 px.
/// Выбранное состояние (заливка <c>selected</c>, у иконки — <c>tool-active</c>) есть у переключаемых вариантов: Toggle, Icon, Ghost и базовый Button;
/// у Primary/Secondary/Danger нажатие — только просадка без смены цвета.
/// Масштаб 75–150 % — <c>content_scale_factor</c> окна (<see cref="UiScale"/>), тема в логических пикселях.
/// </summary>
public static class ThemeBuilder
{
    public const string Primary = "ButtonPrimary";
    public const string Secondary = "ButtonSecondary";
    public const string Ghost = "ButtonGhost";
    public const string Icon = "ButtonIcon";
    public const string Toggle = "ButtonToggle";
    public const string Danger = "ButtonDanger";

    /// <summary>Все варианты кнопок (витрина).</summary>
    public static readonly string[] ButtonVariations = { Primary, Secondary, Ghost, Icon, Toggle, Danger };

    /// <summary>Состояния StyleBox кнопки в Godot.</summary>
    public static readonly string[] ButtonStates = { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" };

    private static Font? _font;

    /// <summary>Шрифт по умолчанию: Fredoka 600 (вариативный, ось wght).</summary>
    public static Font DefaultFont
    {
        get
        {
            if (_font == null)
            {
                var file = GD.Load<FontFile>(Tokens.FontPath);
                long wght = TextServerManager.GetPrimaryInterface().NameToTag("wght");
                _font = new FontVariation
                {
                    BaseFont = file,
                    VariationOpentype = new Godot.Collections.Dictionary { { wght, Tokens.FontWeight } },
                };
            }

            return _font;
        }
    }

    public static Theme Build(float panelAlpha = 1f)
    {
        var t = new Theme { DefaultFont = DefaultFont, DefaultFontSize = Tokens.FontBody };
        Color panelBg = Tokens.Surface with { A = panelAlpha };

        t.SetStylebox("panel", "PanelContainer", PanelBox(panelBg));
        t.SetStylebox("panel", "Panel", PanelBox(panelBg));
        t.SetStylebox("panel", "PopupPanel", PanelBox(Tokens.Surface));
        t.SetStylebox("panel", "TooltipPanel", PanelBox(Tokens.Surface, Tokens.RadiusButton, Tokens.Space2, Tokens.ShadowButton));
        t.SetColor("font_color", "TooltipLabel", Tokens.Ink);

        t.SetColor("font_color", "Label", Tokens.Ink);
        t.SetFontSize("font_size", "Label", Tokens.FontBody);
        t.SetTypeVariation(UiTheme.LabelSecondary, "Label");
        t.SetColor("font_color", UiTheme.LabelSecondary, Tokens.InkSecondary);
        t.SetFontSize("font_size", UiTheme.LabelSecondary, Tokens.FontSecondary);
        t.SetTypeVariation(UiTheme.LabelHeader, "Label");
        t.SetFontSize("font_size", UiTheme.LabelHeader, Tokens.FontHeader);
        t.SetColor("default_color", "RichTextLabel", Tokens.Ink);
        t.SetFontSize("normal_font_size", "RichTextLabel", Tokens.FontBody);

        // базовые Button/CheckButton = secondary: старые экраны получают новый вид без переписывания (F4)
        ButtonStyles(t, "Button", Tokens.SurfaceAlt, Tokens.Selected, Tokens.Space3, outline: true);
        ButtonStyles(t, "CheckButton", Tokens.SurfaceAlt, Tokens.Selected, Tokens.Space3, outline: true);
        ButtonStyles(t, "OptionButton", Tokens.SurfaceAlt, Tokens.Selected, Tokens.Space3, outline: true);
        Variation(t, UiTheme.ButtonCompact, Tokens.SurfaceAlt, Tokens.Selected, Tokens.Space2, outline: true);

        Variation(t, Primary, Tokens.Accent, Tokens.Accent, Tokens.Space4, outline: true);
        Variation(t, Secondary, Tokens.SurfaceAlt, Tokens.SurfaceAlt, Tokens.Space4, outline: true);
        Variation(t, Ghost, Colors.Transparent, Tokens.Selected, Tokens.Space4, outline: false);
        Variation(t, Icon, Tokens.SurfaceAlt, Tokens.ToolActive, Tokens.Space1, outline: true);
        Variation(t, Toggle, Tokens.SurfaceAlt, Tokens.Selected, Tokens.Space4, outline: true);
        Variation(t, Danger, Tokens.Danger, Tokens.Danger, Tokens.Space4, outline: true);
        t.SetConstant("icon_max_width", Icon, Tokens.ToolSize - (2 * Tokens.Space1) - (2 * Tokens.Stroke));

        // полосы на всю ширину: контур только со стороны мира, без скругления
        t.SetTypeVariation(UiTheme.BarTop, "PanelContainer");
        t.SetStylebox("panel", UiTheme.BarTop, Bar(panelBg, bottomEdge: true));
        t.SetTypeVariation(UiTheme.BarBottom, "PanelContainer");
        t.SetStylebox("panel", UiTheme.BarBottom, Bar(panelBg, bottomEdge: false));

        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Tokens.Ink with { A = 0.35f }, Thickness = 2 });
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Tokens.Ink with { A = 0.35f }, Thickness = 2, Vertical = true });
        t.SetConstant("separation", "HBoxContainer", Tokens.Space2);
        t.SetConstant("separation", "VBoxContainer", Tokens.Space2);
        return t;
    }

    /// <summary>Панель: кремовая, контур 3 px <c>ink</c>, радиус 18, твёрдая тень 5 px.</summary>
    public static StyleBoxFlat PanelBox(Color bg, int radius = Tokens.RadiusPanel, int pad = Tokens.Space3, int shadow = Tokens.ShadowPanel)
    {
        var sb = new StyleBoxFlat { BgColor = bg, BorderColor = Tokens.Ink, AntiAliasing = true, AntiAliasingSize = 0.6f };
        sb.SetBorderWidthAll(Tokens.Stroke);
        sb.BorderWidthBottom = Tokens.Stroke + shadow;
        sb.SetCornerRadiusAll(radius);
        sb.SetContentMarginAll(pad);
        sb.ContentMarginBottom = pad + shadow;
        return sb;
    }

    private static void Variation(Theme t, string name, Color fill, Color pressedFill, int padX, bool outline)
    {
        t.SetTypeVariation(name, "Button");
        ButtonStyles(t, name, fill, pressedFill, padX, outline);
    }

    private static void ButtonStyles(Theme t, string type, Color fill, Color pressedFill, int padX, bool outline)
    {
        bool ghost = !outline;
        t.SetStylebox("normal", type, ButtonBox(fill, Tokens.Ink, padX, pressed: false, ghost));
        t.SetStylebox("hover", type, ButtonBox(ghost ? Tokens.Ink with { A = 0.08f } : Tokens.Hover(fill), Tokens.Ink, padX, pressed: false, ghost));
        t.SetStylebox("pressed", type, ButtonBox(pressedFill, Tokens.Ink, padX, pressed: true, ghost));
        t.SetStylebox("hover_pressed", type, ButtonBox(Tokens.Hover(pressedFill), Tokens.Ink, padX, pressed: true, ghost));
        t.SetStylebox("disabled", type, ButtonBox(ghost ? Colors.Transparent : Tokens.DisabledFill, Tokens.DisabledInk, padX, pressed: false, ghost));
        t.SetStylebox("focus", type, FocusBox());

        t.SetColor("font_color", type, Tokens.Ink);
        t.SetColor("font_hover_color", type, Tokens.Ink);
        t.SetColor("font_pressed_color", type, Tokens.Ink);
        t.SetColor("font_hover_pressed_color", type, Tokens.Ink);
        t.SetColor("font_focus_color", type, Tokens.Ink);
        t.SetColor("font_disabled_color", type, Tokens.DisabledInk);
        t.SetColor("icon_disabled_color", type, Colors.White with { A = 0.5f });
        t.SetFontSize("font_size", type, Tokens.FontBody);
    }

    /// <summary>
    /// Кнопка: контур 3 px, радиус 14, тень 4 px толстым нижним контуром. Нажата: верх ниже на 3 px (отрицательный expand),
    /// тень 1 px — низ кнопки остаётся на месте, лицевая часть «проседает».
    /// </summary>
    private static StyleBoxFlat ButtonBox(Color fill, Color border, int padX, bool pressed, bool ghost)
    {
        var sb = new StyleBoxFlat { BgColor = fill, BorderColor = border, AntiAliasing = true, AntiAliasingSize = 0.6f };
        sb.SetCornerRadiusAll(Tokens.RadiusButton);
        int stroke = ghost ? 0 : Tokens.Stroke;
        int shadow = ghost ? 0 : pressed ? Tokens.ShadowPressed : Tokens.ShadowButton;
        int sink = ghost || !pressed ? 0 : Tokens.PressSink;
        sb.SetBorderWidthAll(stroke);
        sb.BorderWidthBottom = stroke + shadow;
        sb.ExpandMarginTop = -sink;
        sb.ContentMarginLeft = padX;
        sb.ContentMarginRight = padX;
        sb.ContentMarginTop = Tokens.Space2 + sink;
        sb.ContentMarginBottom = Tokens.Space2 + Tokens.ShadowButton - sink;
        return sb;
    }

    /// <summary>Фокус: второй контур <c>selected</c> 2 px снаружи.</summary>
    private static StyleBoxFlat FocusBox()
    {
        var sb = new StyleBoxFlat { DrawCenter = false, BorderColor = Tokens.Selected, AntiAliasing = true, AntiAliasingSize = 0.6f };
        sb.SetBorderWidthAll(Tokens.FocusStroke);
        sb.SetCornerRadiusAll(Tokens.RadiusButton + Tokens.FocusStroke);
        sb.SetExpandMarginAll(Tokens.FocusStroke + 1);
        return sb;
    }

    private static StyleBoxFlat Bar(Color bg, bool bottomEdge)
    {
        var sb = new StyleBoxFlat { BgColor = bg, BorderColor = Tokens.Ink, AntiAliasing = false };
        if (bottomEdge)
        {
            sb.BorderWidthBottom = Tokens.Stroke;
        }
        else
        {
            sb.BorderWidthTop = Tokens.Stroke;
        }

        sb.ContentMarginLeft = Tokens.Space2;
        sb.ContentMarginRight = Tokens.Space2;
        sb.ContentMarginTop = Tokens.Space1;
        sb.ContentMarginBottom = Tokens.Space1;
        return sb;
    }
}
