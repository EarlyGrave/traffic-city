using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tools;

// Разрез и слияние (T5, player-tools.md §4.4): SplitRoad, MergeAtNode. Перенос машин без дренажа (ChainEdit), поэтому занятость не мешает.
internal static partial class ToolValidator
{
    /// <summary>
    /// <c>SplitRoad</c>: живой сегмент без операции, остановок и Q-meso; точка разреза по полной оси (м от узла A) оставляет обе половины не короче минимума построителя;
    /// рядом с точкой нет узла (прилипание). Ёмкость: узел, два сегмента, их полосы и connector'ы нового узла.
    /// </summary>
    private static RejectReason CheckSplitRoad(World w, in Command cmd, ref ToolCheck c)
    {
        SplitRoadCmd sr = cmd.SplitRoad;
        NetworkStore n = w.Net;
        if (!n.ResolveSegment(in sr.Seg))
        {
            return RejectReason.InvalidHandle;
        }

        if (sr.RestoreNode.Generation != 0 && n.ResolveNode(in sr.RestoreNode))
        {
            return RejectReason.InvalidHandle;   // восстанавливаемый узел жив: хост перепутал handle
        }

        int seg = sr.Seg.Index;
        c.Seg = seg;
        ref readonly SegmentData sd = ref n.Segments[seg];
        Vec2 mid = n.SegmentPoints[sd.FirstPoint + (sd.PointCount / 2)];
        Mark(ref c, EntityKind.Segment, seg, sd.Generation, mid);
        if ((sd.Flags & SegmentFlags.Closing) != 0 || w.Ops.FindAny(seg) >= 0)
        {
            return RejectReason.Busy;
        }

        if (w.Queue != null || HasBusStop(n, seg))
        {
            return RejectReason.NotSupported;   // остановка на сегменте - T9; Q-meso
        }

        if (!float.IsFinite(sr.S))
        {
            return RejectReason.InvalidArgument;
        }

        NetworkBuilder bld = w.Builder;
        if (!ChainEdit.SplitFits(n, bld.Options, seg, sr.S))
        {
            return RejectReason.TooShort;
        }

        Vec2 p = ChainEdit.PointOnFull(n, in sd, sr.S, out _);
        Mark(ref c, EntityKind.Segment, seg, sd.Generation, p);
        int near = bld.FindNode(p);
        if (near >= 0)
        {
            Mark(ref c, EntityKind.Node, near, n.Nodes[near].Generation, n.Nodes[near].Position);
            return RejectReason.InvalidArgument;   // рядом узел: разрез прилип бы к нему
        }

        int total = sd.LanesAB + sd.LanesBA;
        RejectReason cap = ChainCapacity(w, 1, 2, 2 * total, (2 * sd.PointCount) + 2, total);
        if (cap != RejectReason.None)
        {
            c.CapKind = CapacityKind.Lanes;
            return cap;
        }

        c.AX = p.X;
        c.AZ = p.Z;
        c.BX = p.X;
        c.BZ = p.Z;
        c.LengthM = bld.FullAxisLength(seg);
        c.NewNodes = 1;
        c.NewSegments = 2;
        c.NewLanes = 2 * total;
        c.Mechanism = 1;
        c.Cost = 0;
        return RejectReason.None;
    }

    /// <summary>
    /// <c>MergeAtNode</c>: узел степени 2 (два разных сегмента, дальние концы разные), без светофора, знаков рукавов, запретов и зоны, режим <c>None</c>; сегменты без операций и остановок,
    /// с одинаковыми типом, скоростью, профилем (с учётом направления), полосами и флагами полос по направлениям цепочки; ось общего сегмента - не больше <c>MaxVia</c> промежуточных точек.
    /// </summary>
    private static RejectReason CheckMergeAtNode(World w, in Command cmd, ref ToolCheck c)
    {
        MergeAtNodeCmd mc = cmd.MergeAtNode;
        NetworkStore n = w.Net;
        if (!n.ResolveNode(in mc.Node))
        {
            return RejectReason.InvalidHandle;
        }

        if (mc.RestoreSeg.Generation != 0 && n.ResolveSegment(in mc.RestoreSeg))
        {
            return RejectReason.InvalidHandle;
        }

        int node = mc.Node.Index;
        c.Node = node;
        ref readonly NodeData nd = ref n.Nodes[node];
        Mark(ref c, EntityKind.Node, node, nd.Generation, nd.Position);
        if (!ChainEdit.TwoSegments(n, node, out int s1, out int s2) || nd.FirstArm < 0 || nd.ArmCount != 2)
        {
            return RejectReason.InvalidArgument;
        }

        if (nd.Signal >= 0 || nd.TurnBan != 0UL || nd.Zone >= 0 || nd.Control != NodeControl.None ||
            w.Builder.ArmOverrideOf(node, 0) != 0 || w.Builder.ArmOverrideOf(node, 1) != 0)
        {
            return RejectReason.InvalidArgument;   // настройки узла пропали бы со слиянием
        }

        for (int e = 0; e < 2; e++)
        {
            int seg = e == 0 ? s1 : s2;
            if ((n.Segments[seg].Flags & SegmentFlags.Closing) != 0 || w.Ops.FindAny(seg) >= 0)
            {
                return RejectReason.Busy;
            }

            if (HasBusStop(n, seg))
            {
                return RejectReason.NotSupported;
            }
        }

        if (w.Queue != null)
        {
            return RejectReason.NotSupported;
        }

        int far1 = ChainEdit.Far(n, s1, node);
        int far2 = ChainEdit.Far(n, s2, node);
        ref readonly SegmentData d1 = ref n.Segments[s1];
        ref readonly SegmentData d2 = ref n.Segments[s2];
        bool fw1 = d1.NodeA == far1;
        bool fw2 = d2.NodeA == node;
        if (far1 == far2 || far1 == node || far2 == node || d1.Kind != d2.Kind || ((d1.Flags ^ d2.Flags) & SegmentFlags.Bridge) != 0 || d1.SpeedLimit != d2.SpeedLimit || d1.Profile != d2.Profile ||
            (d1.Profile >= 0 && ChainEdit.MirroredAlong(in d1, fw1) != ChainEdit.MirroredAlong(in d2, fw2)) ||
            (d1.PointCount - 2) + 1 + (d2.PointCount - 2) > NetworkBuilder.MaxVia)
        {
            return RejectReason.InvalidArgument;
        }

        for (int d = 0; d < 2; d++)
        {
            int cnt = ChainEdit.LanesAlong(in d1, fw1, d);
            if (cnt != ChainEdit.LanesAlong(in d2, fw2, d))
            {
                return RejectReason.InvalidArgument;
            }

            int l1 = fw1 == (d == 0) ? d1.FirstLaneAB : d1.FirstLaneBA;
            int l2 = fw2 == (d == 0) ? d2.FirstLaneAB : d2.FirstLaneBA;
            for (int k = 0; k < cnt; k++)
            {
                const byte player = LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut;
                if ((n.Lanes[l1 + k].Flags & player) != (n.Lanes[l2 + k].Flags & player))
                {
                    return RejectReason.InvalidArgument;
                }
            }
        }

        int total = d1.LanesAB + d1.LanesBA;
        RejectReason cap = ChainCapacity(w, 0, 1, total, d1.PointCount + d2.PointCount + 1, 0);
        if (cap != RejectReason.None)
        {
            c.CapKind = CapacityKind.Lanes;
            return cap;
        }

        c.AX = nd.Position.X;
        c.AZ = nd.Position.Z;
        c.BX = nd.Position.X;
        c.BZ = nd.Position.Z;
        c.LengthM = w.Builder.FullAxisLength(s1) + w.Builder.FullAxisLength(s2);
        c.NewSegments = 1;
        c.NewLanes = total;
        c.Mechanism = 1;
        c.Cost = 0;
        return RejectReason.None;
    }

    /// <summary>
    /// <c>BuildRoad</c> + <c>AutoSplit</c>: ближайший (ничья - меньший индекс) живой сегмент, ось которого проходит в <c>SnapRadius</c> от <paramref name="p"/>. Найден - точка
    /// <paramref name="p"/> переносится на ось; разрез обязан пройти проверки <c>SplitRoad</c>, а «коробка» узла - быть пустой (иначе <c>Busy</c>: перестройка нового узла под третий рукав
    /// не должна ждать). Не найден - <paramref name="seg"/> = -1 (обычный новый узел).
    /// </summary>
    private static RejectReason FindSplit(World w, ref Vec2 p, ref ToolCheck c, out int seg, out float s)
    {
        NetworkStore n = w.Net;
        NetworkBuilder bld = w.Builder;
        seg = -1;
        s = 0f;
        float best = bld.Options.SnapRadius;
        Vec2 at = p;
        for (int i = 0; i < n.SegmentCount; i++)
        {
            if (!n.SegmentAlive(i))
            {
                continue;
            }

            float d = ChainEdit.Project(n, in n.Segments[i], p, out float si, out Vec2 qi);
            if (d <= best && (d < best || seg < 0))
            {
                best = d;
                seg = i;
                s = si;
                at = qi;
            }
        }

        if (seg < 0)
        {
            return RejectReason.None;
        }

        ref readonly SegmentData sd = ref n.Segments[seg];
        Mark(ref c, EntityKind.Segment, seg, sd.Generation, at);
        if ((sd.Flags & SegmentFlags.Closing) != 0 || w.Ops.FindAny(seg) >= 0)
        {
            return RejectReason.Busy;
        }

        if (w.Queue != null || HasBusStop(n, seg))
        {
            return RejectReason.NotSupported;
        }

        if (!ChainEdit.SplitFits(n, bld.Options, seg, s))
        {
            return RejectReason.TooShort;
        }

        if (bld.FindNode(at) >= 0)
        {
            return RejectReason.InvalidArgument;
        }

        if (ChainEdit.BoxOccupied(w, seg, s))
        {
            return RejectReason.Busy;
        }

        p = at;
        return RejectReason.None;
    }

    // Ёмкость правки цепочки (с запасом: блоки проверяются по отдельности, как у BuildRoad).
    private static RejectReason ChainCapacity(World w, int nodes, int segments, int lanes, int axisPoints, int connectors)
    {
        NetworkStore n = w.Net;
        int samples = w.Builder.Options.ConnectorSamples;
        if (n.NodePool.Capacity - n.NodePool.AliveCount < nodes || n.SegmentPool.Capacity - n.SegmentPool.AliveCount < segments)
        {
            return RejectReason.Capacity;
        }

        if (!n.CanAllocBlock(n.LaneArena, lanes + connectors) || !n.CanAllocBlock(n.LanePointArena, (lanes * axisPoints) + (connectors * samples)) ||
            !n.CanAllocBlock(n.SegmentPointArena, axisPoints) || (connectors > 0 && (!n.CanAllocBlock(n.LaneNextArena, 2 * connectors) || !n.CanAllocBlock(w.Connectors.RowArena, connectors))))
        {
            return RejectReason.Capacity;
        }

        return RejectReason.None;
    }
}
