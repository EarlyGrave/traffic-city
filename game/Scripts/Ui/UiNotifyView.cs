using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>
/// Уведомления (UI6, ui-spec.md §6.4): тосты справа снизу (максимум 3, гаснут через 6 с, не чаще 1 в 3 с), список последних по кнопке «События»
/// в верхней полосе, «Показать» подлетает камерой к объекту. Логика (кольцо, агрегация, порог) - в <see cref="UiNotifyCenter"/>, тексты - в <see cref="UiNotifyMap"/>.
/// События приходят из <see cref="Source"/> (<see cref="IUiEventSource"/>), либо напрямую через <see cref="Center"/>.Push.
/// </summary>
#pragma warning disable CA1001 // Godot-узлы-поля освобождает дерево сцены
public sealed class UiNotifyView
{
    private sealed class Toast
    {
        public PanelContainer Panel = null!;
        public Label Text = null!;
        public NotifyEntry Entry = null!;
        public double Age;
    }

    private readonly UiRoot _ui;
    private readonly VBoxContainer _toastBox;
    private readonly Button _notesButton;
    private readonly PanelContainer _listPanel;
    private readonly VBoxContainer _rows;
    private readonly List<Toast> _toasts = new();
    private readonly List<SimEvent> _drain = new();
    private bool _listDirty;
    private int _shownBadge = -1;

    public UiNotifyView(UiRoot ui, VBoxContainer toastBox, Button notesButton, Control listParent)
    {
        _ui = ui;
        _toastBox = toastBox;
        _notesButton = notesButton;
        Center.Added += _ => _listDirty = true;
        Center.Updated += OnUpdated;
        _toastBox.GrowVertical = Control.GrowDirection.Begin;
        _toastBox.Alignment = BoxContainer.AlignmentMode.End;
        _toastBox.AddThemeConstantOverride("separation", 6);

        _listPanel = new PanelContainer { Name = "NotifyList", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _listPanel.AnchorLeft = 1f;
        _listPanel.AnchorRight = 1f;
        _listPanel.OffsetLeft = -(ListWidth + UiMetrics.Margin);
        _listPanel.OffsetRight = -UiMetrics.Margin;
        _listPanel.OffsetTop = UiMetrics.TopBarHeight + UiMetrics.Margin;
        _listPanel.OffsetBottom = UiMetrics.TopBarHeight + UiMetrics.Margin + 440f;
        listParent.AddChild(_listPanel);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _listPanel.AddChild(box);
        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(head);
        head.AddChild(new Label { Text = "События", ThemeTypeVariation = UiTheme.LabelHeader, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        Button close = SmallButton("×", 32f);
        close.Pressed += () => SetListOpen(false);
        head.AddChild(close);
        box.AddChild(new HSeparator());
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        _rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_rows);

        _notesButton.Pressed += () => SetListOpen(!ListOpen);
    }

    /// <summary>Ширина списка, px.</summary>
    public const float ListWidth = 380f;

    /// <summary>Сколько тостов видно одновременно (спека: 3).</summary>
    public int MaxVisible { get; set; } = 3;

    /// <summary>Сколько секунд живёт тост (спека: 6).</summary>
    public double LifeSec { get; set; } = 6.0;

    public UiNotifyCenter Center { get; } = new();

    /// <summary>Источник событий хоста; null - события только через <see cref="Center"/>.Push.</summary>
    public IUiEventSource? Source { get; set; }

    public bool ListOpen => _listPanel.Visible;

    /// <summary>Видимых тостов сейчас.</summary>
    public int VisibleToasts => _toasts.Count;

    /// <summary>Игровое время тика для списка (например «день 2 14:05»); по умолчанию - секунды игры.</summary>
    public Func<long, string> TimeText { get; set; } = tick => string.Create(CultureInfo.InvariantCulture, $"{tick / 1200:00}:{tick / 20 % 60:00}");

    public void SetListOpen(bool open)
    {
        _listPanel.Visible = open;
        _toastBox.Visible = !open; // список перекрыл бы тосты; закрыли - тосты на месте
        if (open)
        {
            Center.MarkAllRead();
            RebuildList();
        }
    }

    /// <summary>Каждый кадр: забрать события, показать тосты по порогу, состарить, обновить бейдж.</summary>
    public void Update(double delta)
    {
        if (Source != null)
        {
            _drain.Clear();
            Source.DrainEvents(_drain);
            for (int i = 0; i < _drain.Count; i++)
            {
                Center.Push(_drain[i]);
            }
        }

        double now = Time.GetTicksMsec() / 1000.0;
        while (_toasts.Count < MaxVisible && Center.TryDequeueToast(now, out NotifyEntry e))
        {
            AddToast(e);
        }

        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            Toast t = _toasts[i];
            if (!t.Panel.GetGlobalRect().HasPoint(t.Panel.GetGlobalMousePosition()))
            {
                t.Age += delta; // под курсором тост не гаснет
            }

            t.Panel.Modulate = new Color(1f, 1f, 1f, (float)Math.Clamp((LifeSec - t.Age) / 0.4, 0.0, 1.0));
            if (t.Age >= LifeSec)
            {
                RemoveToast(i);
            }
        }

        if (_listDirty && ListOpen)
        {
            _listDirty = false;
            Center.MarkAllRead();
            RebuildList();
        }

        int unread = Center.UnreadCount;
        if (unread != _shownBadge)
        {
            _shownBadge = unread;
            _notesButton.Text = unread > 0 ? "События (" + unread.ToString(CultureInfo.InvariantCulture) + ")" : "События";
        }
    }

    private void OnUpdated(NotifyEntry e)
    {
        _listDirty = true;
        foreach (Toast t in _toasts)
        {
            if (t.Entry == e)
            {
                t.Text.Text = e.Text;
                t.Age = Math.Min(t.Age, LifeSec * 0.5); // серия продолжается - тост не гаснет сразу
            }
        }
    }

    private void AddToast(NotifyEntry e)
    {
        var t = new Toast { Entry = e };
        t.Panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0f, 40f) };
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        t.Panel.AddChild(row);
        row.AddChild(Glyph(e.Severity));
        t.Text = new Label
        {
            Text = e.Text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(120f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(t.Text);
        if (!e.Subject.IsNone)
        {
            row.AddChild(ShowButton(t.Entry));
        }

        Button x = SmallButton("×", 28f);
        x.Pressed += () =>
        {
            int idx = _toasts.IndexOf(t);
            if (idx >= 0)
            {
                RemoveToast(idx);
            }
        };
        row.AddChild(x);
        _toastBox.AddChild(t.Panel);
        _toasts.Add(t);
    }

    private void RemoveToast(int i)
    {
        _toasts[i].Panel.QueueFree();
        _toasts.RemoveAt(i);
    }

    private void RebuildList()
    {
        foreach (Node c in _rows.GetChildren())
        {
            _rows.RemoveChild(c);
            c.QueueFree();
        }

        if (Center.Count == 0)
        {
            _rows.AddChild(new Label { Text = "Событий пока нет", ThemeTypeVariation = UiTheme.LabelSecondary, MouseFilter = Control.MouseFilterEnum.Ignore });
            return;
        }

        for (int i = 0; i < Center.Count; i++)
        {
            NotifyEntry e = Center.Newest(i);
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(Glyph(e.Severity));
            var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
            col.AddThemeConstantOverride("separation", 0);
            col.AddChild(new Label
            {
                Text = e.Text,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(120f, 0f),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            col.AddChild(new Label { Text = TimeText(e.LastTick), ThemeTypeVariation = UiTheme.LabelSecondary, MouseFilter = Control.MouseFilterEnum.Ignore });
            row.AddChild(col);
            if (!e.Subject.IsNone)
            {
                row.AddChild(ShowButton(e));
            }

            _rows.AddChild(row);
            _rows.AddChild(new HSeparator());
        }
    }

    private Button ShowButton(NotifyEntry e)
    {
        Button b = SmallButton("Показать", 0f);
        b.Pressed += () => Show(e.Subject);
        return b;
    }

    /// <summary>«Показать»: подлететь камерой к объекту и выделить его; без источника или при исчезнувшем объекте - ничего.</summary>
    public bool Show(NotifySubject s)
    {
        if (Source == null || s.IsNone || !Source.TryLocate(in s, out Vector3 pos, out float extent))
        {
            return false;
        }

        _ui.GetNodeOrNull<RtsCamera>("../CameraRig")?.Focus(pos, Math.Max(extent, 40f));
        Source.SelectSubject(in s);
        return true;
    }

    private static Label Glyph(NotifySeverity s)
    {
        (string text, Color col) = s switch
        {
            NotifySeverity.Error => ("✕", UiPalette.Error),
            NotifySeverity.Warning => ("!", UiPalette.Warning),
            NotifySeverity.Good => ("✓", UiPalette.Accent),
            _ => ("i", UiPalette.TextSecondary),
        };
        var l = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(18f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeColorOverride("font_color", col);
        return l;
    }

    private static Button SmallButton(string text, float minWidth) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.ButtonCompact,
        FocusMode = Control.FocusModeEnum.None,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        CustomMinimumSize = new Vector2(minWidth, 28f),
    };
}
#pragma warning restore CA1001

/// <summary>Демо-источник для <c>--ui-demo-toasts</c>: пачка синтетических событий без ядра (скриншоты, ручная проверка).</summary>
public sealed class UiDemoEventSource : IUiEventSource
{
    private bool _sent;

    public void DrainEvents(List<SimEvent> sink)
    {
        if (_sent)
        {
            return;
        }

        _sent = true;
        for (int i = 0; i < 12; i++)
        {
            sink.Add(new SimEvent { Kind = SimEventKind.GridlockOn, Tick = 100 + (i * 10), A = 40 + i, B = 900 });
        }

        sink.Add(new SimEvent { Kind = SimEventKind.CommandRejected, Tick = 300, A = (int)RejectReason.InsufficientFunds, B = 0 });
        for (int i = 0; i < 3; i++)
        {
            sink.Add(new SimEvent { Kind = SimEventKind.VehicleStuck, Tick = 320 + i, A = 10 + i, B = 1 });
        }

        sink.Add(new SimEvent { Kind = SimEventKind.ZoneLotsLost, Tick = 340, A = 5, B = 4 });
        sink.Add(new SimEvent { Kind = SimEventKind.BudgetNegative, Tick = 360, A = 2, B = -1200 });
        sink.Add(new SimEvent { Kind = SimEventKind.DrainStalled, Tick = 380, A = 1, B = 7 });
        sink.Add(new SimEvent { Kind = SimEventKind.GridlockOff, Tick = 400, A = 0, B = 900 });
        sink.Add(new SimEvent { Kind = SimEventKind.BusRouteCreated, Tick = 420, A = 1 });
        sink.Add(new SimEvent { Kind = SimEventKind.CapacityExhausted, Tick = 440, A = (int)CapacityKind.Vehicles, B = 3 });
    }

    public bool TryLocate(in NotifySubject subject, out Vector3 position, out float extent)
    {
        position = new Vector3(subject.Index * 60f, 0f, subject.Index * -40f);
        extent = 120f;
        return true;
    }

    public void SelectSubject(in NotifySubject subject)
    {
    }
}
