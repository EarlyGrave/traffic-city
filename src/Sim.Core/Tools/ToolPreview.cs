using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tools;

/// <summary>Писатель предупреждений превью: считает все, записывает сколько влезло (<see cref="PreviewResult.IssueCount"/> / <see cref="PreviewResult.IssuesWritten"/>).</summary>
internal ref struct IssueWriter
{
    private readonly Span<PreviewIssue> _span;
    public int Total;
    public int Written;

    public IssueWriter(Span<PreviewIssue> span)
    {
        _span = span;
        Total = 0;
        Written = 0;
    }

    public void Add(IssueCode code, IssueSeverity severity, EntityKind kind, int index, int gen, float x, float z, int value = 0)
    {
        Total++;
        if (Written < _span.Length)
        {
            _span[Written++] = new PreviewIssue
            {
                Code = code,
                Severity = severity,
                ObjKind = (byte)kind,
                Obj = kind == EntityKind.None ? EntityRef.None : new EntityRef(index, gen),
                X = x,
                Z = z,
                Value = value,
            };
        }
    }
}

/// <summary>
/// Превью команды инструмента (player-tools.md §7): вердикт общего валидатора (<see cref="ToolValidator"/>) плюс стоимость, привязка, ось, последствия и предупреждения.
/// Не меняет мир; scratch выделяется один раз (связность - лениво при первом превью, требующем её). Вне <c>Step</c>, на том же потоке.
/// </summary>
internal static class ToolPreview
{
    private const int NearCapacityPct = 90;

    public static RejectReason Run(World w, in Command cmd, out PreviewResult r, Span<PreviewIssue> issues, Span<Vec2> axis, PreviewOptions flags)
    {
        r = default;
        r.RoutesThrough = -1;
        var c = default(ToolCheck);
        RejectReason v = ToolValidator.Check(w, in cmd, ref c);
        var iw = new IssueWriter(issues);
        r.Verdict = v;
        r.Mechanism = c.Mechanism;
        r.SnapKindA = c.SnapKindA;
        r.SnapKindB = c.SnapKindB;
        r.SnapA = c.NodeA >= 0 ? new EntityRef(c.NodeA, w.Net.Nodes[c.NodeA].Generation) : EntityRef.None;
        r.SnapB = c.NodeB >= 0 ? new EntityRef(c.NodeB, w.Net.Nodes[c.NodeB].Generation) : EntityRef.None;
        r.Cost = c.Cost;
        r.AX = c.AX;
        r.AZ = c.AZ;
        r.BX = c.BX;
        r.BZ = c.BZ;
        r.LengthM = c.LengthM;
        r.NewNodes = c.NewNodes;
        r.NewSegments = c.NewSegments;
        r.NewLanes = c.NewLanes;

        if (cmd.Kind == CommandKind.BuildRoad && c.LengthM > 0f)
        {
            // Ось будущей дороги тем же сэмплированием, что у Builder: прямой сегмент - концы после привязки (кривая и разрез - T5).
            r.AxisPointCount = 2;
            if (axis.Length > 0)
            {
                axis[0] = new Vec2(c.AX, c.AZ);
            }

            if (axis.Length > 1)
            {
                axis[1] = new Vec2(c.BX, c.BZ);
            }
        }

        if (v != RejectReason.None)
        {
            iw.Add((IssueCode)(int)v, IssueSeverity.Error, c.IssueKind, c.IssueIndex, c.IssueGen, c.IssueX, c.IssueZ);
        }
        else
        {
            switch (cmd.Kind)
            {
                case CommandKind.BuildRoad:
                    BuildRoadWarnings(w, in cmd, in c, ref r, ref iw);
                    break;
                case CommandKind.RemoveRoad:
                    RemoveRoadWarnings(w, in c, flags, ref r, ref iw);
                    break;
                case CommandKind.SetTurnBan:
                case CommandKind.SetNodeControl:
                    NodeWarnings(w, in c, ref iw);
                    break;
            }
        }

        r.IssueCount = iw.Total;
        r.IssuesWritten = iw.Written;
        return v;
    }

    // ---------------------------------------------------------------- BuildRoad

    private static void BuildRoadWarnings(World w, in Command cmd, in ToolCheck c, ref PreviewResult r, ref IssueWriter iw)
    {
        if (c.Reopen)
        {
            return;   // отмена сноса: сегмент уже в сети, связность не меняется
        }

        NetworkStore n = w.Net;
        BuildRoadCmd b = cmd.BuildRoad;
        r.NewNodes = c.NewNodes;
        NearCapacity(n.NodePool.AliveCount + c.NewNodes, n.NodePool.Capacity, ref iw);
        NearCapacity(n.SegmentPool.AliveCount + 1, n.SegmentPool.Capacity, ref iw);

        Connectivity cn = ConnOf(w);
        cn.EnsureBase();
        bool oneWay = b.LanesAB == 0 || b.LanesBA == 0;
        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? c.NodeA : c.NodeB;
            float x = e == 0 ? c.AX : c.BX;
            float z = e == 0 ? c.AZ : c.BZ;
            if (node >= 0)
            {
                // Существующий узел вне эталонной компоненты: новая дорога подключает «остров».
                if (cn.RefBase >= 0 && n.IncHead[node] >= 0 && !NodeInComp(n, cn, node, cn.RefBase))
                {
                    iw.Add(IssueCode.DeadEnd, IssueSeverity.Info, EntityKind.Node, node, n.Nodes[node].Generation, x, z);
                }

                if (n.Nodes[node].Signal >= 0)
                {
                    iw.Add(IssueCode.SignalPlanReset, IssueSeverity.Info, EntityKind.Node, node, n.Nodes[node].Generation, x, z);
                }
            }
            else
            {
                // Новый свободный конец: тупик; односторонний, начинающийся в изолированном узле - в него никто не въедет.
                bool origin = oneWay && (b.LanesAB > 0 ? e == 0 : e == 1);
                iw.Add(IssueCode.DeadEnd, origin ? IssueSeverity.Warning : IssueSeverity.Info, EntityKind.None, -1, 0, x, z);
            }
        }
    }

    // ---------------------------------------------------------------- RemoveRoad

    private static void RemoveRoadWarnings(World w, in ToolCheck c, PreviewOptions flags, ref PreviewResult r, ref IssueWriter iw)
    {
        NetworkStore n = w.Net;
        int seg = c.Seg;
        ref readonly SegmentData sd = ref n.Segments[seg];
        float mx = c.IssueX;
        float mz = c.IssueZ;
        int drain = ToolOpsPhase.CountOn(w, seg);
        r.DrainVehicles = drain;
        if (drain > 0)
        {
            iw.Add(IssueCode.DrainNeeded, IssueSeverity.Warning, EntityKind.Segment, seg, sd.Generation, mx, mz, drain);
        }

        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? sd.NodeA : sd.NodeB;
            if (n.Nodes[node].Signal >= 0)
            {
                iw.Add(IssueCode.SignalPlanReset, IssueSeverity.Info, EntityKind.Node, node, n.Nodes[node].Generation, n.Nodes[node].Position.X, n.Nodes[node].Position.Z);
            }
        }

        if (w.City != null)
        {
            // Z6: участки вдоль сегмента станут Lost, их здания - заброшенными (ADR-0011 §8.4). Value - число зданий.
            LotStore l = w.City.Lots;
            int buildings = 0;
            for (int lot = 0; lot < l.Count; lot++)
            {
                if (l.Seg[lot] == seg && l.SegGen[lot] == sd.Generation && (l.Flags[lot] & LotFlags.Lost) == 0 && l.Building[lot] >= 0)
                {
                    buildings++;
                }
            }

            if (buildings > 0)
            {
                iw.Add(IssueCode.ZoneLotsLost, IssueSeverity.Warning, EntityKind.Segment, seg, sd.Generation, mx, mz, buildings);
            }
        }

        if ((flags & PreviewOptions.Detail) != 0)
        {
            r.RoutesThrough = CountRoutesThrough(w, seg);
            if (r.RoutesThrough > 0)
            {
                iw.Add(IssueCode.RoutesAffected, IssueSeverity.Info, EntityKind.Segment, seg, sd.Generation, mx, mz, r.RoutesThrough);
            }
        }

        // Связность (Tarjan на scratch без сегмента): зоны и остановки, выпавшие из эталонной компоненты.
        Connectivity cn = ConnOf(w);
        cn.EnsureBase();
        if (cn.RefBase < 0)
        {
            return;
        }

        cn.RunWithout(seg);
        for (int z = 0; z < n.ZoneCount; z++)
        {
            ref readonly ZoneData zd = ref n.Zones[z];
            bool before = false;
            bool after = false;
            for (int k = 0; k < zd.SpawnLaneCount; k++)
            {
                int u = cn.DirSegOf(n.ZoneLanes[zd.FirstSpawnLane + k]);
                if (u < 0)
                {
                    continue;
                }

                before |= cn.CompBase[u] == cn.RefBase;
                after |= cn.RefAlt >= 0 && cn.CompAlt[u] == cn.RefAlt;
            }

            if (before && !after)
            {
                r.ZonesLosingAccess++;
                iw.Add(IssueCode.ZoneAccessLost, IssueSeverity.Warning, EntityKind.Zone, z, 0, zd.Center.X, zd.Center.Z);
            }
        }

        for (int s = 0; s < n.BusStopCount; s++)
        {
            if (!n.BusStopAlive(s) || n.BusStops[s].Lane < 0)
            {
                continue;
            }

            int u = cn.DirSegOf(n.BusStops[s].Lane);
            if (u >= 0 && cn.CompBase[u] == cn.RefBase && !(cn.RefAlt >= 0 && cn.CompAlt[u] == cn.RefAlt))
            {
                Vec2 p = n.Nodes[sd.NodeA].Position;
                iw.Add(IssueCode.StopAccessLost, IssueSeverity.Warning, EntityKind.BusStop, s, n.BusStops[s].Generation, p.X, p.Z);
            }
        }
    }

    // Окна маршрутов машин (от текущего шага) с полосой сегмента; планы за окном не считаются (O(машин x окно)).
    private static int CountRoutesThrough(World w, int seg)
    {
        NetworkStore n = w.Net;
        Agents.VehicleStore v = w.Vehicles;
        Routing.RouteStore rs = w.Routes;
        int count = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            int slot = v.Route[i];
            if (slot < 0 || !v.Pool.IsAlive(i) || !rs.IsAlive(new RouteHandle(slot, v.RouteGen[i])))
            {
                continue;
            }

            int b = slot * rs.WindowSteps;
            int wc = rs.WinCount[slot];
            for (int k = Math.Max(0, v.RouteCursor[i] + 1); k < wc; k++)
            {
                int lane = rs.Window[b + k].Lane;
                if (n.Lanes[lane].Segment == seg)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static void NodeWarnings(World w, in ToolCheck c, ref IssueWriter iw)
    {
        if (c.Node >= 0 && w.Net.Nodes[c.Node].Signal >= 0)
        {
            ref readonly NodeData nd = ref w.Net.Nodes[c.Node];
            iw.Add(IssueCode.SignalPlanReset, IssueSeverity.Info, EntityKind.Node, c.Node, nd.Generation, nd.Position.X, nd.Position.Z);
        }
    }

    // ---------------------------------------------------------------- помощники

    private static Connectivity ConnOf(World w) => w.ToolScratch.Conn ??= new Connectivity(w.Net);

    private static void NearCapacity(int used, int capacity, ref IssueWriter iw)
    {
        if (capacity > 0 && (long)used * 100 >= (long)capacity * NearCapacityPct)
        {
            iw.Add(IssueCode.NearCapacity, IssueSeverity.Warning, EntityKind.None, -1, 0, 0f, 0f, (int)((long)used * 100 / capacity));
        }
    }

    // Есть ли у узла сегмент, любая вершина которого лежит в компоненте comp.
    private static bool NodeInComp(NetworkStore n, Connectivity cn, int node, int comp)
    {
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            int seg = code >> 1;
            if (cn.CompBase[seg * 2] == comp || cn.CompBase[(seg * 2) + 1] == comp)
            {
                return true;
            }
        }

        return false;
    }
}
