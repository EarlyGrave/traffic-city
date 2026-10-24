using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Gen;

/// <summary>Результат генерации зон: <c>ZoneData[]</c> + <c>ZoneLanes[]</c> (core-api §6.6). Выделяется один раз.</summary>
internal sealed class GenZones
{
    public readonly ZoneData[] Zones;
    public readonly int[] ZoneLanes;

    public GenZones(int maxZones, int maxLanes)
    {
        Zones = new ZoneData[Math.Max(0, maxZones)];
        ZoneLanes = new int[Math.Max(0, maxLanes)];
    }

    public int ZoneCount { get; internal set; }

    public int ZoneLaneCount { get; internal set; }
}

internal struct ZoneGenResult
{
    public int ZoneCount, InternalZones, ExternalZones, SpawnLanesTotal, EmptyZones;
}

/// <summary>Зоны — источники и стоки поездок (city-generator §5). PRNG: стрим Zones, по одному вызову на зону.</summary>
internal static class ZoneGenerator
{
    public static CreateStatus Generate(
        in DemandDesc demand,
        float width,
        float height,
        NetworkStore net,
        GenZones zones,
        ref Pcg32 rng,
        out ZoneGenResult result)
    {
        result = default;
        float zoneM = demand.ZoneSize;
        if (!(zoneM > 0f) || !(width > 0f) || !(height > 0f))
        {
            return CreateStatus.InvalidDemand;
        }

        int zx = (int)MathF.Ceiling(width / zoneM);
        int zz = (int)MathF.Ceiling(height / zoneM);
        if (zx < 1 || zz < 1)
        {
            return CreateStatus.InvalidDemand;
        }

        long total = ((long)zx * zz) + (2L * (zx + zz));
        if (total > zones.Zones.Length)
        {
            return CreateStatus.InvalidDemand;
        }

        int zoneCount = (int)total;
        int internalCount = zx * zz;
        float cx = width / zx;
        float cz = height / zz;
        float halfW = width * 0.5f;
        float halfH = height * 0.5f;

        // ---- 5.1 разбиение ----
        for (int kz = 0; kz < zz; kz++)
        {
            for (int kx = 0; kx < zx; kx++)
            {
                zones.Zones[(kz * zx) + kx] = new ZoneData
                {
                    Center = new Vec2((-halfW) + ((kx + 0.5f) * cx), (-halfH) + ((kz + 0.5f) * cz)),
                    HalfSize = cx * 0.5f,
                    IsExternal = 0,
                };
            }
        }

        int south = internalCount;
        int east = south + zx;
        int north = east + zz;
        int west = north + zx;
        for (int k = 0; k < zx; k++)
        {
            float x = (-halfW) + ((k + 0.5f) * cx);
            zones.Zones[south + k] = new ZoneData { Center = new Vec2(x, -halfH), HalfSize = cx * 0.5f, IsExternal = 1 };
            zones.Zones[north + k] = new ZoneData { Center = new Vec2(x, halfH), HalfSize = cx * 0.5f, IsExternal = 1 };
        }

        for (int k = 0; k < zz; k++)
        {
            float z = (-halfH) + ((k + 0.5f) * cz);
            zones.Zones[east + k] = new ZoneData { Center = new Vec2(halfW, z), HalfSize = cz * 0.5f, IsExternal = 1 };
            zones.Zones[west + k] = new ZoneData { Center = new Vec2(-halfW, z), HalfSize = cz * 0.5f, IsExternal = 1 };
        }

        // ---- 5.4 зона узла; классификация края ----
        int nodeCount = net.NodeCount;
        byte[] cornerOrSide = new byte[nodeCount]; // 0 внутренний, 1 граничный неугловой, 2 угловой
        for (int n = 0; n < nodeCount; n++)
        {
            Vec2 pos = net.Nodes[n].Position;
            bool bx = MathF.Abs(pos.X) >= halfW - 0.5f;
            bool bz = MathF.Abs(pos.Z) >= halfH - 0.5f;
            int kx = Clamp((int)MathF.Floor((pos.X + halfW) / cx), zx - 1);
            int kz = Clamp((int)MathF.Floor((pos.Z + halfH) / cz), zz - 1);
            int zone;
            if (bx && bz)
            {
                cornerOrSide[n] = 2;
                zone = pos.Z < 0f ? south + kx : north + kx;
            }
            else if (bz)
            {
                cornerOrSide[n] = 1;
                zone = pos.Z < 0f ? south + kx : north + kx;
            }
            else if (bx)
            {
                cornerOrSide[n] = 1;
                zone = pos.X < 0f ? west + kz : east + kz;
            }
            else
            {
                zone = (kz * zx) + kx;
            }

            net.Nodes[n].Zone = zone;
        }

        // ---- 5.2 полосы спавна ----
        // Порядок в ZoneLanes — по индексам зон (внутренние, затем внешние), внутри зоны — по возрастанию полосы.
        int[] cnt = new int[zoneCount];
        int[] cntStreet = new int[internalCount];
        int[] cntArt = new int[internalCount];
        byte[] taken = new byte[net.LaneCount];
        int laneCap = zones.ZoneLanes.Length;

        // внешние (первыми «забирают» свои полосы): счёт и пометка
        for (int n = 0; n < nodeCount; n++)
        {
            if (cornerOrSide[n] != 1)
            {
                continue;
            }

            int seg = EntrySegment(net, n, halfW, halfH);
            if (seg < 0)
            {
                continue;
            }

            EntryLanes(net, n, seg, out int first, out int num);
            cnt[net.Nodes[n].Zone] += num;
            for (int l = 0; l < num; l++)
            {
                taken[first + l] = 1;
            }
        }

        // внутренние: Street-полосы клетки, иначе Arterial
        for (int s = 0; s < net.SegmentCount; s++)
        {
            int zone = SegmentZone(net, s, halfW, halfH, cx, cz, zx, zz);
            ref readonly SegmentData sd = ref net.Segments[s];
            int free = FreeLanes(in sd, taken);
            if (sd.Kind == RoadKind.Street)
            {
                cntStreet[zone] += free;
            }
            else
            {
                cntArt[zone] += free;
            }
        }

        for (int z = 0; z < internalCount; z++)
        {
            cnt[z] = cntStreet[z] > 0 ? cntStreet[z] : cntArt[z];
        }

        int acc = 0;
        for (int z = 0; z < zoneCount; z++)
        {
            zones.Zones[z].FirstSpawnLane = acc;
            zones.Zones[z].SpawnLaneCount = cnt[z];
            acc += cnt[z];
            cnt[z] = 0;
        }

        if (acc > laneCap)
        {
            return CreateStatus.InvalidDemand;
        }

        for (int n = 0; n < nodeCount; n++)
        {
            if (cornerOrSide[n] != 1)
            {
                continue;
            }

            int seg = EntrySegment(net, n, halfW, halfH);
            if (seg < 0)
            {
                continue;
            }

            EntryLanes(net, n, seg, out int first, out int num);
            int zone = net.Nodes[n].Zone;
            for (int l = 0; l < num; l++)
            {
                zones.ZoneLanes[zones.Zones[zone].FirstSpawnLane + cnt[zone]] = first + l;
                cnt[zone]++;
            }
        }

        for (int z = internalCount; z < zoneCount; z++)
        {
            SortRange(zones.ZoneLanes, zones.Zones[z].FirstSpawnLane, cnt[z]);
        }

        for (int s = 0; s < net.SegmentCount; s++)
        {
            int zone = SegmentZone(net, s, halfW, halfH, cx, cz, zx, zz);
            ref readonly SegmentData sd = ref net.Segments[s];
            if ((sd.Kind == RoadKind.Street) != (cntStreet[zone] > 0))
            {
                continue;
            }

            int baseIdx = zones.Zones[zone].FirstSpawnLane;
            for (int dir = 0; dir < 2; dir++)
            {
                int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
                int num = dir == 0 ? sd.LanesAB : sd.LanesBA;
                for (int l = 0; l < num; l++)
                {
                    if (taken[first + l] == 0)
                    {
                        zones.ZoneLanes[baseIdx + cnt[zone]] = first + l;
                        cnt[zone]++;
                    }
                }
            }
        }

        int accI = acc;

        AssignHubs(net, zones, zoneCount);

        // ---- 5.3 веса: ровно один вызов на зону, в порядке индексов ----
        float jit = demand.WeightJitter;
        int empty = 0;
        for (int z = 0; z < zoneCount; z++)
        {
            float u = rng.NextRange(-jit, jit);
            int lanes = zones.Zones[z].SpawnLaneCount;
            if (lanes == 0)
            {
                empty++;
            }

            if (z < internalCount)
            {
                zones.Zones[z].Weight = lanes > 0 ? 1f + u : 0f;
            }
            else
            {
                zones.Zones[z].Weight = lanes * (1f + u);
            }
        }

        zones.ZoneCount = zoneCount;
        zones.ZoneLaneCount = accI;
        net.ZoneCount = zoneCount;
        result.ZoneCount = zoneCount;
        result.InternalZones = internalCount;
        result.ExternalZones = zoneCount - internalCount;
        result.SpawnLanesTotal = accI;
        result.EmptyZones = empty;
        return CreateStatus.Ok;
    }

    /// <summary>HubNode: ближайший к центру зоны узел среди узлов зоны (ничья — меньший индекс); пустая зона — ближайший по сети.</summary>
    private static void AssignHubs(NetworkStore net, GenZones zones, int zoneCount)
    {
        int nodeCount = net.NodeCount;
        int[] start = new int[zoneCount + 1];
        for (int n = 0; n < nodeCount; n++)
        {
            start[net.Nodes[n].Zone + 1]++;
        }

        for (int z = 0; z < zoneCount; z++)
        {
            start[z + 1] += start[z];
        }

        int[] fill = new int[zoneCount];
        int[] byZone = new int[nodeCount];
        for (int n = 0; n < nodeCount; n++)
        {
            int z = net.Nodes[n].Zone;
            byZone[start[z] + fill[z]++] = n;
        }

        for (int z = 0; z < zoneCount; z++)
        {
            Vec2 c = zones.Zones[z].Center;
            int best = -1;
            float bestD = float.MaxValue;
            if (start[z + 1] > start[z])
            {
                for (int k = start[z]; k < start[z + 1]; k++)
                {
                    NearestStep(net, byZone[k], c, ref best, ref bestD);
                }
            }
            else
            {
                for (int n = 0; n < nodeCount; n++)
                {
                    NearestStep(net, n, c, ref best, ref bestD);
                }
            }

            zones.Zones[z].HubNode = best;
        }
    }

    private static void NearestStep(NetworkStore net, int n, Vec2 c, ref int best, ref float bestD)
    {
        Vec2 p = net.Nodes[n].Position;
        float dx = p.X - c.X;
        float dz = p.Z - c.Z;
        float d = (dx * dx) + (dz * dz);
        if (d < bestD || (d == bestD && n < best))
        {
            bestD = d;
            best = n;
        }
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : (v > max ? max : v);

    private static bool IsBorder(Vec2 p, float halfW, float halfH) =>
        MathF.Abs(p.X) >= halfW - 0.5f || MathF.Abs(p.Z) >= halfH - 0.5f;

    /// <summary>Въездной сегмент граничного узла: единственный рукав, второй конец которого не граничный.</summary>
    private static int EntrySegment(NetworkStore net, int n, float halfW, float halfH)
    {
        ref readonly NodeData nd = ref net.Nodes[n];
        for (int k = 0; k < nd.ArmCount; k++)
        {
            int seg = net.NodeArms[nd.FirstArm + k];
            ref readonly SegmentData sd = ref net.Segments[seg];
            int other = sd.NodeA == n ? sd.NodeB : sd.NodeA;
            if (!IsBorder(net.Nodes[other].Position, halfW, halfH))
            {
                return seg;
            }
        }

        return -1;
    }

    /// <summary>Полосы въездного сегмента, направленные от граничного узла.</summary>
    private static void EntryLanes(NetworkStore net, int n, int seg, out int first, out int num)
    {
        ref readonly SegmentData sd = ref net.Segments[seg];
        if (sd.NodeA == n)
        {
            first = sd.FirstLaneAB;
            num = sd.LanesAB;
        }
        else
        {
            first = sd.FirstLaneBA;
            num = sd.LanesBA;
        }
    }

    private static int SegmentZone(NetworkStore net, int s, float halfW, float halfH, float cx, float cz, int zx, int zz)
    {
        ref readonly SegmentData sd = ref net.Segments[s];
        Vec2 a = net.Nodes[sd.NodeA].Position;
        Vec2 b = net.Nodes[sd.NodeB].Position;
        float xm = (a.X + b.X) * 0.5f;
        float zm = (a.Z + b.Z) * 0.5f;
        int kx = Clamp((int)MathF.Floor((xm + halfW) / cx), zx - 1);
        int kz = Clamp((int)MathF.Floor((zm + halfH) / cz), zz - 1);
        return (kz * zx) + kx;
    }

    private static int FreeLanes(in SegmentData sd, byte[] taken)
    {
        int free = 0;
        for (int l = 0; l < sd.LanesAB; l++)
        {
            if (taken[sd.FirstLaneAB + l] == 0)
            {
                free++;
            }
        }

        for (int l = 0; l < sd.LanesBA; l++)
        {
            if (taken[sd.FirstLaneBA + l] == 0)
            {
                free++;
            }
        }

        return free;
    }

    private static void SortRange(int[] a, int start, int count)
    {
        for (int i = 1; i < count; i++)
        {
            int v = a[start + i];
            int j = i - 1;
            while (j >= 0 && a[start + j] > v)
            {
                a[start + j + 1] = a[start + j];
                j--;
            }

            a[start + j + 1] = v;
        }
    }
}
