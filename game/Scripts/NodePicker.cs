using System;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;

namespace TrafficCity;

/// <summary>
/// Аналитический пик узла (render.md §9, V10): луч камеры -> плоскость земли y = 0 -> ближайший узел в радиусе по сетке 200 м.
/// Физических коллайдеров нет. Сетка строится из <see cref="NetworkView"/> и пересобирается при смене <c>Version</c> сети.
/// Подсветка выбранного узла - одно кольцо (1 draw call).
/// </summary>
public sealed partial class NodePicker : Node3D
{
    public const float CellSize = 200f;
    public const float MinRadius = 12f;

    private readonly MeshInstance3D _ring;
    private int _version = -1;
    private int _w;
    private int _h;
    private float _x0;
    private float _z0;
    private int[] _cellStart = Array.Empty<int>();
    private int[] _cellNodes = Array.Empty<int>();

    public NodePicker()
    {
        _ring = new MeshInstance3D
        {
            Name = "NodeHighlight",
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new TorusMesh { InnerRadius = 13f, OuterRadius = 15f, Rings = 24, RingSegments = 4 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.3f, 0.8f, 1f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true,
            },
        };
        AddChild(_ring);
    }

    public void ShowAt(in NodeData node)
    {
        _ring.Position = new Vector3(node.Position.X, 0.8f, node.Position.Z);
        _ring.Visible = true;
    }

    public void Clear() => _ring.Visible = false;

    /// <summary>Ближайший узел к точке земли в радиусе <paramref name="radius"/> (не меньше <see cref="MinRadius"/>); -1, если нет.</summary>
    public int Pick(Simulation sim, float x, float z, float radius)
    {
        NetworkView net = sim.Network;
        if (net.Nodes.Length == 0)
        {
            return -1;
        }

        if (net.Version != _version)
        {
            Build(net);
        }

        float r = MathF.Max(MinRadius, radius);
        int cx0 = Math.Clamp((int)MathF.Floor((x - r - _x0) / CellSize), 0, _w - 1);
        int cx1 = Math.Clamp((int)MathF.Floor((x + r - _x0) / CellSize), 0, _w - 1);
        int cz0 = Math.Clamp((int)MathF.Floor((z - r - _z0) / CellSize), 0, _h - 1);
        int cz1 = Math.Clamp((int)MathF.Floor((z + r - _z0) / CellSize), 0, _h - 1);
        int best = -1;
        float bestD = r * r;
        for (int cz = cz0; cz <= cz1; cz++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int c = (cz * _w) + cx;
                for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    int n = _cellNodes[k];
                    float dx = net.Nodes[n].Position.X - x;
                    float dz = net.Nodes[n].Position.Z - z;
                    float d = (dx * dx) + (dz * dz);
                    if (d <= bestD)
                    {
                        bestD = d;
                        best = n;
                    }
                }
            }
        }

        return best;
    }

    private void Build(NetworkView net)
    {
        _version = net.Version;
        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxZ = float.MinValue;
        for (int i = 0; i < net.Nodes.Length; i++)
        {
            if (!net.IsNodeAlive(i))
            {
                continue;
            }

            minX = MathF.Min(minX, net.Nodes[i].Position.X);
            maxX = MathF.Max(maxX, net.Nodes[i].Position.X);
            minZ = MathF.Min(minZ, net.Nodes[i].Position.Z);
            maxZ = MathF.Max(maxZ, net.Nodes[i].Position.Z);
        }

        _x0 = minX;
        _z0 = minZ;
        _w = Math.Max(1, (int)((maxX - minX) / CellSize) + 1);
        _h = Math.Max(1, (int)((maxZ - minZ) / CellSize) + 1);
        int cells = _w * _h;
        _cellStart = new int[cells + 1];
        _cellNodes = new int[net.Nodes.Length];
        int[] cellOf = new int[net.Nodes.Length]; // мёртвые слоты в сетку не попадают
        for (int i = 0; i < net.Nodes.Length; i++)
        {
            if (!net.IsNodeAlive(i))
            {
                continue;
            }

            int cx = Math.Clamp((int)((net.Nodes[i].Position.X - _x0) / CellSize), 0, _w - 1);
            int cz = Math.Clamp((int)((net.Nodes[i].Position.Z - _z0) / CellSize), 0, _h - 1);
            cellOf[i] = (cz * _w) + cx;
            _cellStart[cellOf[i] + 1]++;
        }

        for (int c = 0; c < cells; c++)
        {
            _cellStart[c + 1] += _cellStart[c];
        }

        int[] fill = new int[cells];
        for (int i = 0; i < net.Nodes.Length; i++)
        {
            if (!net.IsNodeAlive(i))
            {
                continue;
            }

            int c = cellOf[i];
            _cellNodes[_cellStart[c] + fill[c]++] = i;
        }
    }
}
