using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Transit;
using Sim.Core.Util;

namespace Sim.Core.Tools;

// Автобусные инструменты (T9, player-tools.md §8.7): проверки общего валидатора превью и применения. Остановка задаётся полосой (сегмент, направление, LaneNo) и S вдоль полосы по ходу движения, м.
internal static partial class ToolValidator
{
    /// <summary>Наибольший интервал линии (сутки игрового времени), тики.</summary>
    public const int MaxHeadwayTicks = 24 * 3600 * Simulation.TicksPerSecond;

    private static bool IsBusKind(CommandKind k) => k is CommandKind.CreateBusRoute or CommandKind.AppendBusStop or CommandKind.InsertBusStop or CommandKind.RemoveBusStop
        or CommandKind.RemoveBusRoute or CommandKind.SetBusRouteParams;

    private static RejectReason CheckCreateBusRoute(World w, in Command cmd, ref ToolCheck c)
    {
        CreateBusRouteCmd b = cmd.CreateBusRoute;
        NetworkStore n = w.Net;
        if ((b.Flags & ~BusRouteFlags.Suspend) != 0 || b.HeadwayTicks <= 0 || b.HeadwayTicks > MaxHeadwayTicks || (uint)b.VehicleType >= (uint)w.Types.Length)
        {
            return RejectReason.InvalidArgument;
        }

        if (b.RestoreOf.Generation != 0 && n.ResolveBusRoute(in b.RestoreOf))
        {
            return RejectReason.InvalidHandle;   // восстанавливаемая линия жива: восстанавливать нечего
        }

        if (n.BusRoutePool.PeekIndex() < 0)
        {
            c.CapKind = CapacityKind.BusRoutes;
            return RejectReason.Capacity;
        }

        c.Cost = w.ToolsCfg.Costs.BusRoute;
        return RejectReason.None;
    }

    private static RejectReason CheckAppendBusStop(World w, in Command cmd, ref ToolCheck c)
    {
        AppendBusStopCmd a = cmd.AppendBusStop;
        NetworkStore n = w.Net;
        if (!n.BusRouteAlive(a.Route) || !n.LaneAlive(a.Lane) || n.Lanes[a.Lane].Kind != LaneKind.Normal || !n.SegmentAlive(n.Lanes[a.Lane].Segment))
        {
            return RejectReason.InvalidHandle;
        }

        return CheckStopPlace(w, a.Route, n.BusRoutes[a.Route].StopCount, a.Lane, a.S, (int)a.Kind, ref c);
    }

    private static RejectReason CheckInsertBusStop(World w, in Command cmd, ref ToolCheck c)
    {
        InsertBusStopCmd b = cmd.InsertBusStop;
        NetworkStore n = w.Net;
        if (!n.ResolveBusRoute(in b.Route))
        {
            return RejectReason.InvalidHandle;
        }

        int r = b.Route.Index;
        if (b.RestoreOf.Generation != 0 && n.ResolveBusStop(in b.RestoreOf))
        {
            // Восстанавливаемая остановка жива и стоит на этой линии (повтор обратной записи) - ничего не делать; иначе хост перепутал handle.
            if (n.BusStops[b.RestoreOf.Index].Route == r && n.StopPos(b.RestoreOf.Index) >= 0)
            {
                c.Noop = true;
                c.Stop = b.RestoreOf.Index;
                c.Route = r;
                return RejectReason.None;
            }

            return RejectReason.InvalidHandle;
        }

        if (!n.ResolveSegment(in b.Seg))
        {
            return RejectReason.InvalidHandle;
        }

        ref readonly SegmentData sd = ref n.Segments[b.Seg.Index];
        int lanes = b.Dir == 0 ? sd.LanesAB : b.Dir == 1 ? sd.LanesBA : 0;
        if (b.Dir > 1 || b.LaneNo >= lanes)
        {
            return RejectReason.InvalidArgument;
        }

        int lane = (b.Dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA) + b.LaneNo;
        int cnt = n.BusRoutes[r].StopCount;
        int pos = b.Position < 0 ? cnt : b.Position;
        if (b.Position < -1 || pos > cnt)
        {
            return RejectReason.InvalidArgument;
        }

        return CheckStopPlace(w, r, pos, lane, b.S, b.Kind, ref c);
    }

    // Общая часть вставки: место на полосе, ёмкость, достижимость от соседних остановок линии, цена.
    private static RejectReason CheckStopPlace(World w, int r, int pos, int lane, float s, int kind, ref ToolCheck c)
    {
        NetworkStore n = w.Net;
        ref readonly LaneData ld = ref n.Lanes[lane];
        c.Route = r;
        c.Lane = lane;
        c.Pos = pos;
        c.StopS = s;
        c.Seg = ld.Segment;
        int hint = 0;
        if (float.IsFinite(s))
        {
            LaneGeometry.Evaluate(n, lane, s, ref hint, out Vec2 p, out _);
            Mark(ref c, EntityKind.Segment, ld.Segment, n.Segments[ld.Segment].Generation, p);
        }

        if (!float.IsFinite(s) || s < 0f || s > ld.Length || (uint)kind > (uint)BusStopKind.Bay)
        {
            return RejectReason.InvalidArgument;
        }

        if ((n.Segments[ld.Segment].Flags & SegmentFlags.Closing) != 0 || (ld.Flags & (LaneFlags.Closed | LaneFlags.Draining)) != 0)
        {
            return RejectReason.Busy;   // дорога сносится или полоса дренируется
        }

        ref readonly BusRouteData br = ref n.BusRoutes[r];
        int cnt = br.StopCount;
        if (cnt + 1 > n.MaxStopsPerRoute || n.BusStopPool.PeekIndex() < 0 ||
            (BusEdit.NeedsNewBlock(n, br.FirstStop, cnt, cnt + 1) && !n.BusRouteStopArena.CanAlloc(cnt + 1)))
        {
            c.CapKind = CapacityKind.BusStops;
            return RejectReason.Capacity;
        }

        // Unreachable (player-tools.md §6): от предыдущей остановки линии до новой и от новой до следующей - путь по текущей сети.
        Connectivity conn = w.ToolScratch.Conn ??= new Connectivity(n);
        if (pos > 0)
        {
            ref readonly BusStopData prev = ref n.BusStops[n.BusRouteStops[br.FirstStop + pos - 1]];
            if (!conn.Reaches(prev.Lane, prev.S, lane, s))
            {
                return RejectReason.Unreachable;
            }
        }

        if (pos < cnt)
        {
            ref readonly BusStopData next = ref n.BusStops[n.BusRouteStops[br.FirstStop + pos]];
            if (!conn.Reaches(lane, s, next.Lane, next.S))
            {
                return RejectReason.Unreachable;
            }
        }

        c.Cost = w.ToolsCfg.Costs.BusStop;
        return RejectReason.None;
    }

    private static RejectReason CheckRemoveBusStop(World w, in Command cmd, ref ToolCheck c)
    {
        RemoveBusStopCmd b = cmd.RemoveBusStop;
        NetworkStore n = w.Net;
        if (!n.ResolveBusStop(in b.Stop))
        {
            return RejectReason.InvalidHandle;
        }

        int si = b.Stop.Index;
        ref readonly BusStopData st = ref n.BusStops[si];
        c.Stop = si;
        c.Route = st.Route;
        int hint = 0;
        if ((uint)st.Lane < (uint)n.LaneCount)
        {
            LaneGeometry.Evaluate(n, st.Lane, st.S, ref hint, out Vec2 p, out _);
            Mark(ref c, EntityKind.BusStop, si, st.Generation, p);
        }

        int pos = n.StopPos(si);
        if (pos >= 0)
        {
            ref readonly BusRouteData br = ref n.BusRoutes[st.Route];
            int cnt = br.StopCount;
            if (cnt > 1 && BusEdit.NeedsNewBlock(n, br.FirstStop, cnt, cnt - 1) && !n.BusRouteStopArena.CanAlloc(cnt - 1))
            {
                c.CapKind = CapacityKind.BusStops;
                return RejectReason.Capacity;
            }
        }

        return RejectReason.None;
    }

    private static RejectReason CheckRemoveBusRoute(World w, in Command cmd, ref ToolCheck c)
    {
        RemoveBusRouteCmd b = cmd.RemoveBusRoute;
        if (!w.Net.ResolveBusRoute(in b.Route))
        {
            return RejectReason.InvalidHandle;
        }

        c.Route = b.Route.Index;
        return RejectReason.None;
    }

    private static RejectReason CheckSetBusRouteParams(World w, in Command cmd, ref ToolCheck c)
    {
        SetBusRouteParamsCmd b = cmd.SetBusRouteParams;
        if (!w.Net.ResolveBusRoute(in b.Route))
        {
            return RejectReason.InvalidHandle;
        }

        const int known = BusRouteFlags.Suspend | BusRouteFlags.Resume;
        if (b.HeadwayTicks < 0 || b.HeadwayTicks > MaxHeadwayTicks || b.VehicleType < -1 || b.VehicleType >= w.Types.Length || (b.Flags & ~known) != 0 || b.Flags == known)
        {
            return RejectReason.InvalidArgument;
        }

        c.Route = b.Route.Index;
        return RejectReason.None;
    }
}
