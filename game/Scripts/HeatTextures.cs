using System;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>
/// Текстура данных heatmap (docs/design/render.md §3.5): RGBA8 шириной 4096, высота <c>ceil(N / 4096)</c>. Данные читает шейдер дорог/тротуаров
/// по индексу из <c>CUSTOM0.x</c>; обновление ~2 Гц над переиспользуемым <c>byte[]</c>, без пересборки мешей.
/// Дороги: R = <c>SpeedRatio</c>, G = <c>Density/120</c>, B = <c>Count &gt; 0</c>. Рёбра пешеходов: R = плотность (пешеходов на 20 м ребра / 4), B = есть пешеходы.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат шейдерному материалу и освобождаются вместе с ним
public sealed class HeatTexture
{
    public const int Width = 4096;
    public const double UpdateIntervalS = 0.5; // 2 Гц

    private readonly byte[] _data;
    private readonly Image _image;
    private readonly ImageTexture _texture;
    private readonly int _height;

    public HeatTexture(int items)
    {
        _height = Math.Max(1, (items + Width - 1) / Width);
        _data = new byte[Width * _height * 4];
        _image = Image.CreateFromData(Width, _height, false, Image.Format.Rgba8, _data);
        _texture = ImageTexture.CreateFromImage(_image);
    }

    public Texture2D Texture => _texture;

    public int Items => Width * _height;

    public void UpdateRoads(ReadOnlySpan<SegmentLoad> loads)
    {
        int n = Math.Min(loads.Length, Items);
        for (int i = 0; i < n; i++)
        {
            ref readonly SegmentLoad l = ref loads[i];
            int o = i * 4;
            _data[o] = Q(l.SpeedRatio);
            _data[o + 1] = Q(l.Density / 120f);
            _data[o + 2] = l.Count > 0 ? (byte)255 : (byte)0;
            _data[o + 3] = 255;
        }

        Array.Clear(_data, n * 4, _data.Length - (n * 4));
        Commit();
    }

    /// <param name="counts">Пешеходов на ребре (<c>PedEdgeCounts</c>).</param>
    /// <param name="edgeLenM">Длина ребра, м (индекс = индекс ребра).</param>
    public void UpdatePeds(ReadOnlySpan<ushort> counts, ReadOnlySpan<float> edgeLenM)
    {
        int n = Math.Min(Math.Min(counts.Length, edgeLenM.Length), Items);
        for (int e = 0; e < n; e++)
        {
            int o = e * 4;
            ushort c = counts[e];
            _data[o] = c > 0 ? Q(c * 20f / MathF.Max(edgeLenM[e], 5f) / 4f) : (byte)0;
            _data[o + 1] = 0;
            _data[o + 2] = c > 0 ? (byte)255 : (byte)0;
            _data[o + 3] = 255;
        }

        Array.Clear(_data, n * 4, _data.Length - (n * 4));
        Commit();
    }

    private static byte Q(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f);

    private void Commit()
    {
        _image.SetData(Width, _height, false, Image.Format.Rgba8, _data);
        _texture.Update(_image);
    }
}
