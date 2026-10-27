using Godot;

namespace TrafficCity;

/// <summary>
/// Процедурные боксы для шейдерных рёбер (docs/design/visual-style.md §3): 24 вершины (по 4 на грань), 12 треугольников на бокс
/// (как <c>BoxMesh</c>), но <c>UV</c> каждой грани - свои (u, v) ∈ [0, 1], а не атлас 3x2 у <c>BoxMesh</c>: шейдер считает расстояние до ребра
/// грани в пикселях как <c>min(u, 1 - u) / fwidth(u)</c>. На боковых гранях v = 0 снизу, v = 1 сверху. Общий для ТС (VS2) и зданий (VS5).
/// Лицевая сторона по часовой стрелке (соглашение Godot), нормали наружу.
/// </summary>
public static class BoxBuilder
{
    /// <summary>Добавить бокс размера <paramref name="size"/> с центром <paramref name="center"/> в <paramref name="st"/> (открыт как Triangles).</summary>
    public static void Append(SurfaceTool st, Vector3 size, Vector3 center)
    {
        Vector3 h = size * 0.5f;
        // нормаль, ось u, ось v: u x v = нормаль (правая тройка), v - вверх на боковых гранях
        Face(st, center, h, Vector3.Right, Vector3.Forward, Vector3.Up);
        Face(st, center, h, Vector3.Left, Vector3.Back, Vector3.Up);
        Face(st, center, h, Vector3.Back, Vector3.Right, Vector3.Up);
        Face(st, center, h, Vector3.Forward, Vector3.Left, Vector3.Up);
        Face(st, center, h, Vector3.Up, Vector3.Back, Vector3.Right);
        Face(st, center, h, Vector3.Down, Vector3.Right, Vector3.Back);
    }

    /// <summary>Один бокс как меш.</summary>
    public static ArrayMesh Box(Vector3 size, Vector3 center)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Append(st, size, center);
        return st.Commit();
    }

    private static void Face(SurfaceTool st, Vector3 c, Vector3 h, Vector3 n, Vector3 u, Vector3 v)
    {
        Vector3 o = c + (n * h);
        Vector3 hu = u * h;
        Vector3 hv = v * h;
        // углы (u, v) = (0,0), (1,0), (1,1), (0,1): u, v - единичные оси со знаком, h - полуразмеры
        Vector3 p00 = o - hu - hv;
        Vector3 p10 = o + hu - hv;
        Vector3 p11 = o + hu + hv;
        Vector3 p01 = o - hu + hv;
        // против часовой (00, 10, 11), (00, 11, 01); лицевая в Godot - по часовой, то есть обратный порядок
        Vert(st, n, p00, 0f, 0f);
        Vert(st, n, p11, 1f, 1f);
        Vert(st, n, p10, 1f, 0f);
        Vert(st, n, p00, 0f, 0f);
        Vert(st, n, p01, 0f, 1f);
        Vert(st, n, p11, 1f, 1f);
    }

    private static void Vert(SurfaceTool st, Vector3 n, Vector3 p, float u, float v)
    {
        st.SetNormal(n);
        st.SetUV(new Vector2(u, v));
        st.AddVertex(p);
    }
}
