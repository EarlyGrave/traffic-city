using System;
using Sim.Core.Agents;
using Sim.Core.Api;

namespace Sim.Core.Network;

/// <summary>
/// Фаза 1а (player-tools.md §12): финалы операций дренажа по состоянию конца прошлого тика, по возрастанию <c>Id</c>. Операция <c>RemoveSegment</c> ждёт, пока на закрытом сегменте
/// и его connector'ах не останется машин, затем перестраивает узлы концов без этого рукава, освобождает сегмент и осиротевшие узлы (<c>KeepNodes</c> - оставить) и убирает его полосы
/// из списков зон. Застрявший дренаж не форсируется: <c>DrainStalled</c> раз в <see cref="ToolOps.StallTicks"/> без убыли <c>Remaining</c>. Один пересчёт производных на пачку финалов.
/// </summary>
internal static class ToolOpsPhase
{
    public static void Run(World w)
    {
        ToolOps ops = w.Ops;
        if (ops.Count == 0)
        {
            return;
        }

        NetworkStore n = w.Net;
        bool finished = false;
        for (int k = 0; k < ops.Count;)
        {
            int seg = ops.TargetIndex[k];
            if (!n.ResolveSegment(seg, ops.TargetGen[k]))
            {
                ops.RemoveAt(k);   // цель уже исчезла (защита: операция не должна пережить сегмент)
                continue;
            }

            int remaining = CountOn(w, seg);
            ops.Remaining[k] = remaining;
            if (remaining == 0)
            {
                int blocked = TryFinish(w, k);
                if (blocked == 0)
                {
                    finished = true;
                    continue;   // запись удалена, на её месте следующая
                }

                remaining = blocked;   // узел концов ждёт опустения обречённых connector'ов: это тоже «осталось»
                ops.Remaining[k] = remaining;
            }

            Progress(w, k, remaining);
            k++;
        }

        if (finished)
        {
            NetworkEdit.RefreshDerived(w, pedsChanged: w.Peds != null);
            w.City?.LotGen.RestoreDerived(w);   // Z6: компоненты связности, полосы и рёбра тротуара оставшихся участков
        }
    }

    // Убыль Remaining - прогресс; без прогресса дольше StallTicks - DrainStalled раз за период (цель остаётся закрытой, undo переоткрывает).
    private static void Progress(World w, int k, int remaining)
    {
        ToolOps ops = w.Ops;
        if (remaining < ops.BestRemaining[k])
        {
            ops.BestRemaining[k] = remaining;
            ops.LastProgressTick[k] = w.Tick;
            return;
        }

        if (w.Tick - ops.LastProgressTick[k] >= ToolOps.StallTicks && w.Tick - ops.LastStallTick[k] >= ToolOps.StallTicks)
        {
            ops.LastStallTick[k] = w.Tick;
            w.Work.DrainStalls++;
            w.Emit(SimEventKind.DrainStalled, 0, ops.Id[k], remaining);
        }
    }

    /// <summary>Машины на полосах сегмента (с ghost'ами) и на connector'ах его концов, ведущих в него и из него; пешеходы - T11.</summary>
    public static int CountOn(World w, int seg)
    {
        NetworkStore n = w.Net;
        ref readonly SegmentData sd = ref n.Segments[seg];
        int total = sd.LanesAB + sd.LanesBA;
        int first = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
        int count = 0;
        for (int k = 0; k < total; k++)
        {
            count += w.Occupancy.LaneCount[first + k];
        }

        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? sd.NodeA : sd.NodeB;
            ref readonly NodeData nd = ref n.Nodes[node];
            if (nd.FirstArm < 0)
            {
                continue;
            }

            for (int p = 0; p < nd.ConnectorCount; p++)
            {
                int c = nd.FirstConnector + p;
                if (n.Lanes[n.Lanes[c].FromLane].Segment == seg || n.Lanes[n.Lanes[c].ToLane].Segment == seg)
                {
                    count += w.Occupancy.LaneCount[c];
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Пытается закончить операцию <paramref name="k"/>: сегмент опустел. 0 - сделано (запись удалена); иначе число занятых машинами connector'ов, обречённых перестройкой узла
    /// (они закрыты и ждут опустения) либо 1, если перестройка не получилась по другой причине. Мир при неудаче не тронут, кроме закрытия обречённых connector'ов.
    /// </summary>
    private static int TryFinish(World w, int k)
    {
        ToolOps ops = w.Ops;
        NetworkStore n = w.Net;
        int seg = ops.TargetIndex[k];
        SegmentData sd = n.Segments[seg];
        int a = sd.NodeA;
        int b = sd.NodeB;
        w.Builder.UnlinkSegment(seg);
        NetworkStatus sa = n.Nodes[a].FirstArm >= 0 ? NodeEdit.Check(w, a, rejectTrap: false) : NetworkStatus.Ok;
        NetworkStatus sb = sa == NetworkStatus.Ok && n.Nodes[b].FirstArm >= 0 ? NodeEdit.Check(w, b, rejectTrap: false) : NetworkStatus.Ok;
        w.Builder.LinkSegment(seg);
        if (sa != NetworkStatus.Ok || sb != NetworkStatus.Ok)
        {
            int waiting = 0;
            if (sa == NetworkStatus.Busy)
            {
                waiting += CloseDoomed(w, seg, a);
            }

            if (sb == NetworkStatus.Busy || (sa == NetworkStatus.Ok && sb != NetworkStatus.Ok))
            {
                waiting += CloseDoomed(w, seg, b);
            }

            return Math.Max(1, waiting);
        }

        Finish(w, k, seg, sd);
        return 0;
    }

    // Обречённые перестройкой connector'ы узла закрываются для въезда (занятые ждут опустения, свободные исчезнут при перестройке); возвращает число занятых.
    private static int CloseDoomed(World w, int seg, int node)
    {
        w.Builder.UnlinkSegment(seg);
        NetworkStatus st = w.Builder.PlanRebuild(w, node, rejectTrap: false, out RebuildPlan rp);
        int busy = 0;
        if (st == NetworkStatus.Busy)
        {
            busy = w.Builder.CloseDoomedConnectors(w, in rp);
        }

        w.Builder.LinkSegment(seg);
        if (busy > 0)
        {
            SegmentClosure.AfterClosing(w);
        }

        return busy;
    }

    private static void Finish(World w, int k, int seg, SegmentData sd)
    {
        ToolOps ops = w.Ops;
        NetworkStore n = w.Net;
        int a = sd.NodeA;
        int b = sd.NodeB;
        int opId = ops.Id[k];
        bool keepNodes = (ops.Flags[k] & ToolOpFlags.KeepNodes) != 0;
        w.Builder.UnlinkSegment(seg);
        if (n.Nodes[a].FirstArm >= 0)
        {
            NetworkStatus s = NodeEdit.Rebuild(w, a, rejectTrap: false, 0);
            System.Diagnostics.Debug.Assert(s == NetworkStatus.Ok, "перестройка узла A после проверки");
        }

        if (n.Nodes[b].FirstArm >= 0)
        {
            NetworkStatus s = NodeEdit.Rebuild(w, b, rejectTrap: false, 0);
            System.Diagnostics.Debug.Assert(s == NetworkStatus.Ok, "перестройка узла B после проверки");
        }

        DegradeControl(w, a);
        DegradeControl(w, b);
        RemoveFromZones(w, seg);

        // Сегмент мёртв: оставшиеся ссылки на его полосы (назначения, поездки, окна) - в «нет» (маршруты освобождаются).
        w.Builder.LaneRemap.Clear();
        int total = sd.LanesAB + sd.LanesBA;
        int first = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
        for (int q = 0; q < total; q++)
        {
            w.Builder.LaneRemap.Add(first + q, -1);
        }

        w.Builder.RowRemap.Clear();
        Relocate.Apply(w, w.Builder.LaneRemap, w.Builder.RowRemap);

        bool released = NetworkEdit.ReleaseSegment(w, seg);
        System.Diagnostics.Debug.Assert(released, "освобождение сегмента после перестройки узлов");
        w.Emit(SimEventKind.SegmentRemoved, 0, seg, sd.Generation);
        ReleaseOrphan(w, a, keepNodes);
        ReleaseOrphan(w, b, keepNodes);

        w.Work.ToolOpsFinished++;
        long ticks = w.Tick - ops.StartTick[k];
        if (ticks > w.Work.DrainTicksMax)
        {
            w.Work.DrainTicksMax = ticks;
        }

        w.Emit(SimEventKind.ToolOpFinished, 0, opId, (int)CommandKind.RemoveRoad);
        ops.RemoveAt(k);
    }

    // Узел с правилом «справа» потерял рукав и стал проходным (<= 2 рукавов): правил нет, как у построенного генератором узла степени 2 (так обратная к BuildRoad возвращает прежний режим).
    private static void DegradeControl(World w, int node)
    {
        NetworkStore n = w.Net;
        ref readonly NodeData nd = ref n.Nodes[node];
        if (nd.FirstArm < 0 || nd.ArmCount > 2 || nd.Control != NodeControl.RightHandRule)
        {
            return;
        }

        w.Builder.SetNodeControl(node, NodeControl.None);
        w.Connectors.RecomputeRel(n, node, false);
        Rules.Intersections.GatePhase.ResetNode(w, node);
        w.Vehicles.MarkAllRouteDirty();
    }

    private static void ReleaseOrphan(World w, int node, bool keep)
    {
        NetworkStore n = w.Net;
        if (keep || !n.NodeAlive(node) || n.IncHead[node] >= 0 || n.Nodes[node].FirstArm >= 0 || n.Nodes[node].Signal >= 0)
        {
            return;
        }

        int gen = n.Nodes[node].Generation;
        if (NetworkEdit.ReleaseNode(w, node))
        {
            w.Emit(SimEventKind.NodeRemoved, 0, node, gen);
        }
    }

    // Полосы удаляемого сегмента уходят из списков зон (порядок остальных сохраняется); зона без полос получает ближайшую открытую (LaneNo 0, ничья - меньший индекс).
    private static void RemoveFromZones(World w, int seg)
    {
        NetworkStore n = w.Net;
        for (int z = 0; z < n.ZoneCount; z++)
        {
            ref ZoneData zd = ref n.Zones[z];
            int cnt = zd.SpawnLaneCount;
            int write = 0;
            for (int q = 0; q < cnt; q++)
            {
                int lane = n.ZoneLanes[zd.FirstSpawnLane + q];
                if (n.Lanes[lane].Segment != seg)
                {
                    n.ZoneLanes[zd.FirstSpawnLane + write++] = lane;
                }
            }

            if (write == cnt)
            {
                continue;
            }

            for (int q = write; q < cnt; q++)
            {
                n.ZoneLanes[zd.FirstSpawnLane + q] = 0;
            }

            zd.SpawnLaneCount = write;
            if (write == 0)
            {
                int nearest = NearestOpenLane(n, zd.Center, seg);
                if (nearest >= 0)
                {
                    n.ZoneLanes[zd.FirstSpawnLane] = nearest;
                    zd.SpawnLaneCount = 1;
                }
            }
        }
    }

    private static int NearestOpenLane(NetworkStore n, Util.Vec2 center, int excludeSeg)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int l = 0; l < n.LaneCount; l++)
        {
            if (!n.LaneAlive(l) || n.Lanes[l].Kind != LaneKind.Normal || n.Lanes[l].LaneNo != 0 || n.Lanes[l].Segment == excludeSeg || n.LaneClosed(l))
            {
                continue;
            }

            Util.Vec2 p = n.LanePoints[n.Lanes[l].FirstPoint];
            float dx = p.X - center.X;
            float dz = p.Z - center.Z;
            float d = (dx * dx) + (dz * dz);
            if (d < bestD)
            {
                bestD = d;
                best = l;
            }
        }

        return best;
    }
}
