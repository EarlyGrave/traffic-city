using System;
using System.Collections.Generic;
using Sim.Core.Agents;
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
        return Pools(n) ?? Slots(n) ?? Indexes(w) ?? LaneDerived(n) ?? Graph(w) ?? Occupancy(w) ?? Lod(w) ?? Connectors(w) ?? Topology(w) ?? References(w);
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
        // Версия графа может отставать от сети после правок «на месте» (знаки, флаги полос): граф пересобирается лениво (RoutePhase), его топология при этом та же - сверяется ниже.
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
            int g0 = -1;
            for (int p = 0; p < nd.ConnectorCount; p++)
            {
                int lane = nd.FirstConnector + p;
                int g = n.Lanes[lane].ConflictGroup;
                if (!n.LaneAlive(lane) || n.Lanes[lane].Kind != LaneKind.Connector || n.Lanes[lane].Node != node)
                {
                    return $"узел {node}: connector {p} - не живая полоса узла";
                }

                if (g < 0)
                {
                    continue;   // узел без записи в ConnectorStore (нехватка места при построении): строк нет
                }

                rows++;
                if (p == 0)
                {
                    g0 = g;
                }

                if (g != g0 + p || cs.Lane[g] != lane || cs.Node[g] != node || cs.LocalBit[g] != p || cs.InLane[g] != n.Lanes[lane].FromLane || cs.OutLane[g] != n.Lanes[lane].ToLane)
                {
                    return $"строка connector'ов {g} не равна полосе {lane} узла {node}";
                }

                int arm = cs.FromArm[g];
                if (arm >= nd.ArmCount || n.NodeArms[nd.FirstArm + arm] != n.Lanes[n.Lanes[lane].FromLane].Segment)
                {
                    return $"строка {g}: FromArm {arm} не рукав входной полосы";
                }

                byte af = n.ArmFlags[nd.FirstArm + arm];
                int flags = ((af & NetworkBuilder.ArmMinorBit) != 0 ? 1 : 0) | ((af & NetworkBuilder.ArmStopBit) != 0 ? 2 : 0);
                if (cs.Flags[g] != flags)
                {
                    return $"строка {g}: Flags {cs.Flags[g]} != {flags} по ArmFlags";
                }

                int end = cs.FirstConflict[g] + cs.ConflictCount[g];
                if (cs.FirstConflict[g] < 0 || end > cs.ConflictsUsed)
                {
                    return $"строка {g}: конфликты вне арены";
                }

                for (int e = cs.FirstConflict[g]; e < end; e++)
                {
                    if (cs.Conflicts[e].OtherLocal >= nd.ConnectorCount || cs.Conflicts[e].OtherLocal == p)
                    {
                        return $"строка {g}: конфликт {e} с недопустимым OtherLocal";
                    }
                }
            }
        }

        int live = 0;
        for (int g = 0; g < cs.Count; g++)
        {
            if (cs.Lane[g] >= 0)
            {
                live++;
            }
            else if (!cs.DeadRowIsCanonical(g))
            {
                return $"мёртвая строка connector'ов {g} не каноническая";
            }
        }

        return live != rows ? $"живых строк connector'ов {live}, у узлов {rows}" : null;
    }

    // Реестр ссылок на полосы (TL8, Relocate.Handlers): ни один владелец ссылки не указывает на мёртвую полосу.
    private static string? References(World w)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            string? bad = LaneRef(n, $"VEHC.Lane[{i}]", v.Lane[i]) ?? LaneRef(n, $"VEHC.GhostLane[{i}]", v.GhostLane[i]) ?? LaneRef(n, $"VEHC.DestLane[{i}]", v.DestLane[i]) ??
                          LaneRef(n, $"VEHC.NextConn[{i}]", v.NextConn[i], connector: true) ?? LaneRef(n, $"VEHC.GrantConnector[{i}]", v.GrantConnector[i], connector: true);
            if (bad != null)
            {
                return bad;
            }

            int r = v.Route[i];
            if (r >= 0 && w.Routes.IsAlive(new RouteHandle(r, v.RouteGen[i])))
            {
                string? win = Window(w, r, i);
                if (win != null)
                {
                    return win;
                }
            }
        }

        for (int k = 0; k < w.Trips.Count; k++)
        {
            ref readonly Agents.PendingTrip t = ref w.Trips.At(k);
            string? bad = LaneRef(n, $"TRIP.FromLane[{k}]", t.FromLane) ?? LaneRef(n, $"TRIP.DestLane[{k}]", t.DestLane);
            if (bad == null && t.HasRoute && w.Routes.IsAlive(new RouteHandle(t.Route, t.RouteGen)))
            {
                bad = Window(w, t.Route, -1 - k);
            }

            if (bad != null)
            {
                return bad;
            }
        }

        for (int s = 0; s < n.BusStopCount; s++)
        {
            if (n.BusStopAlive(s))
            {
                string? bad = LaneRef(n, $"NBUS.StopLane[{s}]", n.BusStops[s].Lane);
                if (bad != null)
                {
                    return bad;
                }
            }
        }

        for (int z = 0; z < n.ZoneCount; z++)
        {
            for (int k = 0; k < n.Zones[z].SpawnLaneCount; k++)
            {
                string? bad = LaneRef(n, $"NZON.ZoneLanes[{z}][{k}]", n.ZoneLanes[n.Zones[z].FirstSpawnLane + k]);
                if (bad != null)
                {
                    return bad;
                }
            }
        }

        return null;
    }

    private static string? Window(World w, int slot, int owner)
    {
        Routing.RouteStore rs = w.Routes;
        if (!float.IsFinite(rs.PlanCost[slot]) || rs.PlanCap[slot] < rs.PlanCount[slot] || (long)rs.PlanFirst[slot] + rs.PlanCap[slot] > rs.PlanUsed)
        {
            return $"маршрут {slot} (владелец {owner}): PlanCost {rs.PlanCost[slot]}, план {rs.PlanFirst[slot]}+{rs.PlanCap[slot]}/{rs.PlanCount[slot]}, занято {rs.PlanUsed}";
        }

        for (int s = 0; s < rs.WinCount[slot]; s++)
        {
            string? bad = LaneRef(w.Net, $"ROUT.WinLane[{slot}][{s}] (владелец {owner})", rs.Window[(slot * rs.WindowSteps) + s].Lane);
            if (bad != null)
            {
                return bad;
            }
        }

        return null;
    }

    private static string? LaneRef(NetworkStore n, string what, int lane, bool connector = false)
    {
        if (lane < 0)
        {
            return null;
        }

        if (!n.LaneAlive(lane))
        {
            return $"{what} = {lane}: мёртвая полоса";
        }

        return connector && n.Lanes[lane].Kind != LaneKind.Connector ? $"{what} = {lane}: не connector" : null;
    }

    // Рукава, связи входных полос с connector'ами и блоки LaneNext: то, что перестройка узла обязана оставить согласованным.
    private static string? Topology(World w)
    {
        NetworkStore n = w.Net;
        int connectors = 0;
        for (int node = 0; node < n.NodeCount; node++)
        {
            if (!n.NodeAlive(node))
            {
                continue;
            }

            ref readonly NodeData nd = ref n.Nodes[node];
            int inc = 0;
            for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
            {
                inc++;
            }

            if (nd.FirstArm < 0)
            {
                if (nd.ArmCount != 0 || nd.ConnectorCount != 0)
                {
                    return $"узел {node}: не построен, но ArmCount {nd.ArmCount}, ConnectorCount {nd.ConnectorCount}";
                }

                continue;
            }

            if (nd.ArmCount != inc)
            {
                return $"узел {node}: ArmCount {nd.ArmCount}, сегментов у узла {inc}";
            }

            for (int a = 0; a < nd.ArmCount; a++)
            {
                int seg = n.NodeArms[nd.FirstArm + a];
                if (!n.SegmentAlive(seg) || (n.Segments[seg].NodeA != node && n.Segments[seg].NodeB != node))
                {
                    return $"узел {node}: рукав {a} (сегмент {seg}) не инцидентен узлу";
                }
            }

            connectors += nd.ConnectorCount;
        }

        int links = 0;
        for (int l = 0; l < n.LaneCount; l++)
        {
            if (!n.LaneAlive(l))
            {
                continue;
            }

            ref readonly LaneData ld = ref n.Lanes[l];
            if (ld.Kind == LaneKind.Connector)
            {
                if (ld.NextCount != 1 || n.LaneNext[ld.FirstNext] != ld.ToLane)
                {
                    return $"connector {l}: LaneNext не равен ToLane";
                }

                continue;
            }

            for (int k = 0; k < ld.NextCount; k++)
            {
                int c = n.LaneNext[ld.FirstNext + k];
                if (!n.LaneAlive(c) || n.Lanes[c].Kind != LaneKind.Connector || n.Lanes[c].FromLane != l)
                {
                    return $"полоса {l}: LaneNext[{k}] = {c} не connector этой полосы";
                }
            }

            links += ld.NextCount;
        }

        return links != connectors ? $"связей входных полос {links}, connector'ов у узлов {connectors}" : null;
    }
}
