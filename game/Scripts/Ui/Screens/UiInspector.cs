using System;
using System.Text;
using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Правая панель-инспектор (UI3, ui-spec.md §3.3, §7): заголовок с ×, строки «ключ - значение», таблица рукавов узла, примечание, ряд действий.
/// Только вид: данные и смысл кнопок задаёт хост мира (SimHost.Inspect). Узлы строк созданы один раз (пул), лишнее скрыто;
/// текст уходит в <see cref="Label"/> только при изменении, сравнение - со своей копией строки (без чтения <c>Label.Text</c> из движка).
/// </summary>
#pragma warning disable CA1001 // узлы панели принадлежат дереву сцены и освобождаются вместе с ним
public sealed class UiInspector
#pragma warning restore CA1001
{
    public const int MaxRows = 8;
    public const int MaxActions = 4;
    public const int ArmColumns = 7;
    public const int MaxArmRows = 8;

    private readonly UiPanel _root;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _content;
    private float _maxHeight = float.MaxValue;
    private readonly Label _title;
    private readonly Label[] _keys = new Label[MaxRows];
    private readonly Label[] _values = new Label[MaxRows];
    private readonly string[] _keyText = new string[MaxRows];
    private readonly string[] _valueText = new string[MaxRows];
    private readonly GridContainer _rows;
    private readonly GridContainer _arms;
    private readonly Control _armsCard;
    private readonly Label[] _armCells = new Label[ArmColumns * (MaxArmRows + 1)];
    private readonly string[] _armText = new string[ArmColumns * (MaxArmRows + 1)];
    private readonly Color[] _armColor = new Color[ArmColumns * (MaxArmRows + 1)];
    private readonly Label _note;
    private readonly UiCard _noteCard;
    private readonly HSeparator _actionsSep;
    private readonly HFlowContainer _actionsRow;
    private readonly UiButton[] _actions = new UiButton[MaxActions];
    private string _titleText = string.Empty;
    private string _noteText = string.Empty;
    private int _rowCount;
    private int _armRows = -1;

    public UiInspector(Control parent)
    {
        _root = new UiPanel { Name = "Inspector", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        parent.AddChild(_root);
        VBoxContainer box = _root.Body;

        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(head);
        _title = new Label
        {
            ThemeTypeVariation = UiTheme.LabelHeader,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        head.AddChild(_title);
        var close = new UiButton("×", UiButtonKind.Ghost).WithTooltip("Закрыть", "Esc");
        close.FocusMode = Control.FocusModeEnum.None;
        close.CustomMinimumSize = new Vector2(36f, 36f);
        close.Pressed += () => CloseRequested?.Invoke();
        head.AddChild(close);

        // тело прокручивается: высоту задаёт SetMaxHeight (док не должен выходить за тулбар)
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddChild(_scroll);
        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        _content.AddThemeConstantOverride("separation", Tokens.Space2);
        _scroll.AddChild(_content);
        _content.MinimumSizeChanged += FitHeight;
        box = _content;

        var rowsCard = new UiCard();
        box.AddChild(rowsCard);

        _rows = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        _rows.AddThemeConstantOverride("h_separation", 16);
        _rows.AddThemeConstantOverride("v_separation", 4);
        rowsCard.Add(_rows);
        for (int i = 0; i < MaxRows; i++)
        {
            _keyText[i] = string.Empty;
            _valueText[i] = string.Empty;
            _keys[i] = new Label { ThemeTypeVariation = UiTheme.LabelSecondary, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _values[i] = new Label
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = false,
            };
            _rows.AddChild(_keys[i]);
            _rows.AddChild(_values[i]);
        }

        _arms = new GridContainer { Columns = ArmColumns, MouseFilter = Control.MouseFilterEnum.Ignore };
        _arms.AddThemeConstantOverride("h_separation", 4);
        _arms.AddThemeConstantOverride("v_separation", 2);
        var armsCard = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        armsCard.AddChild(_arms);
        _armsCard = armsCard;
        box.AddChild(armsCard);
        for (int i = 0; i < _armCells.Length; i++)
        {
            bool header = i < ArmColumns;
            _armText[i] = string.Empty;
            _armCells[i] = new Label
            {
                ThemeTypeVariation = header ? UiTheme.LabelSecondary : string.Empty,
                HorizontalAlignment = (i % ArmColumns) == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                SizeFlagsHorizontal = (i % ArmColumns) == ArmColumns - 1 ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _arms.AddChild(_armCells[i]);
        }

        _note = new Label
        {
            ThemeTypeVariation = UiTheme.LabelSecondary,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(UiMetrics.DockWidth - 96f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _noteCard = new UiCard { Visible = false };
        _noteCard.Add(_note);
        box.AddChild(_noteCard);

        _actionsSep = new HSeparator();
        box.AddChild(_actionsSep);
        // ряд кнопок переносится на вторую строку, если подписи не влезают в 320 px (пресеты узла, «Заменить профиль»)
        _actionsRow = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _actionsRow.AddThemeConstantOverride("h_separation", 6);
        _actionsRow.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(_actionsRow);
        for (int i = 0; i < MaxActions; i++)
        {
            int idx = i;
            var b = new UiButton(string.Empty, UiButtonKind.Secondary)
            {
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0f, 36f),
                Visible = false,
            };
            b.Pressed += () => ActionPressed?.Invoke(idx);
            _actionsRow.AddChild(b);
            _actions[i] = b;
        }
    }

    /// <summary>Предел высоты панели в доке: тело прокручивается, если не влезает (шапка и поля панели вычитаются здесь).</summary>
    public void SetMaxHeight(float panelMax)
    {
        float body = Math.Max(120f, panelMax - 36f - (2f * Tokens.Space3) - (2f * Tokens.Stroke) - Tokens.ShadowPanel - Tokens.Space2);
        if (Math.Abs(body - _maxHeight) > 0.5f)
        {
            _maxHeight = body;
            FitHeight();
        }
    }

    private void FitHeight()
    {
        float h = Math.Min(_content.GetCombinedMinimumSize().Y, _maxHeight);
        if (Math.Abs(_scroll.CustomMinimumSize.Y - h) > 0.5f)
        {
            _scroll.CustomMinimumSize = new Vector2(0f, h);
        }
    }

    /// <summary>Нажата кнопка действия (номер в ряду).</summary>
    public event Action<int>? ActionPressed;

    /// <summary>Нажат × в заголовке.</summary>
    public event Action? CloseRequested;

    public bool Visible
    {
        get => _root.Visible;
        set => _root.Visible = value;
    }

    /// <summary>Корневой узел панели (проверки и скриптовые прогоны: координаты кнопок).</summary>
    public Control Root => _root;

    /// <summary>Кнопка действия (скриптовые прогоны).</summary>
    public UiButton Action(int i) => _actions[i];

    public void SetTitle(string text)
    {
        if (!string.Equals(_titleText, text, StringComparison.Ordinal))
        {
            _titleText = text;
            _title.Text = text;
        }
    }

    /// <summary>Строка <paramref name="i"/>: ключ (обычно литерал) и значение из буфера; узел меняется только при изменении текста.</summary>
    public void SetRow(int i, string key, StringBuilder value)
    {
        if (!ReferenceEquals(_keyText[i], key) && !string.Equals(_keyText[i], key, StringComparison.Ordinal))
        {
            _keyText[i] = key;
            _keys[i].Text = key;
        }

        if (!value.Equals(_valueText[i].AsSpan()))
        {
            _valueText[i] = value.ToString();
            _values[i].Text = _valueText[i];
        }
    }

    public void SetRow(int i, string key, string value)
    {
        if (!string.Equals(_keyText[i], key, StringComparison.Ordinal))
        {
            _keyText[i] = key;
            _keys[i].Text = key;
        }

        if (!string.Equals(_valueText[i], value, StringComparison.Ordinal))
        {
            _valueText[i] = value;
            _values[i].Text = value;
        }
    }

    /// <summary>Сколько строк показать (остальные скрыты).</summary>
    public void SetRowCount(int n)
    {
        n = Math.Clamp(n, 0, MaxRows);
        if (n == _rowCount)
        {
            return;
        }

        _rowCount = n;
        for (int i = 0; i < MaxRows; i++)
        {
            _keys[i].Visible = i < n;
            _values[i].Visible = i < n;
        }
    }

    /// <summary>Ячейка таблицы рукавов: строка 0 - заголовок, 1..<see cref="MaxArmRows"/> - рукава.</summary>
    public void SetArmCell(int row, int col, string text, Color color)
    {
        int k = (row * ArmColumns) + col;
        if (!ReferenceEquals(_armText[k], text) && !string.Equals(_armText[k], text, StringComparison.Ordinal))
        {
            _armText[k] = text;
            _armCells[k].Text = text;
        }

        if (_armColor[k] != color)
        {
            _armColor[k] = color;
            _armCells[k].AddThemeColorOverride("font_color", color);
        }
    }

    /// <summary>Строк-рукавов в таблице; 0 - таблица скрыта.</summary>
    public void SetArmRows(int n)
    {
        n = Math.Clamp(n, 0, MaxArmRows);
        if (n == _armRows)
        {
            return;
        }

        _armRows = n;
        _armsCard.Visible = n > 0;
        for (int r = 1; r <= MaxArmRows; r++)
        {
            for (int c = 0; c < ArmColumns; c++)
            {
                _armCells[(r * ArmColumns) + c].Visible = r <= n;
            }
        }
    }

    /// <summary>Примечание под строками (пусто - скрыто): «объект исчез», «нет данных».</summary>
    public void SetNote(string text)
    {
        if (string.Equals(_noteText, text, StringComparison.Ordinal))
        {
            return;
        }

        _noteText = text;
        _note.Text = text;
        _noteCard.Visible = text.Length > 0;
    }

    /// <summary>Кнопка действия: подпись, переключатель ли и его состояние, доступность, подсказка.</summary>
    public void SetAction(int i, string text, bool toggle, bool pressed, bool enabled, string tooltip)
    {
        UiButton b = _actions[i];
        if (!string.Equals(b.Text, text, StringComparison.Ordinal))
        {
            b.Text = text;
        }

        b.ToggleMode = toggle;
        b.ThemeTypeVariation = toggle ? ThemeBuilder.Toggle : ThemeBuilder.Secondary;
        if (toggle && b.ButtonPressed != pressed)
        {
            b.SetPressedNoSignal(pressed);
        }

        b.Disabled = !enabled;
        b.WithTooltip(tooltip);
        b.Visible = true;
    }

    /// <summary>Переключатель кнопки без события (состояние сменилось извне: Esc, потеря объекта).</summary>
    public void SetActionPressed(int i, bool pressed)
    {
        if (_actions[i].ButtonPressed != pressed)
        {
            _actions[i].SetPressedNoSignal(pressed);
        }
    }

    /// <summary>Сколько кнопок действий видно; 0 - ряд и разделитель скрыты.</summary>
    public void SetActionCount(int n)
    {
        for (int i = n; i < MaxActions; i++)
        {
            _actions[i].Visible = false;
        }

        _actionsRow.Visible = n > 0;
        _actionsSep.Visible = n > 0;
    }
}
