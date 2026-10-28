using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;

namespace Sim.Core.Tools;

/// <summary>Состояние построения обратной записи одной команды: команды лежат в хвосте буфера <c>[Start, Start+N)</c>, пока запись не зафиксирована.</summary>
internal struct InvCtx
{
    public int Start;
    public int N;
    public bool Truncated;
    public bool NoInverse;
    public bool Inactive;
    public int NodeA, NodeB, GenA, GenB;   // BuildRoad: существующие построенные узлы концов (до мутации) и их режим
    public NodeControl CtlA, CtlB;
}

/// <summary>
/// Обратные записи ядра (player-tools.md §8.1): строятся из фактического состояния до мутации (<see cref="Begin"/>), для <c>BuildRoad</c> дополняются после неё (<see cref="End"/>,
/// индекс нового сегмента известен только тогда). Запись фиксируется, только если команда применена. Автобусные команды (T9) обратных пока не имеют.
/// </summary>
internal static class InverseBuilder
{
    public static bool IsTool(CommandKind k) => k switch
    {
        CommandKind.BuildRoad or CommandKind.RemoveRoad or CommandKind.SetArmControl or CommandKind.SetTurnBan or CommandKind.SetLaneFlags
            or CommandKind.SetNodeControl or CommandKind.SetSignalPlan or CommandKind.UpgradeRoad or CommandKind.SplitRoad or CommandKind.MergeAtNode
            or CommandKind.CreateBusRoute or CommandKind.AppendBusStop or CommandKind.InsertBusStop or CommandKind.RemoveBusStop
            or CommandKind.RemoveBusRoute or CommandKind.SetBusRouteParams or CommandKind.PaintZone => true,
        _ => false,
    };

    public static InvCtx Begin(World w, in Command cmd)
    {
        InverseLog log = w.Inv;
        var ctx = new InvCtx { Start = log.CmdCount, NodeA = -1, NodeB = -1 };
        log.BuiltSeg = -1;
        if (!IsTool(cmd.Kind))
        {
            ctx.Inactive = true;
            return ctx;
        }

        switch (cmd.Kind)
        {
            case CommandKind.BuildRoad:
                BeginBuildRoad(w, in cmd, ref ctx);
                break;
            case CommandKind.RemoveRoad:
                BeginRemoveRoad(w, in cmd, ref ctx);
                break;
            case CommandKind.SetArmControl:
                BeginArmControl(w, in cmd, ref ctx);
                break;
            case CommandKind.SetTurnBan:
                BeginTurnBan(w, in cmd, ref ctx);
                break;
            case CommandKind.SetLaneFlags:
                BeginLaneFlags(w, in cmd, ref ctx);
                break;
            case CommandKind.SetNodeControl:
                BeginNodeControl(w, in cmd, ref ctx);
                break;
            case CommandKind.SetSignalPlan:
                BeginSignalPlan(w, in cmd, ref ctx);
                break;
            case CommandKind.UpgradeRoad:
                BeginUpgradeRoad(w, in cmd, ref ctx);
                break;
            case CommandKind.PaintZone:
                BeginPaintZone(w, in cmd, ref ctx);
                break;
            default:
                ctx.NoInverse = true;   // SplitRoad/MergeAtNode (T5) и автобусные (T9): обратных пока нет
                break;
        }

        return ctx;
    }

    /// <summary>Фиксирует запись, если команда применена; иначе отбрасывает построенное.</summary>
    public static void End(World w, in Command cmd, ref InvCtx ctx, bool applied)
    {
        if (ctx.Inactive || !applied)
        {
            return;
        }

        InverseLog log = w.Inv;
        if (cmd.Kind == CommandKind.BuildRoad && log.BuiltSeg >= 0 && !ctx.NoInverse)
        {
            // Новый сегмент: обратная - снос (осиротевшие созданные узлы уйдут вместе с ним). Переоткрытие обратную записало в Begin.
            ref readonly SegmentData sd = ref w.Net.Segments[log.BuiltSeg];
            Add(log, ref ctx, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(log.BuiltSeg, sd.Generation) }));
            if (ctx.NodeA >= 0)
            {
                Add(log, ref ctx, Command.MakeSetNodeControl(new SetNodeControlCmd { Node = ctx.NodeA, Control = ctx.CtlA, NodeGen = ctx.GenA }));
            }

            if (ctx.NodeB >= 0)
            {
                Add(log, ref ctx, Command.MakeSetNodeControl(new SetNodeControlCmd { Node = ctx.NodeB, Control = ctx.CtlB, NodeGen = ctx.GenB }));
            }
        }

        if (log.RecCount >= log.Recs.Length)
        {
            return;
        }

        var rec = new InverseRecord { Tag = cmd.Tag, First = ctx.Start, Count = ctx.N };
        if (ctx.NoInverse)
        {
            rec.Flags = InverseFlags.NoInverse;
            rec.Count = 0;
        }
        else if (ctx.Truncated)
        {
            rec.Flags = InverseFlags.Truncated;
            rec.Count = 0;
        }

        if (rec.Count > 0)
        {
            log.CmdCount = ctx.Start + ctx.N;
        }

        log.Recs[log.RecCount++] = rec;
    }

    private static void Add(InverseLog log, ref InvCtx ctx, in Command c)
    {
        if (ctx.Truncated)
        {
            return;
        }

        int at = ctx.Start + ctx.N;
        if (ctx.N >= InverseLog.MaxPerRecord || at >= log.Cmds.Length)
        {
            ctx.Truncated = true;
            return;
        }

        log.Cmds[at] = c;
        log.Cmds[at].Flags |= CommandFlags.Inverse;   // city-economy.md §8.3: цену обратной определяет запись undo-кредита
        ctx.N++;
    }

    /// <summary>Есть ли у команды обратная запись вообще (иначе <see cref="InverseFlags.NoInverse"/>): зеркало ветвления <c>Begin</c>.</summary>
    public static bool HasInverse(CommandKind k) => k is CommandKind.BuildRoad or CommandKind.RemoveRoad or CommandKind.SetArmControl or CommandKind.SetTurnBan
        or CommandKind.SetLaneFlags or CommandKind.SetNodeControl or CommandKind.SetSignalPlan or CommandKind.UpgradeRoad or CommandKind.PaintZone;

    /// <summary>
    /// <c>UpgradeRoad</c>, у которого обратной записи не будет: сегмент без профиля получает профиль (вернуть «без профиля» командой нельзя). Зеркало <c>BeginUpgradeRoad</c> для превью.
    /// </summary>
    public static bool UpgradeLacksInverse(World w, in Command cmd)
    {
        var chk = default(ToolCheck);
        return ToolValidator.Check(w, in cmd, ref chk) == RejectReason.None && chk.Reshape != ReshapeMode.None && chk.Profile >= 0 && w.Net.Segments[chk.Seg].Profile < 0;
    }

    /// <summary>
    /// <c>PaintZone</c> (UI-C1/E4): прежние вид, плотность и <c>Painted</c> участков под прямоугольником (<see cref="Zoning.BuildInverse"/>). Не влезла в запись (&gt; 64 команд) - <c>Truncated</c>.
    /// Отклоняемая команда (валидатор) записи не даёт. Заброшенные зданием участки зону вернут, здание - нет (undo логический).
    /// </summary>
    private static void BeginPaintZone(World w, in Command cmd, ref InvCtx ctx)
    {
        if (Zoning.Check(w, in cmd.PaintZone, out _) != RejectReason.None)
        {
            ctx.Inactive = true;
            return;
        }

        InverseLog log = w.Inv;
        int room = Math.Min(InverseLog.MaxPerRecord, log.Cmds.Length - ctx.Start);
        Span<Command> dst = room > 0 ? log.Cmds.AsSpan(ctx.Start, room) : default;
        int n = Zoning.BuildInverse(w, in cmd.PaintZone, dst);
        if (n > room)
        {
            ctx.Truncated = true;
            return;
        }

        for (int i = 0; i < n; i++)
        {
            dst[i].Flags |= CommandFlags.Inverse;
        }

        ctx.N = n;
    }

    private static void BeginUpgradeRoad(World w, in Command cmd, ref InvCtx ctx)
    {
        var chk = default(ToolCheck);
        if (ToolValidator.Check(w, in cmd, ref chk) != RejectReason.None)
        {
            return;   // команда будет отклонена
        }

        ref readonly SegmentData sd = ref w.Net.Segments[chk.Seg];
        if (chk.Reshape == ReshapeMode.None)
        {
            return;
        }

        if (chk.Profile >= 0 && sd.Profile < 0)
        {
            ctx.NoInverse = true;
            return;
        }

        // Прежние значения: профиль (с зеркальностью) или явные полосы и тип. Флаги ReshapeMode.Swap на занятость не влияют: возврат проходит тот же валидатор.
        var u = new UpgradeRoadCmd
        {
            Seg = new EntityRef(chk.Seg, sd.Generation),
            SpeedLimit = sd.SpeedLimit,
            LanesAB = sd.LanesAB,
            LanesBA = sd.LanesBA,
            Kind = sd.Kind,
            Profile = (ushort)(sd.Profile + 1),
            Flags = (byte)(sd.Profile >= 0 && (sd.Flags & SegmentFlags.Mirrored) != 0 ? UpgradeRoadFlags.Mirrored : 0),
        };
        Add(w.Inv, ref ctx, Command.MakeUpgradeRoad(in u));
    }

    /// <summary>
    /// Верхняя граница числа обратных команд сноса сегмента (для превью, <see cref="IssueCode.UndoUnavailable"/>): сам сегмент, полосы с флагами,
    /// на каждый конец - режим, план (если есть сигнал), переопределение рукава и все запреты манёвров узла. Консервативна: не читает планы и не знает, какие запреты именно рукава.
    /// </summary>
    public static int MaxRemoveRoadInverse(World w, int seg)
    {
        NetworkStore n = w.Net;
        ref readonly SegmentData sd = ref n.Segments[seg];
        int total = 1 + sd.LanesAB + sd.LanesBA;
        for (int e = 0; e < 2; e++)
        {
            ref readonly NodeData nd = ref n.Nodes[e == 0 ? sd.NodeA : sd.NodeB];
            if (nd.FirstArm < 0)
            {
                continue;
            }

            total += 1 + (nd.Signal >= 0 ? 1 : 0) + 1 + System.Numerics.BitOperations.PopCount(nd.TurnBan);
        }

        return total;
    }

    private static void BeginBuildRoad(World w, in Command cmd, ref InvCtx ctx)
    {
        BuildRoadCmd b = cmd.BuildRoad;
        NetworkStore n = w.Net;
        if (b.RestoreOf.Generation != 0 && n.ResolveSegment(in b.RestoreOf))
        {
            int k = w.Ops.FindSegment(b.RestoreOf.Index);
            if (k >= 0)
            {
                // Переоткрытие: обратная - снова снос с теми же флагами.
                Add(w.Inv, ref ctx, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = b.RestoreOf, Flags = w.Ops.Flags[k] & 1 }));
            }

            return;
        }

        if (float.IsFinite(b.Ax) && float.IsFinite(b.Az) && float.IsFinite(b.Bx) && float.IsFinite(b.Bz))
        {
            // Врезка рукава в построенный узел перестраивает его и может сменить режим: обратная возвращает прежний.
            int na = w.Builder.FindNode(new Util.Vec2(b.Ax, b.Az));
            int nb = w.Builder.FindNode(new Util.Vec2(b.Bx, b.Bz));
            if (na >= 0 && n.Nodes[na].FirstArm >= 0)
            {
                ctx.NodeA = na;
                ctx.GenA = n.Nodes[na].Generation;
                ctx.CtlA = n.Nodes[na].Control;
            }

            if (nb >= 0 && nb != na && n.Nodes[nb].FirstArm >= 0)
            {
                ctx.NodeB = nb;
                ctx.GenB = n.Nodes[nb].Generation;
                ctx.CtlB = n.Nodes[nb].Control;
            }
        }
    }

    private static void BeginRemoveRoad(World w, in Command cmd, ref InvCtx ctx)
    {
        RemoveRoadCmd c = cmd.RemoveRoad;
        NetworkStore n = w.Net;
        InverseLog log = w.Inv;
        if (!n.ResolveSegment(in c.Seg) || w.Ops.FindSegment(c.Seg.Index) >= 0)
        {
            return;   // команда будет отклонена
        }

        int seg = c.Seg.Index;
        ref readonly SegmentData sd = ref n.Segments[seg];
        bool keep = (c.Flags & RemoveRoadFlags.KeepNodes) != 0;
        EntityRef self = new(seg, sd.Generation);
        ref readonly NodeData na = ref n.Nodes[sd.NodeA];
        ref readonly NodeData nb = ref n.Nodes[sd.NodeB];
        bool freeA = !keep && na.ArmCount == 1;
        bool freeB = !keep && nb.ArmCount == 1;
        var b = new BuildRoadCmd
        {
            Ax = na.Position.X,
            Az = na.Position.Z,
            Bx = nb.Position.X,
            Bz = nb.Position.Z,
            SpeedLimit = sd.SpeedLimit,
            LanesAB = sd.LanesAB,
            LanesBA = sd.LanesBA,
            Kind = sd.Kind,
            RestoreOf = self,
            RestoreNodeA = freeA ? new EntityRef(sd.NodeA, na.Generation) : EntityRef.None,
            RestoreNodeB = freeB ? new EntityRef(sd.NodeB, nb.Generation) : EntityRef.None,
        };
        if ((sd.Flags & SegmentFlags.Curve) != 0)
        {
            b.Flags = BuildRoadFlags.Curve;
            b.Cx = sd.Cx;
            b.Cz = sd.Cz;
        }
        else if (sd.Profile >= 0)
        {
            // Профиль и зеркальность возвращаются вместе с сегментом (RP7): иначе дорога вернулась бы без профиля (поле делит байты с Cz - кривые и профили вместе пока не поддержаны).
            b.Profile = (ushort)(sd.Profile + 1);
            if ((sd.Flags & SegmentFlags.Mirrored) != 0)
            {
                b.Flags |= BuildRoadFlags.Mirrored;
            }
        }

        Add(log, ref ctx, Command.MakeBuildRoad(in b));

        const byte writable = LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut;
        for (byte dir = 0; dir < 2; dir++)
        {
            int lanes = dir == 0 ? sd.LanesAB : sd.LanesBA;
            int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            for (byte q = 0; q < lanes; q++)
            {
                byte f = (byte)(n.Lanes[first + q].Flags & writable);
                if (f != 0)
                {
                    Add(log, ref ctx, Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = self, Dir = dir, LaneNo = q, Flags = f, Mask = writable }));
                }
            }
        }

        EndNodeSettings(w, ref ctx, sd.NodeA, seg, freeA);
        EndNodeSettings(w, ref ctx, sd.NodeB, seg, freeB);
    }

    // Настройки конца сносимого сегмента: режим и план узла, у выживающего узла - ещё знак и запреты манёвров рукава сегмента.
    private static void EndNodeSettings(World w, ref InvCtx ctx, int node, int seg, bool freed)
    {
        NetworkStore n = w.Net;
        InverseLog log = w.Inv;
        ref readonly NodeData nd = ref n.Nodes[node];
        if (nd.FirstArm < 0)
        {
            return;
        }

        int gen = nd.Generation;
        // Режим и план узла: перестройка без рукава (и пересоздание узла) возвращает режим по умолчанию для степени узла.
        Add(log, ref ctx, Command.MakeSetNodeControl(new SetNodeControlCmd { Node = node, Control = nd.Control, NodeGen = gen }));
        AddPlanIfCustom(w, ref ctx, node);

        if (freed)
        {
            return;
        }

        int slot = -1;
        for (int k = 0; k < nd.ArmCount; k++)
        {
            if (n.NodeArms[nd.FirstArm + k] == seg)
            {
                slot = k;
                break;
            }
        }

        if (slot < 0 || slot >= 8)
        {
            return;
        }

        int ov = w.Builder.ArmOverrideOf(node, slot);
        if (ov != 0)
        {
            Add(log, ref ctx, Command.MakeSetArmControl(new SetArmControlCmd { Node = new EntityRef(node, gen), ArmSlot = (byte)slot, Control = (byte)ov }));
        }

        if (nd.TurnBan != 0)
        {
            for (int o = 0; o < 8; o++)
            {
                if ((nd.TurnBan & (1UL << ((slot * 8) + o))) != 0)
                {
                    Add(log, ref ctx, Command.MakeSetTurnBan(new SetTurnBanCmd { Node = new EntityRef(node, gen), FromArm = (byte)slot, ToArm = (byte)o, Banned = 1 }));
                }

                if (o != slot && (nd.TurnBan & (1UL << ((o * 8) + slot))) != 0)
                {
                    Add(log, ref ctx, Command.MakeSetTurnBan(new SetTurnBanCmd { Node = new EntityRef(node, gen), FromArm = (byte)o, ToArm = (byte)slot, Banned = 1 }));
                }
            }
        }
    }

    // План узла возвращается отдельной командой, только если он не равен плану по умолчанию: SetNodeControl(Signal) и так создаёт план по умолчанию для нынешних осей узла,
    // а снимок плана привязан к числу осей на момент записи (дренаж асинхронен, рукава узла к моменту отката могут быть другими).
    private static void AddPlanIfCustom(World w, ref InvCtx ctx, int node)
    {
        ref readonly NodeData nd = ref w.Net.Nodes[node];
        if (nd.Signal < 0)
        {
            return;
        }

        SetSignalPlanCmd cur = w.Signals.ReadPlan(nd.Signal);
        int axes = w.Signals.PhaseCount[nd.Signal];
        SetSignalPlanCmd def = Rules.Intersections.SignalStore.Resolve(Rules.Intersections.SignalStore.DefaultPlan(w, node, axes), axes, in w.Rules);
        bool same = cur.Mode == def.Mode && cur.PhaseCount == def.PhaseCount && cur.YellowTicks == def.YellowTicks && cur.AllRedTicks == def.AllRedTicks && cur.OffsetTicks == def.OffsetTicks &&
            cur.MinGreenTicks == def.MinGreenTicks && cur.GapTicks == def.GapTicks && cur.Flags == def.Flags;
        for (int k = 0; same && k < axes; k++)
        {
            same = Rules.Intersections.SignalStore.GreenOf(in cur, k) == Rules.Intersections.SignalStore.GreenOf(in def, k);
        }

        if (!same)
        {
            cur.Node = node;
            cur.NodeGen = nd.Generation;
            Add(w.Inv, ref ctx, Command.MakeSetSignalPlan(in cur));
        }
    }

    private static void BeginArmControl(World w, in Command cmd, ref InvCtx ctx)
    {
        SetArmControlCmd a = cmd.SetArmControl;
        NetworkStore n = w.Net;
        if (!n.ResolveNode(in a.Node) || n.Nodes[a.Node.Index].FirstArm < 0 || a.ArmSlot >= n.Nodes[a.Node.Index].ArmCount)
        {
            return;
        }

        ref readonly NodeData nd = ref n.Nodes[a.Node.Index];
        int prev = w.Builder.ArmOverrideOf(a.Node.Index, a.ArmSlot);
        Add(w.Inv, ref ctx, Command.MakeSetArmControl(new SetArmControlCmd { Node = new EntityRef(a.Node.Index, nd.Generation), ArmSlot = a.ArmSlot, Control = (byte)prev }));
        if (nd.Control != NodeControl.Signal)
        {
            // Знак второстепенного рукава сам переключает режим узла без правил (уступи/стоп): возвращаем и режим.
            Add(w.Inv, ref ctx, Command.MakeSetNodeControl(new SetNodeControlCmd { Node = a.Node.Index, Control = nd.Control, NodeGen = nd.Generation }));
        }
    }

    private static void BeginTurnBan(World w, in Command cmd, ref InvCtx ctx)
    {
        SetTurnBanCmd t = cmd.SetTurnBan;
        NetworkStore n = w.Net;
        if (!n.ResolveNode(in t.Node) || t.FromArm >= 8 || t.ToArm >= 8)
        {
            return;
        }

        ref readonly NodeData nd = ref n.Nodes[t.Node.Index];
        byte prev = (byte)((nd.TurnBan >> ((t.FromArm * 8) + t.ToArm)) & 1UL);
        Add(w.Inv, ref ctx, Command.MakeSetTurnBan(new SetTurnBanCmd { Node = new EntityRef(t.Node.Index, nd.Generation), FromArm = t.FromArm, ToArm = t.ToArm, Banned = prev }));
    }

    private static void BeginLaneFlags(World w, in Command cmd, ref InvCtx ctx)
    {
        SetLaneFlagsCmd f = cmd.SetLaneFlags;
        NetworkStore n = w.Net;
        if (!n.ResolveSegment(in f.Seg) || f.Dir > 1)
        {
            return;
        }

        ref readonly SegmentData sd = ref n.Segments[f.Seg.Index];
        int lanes = f.Dir == 0 ? sd.LanesAB : sd.LanesBA;
        if (f.LaneNo >= lanes)
        {
            return;
        }

        int lane = (f.Dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA) + f.LaneNo;
        byte prev = (byte)(n.Lanes[lane].Flags & f.Mask);
        Add(w.Inv, ref ctx, Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = new EntityRef(f.Seg.Index, sd.Generation), Dir = f.Dir, LaneNo = f.LaneNo, Flags = prev, Mask = f.Mask }));
    }

    private static void BeginNodeControl(World w, in Command cmd, ref InvCtx ctx)
    {
        SetNodeControlCmd c = cmd.SetNodeControl;
        NetworkStore n = w.Net;
        if (!n.ResolveNode(c.Node, c.NodeGen))
        {
            return;
        }

        ref readonly NodeData nd = ref n.Nodes[c.Node];
        if (nd.Control == c.Control)
        {
            return;   // режим не меняется: обратной записи нечего возвращать (и снимок плана к моменту отката мог бы не подойти)
        }

        Add(w.Inv, ref ctx, Command.MakeSetNodeControl(new SetNodeControlCmd { Node = c.Node, Control = nd.Control, NodeGen = nd.Generation }));
        AddPlanIfCustom(w, ref ctx, c.Node);
    }

    private static void BeginSignalPlan(World w, in Command cmd, ref InvCtx ctx)
    {
        SetSignalPlanCmd s = cmd.SetSignalPlan;
        NetworkStore n = w.Net;
        if (!n.ResolveNode(s.Node, s.NodeGen) || n.Nodes[s.Node].Signal < 0)
        {
            return;
        }

        SetSignalPlanCmd p = w.Signals.ReadPlan(n.Nodes[s.Node].Signal);
        p.Node = s.Node;
        p.NodeGen = n.Nodes[s.Node].Generation;
        Add(w.Inv, ref ctx, Command.MakeSetSignalPlan(in p));
    }
}
