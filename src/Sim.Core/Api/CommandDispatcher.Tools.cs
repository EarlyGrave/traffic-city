using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tools;

namespace Sim.Core.Api;

// Инструменты игрока (4.3, player-tools.md §1): знаки рукавов, запреты поворотов, флаги полос (T3). Каждая команда - ровно одно Applied/Rejected;
// проверка handle (CheckEntity) и всех условий - до первой мутации.
internal static partial class CommandDispatcher
{
    /// <summary>
    /// <c>SetArmControl</c> (на месте): знак рукава <c>ArmSlot</c> узла - 0 авто, 1 главный, 2 уступи, 3 стоп. Пересчитывает <c>ArmFlags</c>, <c>Control</c> узла (второстепенный рукав
    /// включает «уступи» на узле без правил), флаги и <c>Rel</c> конфликтов, сбрасывает допуски узла (intersections.md §5.7). Индексы не меняются.
    /// </summary>
    private static void SetArmControl(World w, in Command cmd)
    {
        SetArmControlCmd c = cmd.SetArmControl;
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why);
            return;
        }

        NetworkStore n = w.Net;
        int node = c.Node.Index;
        ref NodeData nd = ref n.Nodes[node];
        if (w.Builder.ArmOverrideOf(node, c.ArmSlot) == c.Control)
        {
            AppliedPaid(w, in cmd, in chk);
            return;
        }

        n.ToolsTouched = 1;
        w.Builder.SetArmOverride(node, c.ArmSlot, c.Control);
        w.Connectors.RefreshFlags(n, node);
        w.Connectors.RecomputeRel(n, node, nd.Control == NodeControl.Yield || nd.Control == NodeControl.Stop);
        GatePhase.ResetNode(w, node);
        w.Gate.FullScan = true;
        w.Vehicles.MarkAllRouteDirty();
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// <c>SetTurnBan</c>: запрет (или разрешение) манёвра рукав -> рукав (слоты рукавов по часовой, 0..7). Перестройка узла (player-tools.md §5); запрет, оставляющий входную полосу
    /// без выезда, - <c>CreatesTrap</c>; занятый машинами исчезающий connector - <c>Busy</c> (до T4 нет дренажа узла: повторить позже).
    /// </summary>
    private static void SetTurnBan(World w, in Command cmd)
    {
        SetTurnBanCmd c = cmd.SetTurnBan;
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why);
            return;
        }

        NetworkStore n = w.Net;
        int node = c.Node.Index;
        ref NodeData nd = ref n.Nodes[node];
        ulong bit = 1UL << ((c.FromArm * 8) + c.ToArm);
        ulong old = nd.TurnBan;
        ulong want = c.Banned == 1 ? old | bit : old & ~bit;
        if (want == old)
        {
            AppliedPaid(w, in cmd, in chk);
            return;
        }

        n.ToolsTouched = 1;
        nd.TurnBan = want;
        NetworkStatus st = w.Builder.PlanRebuild(w, node, rejectTrap: c.Banned == 1, out RebuildPlan rp);
        if (st == NetworkStatus.Ok && rp.Plan == rp.OConnN && rp.Survived == rp.OConnN && rp.Arms == rp.OArms)
        {
            AppliedPaid(w, in cmd, in chk);   // манёвр и так не порождал connector'ов (нет полос): запрет только запомнен
            return;
        }

        if (st == NetworkStatus.Ok)
        {
            st = NodeEdit.Rebuild(w, node, rejectTrap: c.Banned == 1, cmd.Tag);
        }

        if (st != NetworkStatus.Ok)
        {
            n.Nodes[node].TurnBan = old;
            Rejected(w, in cmd, RejectOf(st));
            return;
        }

        NetworkEdit.RefreshDerived(w);
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// <c>RemoveRoad</c> (дренаж, player-tools.md §4.3): сегмент закрывается для въезда (<c>SegmentClosure.Close</c>), заводится операция; удаление - в фазе 1а, когда он опустеет.
    /// Цель уже закрывается - <c>Busy</c> (отмена - <c>BuildRoad(RestoreOf)</c>); остановка автобуса на сегменте - <c>NotSupported</c> до T9; Q-meso - <c>NotSupported</c>.
    /// </summary>
    private static void RemoveRoad(World w, in Command cmd)
    {
        RemoveRoadCmd c = cmd.RemoveRoad;
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why);
            return;
        }

        NetworkStore n = w.Net;
        int seg = c.Seg.Index;
        n.ToolsTouched = 1;
        w.Ops.TryAdd(ToolOpKind.RemoveSegment, seg, n.Segments[seg].Generation, (byte)(c.Flags & 1), w.Tick, ToolOpsPhase.CountOn(w, seg), out _);
        SegmentClosure.Close(w, seg, cmd.Tag);
        NetworkEdit.RefreshDerived(w, pedsChanged: false);
        w.Work.ToolOpsStarted++;
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>Отмена сноса: сегмент открывается, операция снимается (граф и оверлей пересчитываются: вес снова конечный).</summary>
    private static void ReopenRoad(World w, in Command cmd, in ToolCheck chk, int seg)
    {
        SegmentClosure.Reopen(w, seg);
        w.Ops.RemoveAt(w.Ops.FindSegment(seg));
        NetworkEdit.RefreshDerived(w, pedsChanged: false);
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// <c>SetLaneFlags</c> (на месте): биты <c>BusOnly</c>, <c>NoLaneChangeOut</c> полосы <c>LaneNo</c> направления <c>Dir</c> сегмента по маске <c>Mask</c>
    /// (<c>Closed/Draining</c> ставит только ядро при закрытии). Читает MOBIL (<c>Mobil.Evaluate</c>).
    /// </summary>
    private static void SetLaneFlags(World w, in Command cmd)
    {
        SetLaneFlagsCmd c = cmd.SetLaneFlags;
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why);
            return;
        }

        NetworkStore n = w.Net;
        ref readonly SegmentData sd = ref n.Segments[c.Seg.Index];
        int lane = (c.Dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA) + c.LaneNo;
        byte old = n.Lanes[lane].Flags;
        var want = (byte)((old & ~c.Mask) | (c.Flags & c.Mask));
        if (want != old)
        {
            n.ToolsTouched = 1;
            n.Lanes[lane].Flags = want;
            n.Version++;
            w.Work.ToolCommandsApplied++;
        }

        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// <c>UpgradeRoad</c> (RP7, T5; player-tools.md §4.5): скорость, тип, полосы и профиль сегмента. Ось не меняется; сегмент и его handle сохраняются. Новый блок полос (другое число полос) -
    /// перенос ссылок <c>Relocate</c> (машины уцелевших полос переезжают с полосой) и перестройка узлов концов; те же полосы - правка геометрии на месте. Занятый сегмент, у которого убираемые
    /// полосы или исчезающие connector'ы концов заняты, получает операцию <c>ShrinkLanes</c> (дренаж, финал - фаза 1а); скорость меняется сразу. Команда во время дренажа, возвращающая
    /// прежние полосы, отменяет операцию (undo). После правки участки вдоль дороги пересчитываются (<c>LotBuilder.OnRoadsChanged</c>).
    /// </summary>
    private static void UpgradeRoad(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        int seg = chk.Seg;
        NetworkStore n = w.Net;
        if (chk.CancelShrink)
        {
            int k = w.Ops.FindShrink(seg);
            ToolOps.UnpackReshape(w.Ops.TargetGen[k], out int tab, out int tba, out _, out _, out _);
            w.Builder.CancelShrink(w, seg, tab, tba);
            w.Ops.RemoveAt(k);
            w.Builder.ReshapeSegment(w, seg, chk.Reshape, chk.Profile, chk.Mirrored, chk.LanesAB, chk.LanesBA, chk.Speed, chk.Kind, cmd.Tag);   // None или Speed
            NetworkEdit.RefreshDerived(w, pedsChanged: false);
            w.Work.ToolCommandsApplied++;
            AppliedPaid(w, in cmd, in chk);
            return;
        }

        if (chk.Reshape == ReshapeMode.None)
        {
            AppliedPaid(w, in cmd, in chk);
            return;
        }

        if (chk.Drain > 0)
        {
            // Дренаж: скорость - сразу, полосы и тип - в финале операции.
            n.ToolsTouched = 1;
            if (n.Segments[seg].SpeedLimit != chk.Speed)
            {
                w.Builder.ReshapeSegment(w, seg, ReshapeMode.Speed, chk.Profile, chk.Mirrored, chk.LanesAB, chk.LanesBA, chk.Speed, chk.Kind, cmd.Tag);
            }

            int packed = ToolOps.PackReshape(chk.LanesAB, chk.LanesBA, chk.Kind, chk.Profile, chk.Mirrored);
            w.Ops.TryAdd(ToolOpKind.ShrinkLanes, seg, packed, 0, w.Tick, chk.Drain, out _);
            w.Builder.BeginShrink(w, seg, chk.LanesAB, chk.LanesBA);
            NetworkEdit.RefreshDerived(w, pedsChanged: false);
            w.Work.ToolOpsStarted++;
            w.Work.ToolCommandsApplied++;
            AppliedPaid(w, in cmd, in chk);
            return;
        }

        NetworkStatus st = ReshapeEdit.Apply(w, seg, chk.Reshape, chk.Profile, chk.Mirrored, chk.LanesAB, chk.LanesBA, chk.Speed, chk.Kind, cmd.Tag);
        if (st != NetworkStatus.Ok)
        {
            Rejected(w, in cmd, RejectOf(st));   // ёмкость при перестройке узла: пробное планирование валидатора её исключает
            return;
        }

        n.ToolsTouched = 1;
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// <c>SplitRoad</c> (T5, player-tools.md §4.4): узел в точке полной оси (м от узла A), сегмент заменяется двумя половинами; машины переносятся по координате (в «коробке» узла - на его
    /// connector'ы), connector'ы концов сохраняются. Перенумерация (<c>SegmentRemapped</c>/<c>NodeRemapped</c>) - только для <c>RestoreSeg1/2</c> и <c>RestoreNode</c> (undo слияния).
    /// </summary>
    private static void SplitRoad(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        SplitRoadCmd c = cmd.SplitRoad;
        NetworkStatus st = ChainEdit.Split(w, chk.Seg, c.S, cmd.Tag, out int node, out int seg1, out int seg2);
        if (st != NetworkStatus.Ok)
        {
            Rejected(w, in cmd, RejectOf(st));
            return;
        }

        // Перенумерация (player-tools.md §8.2) - только для восстанавливаемого (undo слияния): ссылки хоста на прежние половины и узел переводятся на новые.
        NetworkStore n = w.Net;
        RestoreRemap(w, in cmd, EntityKind.Node, in c.RestoreNode, new EntityRef(node, n.Nodes[node].Generation));
        RestoreRemap(w, in cmd, EntityKind.Segment, in c.RestoreSeg1, new EntityRef(seg1, n.Segments[seg1].Generation));
        RestoreRemap(w, in cmd, EntityKind.Segment, in c.RestoreSeg2, new EntityRef(seg2, n.Segments[seg2].Generation));

        w.Inv.BuiltNode = node;
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// <c>MergeAtNode</c> (T5, player-tools.md §4.4): два сегмента узла степени 2 сливаются в один, узел освобождается; машины на сегментах и connector'ах узла переносятся.
    /// Перенумерация - только для <c>RestoreSeg</c> (undo разреза): ссылки на половины остаются ссылками на мёртвые сегменты, их вернёт обратная <c>SplitRoad</c> (<c>RestoreSeg1/2</c>).
    /// </summary>
    private static void MergeAtNode(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        NetworkStore n = w.Net;
        ChainEdit.TwoSegments(n, chk.Node, out int s1, out int s2);
        w.Inv.MergedA = new EntityRef(s1, n.Segments[s1].Generation);
        w.Inv.MergedB = new EntityRef(s2, n.Segments[s2].Generation);
        NetworkStatus st = ChainEdit.Merge(w, chk.Node, cmd.Tag, out int merged, out float splitS);
        if (st != NetworkStatus.Ok)
        {
            Rejected(w, in cmd, RejectOf(st));
            return;
        }

        MergeAtNodeCmd c = cmd.MergeAtNode;
        RestoreRemap(w, in cmd, EntityKind.Segment, in c.RestoreSeg, new EntityRef(merged, n.Segments[merged].Generation));
        w.Inv.BuiltSeg = merged;
        w.Inv.BuiltS = splitS;
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    // ---------------------------------------------------------------- автобусные линии (T9, player-tools.md §8.7)

    /// <summary><c>CreateBusRoute</c>: новая линия без остановок (выпуск начнётся, когда появится первая остановка); <c>RestoreOf</c> - пересоздание снятой линии (undo).</summary>
    private static void CreateBusRoute(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        CreateBusRouteCmd b = cmd.CreateBusRoute;
        int r = Transit.BusEdit.CreateRoute(w, b.HeadwayTicks, b.VehicleType, (b.Flags & BusRouteFlags.Suspend) != 0, cmd.Tag);
        if (r < 0)
        {
            RejectTool(w, in cmd, RejectReason.Capacity, CapacityKind.BusRoutes);
            return;
        }

        BusRemap(w, in cmd, EntityKind.BusRoute, in b.RestoreOf, new EntityRef(r, w.Net.BusRoutes[r].Generation));
        w.Inv.BuiltRoute = r;
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary>
    /// <c>InsertBusStop</c> / <c>AppendBusStop</c>: новая остановка на позиции линии (полоса и S по ходу полосы); проверка <c>Unreachable</c> - от соседних остановок по текущей сети.
    /// <c>RestoreOf</c> мёртв - пересоздание (<c>BusStopRemapped</c>), жив и уже на линии - ничего (повтор обратной записи).
    /// </summary>
    private static void InsertBusStop(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        if (chk.Noop)
        {
            w.Inv.BuiltStop = chk.Stop;
            Applied(w, in cmd);
            return;
        }

        BusStopKind kind = cmd.Kind == CommandKind.AppendBusStop ? cmd.AppendBusStop.Kind : (BusStopKind)cmd.InsertBusStop.Kind;
        int si = Transit.BusEdit.InsertStop(w, chk.Route, chk.Pos, chk.Lane, chk.StopS, kind, cmd.Tag);
        if (si < 0)
        {
            RejectTool(w, in cmd, RejectReason.Capacity, CapacityKind.BusStops);
            return;
        }

        if (cmd.Kind == CommandKind.InsertBusStop)
        {
            BusRemap(w, in cmd, EntityKind.BusStop, in cmd.InsertBusStop.RestoreOf, new EntityRef(si, w.Net.BusStops[si].Generation));
        }

        BusStopsChanged(w);
        w.Inv.BuiltStop = si;
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary><c>RemoveBusStop</c>: остановка уходит с линии, слот освобождается; автобусы, ехавшие к ней, - к следующей достижимой.</summary>
    private static void RemoveBusStop(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        Transit.BusEdit.RemoveStop(w, chk.Stop, cmd.Tag);
        BusStopsChanged(w);
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary><c>RemoveBusRoute</c>: линия и её остановки освобождаются; автобусы доезжают до своей цели и завершают рейс.</summary>
    private static void RemoveBusRoute(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        bool hadStops = w.Net.BusRoutes[chk.Route].StopCount > 0;
        Transit.BusEdit.RemoveRoute(w, chk.Route, cmd.Tag);
        if (hadStops)
        {
            BusStopsChanged(w);
        }

        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    /// <summary><c>SetBusRouteParams</c> (на месте): интервал, тип ТС, приостановка/возобновление.</summary>
    private static void SetBusRouteParams(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        RejectReason why = ToolValidator.Check(w, in cmd, ref chk);
        if (why != RejectReason.None)
        {
            RejectTool(w, in cmd, why, chk.CapKind);
            return;
        }

        SetBusRouteParamsCmd b = cmd.SetBusRouteParams;
        Transit.BusEdit.SetParams(w, chk.Route, b.HeadwayTicks, b.VehicleType, b.Flags);
        w.Work.ToolCommandsApplied++;
        AppliedPaid(w, in cmd, in chk);
    }

    // Сеть пешеходов - производное от остановок (переходы на перегоне у остановок, pedestrians.md §2): после правки остановок - пересборка со сбросом агентов, как после правки дорог.
    private static void BusStopsChanged(World w)
    {
        if (w.Peds != null && w.Net.PedNet != null && w.Net.PedNet.MidCap > 0)
        {
            w.Peds.OnNetworkGrown(w);
        }
    }

    private static void BusRemap(World w, in Command cmd, EntityKind kind, in EntityRef old, in EntityRef now)
    {
        if (old.Generation == 0)
        {
            return;
        }

        w.Inv.AddRemap(kind, in old, in now);
        w.Emit(kind == EntityKind.BusStop ? SimEventKind.BusStopRemapped : SimEventKind.BusRouteRemapped, cmd.Tag, old.Index, now.Index);
    }

    // Пересоздание по Restore*: событие *Remapped и запись scratch-таблицы old -> new (player-tools.md §8.2).
    private static void RestoreRemap(World w, in Command cmd, EntityKind kind, in EntityRef old, in EntityRef now)
    {
        if (old.Generation == 0)
        {
            return;
        }

        w.Inv.AddRemap(kind, in old, in now);
        w.Emit(kind == EntityKind.Node ? SimEventKind.NodeRemapped : SimEventKind.SegmentRemapped, cmd.Tag, old.Index, now.Index);
    }
}
