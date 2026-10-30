using System;
using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tools;
using Sim.Core.Util;

namespace Sim.Core.Api;

/// <summary>
/// Фаза 1 (core-api 4.2, 7): применяет всю очередь команд строго FIFO. Каждая команда даёт ровно одно
/// <see cref="SimEventKind.CommandApplied"/> / <see cref="SimEventKind.CommandRejected"/> с эхом Tag.
/// Applied: <c>A = (int)CommandKind</c>; Rejected: <c>A = (int)RejectReason</c>, <c>B = (int)CommandKind</c>.
/// </summary>
internal static partial class CommandDispatcher
{
    public static void Run(World w)
    {
        ToolOpsPhase.Run(w);   // 1а: финалы дренажа по состоянию конца прошлого тика (player-tools.md §12)
        while (w.Commands.TryDequeue(out Command cmd))
        {
            if (w.Inv.RemapCount > 0)
            {
                // Перенумерация фазы 1 (player-tools.md §8.2): ссылки на пересозданные сущности переводятся на новые handle'ы.
                CommandRefs.Remap(ref cmd, new RemapTable(new ReadOnlySpan<RemapEntry>(w.Inv.Remap, 0, w.Inv.RemapCount)));
            }

            InvCtx inv = InverseBuilder.Begin(w, in cmd);
            long applied0 = w.Counters.CommandsApplied;
            Apply(w, in cmd);
            InverseBuilder.End(w, in cmd, ref inv, w.Counters.CommandsApplied != applied0);
        }

        w.Inv.ClearRemap();
        RoadTerrain.Sync(w);   // TR3: высоты полос и Lock после правок сети этого тика (без рельефа - ничего)
        ToolCosts.Purge(w);   // E4: просроченные undo-кредиты не доживают до сейва
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
            case CommandKind.SetOnCall:
                SetOnCall(w, in cmd);
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
                Result(w, in cmd, Locked(w, in cmd) ? RejectReason.Locked : City.Zoning.Apply(w, in cmd));
                break;
            case CommandKind.SetTripExecution:
                Result(w, in cmd, Demand.AggregateExec.ApplySwitch(w, in cmd));
                break;
            case CommandKind.SetBudgetPolicy:
                Result(w, in cmd, Locked(w, in cmd) ? RejectReason.Locked : City.BudgetFlows.SetPolicy(w, in cmd));
                break;
            case CommandKind.SetSignalPlan:
                SetSignalPlan(w, in cmd);
                break;
            case CommandKind.SetNodeControl:
                SetNodeControl(w, in cmd);
                break;
            case CommandKind.CreateBusRoute:
                CreateBusRoute(w, in cmd);
                break;
            case CommandKind.AppendBusStop:
            case CommandKind.InsertBusStop:
                InsertBusStop(w, in cmd);
                break;
            case CommandKind.RemoveBusStop:
                RemoveBusStop(w, in cmd);
                break;
            case CommandKind.RemoveBusRoute:
                RemoveBusRoute(w, in cmd);
                break;
            case CommandKind.SetBusRouteParams:
                SetBusRouteParams(w, in cmd);
                break;
            case CommandKind.SpawnBus:
                Rejected(w, in cmd, RejectReason.NotSupported);   // выпуск - по интервалу линии (BusDispatch)
                break;
            case CommandKind.SetArmControl:
                SetArmControl(w, in cmd);
                break;
            case CommandKind.SetTurnBan:
                SetTurnBan(w, in cmd);
                break;
            case CommandKind.SetLaneFlags:
                SetLaneFlags(w, in cmd);
                break;
            case CommandKind.SetStreetAccess:
                SetStreetAccess(w, in cmd);
                break;
            case CommandKind.RemoveRoad:
                RemoveRoad(w, in cmd);
                break;
            case CommandKind.UpgradeRoad:
                UpgradeRoad(w, in cmd);
                break;
            case CommandKind.SplitRoad:
                SplitRoad(w, in cmd);
                break;
            case CommandKind.MergeAtNode:
                MergeAtNode(w, in cmd);
                break;
            case CommandKind.DemolishBuilding:
                DemolishBuilding(w, in cmd);
                break;
            default:
                Rejected(w, in cmd, RejectReason.UnknownKind);
                break;
        }
    }

    /// <summary>
    /// Замок прогрессии (PG3, progression.md §4.3) для команд вне валидатора инструментов (<c>PaintZone</c>, <c>SetBudgetPolicy</c>): структурная проверка зонирования - та же, что в превью
    /// (<see cref="Tools.ToolPreview"/>), замок - до мутации. Без прогрессии - false сразу.
    /// </summary>
    private static bool Locked(World w, in Command cmd)
    {
        if (w.Progression == null)
        {
            return false;
        }

        if (cmd.Kind == CommandKind.PaintZone && City.Zoning.Check(w, in cmd.PaintZone, out _) is RejectReason.InvalidArgument or RejectReason.NotSupported)
        {
            return false;   // структурный отказ выдаст сам обработчик
        }

        return City.Progression.Check(w, in cmd, out _) == RejectReason.Locked;
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

    /// <summary>Применена платная команда инструмента: проводка в казне (E4, <see cref="ToolCosts.Settle"/>), затем <c>CommandApplied</c>. Сегмент, созданный прямой <c>BuildRoad</c>, - в <c>w.Inv.BuiltSeg</c>.</summary>
    private static void AppliedPaid(World w, in Command cmd, in ToolCheck chk)
    {
        int ti = -1;
        int tg = 0;
        if (cmd.Kind == CommandKind.BuildRoad && !chk.Reopen && w.Inv.BuiltSeg >= 0)
        {
            ti = w.Inv.BuiltSeg;
            tg = w.Net.Segments[ti].Generation;
        }
        else if (cmd.Kind == CommandKind.CreateBusRoute && w.Inv.BuiltRoute >= 0)
        {
            ti = ToolCosts.RouteTarget(w.Inv.BuiltRoute);
            tg = w.Net.BusRoutes[w.Inv.BuiltRoute].Generation;
        }
        else if (cmd.Kind is CommandKind.InsertBusStop or CommandKind.AppendBusStop && w.Inv.BuiltStop >= 0)
        {
            ti = ToolCosts.StopTarget(w.Inv.BuiltStop);
            tg = w.Net.BusStops[w.Inv.BuiltStop].Generation;
        }

        ToolCosts.Settle(w, in cmd, in chk, ti, tg);
        Applied(w, in cmd);
    }

    private static void Applied(World w, in Command cmd)
    {
        w.Counters.CommandsApplied++;
        w.Emit(SimEventKind.CommandApplied, cmd.Tag, (int)cmd.Kind);
    }

    /// <summary>Отказ валидатора инструментов: счётчик неверных handle, событие исчерпанной ёмкости (если валидатор её назвал), затем <c>CommandRejected</c>.</summary>
    private static void RejectTool(World w, in Command cmd, RejectReason reason, CapacityKind cap = 0)
    {
        if (reason == RejectReason.InvalidHandle)
        {
            w.Counters.InvalidHandleOps++;
        }

        if (reason == RejectReason.InsufficientFunds)
        {
            w.Counters.InsufficientFunds++;
        }

        if (reason == RejectReason.Capacity && cap != 0)
        {
            w.Emit(SimEventKind.CapacityExhausted, cmd.Tag, (int)cap, 1);
        }

        Rejected(w, in cmd, reason);
    }

    private static void Rejected(World w, in Command cmd, RejectReason reason)
    {
        if (reason == RejectReason.Locked)
        {
            w.Counters.LockedRejects++;
        }

        w.Counters.CommandsRejected++;
        w.Emit(SimEventKind.CommandRejected, cmd.Tag, (int)reason, (int)cmd.Kind);
    }

    private static void BuildRoad(World w, in Command cmd)
    {
        // Общий валидатор превью и применения (player-tools.md §7.1): аргументы, привязка, границы, длина, рукава, ловушка, ёмкости - до первой мутации.
        RoadTerrain.Sync(w);   // TR3: Lock и высоты полос - по сети после предыдущих команд тика (валидатор читает их)
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        if (chk.Reopen)
        {
            ReopenRoad(w, in cmd, in chk, chk.Seg);   // отмена сноса: живой закрывающийся сегмент переоткрывается (player-tools.md §8.2)
            return;
        }

        BuildRoadCmd b = cmd.BuildRoad;
        bool noConnectors = (b.Flags & BuildRoadFlags.NoConnectors) != 0;
        var a = new Vec2(b.Ax, b.Az);
        var z = new Vec2(b.Bx, b.Bz);
        if (!SplitEnds(w, in cmd, in chk, ref a, ref z))
        {
            return;
        }

        if (!noConnectors)
        {
            // Врезка в построенный узел (4.3, T3): рукав добавляет перестройка узла.
            int na = chk.NodeA;
            int nb = chk.NodeB;

            // Канонический порядок раскладки (save-format.md §13 п. 5): connector'ы, конфликты и входы оверлея лежат по возрастанию индекса узла, пока сеть не правили.
            // Построить ранее созданный (флаг 1), но ещё не построенный узел с индексом ниже построенных - уже правка раскладки: ToolsTouched (раскладка - состояние).
            int top = w.Net.HighestBuiltNode();
            if ((na >= 0 && na < top && w.Net.Nodes[na].FirstArm < 0) || (nb >= 0 && nb < top && w.Net.Nodes[nb].FirstArm < 0))
            {
                w.Net.ToolsTouched = 1;
            }
        }

        w.Builder.ResetCreated();
        NetworkStatus st;
        int seg;
        Span<Vec2> curve = stackalloc Vec2[NetworkBuilder.MaxCurvePoints];
        if (chk.Profile >= 0 || chk.Curve)
        {
            // Дорога по профилю (RP7) и/или кривая (T5): концы прилипают или создаются, полосы и тип - из профиля; ось кривой - сэмплы Безье между узлами концов; отступы - по рукавам узлов.
            st = w.Builder.AddNode(a, NodeControl.None, out int pa);
            seg = -1;
            if (st == NetworkStatus.Ok)
            {
                st = w.Builder.AddNode(z, NodeControl.None, out int pb);
                if (st == NetworkStatus.Ok)
                {
                    scoped ReadOnlySpan<Vec2> via = ReadOnlySpan<Vec2>.Empty;
                    var cp = new Vec2(b.ControlX, b.ControlZ);
                    if (chk.Curve)
                    {
                        int np = NetworkBuilder.SampleQuadratic(w.Net.Nodes[pa].Position, cp, w.Net.Nodes[pb].Position, curve);
                        via = curve[1..(np - 1)];
                    }

                    st = chk.Profile >= 0
                        ? w.Builder.AddProfileSegment(pa, pb, via, chk.Profile, chk.Mirrored, b.SpeedLimit, out seg)
                        : w.Builder.AddSegment(pa, pb, via, b.LanesAB, b.LanesBA, b.SpeedLimit, b.Kind, out seg);
                    if (st == NetworkStatus.Ok && chk.Curve)
                    {
                        ref SegmentData sd = ref w.Net.Segments[seg];
                        sd.Flags |= SegmentFlags.Curve;
                        sd.Cx = cp.X;
                        sd.Cz = cp.Z;
                        w.Net.ToolsTouched = 1;   // флаг Curve и контрольная точка - в хэше и сейве только с правками
                    }
                }
            }
        }
        else
        {
            st = w.Builder.AddSegment(a, z, b.LanesAB, b.LanesBA, b.SpeedLimit, b.Kind, out seg);
        }

        if (st == NetworkStatus.Ok && !noConnectors && !CheckBuiltEnds(w, in cmd, seg))
        {
            return;
        }

        for (int k = 0; k < w.Builder.CreatedNodeCount; k++)
        {
            w.Emit(SimEventKind.NodeCreated, cmd.Tag, w.Builder.CreatedNodes[k]);   // порядок создания (в дыры сети индексы не растут)
        }

        if (st != NetworkStatus.Ok)
        {
            RejectNetwork(w, in cmd, st);
            return;
        }

        if (w.Terrain != null)
        {
            ApplyRoadTerrain(w, in chk, seg);
        }

        w.Emit(SimEventKind.SegmentCreated, cmd.Tag, seg);
        w.Inv.BuiltSeg = seg;
        EmitRestoreRemaps(w, in cmd, seg);
        bool rebuilt = false;
        if (!noConnectors)
        {
            ref readonly SegmentData sd = ref w.Net.Segments[seg];
            int lo = Math.Min(sd.NodeA, sd.NodeB);   // по возрастанию индекса узла (канонический порядок раскладки)
            int hi = Math.Max(sd.NodeA, sd.NodeB);
            NetworkStatus c1 = BuildOrRebuildEnd(w, in cmd, lo, ref rebuilt);
            NetworkStatus c2 = BuildOrRebuildEnd(w, in cmd, hi, ref rebuilt);
            NetworkStatus bad = c1 != NetworkStatus.Ok ? c1 : c2;
            if (bad == NetworkStatus.Ok)
            {
                // Конфликтные пары новых узлов (intersections.md §3): без них ворота не видят пересечений (у перестроенных узлов строки уже есть).
                NetworkStatus k1 = BuildConflictsIfFresh(w, lo);
                NetworkStatus k2 = BuildConflictsIfFresh(w, hi);
                bad = k1 != NetworkStatus.Ok ? k1 : k2;
            }

            if (bad != NetworkStatus.Ok)
            {
                // Сегмент уже добавлен, но connector'ов нет (ёмкость): сообщаем об отказе, сеть остаётся согласованной по версии.
                GrowOverlay(w, rebuilt);
                w.City?.LotGen.OnRoadBuilt(w, seg);   // сегмент уже в сети: участки вдоль него нужны, даже если connector'ы не построились
                RejectNetwork(w, in cmd, bad);
                return;
            }
        }

        GrowOverlay(w, rebuilt);
        w.City?.LotGen.OnRoadBuilt(w, seg);   // Z6: участки вдоль нового сегмента (после пересборки сети пешеходов)
        AppliedPaid(w, in cmd, in chk);
    }

    // TR3 (terrain.md §4.1, §4.4): высоты новых узлов концов (после подгонки валидатора), флаг моста, штамп коридора и площадок, синхронизация производных.
    private static void ApplyRoadTerrain(World w, in ToolCheck chk, int seg)
    {
        ref SegmentData sd = ref w.Net.Segments[seg];
        if (chk.SnapKindA == SnapKind.NewNode)
        {
            w.Net.Nodes[sd.NodeA].Elevation = RoadTerrain.Meters(chk.ElevACm);
        }

        if (chk.SnapKindB == SnapKind.NewNode)
        {
            w.Net.Nodes[sd.NodeB].Elevation = RoadTerrain.Meters(chk.ElevBCm);
        }

        if (chk.Bridge)
        {
            sd.Flags |= SegmentFlags.Bridge;
        }

        RoadTerrain.OnRoadBuilt(w, seg);
    }

    // AutoSplit (T5): концы на оси сегментов - сначала разрезы (узлы разреза - концы новой дороги; индексы второго сегмента первый разрез не трогает).
    private static bool SplitEnds(World w, in Command cmd, in ToolCheck chk, ref Vec2 a, ref Vec2 z)
    {
        for (int e = 0; e < 2; e++)
        {
            int seg = e == 0 ? chk.SplitSegA : chk.SplitSegB;
            if (seg < 0)
            {
                continue;
            }

            NetworkStatus st = ChainEdit.Split(w, seg, e == 0 ? chk.SplitSA : chk.SplitSB, cmd.Tag, out int node, out _, out _);
            if (st != NetworkStatus.Ok)
            {
                RejectNetwork(w, in cmd, st);
                return false;
            }

            if (e == 0)
            {
                a = w.Net.Nodes[node].Position;
            }
            else
            {
                z = w.Net.Nodes[node].Position;
            }
        }

        return true;
    }

    // Пересоздание по RestoreOf (player-tools.md §8.2): событие и запись scratch-таблицы old -> new для сегмента и пересозданных узлов концов.
    private static void EmitRestoreRemaps(World w, in Command cmd, int seg)
    {
        BuildRoadCmd b = cmd.BuildRoad;
        if (b.RestoreOf.Generation == 0)
        {
            return;
        }

        ref readonly SegmentData sd = ref w.Net.Segments[seg];
        w.Inv.AddRemap(EntityKind.Segment, in b.RestoreOf, new EntityRef(seg, sd.Generation));
        w.Emit(SimEventKind.SegmentRemapped, cmd.Tag, b.RestoreOf.Index, seg);
        for (int e = 0; e < 2; e++)
        {
            EntityRef old = e == 0 ? b.RestoreNodeA : b.RestoreNodeB;
            int now = e == 0 ? sd.NodeA : sd.NodeB;
            if (old.Generation != 0 && !(old.Index == now && old.Generation == w.Net.Nodes[now].Generation))
            {
                w.Inv.AddRemap(EntityKind.Node, in old, new EntityRef(now, w.Net.Nodes[now].Generation));
                w.Emit(SimEventKind.NodeRemapped, cmd.Tag, old.Index, now);
            }
        }
    }

    // Концы нового сегмента: непостроенный узел строится как раньше, построенный - перестраивается (врезка рукава, 4.3 T3).
    private static NetworkStatus BuildOrRebuildEnd(World w, in Command cmd, int node, ref bool rebuilt)
    {
        if (w.Net.Nodes[node].FirstArm < 0)
        {
            return w.Builder.BuildConnectorsEx(node);
        }

        rebuilt = true;
        return NodeEdit.Rebuild(w, node, rejectTrap: false, cmd.Tag);
    }

    private static NetworkStatus BuildConflictsIfFresh(World w, int node)
    {
        ref readonly NodeData nd = ref w.Net.Nodes[node];
        if (nd.ConnectorCount > 0 && w.Net.Lanes[nd.FirstConnector].ConflictGroup >= 0)
        {
            return NetworkStatus.Ok;
        }

        return w.Connectors.BuildConflicts(w.Net, node);
    }

    // Перестройка построенных концов должна получиться до любых изменений: иначе новый сегмент (с созданными узлами) снимается и команда отклоняется (Busy и т.п.).
    private static bool CheckBuiltEnds(World w, in Command cmd, int seg)
    {
        ref readonly SegmentData sd = ref w.Net.Segments[seg];
        int lo = Math.Min(sd.NodeA, sd.NodeB);
        int hi = Math.Max(sd.NodeA, sd.NodeB);
        NetworkStatus bad = NetworkStatus.Ok;
        if (w.Net.Nodes[lo].FirstArm >= 0)
        {
            bad = NodeEdit.Check(w, lo, rejectTrap: false);
        }

        if (bad == NetworkStatus.Ok && w.Net.Nodes[hi].FirstArm >= 0)
        {
            bad = NodeEdit.Check(w, hi, rejectTrap: false);
        }

        if (bad == NetworkStatus.Ok)
        {
            return true;
        }

        NetworkEdit.ReleaseSegment(w, seg);
        for (int k = w.Builder.CreatedNodeCount - 1; k >= 0; k--)
        {
            NetworkEdit.ReleaseNode(w, w.Builder.CreatedNodes[k]);
        }

        w.Net.Version++;
        NetworkEdit.RefreshDerived(w, pedsChanged: false);   // слот сегмента мог поднять HighWater: граф и оверлей - по новому числу слотов (T5: ловил фаззер)
        Rejected(w, in cmd, RejectOf(bad));
        return false;
    }

    private static RejectReason RejectOf(NetworkStatus st) => ToolValidator.RejectOf(st);

    // Сеть выросла (routing.md 9.3 "BuildRoad"): граф пересобирается сразу, оверлей дописывает новые узлы/входы/выходы,
    // помечает ячейки грязными или (не хватило запаса) перестраивается целиком.
    private static void GrowOverlay(World w, bool rebuilt = false)
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

        if (rebuilt || w.Net.ToolsTouched != 0)
        {
            ov.RebuildAfterEdit();   // правка раскладки: слоты занимаются заново, дуги узлов меняются - инкремент оверлея (дописывание в конец) неприменим
        }
        else
        {
            ov.OnNetworkGrown();
        }

        w.Counters.OverlayRebuilds = ov.Rebuilds;
        w.Counters.OverlayDisabled = ov.Disabled ? 1 : 0;
    }

    /// <summary>Снос здания игроком (building-lifecycle.md §2.5): проверки и цена - общий валидатор, затем заброшенность + <c>Demolishing</c>; удаление - ближайший суточный шаг.</summary>
    private static void DemolishBuilding(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why);
            return;
        }

        City.CityLife.Demolish(w, w.City!, cmd.DemolishBuilding.Building.Index);
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// intersections.md 6.3, 6.5: узел должен быть <c>Signal</c>; для <c>Fixed</c>/<c>Adaptive</c> PhaseCount = числу осей узла, Green_k >= 1,
    /// цикл <= 65535 (Adaptive: ещё MinGreen/Gap/стрелка и инвариант красного); иначе <c>InvalidArgument</c> (отдельных причин NotSignalized/InvalidPhaseCount в RejectReason нет).
    /// Применяется в фазе команд тика, начальное состояние пересчитывается по OffsetTicks.
    /// </summary>
    private static void SetSignalPlan(World w, in Command cmd)
    {
        SetSignalPlanCmd p = cmd.SetSignalPlan;
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why);
            return;
        }

        ref readonly NodeData nd = ref w.Net.Nodes[p.Node];
        int axes = w.Signals.PhaseCount[nd.Signal];
        SetSignalPlanCmd resolved = SignalStore.Resolve(in p, axes, in w.Rules);

        w.Signals.ApplyPlan(nd.Signal, in resolved, w.Tick);
        w.Vehicles.MarkAllRouteDirty();
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// intersections.md 6.3. Signal: слот в SignalStore + план по умолчанию (нет слота - Capacity; узел без connector'ов - InvalidArgument).
    /// Из Signal в другой режим: swap-remove слота. TODO(J3): пересчёт ArmFlags/Rel и сброс допусков узла (5.7).
    /// </summary>
    private static void SetNodeControl(World w, in Command cmd)
    {
        SetNodeControlCmd c = cmd.SetNodeControl;
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why);
            return;
        }

        ref NodeData nd = ref w.Net.Nodes[c.Node];
        if (c.Control == NodeControl.Signal)
        {
            if (nd.Signal < 0)
            {
                int axes = SignalStore.AxesOf(w, c.Node);
                SetSignalPlanCmd plan = SignalStore.DefaultPlan(w, c.Node, axes);   // all-red с надбавкой на очистку коробки (ADR-0028)
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

        AppliedPaid(w, in cmd, in chk);
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
            fromLane = l.Kind == LaneKind.Normal && float.IsFinite(t.FromS) && t.FromS >= 0f && t.FromS <= l.Length && (l.Flags & LaneFlags.Closed) == 0;
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

    /// <summary>
    /// <c>SetOnCall</c> (VC3, vehicle-classes.md §2.4): флаг <see cref="VehicleFlags.OnCall"/> у машины типа класса <c>Special</c>; иное - отказ (приоритет только у спецмашин, ЖКХ получает его лишь
    /// флагом вызова от диспетчера). Права (<c>Access</c>) меняются сразу, спланированный маршрут не пересчитывается.
    /// </summary>
    private static void SetOnCall(World w, in Command cmd)
    {
        var h = new VehicleHandle(cmd.SetOnCall.Index, cmd.SetOnCall.Generation);
        if (!w.Vehicles.IsAlive(h))
        {
            w.Counters.InvalidHandleOps++;
            Rejected(w, in cmd, RejectReason.InvalidHandle);
            return;
        }

        int i = h.Index;
        if (w.Types[w.Vehicles.Type[i]].Category != VehicleCategory.Special)
        {
            Rejected(w, in cmd, RejectReason.InvalidArgument);
            return;
        }

        if (cmd.SetOnCall.OnCall != 0)
        {
            w.Vehicles.Flags[i] |= (byte)VehicleFlags.OnCall;
        }
        else
        {
            w.Vehicles.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.OnCall);
        }

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
