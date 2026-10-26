using System;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>
/// Пешеходы (docs/design/render.md §3.1, §3.3, §3.4): три батча - LOD0 (капсула, до <c>PedBounds[0]</c>), LOD1 (бокс 12 тр., до <c>PedBounds[1]</c>)
/// и точки (<see cref="PointBatch"/>, шейдер держит размер не меньше <c>point_min_px</c>, геометрия не раздувается). Позиция интерполируется
/// Previous -> Current по доле тика, пара кадров сопоставляется по (Index, Generation). Уровень хранится на слот снапшота (гистерезис 10%);
/// лишнее сверх лимита уровня уходит на уровень грубее, сверх лимита точек отбрасывается. Цвет по <see cref="PedState"/>; выбранный - белый.
/// Счётчики по состояниям считаются по нарисованным пешеходам.
/// </summary>
public sealed class PedMesh
{
    private const float Radius = 0.28f;
    private const float Height = 1.7f;
    private const byte Unset = 255;
    private const int Points = 2; // уровень точек; 0 - капсула, 1 - бокс
    private const int Culled = 3;
    private const float Hysteresis = 0.9f;

    // Плейсхолдер палитры (цвет точек и состояний решает владелец) - все цвета пешеходов здесь, больше нигде.
    private static readonly Color _walking = new(0.95f, 0.95f, 0.85f);
    private static readonly Color _atCrossing = new(1f, 0.85f, 0.2f);
    private static readonly Color _crossing = new(1f, 0.5f, 0.1f);
    private static readonly Color _atStop = new(0.8f, 0.4f, 1f);
    private static readonly Color _passenger = new(0.45f, 0.8f, 1f);
    private static readonly Color _selected = new(1f, 1f, 1f);

    private readonly LodConfig _cfg;
    private readonly BoxBatch[] _batch = new BoxBatch[3]; // LOD0, LOD1, точки
    private readonly int[] _stateCounts = new int[6];
    private readonly int[] _placed = new int[3];
    private readonly float[] _hi2 = new float[3];
    private readonly float[] _lo2 = new float[3];
    private byte[] _level = Array.Empty<byte>();

    public PedMesh(Node parent, LodConfig cfg)
    {
        _cfg = cfg;
        var lit = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
            Roughness = 0.7f,
        };
        _batch[0] = new BoxBatch("Pedestrians_lod0", new CapsuleMesh { Radius = Radius, Height = Height, RadialSegments = 6, Rings = 2 }, lit);
        _batch[1] = new BoxBatch("Pedestrians_lod1", new BoxMesh { Size = new Vector3(Radius * 2f, Height, Radius * 2f) }, lit);
        _batch[2] = PointBatch.Create("Pedestrians_points", cfg.PointMinPx);
        foreach (BoxBatch b in _batch)
        {
            parent.AddChild(b.Node);
        }
    }

    /// <summary>Показать/скрыть все батчи пешеходов.</summary>
    /// <summary>Тени пешеходов LOD0 (только high, <see cref="VisualPreset.PedestrianShadowsHigh"/>); LOD1 и точки - никогда.</summary>
    public void SetLod0Shadows(bool on) =>
        _batch[0].Node.CastShadow = on ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;

    public bool Visible
    {
        set
        {
            foreach (BoxBatch b in _batch)
            {
                b.Node.Visible = value;
            }
        }
    }

    /// <summary>Активных пешеходов в виде (по последнему <see cref="Update"/>).</summary>
    public int Active { get; private set; }

    /// <summary>Нарисовано инстансов по уровням: LOD0, LOD1, точки.</summary>
    public ReadOnlySpan<int> Placed => _placed;

    /// <summary>Пешеходов в состоянии <paramref name="state"/> среди нарисованных.</summary>
    public int CountOf(PedState state) => _stateCounts[(int)state];

    /// <summary>
    /// Перезаписать инстансы. <paramref name="cam"/> - позиция камеры; <paramref name="allRange"/> - режим agents: точки на всём виде,
    /// иначе дальше <see cref="LodConfig.PedPointRange"/> пешеходов не рисуем (там heatmap); (selIndex, selGen) - выбранный пешеход (-1 - нет).
    /// </summary>
    public void Update(Simulation sim, float alpha, Vector3 cam, bool allRange, int selIndex, ushort selGen)
    {
        ReadOnlySpan<PedestrianRenderData> cur = sim.Pedestrians;
        ReadOnlySpan<PedestrianRenderData> prev = sim.PreviousPedestrians;
        Array.Clear(_stateCounts);
        Array.Clear(_placed);
        if (_level.Length < cur.Length)
        {
            Array.Resize(ref _level, cur.Length);
            Array.Fill(_level, Unset);
        }

        for (int k = 0; k < 2; k++)
        {
            float hi = _cfg.PedBounds[k];
            float lo = hi * Hysteresis;
            _hi2[k] = hi * hi;
            _lo2[k] = lo * lo;
        }

        float far = allRange ? float.MaxValue : _cfg.PedPointRange;
        _hi2[Points] = far * far;
        _lo2[Points] = _hi2[Points];

        int active = 0;
        for (int i = 0; i < cur.Length; i++)
        {
            if ((cur[i].Flags & PedRenderFlags.Active) != 0)
            {
                active++;
            }
        }

        Active = active;
        foreach (BoxBatch b in _batch)
        {
            b.Begin(0);
        }

        float a = Math.Clamp(alpha, 0f, 1f);
        for (int i = 0; i < cur.Length; i++)
        {
            ref readonly PedestrianRenderData c = ref cur[i];
            if ((c.Flags & PedRenderFlags.Active) == 0)
            {
                _level[i] = Unset;
                continue;
            }

            float x = c.X;
            float z = c.Z;
            float heading = c.Heading;
            if (i < prev.Length)
            {
                ref readonly PedestrianRenderData p = ref prev[i];
                if ((p.Flags & PedRenderFlags.Active) != 0 && p.Index == c.Index && p.Generation == c.Generation)
                {
                    x = p.X + ((c.X - p.X) * a);
                    z = p.Z + ((c.Z - p.Z) * a);
                    heading = LerpAngle(p.Heading, c.Heading, a);
                }
            }

            float dx = x - cam.X;
            float dy = c.Y - cam.Y;
            float dz = z - cam.Z;
            float d2 = (dx * dx) + (dy * dy) + (dz * dz);
            int lv = _level[i] == Unset ? 0 : _level[i];
            while (lv < Culled && d2 > _hi2[lv])
            {
                lv++;
            }

            while (lv > 0 && d2 < _lo2[lv - 1])
            {
                lv--;
            }

            _level[i] = (byte)lv;
            bool sel = c.Index == selIndex && c.Generation == selGen;
            if (lv == Culled && !sel)
            {
                continue;
            }

            int e = Math.Min(lv, Points);
            while (e < Points && _placed[e] >= _cfg.PedCaps[e])
            {
                e++;
            }

            if (e == Points && _placed[Points] >= _cfg.PedCaps[3] && !sel)
            {
                continue;
            }

            _placed[e]++;
            if (c.State < _stateCounts.Length)
            {
                _stateCounts[c.State]++;
            }

            Color color = ColorOf(in c, sel);
            if (e == Points)
            {
                _batch[Points].Add(x, c.Y + (Height * 0.5f) + 0.05f, z, heading, color, Radius * 2f, 1f, Radius * 2f);
            }
            else
            {
                _batch[e].Add(x, c.Y + (Height * 0.5f) + 0.05f, z, heading, color);
            }
        }

        foreach (BoxBatch b in _batch)
        {
            b.End();
        }
    }

    private static Color ColorOf(in PedestrianRenderData c, bool selected)
    {
        if (selected || (c.Flags & PedRenderFlags.Selected) != 0)
        {
            return _selected;
        }

        return (PedState)c.State switch
        {
            PedState.AtCrossing => _atCrossing,
            PedState.Crossing => _crossing,
            PedState.AtStop => _atStop,
            _ => (c.Flags & PedRenderFlags.Passenger) != 0 ? _passenger : _walking,
        };
    }

    private static float LerpAngle(float from, float to, float t)
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
}
