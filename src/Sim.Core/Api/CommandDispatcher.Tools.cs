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
            Applied(w, in cmd);
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
        Applied(w, in cmd);
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
            Applied(w, in cmd);
            return;
        }

        n.ToolsTouched = 1;
        nd.TurnBan = want;
        NetworkStatus st = w.Builder.PlanRebuild(w, node, rejectTrap: c.Banned == 1, out RebuildPlan rp);
        if (st == NetworkStatus.Ok && rp.Plan == rp.OConnN && rp.Survived == rp.OConnN && rp.Arms == rp.OArms)
        {
            Applied(w, in cmd);   // манёвр и так не порождал connector'ов (нет полос): запрет только запомнен
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
        Applied(w, in cmd);
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
        Applied(w, in cmd);
    }

    /// <summary>Отмена сноса: сегмент открывается, операция снимается (граф и оверлей пересчитываются: вес снова конечный).</summary>
    private static void ReopenRoad(World w, in Command cmd, int seg)
    {
        SegmentClosure.Reopen(w, seg);
        w.Ops.RemoveAt(w.Ops.FindSegment(seg));
        NetworkEdit.RefreshDerived(w, pedsChanged: false);
        w.Work.ToolCommandsApplied++;
        Applied(w, in cmd);
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

        Applied(w, in cmd);
    }
}
