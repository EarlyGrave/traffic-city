using System;
using System.Runtime.InteropServices;
using Sim.Core.Terrain;

namespace Sim.Core.Api;

/// <summary>Грязный прямоугольник чанков 32×32 клетки (включительно), terrain.md §10.1. 12 Б.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TerrainDirtyRect
{
    public int Version;
    public ushort ChunkX0, ChunkZ0, ChunkX1, ChunkZ1;
}

/// <summary>
/// Рельеф для клиента (terrain.md §10.1): только чтение, спаны живут до следующего <c>Step</c>/<c>Load</c>. Без рельефа <see cref="Enabled"/> = false и спаны пусты.
/// Высота отсчёта, м = <c>(BaseCm + Ground[i]) / 100</c>; мировая точка отсчёта <c>(OriginX + ix·CellM, OriginZ + iz·CellM)</c>, <c>i = iz·SamplesX + ix</c>.
/// </summary>
public readonly ref struct TerrainView
{
    public readonly bool Enabled;
    public readonly int Version;
    public readonly float CellM, OriginX, OriginZ;
    public readonly int SamplesX, SamplesZ, BaseCm, ChunkSamples;
    public readonly ReadOnlySpan<ushort> Ground, Water;
    public readonly ReadOnlySpan<byte> WaterKind, Lock;

    internal TerrainView(TerrainStore t)
    {
        Enabled = true;
        Version = t.Version;
        CellM = t.CellM;
        OriginX = t.OriginX;
        OriginZ = t.OriginZ;
        SamplesX = t.SamplesX;
        SamplesZ = t.SamplesZ;
        BaseCm = t.BaseCm;
        ChunkSamples = TerrainStore.ChunkSamples;
        Ground = t.Ground;
        Water = t.Water;
        WaterKind = t.WaterKind;
        Lock = t.Lock;
    }
}

// Рельеф и вода (terrain.md §10.1, ADR-0036): чтение для клиента. Без рельефа - пустой вид, GroundAt = 0, грязи нет.
public sealed partial class Simulation
{
    /// <summary>Включён ли рельеф (секция <c>terrain</c> сценария).</summary>
    public bool HasTerrain => !_disposed && _w.Terrain != null;

    /// <summary>Карта высот и воды; без рельефа - <c>default</c> (Enabled = false).</summary>
    public TerrainView Terrain
    {
        get
        {
            TerrainStore? t = _disposed ? null : _w.Terrain;
            return t == null ? default : new TerrainView(t);
        }
    }

    /// <summary>Высота грунта, м: та же билинейная интерполяция, что в ядре (terrain.md §2.2); без рельефа 0.</summary>
    public float GroundAt(float x, float z)
    {
        TerrainStore? t = _disposed ? null : _w.Terrain;
        return t == null ? 0f : t.GroundAt(x, z);
    }

    /// <summary>
    /// Грязные чанки после версии <paramref name="sinceVersion"/>, от старых к новым. <paramref name="overflow"/> = true - записи потеряны (кольцо 256)
    /// или не влезли в <paramref name="dst"/>: перестроить всё. Без рельефа 0.
    /// </summary>
    public int CopyTerrainDirty(Span<TerrainDirtyRect> dst, int sinceVersion, out bool overflow)
    {
        overflow = false;
        TerrainStore? t = _disposed ? null : _w.Terrain;
        return t == null ? 0 : t.Dirty.CopySince(dst, sinceVersion, out overflow);
    }
}
