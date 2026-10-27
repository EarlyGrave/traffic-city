using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Тротуары, переходы и зебры чанками (docs/design/render.md §3.5, §5.2). Тротуары (и ленты переходов для heatmap) - непрозрачные ленты шириной
/// в тротуар на 0.05 м выше дороги: ближние чанки 250 м (полные полилинии, до <see cref="LodConfig.RoadLod1"/>) и дальние 2 000 м (упрощённые);
/// цвет heatmap <c>PedEdgeCounts</c> - в шейдере <c>sidewalk.gdshader</c> из <see cref="HeatTexture"/> по <c>CUSTOM0.x</c> (ребро), дальше
/// дистанции пешеходов (<see cref="LodConfig.PedPointRange"/>) или при <c>--ped-mode heat</c>. Зебры - ближние чанки до той же дистанции.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены и освобождаются вместе с ними
public sealed class PedChunks
{
    private const float FarTolerance = 3f;
    private const float WalkY = 0.06f;
    private const float FarWalkY = 0.05f;
    private const float ZebraY = 0.07f;

    private static readonly Color _sidewalkColor = Palette.Sidewalk;
    private static readonly Color _zebraColor = Palette.Zebra;

    private readonly ShaderMaterial _material;
    private readonly float[] _edgeLenM;
    private readonly List<int> _keep = new();
    private readonly Vec2[] _scratch = new Vec2[256];

    public PedChunks(Simulation sim, LodConfig lod)
    {
        NetworkView net = sim.Network;
        ReadOnlySpan<PedEdgeData> sidewalks = net.Sidewalks;
        ReadOnlySpan<PedEdgeData> crossings = net.Crossings;
        ReadOnlySpan<Vec2> pts = net.PedEdgePoints;
        float swWidth = net.SidewalkWidthM > 0f ? net.SidewalkWidthM : 3f;
        float czWidth = net.CrosswalkWidthM > 0f ? net.CrosswalkWidthM : 3f;

        int total = sidewalks.Length + crossings.Length;
        _edgeLenM = new float[total];
        Heat = new HeatTexture(total);
        _material = HeatPalette.CreateMaterial(GD.Load<Shader>("res://Shaders/sidewalk.gdshader"), Heat.Texture, true, lod.PedPointRange);
        Root = new Node3D { Name = "PedStatic" };

        var near = new Dictionary<long, MeshBuilder>();
        var far = new Dictionary<long, MeshBuilder>();
        var zebra = new Dictionary<long, MeshBuilder>();
        for (int e = 0; e < total; e++)
        {
            bool cross = e >= sidewalks.Length;
            PedEdgeData ed = cross ? crossings[e - sidewalks.Length] : sidewalks[e];
            _edgeLenM[e] = ed.LenMm / 1000f;
            if (ed.PointCount < 2)
            {
                continue;
            }

            ReadOnlySpan<Vec2> poly = pts.Slice(ed.FirstPoint, ed.PointCount);
            Vec2 mid = poly[poly.Length / 2];
            float half = (cross ? czWidth : swWidth) * 0.5f;
            float flag = cross ? 1f : 0f;
            ChunkGrid.Get(near, ChunkGrid.Key(mid.X, mid.Z, RoadChunks.NearCell), 2).Ribbon(poly, half, 0f, WalkY, _sidewalkColor, e, flag);
            MeshBuilder bf = ChunkGrid.Get(far, ChunkGrid.Key(mid.X, mid.Z, RoadChunks.FarCell), 2);
            ChunkGrid.Simplify(poly, FarTolerance, _keep);
            int m = Math.Min(_keep.Count, _scratch.Length);
            for (int k = 0; k < m; k++)
            {
                _scratch[k] = poly[_keep[k]];
            }

            bf.Ribbon(_scratch.AsSpan(0, m), half, 0f, FarWalkY, _sidewalkColor, e, flag);
            if (cross)
            {
                // VS4: зебра - от кромки до кромки проезжей части (CurbL..CurbR), а не между центрами тротуаров, если у сегмента есть профиль
                Vec2 za = poly[0];
                Vec2 zb = poly[^1];
                if (ed.Segment >= 0 && RoadProfileLayouts.ZebraTrim(net, ed.Segment, za, zb, out float ta, out float tb))
                {
                    float zl = MathF.Sqrt(((zb.X - za.X) * (zb.X - za.X)) + ((zb.Z - za.Z) * (zb.Z - za.Z)));
                    if (zl > ta + tb + 1f)
                    {
                        float ux = (zb.X - za.X) / zl;
                        float uz = (zb.Z - za.Z) / zl;
                        (za, zb) = (new Vec2(za.X + (ux * ta), za.Z + (uz * ta)), new Vec2(zb.X - (ux * tb), zb.Z - (uz * tb)));
                    }
                }

                AddZebra(ChunkGrid.Get(zebra, ChunkGrid.Key(mid.X, mid.Z, RoadChunks.NearCell), 1), za, zb, czWidth);
            }
        }

        ChunkGrid.Emit(Root, near, "Walk", _material, 0f, RoadChunks.FarBegin(lod) + RoadChunks.FarOverlap);
        ChunkGrid.Emit(Root, far, "WalkFar", _material, RoadChunks.FarBegin(lod), 0f);
        var zebraMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/flat.gdshader") };
        ChunkGrid.Emit(Root, zebra, "Zebra", zebraMat, 0f, lod.PedPointRange + RoadChunks.NearHalfDiag);
    }

    public Node3D Root { get; }

    public HeatTexture Heat { get; }

    /// <summary>Режим пешеходов: <c>1</c> - heatmap везде, <c>-1</c> - агенты везде, <c>0</c> - по дистанции.</summary>
    public void SetForce(float force) => _material.SetShaderParameter("heat_force", force);

    /// <summary>Обновить текстуру данных из <c>PedEdgeCounts</c> (~2 Гц).</summary>
    public void RefreshHeat(Simulation sim) => Heat.UpdatePeds(sim.PedEdgeCounts, _edgeLenM);

    private static void AddZebra(MeshBuilder b, Vec2 a, Vec2 end, float width)
    {
        float dx = end.X - a.X;
        float dz = end.Z - a.Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < 1f)
        {
            return;
        }

        dx /= len;
        dz /= len;
        float nx = -dz;
        float nz = dx;

        // полосы вдоль направления перехода, поперёк - шаг около 1 м по ширине зебры
        int stripes = Math.Max(2, (int)width);
        const float hw = 0.25f;
        for (int s = 0; s < stripes; s++)
        {
            float u = (-width * 0.5f) + hw + (s * (width - (2f * hw)) / (stripes - 1));
            float cx = a.X + (nx * u);
            float cz = a.Z + (nz * u);
            int v0 = b.Add(new Vector3(cx - (nx * hw), ZebraY, cz - (nz * hw)), _zebraColor, 0f);
            int v1 = b.Add(new Vector3(cx + (nx * hw), ZebraY, cz + (nz * hw)), _zebraColor, 0f);
            int v2 = b.Add(new Vector3(cx + (dx * len) - (nx * hw), ZebraY, cz + (dz * len) - (nz * hw)), _zebraColor, 0f);
            int v3 = b.Add(new Vector3(cx + (dx * len) + (nx * hw), ZebraY, cz + (dz * len) + (nz * hw)), _zebraColor, 0f);
            b.Quad(v0, v1, v2, v3);
        }
    }
}
