using System.Collections.Generic;
using System.Globalization;
using Godot;
using Sim.Scenarios.Mods.Gltf;

namespace TrafficCity;

/// <summary>
/// Пресет рендера (docs/design/render.md §6, ADR-0014 п. 7): тени (PSSM: сплиты, дальность, атлас), scaling_3d, MSAA, окружение.
/// Числа - из <c>content/render-budgets.json</c> (<see cref="LodConfig.Budget"/>), внешний вид - <see cref="VisualPreset"/>.
/// Не зависит от пути рендера: в Compatibility (облако, llvmpipe) то, что движок не поддерживает (scaling_3d, SSAO), игнорируется.
/// </summary>
public static class RenderPreset
{
    /// <summary>Значения на случай, если файл бюджетов не загрузился (совпадают с <c>presets</c> файла).</summary>
    public static PresetBudget Fallback(string preset) => preset == "low"
        ? new PresetBudget(0.7, 2, 0.75, new ShadowBudget(300, 2, 2048), new Dictionary<string, IReadOnlyList<int>>())
        : new PresetBudget(1.0, 2, 1.0, new ShadowBudget(800, 4, 4096), new Dictionary<string, IReadOnlyList<int>>());

    /// <summary>Текущий час суток (0..24) для света; меняется <see cref="SetHour"/>, учитывается в <see cref="Apply"/> (F4 не сбрасывает время).</summary>
    public static float Hour { get; private set; } = VisualPreset.DefaultHour;

    /// <summary>Задать час до <see cref="Apply"/> (аргумент запуска): узлов ещё не трогает.</summary>
    public static void SetStartHour(float hour) => Hour = ((hour % 24f) + 24f) % 24f;

    /// <summary>Сменить час суток и обновить свет/небо/<c>light_tint</c> (только значения, без пересоздания объектов).</summary>
    public static void SetHour(Node root, float hour)
    {
        Hour = ((hour % 24f) + 24f) % 24f;
        VisualPreset.ApplyTime(
            root.GetNodeOrNull<WorldEnvironment>("WorldEnvironment")?.Environment,
            root.GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D"),
            Hour);
    }

    /// <summary>Применить пресет к сцене: свет, окружение, вьюпорт, атлас теней. <paramref name="root"/> - корень сцены (Main).</summary>
    public static void Apply(Node root, LodConfig cfg)
    {
        bool high = cfg.Preset != "low";
        PresetBudget b = cfg.Budget ?? Fallback(cfg.Preset);
        Viewport vp = root.GetViewport();

        // Окружение и свет: весь вид - VisualPreset.
        if (root.GetNodeOrNull<WorldEnvironment>("WorldEnvironment") is { } we)
        {
            we.Environment = VisualPreset.BuildEnvironment(high);
        }

        if (root.GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D") is { } sun)
        {
            sun.RotationDegrees = new Vector3(-VisualPreset.SunElevationDeg, VisualPreset.SunAzimuthDeg, 0f);
            sun.LightColor = VisualPreset.SunColor;
            sun.LightEnergy = VisualPreset.SunEnergy;
            sun.ShadowEnabled = true;
            sun.ShadowBias = VisualPreset.ShadowBias;
            sun.ShadowNormalBias = VisualPreset.ShadowNormalBias;
            sun.DirectionalShadowMode = b.Shadow.Splits switch
            {
                1 => DirectionalLight3D.ShadowMode.Orthogonal,
                2 => DirectionalLight3D.ShadowMode.Parallel2Splits,
                _ => DirectionalLight3D.ShadowMode.Parallel4Splits,
            };
            sun.DirectionalShadowMaxDistance = (float)b.Shadow.DistanceM;
            sun.DirectionalShadowBlendSplits = high;
        }

        SetHour(root, Hour); // свет суток поверх базового (полдень = значения VisualPreset)

        // Атлас и фильтр теней - глобальные настройки сервера (общие для всех направленных источников).
        RenderingServer.DirectionalShadowAtlasSetSize(b.Shadow.Atlas, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(high ? VisualPreset.ShadowFilterHigh : VisualPreset.ShadowFilterLow);

        // Разрешение рендера и сглаживание (scaling_3d работает только в Forward+/Mobile; MSAA 2x - только high).
        vp.Scaling3DMode = b.Scaling3d < 0.999 ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        vp.Scaling3DScale = (float)b.Scaling3d;
        vp.Msaa3D = high ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Disabled;
    }

    /// <summary>Строка для лога: что применено.</summary>
    public static string Describe(LodConfig cfg)
    {
        PresetBudget b = cfg.Budget ?? Fallback(cfg.Preset);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[render] preset {cfg.Preset}: shadow {b.Shadow.Splits} splits, {b.Shadow.DistanceM:0} m, atlas {b.Shadow.Atlas}; scaling_3d {b.Scaling3d:0.##}; msaa {(cfg.Preset == "low" ? "off" : "2x")}; {RenderingServer.GetCurrentRenderingMethod()}/{RenderingServer.GetCurrentRenderingDriverName()}");
    }
}
