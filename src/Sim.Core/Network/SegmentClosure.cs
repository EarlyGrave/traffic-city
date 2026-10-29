using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Network;

/// <summary>
/// Закрытие сегмента для въезда и обратное открытие (4.3, T4; player-tools.md §4.3): полосы получают <c>Closed</c>, сегмент - <c>Closing</c>, вес в графе <c>+inf</c> (<c>RouteGraph</c>),
/// ворота отказывают во въезде (<c>GateDeny.Closed</c>), допуски на концах сброшены, кэш <c>NextConn</c> и маршруты, проходящие через закрытое, освобождены (перемаршрутизация), назначения
/// машин и поездок на закрытых полосах заменены. Машины и пешеходы на сегменте доезжают сами.
/// </summary>
internal static class SegmentClosure
{
    private const int PlanDirMask = (1 << OverlaySearcher.LevelShift) - 1;

    /// <summary>Закрывает сегмент (идемпотентно): флаги, допуски, маршруты, назначения. Пересчёт графа и оверлея - у вызывающего (<see cref="NetworkEdit.RefreshDerived"/>).</summary>
    public static void Close(World w, int seg, uint tag)
    {
        NetworkStore n = w.Net;
        n.ToolsTouched = 1;
        ref SegmentData sd = ref n.Segments[seg];
        sd.Flags |= SegmentFlags.Closing;
        SetLaneClosed(n, in sd, closed: true);
        n.Version++;
        GatePhase.ResetNode(w, sd.NodeA);
        GatePhase.ResetNode(w, sd.NodeB);
        w.Gate.FullScan = true;
        RetargetDestinations(w, seg);
        AfterClosing(w);
        EmitZoneAccessLost(w, seg, tag);
        w.Emit(SimEventKind.SegmentClosing, tag, seg, sd.Generation);
    }

    /// <summary>Снимает закрытие: флаги сегмента и полос, а также connector'ов его концов, закрытых перестройкой узла (они закроются снова, если ещё нужны).</summary>
    public static void Reopen(World w, int seg)
    {
        NetworkStore n = w.Net;
        ref SegmentData sd = ref n.Segments[seg];
        sd.Flags &= unchecked((byte)~SegmentFlags.Closing);
        SetLaneClosed(n, in sd, closed: false);
        ReopenConnectors(n, sd.NodeA);
        ReopenConnectors(n, sd.NodeB);
        n.Version++;
        w.Gate.FullScan = true;

        // Connector по умолчанию снова может быть тем, что закрытие вытеснило: кэш первого хода пересчитывается у всех (отмена сноса - редкая правка).
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            v.NextConn[i] = v.Pool.IsAlive(i) ? -1 : v.NextConn[i];
        }

        v.MarkAllRouteDirty();
    }

    /// <summary>
    /// Открывает connector'ы концов сегмента, закрытые перестройкой узла (с открытыми полосами по обе стороны), и сбрасывает кэш первого хода у всех машин (T5: отмена дренажа полос).
    /// </summary>
    public static void ReopenEnds(World w, int seg)
    {
        NetworkStore n = w.Net;
        ref readonly SegmentData sd = ref n.Segments[seg];
        ReopenConnectors(n, sd.NodeA);
        ReopenConnectors(n, sd.NodeB);
        n.Version++;
        w.Gate.FullScan = true;
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            v.NextConn[i] = v.Pool.IsAlive(i) ? -1 : v.NextConn[i];
        }

        v.MarkAllRouteDirty();
    }

    private static void SetLaneClosed(NetworkStore n, in SegmentData sd, bool closed)
    {
        int total = sd.LanesAB + sd.LanesBA;
        int first = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
        for (int k = 0; k < total; k++)
        {
            if (closed)
            {
                n.Lanes[first + k].Flags |= LaneFlags.Closed;
            }
            else
            {
                n.Lanes[first + k].Flags &= unchecked((byte)~LaneFlags.Closed);
            }
        }
    }

    // Connector узла, закрытый перестройкой (обречён), а не закрытием соседней полосы: открывается обратно.
    private static void ReopenConnectors(NetworkStore n, int node)
    {
        ref readonly NodeData nd = ref n.Nodes[node];
        if (nd.FirstArm < 0)
        {
            return;
        }

        for (int p = 0; p < nd.ConnectorCount; p++)
        {
            ref LaneData c = ref n.Lanes[nd.FirstConnector + p];
            if ((c.Flags & LaneFlags.Closed) != 0 && (n.Lanes[c.FromLane].Flags & LaneFlags.Closed) == 0 && (n.Lanes[c.ToLane].Flags & LaneFlags.Closed) == 0)
            {
                c.Flags &= unchecked((byte)~LaneFlags.Closed);
            }
        }
    }

    /// <summary>После появления закрытых полос: кэш <c>NextConn</c> машин, чей первый ход ведёт в закрытое, сбрасывается; маршруты через закрытое освобождаются.</summary>
    public static void AfterClosing(World w)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        int hw = v.HighWater;
        int released = 0;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int nc = v.NextConn[i];
            if (nc >= 0 && ((n.Lanes[nc].Flags & LaneFlags.Closed) != 0 || (n.Lanes[n.Lanes[nc].ToLane].Flags & LaneFlags.Closed) != 0))
            {
                v.NextConn[i] = -1;
                v.MarkRouteDirty(i);
            }

            int r = v.Route[i];
            if (r >= 0 && (v.Flags[i] & (byte)VehicleFlags.RouteMissed) == 0 && w.Routes.IsAlive(new RouteHandle(r, v.RouteGen[i])) && RouteThroughClosed(w, r, v.RouteCursor[i] + 1))
            {
                RouteNav.ReleaseRoute(w, i);
                released++;
            }
        }

        PendingTrips trips = w.Trips;
        for (int k = 0; k < trips.Count; k++)
        {
            PendingTrip t = trips.At(k);
            if (t.HasRoute && w.Routes.IsAlive(new RouteHandle(t.Route, t.RouteGen)) && RouteThroughClosed(w, t.Route, 0))
            {
                w.Routes.Free(new RouteHandle(t.Route, t.RouteGen));
                t.Route = -1;
                t.RouteGen = 0;
                trips.Set(k, in t);
                released++;
            }
        }

        w.Work.RouteReleasesOnClose += released;
    }

    // Окно (шаги от fromStep) или план (стек DirSeg) проходят через закрытую полосу/сегмент.
    private static bool RouteThroughClosed(World w, int slot, int fromStep)
    {
        NetworkStore n = w.Net;
        RouteStore rs = w.Routes;
        int b = slot * rs.WindowSteps;
        int wc = rs.WinCount[slot];
        for (int k = Math.Max(0, fromStep); k < wc; k++)
        {
            if ((n.Lanes[rs.Window[b + k].Lane].Flags & LaneFlags.Closed) != 0)
            {
                return true;
            }
        }

        int pf = rs.PlanFirst[slot];
        int pc = rs.PlanCount[slot];
        for (int j = 0; j < pc; j++)
        {
            int u = rs.PlanArena[pf + j] & PlanDirMask;
            int seg = u >> 1;
            if ((uint)seg < (uint)n.SegmentCount && (n.Segments[seg].Flags & SegmentFlags.Closing) != 0)
            {
                return true;
            }
        }

        return false;
    }

    // Назначения на закрытых полосах: машина получает полосу прибытия своей зоны без закрытых (детерминированно, без PRNG) и перемаршрутизируется;
    // поездка в очереди теряет закрытые полосу старта/прибытия и готовый маршрут (команда с явной полосой старта без зоны снимается).
    private static void RetargetDestinations(World w, int seg)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            int d = v.DestLane[i];
            if (!v.Pool.IsAlive(i) || d < 0 || n.Lanes[d].Segment != seg)
            {
                continue;
            }

            int zone = v.DestZone[i];
            if ((uint)zone < (uint)n.ZoneCount && n.OpenZoneLaneCount(zone) > 0)
            {
                int lane = n.OpenZoneLane(zone, i % n.OpenZoneLaneCount(zone));
                int dest = Destinations.OppositeLane(n, lane);
                v.DestLane[i] = dest;
                v.DestS[i] = n.Lanes[dest].Length * Destinations.ArrivalFraction;
                if (v.Route[i] >= 0)
                {
                    RouteNav.ReleaseRoute(w, i);   // план вёл на старую полосу прибытия
                    w.Work.RouteReleasesOnClose++;
                }
            }
        }

        PendingTrips trips = w.Trips;
        for (int k = 0; k < trips.Count; k++)
        {
            PendingTrip t = trips.At(k);
            bool fromClosed = t.FromLane >= 0 && n.Lanes[t.FromLane].Segment == seg;
            bool destClosed = t.DestLane >= 0 && n.Lanes[t.DestLane].Segment == seg;
            if (!fromClosed && !destClosed)
            {
                continue;
            }

            if (fromClosed && t.FromZone < 0)
            {
                trips.RemoveAt(k--);   // поездка с явной полосой старта без зоны: стартовать негде (не машина, деспауна нет)
                w.Counters.TripsDropped++;
                continue;
            }

            if (fromClosed)
            {
                t.FromLane = -1;
            }

            if (destClosed)
            {
                t.DestLane = -1;
            }

            if (t.HasRoute)
            {
                w.Routes.Free(new RouteHandle(t.Route, t.RouteGen));
                t.Route = -1;
                t.RouteGen = 0;
            }

            trips.Set(k, in t);
        }
    }

    // Зона, у которой после закрытия этого сегмента не осталось открытых полос (а были его полосы).
    private static void EmitZoneAccessLost(World w, int seg, uint tag)
    {
        NetworkStore n = w.Net;
        for (int z = 0; z < n.ZoneCount; z++)
        {
            ref readonly ZoneData zd = ref n.Zones[z];
            if (zd.SpawnLaneCount <= 0 || n.OpenZoneLaneCount(z) > 0)
            {
                continue;
            }

            for (int k = 0; k < zd.SpawnLaneCount; k++)
            {
                if (n.Lanes[n.ZoneLanes[zd.FirstSpawnLane + k]].Segment == seg)
                {
                    w.Emit(SimEventKind.ZoneAccessLost, tag, z);
                    break;
                }
            }
        }
    }
}
