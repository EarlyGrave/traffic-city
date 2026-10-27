using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>
/// Слой зданий (docs/design/render.md V8, §3.1, §5.2; стиль B - visual-style.md §3). Здания - процедурные боксы по зоне (<see cref="ChunkBuilder"/>), три представления:
/// ближнее кольцо до LOD1 (500 м) - один <c>MultiMesh</c> с тенями; LOD2 (500-1 500 м) - меши чанков 250 м; бокс-импостор (до 6 000 м) - меши чанков
/// <see cref="ImpostorCell"/>. Все три рисуют один шейдер <c>building.gdshader</c> (рёбра 1.25 px, гаснут к 1 500 м). Чанки пересобираются целиком при смене
/// <c>BuildingsVersion</c> (не чаще раза в <see cref="MinRebuildMs"/>), кольцо - при сдвиге камеры больше <see cref="RingShiftM"/> или после пересборки.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены и освобождаются вместе с ними
public sealed class BuildingLayer
{
    /// <summary>Сдвиг камеры, после которого кольцо перераспределяется (render.md §5.2).</summary>
    public const float RingShiftM = 25f;

    /// <summary>Не чаще: рост застройки не должен давать кадр длиннее среднего (полная пересборка - до очереди по чанкам).</summary>
    public const int MinRebuildMs = 1500;

    /// <summary>Сторона ячейки LOD2-чанков, м.</summary>
    public const float Lod2Cell = 250f;

    /// <summary>
    /// Сторона ячейки импосторов, м. Меньше <see cref="RoadChunks.FarCell"/> (2 000): <c>visibility_range</c> мерит расстояние до центра узла, и чанку 2 000 м
    /// пришлось бы быть видимым почти всегда (полудиагональ 1 414 м), то есть дублировать LOD2.
    /// </summary>
    public const float ImpostorCell = 1000f;

    private readonly LodConfig _cfg;
    private readonly ShaderMaterial _material;
    private readonly BoxBatch _ring;
    private readonly Node3D _lod2 = new() { Name = "BuildingsLod2" };
    private readonly Node3D _far = new() { Name = "BuildingsImpostor" };
    private BuildingRenderData[] _buf = Array.Empty<BuildingRenderData>();
    private BuildingBox[] _boxes = Array.Empty<BuildingBox>();
    private int _count;
    private float[] _dist = Array.Empty<float>();
    private int[] _idx = Array.Empty<int>();
    private int _builtVersion = -1;
    private ulong _lastBuildMs;
    private bool _ringDirty = true;
    private Vector3 _ringCam = new(float.MaxValue, 0f, 0f);

    public BuildingLayer(Node parent, LodConfig cfg)
    {
        _cfg = cfg;
        _material = CreateMaterial(cfg.LodScale);
        _ring = new BoxBatch("Buildings_ring", BoxBuilder.Box(Vector3.One, new Vector3(0f, 0.5f, 0f)), _material);
        _ring.Node.CastShadow = GeometryInstance3D.ShadowCastingSetting.On; // тень: здания LOD0-LOD1 при высоте >= 1 м (render.md §6)
        Root = new Node3D { Name = "Buildings" };
        Root.AddChild(_ring.Node);
        Root.AddChild(_lod2);
        Root.AddChild(_far);
        parent.AddChild(Root);
    }

    public Node3D Root { get; }

    /// <summary>Зданий в последней сборке.</summary>
    public int Count => _count;

    /// <summary>Инстансов в ближнем кольце.</summary>
    public int RingCount => _ring.Count;

    /// <summary>Мешей чанков LOD2 и импосторов.</summary>
    public int ChunkCount => _lod2.GetChildCount() + _far.GetChildCount();

    public string HudText() => string.Create(CultureInfo.InvariantCulture, $"bld {_count}  ring {_ring.Count}/{_cfg.BuildingCaps[0] + _cfg.BuildingCaps[1]}  chunks {ChunkCount}");

    public void Update(Simulation sim, Vector3 cam)
    {
        int ver = sim.BuildingsVersion;
        if (ver != _builtVersion && (Time.GetTicksMsec() - _lastBuildMs >= (ulong)MinRebuildMs || _builtVersion < 0))
        {
            Rebuild(sim);
        }

        float dx = cam.X - _ringCam.X;
        float dz = cam.Z - _ringCam.Z;
        if (_ringDirty || (dx * dx) + (dz * dz) > RingShiftM * RingShiftM || MathF.Abs(cam.Y - _ringCam.Y) > RingShiftM)
        {
            RebuildRing(cam);
        }
    }

    private static ShaderMaterial CreateMaterial(float lodScale)
    {
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/building.gdshader") };
        mat.SetShaderParameter("edge_px", Palette.BuildingEdgePx);
        mat.SetShaderParameter("edge_fade_begin", Palette.BuildingEdgeFadeBegin * lodScale);
        mat.SetShaderParameter("edge_fade_end", Palette.BuildingEdgeFadeEnd * lodScale);
        mat.SetShaderParameter("edge_k", Palette.BuildingEdgeK);
        mat.SetShaderParameter("wall_shade", Palette.BuildingWallShade);
        mat.SetShaderParameter("ao_min", Palette.BuildingAoMin);
        mat.SetShaderParameter("ao_height", Palette.BuildingAoHeight);
        return mat;
    }

    private static void Clear(Node3D n)
    {
        foreach (Node child in n.GetChildren())
        {
            child.QueueFree();
        }
    }

    private void Rebuild(Simulation sim)
    {
        _builtVersion = sim.BuildingsVersion;
        _lastBuildMs = Time.GetTicksMsec();
        int slots = sim.BuildingSlotCount;
        if (_buf.Length < slots)
        {
            _buf = new BuildingRenderData[slots];
        }

        int n = sim.GetBuildings(_buf.AsSpan(0, slots));
        if (_boxes.Length < n)
        {
            _boxes = new BuildingBox[n];
            _dist = new float[n];
            _idx = new int[n];
        }

        int c = 0;
        for (int i = 0; i < n; i++)
        {
            if (ChunkBuilder.TryBox(in _buf[i], out _boxes[c]))
            {
                c++;
            }
        }

        _count = c;
        Clear(_lod2);
        Clear(_far);
        var near = new Dictionary<long, MeshBuilder>();
        var far = new Dictionary<long, MeshBuilder>();
        ChunkBuilder.AppendChunks(near, Lod2Cell, _boxes.AsSpan(0, c));
        ChunkBuilder.AppendChunks(far, ImpostorCell, _boxes.AsSpan(0, c));
        float[] b = _cfg.BuildingBounds;
        float halfNear = Lod2Cell * 0.7072f;
        float halfFar = ImpostorCell * 0.7072f;
        ChunkGrid.Emit(_lod2, near, "Bld2", _material, MathF.Max(0f, b[1] - halfNear), b[2] + halfNear);
        ChunkGrid.Emit(_far, far, "BldFar", _material, MathF.Max(0f, b[2] - halfFar), _cfg.BuildingFar + halfFar);
        _ringDirty = true;
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[buildings] {c} boxes, chunks {_lod2.GetChildCount()} + {_far.GetChildCount()}, version {_builtVersion}"));
    }

    private void RebuildRing(Vector3 cam)
    {
        _ringCam = cam;
        _ringDirty = false;
        float lim = _cfg.BuildingBounds[1];
        float lim2 = lim * lim;
        int cap = _cfg.BuildingCaps[0] + _cfg.BuildingCaps[1];
        int m = 0;
        for (int i = 0; i < _count; i++)
        {
            ref readonly BuildingBox b = ref _boxes[i];
            float dx = b.X - cam.X;
            float dz = b.Z - cam.Z;
            float d2 = (dx * dx) + (dz * dz) + (cam.Y * cam.Y);
            if (d2 < lim2)
            {
                _dist[m] = d2;
                _idx[m] = i;
                m++;
            }
        }

        if (m > cap)
        {
            Array.Sort(_dist, _idx, 0, m);
            m = cap;
        }

        _ring.Begin(m);
        for (int k = 0; k < m; k++)
        {
            ref readonly BuildingBox b = ref _boxes[_idx[k]];
            _ring.Add(b.X, 0f, b.Z, b.Heading, b.Color, b.W, b.H, b.D);
        }

        _ring.End();
    }
}
