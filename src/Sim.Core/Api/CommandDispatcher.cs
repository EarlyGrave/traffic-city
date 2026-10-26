using System;
using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
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
            case CommandKind.SetPedTarget:
                if (w.Peds == null || cmd.SetPedTarget.Count < 0)
                {
                    Rejected(w, in cmd, w.Peds == null ? RejectReason.NotSupported : RejectReason.InvalidArgument);
                }
                else
                {
                    w.TargetPeds = cmd.SetPedTarget.Count;
                    Applied(w, in cmd);
                }

                break;
            case CommandKind.PaintZone:
                Result(w, in cmd, City.Zoning.Apply(w, in cmd));
                break;
            case CommandKind.SetTripExecution:
                Result(w, in cmd, Demand.AggregateExec.ApplySwitch(w, in cmd));
                break;
            case CommandKind.SetSignalPlan:
                SetSignalPlan(w, in cmd);
                break;
            case CommandKind.SetNodeControl:
                SetNodeControl(w, in cmd);
                break;
            case CommandKind.CreateBusRoute:
            case CommandKind.AppendBusStop:
            case CommandKind.SpawnBus:
                // TODO(step 4): транспорт.
                Rejected(w, in cmd, RejectReason.NotSupported);
                break;
            case CommandKind.RemoveRoad:
            case CommandKind.UpgradeRoad:
            case CommandKind.SplitRoad:
            case CommandKind.SetArmControl:
            case CommandKind.SetTurnBan:
            case CommandKind.SetLaneFlags:
            case CommandKind.InsertBusStop:
            case CommandKind.RemoveBusStop:
            case CommandKind.RemoveBusRoute:
            case CommandKind.SetBusRouteParams:
            case CommandKind.MergeAtNode:
                // Инструменты игрока (player-tools.md): номера и payload закреплены в T1, тела - T3..T9.
                Rejected(w, in cmd, RejectReason.NotSupported);
                break;
            default:
                Rejected(w, in cmd, RejectReason.UnknownKind);
                break;
        }
    }

    /// <summary>Итог обработчика города: <see cref="RejectReason.None"/> - применена, иначе отклонена с причиной.</summary>
    private static void Result(World w, in Command cmd, RejectReason reason)
    {
        if (reason == RejectReason.None)
        {
            Applied(w, in cmd);
        }
        else
        {
            Rejected(w, in cmd, reason);
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
        bool noConnectors = (b.Flags & BuildRoadFlags.NoConnectors) != 0;
        if ((b.Flags & ~BuildRoadFlags.Supported) != 0 || b.RestoreOf.Generation != 0 || b.RestoreNodeA.Generation != 0 || b.RestoreNodeB.Generation != 0)
        {
            // AutoSplit / Curve / RestoreOf - T4, T5.
            Rejected(w, in cmd, RejectReason.NotSupported);
            return;
        }

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

            // Канонический порядок раскладки (save-format.md §13 п. 5): connector'ы, конфликты и входы оверлея лежат по возрастанию индекса узла,
            // поэтому ранее созданный (флаг 1), но ещё не построенный узел с индексом ниже построенных построить нельзя (вставка в середину
            // перенумеровала бы connector'ы). Новые узлы всегда выше всех существующих.
            int top = w.Net.HighestBuiltNode();
            if ((na >= 0 && na < top) || (nb >= 0 && nb < top))
            {
                Rejected(w, in cmd, RejectReason.NotSupported);
                return;
            }
        }

        w.Builder.ResetCreated();
        NetworkStatus st = w.Builder.AddSegment(a, z, b.LanesAB, b.LanesBA, b.SpeedLimit, b.Kind, out int seg);
        for (int k = 0; k < w.Builder.CreatedNodeCount; k++)
        {
            w.Emit(SimEventKind.NodeCreated, cmd.Tag, w.Builder.CreatedNodes[k]);   // порядок создания (в дыры сети индексы не растут)
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
            int lo = Math.Min(sd.NodeA, sd.NodeB);   // по возрастанию индекса узла (канонический порядок раскладки)
            int hi = Math.Max(sd.NodeA, sd.NodeB);
            NetworkStatus c1 = w.Builder.BuildConnectorsEx(lo);
            NetworkStatus c2 = w.Builder.BuildConnectorsEx(hi);
            NetworkStatus bad = c1 != NetworkStatus.Ok ? c1 : c2;
            if (bad == NetworkStatus.Ok)
            {
                // Конфликтные пары новых узлов (intersections.md §3): без них ворота не видят пересечений.
                NetworkStatus k1 = w.Connectors.BuildConflicts(w.Net, lo);
                NetworkStatus k2 = w.Connectors.BuildConflicts(w.Net, hi);
                bad = k1 != NetworkStatus.Ok ? k1 : k2;
            }

            if (bad != NetworkStatus.Ok)
            {
                // Сегмент уже добавлен, но connector'ов нет (ёмкость): сообщаем об отказе, сеть остаётся согласованной по версии.
                GrowOverlay(w);
                RejectNetwork(w, in cmd, bad);
                return;
            }
        }

        GrowOverlay(w);
        Applied(w, in cmd);
    }

    // Сеть выросла (routing.md 9.3 "BuildRoad"): граф пересобирается сразу, оверлей дописывает новые узлы/входы/выходы,
    // помечает ячейки грязными или (не хватило запаса) перестраивается целиком.
    private static void GrowOverlay(World w)
    {
        w.Peds?.OnNetworkGrown(w); // сеть пешеходов - производное сети: пересобирается целиком (pedestrians.md §2); при выключенных peds - null
        w.Lod?.OnNetworkGrown(); // LOD-ячейки: новые узлы и полосы получают ячейку (sim-lod.md §1); при Lod.Mode = Off - null
        w.Vehicles.MarkAllRouteDirty();
        // Rebuild сбрасывает SegCost к свободному потоку: возвращаем веса по текущей загрузке, иначе под нагрузкой пороговая
        // проверка оверлея пометит почти все ячейки и волна будет огромной.
        w.Graph.Rebuild();
        w.Graph.RecalcCosts(w.EnsureSegmentLoads(), w.Net.SegmentCount); // машины ещё не двигались в этом тике: нагрузка конца прошлого
        Routing.Overlay? ov = w.Overlay;
        if (ov == null || ov.Disabled)
        {
            return;
        }

        ov.OnNetworkGrown();
        w.Counters.OverlayRebuilds = ov.Rebuilds;
        w.Counters.OverlayDisabled = ov.Disabled ? 1 : 0;
    }

    /// <summary>
    /// intersections.md 6.3, 6.5: узел должен быть <c>Signal</c>; для <c>Fixed</c>/<c>Adaptive</c> PhaseCount = числу осей узла, Green_k >= 1,
    /// цикл <= 65535 (Adaptive: ещё MinGreen/Gap/стрелка и инвариант красного); иначе <c>InvalidArgument</c> (отдельных причин NotSignalized/InvalidPhaseCount в RejectReason нет).
    /// Применяется в фазе команд тика, начальное состояние пересчитывается по OffsetTicks.
    /// </summary>
    private static void SetSignalPlan(World w, in Command cmd)
    {
        SetSignalPlanCmd p = cmd.SetSignalPlan;
        if ((uint)p.Node >= (uint)w.Net.NodeCount)
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
            return;
        }

        if (!CheckNodeHandle(w, in cmd, p.Node, p.NodeGen))
        {
            return;
        }

        ref readonly NodeData nd = ref w.Net.Nodes[p.Node];
        if (nd.Control != NodeControl.Signal || nd.Signal < 0 || nd.Signal >= w.Signals.Count)
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
            return;
        }

        // 2.5: нулевые параметры Adaptive/стрелки -> дефолты RulesDesc; затем проверка (для Adaptive - с инвариантом красного, intersections.md 5.6).
        int axes = w.Signals.PhaseCount[nd.Signal];
        SetSignalPlanCmd resolved = SignalStore.Resolve(in p, axes, in w.Rules);
        if (!SignalStore.IsValid(in resolved, axes, enforceRed: true, pedGreenTicks: Sim.Core.Peds.PedSignals.CoordMinGreenTicks(w, p.Node)))
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
            return;
        }

        w.Signals.ApplyPlan(nd.Signal, in resolved, w.Tick);
        w.Vehicles.MarkAllRouteDirty();
        Applied(w, in cmd);
    }

    /// <summary>
    /// intersections.md 6.3. Signal: слот в SignalStore + план по умолчанию (нет слота - Capacity; узел без connector'ов - InvalidArgument).
    /// Из Signal в другой режим: swap-remove слота. TODO(J3): пересчёт ArmFlags/Rel и сброс допусков узла (5.7).
    /// </summary>
    private static void SetNodeControl(World w, in Command cmd)
    {
        SetNodeControlCmd c = cmd.SetNodeControl;
        if ((uint)c.Node >= (uint)w.Net.NodeCount || (uint)c.Control > (uint)NodeControl.RightHandRule)
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
            return;
        }

        if (!CheckNodeHandle(w, in cmd, c.Node, c.NodeGen))
        {
            return;
        }

        ref NodeData nd = ref w.Net.Nodes[c.Node];
        if (c.Control == NodeControl.Signal)
        {
            if (nd.Signal < 0)
            {
                if (w.Signals.Count >= w.Signals.Capacity)
                {
                    Rejected(w, in cmd, RejectReason.Capacity);
                    return;
                }

                int axes = SignalStore.AxesOf(w, c.Node);
                SetSignalPlanCmd plan = SignalStore.DefaultPlan(axes, in w.Rules);
                if (axes < 1 || !w.Signals.TryAdd(w, c.Node, in plan, out _))
                {
                    Rejected(w, in cmd, RejectReason.InvalidArgument);
                    return;
                }
            }
        }
        else if (nd.Signal >= 0)
        {
            w.Signals.Remove(w, nd.Signal);
        }

        if (nd.Control != c.Control)
        {
            // intersections.md §5.7, §6.3: смена режима пересчитывает ArmFlags (с нормализацией Yield/Stop без второстепенных рукавов
            // в RightHandRule), Rel конфликтов узла и сбрасывает допуски машин на его входных полосах. Version++ делает Builder.
            w.Builder.SetNodeControl(c.Node, c.Control);
            NodeControl applied = w.Net.Nodes[c.Node].Control;
            w.Connectors.RecomputeRel(w.Net, c.Node, applied == NodeControl.Yield || applied == NodeControl.Stop);
            GatePhase.ResetNode(w, c.Node);
            w.Vehicles.MarkAllRouteDirty();
        }

        Applied(w, in cmd);
    }

    /// <summary>
    /// Handle узла (индекс + поколение, player-tools.md §2, ADR-0023): слот жив и (при <paramref name="gen"/> != 0) поколение совпало, иначе <c>InvalidHandle</c>
    /// и <c>InvalidHandleOps++</c>. <c>gen == 0</c> - без проверки поколения (генератор, тесты); мёртвый слот отклоняется всегда.
    /// </summary>
    private static bool CheckNodeHandle(World w, in Command cmd, int node, int gen) => CheckEntity(w, in cmd, EntityKind.Node, new EntityRef(node, gen));

    /// <summary>
    /// Проверка <see cref="EntityRef"/> команды (узел, сегмент, остановка, маршрут): слот жив и поколение совпало (<c>Generation == 0</c> - без проверки поколения), иначе
    /// <c>InvalidHandle</c> (+<c>InvalidHandleOps</c>) и <c>false</c>. Общая точка для команд инструментов T3..T9: ни одна из них не обращается к слоту до этой проверки.
    /// </summary>
    internal static bool CheckEntity(World w, in Command cmd, EntityKind kind, in EntityRef r)
    {
        NetworkStore n = w.Net;
        bool ok = kind switch
        {
            EntityKind.Node => n.ResolveNode(in r),
            EntityKind.Segment => n.ResolveSegment(in r),
            EntityKind.BusStop => n.ResolveBusStop(in r),
            EntityKind.BusRoute => n.ResolveBusRoute(in r),
            _ => false,
        };
        if (!ok)
        {
            w.Counters.InvalidHandleOps++;
            Rejected(w, in cmd, RejectReason.InvalidHandle);
        }

        return ok;
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
            DestLane = -1,   // точку прибытия в зоне ToZone и маршрут выбирает фаза 3
            Route = -1,
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
