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
        Colorize(mat, peds, Mode);
        Tracked.Add((mat, peds));
        return mat;
    }

    /// <summary>Режим палитры для дальтоников (UI12): применяется ко всем созданным материалам и к новым.</summary>
    public static TrafficCity.Ui.ColorblindMode Mode { get; private set; }

    private static readonly System.Collections.Generic.List<(ShaderMaterial Mat, bool Peds)> Tracked = new();

    public static void SetMode(TrafficCity.Ui.ColorblindMode mode)
    {
        Mode = mode;
        Tracked.RemoveAll(t => !GodotObject.IsInstanceValid(t.Mat));
        foreach ((ShaderMaterial mat, bool peds) in Tracked)
        {
            Colorize(mat, peds, mode);
        }
    }

    private static void Colorize(ShaderMaterial mat, bool peds, TrafficCity.Ui.ColorblindMode mode)
    {
        (Color low, Color mid, Color high) = peds ? Palette.HeatPed(mode) : Palette.HeatRoad(mode);
        mat.SetShaderParameter("c_low", low);
        mat.SetShaderParameter("c_mid", mid);
        mat.SetShaderParameter("c_high", high);
    }
}
