using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>Габариты и цвет одного здания: вход ближнего кольца (<c>MultiMesh</c>) и мешей чанков (docs/design/render.md V8).</summary>
public struct BuildingBox
{
    public float X { get; init; }

    public float Z { get; init; }

    public float Heading { get; init; }

    /// <summary>Размер вдоль курса, м.</summary>
    public float W { get; init; }

    /// <summary>Размер поперёк курса, м.</summary>
    public float D { get; init; }

    /// <summary>Высота, м; здание стоит на земле (y = 0).</summary>
    public float H { get; init; }

    public Color Color { get; init; }
}

/// <summary>
/// Процедурные здания-заглушки (render.md V8, пока нет моделей): габариты по зоне и плотности, цвет по палитре, меши чанков LOD2 и бокс-импостора.
/// Все уровни - один и тот же бокс (<see cref="BoxBuilder"/>), поэтому рёбра <c>building.gdshader</c> есть на каждом из них. Без дна: 10 треугольников.
/// </summary>
public static class ChunkBuilder
{
    // (вдоль курса, поперёк, высота), м; участок по умолчанию 40 x 40
    private static readonly Vector3[,] _dims =
    {
        { default, default },                                                    // зона 0: нет
        { new(16f, 14f, 7f), new(24f, 20f, 24f) },                               // R: низкая / высокая плотность
        { new(26f, 18f, 9f), new(28f, 24f, 36f) },                               // C
        { new(30f, 24f, 8f), new(32f, 26f, 14f) },                               // I
    };

    /// <summary>Заполнить <paramref name="box"/> по записи ядра; false - мёртвый слот или неизвестная зона.</summary>
    public static bool TryBox(in BuildingRenderData b, out BuildingBox box) => TryBox(in b, 0f, 0f, out box);

    /// <summary>Отступ здания от границ участка по фасаду и от дороги, м (RP10b).</summary>
    public const float LotSetbackM = 3f;

    /// <summary>
    /// Как <see cref="TryBox(in BuildingRenderData, out BuildingBox)"/>, но в режиме профилей габарит вписан в участок: <paramref name="lotFacadeM"/> (по фасаду, поперёк курса) и
    /// <paramref name="lotDepthM"/> (вглубь, вдоль курса: курс здания - фасадом к дороге) минус отступ <see cref="LotSetbackM"/>; 0 - участок неизвестен (legacy), габарит по зоне.
    /// </summary>
    public static bool TryBox(in BuildingRenderData b, float lotFacadeM, float lotDepthM, out BuildingBox box)
    {
        box = default;
        if (b.Kind < 1 || b.Kind > 3)
        {
            return false;
        }

        // плотность в BuildingRenderData не отдаётся; у пакета base типы идут парами (низкая, высокая) - чётность Type (модам - позже, через visual)
        int density = b.Type & 1;
        Vector3 d = _dims[b.Kind, density];
        uint hx = Hash(b.X, b.Z);
        float j0 = (hx & 0xFFu) / 255f;
        float j1 = ((hx >> 8) & 0xFFu) / 255f;
        float j2 = ((hx >> 16) & 0xFFu) / 255f;
        float j3 = ((hx >> 24) & 0xFFu) / 255f;
        float h = d.Z * (0.85f + (0.3f * j2));
        if (b.State == (byte)BuildingState.Constructing)
        {
            h *= 0.25f + (0.75f * (b.FillPct / 100f));
        }

        Color tone = b.State switch
        {
            (byte)BuildingState.Constructing => Palette.BuildingConstructing,
            (byte)BuildingState.Abandoned => Palette.BuildingAbandoned,
            _ => b.Kind == (byte)LotZone.Residential ? Palette.BuildingResidential : b.Kind == (byte)LotZone.Commercial ? Palette.BuildingCommercial : Palette.BuildingIndustrial,
        };
        float w = d.X * (0.9f + (0.2f * j0));
        float dd = d.Y * (0.9f + (0.2f * j1));
        if (lotDepthM > 0f && lotFacadeM > 0f)
        {
            w = MathF.Min(w, MathF.Max(4f, lotDepthM - LotSetbackM));
            dd = MathF.Min(dd, MathF.Max(4f, lotFacadeM - LotSetbackM));
        }

        float k = 1f + (((j3 * 2f) - 1f) * Palette.BuildingToneJitter);
        box = new BuildingBox
        {
            X = b.X,
            Z = b.Z,
            Heading = b.Heading,
            W = w,
            D = dd,
            H = MathF.Max(h, 1.5f),
            Color = new Color(tone.R * k, tone.G * k, tone.B * k, 1f),
        };
        return true;
    }

    /// <summary>Добавить боксы в меши чанков сетки <paramref name="cell"/> (без дна, нормали и UV граней - для рёбер).</summary>
    public static void AppendChunks(Dictionary<long, MeshBuilder> map, float cell, ReadOnlySpan<BuildingBox> boxes)
    {
        for (int i = 0; i < boxes.Length; i++)
        {
            ref readonly BuildingBox b = ref boxes[i];
            MeshBuilder mb = ChunkGrid.Get(map, ChunkGrid.Key(b.X, b.Z, cell), 1, true, true);
            BoxBuilder.AppendTo(mb, b.X, 0f, b.Z, b.Heading, new Vector3(b.W, b.H, b.D), b.Color, false);
        }
    }

    private static uint Hash(float x, float z)
    {
        uint h = unchecked((uint)BitConverter.SingleToInt32Bits(x) * 0x9E3779B1u);
        h ^= unchecked((uint)BitConverter.SingleToInt32Bits(z) * 0x85EBCA77u);
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        return h;
    }
}
