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
}
