using System.Globalization;
using Godot;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>
/// Содержимое легенды инфо-слоя (UI7, ui-spec.md §8): шкала в текущей палитре <see cref="HeatPalette.Mode"/>, числа на подписях, сводка слоя.
/// Узлы создаются один раз; в кадре меняются только видимость и текст - и только если значение изменилось (0 аллокаций в стационаре).
/// Данные приходят от SimHost ~2 Гц, вместе с обновлением текстур heatmap (<see cref="HeatTexture"/>).
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed partial class UiLegend
{
    private const int Strips = 32;

    private readonly Ramp _ramp;
    private readonly Label[] _ticks = new Label[3];
    private readonly Label _unit;
    private readonly Label _summary;
    private readonly Label _summary2;
    private readonly Label _summary3;
    private readonly HBoxContainer _stuckRow;
    private readonly VBoxContainer _scaleBox;
    private readonly VBoxContainer _zonesBox;
    private readonly Label[] _zoneLabels = new Label[3];
    private readonly string?[] _zoneText = new string?[3];
    private int _layer = 1;
    private ColorblindMode _drawnMode = (ColorblindMode)(-1);
    private int _drawnLayer;

    // кэш последних значений: строки пересобираются только при изменении
    private int _avgPct = -1;
    private int _p95Pct = -1;
    private int _stuck = -1;
    private int _pedTotal = -1;
    private int _pedEdges = -1;
    private int _topX10 = -1;
    private int _zonesTotal = -1;

    public UiLegend(Control parent)
    {
        var box = new VBoxContainer { Name = "LegendBody", MouseFilter = Control.MouseFilterEnum.Ignore };
        parent.AddChild(box);

        _scaleBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_scaleBox);
        _unit = Label(string.Empty);
        _scaleBox.AddChild(_unit);
        _ramp = new Ramp { CustomMinimumSize = new Vector2(0f, 14f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _scaleBox.AddChild(_ramp);
        var ticks = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _scaleBox.AddChild(ticks);
        HorizontalAlignment[] al = { HorizontalAlignment.Left, HorizontalAlignment.Center, HorizontalAlignment.Right };
        for (int i = 0; i < 3; i++)
        {
            _ticks[i] = Label(string.Empty);
            _ticks[i].HorizontalAlignment = al[i];
            _ticks[i].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            ticks.AddChild(_ticks[i]);
        }

        _stuckRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _scaleBox.AddChild(_stuckRow);
        _stuckRow.AddChild(Swatch(UiPalette.Error));
        _stuckRow.AddChild(Label("машина стоит (stuck)"));

        _zonesBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_zonesBox);
        Color[] zc = { Palette.ZoneResidentialUi, Palette.ZoneCommercialUi, Palette.ZoneIndustrialUi };
        for (int i = 0; i < 3; i++)
        {
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddChild(Swatch(zc[i]));
            _zoneLabels[i] = Label(string.Empty);
            row.AddChild(_zoneLabels[i]);
            _zonesBox.AddChild(row);
        }

        _summary = Label(string.Empty);
        box.AddChild(_summary);
        _summary2 = Label(string.Empty);
        box.AddChild(_summary2);
        _summary3 = Label(string.Empty);
        box.AddChild(_summary3);
        SetLayer(1);
    }

    /// <summary>Сменился слой или палитра: перерисовать шкалу и переключить блоки. Вызывать из Refresh (не из кадра).</summary>
    public void SetLayer(int layer)
    {
        _layer = layer;
        bool zones = layer == InfoMode.LayerZones;
        _scaleBox.Visible = !zones;
        _zonesBox.Visible = zones;
        _stuckRow.Visible = layer == InfoMode.LayerTraffic;
        _avgPct = _p95Pct = _stuck = _pedTotal = _pedEdges = _topX10 = _zonesTotal = -1;
        _summary.Text = string.Empty;
        _summary2.Text = string.Empty;
        _summary3.Text = string.Empty;
        for (int i = 0; i < 3; i++)
        {
            _zoneText[i] = null;
        }

        if (layer == InfoMode.LayerTraffic)
        {
            _unit.Text = "Загрузка дороги";
            _ticks[0].Text = "0%";
            _ticks[1].Text = "50%";
            _ticks[2].Text = "100%";
        }
        else if (layer == InfoMode.LayerPeds)
        {
            _unit.Text = "Пешеходов на 20 м тротуара";
        }

        RedrawRamp();
    }

    /// <summary>Палитра heat сменилась (UI12) - шкала перерисуется по <see cref="HeatPalette.Mode"/>.</summary>
    public void RedrawRamp()
    {
        _drawnMode = HeatPalette.Mode;
        _drawnLayer = _layer;
        (Color low, Color mid, Color high) = _layer == InfoMode.LayerPeds ? Palette.HeatPed(_drawnMode) : Palette.HeatRoad(_drawnMode);
        _ramp.Set(low, mid, high);
    }

    /// <summary>Палитра сменилась без смены слоя (вызывается из Refresh).</summary>
    public void SyncPalette()
    {
        if (_drawnMode != HeatPalette.Mode || _drawnLayer != _layer)
        {
            RedrawRamp();
        }
    }

    /// <summary>Слой «Трафик»: средняя загрузка и 95-й перцентиль (0..1) по сегментам с машинами, stuck из статистики.</summary>
    public void SetTraffic(float avgLoad, float p95, int stuck)
    {
        if (_layer != InfoMode.LayerTraffic)
        {
            return;
        }

        int avg = (int)((avgLoad * 100f) + 0.5f);
        int p = (int)((p95 * 100f) + 0.5f);
        if (avg != _avgPct || p != _p95Pct)
        {
            _avgPct = avg;
            _p95Pct = p;
            _summary.Text = string.Create(CultureInfo.InvariantCulture, $"Средняя загрузка сети: {avg}%");
            _summary3.Text = string.Create(CultureInfo.InvariantCulture, $"95% сегментов не выше {p}%");
        }

        if (stuck != _stuck)
        {
            _stuck = stuck;
            _summary2.Text = string.Create(CultureInfo.InvariantCulture, $"Стоят: {stuck}");
        }
    }

    /// <summary>Слой «Пешеходы»: адаптивный верх шкалы (95-й перцентиль, пешеходов на 20 м), всего на рёбрах.</summary>
    public void SetPeds(float top, int total, int edges)
    {
        if (_layer != InfoMode.LayerPeds)
        {
            return;
        }

        int x10 = (int)((top * 10f) + 0.5f);
        if (x10 != _topX10)
        {
            _topX10 = x10;
            _ticks[0].Text = "0";
            _ticks[1].Text = Fmt(top * 0.5f);
            _ticks[2].Text = "≥" + Fmt(top);
        }

        if (total != _pedTotal || edges != _pedEdges)
        {
            _pedTotal = total;
            _pedEdges = edges;
            _summary.Text = string.Create(CultureInfo.InvariantCulture, $"Пешеходов на тротуарах: {total}");
            _summary2.Text = "Верх шкалы - 95-й перцентиль кадра";
        }
    }

    /// <summary>Слой «Зоны»: участков по видам (<c>LotChunks.ZoneCounts</c>).</summary>
    public void SetZones(int residential, int commercial, int industrial)
    {
        if (_layer != InfoMode.LayerZones)
        {
            return;
        }

        Zone(0, "Жилая", residential);
        Zone(1, "Коммерческая", commercial);
        Zone(2, "Промзона", industrial);
        int total = residential + commercial + industrial;
        if (total != _zonesTotal)
        {
            _zonesTotal = total;
            _summary.Text = string.Create(CultureInfo.InvariantCulture, $"Участков: {total}");
        }
    }

    private static string Fmt(float v) => v >= 10f ? ((int)(v + 0.5f)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);

    private static Label Label(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static ColorRect Swatch(Color c) => new()
    {
        Color = c,
        CustomMinimumSize = new Vector2(14f, 14f),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private void Zone(int i, string name, int n)
    {
        string t = string.Create(CultureInfo.InvariantCulture, $"{name}: {n}");
        if (_zoneText[i] != t)
        {
            _zoneText[i] = t;
            _zoneLabels[i].Text = t;
        }
    }

    /// <summary>Полоса градиента low-mid-high полосками (рисуется только при смене палитры).</summary>
    private sealed partial class Ramp : Control
    {
        private Color _low;
        private Color _mid;
        private Color _high;

        public void Set(Color low, Color mid, Color high)
        {
            _low = low;
            _mid = mid;
            _high = high;
            QueueRedraw();
        }

        public override void _Draw()
        {
            float w = Size.X / Strips;
            for (int i = 0; i < Strips; i++)
            {
                float t = (i + 0.5f) / Strips;
                Color c = t < 0.5f ? _low.Lerp(_mid, t * 2f) : _mid.Lerp(_high, (t - 0.5f) * 2f);
                DrawRect(new Rect2(i * w, 0f, w + 1f, Size.Y), c);
            }

            DrawRect(new Rect2(Vector2.Zero, Size), UiPalette.PanelBorder, false, 1f);
        }
    }
}
