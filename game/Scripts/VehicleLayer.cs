using System;
using System.Globalization;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>
/// Слой ТС (docs/design/render.md §3.1, §3.3, §3.4, §5.1): на тип ТС три батча LOD0/LOD1/LOD2 (меш процедурный, пока нет моделей) и один
/// батч точек на все типы. За кадр один проход по снапшоту: дистанция до камеры -> уровень (гистерезис 10%, уровень хранится на слот) ->
/// запись в буфер батча. Лимит инстансов уровня: лишние уходят на уровень грубее; переполнение уровня <c>k</c> уменьшает его порог на
/// следующий кадр (x0.9, не ниже 0.25 базового), при заполнении &lt; 0.8 лимита порог возвращается (x1.05 до базового) - без сортировки.
/// Раздувания по дистанции нет: точка держит размер шейдером.
/// </summary>
public sealed class VehicleLayer
{
    private const byte Unset = 255;
    private const int Culled = LodConfig.Levels; // 4: дальше точек
    private const float Hysteresis = 0.9f;
    private const float MinThresholdScale = 0.25f;

    private readonly LodConfig _cfg;
    private readonly VehicleTypeDesc[] _types;
    private readonly BoxBatch[] _lod;   // [тип * 3 + уровень]
    private readonly BoxBatch _points;
    private readonly float[] _len;
    private readonly float[] _wid;
    private readonly float[] _hgt;

    // пороги уровня k: переход вниз по детальности при d > hi[k], вверх при d < lo[k] (квадраты, чтобы не брать корень)
    private readonly float[] _thrScale = new float[LodConfig.Levels];
    private readonly float[] _hi2 = new float[LodConfig.Levels];
    private readonly float[] _lo2 = new float[LodConfig.Levels];
    private readonly int[] _placed = new int[LodConfig.Levels];
    private readonly int[] _desired = new int[LodConfig.Levels];
    private byte[] _level = Array.Empty<byte>();

    /// <summary>Информационный режим: stuck-красный показывается только в слое «Трафик».</summary>
    public InfoMode? Info { get; set; }

    public VehicleLayer(Node parent, VehicleTypeDesc[] types, LodConfig cfg)
    {
        _cfg = cfg;
        _types = types;
        _lod = new BoxBatch[types.Length * 3];
        _len = new float[types.Length];
        _wid = new float[types.Length];
        _hgt = new float[types.Length];
        ShaderMaterial lit = CreateMaterial();
        var flat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        for (int t = 0; t < types.Length; t++)
        {
            VehicleTypeDesc td = types[t];
            _len[t] = td.Length > 0f ? td.Length : 4.5f;
            _wid[t] = td.Width > 0f ? td.Width : 1.8f;
            _hgt[t] = td.Category == VehicleCategory.Bus ? 3.0f : 1.5f;
            string tn = t.ToString(CultureInfo.InvariantCulture);
            _lod[(t * 3) + 0] = new BoxBatch("Vehicles_" + tn + "_lod0", BuildLod0(_len[t], _hgt[t], _wid[t], td.Category == VehicleCategory.Bus), lit);
            if (_hgt[t] >= VisualPreset.ShadowMinHeightM)
            {
                _lod[t * 3].Node.CastShadow = GeometryInstance3D.ShadowCastingSetting.On; // тень - только LOD0 (render.md §6)
            }

            _lod[(t * 3) + 1] = new BoxBatch("Vehicles_" + tn + "_lod1", BoxBuilder.Box(new Vector3(_len[t], _hgt[t], _wid[t]), new Vector3(0f, _hgt[t] * 0.5f, 0f)), lit);
            _lod[(t * 3) + 2] = new BoxBatch("Vehicles_" + tn + "_lod2", new PlaneMesh { Size = new Vector2(_len[t], _wid[t]), CenterOffset = new Vector3(0f, _hgt[t] * 0.5f, 0f) }, flat);
        }

        _points = PointBatch.Create("Vehicles_points", cfg.PointMinPx);
        foreach (BoxBatch b in _lod)
        {
            parent.AddChild(b.Node);
        }

        parent.AddChild(_points.Node);
        Array.Fill(_thrScale, 1f);
    }

    /// <summary>Инстансов, записанных в кадре, по уровням: LOD0, LOD1, LOD2, точки.</summary>
    public ReadOnlySpan<int> Placed => _placed;

    /// <summary>Сколько инстансов хотело попасть на уровень до применения лимитов.</summary>
    public ReadOnlySpan<int> Desired => _desired;

    /// <summary>Машин, не записанных из-за лимита точек.</summary>
    public int DroppedByCap { get; private set; }

    /// <summary>Максимальная дистанция до камеры среди записанных инстансов (м); не больше дистанции точек.</summary>
    public float MaxDrawnDistance { get; private set; }

    /// <summary>Текущий множитель порога уровня (обратная связь лимитов).</summary>
    public ReadOnlySpan<float> ThresholdScale => _thrScale;

    public LodConfig Config => _cfg;

    public string HudText()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"veh LOD0 {_placed[0]}/{_cfg.Caps[0]}  LOD1 {_placed[1]}/{_cfg.Caps[1]}  LOD2 {_placed[2]}/{_cfg.Caps[2]}  pts {_placed[3]}/{_cfg.Caps[3]}  dropped {DroppedByCap}  [{_cfg.Preset}]");
    }

    public void Update(Simulation sim, float alpha, Vector3 cam)
    {
        ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
        ReadOnlySpan<VehicleRenderData> prev = sim.PreviousVehicles;
        int types = _types.Length;
        if (types == 0)
        {
            return;
        }

        if (_level.Length < cur.Length)
        {
            Array.Resize(ref _level, cur.Length);
            Array.Fill(_level, Unset);
        }

        for (int k = 0; k < LodConfig.Levels; k++)
        {
            float hi = _cfg.Bounds[k] * _thrScale[k];
            float lo = hi * Hysteresis;
            _hi2[k] = hi * hi;
            _lo2[k] = lo * lo;
            _placed[k] = 0;
            _desired[k] = 0;
        }

        foreach (BoxBatch b in _lod)
        {
            b.Begin(0);
        }

        _points.Begin(0);
        DroppedByCap = 0;
        float maxD2 = 0f;
        float a = Math.Clamp(alpha, 0f, 1f);
        for (int i = 0; i < cur.Length; i++)
        {
            ref readonly VehicleRenderData c = ref cur[i];
            if ((c.Flags & (byte)VehicleRenderFlags.Active) == 0)
            {
                _level[i] = Unset;
                continue;
            }

            float x = c.X;
            float z = c.Z;
            float heading = c.Heading;
            if (i < prev.Length)
            {
                ref readonly VehicleRenderData p = ref prev[i];
                if ((p.Flags & (byte)VehicleRenderFlags.Active) != 0 && p.Index == c.Index && p.Generation == c.Generation)
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

            // желаемый уровень с гистерезисом: вниз по детальности при d > порог, вверх при d < 0.9 порога предыдущего уровня
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
            if (lv == Culled)
            {
                continue;
            }

            _desired[lv]++;
            int e = lv; // лишнее переполнение уходит на уровень грубее; сверх лимита точек - отбрасываем
            while (e < LodConfig.Levels && _placed[e] >= _cfg.Caps[e])
            {
                e++;
            }

            if (e == LodConfig.Levels)
            {
                DroppedByCap++;
                continue;
            }

            _placed[e]++;
            if (d2 > maxD2)
            {
                maxD2 = d2;
            }

            int type = Math.Min((int)c.Type, types - 1);
            bool point = e == LodConfig.Levels - 1;
            Color color = VehicleColor(c.Flags, _types[type].Category, c.Index, point);
            if (point)
            {
                _points.Add(x, c.Y + (_hgt[type] * 0.5f), z, heading, color, _len[type], 1f, _wid[type]);
            }
            else
            {
                // LOD0/LOD1: низ меша на y = 0 (AO-градиент шейдера считает от земли), LOD2: плоскость поднята на высоту
                float y = e < 2 ? c.Y + 0.05f : c.Y + (_hgt[type] * 0.5f) + 0.05f;
                _lod[(type * 3) + e].Add(x, y, z, heading, color);
            }
        }

        MaxDrawnDistance = MathF.Sqrt(maxD2);
        foreach (BoxBatch b in _lod)
        {
            b.End();
        }

        _points.End();
        Feedback();
    }

    /// <summary>Обратная связь лимитов на следующий кадр (render.md §3.3).</summary>
    private void Feedback()
    {
        for (int k = 0; k < LodConfig.Levels; k++)
        {
            int cap = _cfg.Caps[k];
            if (_desired[k] > cap)
            {
                _thrScale[k] = MathF.Max(MinThresholdScale, _thrScale[k] * 0.9f);
            }
            else if (_placed[k] < 0.8f * cap && _thrScale[k] < 1f)
            {
                _thrScale[k] = MathF.Min(1f, _thrScale[k] * 1.05f);
            }
        }
    }

    /// <summary>
    /// Цвет экземпляра (Palette.cs). Точки вдали темнее карты, ближние уровни - по слоту (5 цветов легковой). Выделение и stuck
    /// (stuck - только в инфо-слое «Трафик») идут с a = 0: шейдер не тонирует их в беж (visual-style.md §7).
    /// </summary>
    private Color VehicleColor(byte flags, VehicleCategory category, int slot, bool point)
    {
        if ((flags & (byte)VehicleRenderFlags.Selected) != 0)
        {
            return Palette.SelectedOverlay;
        }

        if ((flags & (byte)VehicleRenderFlags.Stuck) != 0 && Info is { ShowStuck: true })
        {
            return Palette.StuckOverlay;
        }

        if (category == VehicleCategory.Bus)
        {
            return point ? Palette.BusPoint : Palette.Bus;
        }

        return point ? Palette.CarPoint : Palette.CarSlots[slot % Palette.CarSlots.Length];
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

    /// <summary>Материал LOD0/LOD1: <c>vehicle.gdshader</c>, числа - из <see cref="Palette"/>.</summary>
    private static ShaderMaterial CreateMaterial()
    {
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/vehicle.gdshader") };
        mat.SetShaderParameter("edge_px", Palette.VehicleEdgePx);
        mat.SetShaderParameter("edge_fade_begin", Palette.VehicleEdgeFadeBegin);
        mat.SetShaderParameter("edge_fade_end", Palette.VehicleEdgeFadeEnd);
        mat.SetShaderParameter("edge_k", Palette.VehicleEdgeK);
        mat.SetShaderParameter("wall_shade", Palette.VehicleWallShade);
        mat.SetShaderParameter("ao_min", Palette.VehicleAoMin);
        mat.SetShaderParameter("ao_height", Palette.VehicleAoHeight);
        return mat;
    }

    /// <summary>LOD0 процедурного ТС: кузов + кабина (24 треугольника, одна поверхность, низ на y = 0); автобус - кузов + крыша.</summary>
    private static ArrayMesh BuildLod0(float len, float hgt, float wid, bool bus)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (bus)
        {
            BoxBuilder.Append(st, new Vector3(len, hgt * 0.9f, wid), new Vector3(0f, hgt * 0.45f, 0f));
            BoxBuilder.Append(st, new Vector3(len * 0.3f, hgt * 0.1f, wid * 0.6f), new Vector3(0f, hgt * 0.95f, 0f));
        }
        else
        {
            BoxBuilder.Append(st, new Vector3(len, hgt * 0.55f, wid), new Vector3(0f, hgt * 0.275f, 0f));
            BoxBuilder.Append(st, new Vector3(len * 0.5f, hgt * 0.45f, wid * 0.85f), new Vector3(-len * 0.05f, hgt * 0.775f, 0f));
        }

        return st.Commit();
    }
}
