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

    private const int Bins = 64;
    private const float PedMinTop = 2f;      // нижняя граница верха шкалы пешеходов: пешеходов на 20 м ребра
    private const float PedMaxDensity = 32f; // верх гистограммы плотности

    private readonly int[] _bins = new int[Bins];
    private float _pedTop = PedMinTop;

    public Texture2D Texture => _texture;

    /// <summary>Дороги: средняя загрузка (0..1) по сегментам с машинами, как в шейдере: max(1 - SpeedRatio, min(Density/120 * 0.6, 1)).</summary>
    public float RoadAvgLoad { get; private set; }

    /// <summary>Дороги: 95-й перцентиль загрузки (0..1) по сегментам с машинами.</summary>
    public float RoadP95 { get; private set; }

    /// <summary>Дороги: сегментов с машинами.</summary>
    public int RoadsWithCars { get; private set; }

    /// <summary>Пешеходы: верх шкалы = 95-й перцентиль плотности (пешеходов на 20 м ребра), не ниже минимума; плавно догоняет цель.</summary>
    public float PedTop => _pedTop;

    /// <summary>Пешеходы: всего на рёбрах.</summary>
    public int PedTotal { get; private set; }

    /// <summary>Пешеходы: рёбер с пешеходами.</summary>
    public int PedEdges { get; private set; }

    /// <summary>Нормированная загрузка сегмента (0..1) - формула шейдера <c>road.gdshader</c>.</summary>
    public static float RoadLoad(in SegmentLoad l) => MathF.Max(1f - l.SpeedRatio, MathF.Min(l.Density / 120f * 0.6f, 1f));

    public int Items => Width * _height;

    public void UpdateRoads(ReadOnlySpan<SegmentLoad> loads)
    {
        int n = Math.Min(loads.Length, Items);
        Array.Clear(_bins);
        float sum = 0f;
        int cars = 0;
        for (int i = 0; i < n; i++)
        {
            ref readonly SegmentLoad l = ref loads[i];
            if (l.Count > 0)
            {
                float t = Math.Clamp(RoadLoad(in l), 0f, 1f);
                sum += t;
                cars++;
                _bins[Math.Min((int)(t * Bins), Bins - 1)]++;
            }

            int o = i * 4;
            _data[o] = Q(l.SpeedRatio);
            _data[o + 1] = Q(l.Density / 120f);
            _data[o + 2] = l.Count > 0 ? (byte)255 : (byte)0;
            _data[o + 3] = 255;
        }

        Array.Clear(_data, n * 4, _data.Length - (n * 4));
        RoadsWithCars = cars;
        RoadAvgLoad = cars > 0 ? sum / cars : 0f;
        RoadP95 = Percentile95(cars) / Bins;
        Commit();
    }

    /// <param name="counts">Пешеходов на ребре (<c>PedEdgeCounts</c>).</param>
    /// <param name="edgeLenM">Длина ребра, м (индекс = индекс ребра).</param>
    public void UpdatePeds(ReadOnlySpan<ushort> counts, ReadOnlySpan<float> edgeLenM)
    {
        int n = Math.Min(Math.Min(counts.Length, edgeLenM.Length), Items);

        // верх шкалы адаптивный (ui-spec.md §8): 95-й перцентиль плотности рёбер с пешеходами, иначе один затор обесцвечивает всё остальное
        Array.Clear(_bins);
        int total = 0;
        int edges = 0;
        for (int e = 0; e < n; e++)
        {
            ushort c = counts[e];
            if (c > 0)
            {
                total += c;
                edges++;
                float d = c * 20f / MathF.Max(edgeLenM[e], 5f);
                _bins[Math.Min((int)(d / PedMaxDensity * Bins), Bins - 1)]++;
            }
        }

        PedTotal = total;
        PedEdges = edges;
        float target = MathF.Max(PedMinTop, Percentile95(edges) / Bins * PedMaxDensity);
        _pedTop += (target - _pedTop) * 0.5f;
        float invTop = 1f / _pedTop;
        for (int e = 0; e < n; e++)
        {
            int o = e * 4;
            ushort c = counts[e];
            _data[o] = c > 0 ? Q(c * 20f / MathF.Max(edgeLenM[e], 5f) * invTop) : (byte)0;
            _data[o + 1] = 0;
            _data[o + 2] = c > 0 ? (byte)255 : (byte)0;
            _data[o + 3] = 255;
        }

        Array.Clear(_data, n * 4, _data.Length - (n * 4));
        Commit();
    }

    /// <summary>Верхняя граница корзины 95-го перцентиля, в корзинах (0..Bins).</summary>
    private float Percentile95(int count)
    {
        if (count <= 0)
        {
            return 0f;
        }

        int need = (int)MathF.Ceiling(count * 0.95f);
        int acc = 0;
        for (int i = 0; i < Bins; i++)
        {
            acc += _bins[i];
            if (acc >= need)
            {
                return i + 1;
            }
        }

        return Bins;
    }

    private static byte Q(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f);

    private void Commit()
    {
        _image.SetData(Width, _height, false, Image.Format.Rgba8, _data);
        _texture.Update(_image);
    }
}
