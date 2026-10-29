using System;
using System.Runtime.InteropServices;
using Sim.Core.Util;

namespace Sim.Core.Terrain;

/// <summary>
/// Раздел 14 хэша состояния (terrain.md §8): только при рельефе. Геометрия сетки, <c>Version</c>, <c>OpSeq</c>, <c>XxHash64</c> байтов <c>Ground</c>, <c>Water</c>,
/// <c>WaterKind</c> (три <c>ulong</c>), затем <c>Elevation</c> живых узлов по индексу. 0 Б; O(отсчётов) - считают тесты и <c>compare</c>, не тик.
/// </summary>
internal static class TerrainHash
{
    public static void Add(ref Fnv1a64 h, World w)
    {
        TerrainStore t = w.Terrain!;
        h.Add(t.CellMm);
        h.Add(t.OriginX);
        h.Add(t.OriginZ);
        h.Add(t.SamplesX);
        h.Add(t.SamplesZ);
        h.Add(t.BaseCm);
        h.Add(t.Version);
        h.Add(t.OpSeq);
        h.Add(XxHash64.Hash(MemoryMarshal.AsBytes(new ReadOnlySpan<ushort>(t.Ground))));
        h.Add(XxHash64.Hash(MemoryMarshal.AsBytes(new ReadOnlySpan<ushort>(t.Water))));
        h.Add(XxHash64.Hash(t.WaterKind));
        Network.NetworkStore net = w.Net;
        for (int i = 0; i < net.NodeCount; i++)
        {
            if (net.NodePool.IsAlive(i))
            {
                h.Add(net.Nodes[i].Elevation);
            }
        }
    }
}
