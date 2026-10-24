using System;
using System.Globalization;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Headless.Render;

/// <summary>
/// Рисует кадр симуляции по публичному API ядра (как будущий клиент): статическая сеть - один раз в фон,
/// затем на каждый кадр копия фона + машины (интерполяция Previous/Current) + светофоры + HUD.
/// </summary>
public sealed class FrameRenderer
{
    public static readonly Rgb Background = new(14, 16, 22);
    public static readonly Rgb ConnectorColor = new(24, 28, 38);
    public static readonly Rgb StreetColor = new(38, 44, 58);
    public static readonly Rgb ArterialColor = new(66, 78, 100);
    public static readonly Rgb CarColor = new(51, 221, 255);
    public static readonly Rgb BusColor = new(255, 160, 32);
    public static readonly Rgb StuckColor = new(255, 48, 48);
    public static readonly Rgb VehicleOutlineColor = new(8, 10, 14);
    public static readonly Rgb SelectedColor = new(255, 255, 255);
    public static readonly Rgb SignalRed = new(255, 50, 50);
    public static readonly Rgb SignalYellow = new(255, 220, 40);
    public static readonly Rgb SignalGreen = new(60, 230, 90);
    public static readonly Rgb HudText = new(220, 225, 235);

    private const float LaneWidthM = 3.2f;
    /// <summary>Машина меньше этого (по длине или ширине, px) рисуется как точка <see cref="MinDotPx"/>.</summary>
    private const float SmallVehicleLengthPx = 3f;
    private const float SmallVehicleWidthPx = 2f;

    /// <summary>Минимальный размер точки машины на общем плане, px (3x3 - виден поток на 3 км).</summary>
    public const float MinDotPx = 3f;

    private readonly Canvas _canvas;
    private readonly Viewport _view;
    private readonly VehicleTypeDesc[] _types;
    private readonly byte[] _background;

    public FrameRenderer(Simulation sim, VehicleTypeDesc[] types, int width, int height, float[]? view)
    {
        _canvas = new Canvas(width, height);
        _types = types;
        NetworkView net = sim.Network;
        if (net.Lanes.Length == 0)
        {
            throw new RenderException("scenario has an empty road network (nothing to draw)");
        }

        if (view != null)
        {
            _view = new Viewport(view[0], view[1], view[2], view[3], width, height);
        }
        else
        {
            _view = FitNetwork(net, width, height);
        }

        _canvas.Clear(Background);
        PaintNetwork(net);
        _background = (byte[])_canvas.Pixels.Clone();
    }

    public Canvas Canvas => _canvas;

    public Viewport View => _view;

    /// <summary>Видимая область, вписывающая всю сеть с полем 3% (метры).</summary>
    public static Viewport FitNetwork(in NetworkView net, int width, int height)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        ReadOnlySpan<Vec2> pts = net.LanePoints;
        for (int i = 0; i < pts.Length; i++)
        {
            minX = MathF.Min(minX, pts[i].X);
            maxX = MathF.Max(maxX, pts[i].X);
            minZ = MathF.Min(minZ, pts[i].Z);
            maxZ = MathF.Max(maxZ, pts[i].Z);
        }

        float w = MathF.Max(maxX - minX, 1f);
        float h = MathF.Max(maxZ - minZ, 1f);
        float mx = w * 0.03f;
        float mz = h * 0.03f;
        return new Viewport(minX - mx, minZ - mz, maxX + mx, maxZ + mz, width, height);
    }

    /// <summary>Рендерит кадр и возвращает буфер RGB24 (переиспользуется между вызовами).</summary>
    /// <param name="alpha">Доля между Previous (0) и Current (1) снапшотами.</param>
    public byte[] Render(Simulation sim, float alpha, string hud)
    {
        _canvas.CopyFrom(_background);
        DrawVehicles(sim.Vehicles, sim.PreviousVehicles, alpha);
        DrawSignals(sim);
        DrawHud(hud);
        return _canvas.Pixels;
    }

    /// <summary>Машины с линейной интерполяцией по Index+Generation; нет пары в Previous - рисуется Current.</summary>
    public void DrawVehicles(ReadOnlySpan<VehicleRenderData> cur, ReadOnlySpan<VehicleRenderData> prev, float alpha)
    {
        float a = Math.Clamp(alpha, 0f, 1f);
        float scale = _view.Scale;
        for (int i = 0; i < cur.Length; i++)
        {
            ref readonly VehicleRenderData c = ref cur[i];
            if ((c.Flags & (byte)VehicleRenderFlags.Active) == 0)
            {
                continue;
            }

            float x = c.X;
            float z = c.Z;
            float h = c.Heading;
            if (i < prev.Length)
            {
                ref readonly VehicleRenderData p = ref prev[i];
                if ((p.Flags & (byte)VehicleRenderFlags.Active) != 0 && p.Index == c.Index && p.Generation == c.Generation)
                {
                    x = p.X + ((c.X - p.X) * a);
                    z = p.Z + ((c.Z - p.Z) * a);
                    h = LerpAngle(p.Heading, c.Heading, a);
                }
            }

            VehicleTypeDesc t = c.Type < _types.Length ? _types[c.Type] : default;
            float lengthM = t.Length > 0f ? t.Length : 4.5f;
            float widthM = t.Width > 0f ? t.Width : 1.8f;
            float len = lengthM * scale;
            float wid = widthM * scale;
            if (len < SmallVehicleLengthPx || wid < SmallVehicleWidthPx)
            {
                len = MathF.Max(len, MinDotPx);
                wid = MathF.Max(wid, MinDotPx);
            }

            float sx = _view.ToScreenX(x);
            float sy = _view.ToScreenY(z);
            float dx = MathF.Cos(h);
            float dy = MathF.Sin(h);
            _canvas.FillOrientedRect(sx, sy, dx, dy, len + 2f, wid + 2f, VehicleOutlineColor);
            _canvas.FillOrientedRect(sx, sy, dx, dy, len, wid, VehicleColor(c.Flags, t.Category));
        }
    }

    public static Rgb VehicleColor(byte flags, VehicleCategory category)
    {
        if ((flags & (byte)VehicleRenderFlags.Stuck) != 0)
        {
            return StuckColor;
        }

        if ((flags & (byte)VehicleRenderFlags.Selected) != 0)
        {
            return SelectedColor;
        }

        return category == VehicleCategory.Bus ? BusColor : CarColor;
    }

    public static float LerpAngle(float from, float to, float t)
    {
        float d = to - from;
        while (d > MathF.PI)
        {
            d -= 2f * MathF.PI;
        }

        while (d < -MathF.PI)
        {
            d += 2f * MathF.PI;
        }

        return from + (d * t);
    }

    private void DrawSignals(Simulation sim)
    {
        ReadOnlySpan<byte> ls = sim.LaneSignals;
        if (ls.IsEmpty)
        {
            return;
        }

        NetworkView net = sim.Network;
        int n = Math.Min(ls.Length, net.Lanes.Length);
        for (int i = 0; i < n; i++)
        {
            if (ls[i] == (byte)LaneSignal.None)
            {
                continue;
            }

            LaneData lane = net.Lanes[i];
            if (lane.Kind == LaneKind.Connector || lane.PointCount < 2)
            {
                continue;
            }

            Vec2 p = net.LanePoints[lane.FirstPoint + lane.PointCount - 1];
            Rgb col = ls[i] switch
            {
                (byte)LaneSignal.Green => SignalGreen,
                (byte)LaneSignal.Yellow => SignalYellow,
                _ => SignalRed,
            };
            _canvas.FillCircle(_view.ToScreenX(p.X), _view.ToScreenY(p.Z), 2.5f, col);
        }
    }

    private void DrawHud(string hud)
    {
        if (hud.Length == 0)
        {
            return;
        }

        const int scale = 2;
        int w = Canvas.TextWidth(hud, scale) + 12;
        int h = (PixelFont.GlyphHeight * scale) + 10;
        _canvas.FillConvexQuad(0, 0, w, 0, w, h, 0, h, Background);
        _canvas.DrawText(6, 5, hud, HudText, scale);
    }

    public static string FormatHud(Simulation sim, double simSeconds)
    {
        sim.GetStats(out SimStats st);
        int total = (int)simSeconds;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"T {total / 3600:D2}:{total / 60 % 60:D2}:{total % 60:D2}  TICK {sim.Tick}  VEH {sim.ActiveVehicleCount}  AVG {st.AvgSpeedKmh:0.0} KMH  STUCK {st.StuckVehicles}");
    }

    private void PaintNetwork(in NetworkView net)
    {
        float scale = _view.Scale;
        float laneW = LaneWidthM * scale;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int li = 0; li < net.Lanes.Length; li++)
            {
                LaneData lane = net.Lanes[li];
                if (lane.PointCount < 2)
                {
                    continue;
                }

                bool connector = lane.Kind == LaneKind.Connector;
                bool arterial = !connector && lane.Segment >= 0 && lane.Segment < net.Segments.Length
                    && net.Segments[lane.Segment].Kind == RoadKind.Arterial;
                int lanePass = connector ? 0 : (arterial ? 2 : 1);
                if (lanePass != pass)
                {
                    continue;
                }

                Rgb col = pass == 0 ? ConnectorColor : (pass == 1 ? StreetColor : ArterialColor);
                float th = pass == 0 ? 1.2f : (pass == 1 ? MathF.Max(laneW, 1.4f) : MathF.Max(laneW, 1.8f));
                for (int k = 0; k + 1 < lane.PointCount; k++)
                {
                    Vec2 a = net.LanePoints[lane.FirstPoint + k];
                    Vec2 b = net.LanePoints[lane.FirstPoint + k + 1];
                    _canvas.FillThickLine(_view.ToScreenX(a.X), _view.ToScreenY(a.Z), _view.ToScreenX(b.X), _view.ToScreenY(b.Z), th, col);
                }
            }
        }
    }
}
