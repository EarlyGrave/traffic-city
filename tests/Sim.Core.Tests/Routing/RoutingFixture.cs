using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Gen;
using Sim.Core.Util;

namespace Sim.Core.Tests.Routing;

/// <summary>Сгенерированный город + граф маршрутизации поверх него (без World и фаз).</summary>
internal sealed class RoutingRig
{
    public GenRun Run = null!;
    public NetworkStore Net = null!;
    public RouteGraph Graph = null!;
    public int ZoneCount;
}

internal static class RoutingFixture
{
    public static RoutingRig City(string scenario)
    {
        GenRun run = GenFixture.Run(scenario);
        if (run.ZoneStatus != CreateStatus.Ok)
        {
            throw new InvalidOperationException("generation failed: " + run.ZoneStatus);
        }

        NetworkStore net = run.Net;
        Array.Copy(run.Zones.Zones, net.Zones, run.Zones.ZoneCount);
        Array.Copy(run.Zones.ZoneLanes, net.ZoneLanes, run.Zones.ZoneLaneCount);
        net.ZoneCount = run.Zones.ZoneCount;
        net.ZoneLaneCount = run.Zones.ZoneLaneCount;
        var graph = new RouteGraph(net);
        graph.Rebuild();
        return new RoutingRig
        {
            Run = run,
            Net = net,
            Graph = graph,
            ZoneCount = net.ZoneCount,
        };
    }

    /// <summary>Случайная обычная полоса, чей DirSeg имеет исходящие дуги (годится как старт и как цель).</summary>
    public static int RandomLane(RoutingRig r, ref Pcg32 rng)
    {
        while (true)
        {
            int lane = rng.NextInt(0, r.Net.LaneCount);
            ref readonly LaneData ld = ref r.Net.Lanes[lane];
            if (ld.Kind == LaneKind.Normal && ld.Segment >= 0)
            {
                int u = RouteGraph.DirSegOfLane(r.Net, lane);
                if (r.Graph.ArcFirst[u + 1] > r.Graph.ArcFirst[u])
                {
                    return lane;
                }
            }
        }
    }

    /// <summary>Полоса появления зоны (обычная), индекс k по кругу.</summary>
    public static int ZoneLane(RoutingRig r, int zone, int k)
    {
        ref readonly ZoneData z = ref r.Net.Zones[zone];
        if (z.SpawnLaneCount == 0)
        {
            return -1;
        }

        for (int i = 0; i < z.SpawnLaneCount; i++)
        {
            int lane = r.Net.ZoneLanes[z.FirstSpawnLane + ((k + i) % z.SpawnLaneCount)];
            if (r.Net.Lanes[lane].Kind == LaneKind.Normal)
            {
                return lane;
            }
        }

        return -1;
    }

    /// <summary>Эталон: Дейкстра O(V^2) по тем же весам графа. Возвращает стоимость или +inf.</summary>
    public static float Dijkstra(RouteGraph g, int start, float startG, int goal)
    {
        int n = g.DirSegCount;
        var dist = new float[n];
        var done = new bool[n];
        Array.Fill(dist, float.PositiveInfinity);
        dist[start] = startG;
        for (int it = 0; it < n; it++)
        {
            int v = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (!done[i] && dist[i] < best)
                {
                    best = dist[i];
                    v = i;
                }
            }

            if (v < 0)
            {
                break;
            }

            if (v == goal)
            {
                return best;
            }

            done[v] = true;
            for (int a = g.ArcFirst[v]; a < g.ArcFirst[v + 1]; a++)
            {
                int w = g.ArcTo[a];
                float nd = best + g.ArcCost[a] + g.SegCost[w];
                if (nd < dist[w])
                {
                    dist[w] = nd;
                }
            }
        }

        return float.PositiveInfinity;
    }

    /// <summary>Стоимость пути, пересчитанная по дугам графа; NaN, если между соседями нет дуги.</summary>
    public static float PathCost(RouteGraph g, ReadOnlySpan<int> path, float startG)
    {
        float c = startG;
        for (int i = 1; i < path.Length; i++)
        {
            int a = g.FindArc(path[i - 1], path[i]);
            if (a < 0)
            {
                return float.NaN;
            }

            c += g.ArcCost[a] + g.SegCost[path[i]];
        }

        return c;
    }
}
