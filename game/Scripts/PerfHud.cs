using System;
using System.Globalization;
using System.Text;
using Godot;

namespace TrafficCity;

/// <summary>Перф-HUD (F3): FPS, frame time, tick ms p50/p95, агенты, аллокации на тик, GC.</summary>
public partial class PerfHud : Label
{
    private const int WindowSize = 256;

    private readonly float[] _tickMs = new float[WindowSize];
    private readonly float[] _sorted = new float[WindowSize];
    private readonly StringBuilder _sb = new();
    private int _tickCount;
    private int _tickHead;
    private long _allocBytes;
    private long _allocTicks;
    private double _refresh;

    /// <summary>Число активных агентов (заполняет SimHost).</summary>
    public int Agents { get; set; }

    /// <summary>Свободный текст (пауза, скорость, время симуляции...).</summary>
    public string Extra { get; set; } = string.Empty;

    public override void _Ready()
    {
        Visible = false;
        Position = new Vector2(8, 8);
        AddThemeColorOverride("font_color", new Color(0.9f, 1f, 0.9f));
        AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
        AddThemeConstantOverride("shadow_offset_x", 1);
        AddThemeConstantOverride("shadow_offset_y", 1);
    }

    /// <summary>Записать один вызов Step: длительность (мс) и аллоцированные за него байты.</summary>
    public void RecordTick(double ms, long allocatedBytes, int ticks)
    {
        _tickMs[_tickHead] = (float)ms;
        _tickHead = (_tickHead + 1) % WindowSize;
        if (_tickCount < WindowSize)
        {
            _tickCount++;
        }

        _allocBytes += allocatedBytes;
        _allocTicks += ticks;
    }

    public override void _Process(double delta)
    {
        if (!Visible)
        {
            return;
        }

        _refresh -= delta;
        if (_refresh > 0)
        {
            return;
        }

        _refresh = 0.25;
        float p50 = 0;
        float p95 = 0;
        if (_tickCount > 0)
        {
            Array.Copy(_tickMs, _sorted, _tickCount);
            Array.Sort(_sorted, 0, _tickCount);
            p50 = _sorted[Math.Min(_tickCount - 1, (int)(_tickCount * 0.50))];
            p95 = _sorted[Math.Min(_tickCount - 1, (int)(_tickCount * 0.95))];
        }

        double bytesPerTick = _allocTicks > 0 ? (double)_allocBytes / _allocTicks : 0;
        double fps = Engine.GetFramesPerSecond();
        double frameMs = fps > 0 ? 1000.0 / fps : 0;
        CultureInfo c = CultureInfo.InvariantCulture;
        _sb.Clear();
        _sb.Append(c, $"FPS {fps:0}   frame {frameMs:0.00} ms\n");
        _sb.Append(c, $"tick ms  p50 {p50:0.000}   p95 {p95:0.000}   (n={_tickCount})\n");
        _sb.Append(c, $"agents {Agents}\n");
        _sb.Append(c, $"alloc/tick {bytesPerTick:0.0} B\n");
        _sb.Append(c, $"GC gen0 {GC.CollectionCount(0)}  gen1 {GC.CollectionCount(1)}  gen2 {GC.CollectionCount(2)}");
        if (Extra.Length > 0)
        {
            _sb.Append('\n').Append(Extra);
        }

        Text = _sb.ToString();
    }
}
