using System;

namespace Sim.Headless.Render;

/// <summary>
/// Отображение мир (X, Z, м) -> экран (px). Равномерный масштаб, вид центрируется в кадре;
/// ось Z направлена вниз экрана (вид сверху, как в клиенте), направление (cos h, sin h) сохраняется.
/// </summary>
public readonly struct Viewport
{
    public Viewport(float x0, float z0, float x1, float z1, int width, int height)
    {
        float vw = x1 - x0;
        float vh = z1 - z0;
        if (!(vw > 0f) || !(vh > 0f))
        {
            throw new ArgumentException("view rectangle must have positive size");
        }

        Scale = MathF.Min(width / vw, height / vh);
        OffsetX = ((width - (vw * Scale)) * 0.5f) - (x0 * Scale);
        OffsetY = ((height - (vh * Scale)) * 0.5f) - (z0 * Scale);
    }

    /// <summary>Пикселей на метр.</summary>
    public float Scale { get; }

    public float OffsetX { get; }

    public float OffsetY { get; }

    public float ToScreenX(float worldX) => (worldX * Scale) + OffsetX;

    public float ToScreenY(float worldZ) => (worldZ * Scale) + OffsetY;
}
