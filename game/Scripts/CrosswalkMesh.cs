using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Инфраструктура пешеходов из <c>NetworkView</c> (строится один раз): тротуары (серые ленты), зебры (белые полосы),
/// маркеры Walk/Clear/DontWalk у светофорных переходов (по <c>CrossingStates</c>), столбики ожидающих на остановках
/// (по <c>BusStopWaiting</c>) и тепловая карта <c>PedEdgeCounts</c> для дальнего плана (широкие ленты по тем же рёбрам).
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены и освобождаются вместе с ними
public sealed class CrosswalkMesh
{
    private const float HeatWidth = 14f;
    private const float MarkerSize = 0.5f;

    private static readonly Color _sidewalkColor = new(0.50f, 0.52f, 0.55f);
    private static readonly Color _zebraColor = new(0.92f, 0.92f, 0.92f);
    private static readonly Color _dontWalk = new(1f, 0.2f, 0.2f);
    private static readonly Color _walk = new(0.24f, 0.9f, 0.35f);
    private static readonly Color _clear = new(1f, 0.86f, 0.15f);
    private static readonly Color _stopQueue = new(0.8f, 0.4f, 1f);

    private readonly PedEdgeData[] _sidewalks;
    private readonly PedEdgeData[] _crossings;
    private readonly Vec2[] _points;
    private readonly float _sidewalkWidth;
    private readonly float _crosswalkWidth;
    private readonly int _armBase;
    private readonly Vec2[] _stopPos;

    private readonly ArrayMesh _heatMesh = new();
    private readonly Godot.Collections.Array _heatArrays = new();
    private readonly BoxBatch _markers;
    private readonly BoxBatch _queues;
    private Vector3[] _heatVertices = Array.Empty<Vector3>();
    private Vector3[] _heatNormals = Array.Empty<Vector3>();
    private Color[] _heatColors = Array.Empty<Color>();
    private int[] _heatIndices = Array.Empty<int>();
    private int[] _edgeFirstVertex = Array.Empty<int>();
    private int[] _edgeVertexCount = Array.Empty<int>();
    private float[] _edgeLen = Array.Empty<float>();

    public CrosswalkMesh(Simulation sim)
    {
        NetworkView net = sim.Network;
        _sidewalks = net.Sidewalks.ToArray();
        _crossings = net.Crossings.ToArray();
        _points = net.PedEdgePoints.ToArray();
        _sidewalkWidth = net.SidewalkWidthM > 0f ? net.SidewalkWidthM : 3f;
        _crosswalkWidth = net.CrosswalkWidthM > 0f ? net.CrosswalkWidthM : 3f;
        for (int i = 0; i < net.Nodes.Length; i++)
        {
            _armBase += net.Nodes[i].ArmCount;
        }

        ReadOnlySpan<BusStopData> stops = net.BusStops;
        _stopPos = new Vec2[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            _stopPos[i] = LanePointAt(net, stops[i].Lane, stops[i].S);
        }

        Sidewalks = BuildSidewalks();
        Zebras = BuildZebras();
        BuildHeat();
        Heat = new MeshInstance3D
        {
            Name = "PedHeat",
            Mesh = _heatMesh,
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        _markers = new BoxBatch("CrossSignals", new Vector3(MarkerSize, 1.8f, MarkerSize), true);
        _queues = new BoxBatch("StopQueues", new Vector3(1f, 1f, 1f), false);
    }

    public MeshInstance3D Sidewalks { get; }

    public MeshInstance3D Zebras { get; }

    public MeshInstance3D Heat { get; }

    public MultiMeshInstance3D Markers => _markers.Node;

    public MultiMeshInstance3D Queues => _queues.Node;

    /// <summary>Сумма по <c>BusStopWaiting</c> (для HUD), пересчитывается в <see cref="UpdateQueues"/>.</summary>
    public int StopWaitingTotal { get; private set; }

    /// <summary>Закрашивает ленты по <c>PedEdgeCounts</c>: плотность (пешеходов на 20 м ребра) от прозрачного до красного. Звать ~2 Гц, пока видна.</summary>
    public void RefreshHeat(Simulation sim)
    {
        ReadOnlySpan<ushort> counts = sim.PedEdgeCounts;
        for (int e = 0; e < _edgeFirstVertex.Length; e++)
        {
            int n = _edgeVertexCount[e];
            if (n == 0)
            {
                continue;
            }

            Color col = e < counts.Length && counts[e] > 0 ? HeatColor(counts[e] * 20f / MathF.Max(_edgeLen[e], 5f)) : new Color(0f, 0f, 0f, 0f);
            Array.Fill(_heatColors, col, _edgeFirstVertex[e], n);
        }

        RebuildHeat();
    }

    /// <summary>Маркеры Walk/Clear/DontWalk у светофорных переходов внутри прямоугольника вида (мировые координаты).</summary>
    public void UpdateMarkers(Simulation sim, float x0, float z0, float x1, float z1)
    {
        ReadOnlySpan<byte> states = sim.CrossingStates;
        int needed = 0;
        for (int j = 0; j < _crossings.Length; j++)
        {
            if ((_crossings[j].Flags & 1) != 0 && InView(j, x0, z0, x1, z1))
            {
                needed += 2;
            }
        }

        _markers.Begin(needed);
        for (int j = 0; j < _crossings.Length; j++)
        {
            ref readonly PedEdgeData e = ref _crossings[j];
            if ((e.Flags & 1) == 0 || !InView(j, x0, z0, x1, z1))
            {
                continue;
            }

            int x = e.Kind == PedEdgeKind.Crossing ? e.Slot : _armBase + e.Slot;
            int sig = x >= 0 && x < states.Length ? (states[x] >> 2) & 3 : 0;
            Color col = sig switch
            {
                1 => _walk,
                2 => _clear,
                _ => _dontWalk,
            };
            Vec2 a = _points[e.FirstPoint];
            Vec2 b = _points[e.FirstPoint + e.PointCount - 1];
            _markers.Add(a.X, 1.1f, a.Z, 0f, col);
            _markers.Add(b.X, 1.1f, b.Z, 0f, col);
        }

        _markers.End();
    }

    /// <summary>Кубы над остановками: размер растёт с числом ожидающих (<c>BusStopWaiting</c>).</summary>
    public void UpdateQueues(Simulation sim)
    {
        ReadOnlySpan<ushort> waiting = sim.BusStopWaiting;
        int n = Math.Min(waiting.Length, _stopPos.Length);
        int needed = 0;
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += waiting[i];
            if (waiting[i] > 0)
            {
                needed++;
            }
        }

        StopWaitingTotal = total;
        _queues.Begin(needed);
        for (int i = 0; i < n; i++)
        {
            if (waiting[i] == 0)
            {
                continue;
            }

            float s = 1.5f + MathF.Sqrt(waiting[i]);
            _queues.Add(_stopPos[i].X, (s * 0.5f) + 2f, _stopPos[i].Z, 0f, _stopQueue, s);
        }

        _queues.End();
    }

    private static Vec2 LanePointAt(NetworkView net, int lane, float s)
    {
        if ((uint)lane >= (uint)net.Lanes.Length)
        {
            return default;
        }

        LaneData ld = net.Lanes[lane];
        if (ld.PointCount == 0)
        {
            return default;
        }

        int last = ld.FirstPoint + ld.PointCount - 1;
        for (int k = ld.FirstPoint; k < last; k++)
        {
            float s0 = net.LanePointS[k];
            float s1 = net.LanePointS[k + 1];
            if (s <= s1)
            {
                float t = s1 > s0 ? Math.Clamp((s - s0) / (s1 - s0), 0f, 1f) : 0f;
                Vec2 p0 = net.LanePoints[k];
                Vec2 p1 = net.LanePoints[k + 1];
                return new Vec2(p0.X + ((p1.X - p0.X) * t), p0.Z + ((p1.Z - p0.Z) * t));
            }
        }

        return net.LanePoints[last];
    }

    private static Color HeatColor(float density)
    {
        float t = Math.Clamp(density / 4f, 0f, 1f);
        Color c = t < 0.5f
            ? new Color(0.2f, 0.7f, 1f).Lerp(new Color(1f, 0.9f, 0.1f), t * 2f)
            : new Color(1f, 0.9f, 0.1f).Lerp(new Color(0.95f, 0.1f, 0.1f), (t - 0.5f) * 2f);
        c.A = 0.9f;
        return c;
    }

    private static MeshInstance3D MakeInstance(string name, List<Vector3> verts, List<int> idx, Color color)
    {
        var mesh = new ArrayMesh();
        if (verts.Count > 0)
        {
            var normals = new Vector3[verts.Count];
            Array.Fill(normals, Vector3.Up);
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        return new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
    }

    private bool InView(int crossing, float x0, float z0, float x1, float z1)
    {
        PedEdgeData e = _crossings[crossing];
        Vec2 a = _points[e.FirstPoint];
        return a.X >= x0 && a.X <= x1 && a.Z >= z0 && a.Z <= z1;
    }

    private MeshInstance3D BuildSidewalks()
    {
        var verts = new List<Vector3>();
        var idx = new List<int>();
        for (int i = 0; i < _sidewalks.Length; i++)
        {
            AppendRibbon(verts, idx, _sidewalks[i], _sidewalkWidth * 0.5f, 0.06f);
        }

        return MakeInstance("Sidewalks", verts, idx, _sidewalkColor);
    }

    private MeshInstance3D BuildZebras()
    {
        var verts = new List<Vector3>();
        var idx = new List<int>();
        for (int j = 0; j < _crossings.Length; j++)
        {
            PedEdgeData e = _crossings[j];
            if (e.PointCount < 2)
            {
                continue;
            }

            Vec2 a = _points[e.FirstPoint];
            Vec2 b = _points[e.FirstPoint + e.PointCount - 1];
            float dx = b.X - a.X;
            float dz = b.Z - a.Z;
            float len = MathF.Sqrt((dx * dx) + (dz * dz));
            if (len < 1f)
            {
                continue;
            }

            dx /= len;
            dz /= len;
            float nx = -dz;
            float nz = dx;

            // полосы вдоль направления перехода, поперёк - шаг около 1 м по ширине зебры
            int stripes = Math.Max(2, (int)_crosswalkWidth);
            const float hw = 0.25f;
            for (int s = 0; s < stripes; s++)
            {
                float u = (-_crosswalkWidth * 0.5f) + hw + (s * (_crosswalkWidth - (2f * hw)) / (stripes - 1));
                float cx = a.X + (nx * u);
                float cz = a.Z + (nz * u);
                int v = verts.Count;
                verts.Add(new Vector3(cx - (nx * hw), 0.07f, cz - (nz * hw)));
                verts.Add(new Vector3(cx + (nx * hw), 0.07f, cz + (nz * hw)));
                verts.Add(new Vector3(cx + (dx * len) - (nx * hw), 0.07f, cz + (dz * len) - (nz * hw)));
                verts.Add(new Vector3(cx + (dx * len) + (nx * hw), 0.07f, cz + (dz * len) + (nz * hw)));
                idx.Add(v);
                idx.Add(v + 1);
                idx.Add(v + 2);
                idx.Add(v + 1);
                idx.Add(v + 3);
                idx.Add(v + 2);
            }
        }

        return MakeInstance("Crosswalks", verts, idx, _zebraColor);
    }

    private void AppendRibbon(List<Vector3> verts, List<int> idx, in PedEdgeData e, float half, float y)
    {
        if (e.PointCount < 2)
        {
            return;
        }

        int first = verts.Count;
        for (int k = 0; k < e.PointCount; k++)
        {
            Vec2 p = _points[e.FirstPoint + k];
            Vec2 pr = _points[e.FirstPoint + Math.Max(k - 1, 0)];
            Vec2 nx = _points[e.FirstPoint + Math.Min(k + 1, e.PointCount - 1)];
            float dx = nx.X - pr.X;
            float dz = nx.Z - pr.Z;
            float len = MathF.Sqrt((dx * dx) + (dz * dz));
            if (len < 1e-4f)
            {
                dx = 1f;
                dz = 0f;
                len = 1f;
            }

            float ox = -dz / len * half;
            float oz = dx / len * half;
            verts.Add(new Vector3(p.X + ox, y, p.Z + oz));
            verts.Add(new Vector3(p.X - ox, y, p.Z - oz));
        }

        for (int k = 0; k + 1 < e.PointCount; k++)
        {
            int a = first + (k * 2);
            idx.Add(a);
            idx.Add(a + 1);
            idx.Add(a + 2);
            idx.Add(a + 1);
            idx.Add(a + 3);
            idx.Add(a + 2);
        }
    }

    private void BuildHeat()
    {
        int total = _sidewalks.Length + _crossings.Length;
        _edgeFirstVertex = new int[total];
        _edgeVertexCount = new int[total];
        _edgeLen = new float[total];
        var verts = new List<Vector3>();
        var idx = new List<int>();
        for (int e = 0; e < total; e++)
        {
            PedEdgeData ed = e < _sidewalks.Length ? _sidewalks[e] : _crossings[e - _sidewalks.Length];
            _edgeFirstVertex[e] = verts.Count;
            _edgeLen[e] = ed.LenMm / 1000f;
            if (ed.PointCount < 2)
            {
                continue;
            }

            AppendRibbon(verts, idx, ed, HeatWidth * 0.5f, 0.2f);
            _edgeVertexCount[e] = verts.Count - _edgeFirstVertex[e];
        }

        _heatVertices = verts.ToArray();
        _heatIndices = idx.ToArray();
        _heatNormals = new Vector3[_heatVertices.Length];
        Array.Fill(_heatNormals, Vector3.Up);
        _heatColors = new Color[_heatVertices.Length];
        _heatArrays.Resize((int)Mesh.ArrayType.Max);
        RebuildHeat();
    }

    private void RebuildHeat()
    {
        _heatMesh.ClearSurfaces();
        if (_heatVertices.Length == 0)
        {
            return;
        }

        _heatArrays[(int)Mesh.ArrayType.Vertex] = _heatVertices;
        _heatArrays[(int)Mesh.ArrayType.Normal] = _heatNormals;
        _heatArrays[(int)Mesh.ArrayType.Color] = _heatColors;
        _heatArrays[(int)Mesh.ArrayType.Index] = _heatIndices;
        _heatMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _heatArrays);
    }
}
