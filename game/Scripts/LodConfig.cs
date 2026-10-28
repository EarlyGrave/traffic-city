using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Sim.Scenarios.Mods;
using Sim.Scenarios.Mods.Gltf;

namespace TrafficCity;

/// <summary>
/// Числа LOD для ТС (docs/design/render.md §3.1, §3.3): дистанции уровней, дистанция точек, минимальный размер точки в пикселях и лимиты
/// инстансов на уровень (LOD0/LOD1/LOD2/точки). Источник - <c>content/render-budgets.json</c> (<see cref="RenderBudgets"/>, V2);
/// если файл не загрузился - дефолты таблиц §3.1/§3.3 (в <see cref="KindBudget"/> они не подставляются).
/// </summary>
public sealed class LodConfig
{
    public const int Levels = 4; // LOD0, LOD1, LOD2, точки

    private static readonly double[] _defaultDist = [60, 200, 600];
    private static readonly int[] _defaultCapsLow = [100, 800, 4000, 30000];
    private static readonly int[] _defaultCapsHigh = [200, 2000, 8000, 60000];

    /// <summary>Верхние границы уровней 0..3 (LOD0, LOD1, LOD2, точки), м, уже умноженные на <c>lod_scale</c>.</summary>
    public float[] Bounds { get; } = new float[Levels];

    /// <summary>Лимиты инстансов на кадр по уровням.</summary>
    public int[] Caps { get; } = new int[Levels];

    public float PointMinPx { get; private set; } = 2f;

    /// <summary>Дорога (render.md §3.1): LOD0 до, LOD1 до, дальше лента до <see cref="RoadFar"/>; уже умножено на <c>lod_scale</c>.</summary>
    public float RoadLod0 { get; private set; } = 250f;

    public float RoadLod1 { get; private set; } = 1000f;

    public float RoadFar { get; private set; } = 6000f;

    /// <summary>Дистанция, дальше которой пешеходы - heatmap <c>PedEdgeCounts</c> (render.md §3.1, §3.5).</summary>
    public float PedPointRange { get; private set; } = 900f;

    /// <summary>Пешеходы (render.md §3.1): верхние границы LOD0 и LOD1, м (уже умножены на <c>lod_scale</c>); дальше - точки до <see cref="PedPointRange"/>.</summary>
    public float[] PedBounds { get; } = [40f, 150f];

    /// <summary>Лимиты инстансов пешеходов: LOD0, LOD1, (LOD2 не используется), точки.</summary>
    public int[] PedCaps { get; } = [500, 5000, 0, 40000];

    /// <summary>Здания (render.md §3.1): верхние границы LOD0, LOD1, LOD2, м (уже умножены на <c>lod_scale</c>); дальше - бокс-импостор до <see cref="BuildingFar"/>.</summary>
    public float[] BuildingBounds { get; } = [150f, 500f, 1500f];

    public float BuildingFar { get; private set; } = 6000f;

    /// <summary>Лимиты инстансов зданий ближнего кольца: LOD0, LOD1 (LOD2 и импосторы - в мешах чанков).</summary>
    public int[] BuildingCaps { get; } = [120, 800];

    /// <summary>Множитель дистанций пресета (<c>lod_scale</c>); рёбра зданий гаснут на 1 000-1 500 м x этот множитель.</summary>
    public float LodScale { get; private set; } = 1f;

    public string Preset { get; private set; } = "high";

    /// <summary>Пресет из <c>render-budgets.json</c> (тени, scaling_3d); null, если файл не загрузился.</summary>
    public PresetBudget? Budget { get; private set; }

    /// <summary>Откуда взяты числа (для лога).</summary>
    public string Source { get; private set; } = "defaults (render.md §3.1/§3.3)";

    /// <summary>Дистанция точек: дальше машин не рисуем и ядро их не пишет.</summary>
    public float PointRange => Bounds[Levels - 1];

    public static LodConfig Load(string contentDir, string preset)
    {
        var cfg = new LodConfig { Preset = preset };
        bool low = preset == "low";
        double scale = low ? 0.7 : 1.0;
        double[] dist = _defaultDist;
        double far = 2500;
        double roadNear = 250;
        double roadMid = 1000;
        double roadFar = 6000;
        double pedFar = 900;
        double[] pedDist = [40, 150];
        double[] bldDist = [150, 500, 1500];
        double bldFar = 6000;
        int[] bldCaps = low ? [50, 300] : [120, 800];
        int[] pedCaps = low ? [200, 2000, 0, 20000] : [500, 5000, 0, 40000];
        int[] caps = low ? _defaultCapsLow : _defaultCapsHigh;
        try
        {
            string path = System.IO.Path.Combine(contentDir, RenderBudgets.FileName);
            var issues = new List<ModIssue>();
            RenderBudgets? rb = RenderBudgets.Load(path, issues);
            if (rb == null)
            {
                // Пустой contentDir - заглушка инициализатора поля SimHost до _Ready: предупреждать нечего.
                if (contentDir.Length > 0)
                {
                    GD.PushWarning(string.Create(CultureInfo.InvariantCulture, $"render-budgets: {path} не загрузился ({issues.Count} замечаний), дефолты §3.1/§3.3"));
                }
            }
            else
            {
                KindBudget kb = rb.For(AssetKind.Vehicle);
                if (kb.LodDistancesM is { Count: >= 3 })
                {
                    dist = [kb.LodDistancesM[0], kb.LodDistancesM[1], kb.LodDistancesM[2]];
                }

                if (kb.FarDistanceM is { } fd)
                {
                    far = fd;
                }

                KindBudget road = rb.For(AssetKind.Road);
                if (road.LodDistancesM is { Count: >= 2 })
                {
                    roadNear = road.LodDistancesM[0];
                    roadMid = road.LodDistancesM[1];
                }

                if (road.FarDistanceM is { } rf)
                {
                    roadFar = rf;
                }

                KindBudget bld = rb.For(AssetKind.Building);
                if (bld.LodDistancesM is { Count: >= 3 })
                {
                    bldDist = [bld.LodDistancesM[0], bld.LodDistancesM[1], bld.LodDistancesM[2]];
                }

                if (bld.FarDistanceM is { } bf)
                {
                    bldFar = bf;
                }

                if (rb.Pedestrian?.FarDistanceM is { } pf)
                {
                    pedFar = pf;
                }

                if (rb.Pedestrian?.LodDistancesM is { Count: >= 2 } pd)
                {
                    pedDist = [pd[0], pd[1]];
                }

                if (rb.Presets.TryGetValue(preset, out PresetBudget? pb))
                {
                    cfg.Budget = pb;
                    scale = pb.LodScale;
                    cfg.PointMinPx = (float)pb.PointMinPx;
                    if (pb.Caps.TryGetValue("pedestrian", out IReadOnlyList<int>? pc) && pc.Count >= Levels)
                    {
                        pedCaps = [pc[0], pc[1], pc[2], pc[3]];
                    }

                    if (pb.Caps.TryGetValue("building", out IReadOnlyList<int>? bc) && bc.Count >= 2)
                    {
                        bldCaps = [bc[0], bc[1]];
                    }

                    if (pb.Caps.TryGetValue("vehicle", out IReadOnlyList<int>? c) && c.Count >= Levels)
                    {
                        caps = [c[0], c[1], c[2], c[3]];
                    }

                    cfg.Source = "content/render-budgets.json, preset " + preset;
                }
                else
                {
                    cfg.Source = "content/render-budgets.json (пресета " + preset + " нет, лимиты по умолчанию)";
                }
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushWarning("render-budgets: " + e.Message);
        }

        for (int k = 0; k < 3; k++)
        {
            cfg.Bounds[k] = (float)(dist[k] * scale);
        }

        cfg.Bounds[3] = (float)(far * scale);
        cfg.RoadLod0 = (float)(roadNear * scale);
        cfg.RoadLod1 = (float)(roadMid * scale);
        cfg.RoadFar = (float)roadFar; // дальность вида не масштабируется
        cfg.PedPointRange = (float)(pedFar * scale);
        cfg.PedBounds[0] = (float)(pedDist[0] * scale);
        cfg.PedBounds[1] = (float)(pedDist[1] * scale);
        for (int k = 0; k < 3; k++)
        {
            cfg.BuildingBounds[k] = (float)(bldDist[k] * scale);
        }

        cfg.BuildingFar = (float)bldFar;
        cfg.BuildingCaps[0] = bldCaps[0];
        cfg.BuildingCaps[1] = bldCaps[1];
        cfg.LodScale = (float)scale;
        Array.Copy(pedCaps, cfg.PedCaps, Levels);
        Array.Copy(caps, cfg.Caps, Levels);
        return cfg;
    }
}
