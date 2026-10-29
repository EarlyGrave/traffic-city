using System.Globalization;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Витрина UI-кита (F1, F2): все компоненты Kit во всех состояниях (кнопки, тулбар, сегменты, слайдер, список, панель, карточка,
/// плитки KPI, бейджи, подсказка, диалог, тосты), переключатели палитры и масштаба.
/// Запуск: сцена <c>res://Scripts/Ui/Kit/KitGallery.tscn</c>; аргументы после <c>--</c>: <c>--kit-palette 0..2</c>,
/// <c>--ui-scale 50..150</c>, <c>--ui-shot файл.png [--ui-shot-frame K]</c> (кадр окна в PNG и выход, как у <see cref="UiRoot"/>).
/// Масштаб витрины = пользовательский процент (без базы от высоты окна), чтобы 100 % и 150 % сравнивались на одном окне.
/// Тема и дерево строятся разово (старт и клик по переключателю), в кадре ничего не выделяется.
/// </summary>
public sealed partial class KitGallery : Control
{
    private static readonly string[] _variationTitles = { "Основная", "Вторичная", "Призрачная", "Иконка", "Переключатель", "Опасная" };
    private static readonly string[] _stateTitles = { "обычная", "наведение", "нажата", "выбрана", "заблок.", "фокус" };
    private static readonly int[] _scales = { 50, 75, 100, 125, 150 };

    private static readonly string[] _toolTitles =
    {
        "Дороги", "Перекрёстки", "Зоны", "Автобусы", "Снос", "Слои", "Статистика", "Отменить", "Повторить", "Пауза", "Быстрее", "Настройки",
    };

    private static readonly string[] _toolKeys = { "R", "I", "Z", "B", "X", "L", "S", "Ctrl+Z", "Ctrl+Y", "Space", "F", "Esc" };

    private Control _content = null!;
    private UiDialog _dialog = null!;
    private UiToast _toasts = null!;
    private int _scalePercent = 100;
    private string _shotPath = string.Empty;
    private int _shotFrame = 20;
    private int _frames;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        ParseArgs(OS.GetCmdlineUserArgs());
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
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        col.AddChild(scroll);
        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", Tokens.Space6);
        scroll.AddChild(body);
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", Tokens.Space4);
        var right = new VBoxContainer();
        right.AddThemeConstantOverride("separation", Tokens.Space4);
        body.AddChild(left);
        body.AddChild(right);
        FillLeft(left, theme);
        FillRight(right);

        _dialog = new UiDialog();
        root.AddChild(_dialog);
        _toasts = new UiToast();
        root.AddChild(_toasts);
        _toasts.Push("Маршрут 7 открыт", UiTone.Info, "автобус", 0f);
        _toasts.Push("Не хватает бюджета", UiTone.Danger, "!", 0f);

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

    private static Label Section(string text) => new() { Text = text, ThemeTypeVariation = UiTheme.LabelHeader };

    private static Label Caption(string text) => new() { Text = text, ThemeTypeVariation = UiTheme.LabelSecondary, SizeFlagsVertical = SizeFlags.ShrinkCenter };

    private static HBoxContainer Row(params Control[] items)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", Tokens.Space3);
        foreach (Control c in items)
        {
            row.AddChild(c);
        }

        return row;
    }

    private static void FillLeft(VBoxContainer col, Theme theme)
    {
        col.AddChild(Section("Кнопки"));
        col.AddChild(ButtonGrid(theme));
        col.AddChild(Row(
            Caption("С иконкой"),
            new UiButton("Дорога", UiButtonKind.Primary, UiIcons.Glyph("road")),
            new UiButton("Автобус", UiButtonKind.Secondary, UiIcons.Glyph("bus")),
            new UiButton("Снести", UiButtonKind.Danger, UiIcons.Glyph("bulldoze")),
            new UiButton("Слои", UiButtonKind.Ghost, UiIcons.Glyph("layers"))));

        col.AddChild(Section("Тулбар"));
        var bar = new UiToolbar();
        for (int i = 0; i < UiIcons.ToolbarNames.Length; i++)
        {
            if (i == 7)
            {
                bar.Separator();
            }

            bar.Add(UiIcons.ToolbarNames[i], _toolTitles[i], _toolKeys[i]);
        }

        bar.Select(0);
        bar[3].AddThemeStyleboxOverride("normal", theme.GetStylebox("hover", ThemeBuilder.Icon));
        bar[4].Disabled = true;
        col.AddChild(bar);
        col.AddChild(Caption("выбран «Дороги», наведение — «Автобусы», заблокирован — «Снос»"));

        col.AddChild(Section("Сегменты"));
        col.AddChild(Row(new UiSegmented("День", "Неделя", "Месяц"), new UiSegmented("Авто", "Пешком").Select(1).Disable()));

        col.AddChild(Section("Слайдер и список"));
        col.AddChild(new UiSlider("Громкость", 0, 100, 5, 70, "{0} %"));
        col.AddChild(new UiSlider("Заблок.", 0, 100, 5, 30, "{0} %").Disable());
        var hover = new UiDropdown("Обычная", "Дейтеранопия", "Тританопия");
        hover.AddThemeStyleboxOverride("normal", theme.GetStylebox("hover", "OptionButton"));
        col.AddChild(Row(
            new UiDropdown("Обычная", "Дейтеранопия", "Тританопия").Choose((int)Tokens.Mode),
            hover,
            new UiDropdown("Заблок.").Disable()));
    }

    private void FillRight(VBoxContainer col)
    {
        col.AddChild(Section("Панель и карточка"));
        var panel = new UiPanel("Бюджет").MinWidth(300);
        panel.Add(new Label { Text = "Баланс: 12 345 ₽" })
            .Add(new Label { Text = "Цифры 0123456789", ThemeTypeVariation = UiTheme.LabelSecondary })
            .Add(new UiCard("Маршрут 7").Add(Row(new Label { Text = "12 автобусов" }, new UiBadge("в пути", UiTone.Info))))
            .AddRow(new UiButton("Ок", UiButtonKind.Primary), new UiButton("Отмена"));
        col.AddChild(panel);

        col.AddChild(Section("Плитки KPI"));
        col.AddChild(Row(
            new UiTile("Население", "8 096", "+120", UiTone.Info),
            new UiTile("Пробки", "14 %", "−3", UiTone.Danger),
            new UiTile("Бюджет", "12 345")));

        col.AddChild(Section("Бейджи"));
        col.AddChild(Row(new UiBadge("обычный"), new UiBadge("новое", UiTone.Accent), new UiBadge("инфо", UiTone.Info), new UiBadge("опасно", UiTone.Danger)));

        col.AddChild(Section("Подсказка"));
        col.AddChild(UiTooltip.BuildPanel("Дороги", "R"));

        col.AddChild(Section("Диалог"));
        var dialog = new UiPanel("Снести район?", ThemeBuilder.Dialog).MinWidth(320);
        dialog.Add(new Label { Text = "Действие нельзя отменить." })
            .AddRow(new UiButton("Отмена"), new UiButton("Снести", UiButtonKind.Danger));
        dialog.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        col.AddChild(dialog);
        col.AddChild(Row(
            new UiButton("Открыть диалог").OnPressed(() => _dialog.Open("Снести район?", "Enter — снести, Esc — отмена.", "Снести", "Отмена", danger: true)),
            new UiButton("Тост").OnPressed(() => _toasts.Push("Сохранено", UiTone.Info, "ок"))));
    }

    /// <summary>Сетка вариант x состояние: состояния показаны подменой стиля normal (наведение и фокус иначе не снять скриншотом).</summary>
    private static GridContainer ButtonGrid(Theme theme)
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
            grid.AddChild(Caption(_variationTitles[v]));
            for (int s = 0; s < _stateTitles.Length; s++)
            {
                grid.AddChild(StateButton(theme, (UiButtonKind)v, s));
            }
        }

        return grid;
    }

    private static UiButton StateButton(Theme theme, UiButtonKind kind, int state)
    {
        string variation = UiButton.VariationOf(kind);
        UiButton b = kind == UiButtonKind.Icon
            ? UiButton.ForIcon("road", "Дороги", "R")
            : new UiButton(kind == UiButtonKind.Danger ? "Снести" : "Кнопка", kind) { CustomMinimumSize = new Vector2(96, 0) };
        b.FocusMode = FocusModeEnum.None;
        b.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;

        switch (state)
        {
            case 1:
                b.AddThemeStyleboxOverride("normal", theme.GetStylebox("hover", variation));
                break;
            case 2:
                // нажатие = просадка; у переключателя нажатие без выбора показываем стилем вторичной кнопки
                b.AddThemeStyleboxOverride("normal", theme.GetStylebox("pressed", kind == UiButtonKind.Toggle ? ThemeBuilder.Secondary : variation));
                break;
            case 3:
                b.Selected();
                break;
            case 4:
                b.Disabled = true;
                break;
            case 5:
                var focus = new Panel { MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1f, AnchorBottom = 1f };
                focus.AddThemeStyleboxOverride("panel", theme.GetStylebox("focus", variation));
                b.AddChild(focus);
                break;
        }

        return b;
    }
}
