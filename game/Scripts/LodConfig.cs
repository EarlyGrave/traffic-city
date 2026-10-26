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
        int[] caps = low ? _defaultCapsLow : _defaultCapsHigh;
        try
        {
            string path = System.IO.Path.Combine(contentDir, RenderBudgets.FileName);
            var issues = new List<ModIssue>();
            RenderBudgets? rb = RenderBudgets.Load(path, issues);
            if (rb == null)
            {
                GD.PushWarning(string.Create(CultureInfo.InvariantCulture, $"render-budgets: {path} не загрузился ({issues.Count} замечаний), дефолты §3.1/§3.3"));
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

                if (rb.Pedestrian?.FarDistanceM is { } pf)
                {
                    pedFar = pf;
                }

                if (rb.Presets.TryGetValue(preset, out PresetBudget? pb))
                {
                    cfg.Budget = pb;
                    scale = pb.LodScale;
                    cfg.PointMinPx = (float)pb.PointMinPx;
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
        Array.Copy(caps, cfg.Caps, Levels);
        return cfg;
    }
}
