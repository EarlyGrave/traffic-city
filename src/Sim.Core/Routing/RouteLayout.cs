using System;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Routing;

/// <summary>Раскладка пути по направленным сегментам на полосы (routing.md 5); статический класс без состояния и аллокаций.</summary>
internal static class RouteLayout
{
    /// <summary>Оценка длины на одно перестроение, м (routing.md 5; в этой версии не используется при выборе connector'а).</summary>
    public const float LaneChangeDistance = 60f;

    /// <summary>
    /// Раскладка DirSeg-пути на полосы (routing.md 5): <c>[L0, C0, L1, ..., Lk]</c>, 2k+1 шагов. Возвращает число шагов или -1.
    /// Выбор connector'а: минимум |LaneNo(ToLane) - LaneNo(L_{j+1})|; для первого сегмента ничья - ближе к стартовой полосе,
    /// далее - меньший индекс connector'а.
    /// </summary>
    public static int LayoutLanes(NetworkStore net, RouteGraph graph, ReadOnlySpan<int> path, int len, int startLane, int destLane, Span<RouteStep> output)
    {
        if (len < 1 || output.Length < (2 * len) - 1)
        {
            return -1;
        }

        int k = len - 1;
        int lane = destLane;
        output[2 * k] = new RouteStep { Lane = lane, Node = -1 };
        int startNo = net.Lanes[startLane].LaneNo;
        for (int j = k - 1; j >= 0; j--)
        {
            int u = path[j];
            int w = path[j + 1];
            int firstLane = (u & 1) == 0 ? net.Segments[u >> 1].FirstLaneAB : net.Segments[u >> 1].FirstLaneBA;
            int laneCount = (u & 1) == 0 ? net.Segments[u >> 1].LanesAB : net.Segments[u >> 1].LanesBA;
            int targetNo = net.Lanes[lane].LaneNo;
            int bestConn = -1;
            int bestDist = int.MaxValue;
            int bestTie = int.MaxValue;
            for (int li = 0; li < laneCount; li++)
            {
                ref readonly LaneData ld = ref net.Lanes[firstLane + li];
                for (int c = 0; c < ld.NextCount; c++)
                {
                    int conn = net.LaneNext[ld.FirstNext + c];
                    ref readonly LaneData cd = ref net.Lanes[conn];
                    if (cd.Kind != LaneKind.Connector)
                    {
                        continue;
                    }

                    ref readonly LaneData to = ref net.Lanes[cd.ToLane];
                    if (((to.Segment * 2) + to.Direction) != w)
                    {
                        continue;
                    }

                    int dist = Math.Abs(to.LaneNo - targetNo);
                    int tie = j == 0 ? Math.Abs(ld.LaneNo - startNo) : 0;
                    if (dist < bestDist || (dist == bestDist && (tie < bestTie || (tie == bestTie && conn < bestConn))))
                    {
                        bestDist = dist;
                        bestTie = tie;
                        bestConn = conn;
                    }
                }
            }

            if (bestConn < 0)
            {
                return -1;
            }

            ref readonly LaneData best = ref net.Lanes[bestConn];
            output[(2 * j) + 1] = new RouteStep { Lane = bestConn, Node = best.Node };
            lane = best.FromLane;
            output[2 * j] = new RouteStep { Lane = lane, Node = graph.EndNode[u] };
        }

        return (2 * len) - 1;
    }
}
