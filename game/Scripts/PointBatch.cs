using Godot;

namespace TrafficCity;

/// <summary>
/// Батч точек (docs/design/render.md §3.4): один <see cref="BoxBatch"/> горизонтальных квадов 1x1 с шейдером <c>points.gdshader</c>.
/// Масштаб инстанса = истинный размер агента (длина, 1, ширина); шейдер не даёт точке стать меньше <c>k_px</c> пикселей.
/// Unshaded, без теней, цвет инстанса (выделение, stuck, тип) - как у ближних уровней.
/// </summary>
public static class PointBatch
{
    public const string ShaderPath = "res://Shaders/points.gdshader";

    public static BoxBatch Create(string name, float minPx)
    {
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        mat.SetShaderParameter("k_px", minPx);
        return new BoxBatch(name, new PlaneMesh { Size = Vector2.One }, mat);
    }
}
