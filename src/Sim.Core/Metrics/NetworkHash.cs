using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Metrics;

/// <summary>
/// Хэши статики сети для кросс-языкового golden Rust-пилота (rust-pilot.md §5.2): FNV-1a 64, порядок и состав зафиксированы спекой.
/// Не входят в хэш состояния симуляции и не влияют на него.
/// </summary>
internal static class NetworkHash
{
    /// <summary>Версия формата сетевых хэшей; меняется вместе с <c>golden_version</c>.</summary>
    public const int Version = 1;

    /// <summary>Сеть после построения и <see cref="ConnectorStore"/> (rust-pilot.md §5.2, разделы 1-7).</summary>
    public static ulong Network(World w)
    {
        NetworkStore net = w.Net;
        ConnectorStore cs = w.Connectors;
        var h = Fnv1a64.Create();
        h.Add(Version);
        h.Add(net.NodeCount);
        h.Add(net.SegmentCount);
        h.Add(net.LaneCount);
        h.Add(net.LanePointCount);
        h.Add(net.SegmentPointCount);
        h.Add(net.NodeArmCount);
        h.Add(net.LaneNextCount);
        h.Add(cs.Count);
        h.Add(cs.ConflictsUsed);

        for (int i = 0; i < net.NodeCount; i++)
        {
            ref readonly NodeData n = ref net.Nodes[i];
            h.Add(n.Position.X);
            h.Add(n.Position.Z);
            h.Add(n.Elevation);
            h.Add(n.Radius);
            h.Add((int)n.Control);
            h.Add(n.FirstArm);
            h.Add(n.ArmCount);
            h.Add(n.FirstConnector);
            h.Add(n.ConnectorCount);
            h.Add(n.Signal);
            h.Add(n.Zone);
            h.Add(n.Generation);
        }

        for (int i = 0; i < net.SegmentCount; i++)
        {
            ref readonly SegmentData s = ref net.Segments[i];
            h.Add(s.NodeA);
            h.Add(s.NodeB);
            h.Add(s.Length);
            h.Add(s.SpeedLimit);
            h.Add((byte)s.Kind);
            h.Add(s.LanesAB);
            h.Add(s.LanesBA);
            h.Add(s.Flags);
            h.Add(s.FirstLaneAB);
            h.Add(s.FirstLaneBA);
            h.Add(s.FirstPoint);
            h.Add(s.PointCount);
            h.Add(s.Generation);
        }

        for (int i = 0; i < net.LaneCount; i++)
        {
            ref readonly LaneData l = ref net.Lanes[i];
            h.Add((byte)l.Kind);
            h.Add(l.LaneNo);
            h.Add(l.Direction);
            h.Add(l.Flags);
            h.Add(l.Segment);
            h.Add(l.Node);
            h.Add(l.FromLane);
            h.Add(l.ToLane);
            h.Add(l.Length);
            h.Add(l.SpeedLimit);
            h.Add(l.FirstPoint);
            h.Add(l.PointCount);
            h.Add(l.Left);
            h.Add(l.Right);
            h.Add(l.FirstNext);
            h.Add(l.NextCount);
            h.Add(l.ConflictGroup);
            h.Add(l.Generation);
            h.Add(net.LaneLength[i]);
            h.Add(net.LaneGateNode[i]);
        }

        for (int i = 0; i < net.NodeArmCount; i++)
        {
            h.Add(net.NodeArms[i]);
            h.Add(net.ArmFlags[i]);
            h.Add(net.ArmDir[i].X);
            h.Add(net.ArmDir[i].Z);
        }

        for (int i = 0; i < net.LaneNextCount; i++)
        {
            h.Add(net.LaneNext[i]);
        }

        for (int i = 0; i < net.SegmentPointCount; i++)
        {
            h.Add(net.SegmentPoints[i].X);
            h.Add(net.SegmentPoints[i].Z);
        }

        for (int i = 0; i < net.LanePointCount; i++)
        {
            h.Add(net.LanePoints[i].X);
            h.Add(net.LanePoints[i].Z);
            h.Add(net.LanePointS[i]);
        }

        for (int i = 0; i < cs.Count; i++)
        {
            h.Add(cs.Lane[i]);
            h.Add(cs.Node[i]);
            h.Add(cs.InLane[i]);
            h.Add(cs.OutLane[i]);
            h.Add(cs.Length[i]);
            h.Add(cs.InLength[i]);
            h.Add(cs.LocalBit[i]);
            h.Add(cs.Maneuver[i]);
            h.Add(cs.FromArm[i]);
            h.Add(cs.Flags[i]);
        }

        return h.Value;
    }

    /// <summary>Конфликты connector'ов (rust-pilot.md §5.2); <c>ushort</c> - два байта little-endian.</summary>
    public static ulong Conflicts(World w)
    {
        ConnectorStore cs = w.Connectors;
        var h = Fnv1a64.Create();
        h.Add(Version);
        h.Add(cs.Count);
        h.Add(cs.ConflictsUsed);
        for (int i = 0; i < cs.Count; i++)
        {
            h.Add(cs.FirstConflict[i]);
            h.Add(cs.ConflictCount[i]);
            h.Add(cs.ConflictMask[i]);
        }

        for (int i = 0; i < cs.ConflictsUsed; i++)
        {
            ref readonly ConflictEntry e = ref cs.Conflicts[i];
            h.Add(e.OtherLocal);
            h.Add(e.Flags);
            AddU16(ref h, e.SMineCm);
            AddU16(ref h, e.SOtherCm);
            AddU16(ref h, e.Reserved);
        }

        return h.Value;
    }

    /// <summary>Курс отрезков полос <c>LanePieceHeading</c> (atan2, системный libm; не блокирует golden).</summary>
    public static ulong Headings(World w)
    {
        NetworkStore net = w.Net;
        var h = Fnv1a64.Create();
        h.Add(Version);
        h.Add(net.LanePointCount);
        for (int i = 0; i < net.LanePointCount; i++)
        {
            h.Add(net.LanePieceHeading[i]);
        }

        return h.Value;
    }

    /// <summary>Разобранный сценарий до <c>Create</c>: seed, ёмкости, явная сеть, параметры построителя (<see cref="NetworkOptions.Default"/>).</summary>
    public static ulong Desc(in ScenarioDesc d)
    {
        var h = Fnv1a64.Create();
        h.Add(Version);
        h.Add(d.Seed);
        SimCapacities c = d.Capacities;
        h.Add(c.MaxVehicles);
        h.Add(c.MaxNodes);
        h.Add(c.MaxSegments);
        h.Add(c.MaxLanes);
        h.Add(c.MaxLanePoints);
        h.Add(c.MaxZones);
        h.Add(c.MaxBusRoutes);
        h.Add(c.MaxBusStops);
        h.Add(c.MaxBusStopsPerRoute);
        h.Add(c.MaxRoutes);
        h.Add(c.MaxPlanSteps);
        h.Add(c.MaxPendingTrips);
        h.Add(c.CommandQueueCapacity);
        h.Add(c.EventCapacity);
        h.Add(c.MaxSignalizedNodes);
        h.Add(c.MaxConnectors);
        h.Add(c.MaxConflicts);
        h.Add(c.MaxPeds);
        h.Add(c.MaxResidents);
        h.Add(c.MaxPedPathSteps);
        h.Add(c.MaxLots);
        h.Add(c.MaxBuildings);
        h.Add(c.MaxCityResidents);
        h.Add(c.SkimObsCapacity);

        NodeDesc[] nodes = d.Network.Nodes ?? [];
        h.Add(nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            h.Add(nodes[i].X);
            h.Add(nodes[i].Z);
            h.Add((int)nodes[i].Control);
        }

        SegmentDesc[] segs = d.Network.Segments ?? [];
        h.Add(segs.Length);
        for (int i = 0; i < segs.Length; i++)
        {
            h.Add(segs[i].A);
            h.Add(segs[i].B);
            h.Add(segs[i].LanesAB);
            h.Add(segs[i].LanesBA);
            h.Add(segs[i].SpeedLimit);
            h.Add((byte)segs[i].Kind);
        }

        NetworkOptions o = NetworkOptions.Default;
        h.Add(o.LaneWidth);
        h.Add(o.SnapRadius);
        h.Add(o.NodeSetback);
        h.Add(o.ConnectorSamples);
        h.Add(o.MinLaneLength);
        h.Add(NetworkOptions.CornerRadiusFromDesc(d.Network.CornerRadius));   // ADR-0028: действующий радиус явной сети (golden перегенерирован, golden_version прежний)
        return h.Value;
    }

    private static void AddU16(ref Fnv1a64 h, ushort v)
    {
        h.Add((byte)v);
        h.Add((byte)(v >> 8));
    }
}
