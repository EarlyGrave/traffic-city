using System.Globalization;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Витрина UI-кита (F1): все варианты кнопок во всех состояниях, панель, тулбар из 12 иконок, переключатели палитры и масштаба.
/// Запуск: сцена <c>res://Scripts/Ui/Kit/KitGallery.tscn</c>; аргументы после <c>--</c>: <c>--kit-palette 0..2</c>,
/// <c>--ui-scale 75..150</c>, <c>--ui-shot файл.png [--ui-shot-frame K]</c> (кадр окна в PNG и выход, как у <see cref="UiRoot"/>).
/// Масштаб витрины = пользовательский процент (без базы от высоты окна), чтобы 100 % и 150 % сравнивались на одном окне.
/// Тема и дерево строятся разово (старт и клик по переключателю), в кадре ничего не выделяется.
/// </summary>
public sealed partial class KitGallery : Control
{
    private static readonly string[] _icons =
    {
        "road", "intersection", "zones", "bus", "bulldoze", "layers", "stats", "undo", "redo", "pause", "fast", "settings",
    };

    private static readonly string[] _variationTitles = { "Основная", "Вторичная", "Призрачная", "Иконка", "Переключатель", "Опасная" };
    private static readonly string[] _stateTitles = { "обычная", "наведение", "нажата", "выбрана", "заблок.", "фокус" };
    private static readonly int[] _scales = { 75, 100, 125, 150 };

    private readonly Texture2D[] _iconTex = new Texture2D[12];
    private Control _content = null!;
    private int _scalePercent = 100;
    private string _shotPath = string.Empty;
    private int _shotFrame = 20;
    private int _frames;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        ParseArgs(OS.GetCmdlineUserArgs());
        for (int i = 0; i < _icons.Length; i++)
        {
            _iconTex[i] = GD.Load<Texture2D>("res://assets/ui/icons/toolbar/" + _icons[i] + ".png");
        }

        ApplyScale();
        Rebuild();
        SetProcess(_shotPath.Length > 0);
    }

    public override void _Process(double delta)
    {
        if (++_frames == _shotFrame)
        {
            RenderingServer.FramePostDraw += OnShotDraw;
        }
    }

    private void OnShotDraw()
    {
        RenderingServer.FramePostDraw -= OnShotDraw;
        Image img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(_shotPath);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui-shot] {_shotPath} {img.GetWidth()}x{img.GetHeight()} {err}"));
        GetTree().Quit();
    }

    private void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            string v = args[i + 1];
            if (args[i] == "--kit-palette" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is >= 0 and <= 2)
            {
                Tokens.SetPalette((ColorblindMode)p);
            }
            else if (args[i] == "--ui-scale" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
            {
                _scalePercent = UiScale.Snap(s);
            }
            else if (args[i] == "--ui-shot")
            {
                _shotPath = v;
            }
            else if (args[i] == "--ui-shot-frame" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f))
            {
                _shotFrame = f;
            }
        }
    }

    private void ApplyScale()
    {
        Window w = GetWindow();
        w.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        w.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        // canvas_items сам растягивает канву от базового размера проекта до окна — делим, чтобы 1 логический px = 1 px x процент
        float baseW = (float)ProjectSettings.GetSetting("display/window/size/viewport_width").AsDouble();
        float baseH = (float)ProjectSettings.GetSetting("display/window/size/viewport_height").AsDouble();
        float stretch = Mathf.Min(w.Size.X / baseW, w.Size.Y / baseH);
        w.ContentScaleFactor = _scalePercent / 100f / stretch;
    }

    private void SetPalette(int index)
    {
        Tokens.SetPalette((ColorblindMode)index);
        Rebuild();
    }

    private void SetScale(int percent)
    {
        _scalePercent = UiScale.Snap(percent);
        ApplyScale();
        Rebuild();
    }

    private void Rebuild()
    {
        if (_content != null)
        {
            RemoveChild(_content);
            _content.QueueFree();
        }

        Theme theme = ThemeBuilder.Build();
        Theme = theme;

        var root = new Control();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddChild(new ColorRect { Color = Tokens.WorldBg, AnchorRight = 1f, AnchorBottom = 1f });
        var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, Tokens.Space4);
        }

        root.AddChild(margin);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", Tokens.Space3);
        margin.AddChild(col);

        col.AddChild(Header());
        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", Tokens.Space4);
        col.AddChild(body);
        body.AddChild(ButtonGrid(theme));
        body.AddChild(SamplePanel());
        col.AddChild(Toolbar());

        _content = root;
        AddChild(root);
    }

    private HBoxContainer Header()
    {
        var row = new HBoxContainer();
        var title = new Label { Text = "UI-кит «Пластилин»", ThemeTypeVariation = UiTheme.LabelHeader, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", Tokens.FontTitle);
        row.AddChild(title);
        string[] palettes = { "Обычная", "Д/П", "Тритан." };
        for (int i = 0; i < palettes.Length; i++)
        {
            int idx = i;
            var b = new Button { Text = palettes[i], ThemeTypeVariation = ThemeBuilder.Toggle, ToggleMode = true, ButtonPressed = (int)Tokens.Mode == i };
            b.Pressed += () => SetPalette(idx);
            row.AddChild(b);
        }

        row.AddChild(new VSeparator());
        foreach (int s in _scales)
        {
            int pct = s;
            var b = new Button { Text = s.ToString(CultureInfo.InvariantCulture) + " %", ThemeTypeVariation = ThemeBuilder.Toggle, ToggleMode = true, ButtonPressed = _scalePercent == s };
            b.Pressed += () => SetScale(pct);
            row.AddChild(b);
        }

        return row;
    }

    /// <summary>Сетка вариант x состояние: состояния показаны подменой стиля normal (наведение и фокус иначе не снять скриншотом).</summary>
    private GridContainer ButtonGrid(Theme theme)
    {
        var grid = new GridContainer { Columns = 1 + _stateTitles.Length };
        grid.AddThemeConstantOverride("h_separation", Tokens.Space2);
        grid.AddThemeConstantOverride("v_separation", Tokens.Space3);
        grid.AddChild(new Control());
        foreach (string s in _stateTitles)
        {
            grid.AddChild(new Label { Text = s, ThemeTypeVariation = UiTheme.LabelSecondary, HorizontalAlignment = HorizontalAlignment.Center });
        }

        for (int v = 0; v < ThemeBuilder.ButtonVariations.Length; v++)
        {
            string variation = ThemeBuilder.ButtonVariations[v];
            grid.AddChild(new Label { Text = _variationTitles[v], ThemeTypeVariation = UiTheme.LabelSecondary, SizeFlagsVertical = SizeFlags.ShrinkCenter });
            for (int s = 0; s < _stateTitles.Length; s++)
            {
                grid.AddChild(StateButton(theme, variation, s));
            }
        }

        return grid;
    }

    private Button StateButton(Theme theme, string variation, int state)
    {
        bool icon = variation == ThemeBuilder.Icon;
        var b = new Button { ThemeTypeVariation = variation, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        if (icon)
        {
            b.Icon = _iconTex[0];
            b.ExpandIcon = true;
            b.CustomMinimumSize = new Vector2(Tokens.ToolSize, Tokens.ToolSize);
        }
        else
        {
            b.Text = variation == ThemeBuilder.Danger ? "Снести" : "Кнопка";
            b.CustomMinimumSize = new Vector2(96, 0);
        }

        string box = state switch
        {
            1 => "hover",
            2 => "pressed",
            3 => "pressed",
            4 => "disabled",
            _ => "normal",
        };

        if (state == 3)
        {
            // выбрана = зажатый переключатель: заливка selected (у иконки — tool-active), просадка
            b.ToggleMode = true;
            b.ButtonPressed = true;
        }
        else if (state == 4)
        {
            b.Disabled = true;
        }
        else if (state != 0)
        {
            b.AddThemeStyleboxOverride("normal", theme.GetStylebox(box, variation));
        }

        if (state == 5)
        {
            b.AddThemeStyleboxOverride("normal", theme.GetStylebox("normal", variation));
            var focus = new Panel { MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1f, AnchorBottom = 1f };
            focus.AddThemeStyleboxOverride("panel", theme.GetStylebox("focus", variation));
            b.AddChild(focus);
        }

        if (state == 2 && variation == ThemeBuilder.Toggle)
        {
            b.AddThemeStyleboxOverride("normal", theme.GetStylebox("pressed", ThemeBuilder.Secondary));
        }

        return b;
    }

    private static PanelContainer SamplePanel()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(250, 0), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        var v = new VBoxContainer();
        panel.AddChild(v);
        v.AddChild(new Label { Text = "Панель", ThemeTypeVariation = UiTheme.LabelHeader });
        v.AddChild(new Label { Text = "Бюджет: 12 345 ₽", });
        v.AddChild(new Label { Text = "Население 8 096 · 08:30", });
        v.AddChild(new Label { Text = "Цифры 0123456789", ThemeTypeVariation = UiTheme.LabelSecondary });
        v.AddChild(new HSeparator());
        var row = new HBoxContainer();
        row.AddChild(new Button { Text = "Ок", ThemeTypeVariation = ThemeBuilder.Primary });
        row.AddChild(new Button { Text = "Отмена", ThemeTypeVariation = ThemeBuilder.Secondary });
        v.AddChild(row);
        return panel;
    }

    private PanelContainer Toolbar()
    {
        var bar = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", Tokens.Space1);
        bar.AddChild(row);
        for (int i = 0; i < _iconTex.Length; i++)
        {
            row.AddChild(new TextureRect
            {
                Texture = _iconTex[i],
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(Tokens.ToolSize + Tokens.Space2, Tokens.ToolSize + Tokens.Space2),
                TooltipText = _icons[i],
            });
        }

        return bar;
    }
}
