using Godot;

namespace TrafficCity;

/// <summary>
/// Палитра heatmap берётся из <see cref="Palette"/>; здесь - сборка материала дорог/тротуаров. Heatmap виден только в информационном режиме
/// (<see cref="InfoMode"/>, F1): слой 1 - дороги, слой 2 - тротуары. Авто-heatmap вдали в основном виде выключен решением владельца
/// 2026-10-27 (visual-style.md §7, §10 п. 1); вернуть - <see cref="AutoFar"/>.
/// </summary>
public static class HeatPalette
{
    /// <summary>Переключатель авто-heatmap дальше дистанции точек в основном виде (render.md §3.5): 0 - выключен.</summary>
    public const float AutoFar = 0f;

    public static ShaderMaterial CreateMaterial(Shader shader, Texture2D data, bool peds, float heatDist)
    {
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("heat_tex", data);
        mat.SetShaderParameter("heat_dist", heatDist);
        mat.SetShaderParameter("heat_force", 0f);
        mat.SetShaderParameter("heat_auto", AutoFar);
        mat.SetShaderParameter("heat_layer", peds ? InfoMode.LayerPeds : InfoMode.LayerTraffic);
        mat.SetShaderParameter("c_low", peds ? Palette.HeatPedLow : Palette.HeatRoadLow);
        mat.SetShaderParameter("c_mid", peds ? Palette.HeatPedMid : Palette.HeatRoadMid);
        mat.SetShaderParameter("c_high", peds ? Palette.HeatPedHigh : Palette.HeatRoadHigh);
        return mat;
    }
}
