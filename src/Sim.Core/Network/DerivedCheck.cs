using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Сверка производных сети с полной перестройкой (<c>World.VerifyDerived</c>, player-tools.md §3 п. 4, §14 <c>EditFuzz</c>): локальное обновление после правки обязано дать то же,
/// что построение «по возрастанию индекса живых слотов». Только для тестов и отладки (аллоцирует); возвращает описание первого расхождения или <c>null</c>.
/// Покрыто: пулы и мёртвые слоты, список инцидентности и сетка прилипания, производные полос (<c>LaneLength</c>, курсы, <c>LaneGateNode</c>), топология <see cref="Routing.RouteGraph"/>,
/// маска занятых полос, привязка узлов и полос к ячейкам LOD, строки connector'ов. Оверлей, сеть пешеходов и город пересобираются по <c>Net.Version</c> (T3, T11).
/// </summary>
internal static class DerivedCheck
{
    public static string? Run(World w)
    {
        NetworkStore n = w.Net;
        return Pools(n) ?? Slots(n) ?? Indexes(w) ?? LaneDerived(n) ?? Graph(w) ?? Occupancy(w) ?? Lod(w) ?? Connectors(w);
    }

    private static string? Pools(NetworkStore n)
    {
        string? r = Pool("nodes", n.NodePool, i => n.Nodes[i].Generation);
        r ??= Pool("segments", n.SegmentPool, i => n.Segments[i].Generation);
        r ??= Pool("bus stops", n.BusStopPool, i => n.BusStops[i].Generation);
        r ??= Pool("bus routes", n.BusRoutePool, i => n.BusRoutes[i].Generation);
        return r;
    }

    private static string? Pool(string name, HandlePool p, Func<int, int> dataGeneration)
    {
        int dead = 0;
        for (int i = 0; i < p.HighWater; i++)
        {
            if (p.GenerationOf(i) != dataGeneration(i))
            {
                return $"{name}[{i}]: Generation в данных {dataGeneration(i)} != в пуле {p.GenerationOf(i)}";
            }

            if (!p.IsAlive(i))
            {
                dead++;
            }
        }

        if (dead != p.FreeCount || p.AliveCount != p.HighWater - dead)
        {
            return $"{name}: мёртвых {dead}, free-list {p.FreeCount}, живых {p.AliveCount} при HighWater {p.HighWater}";
        }

        var seen = new bool[p.HighWater];
        for (int k = 0; k < p.FreeCount; k++)
        {
            int slot = p.FreeListArray[k];
            if ((uint)slot >= (uint)p.HighWater || p.IsAlive(slot) || seen[slot])
            {
                return $"{name}: free-list[{k}] = {slot} не мёртвый слот или повтор";
            }

            seen[slot] = true;
        }

        return null;
    }

    private static string? Slots(NetworkStore n)
    {
        for (int i = 0; i < n.NodeCount; i++)
        {
            if (!n.NodeAlive(i) && !Save.SaveValidator.DeadNodeIsCanonical(in n.Nodes[i]))
            {
                return $"node {i}: мёртвый слот не канонический";
            }
        }

        for (int i = 0; i < n.SegmentCount; i++)
        {
            if (n.SegmentAlive(i))
            {
                if (!n.NodeAlive(n.Segments[i].NodeA) || !n.NodeAlive(n.Segments[i].NodeB))
                {
                    return $"segment {i}: конец - мёртвый узел";
                }
            }
            else if (!Save.SaveValidator.DeadSegmentIsCanonical(in n.Segments[i]))
            {
                return $"segment {i}: мёртвый слот не канонический";
            }
        }

        for (int i = 0; i < n.LaneCount; i++)
        {
            if (!n.LaneAlive(i))
            {
                if (!NetworkStore.IsDeadLane(in n.Lanes[i]))
                {
                    return $"lane {i}: мёртвая полоса не каноническая";
                }

                continue;
            }

            ref readonly LaneData l = ref n.Lanes[i];
            if (l.Kind == LaneKind.Connector ? !n.NodeAlive(l.Node) : !n.SegmentAlive(l.Segment))
            {
                return $"lane {i}: живая полоса мёртвого владельца";
            }
        }

        for (int i = 0; i < n.BusStopCount; i++)
        {
            if (!n.BusStopAlive(i) && !Save.SaveValidator.DeadBusStopIsCanonical(in n.BusStops[i]))
            {
                return $"bus stop {i}: мёртвый слот не канонический";
            }
        }

        for (int i = 0; i < n.BusRouteCount; i++)
        {
            if (!n.BusRouteAlive(i) && !Save.SaveValidator.DeadBusRouteIsCanonical(in n.BusRoutes[i]))
            {
                return $"bus route {i}: мёртвый слот не канонический";
            }
        }

        return null;
    }

    // Список инцидентности и сетка прилипания: цепочки идут по убыванию индекса (как после полной перестройки по возрастанию индекса живых).
    private static string? Indexes(World w)
    {
        NetworkStore n = w.Net;
        int nodes = n.NodeCount;
        var expHead = new int[nodes];
        var expNext = new int[2 * n.SegmentCount];
        Array.Fill(expHead, -1);
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            int a = n.Segments[s].NodeA;
            int b = n.Segments[s].NodeB;
            expNext[2 * s] = expHead[a];
            expHead[a] = 2 * s;
            expNext[(2 * s) + 1] = expHead[b];
            expHead[b] = (2 * s) + 1;
        }

        for (int i = 0; i < nodes; i++)
        {
            if (!n.NodeAlive(i))
            {
                continue;
            }

            int x = n.IncHead[i];
            int y = expHead[i];
            while (x >= 0 || y >= 0)
            {
                if (x != y)
                {
                    return $"IncHead цепочка узла {i}: {x} != ожидаемого {y}";
                }

                x = n.IncNext[x];
                y = expNext[y];
            }
        }

        var lastInCell = new Dictionary<long, int>();
        for (int i = 0; i < nodes; i++)
        {
            if (!n.NodeAlive(i))
            {
                continue;
            }

            long key = w.Builder.CellKeyOf(n.Nodes[i].Position);
            int expected = lastInCell.TryGetValue(key, out int prev) ? prev : -1;
            if (n.CellNext[i] != expected)
            {
                return $"CellNext[{i}] = {n.CellNext[i]}, ожидалось {expected}";
            }

            lastInCell[key] = i;
        }

        foreach (KeyValuePair<long, int> kv in lastInCell)
        {
            if (!n.Cells.TryGet(kv.Key, out int head) || head != kv.Value)
            {
                return $"Cells[{kv.Key}] != {kv.Value}";
            }
        }

        if (n.Cells.Count != lastInCell.Count)
        {
            return $"Cells: ключей {n.Cells.Count}, ячеек с узлами {lastInCell.Count}";
        }

        return null;
    }

    private static string? LaneDerived(NetworkStore n)
    {
        for (int l = 0; l < n.LaneCount; l++)
        {
            if (!n.LaneAlive(l))
            {
                if (n.LaneLength[l] != 0f)
                {
                    return $"LaneLength[{l}] у мёртвой полосы";
                }

                continue;
            }

            if (n.LaneLength[l] != n.Lanes[l].Length)
            {
                return $"LaneLength[{l}] != Lanes[{l}].Length";
            }

            for (int k = 0; k + 1 < n.Lanes[l].PointCount; k++)
            {
                int p = n.Lanes[l].FirstPoint + k;
                if (n.LanePieceHeading[p] != NetworkStore.PieceHeading(n.LanePoints[p], n.LanePoints[p + 1]))
                {
                    return $"LanePieceHeading[{p}] (полоса {l}) не равен пересчёту";
                }
            }

            bool conn = n.Lanes[l].Kind == LaneKind.Connector;
            int expected = conn ? (GateRules.IsGatedConnectorSlow(n, in n.Lanes[l]) ? n.Lanes[l].Node : -1) : GateRules.GateNodeSlow(n, l);
            if (n.LaneGateNode[l] != expected)
            {
                return $"LaneGateNode[{l}] = {n.LaneGateNode[l]}, ожидалось {expected}";
            }
        }

        return null;
    }

    // Топология графа (дуги, вершины); веса зависят от нагрузки и планов и в сверку не входят.
    private static string? Graph(World w)
    {
        var fresh = new Routing.RouteGraph(w.Net);
        fresh.Rebuild();
        Routing.RouteGraph g = w.Graph;
        if (g.BuiltVersion != w.Net.Version)
        {
            return $"RouteGraph построен по версии {g.BuiltVersion}, сеть {w.Net.Version}";
        }

        if (fresh.DirSegCount != g.DirSegCount || fresh.ArcCount != g.ArcCount)
        {
            return $"RouteGraph: DirSeg {g.DirSegCount}/{fresh.DirSegCount}, дуг {g.ArcCount}/{fresh.ArcCount}";
        }

        for (int u = 0; u < g.DirSegCount; u++)
        {
            if (g.StartNode[u] != fresh.StartNode[u] || g.EndNode[u] != fresh.EndNode[u] || g.ArcFirst[u] != fresh.ArcFirst[u] || g.ArcFirst[u + 1] != fresh.ArcFirst[u + 1])
            {
                return $"RouteGraph: вершина {u} расходится";
            }
        }

        for (int a = 0; a < g.ArcCount; a++)
        {
            if (g.ArcTo[a] != fresh.ArcTo[a] || g.ArcManeuver[a] != fresh.ArcManeuver[a])
            {
                return $"RouteGraph: дуга {a} расходится";
            }
        }

        return null;
    }

    private static string? Occupancy(World w)
    {
        for (int l = 0; l < w.Net.LaneCount; l++)
        {
            bool bit = (w.Occupancy.OccMask[l >> 6] & (1UL << (l & 63))) != 0;
            if (bit != (w.Occupancy.LaneCount[l] > 0))
            {
                return $"OccMask[{l}] не равен LaneCount > 0";
            }

            if (!w.Net.LaneAlive(l) && (w.Occupancy.LaneCount[l] != 0 || w.Occupancy.LaneHead[l] != -1 || w.Occupancy.LaneTail[l] != -1))
            {
                return $"мёртвая полоса {l} занята";
            }
        }

        return null;
    }

    private static string? Lod(World w)
    {
        Sim.Core.Lod.LodCells? c = w.Lod;
        if (c == null)
        {
            return null;
        }

        NetworkStore n = w.Net;
        for (int i = 0; i < n.NodeCount; i++)
        {
            Vec2 p = n.Nodes[i].Position;
            int expected = n.NodeAlive(i) ? c.CellOf(p.X, p.Z) : -1;
            if (c.NodeCell[i] != expected)
            {
                return $"LOD NodeCell[{i}] = {c.NodeCell[i]}, ожидалось {expected}";
            }
        }

        for (int l = 0; l < n.LaneCount; l++)
        {
            int expected = -1;
            if (n.LaneAlive(l))
            {
                ref readonly LaneData ld = ref n.Lanes[l];
                int node = ld.Kind == LaneKind.Connector ? ld.Node : (ld.Direction == 0 ? n.Segments[ld.Segment].NodeB : n.Segments[ld.Segment].NodeA);
                expected = c.NodeCell[node];
            }

            if (c.LaneCell[l] != expected)
            {
                return $"LOD LaneCell[{l}] = {c.LaneCell[l]}, ожидалось {expected}";
            }
        }

        return null;
    }

    private static string? Connectors(World w)
    {
        NetworkStore n = w.Net;
        ConnectorStore cs = w.Connectors;
        int rows = 0;
        for (int node = 0; node < n.NodeCount; node++)
        {
            if (!n.NodeAlive(node) || n.Nodes[node].FirstArm < 0)
            {
                continue;
            }

            ref readonly NodeData nd = ref n.Nodes[node];
            for (int p = 0; p < nd.ConnectorCount; p++)
            {
                int lane = nd.FirstConnector + p;
                int g = n.Lanes[lane].ConflictGroup;
                if (!n.LaneAlive(lane) || n.Lanes[lane].Kind != LaneKind.Connector || n.Lanes[lane].Node != node)
                {
                    return $"узел {node}: connector {p} - не живая полоса узла";
                }

                if (g >= 0)
                {
                    rows++;
                    if (cs.Lane[g] != lane || cs.Node[g] != node || cs.LocalBit[g] != p)
                    {
                        return $"строка connector'ов {g} не равна полосе {lane} узла {node}";
                    }
                }
            }
        }

        return rows > cs.Count ? $"строк connector'ов {rows} больше Count {cs.Count}" : null;
    }
}
