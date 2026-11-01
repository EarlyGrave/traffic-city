using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Слой земли (VS9, docs/design/visual-style.md §8, ADR-0027): парки, пруды, вода и береговая полоса из <see cref="Simulation.LandUse"/> плюс тон кварталов
/// (<see cref="LotChunks"/>). Один меш на дальний чанк 2 000 м (<see cref="RoadChunks.FarCell"/>) и один материал: ≤ 1 draw call на чанк.
/// Слой лежит под дорогами и над плоскостью <c>Ground</c> (y = -0.15), без видимости по дистанции (фоновая плоскость - за краем карты).
/// Перестраивается при смене <c>LandUseVersion</c> или <c>LotsVersion</c>.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены и освобождаются вместе с ними
public sealed class LandUseChunks
{
    private const float ParkY = -0.12f;
    private const float PondY = -0.11f;
    private const int PondSides = 16;
    private const float PondInset = 0.62f;   // радиус пруда: доля половины меньшей стороны клетки
    private const float ShoreBandM = 24f;     // светлая полоса у берега, м
    private const float CoastExtendM = 3000f; // вода уходит за край карты: фон не обрывается на плоскости Ground

    private readonly Vec2 _mapCenter;
    private readonly ShaderMaterial _material;

    public LandUseChunks(Vec2 mapCenter)
    {
        _mapCenter = mapCenter;
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/ground.gdshader") };
        _material.SetShaderParameter("shore_color", Palette.ShoreTone);
        Root = new Node3D { Name = "LandUse" };
    }

    public Node3D Root { get; }

    /// <summary>Версия землепользования, по которой собран слой.</summary>
    public int BuiltLandUseVersion { get; private set; } = -1;

    /// <summary>Версия участков, по которой собран слой.</summary>
    public int BuiltLotsVersion { get; private set; } = -1;

    public int PolyCount { get; private set; }

    public int LotCount { get; private set; }

    /// <summary>Есть ли что рисовать (землепользование выключено и нет участков - слой не нужен).</summary>
    public static bool Needed(Simulation sim) => sim.LandUse.Length > 0 || sim.LotCount > 0;

    public bool IsStale(int landUseVersion, int lotsVersion) => landUseVersion != BuiltLandUseVersion || lotsVersion != BuiltLotsVersion;

    /// <summary>Собрать слой заново: старые чанки освобождаются, новые добавляются в тот же корень.</summary>
    public void Rebuild(Simulation sim)
    {
        foreach (Node child in Root.GetChildren())
        {
            child.QueueFree();
        }

        BuiltLandUseVersion = sim.LandUseVersion;
        BuiltLotsVersion = sim.LotsVersion;
        var map = new Dictionary<long, MeshBuilder>();
        ReadOnlySpan<LandUsePoly> polys = sim.LandUse;
        PolyCount = polys.Length;
        for (int i = 0; i < polys.Length; i++)
        {
            AddPoly(map, in polys[i]);
        }

        LotCount = LotChunks.Append(map, sim);
        ChunkGrid.Emit(Root, map, "Ground", _material, 0f, 0f);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[landuse] polys {PolyCount}, lots {LotCount}, chunks {map.Count}, version {BuiltLandUseVersion}/{BuiltLotsVersion}"));
    }

    private void AddPoly(Dictionary<long, MeshBuilder> map, in LandUsePoly p)
    {
        var centroid = new Vec2((p.P0.X + p.P1.X + p.P2.X + p.P3.X) * 0.25f, (p.P0.Z + p.P1.Z + p.P2.Z + p.P3.Z) * 0.25f);
        MeshBuilder b = ChunkGrid.Get(map, ChunkGrid.Key(centroid.X, centroid.Z, RoadChunks.FarCell), 4, true);
        bool coast = (p.Flags & (byte)LandUseMarks.Coast) != 0;
        if ((LandUseKind)p.Kind == LandUseKind.Water)
        {
            if (coast)
            {
                AddCoast(b, in p, centroid);
            }
            else
            {
                Quad(b, in p, Palette.WaterTone, Palette.ZoneWater);
            }

            return;
        }

        Quad(b, in p, Palette.ParkTone, Palette.ZonePark);
        if ((p.Flags & (byte)LandUseMarks.Pond) != 0)
        {
            AddPond(b, in p, centroid);
        }
    }

    private static void Quad(MeshBuilder b, in LandUsePoly p, Color tone, Color zone)
    {
        int v0 = Vtx(b, p.P0, ParkY, tone, zone, 0f);
        int v1 = Vtx(b, p.P1, ParkY, tone, zone, 0f);
        int v2 = Vtx(b, p.P2, ParkY, tone, zone, 0f);
        int v3 = Vtx(b, p.P3, ParkY, tone, zone, 0f);
        b.Tri(v0, v1, v2);
        b.Tri(v0, v2, v3);
    }

    private static int Vtx(MeshBuilder b, Vec2 p, float y, Color tone, Color zone, float shore) =>
        b.Add(new Vector3(p.X, y, p.Z), tone, zone.R, zone.G, zone.B, 0f, new Vector2(shore, 0f));

    /// <summary>Пруд: 16-угольник с центром в центре клетки; <c>UV.x</c> = 0 в центре и 1 на кромке (светлая полоса берега).</summary>
    private static void AddPond(MeshBuilder b, in LandUsePoly p, Vec2 c)
    {
        float r = MathF.Min(MathF.Min(Dist(p.P0, p.P1), Dist(p.P1, p.P2)), MathF.Min(Dist(p.P2, p.P3), Dist(p.P3, p.P0))) * 0.5f * PondInset;
        if (r < 4f)
        {
            return;
        }

        int center = Vtx(b, c, PondY, Palette.WaterTone, Palette.ZoneWater, 0f);
        int first = -1;
        int prev = -1;
        for (int k = 0; k < PondSides; k++)
        {
            float a = k * MathF.Tau / PondSides;
            int v = Vtx(b, new Vec2(c.X + (MathF.Cos(a) * r), c.Z + (MathF.Sin(a) * r)), PondY, Palette.WaterTone, Palette.ZoneWater, 1f);
            if (prev >= 0)
            {
                b.Tri(center, prev, v);
            }
            else
            {
                first = v;
            }

            prev = v;
        }

        b.Tri(center, prev, first);
    }

    /// <summary>
    /// Береговая полоса: две вершины у края карты (ближе к центру по оси берега) - кромка (<c>UV.x = 1</c>), узкая светлая полоса, затем вода,
    /// уходящая далеко за карту. Вершины порядка обхода не предполагаем: ближние две и их дальние пары находим по расстоянию.
    /// </summary>
    private void AddCoast(MeshBuilder b, in LandUsePoly p, Vec2 centroid)
    {
        Vec2[] v = { p.P0, p.P1, p.P2, p.P3 };
        bool alongX = MathF.Abs(centroid.X - _mapCenter.X) >= MathF.Abs(centroid.Z - _mapCenter.Z);
        Array.Sort(v, (a, c) => Ax(a, alongX).CompareTo(Ax(c, alongX)));
        Vec2 n0 = v[0];
        Vec2 n1 = v[1];
        bool swap = Dist(v[2], n0) > Dist(v[3], n0);
        Vec2 f0 = swap ? v[3] : v[2];
        Vec2 f1 = swap ? v[2] : v[3];
        Vec2 d0 = Unit(f0, n0);
        Vec2 d1 = Unit(f1, n1);
        var m0 = new Vec2(n0.X + (d0.X * ShoreBandM), n0.Z + (d0.Z * ShoreBandM));
        var m1 = new Vec2(n1.X + (d1.X * ShoreBandM), n1.Z + (d1.Z * ShoreBandM));
        var e0 = new Vec2(f0.X + (d0.X * CoastExtendM), f0.Z + (d0.Z * CoastExtendM));
        var e1 = new Vec2(f1.X + (d1.X * CoastExtendM), f1.Z + (d1.Z * CoastExtendM));
        Color tone = Palette.WaterTone;
        Color zone = Palette.ZoneWater;
        int a0 = Vtx(b, n0, ParkY, tone, zone, 1f);
        int a1 = Vtx(b, n1, ParkY, tone, zone, 1f);
        int b0 = Vtx(b, m0, ParkY, tone, zone, 0f);
        int b1 = Vtx(b, m1, ParkY, tone, zone, 0f);
        int c0 = Vtx(b, e0, ParkY, tone, zone, 0f);
        int c1 = Vtx(b, e1, ParkY, tone, zone, 0f);
        b.Quad(a0, a1, b0, b1);
        b.Quad(b0, b1, c0, c1);
    }

    private float Ax(Vec2 p, bool alongX) => alongX ? MathF.Abs(p.X - _mapCenter.X) : MathF.Abs(p.Z - _mapCenter.Z);

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static Vec2 Unit(Vec2 to, Vec2 from)
    {
        float d = Dist(to, from);
        return d < 1e-3f ? new Vec2(0f, 0f) : new Vec2((to.X - from.X) / d, (to.Z - from.Z) / d);
    }
}
