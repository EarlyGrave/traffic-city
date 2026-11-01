using System;
using Sim.Core.Api;

namespace Sim.Core.Terrain;

/// <summary>
/// Кольцо грязных прямоугольников чанков для клиента (terrain.md §10.1): 256 записей <see cref="TerrainDirtyRect"/>, старые вытесняются.
/// <see cref="DroppedVersion"/> - наибольшая версия вытесненной записи: клиент с <c>sinceVersion</c> меньше неё потерял грязь и перестраивает всё.
/// Производное: не в сейве и не в хэше; после загрузки <see cref="Reset"/> с текущей версией (клиент старше сейва перестраивает всё).
/// </summary>
internal sealed class TerrainDirtyRing
{
    public const int Capacity = 256;

    private readonly TerrainDirtyRect[] _r = new TerrainDirtyRect[Capacity];
    private int _head;     // следующая запись
    private int _count;

    public int DroppedVersion { get; private set; }

    public int Count => _count;

    public void Add(int version, ushort cx0, ushort cz0, ushort cx1, ushort cz1)
    {
        if (_count == Capacity)
        {
            DroppedVersion = Math.Max(DroppedVersion, _r[_head].Version);
        }
        else
        {
            _count++;
        }

        _r[_head] = new TerrainDirtyRect { Version = version, ChunkX0 = cx0, ChunkZ0 = cz0, ChunkX1 = cx1, ChunkZ1 = cz1 };
        _head = (_head + 1) & (Capacity - 1);
    }

    /// <summary>
    /// Записи с <c>Version &gt; sinceVersion</c> от старых к новым. <paramref name="overflow"/> = true: кольцо потеряло нужные записи или они не влезли
    /// в <paramref name="dst"/> - перестроить всё.
    /// </summary>
    public int CopySince(Span<TerrainDirtyRect> dst, int sinceVersion, out bool overflow)
    {
        overflow = sinceVersion < DroppedVersion;
        int n = 0;
        for (int k = 0; k < _count; k++)
        {
            ref readonly TerrainDirtyRect r = ref _r[(_head - _count + k) & (Capacity - 1)];
            if (r.Version <= sinceVersion)
            {
                continue;
            }

            if (n == dst.Length)
            {
                overflow = true;
                break;
            }

            dst[n++] = r;
        }

        return n;
    }

    /// <summary>Очистить; клиенты с версией меньше <paramref name="version"/> получат overflow.</summary>
    public void Reset(int version)
    {
        _head = 0;
        _count = 0;
        DroppedVersion = version;
    }
}
