using System;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Дороги: один ArrayMesh из плоских лент по полилиниям всех полос (строится один раз при старте).
/// Режим тепловой карты перекрашивает вершины по <c>SegmentLoads</c> и пересобирает поверхность.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлу сцены и освобождаются вместе с ним
public sealed class RoadMesh
{
    private const float LaneHalfWidth = 1.55f;
    private const float ConnectorHalfWidth = 1.1f;

    private static readonly Color _streetColor = new(0.23f, 0.25f, 0.30f);
    private static readonly Color _arterialColor = new(0.34f, 0.38f, 0.46f);
    private static readonly Color _connectorColor = new(0.17f, 0.18f, 0.22f);

    private readonly ArrayMesh _mesh = new();
    private readonly Godot.Collections.Array _arrays = new();
    private readonly Vector3[] _vertices;
    private readonly Vector3[] _normals;
    private readonly int[] _indices;
    private readonly Color[] _baseColors;
    private readonly Color[] _colors;
    private readonly int[] _laneFirstVertex;
    private readonly int[] _laneVertexCount;
    private readonly int[] _laneSegment;
    private bool _heat;

    public RoadMesh(Simulation sim)
    {
        NetworkView net = sim.Network;
        int laneCount = net.Lanes.Length;
        _laneFirstVertex = new int[laneCount];
        _laneVertexCount = new int[laneCount];
        _laneSegment = new int[laneCount];

        int vertTotal = 0;
        int quadTotal = 0;
        for (int li = 0; li < laneCount; li++)
        {
            int pc = net.Lanes[li].PointCount;
            if (pc >= 2)
            {
                vertTotal += pc * 2;
                quadTotal += pc - 1;
            }
        }

        _vertices = new Vector3[vertTotal];
        _normals = new Vector3[vertTotal];
        _baseColors = new Color[vertTotal];
        _colors = new Color[vertTotal];
        _indices = new int[quadTotal * 6];

        int v = 0;
        int ix = 0;
        for (int li = 0; li < laneCount; li++)
        {
            LaneData lane = net.Lanes[li];
            _laneSegment[li] = lane.Kind == LaneKind.Connector ? -1 : lane.Segment;
            if (lane.PointCount < 2)
            {
                continue;
            }

            bool connector = lane.Kind == LaneKind.Connector;
            bool arterial = !connector && lane.Segment >= 0 && lane.Segment < net.Segments.Length
                && net.Segments[lane.Segment].Kind == RoadKind.Arterial;
            float half = connector ? ConnectorHalfWidth : LaneHalfWidth;
            float y = connector ? 0.04f : (arterial ? 0.02f : 0.0f);
            Color col = connector ? _connectorColor : (arterial ? _arterialColor : _streetColor);

            _laneFirstVertex[li] = v;
            _laneVertexCount[li] = lane.PointCount * 2;
            int first = v;
            for (int k = 0; k < lane.PointCount; k++)
            {
                Vec2 p = net.LanePoints[lane.FirstPoint + k];
                Vec2 prev = net.LanePoints[lane.FirstPoint + Math.Max(k - 1, 0)];
                Vec2 next = net.LanePoints[lane.FirstPoint + Math.Min(k + 1, lane.PointCount - 1)];
                float dx = next.X - prev.X;
                float dz = next.Z - prev.Z;
                float len = MathF.Sqrt((dx * dx) + (dz * dz));
                if (len < 1e-4f)
                {
                    dx = 1f;
                    dz = 0f;
                    len = 1f;
                }

                float nx = -dz / len * half;
                float nz = dx / len * half;
                _vertices[v] = new Vector3(p.X + nx, y, p.Z + nz);
                _vertices[v + 1] = new Vector3(p.X - nx, y, p.Z - nz);
                _normals[v] = Vector3.Up;
                _normals[v + 1] = Vector3.Up;
                _baseColors[v] = col;
                _baseColors[v + 1] = col;
                v += 2;
            }

            for (int k = 0; k + 1 < lane.PointCount; k++)
            {
                int a = first + (k * 2);
                _indices[ix++] = a;
                _indices[ix++] = a + 1;
                _indices[ix++] = a + 2;
                _indices[ix++] = a + 1;
                _indices[ix++] = a + 3;
                _indices[ix++] = a + 2;
            }
        }

        Array.Copy(_baseColors, _colors, _colors.Length);
        _arrays.Resize((int)Mesh.ArrayType.Max);
        Rebuild();

        Instance = new MeshInstance3D
        {
            Name = "Roads",
            Mesh = _mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
    }

    public MeshInstance3D Instance { get; }

    /// <summary>Тепловая карта загрузки сегментов включена.</summary>
    public bool Heat => _heat;

    public void SetHeat(bool on, Simulation sim)
    {
        _heat = on;
        Refresh(sim);
    }

    /// <summary>Обновить цвета (при включённой тепловой карте - по SegmentLoads); вызывать нечасто (~2 Гц).</summary>
    public void Refresh(Simulation sim)
    {
        if (_heat)
        {
            ReadOnlySpan<SegmentLoad> loads = sim.SegmentLoads;
            for (int li = 0; li < _laneSegment.Length; li++)
            {
                int n = _laneVertexCount[li];
                if (n == 0)
                {
                    continue;
                }

                int seg = _laneSegment[li];
                Color col = seg >= 0 && seg < loads.Length ? HeatColor(loads[seg], _baseColors[_laneFirstVertex[li]]) : _baseColors[_laneFirstVertex[li]];
                Array.Fill(_colors, col, _laneFirstVertex[li], n);
            }
        }
        else
        {
            Array.Copy(_baseColors, _colors, _colors.Length);
        }

        Rebuild();
    }

    private static Color HeatColor(in SegmentLoad load, Color empty)
    {
        if (load.Count == 0)
        {
            return empty;
        }

        // 0 = свободно (зелёный), 1 = стоим (красный); по отношению средней скорости к лимиту
        float t = 1f - Math.Clamp(load.SpeedRatio, 0f, 1f);
        // плотность усиливает "красноту" даже при высокой скорости
        t = Math.Max(t, Math.Clamp(load.Density / 120f, 0f, 1f) * 0.6f);
        return t < 0.5f
            ? new Color(0.1f, 0.85f, 0.2f).Lerp(new Color(1f, 0.9f, 0.1f), t * 2f)
            : new Color(1f, 0.9f, 0.1f).Lerp(new Color(0.95f, 0.1f, 0.1f), (t - 0.5f) * 2f);
    }

    private void Rebuild()
    {
        _mesh.ClearSurfaces();
        if (_vertices.Length == 0)
        {
            return;
        }

        _arrays[(int)Mesh.ArrayType.Vertex] = _vertices;
        _arrays[(int)Mesh.ArrayType.Normal] = _normals;
        _arrays[(int)Mesh.ArrayType.Color] = _colors;
        _arrays[(int)Mesh.ArrayType.Index] = _indices;
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _arrays);
    }
}
