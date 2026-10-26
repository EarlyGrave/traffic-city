using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Дороги чанками (docs/design/render.md §5.2, §4.3): ближние чанки 250 м - LOD0 (ленты по полосам и connector'ам, до <see cref="LodConfig.RoadLod0"/>)
/// и LOD1 (по ленте на направление сегмента + квадрат узла, до <see cref="LodConfig.RoadLod1"/>); дальние чанки 2 000 м - лента по сегменту с
/// упрощённой полилинией (клиент строит сам), видны от камеры и без верхней границы (<see cref="LodConfig.RoadFar"/> - бюджет, на общем плане s10k+ камера дальше). Штатные <c>visibility_range</c> и frustum culling движка.
/// Цвет heatmap берёт шейдер <c>road.gdshader</c> из текстуры <see cref="HeatTexture"/> по <c>CUSTOM0.x</c> (сегмент); меши не пересобираются.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены и освобождаются вместе с ними
public sealed class RoadChunks
{
    public const float NearCell = 250f;
    public const float FarCell = 2000f;

    /// <summary>
    /// Полудиагональ ближней ячейки: <c>visibility_range</c> считается до центра AABB чанка, а пороги LOD - до самой дороги (по ближнему краю чанка),
    /// поэтому к порогу добавляется полудиагональ; иначе у края чанка под камерой включался бы LOD1.
    /// </summary>
    public const float NearHalfDiag = 177f;

    /// <summary>
    /// Запас перекрытия ближнего LOD1 и дальней ленты: центр дальнего чанка (2 000 м) может быть дальше центра вложенного ближнего на ≈1 237 м
    /// (полудиагональ 1 414 - 177), поэтому ближний слой держится до <c>begin дальнего + FarOverlap</c> - иначе у края дальнего чанка появляются дыры.
    /// </summary>
    public const float FarOverlap = 1240f;

    /// <summary>Дальняя лента видна от этой дистанции (до центра её чанка): ниже ближние чанки полнее, а лента заполняла бы зазоры между полосами LOD0.</summary>
    public static float FarBegin(LodConfig lod) => lod.RoadLod1;

    private const float LaneHalfWidth = 1.55f;
    private const float ConnectorHalfWidth = 1.1f;
    private const float Lod1Tolerance = 0.4f;
    private const float FarTolerance = 6f;
    private const float FarY = -0.10f; // под дорогами ближних чанков (y >= 0), над землёй (-0.15)

    private static readonly Color _streetColor = new(0.23f, 0.25f, 0.30f);
    private static readonly Color _arterialColor = new(0.34f, 0.38f, 0.46f);
    private static readonly Color _connectorColor = new(0.17f, 0.18f, 0.22f);

    private readonly ShaderMaterial _material;
    private readonly List<int> _keep = new();
    private readonly Vec2[] _scratch = new Vec2[256];

    /// <summary>Версия сети (<c>NetworkView.Version</c>), по которой построены меши; при расхождении <see cref="SimHost"/> пересобирает дороги целиком.</summary>
    public int Version { get; }

    public RoadChunks(Simulation sim, HeatTexture heat, LodConfig lod)
    {
        Version = sim.Network.Version;
        _material = HeatPalette.CreateMaterial(GD.Load<Shader>("res://Shaders/road.gdshader"), heat.Texture, false, lod.PointRange);
        Root = new Node3D { Name = "Roads" };
        Build(sim.Network, lod);
    }

    public Node3D Root { get; }

    /// <summary>F1: heatmap везде (иначе только дальше дистанции точек).</summary>
    public bool Heat { get; private set; }

    public void SetHeat(bool on)
    {
        Heat = on;
        _material.SetShaderParameter("heat_force", on ? 1f : 0f);
    }

    private static Color KindColor(NetworkView net, int segment)
    {
        if ((uint)segment >= (uint)net.Segments.Length)
        {
            return _streetColor;
        }

        Color c = net.Segments[segment].Kind == RoadKind.Arterial ? _arterialColor : _streetColor;

        // закрывающийся сегмент (4.3, T4: Closing, дренаж) - затемнён и сдвинут к красно-коричневому; меши пересобираются при смене Version сети
        return (net.Segments[segment].Flags & SegmentFlags.Closing) != 0 ? new Color(c.R * 0.45f + 0.10f, c.G * 0.35f, c.B * 0.35f) : c;
    }

    private static float KindY(NetworkView net, int segment) =>
        segment >= 0 && segment < net.Segments.Length && net.Segments[segment].Kind == RoadKind.Arterial ? 0.02f : 0f;

    private static Vec2 NodePos(NetworkView net, int node, Vec2 fallback) => (uint)node < (uint)net.Nodes.Length ? net.Nodes[node].Position : fallback;

    private void Build(NetworkView net, LodConfig lod)
    {
        var lod0 = new Dictionary<long, MeshBuilder>();
        var lod1 = new Dictionary<long, MeshBuilder>();
        var far = new Dictionary<long, MeshBuilder>();

        // позиция сегмента для привязки к чанку (середина между узлами): все представления сегмента - в одном чанке, иначе при смене LOD бывают дыры
        int segCount = net.Segments.Length;
        var segCx = new float[segCount];
        var segCz = new float[segCount];
        for (int s = 0; s < segCount; s++)
        {
            if (!net.IsSegmentAlive(s))
            {
                continue;
            }

            SegmentData sd = net.Segments[s];
            int lane = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            Vec2 fb = (uint)lane < (uint)net.Lanes.Length && net.Lanes[lane].PointCount > 0 ? net.LanePoints[net.Lanes[lane].FirstPoint] : default;
            Vec2 a = NodePos(net, sd.NodeA, fb);
            Vec2 b = NodePos(net, sd.NodeB, fb);
            segCx[s] = (a.X + b.X) * 0.5f;
            segCz[s] = (a.Z + b.Z) * 0.5f;
        }

        // LOD0: ленты по всем полосам, как раньше (connector'ы - по узлу)
        for (int li = 0; li < net.Lanes.Length; li++)
        {
            LaneData lane = net.Lanes[li];
            if (!net.IsLaneAlive(li) || lane.PointCount < 2)
            {
                continue;
            }

            bool connector = lane.Kind == LaneKind.Connector;
            int seg = connector ? -1 : lane.Segment;
            float cx;
            float cz;
            if (connector && (uint)lane.Node < (uint)net.Nodes.Length)
            {
                cx = net.Nodes[lane.Node].Position.X;
                cz = net.Nodes[lane.Node].Position.Z;
            }
            else if ((uint)seg < (uint)segCount)
            {
                cx = segCx[seg];
                cz = segCz[seg];
            }
            else
            {
                Vec2 p0 = net.LanePoints[lane.FirstPoint];
                cx = p0.X;
                cz = p0.Z;
            }

            Color col = connector ? _connectorColor : KindColor(net, seg);
            float y = connector ? 0.04f : KindY(net, seg);
            MeshBuilder b = ChunkGrid.Get(lod0, ChunkGrid.Key(cx, cz, NearCell), 1);
            b.Ribbon(net.LanePoints.Slice(lane.FirstPoint, lane.PointCount), connector ? ConnectorHalfWidth : LaneHalfWidth, 0f, y, col, seg);
        }

        // LOD1 и лента: по направлению сегмента; узлы - квадратом
        for (int s = 0; s < segCount; s++)
        {
            if (!net.IsSegmentAlive(s))
            {
                continue;
            }

            SegmentData sd = net.Segments[s];
            MeshBuilder b1 = ChunkGrid.Get(lod1, ChunkGrid.Key(segCx[s], segCz[s], NearCell), 1);
            MeshBuilder bf = ChunkGrid.Get(far, ChunkGrid.Key(segCx[s], segCz[s], FarCell), 1);
            Color col = KindColor(net, s);
            float y = KindY(net, s);
            AddGroup(net, sd.FirstLaneAB, sd.LanesAB, s, b1, Lod1Tolerance, y, col);
            AddGroup(net, sd.FirstLaneBA, sd.LanesBA, s, b1, Lod1Tolerance, y, col);
            AddGroup(net, sd.FirstLaneAB, sd.LanesAB, s, bf, FarTolerance, FarY, col);
            AddGroup(net, sd.FirstLaneBA, sd.LanesBA, s, bf, FarTolerance, FarY, col);
        }

        for (int n = 0; n < net.Nodes.Length; n++)
        {
            NodeData nd = net.Nodes[n];
            if (!net.IsNodeAlive(n) || nd.ArmCount <= 0)
            {
                continue;
            }

            float h = MathF.Max(nd.Radius, 3f);
            AddNodeQuad(ChunkGrid.Get(lod1, ChunkGrid.Key(nd.Position.X, nd.Position.Z, NearCell), 1), nd.Position, h, 0.04f);
            AddNodeQuad(ChunkGrid.Get(far, ChunkGrid.Key(nd.Position.X, nd.Position.Z, FarCell), 1), nd.Position, h, FarY + 0.01f);
        }

        GD.Print(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[roads] chunks: lod0 {lod0.Count}, lod1 {lod1.Count}, far {far.Count} (ячейки {NearCell:0}/{FarCell:0} м, LOD0 до {lod.RoadLod0:0} м, лента от {FarBegin(lod):0} м без верхней границы дальности, LOD1 до {FarBegin(lod) + FarOverlap:0} м)"));
        ChunkGrid.Emit(Root, lod0, "Road0", _material, 0f, lod.RoadLod0 + NearHalfDiag);
        ChunkGrid.Emit(Root, lod1, "Road1", _material, lod.RoadLod0 + NearHalfDiag, FarBegin(lod) + FarOverlap);
        // дальняя лента от FarBegin; ближний LOD1 держится до FarBegin + FarOverlap, чтобы не было дыр (центры чанков разных сеток)
        // верхней границы нет: на общем плане s10k/s1m камера дальше 6 000 м (render-budgets road.far_distance_m), а лента - единственное, что видно
        ChunkGrid.Emit(Root, far, "RoadFar", _material, FarBegin(lod), 0f);
    }

    private static void AddNodeQuad(MeshBuilder b, Vec2 p, float half, float y)
    {
        int a = b.Add(new Vector3(p.X - half, y, p.Z - half), _connectorColor, -1f);
        int c = b.Add(new Vector3(p.X + half, y, p.Z - half), _connectorColor, -1f);
        int d = b.Add(new Vector3(p.X - half, y, p.Z + half), _connectorColor, -1f);
        int e = b.Add(new Vector3(p.X + half, y, p.Z + half), _connectorColor, -1f);
        b.Quad(a, c, d, e);
    }

    /// <summary>Лента на все полосы одного направления сегмента: по полилинии крайней правой полосы, сдвиг к середине группы, упрощение.</summary>
    private void AddGroup(NetworkView net, int firstLane, int n, int seg, MeshBuilder b, float tol, float y, Color col)
    {
        if (n <= 0 || (uint)firstLane >= (uint)net.Lanes.Length)
        {
            return;
        }

        LaneData l0 = net.Lanes[firstLane];
        if (l0.PointCount < 2)
        {
            return;
        }

        ReadOnlySpan<Vec2> pts = net.LanePoints.Slice(l0.FirstPoint, l0.PointCount);
        float shift = 0f;
        if (n > 1 && (uint)(firstLane + n - 1) < (uint)net.Lanes.Length)
        {
            LaneData ll = net.Lanes[firstLane + n - 1];
            if (ll.PointCount >= 1)
            {
                Vec2 p0 = pts[0];
                Vec2 p1 = pts[1];
                Vec2 q = net.LanePoints[ll.FirstPoint];
                // нормаль как у MeshBuilder.Ribbon: (-dz, dx)
                float side = ((-(p1.Z - p0.Z)) * (q.X - p0.X)) + ((p1.X - p0.X) * (q.Z - p0.Z));
                shift = (side >= 0f ? 1f : -1f) * (n - 1) * LaneHalfWidth;
            }
        }

        ChunkGrid.Simplify(pts, tol, _keep);
        int m = Math.Min(_keep.Count, _scratch.Length);
        for (int k = 0; k < m; k++)
        {
            _scratch[k] = pts[_keep[k]];
        }

        b.Ribbon(_scratch.AsSpan(0, m), n * LaneHalfWidth, shift, y, col, seg);
    }
}
