using System;
using System.Globalization;
using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>Население и казна для верхней полосы: <see cref="SimHost"/> обновляет раз в секунду; <c>Has*</c> = false - блок не рисуется (ui-spec.md §3.1).</summary>
public struct CityHudData
{
    public bool HasPopulation { get; set; }
    public int Population { get; set; }
    public bool HasBudget { get; set; }
    public long Balance { get; set; }
    public long FlowPerDay { get; set; }   // сглаженный доход минус расход за сутки
    public bool Insolvent { get; set; }    // за кредитным лимитом
}

/// <summary>
/// Верхняя полоса 40 px (P1, ui-spec.md §3.1, §9): слева деньги и население, по центру пауза/скорость/время, справа уведомления и настройки (заглушки UI6/UI12).
/// Пустые блоки не рисуются: сценарий без жителей показывает только время. Данные берёт у <see cref="SimHost"/> (тик кадра, <see cref="CityHudData"/>);
/// тексты пересобираются, только когда значение изменилось.
/// </summary>
#pragma warning disable CA1001 // Godot-узлы-поля освобождает дерево сцены
public sealed class UiTopBar
{
    private static readonly string[] _weekdays = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
    private static readonly float[] _speeds = { 1f, 2f, 4f };
    private const float TimeButtonSize = 40f;

    private readonly HBoxContainer _money = new() { MouseFilter = Control.MouseFilterEnum.Pass };
    private readonly Label _balance = Value(big: true);
    private readonly UiBadge _flow = new();
    private readonly HBoxContainer _people = new() { MouseFilter = Control.MouseFilterEnum.Pass };
    private readonly Label _population = Value(big: true);
    private readonly VSeparator _sepLeft = new();
    private readonly UiButton _pause = TimeIcon("pause", "Пауза", "Space", toggle: true);
    private readonly UiButton[] _speedButtons = new UiButton[3];
    private readonly UiBadge _speedLabel = new();
    private readonly Label _clock = Value(big: true);

    private SimHost? _host;
    private long _shownMinute = -1;
    private long _shownBalance = long.MinValue;
    private long _shownFlow = long.MinValue;
    private int _shownPop = -1;
    private bool _shownInsolvent;
    private bool _shownPaused;
    private float _shownScale = -1f;
    private bool _blinkDim;

    /// <summary>Кнопка «События» (UI6): открывает список уведомлений.</summary>
    public Button NotesButton { get; private set; } = null!;

    public Button SettingsButton { get; private set; } = null!;

    /// <summary>Собрать содержимое в <paramref name="bar"/> (пустую панель верхней полосы UI1).</summary>
    public UiTopBar(PanelContainer bar)
    {
        // край - центр - край в одной строке: блоки не наезжают друг на друга при любом масштабе шрифта (центр смещается, если края разной ширины)
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        // слева: деньги, население
        var left = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        left.AddThemeConstantOverride("separation", 12);
        row.AddChild(left);
        _money.AddThemeConstantOverride("separation", 8);
        _money.TooltipText = "Казна города и сглаженный итог за игровые сутки (доход минус расход). Подробности — бюджет (B), позже.";
        _money.AddChild(_balance);
        _money.AddChild(_flow);
        left.AddChild(_money);
        left.AddChild(_sepLeft);
        _people.TooltipText = "Жители города";
        _people.AddChild(new Label { Text = "Жители", ThemeTypeVariation = UiTheme.LabelSecondary, MouseFilter = Control.MouseFilterEnum.Ignore });
        _people.AddChild(_population);
        left.AddChild(_people);

        // по центру: пауза, ×1/×2/×4, множитель, время
        var center = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        center.AddThemeConstantOverride("separation", 4);
        row.AddChild(center);
        _pause.Toggled += on => _host?.SetPaused(on);
        center.AddChild(_pause);
        string[] keys = { "1", "2", "3" };
        for (int i = 0; i < _speeds.Length; i++)
        {
            float s = _speeds[i];
            UiButton b = new UiButton("×" + s.ToString("0", CultureInfo.InvariantCulture), UiButtonKind.Toggle)
            {
                FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(TimeButtonSize, TimeButtonSize),
            }.WithTooltip("Скорость ×" + s.ToString("0", CultureInfo.InvariantCulture) + " (вне инфо-режима; +/− меняют скорость вдвое)", keys[i]);
            b.Pressed += () => _host?.SetSpeed(s);
            center.AddChild(b);
            _speedButtons[i] = b;
        }

        _speedLabel.TooltipText = "Скорость вне ×1/×2/×4 (клавиши +/−)";
        _speedLabel.Visible = false;
        _speedLabel.Set(string.Empty, UiTone.Info);
        center.AddChild(_speedLabel);
        _clock.CustomMinimumSize = new Vector2(140f, 0f);
        _clock.HorizontalAlignment = HorizontalAlignment.Center;
        center.AddChild(_clock);

        // справа: заглушки (UI6 - уведомления, UI12 - настройки)
        var right = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Alignment = BoxContainer.AlignmentMode.End };
        right.AddThemeConstantOverride("separation", 4);
        row.AddChild(right);
        var notes = new UiButton("События")
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0f, TimeButtonSize),
        }.WithTooltip("Список последних уведомлений");
        right.AddChild(notes);
        NotesButton = notes;
        UiButton settings = TimeIcon("settings", "Настройки", "F10");
        SettingsButton = settings;
        right.AddChild(settings);

        _money.Visible = false;
        _sepLeft.Visible = false;
        _people.Visible = false;
    }

    /// <summary>Каждый кадр: подхватить хост, обновить изменившееся.</summary>
    public void Update(SimHost? host)
    {
        if (_host == null)
        {
            _host = host;
            if (_host == null)
            {
                return;
            }
        }

        SimHost h = _host;
        UpdateTime(h);
        UpdateCity(h);
    }

    private void UpdateTime(SimHost h)
    {
        bool paused = h.TimePaused;
        float scale = h.TimeScale;
        if (paused != _shownPaused || scale != _shownScale)
        {
            _shownPaused = paused;
            _shownScale = scale;
            _pause.SetPressedNoSignal(paused);
            for (int i = 0; i < _speeds.Length; i++)
            {
                _speedButtons[i].SetPressedNoSignal(!paused && scale == _speeds[i]);
            }

            // кнопки ×1/×2/×4 и «пауза» подсвечены сами; число нужно только для нестандартной скорости (клавиши +/-)
            bool preset = Array.IndexOf(_speeds, scale) >= 0;
            _speedLabel.Visible = !preset;
            _speedLabel.Set("×" + scale.ToString("0.###", CultureInfo.InvariantCulture), UiTone.Info);
        }

        long tick = h.SimTick;
        int day = h.DayTicks;
        long minute = day > 0 ? tick * 1440L / day : tick / 20L; // календарь - минуты суток, без календаря - секунды игры
        if (minute != _shownMinute)
        {
            _shownMinute = minute;
            if (day > 0)
            {
                long dayIndex = tick / day;
                long m = minute % 1440L;
                _clock.Text = string.Create(CultureInfo.InvariantCulture, $"{_weekdays[dayIndex % 7]} {m / 60:00}:{m % 60:00}, день {dayIndex + 1}");
            }
            else
            {
                _clock.Text = string.Create(CultureInfo.InvariantCulture, $"{minute / 3600:00}:{minute / 60 % 60:00}:{minute % 60:00}");
                _clock.TooltipText = "Игровое время с начала сценария (календаря нет)";
            }
        }

        // на паузе время мигает раз в 2 с (ui-spec.md §9)
        bool dim = paused && Time.GetTicksMsec() % 2000UL >= 1000UL;
        if (dim != _blinkDim)
        {
            _blinkDim = dim;
            _clock.Modulate = new Color(1f, 1f, 1f, dim ? 0.35f : 1f);
            _speedLabel.Modulate = _clock.Modulate;
        }
    }

    private void UpdateCity(SimHost h)
    {
        CityHudData d = h.CityHud;
        if (d.HasBudget != _money.Visible || d.HasPopulation != _people.Visible)
        {
            _money.Visible = d.HasBudget;
            _people.Visible = d.HasPopulation;
            _sepLeft.Visible = d.HasBudget && d.HasPopulation;
        }

        if (d.HasBudget && (d.Balance != _shownBalance || d.FlowPerDay != _shownFlow || d.Insolvent != _shownInsolvent))
        {
            _shownBalance = d.Balance;
            _shownFlow = d.FlowPerDay;
            _shownInsolvent = d.Insolvent;
            _balance.Text = "$\u00A0" + Group(d.Balance);
            string sign = d.FlowPerDay >= 0 ? "+" : "−";
            _flow.Set((d.FlowPerDay >= 0 ? "▲ " : "▼ ") + sign + Short(Math.Abs(d.FlowPerDay)) + "/д", d.FlowPerDay >= 0 ? UiTone.Neutral : UiTone.Danger);
            _balance.AddThemeColorOverride("font_color", d.Balance < 0 || d.Insolvent ? UiPalette.Error : UiPalette.Text);
            _balance.TooltipText = d.Insolvent ? "Казна за кредитным лимитом" : string.Empty;
        }

        if (d.HasPopulation && d.Population != _shownPop)
        {
            _shownPop = d.Population;
            _population.Text = Group(d.Population);
        }
    }

    /// <summary>Тысячи через пробел: 1 240 500.</summary>
    internal static string Group(long v)
    {
        string s = Math.Abs(v).ToString(CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder(s.Length + s.Length / 3 + 1);
        if (v < 0)
        {
            sb.Append('−');
        }

        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && (s.Length - i) % 3 == 0)
            {
                sb.Append(' '); // неразрывный пробел
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }

    /// <summary>Короткая запись потока: 950, 3.2k, 1.4M.</summary>
    internal static string Short(long v) => v switch
    {
        >= 1_000_000 => (v / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (v / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        >= 1000 => (v / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + "k",
        _ => v.ToString(CultureInfo.InvariantCulture),
    };

    private static Label Value(bool big = false) => new()
    {
        ThemeTypeVariation = big ? ThemeBuilder.LabelValue : string.Empty,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Pass,
    };

    /// <summary>Квадратная кнопка-пиктограмма верхней полосы (меньше инструментов тулбара). Space/Enter не должны её «нажимать»: действия времени приходят из InputMap.</summary>
    private static UiButton TimeIcon(string glyph, string tooltip, string hint, bool toggle = false)
    {
        UiButton b = UiButton.ForIcon(glyph, tooltip, hint, toggle);
        b.FocusMode = Control.FocusModeEnum.None;
        b.CustomMinimumSize = new Vector2(TimeButtonSize, TimeButtonSize);
        return b;
    }
}
#pragma warning restore CA1001
