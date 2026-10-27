using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>
/// Тон кварталов по зоне участков (VS9, visual-style.md §8): квадратная подложка на участок, добавляется в те же меши чанков слоя земли
/// (<see cref="LandUseChunks"/>), поэтому лишних draw calls нет. Участки есть только при <c>demand.model = residents</c> (иначе <c>LotCount = 0</c>).
/// Цвета - только из <see cref="Palette"/>: тон основного вида в <c>COLOR</c>, насыщенный цвет слоя «Зоны» в <c>CUSTOM0</c>.
/// </summary>
public static class LotChunks
{
    /// <summary>Сторона подложки, м (участок по умолчанию 40 x 40, зазор до дорог закрывают ленты).</summary>
    public const float PadM = 36f;

    public const float PadY = -0.13f;

    private static LotRenderData[] _buf = Array.Empty<LotRenderData>();

    /// <summary>Дописать подложки участков в <paramref name="map"/>; возвращает число нарисованных.</summary>
    public static int Append(Dictionary<long, MeshBuilder> map, Simulation sim)
    {
        int count = sim.LotCount;
        if (count <= 0)
        {
            return 0;
        }

        if (_buf.Length < count)
        {
            _buf = new LotRenderData[count];
        }

        int n = sim.GetLots(_buf.AsSpan(0, count));
        int drawn = 0;
        float h = PadM * 0.5f;
        for (int i = 0; i < n; i++)
        {
            LotRenderData l = _buf[i];
            if ((l.Flags & LotRenderFlags.Lost) != 0 || !Tones(l.Zone, out Color tone, out Color zone))
            {
                continue;
            }

            MeshBuilder b = ChunkGrid.Get(map, ChunkGrid.Key(l.X, l.Z, RoadChunks.FarCell), 4, true);
            float c = MathF.Cos(l.Heading);
            float s = MathF.Sin(l.Heading);
            int v0 = Corner(b, l, -h, -h, c, s, tone, zone);
            int v1 = Corner(b, l, h, -h, c, s, tone, zone);
            int v2 = Corner(b, l, -h, h, c, s, tone, zone);
            int v3 = Corner(b, l, h, h, c, s, tone, zone);
            b.Quad(v0, v1, v2, v3);
            drawn++;
        }

        return drawn;
    }

    private static int Corner(MeshBuilder b, LotRenderData l, float dx, float dz, float c, float s, Color tone, Color zone) =>
        b.Add(new Vector3(l.X + (dx * c) - (dz * s), PadY, l.Z + (dx * s) + (dz * c)), tone, zone.R, zone.G, zone.B, 0f);

    private static bool Tones(byte zone, out Color tone, out Color overlay)
    {
        switch ((LotZone)zone)
        {
            case LotZone.Residential:
                tone = Palette.LotResidentialTone;
                overlay = Palette.ZoneResidential;
                return true;
            case LotZone.Commercial:
                tone = Palette.LotCommercialTone;
                overlay = Palette.ZoneCommercial;
                return true;
            case LotZone.Industrial:
                tone = Palette.LotIndustrialTone;
                overlay = Palette.ZoneIndustrial;
                return true;
            default:
                tone = default;
                overlay = default;
                return false;
        }
    }
}
