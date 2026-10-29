using System;
using System.Globalization;
using System.Text;
using Godot;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>
/// Панель «Город» (P16, UI16; progression.md §2.1, §3): показатели K1–K6 плитками, график по часам (24 ч, ряд на выбор) и баланс казны по суткам (32 сут),
/// карточка следующей вехи (население, ворота с текущими значениями, что откроет) и список вех. Живёт в правой док-панели, клавиша C, Esc закрывает.
/// Данные - <see cref="CityPanelData"/> (1 Гц); тексты пересобираются только при новой версии данных, в остальных кадрах - ни одной аллокации.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class UiCityPanel
{
    private static readonly string[] _featureNames =
    {
        "прямые дороги", "смена профиля дорог", "кривые дороги", "светофоры", "адаптивные светофоры", "запреты поворотов",
        "автобусные маршруты", "выделенные полосы", "зонирование", "налоги", "плата за парковку",
    };

    private static readonly (KpiKind Kind, string Title, string Unit)[] _series =
    {
        (KpiKind.TravelMin, "Время в пути", "мин"), (KpiKind.IdlePct, "Простой машин", "%"), (KpiKind.SatisfactionPct, "Довольство", "%"), (KpiKind.BusSharePct, "Доля автобуса", "%"),
    };

    private readonly PanelContainer _panel;
    private readonly ScrollContainer _scroll;
    private readonly Label _status;
    private readonly Label[] _tileValue = new Label[6];
    private readonly Label[] _tileSub = new Label[6];
    private readonly OptionButton _seriesPick;
    private readonly UiMiniChart _hourChart = new();
    private readonly Label _hourCaption;
    private readonly UiMiniChart _dayChart = new();
    private readonly Label _dayCaption;
    private readonly Label _nextTitle;
    private readonly Label _nextBody;
    private readonly Label[] _rows = new Label[CityPanelData.MaxMilestones];
    private readonly Label _noMilestones;
    private readonly StringBuilder _sb = new(256);
    private int _shownVersion = -1;
    private int _seriesIndex;

    /// <summary>Кнопка × в заголовке.</summary>
    public event Action? CloseRequested;

    public UiCityPanel(Control dock)
    {
        _panel = new PanelContainer { Name = "CityPanel", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        dock.AddChild(_panel);
        var outer = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        outer.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(outer);

        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        outer.AddChild(head);
        head.AddChild(new Label { Text = "Город", ThemeTypeVariation = UiTheme.LabelHeader, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var close = new Button { Text = "×", ThemeTypeVariation = UiTheme.ButtonCompact, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(28f, 28f), TooltipText = "Закрыть (C или Esc)" };
        close.Pressed += () => CloseRequested?.Invoke();
        head.AddChild(close);

        var scroll = _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        _status = Secondary(string.Empty);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_status);

        var grid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        box.AddChild(grid);
        (string Title, string Tip)[] tiles =
        {
            ("Время в пути", "K1: среднее время «от двери до двери» за последние сутки, игровые минуты; ниже - время до работы"),
            ("Автобус", "K2: доля поездок на автобусе; ниже - доля пешком"),
            ("Простой машин", "K3: доля времени, когда машины стоят в пробках и на красном; ниже - застрявшие сейчас"),
            ("Довольство", "K4: доля поездок без происшествий (долгое ожидание, нет парковки, долгая дорога, отмена, ожидание автобуса)"),
            ("Казна", "K5: баланс и сглаженный итог за сутки; ниже - дней до кредитного лимита при минусе"),
            ("Жители", "K6: население города; ниже - поездок за окно показателей"),
        };
        for (int i = 0; i < tiles.Length; i++)
        {
            var tile = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass, TooltipText = tiles[i].Tip };
            tile.AddThemeConstantOverride("separation", 0);
            tile.AddChild(Secondary(tiles[i].Title));
            _tileValue[i] = new Label { Text = "—", ThemeTypeVariation = UiTheme.LabelHeader, MouseFilter = Control.MouseFilterEnum.Ignore };
            tile.AddChild(_tileValue[i]);
            _tileSub[i] = Secondary(string.Empty);
            tile.AddChild(_tileSub[i]);
            grid.AddChild(tile);
        }

        box.AddChild(new HSeparator());
        var seriesRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        seriesRow.AddThemeConstantOverride("separation", 8);
        box.AddChild(seriesRow);
        seriesRow.AddChild(Secondary("График за 24 ч:"));
        _seriesPick = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Какой показатель показать по часам; пунктир - порог следующей вехи" };
        for (int i = 0; i < _series.Length; i++)
        {
            _seriesPick.AddItem(_series[i].Title, i);
        }

        _seriesPick.ItemSelected += idx => SelectSeries((int)idx);
        seriesRow.AddChild(_seriesPick);
        _hourChart.CustomMinimumSize = new Vector2(0f, 64f);
        _hourChart.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddChild(_hourChart);
        _hourCaption = Secondary(string.Empty);
        _hourCaption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_hourCaption);

        _dayChart.CustomMinimumSize = new Vector2(0f, 56f);
        _dayChart.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddChild(_dayChart);
        _dayCaption = Secondary(string.Empty);
        _dayCaption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_dayCaption);

        box.AddChild(new HSeparator());
        _nextTitle = new Label { Text = "Следующая веха", MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_nextTitle);
        _nextBody = Secondary(string.Empty);
        _nextBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_nextBody);

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "Вехи", MouseFilter = Control.MouseFilterEnum.Ignore });
        _noMilestones = Secondary("В этом сценарии вех нет: всё открыто с начала.");
        _noMilestones.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_noMilestones);
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new Label { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            box.AddChild(_rows[i]);
        }
    }

    public bool Visible
    {
        get => _panel.Visible;
        set => _panel.Visible = value;
    }

    /// <summary>Прокрутить к списку вех (скриншоты <c>--ui-city-bottom</c>).</summary>
    public void ScrollToEnd() => _scroll.ScrollVertical = 100_000;

    /// <summary>Каждый кадр, пока панель видна: пересобрать тексты, только если пришла новая версия данных.</summary>
    public void Update(CityPanelData d, int dayTicks)
    {
        if (!_panel.Visible || d.Version == _shownVersion)
        {
            return;
        }

        _shownVersion = d.Version;
        FillTiles(d);
        FillHourChart(d, dayTicks);
        FillDayChart(d);
        FillMilestones(d);
    }

    private void SelectSeries(int idx)
    {
        _seriesIndex = idx;
        _shownVersion = -1; // перерисовать при следующем кадре
    }

    private void FillTiles(CityPanelData d)
    {
        ref readonly CityKpiInfo k = ref d.Kpi;
        if (!d.HasKpi)
        {
            _status.Text = "Показателей нет: сценарий без жителей.";
            return;
        }

        _status.Text = k.HoursFilled < 24
            ? string.Create(CultureInfo.InvariantCulture, $"Собираем данные: {k.HoursFilled} из 24 игровых часов. Пока числа - по неполному окну.")
            : "Окно показателей - последние 24 игровых часа.";

        Set(0, MinText(k.TravelMinX10), k.WorkMinX10 >= 0 ? "до работы " + MinText(k.WorkMinX10) : "до работы —");
        Set(1, PctText(k.BusSharePermille), "пешком " + PctText(k.WalkSharePermille));
        Set(2, PctText(k.IdlePermille), k.Gridlock != 0 ? "КЛИН! застряло " + k.StuckNow.ToString(CultureInfo.InvariantCulture) : "застряло " + k.StuckNow.ToString(CultureInfo.InvariantCulture));
        _tileValue[2].AddThemeColorOverride("font_color", k.Gridlock != 0 ? UiPalette.Error : UiPalette.Text);
        int inc = k.IncLongWait + k.IncNoParking + k.IncLongCommute + k.IncTourCancelled + k.IncLongStopWait;
        Set(3, PctText(k.SatisfactionPermille), "происшествий " + inc.ToString(CultureInfo.InvariantCulture));
        _tileSub[3].TooltipText = string.Create(CultureInfo.InvariantCulture, $"Долгое ожидание: {k.IncLongWait}\nНет парковки: {k.IncNoParking}\nДолгая дорога на работу: {k.IncLongCommute}\nОтменённые поездки: {k.IncTourCancelled}\nДолгое ожидание автобуса: {k.IncLongStopWait}");
        _tileSub[3].MouseFilter = Control.MouseFilterEnum.Pass;
        if (k.HasBudget != 0)
        {
            string trend = (k.TrendPerDay >= 0 ? "+" : "−") + UiTopBar.Short(Math.Abs(k.TrendPerDay)) + "/д";
            Set(4, "$ " + UiTopBar.Group(k.Balance), k.DaysToLimit >= 0 ? trend + ", до лимита " + k.DaysToLimit.ToString(CultureInfo.InvariantCulture) + " д" : trend);
            _tileValue[4].AddThemeColorOverride("font_color", k.Balance < 0 ? UiPalette.Error : UiPalette.Text);
            _tileSub[4].AddThemeColorOverride("font_color", k.DaysToLimit >= 0 ? UiPalette.Error : UiPalette.TextSecondary);
        }
        else
        {
            Set(4, "—", "казны в сценарии нет");
        }

        Set(5, UiTopBar.Group(k.Population), "поездок " + UiTopBar.Group(k.LegsAll));
    }

    private void Set(int i, string value, string sub)
    {
        _tileValue[i].Text = value;
        _tileSub[i].Text = sub;
    }

    private void FillHourChart(CityPanelData d, int dayTicks)
    {
        (KpiKind kind, string title, string unit) = _series[_seriesIndex];
        _hourChart.Begin(CityPanelData.MaxHours);
        int last = -1;
        for (int i = 0; i < d.HourCount; i++)
        {
            int v = CityPanelData.HourValue(in d.Hours[i], kind, dayTicks);
            _hourChart.Add(v / 10.0, v >= 0);
            if (v >= 0)
            {
                last = v;
            }
        }

        _hourChart.SetValues(true, GateRef(d, kind));
        _hourCaption.Text = d.HourCount == 0
            ? title + " по часам: ещё нет ни одного полного часа"
            : string.Create(CultureInfo.InvariantCulture, $"{title} по часам, {unit}: последние {d.HourCount} ч, сейчас {(last >= 0 ? (last / 10.0).ToString("0.#", CultureInfo.InvariantCulture) : "—")}");
    }

    /// <summary>Порог ворот следующей вехи по этому показателю - пунктиром на графике; NaN, если ворот нет.</summary>
    private static double GateRef(CityPanelData d, KpiKind kind)
    {
        if (!d.HasProgression || d.Progress.Reached >= d.MilestoneCount)
        {
            return double.NaN;
        }

        ref readonly MilestoneDesc m = ref d.Milestones[d.Progress.Reached].Desc;
        return m.G0.Kpi == kind ? m.G0.Value / 10.0 : m.G1.Kpi == kind ? m.G1.Value / 10.0 : double.NaN;
    }

    private void FillDayChart(CityPanelData d)
    {
        bool budget = d.HasKpi && d.Kpi.HasBudget != 0;
        _dayChart.Visible = budget;
        _dayCaption.Visible = budget;
        if (!budget)
        {
            return;
        }

        _dayChart.Begin(CityPanelData.MaxDays);
        for (int i = 0; i < d.DayCount; i++)
        {
            _dayChart.Add(d.Days[i], true);
        }

        _dayChart.SetValues(true, double.NaN);
        _dayCaption.Text = d.DayCount == 0
            ? "Казна по суткам: первые сутки ещё идут"
            : string.Create(CultureInfo.InvariantCulture, $"Казна на конец суток, последние {d.DayCount} сут");
    }

    private void FillMilestones(CityPanelData d)
    {
        bool any = d.HasProgression && d.MilestoneCount > 0;
        _noMilestones.Visible = !any;
        _nextTitle.Visible = any;
        _nextBody.Visible = any;
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i].Visible = any && i < d.MilestoneCount;
        }

        if (!any)
        {
            return;
        }

        ref readonly ProgressionInfo p = ref d.Progress;
        int pop = d.HasKpi ? d.Kpi.Population : 0;
        if (p.Reached >= d.MilestoneCount)
        {
            _nextTitle.Text = "Все вехи взяты";
            _nextBody.Text = "Город прошёл все вехи сценария.";
        }
        else
        {
            ref readonly MilestoneDesc m = ref d.Milestones[p.Reached].Desc;
            _nextTitle.Text = "Следующая веха: " + (p.Reached + 1).ToString(CultureInfo.InvariantCulture);
            _sb.Clear();
            _sb.Append(pop >= m.Population ? "✓ " : "✗ ").Append("Жители: ").Append(UiTopBar.Group(pop)).Append(" из ").Append(UiTopBar.Group(m.Population)).Append(' ')
                .Append(m.Population > 0 ? Math.Min(100, (int)((long)pop * 100 / m.Population)) : 100).Append('%');
            AppendGate(in m.G0, p.NextGateValue0, (p.NextGateMask & 1) == 0);
            if (m.G1.Kpi != KpiKind.None)
            {
                _sb.Append('\n').Append((m.Flags & MilestoneFlags.GatesAny) != 0 ? "или" : "и");
            }

            AppendGate(in m.G1, p.NextGateValue1, (p.NextGateMask & 2) == 0);
            _sb.Append("\nОткроет: ");
            AppendUnlocks(in m);
            _nextBody.Text = _sb.ToString();
        }

        for (int i = 0; i < d.MilestoneCount; i++)
        {
            ref readonly MilestoneInfo mi = ref d.Milestones[i];
            bool done = mi.ReachedTick >= 0;
            bool next = i == p.Reached;
            _sb.Clear();
            _sb.Append(done ? "✓ " : next ? "▶ " : "○ ").Append("Веха ").Append(i + 1).Append(" — ").Append(UiTopBar.Group(mi.Desc.Population)).Append(" жит.: ");
            AppendUnlocks(in mi.Desc);
            _rows[i].Text = _sb.ToString();
            _rows[i].AddThemeColorOverride("font_color", done ? UiPalette.TextSecondary : next ? UiPalette.Text : UiPalette.TextSecondary);
        }
    }

    private void AppendGate(in KpiGate g, int value, bool ok)
    {
        if (g.Kpi == KpiKind.None)
        {
            return;
        }

        _sb.Append('\n').Append(ok ? "✓ " : "✗ ").Append(KpiName(g.Kpi)).Append(": ")
            .Append(value >= 0 ? (value / 10.0).ToString("0.#", CultureInfo.InvariantCulture) : "мало данных")
            .Append(g.IsMin != 0 ? ", нужно ≥ " : ", нужно ≤ ").Append((g.Value / 10.0).ToString("0.#", CultureInfo.InvariantCulture)).Append(' ').Append(KpiUnit(g.Kpi));
    }

    private void AppendUnlocks(in MilestoneDesc m)
    {
        int start = _sb.Length;
        for (int f = 0; f < _featureNames.Length; f++)
        {
            if ((m.Features & (1UL << f)) != 0)
            {
                _sb.Append(_sb.Length > start ? ", " : string.Empty).Append(_featureNames[f]);
            }
        }

        if (m.ProfileCount > 0)
        {
            _sb.Append(_sb.Length > start ? ", " : string.Empty).Append("профили дорог: ").Append(m.ProfileCount);
        }

        if (m.Grant > 0)
        {
            _sb.Append(_sb.Length > start ? ", " : string.Empty).Append("грант $ ").Append(UiTopBar.Group(m.Grant));
        }

        if (m.CreditDays > 0)
        {
            _sb.Append(_sb.Length > start ? ", " : string.Empty).Append("кредит +").Append(m.CreditDays).Append(" д");
        }

        if (m.Area >= 0)
        {
            _sb.Append(_sb.Length > start ? ", " : string.Empty).Append("область карты ").Append(m.Area);
        }

        if (_sb.Length == start)
        {
            _sb.Append('—');
        }
    }

    /// <summary>Название признака для подсказок замков.</summary>
    public static string FeatureName(Feature f) => (int)f < _featureNames.Length ? _featureNames[(int)f] : "?";

    private static string KpiName(KpiKind k) => k switch
    {
        KpiKind.TravelMin => "время в пути",
        KpiKind.WorkMin => "время до работы",
        KpiKind.IdlePct => "простой машин",
        KpiKind.BusSharePct => "доля автобуса",
        KpiKind.SatisfactionPct => "довольство",
        _ => "?",
    };

    private static string KpiUnit(KpiKind k) => k is KpiKind.TravelMin or KpiKind.WorkMin ? "мин" : "%";

    private static string MinText(int x10) => x10 >= 0 ? (x10 / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + " мин" : "—";

    private static string PctText(int permille) => permille >= 0 ? (permille / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + " %" : "—";

    private static Label Secondary(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
#pragma warning restore CA1001
