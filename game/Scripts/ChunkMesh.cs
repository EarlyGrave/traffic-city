using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Сборка статического меша чанка (docs/design/render.md §5.2): вершины в мировых координатах, цвет, <c>CUSTOM0</c> (индекс данных
/// для шейдера heatmap: у дорог <c>x</c> = сегмент, у тротуаров <c>x</c> = ребро, <c>y</c> = 1 у переходов). Меши собираются один раз
/// при старте и при изменении сети (инструменты игрока, ADR-0023); в кадре без изменений сети <c>AddSurfaceFromArrays</c> не вызывается.
/// </summary>
public sealed class MeshBuilder
{
    private readonly List<Vector3> _v = new();
    private readonly List<Color> _c = new();
    private readonly List<float> _custom = new();
    private readonly List<int> _i = new();
    private readonly List<Vector2> _uv = new();
    private readonly List<Vector3> _n = new();
    private readonly int _stride;
    private readonly bool _useUv;
    private readonly bool _useNormal;

    /// <summary><paramref name="customStride"/>: 1, 2 или 4 float в <c>CUSTOM0</c>; <paramref name="useUv"/> - писать <c>UV</c> (дороги: поперечная координата полос).</summary>
    public MeshBuilder(int customStride, bool useUv = false, bool useNormal = false)
    {
        _stride = customStride;
        _useUv = useUv;
        _useNormal = useNormal;
    }

    public int VertexCount => _v.Count;

    public bool IsEmpty => _i.Count == 0;

    public int Add(Vector3 p, Color c, float c0, float c1 = 0f, float c2 = 0f, float c3 = 0f, Vector2 uv = default)
    {
        _v.Add(p);
        _c.Add(c);
        _custom.Add(c0);
        if (_stride > 1)
        {
            _custom.Add(c1);
        }

        if (_stride > 2)
        {
            _custom.Add(c2);
            _custom.Add(c3);
        }

        if (_useUv)
        {
            _uv.Add(uv);
        }

        return _v.Count - 1;
    }

    /// <summary>Вершина с нормалью (только для построителя с <c>useNormal</c>: здания, VS5).</summary>
    public int AddLit(Vector3 p, Vector3 n, Color c, Vector2 uv)
    {
        int i = Add(p, c, 0f, 0f, 0f, 0f, uv);
        _n.Add(n);
        return i;
    }

    /// <summary>Единичная нормаль <c>(-dz, dx)</c> к полилинии в точке <paramref name="k"/> (по соседям k-1, k+1) - общая для лент и площадок перекрёстков.</summary>
    public static void EdgeNormal(ReadOnlySpan<Vec2> pts, int k, out float ox, out float oz)
    {
        Vec2 pr = pts[Math.Max(k - 1, 0)];
        Vec2 nx = pts[Math.Min(k + 1, pts.Length - 1)];
        float dx = nx.X - pr.X;
        float dz = nx.Z - pr.Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < 1e-4f)
        {
            dx = 1f;
            dz = 0f;
            len = 1f;
        }

        ox = -dz / len;
        oz = dx / len;
    }

    public void Tri(int a, int b, int c)
    {
        _i.Add(a);
        _i.Add(b);
        _i.Add(c);
    }

    public void Quad(int a, int b, int c, int d)
    {
        Tri(a, b, c);
        Tri(b, d, c);
    }

    /// <summary>Лента по полилинии с левой/правой кромкой на <paramref name="half"/> от оси, смещённой на <paramref name="shift"/> вбок.</summary>
    public void Ribbon(ReadOnlySpan<Vec2> pts, float half, float shift, float y, Color col, float c0, float c1 = 0f)
    {
        if (pts.Length < 2)
        {
            return;
        }

        int first = _v.Count;
        for (int k = 0; k < pts.Length; k++)
        {
            Vec2 p = pts[k];
            EdgeNormal(pts, k, out float ox, out float oz);
            float cx = p.X + (ox * shift);
            float cz = p.Z + (oz * shift);
            Add(new Vector3(cx + (ox * half), y, cz + (oz * half)), col, c0, c1);
            Add(new Vector3(cx - (ox * half), y, cz - (oz * half)), col, c0, c1);
        }

        for (int k = 0; k + 1 < pts.Length; k++)
        {
            int a = first + (k * 2);
            Quad(a, a + 1, a + 2, a + 3);
        }
    }

    public ArrayMesh? ToMesh()
    {
        if (_i.Count == 0)
        {
            return null;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _c.ToArray();
        arrays[(int)Mesh.ArrayType.Custom0] = _custom.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _i.ToArray();
        if (_useUv)
        {
            arrays[(int)Mesh.ArrayType.TexUV] = _uv.ToArray();
        }

        if (_useNormal)
        {
            arrays[(int)Mesh.ArrayType.Normal] = _n.ToArray();
        }

        Mesh.ArrayCustomFormat fmt = _stride > 2 ? Mesh.ArrayCustomFormat.RgbaFloat : _stride > 1 ? Mesh.ArrayCustomFormat.RgFloat : Mesh.ArrayCustomFormat.RFloat;
        var flags = (Mesh.ArrayFormat)((ulong)Mesh.ArrayFormat.FormatCustom0 | ((ulong)fmt << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
        ChunkStats.SurfaceBuilds++;
        return mesh;
    }
}

/// <summary>Счётчики сборки чанков (приёмка V4: после старта в кадрах без изменений сети <see cref="SurfaceBuilds"/> не растёт).</summary>
public static class ChunkStats
{
    public static int SurfaceBuilds { get; internal set; }

    public static int Chunks { get; internal set; }
}

/// <summary>Сетка чанков и вспомогательные функции полилиний.</summary>
public static class ChunkGrid
{
    public static long Key(float x, float z, float cell)
    {
        int cx = (int)MathF.Floor(x / cell);
        int cz = (int)MathF.Floor(z / cell);
        return ((long)cx << 32) ^ (uint)cz;
    }

    public static MeshBuilder Get(Dictionary<long, MeshBuilder> map, long key, int stride, bool uv = false, bool normal = false)
    {
        if (!map.TryGetValue(key, out MeshBuilder? b))
        {
            b = new MeshBuilder(stride, uv, normal);
            map[key] = b;
        }

        return b;
    }

    /// <summary>
    /// Добавить меши чанков как дочерние узлы с <c>visibility_range</c> (без fade: он уводит объект в прозрачный проход).
    /// <paramref name="end"/> = 0 - без верхней границы. <c>visibility_range_*_margin</c> = 0: при fade Disabled ненулевой margin (hysteresis) выключает узел
    /// по обе стороны порога (мёртвая зона ~2*margin), и стыкующиеся LOD (Road0/Road1) оставляли дыры в асфальте (lessons.md, агент L34).
    /// </summary>
    public static void Emit(Node3D parent, Dictionary<long, MeshBuilder> map, string prefix, Material material, float begin, float end)
    {
        foreach (KeyValuePair<long, MeshBuilder> kv in map)
        {
            ArrayMesh? mesh = kv.Value.ToMesh();
            if (mesh == null)
            {
                continue;
            }

            int cx = (int)(kv.Key >> 32);
            int cz = (int)(uint)(kv.Key & 0xffffffffL);
            var node = new MeshInstance3D
            {
                Name = $"{prefix}_{cx}_{cz}",
                Mesh = mesh,
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeBegin = begin,
                VisibilityRangeBeginMargin = 0f,
                VisibilityRangeEnd = end,
                VisibilityRangeEndMargin = 0f,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
            };
            parent.AddChild(node);
            ChunkStats.Chunks++;
        }
    }

    /// <summary>Douglas-Peucker: индексы точек, оставшихся после упрощения (всегда первая и последняя).</summary>
    public static void Simplify(ReadOnlySpan<Vec2> pts, float tol, List<int> keep)
    {
        keep.Clear();
        keep.Add(0);
        if (pts.Length > 2)
        {
            SimplifyRange(pts, 0, pts.Length - 1, tol * tol, keep);
        }

        if (pts.Length > 1)
        {
            keep.Add(pts.Length - 1);
        }
    }

    private static void SimplifyRange(ReadOnlySpan<Vec2> pts, int a, int b, float tol2, List<int> keep)
    {
        float ax = pts[a].X;
        float az = pts[a].Z;
        float dx = pts[b].X - ax;
        float dz = pts[b].Z - az;
        float l2 = (dx * dx) + (dz * dz);
        int far = -1;
        float best = tol2;
        for (int k = a + 1; k < b; k++)
        {
            float px = pts[k].X - ax;
            float pz = pts[k].Z - az;
            float t = l2 > 1e-9f ? Math.Clamp(((px * dx) + (pz * dz)) / l2, 0f, 1f) : 0f;
            float ex = px - (dx * t);
            float ez = pz - (dz * t);
            float d2 = (ex * ex) + (ez * ez);
            if (d2 > best)
            {
                best = d2;
                far = k;
            }
        }

        if (far < 0)
        {
            return;
        }

        SimplifyRange(pts, a, far, tol2, keep);
        keep.Add(far);
        SimplifyRange(pts, far, b, tol2, keep);
    }
}
