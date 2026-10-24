using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Api;

/// <summary>
/// Фаза 1 (core-api 4.2, 7): применяет всю очередь команд строго FIFO. Каждая команда даёт ровно одно
/// <see cref="SimEventKind.CommandApplied"/> / <see cref="SimEventKind.CommandRejected"/> с эхом Tag.
/// Applied: <c>A = (int)CommandKind</c>; Rejected: <c>A = (int)RejectReason</c>, <c>B = (int)CommandKind</c>.
/// </summary>
internal static class CommandDispatcher
{
    public static void Run(World w)
    {
        while (w.Commands.TryDequeue(out Command cmd))
        {
            Apply(w, in cmd);
        }
    }

    private static void Apply(World w, in Command cmd)
    {
        switch (cmd.Kind)
        {
            case CommandKind.BuildRoad:
                BuildRoad(w, in cmd);
                break;
            case CommandKind.SpawnTrip:
                SpawnTrip(w, in cmd);
                break;
            case CommandKind.SelectVehicle:
                SelectVehicle(w, in cmd);
                break;
            case CommandKind.ClearSelection:
                ClearSelection(w);
                Applied(w, in cmd);
                break;
            case CommandKind.SetTargetVehicleCount:
                if (cmd.SetTargetVehicleCount.Count < 0)
                {
                    Rejected(w, in cmd, RejectReason.InvalidArgument);
                }
                else
                {
                    w.TargetVehicles = cmd.SetTargetVehicleCount.Count;
                    Applied(w, in cmd);
                }

                break;
            case CommandKind.SetSignalPlan:
            case CommandKind.CreateBusRoute:
            case CommandKind.AppendBusStop:
            case CommandKind.SpawnBus:
            case CommandKind.SetNodeControl:
                // TODO(step 3/4): светофоры, регулирование узлов, транспорт.
                Rejected(w, in cmd, RejectReason.NotSupported);
                break;
            default:
                Rejected(w, in cmd, RejectReason.UnknownKind);
                break;
        }
    }

    private static void Applied(World w, in Command cmd)
    {
        w.Counters.CommandsApplied++;
        w.Emit(SimEventKind.CommandApplied, cmd.Tag, (int)cmd.Kind);
    }

    private static void Rejected(World w, in Command cmd, RejectReason reason)
    {
        w.Counters.CommandsRejected++;
        w.Emit(SimEventKind.CommandRejected, cmd.Tag, (int)reason, (int)cmd.Kind);
    }

    private static void BuildRoad(World w, in Command cmd)
    {
        BuildRoadCmd b = cmd.BuildRoad;
        bool noConnectors = (b.Flags & 1) != 0;
        if (!float.IsFinite(b.Ax) || !float.IsFinite(b.Az) || !float.IsFinite(b.Bx) || !float.IsFinite(b.Bz) ||
            !float.IsFinite(b.SpeedLimit) || b.SpeedLimit <= 0f || b.LanesAB + b.LanesBA == 0)
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
            return;
        }

        var a = new Vec2(b.Ax, b.Az);
        var z = new Vec2(b.Bx, b.Bz);
        if (!noConnectors)
        {
            // Узел строится один раз (NodeAlreadyBuilt): новый сегмент в уже построенный узел не получил бы connector'ов.
            // TODO(step 3): перестройка узла при добавлении рукава.
            int na = w.Builder.FindNode(a);
            int nb = w.Builder.FindNode(z);
            if ((na >= 0 && w.Net.Nodes[na].FirstArm >= 0) || (nb >= 0 && w.Net.Nodes[nb].FirstArm >= 0))
            {
                Rejected(w, in cmd, RejectReason.NotSupported);
                return;
            }
        }

        int nodesBefore = w.Net.NodeCount;
        NetworkStatus st = w.Builder.AddSegment(a, z, b.LanesAB, b.LanesBA, b.SpeedLimit, b.Kind, out int seg);
        for (int n = nodesBefore; n < w.Net.NodeCount; n++)
        {
            w.Emit(SimEventKind.NodeCreated, cmd.Tag, n);
        }

        if (st != NetworkStatus.Ok)
        {
            RejectNetwork(w, in cmd, st);
            return;
        }

        w.Emit(SimEventKind.SegmentCreated, cmd.Tag, seg);
        if (!noConnectors)
        {
            ref readonly SegmentData sd = ref w.Net.Segments[seg];
            NetworkStatus c1 = w.Builder.BuildConnectorsEx(sd.NodeA);
            NetworkStatus c2 = w.Builder.BuildConnectorsEx(sd.NodeB);
            NetworkStatus bad = c1 != NetworkStatus.Ok ? c1 : c2;
            if (bad != NetworkStatus.Ok)
            {
                // Сегмент уже добавлен, но connector'ов нет (ёмкость): сообщаем об отказе, сеть остаётся согласованной по версии.
                RejectNetwork(w, in cmd, bad);
                return;
            }
        }

        Applied(w, in cmd);
    }

    private static void RejectNetwork(World w, in Command cmd, NetworkStatus st)
    {
        if (st.IsCapacity())
        {
            CapacityKind kind = st switch
            {
                NetworkStatus.NodesFull => CapacityKind.Nodes,
                NetworkStatus.SegmentsFull => CapacityKind.Segments,
                NetworkStatus.LanesFull => CapacityKind.Lanes,
                _ => CapacityKind.LanePoints,
            };
            w.Emit(SimEventKind.CapacityExhausted, cmd.Tag, (int)kind, 1);
            Rejected(w, in cmd, RejectReason.Capacity);
        }
        else
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
        }
    }

    private static void SpawnTrip(World w, in Command cmd)
    {
        SpawnTripCmd t = cmd.SpawnTrip;
        bool zoneOk = (uint)t.ToZone < (uint)w.Net.ZoneCount;
        bool fromZone = (uint)t.FromZone < (uint)w.Net.ZoneCount;
        bool fromLane = (uint)t.FromLane < (uint)w.Net.LaneCount;
        if (fromLane)
        {
            ref readonly LaneData l = ref w.Net.Lanes[t.FromLane];
            fromLane = l.Kind == LaneKind.Normal && float.IsFinite(t.FromS) && t.FromS >= 0f && t.FromS <= l.Length;
        }

        if (!zoneOk || (!fromZone && !fromLane) || (uint)t.VehicleType >= (uint)w.Types.Length || t.Count < 1)
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
            return;
        }

        var trip = new PendingTrip
        {
            FromZone = fromZone && !fromLane ? t.FromZone : -1,
            ToZone = t.ToZone,
            FromLane = fromLane ? t.FromLane : -1,
            FromS = fromLane ? t.FromS : 0f,
            Type = (byte)t.VehicleType,
        };

        int free = w.Trips.Capacity - w.Trips.Count;
        int accepted = t.Count < free ? t.Count : free;
        for (int i = 0; i < accepted; i++)
        {
            w.Trips.TryEnqueue(in trip);
        }

        int dropped = t.Count - accepted;
        if (dropped > 0)
        {
            w.Counters.TripsDropped += dropped;
            w.Emit(SimEventKind.CapacityExhausted, cmd.Tag, (int)CapacityKind.PendingTrips, dropped);
        }

        if (accepted > 0)
        {
            Applied(w, in cmd);
        }
        else
        {
            Rejected(w, in cmd, RejectReason.Capacity);
        }
    }

    private static void SelectVehicle(World w, in Command cmd)
    {
        var h = new VehicleHandle(cmd.SelectVehicle.Index, cmd.SelectVehicle.Generation);
        if (!w.Vehicles.IsAlive(h))
        {
            w.Counters.InvalidHandleOps++;
            Rejected(w, in cmd, RejectReason.InvalidHandle);
            return;
        }

        ClearSelection(w);
        w.Selected = h;
        w.Vehicles.Flags[h.Index] |= (byte)VehicleFlags.Selected;
        Applied(w, in cmd);
    }

    private static void ClearSelection(World w)
    {
        if (w.Vehicles.IsAlive(w.Selected))
        {
            w.Vehicles.Flags[w.Selected.Index] &= unchecked((byte)~(byte)VehicleFlags.Selected);
        }

        w.Selected = VehicleHandle.Null;
    }
}
