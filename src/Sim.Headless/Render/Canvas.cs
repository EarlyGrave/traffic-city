using System;

namespace Sim.Headless.Render;

/// <summary>RGB-цвет 0..255.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// Софтверный растр RGB24 (3 байта на пиксель, строки сверху вниз). Пиксель (i, j) покрывает [i, i+1) x [j, j+1);
/// фигура закрашивает пиксель, если его центр внутри фигуры.
/// </summary>
public sealed class Canvas
{
    public Canvas(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "canvas size must be positive");
        }

        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 3)];
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public void Clear(Rgb c)
    {
        byte[] p = Pixels;
        for (int i = 0; i < p.Length; i += 3)
        {
            p[i] = c.R;
            p[i + 1] = c.G;
            p[i + 2] = c.B;
        }
    }

    public void CopyFrom(byte[] source) => Buffer.BlockCopy(source, 0, Pixels, 0, Pixels.Length);

    public void SetPixel(int x, int y, Rgb c)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        int o = ((y * Width) + x) * 3;
        Pixels[o] = c.R;
        Pixels[o + 1] = c.G;
        Pixels[o + 2] = c.B;
    }

    public Rgb GetPixel(int x, int y)
    {
        int o = ((y * Width) + x) * 3;
        return new Rgb(Pixels[o], Pixels[o + 1], Pixels[o + 2]);
    }

    /// <summary>Выпуклый четырёхугольник (любой обход вершин).</summary>
    public void FillConvexQuad(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3, Rgb c)
    {
        float minY = MathF.Min(MathF.Min(y0, y1), MathF.Min(y2, y3));
        float maxY = MathF.Max(MathF.Max(y0, y1), MathF.Max(y2, y3));
        int yStart = Math.Max(0, (int)MathF.Ceiling(minY - 0.5f));
        int yEnd = Math.Min(Height - 1, (int)MathF.Ceiling(maxY - 0.5f) - 1);
        Span<float> xs = stackalloc float[4] { x0, x1, x2, x3 };
        Span<float> ys = stackalloc float[4] { y0, y1, y2, y3 };
        for (int y = yStart; y <= yEnd; y++)
        {
            float yc = y + 0.5f;
            float lo = float.PositiveInfinity;
            float hi = float.NegativeInfinity;
            for (int e = 0; e < 4; e++)
            {
                int n = (e + 1) & 3;
                float ya = ys[e];
                float yb = ys[n];
                if (ya == yb)
                {
                    continue;
                }

                float top = MathF.Min(ya, yb);
                float bot = MathF.Max(ya, yb);
                if (yc < top || yc >= bot)
                {
                    continue;
                }

                float x = xs[e] + ((yc - ya) * (xs[n] - xs[e]) / (yb - ya));
                lo = MathF.Min(lo, x);
                hi = MathF.Max(hi, x);
            }

            if (lo > hi)
            {
                continue;
            }

            int xa = Math.Max(0, (int)MathF.Ceiling(lo - 0.5f));
            int xb = Math.Min(Width - 1, (int)MathF.Ceiling(hi - 0.5f) - 1);
            int o = ((y * Width) + xa) * 3;
            byte[] p = Pixels;
            for (int x = xa; x <= xb; x++)
            {
                p[o++] = c.R;
                p[o++] = c.G;
                p[o++] = c.B;
            }
        }
    }

    /// <summary>Линия заданной толщины (в пикселях), плоские торцы. Толщина не менее 1.2 px, чтобы не рваться.</summary>
    public void FillThickLine(float x0, float y0, float x1, float y1, float thickness, Rgb c)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float len = MathF.Sqrt((dx * dx) + (dy * dy));
        float half = MathF.Max(thickness, 1.2f) * 0.5f;
        if (len < 1e-4f)
        {
            FillCircle(x0, y0, half, c);
            return;
        }

        float nx = -dy / len * half;
        float ny = dx / len * half;
        FillConvexQuad(x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny, c);
    }

    /// <summary>Прямоугольник length x width (px), длинная ось вдоль единичного направления (dirX, dirY).</summary>
    public void FillOrientedRect(float cx, float cy, float dirX, float dirY, float length, float width, Rgb c)
    {
        float hl = length * 0.5f;
        float hw = width * 0.5f;
        float ux = dirX * hl;
        float uy = dirY * hl;
        float vx = -dirY * hw;
        float vy = dirX * hw;
        FillConvexQuad(
            cx + ux + vx, cy + uy + vy,
            cx + ux - vx, cy + uy - vy,
            cx - ux - vx, cy - uy - vy,
            cx - ux + vx, cy - uy + vy,
            c);
    }

    public void FillCircle(float cx, float cy, float r, Rgb c)
    {
        int yStart = Math.Max(0, (int)MathF.Ceiling(cy - r - 0.5f));
        int yEnd = Math.Min(Height - 1, (int)MathF.Floor(cy + r - 0.5f));
        for (int y = yStart; y <= yEnd; y++)
        {
            float dy = y + 0.5f - cy;
            float rem = (r * r) - (dy * dy);
            if (rem < 0f)
            {
                continue;
            }

            float w = MathF.Sqrt(rem);
            int xa = Math.Max(0, (int)MathF.Ceiling(cx - w - 0.5f));
            int xb = Math.Min(Width - 1, (int)MathF.Floor(cx + w - 0.5f));
            for (int x = xa; x <= xb; x++)
            {
                SetPixel(x, y, c);
            }
        }
    }

    /// <summary>Текст шрифтом 5x7, увеличенным в <paramref name="scale"/> раз; (x, y) - левый верхний угол.</summary>
    public void DrawText(int x, int y, string text, Rgb c, int scale = 1)
    {
        int cursor = x;
        foreach (char ch in text)
        {
            ReadOnlySpan<byte> rows = PixelFont.Glyph(ch);
            for (int r = 0; r < PixelFont.GlyphHeight; r++)
            {
                for (int col = 0; col < PixelFont.GlyphWidth; col++)
                {
                    if (((rows[r] >> (PixelFont.GlyphWidth - 1 - col)) & 1) == 0)
                    {
                        continue;
                    }

                    for (int sy = 0; sy < scale; sy++)
                    {
                        for (int sx = 0; sx < scale; sx++)
                        {
                            SetPixel(cursor + (col * scale) + sx, y + (r * scale) + sy, c);
                        }
                    }
                }
            }

            cursor += (PixelFont.GlyphWidth + 1) * scale;
        }
    }

    public static int TextWidth(string text, int scale = 1) =>
        text.Length == 0 ? 0 : ((text.Length * (PixelFont.GlyphWidth + 1)) - 1) * scale;
}
