using Godot;

namespace TrafficCity;

/// <summary>
/// Палитра heatmap - ЕДИНСТВЕННОЕ место (решение по виду - за владельцем, docs/design/render.md §3.5): нейтральный плейсхолдер
/// «свободно - средне - затор» для дорог и «мало - средне - много» для пешеходов. Подставляется uniform'ами в шейдеры дорог и тротуаров.
/// </summary>
public static class HeatPalette
{
    public static readonly Color RoadLow = new(0.10f, 0.85f, 0.20f);
    public static readonly Color RoadMid = new(1.00f, 0.90f, 0.10f);
    public static readonly Color RoadHigh = new(0.95f, 0.10f, 0.10f);

    public static readonly Color PedLow = new(0.20f, 0.70f, 1.00f);
    public static readonly Color PedMid = new(1.00f, 0.90f, 0.10f);
    public static readonly Color PedHigh = new(0.95f, 0.10f, 0.10f);

    public static ShaderMaterial CreateMaterial(Shader shader, Texture2D data, bool peds, float heatDist)
    {
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("heat_tex", data);
        mat.SetShaderParameter("heat_dist", heatDist);
        mat.SetShaderParameter("heat_force", 0f);
        mat.SetShaderParameter("c_low", peds ? PedLow : RoadLow);
        mat.SetShaderParameter("c_mid", peds ? PedMid : RoadMid);
        mat.SetShaderParameter("c_high", peds ? PedHigh : RoadHigh);
        return mat;
    }
}
