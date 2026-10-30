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
    public EntityRef Ref;                  // SplitRoad: разрезаемый сегмент; MergeAtNode: узел слияния (T5)
}

/// <summary>
/// Обратные записи ядра (player-tools.md §8.1): строятся из фактического состояния до мутации (<see cref="Begin"/>), для <c>BuildRoad</c> дополняются после неё (<see cref="End"/>,
/// индекс нового сегмента известен только тогда). Запись фиксируется, только если команда применена. Автобусные (T9): создание - снятие созданного, снятие - пересоздание с <c>RestoreOf</c>, параметры - прежние.
/// </summary>
internal static class InverseBuilder
{
    public static bool IsTool(CommandKind k) => k switch
    {
        CommandKind.BuildRoad or CommandKind.RemoveRoad or CommandKind.SetArmControl or CommandKind.SetTurnBan or CommandKind.SetLaneFlags
            or CommandKind.SetStreetAccess or CommandKind.SetNodeControl or CommandKind.SetSignalPlan or CommandKind.UpgradeRoad or CommandKind.SplitRoad or CommandKind.MergeAtNode
            or CommandKind.CreateBusRoute or CommandKind.AppendBusStop or CommandKind.InsertBusStop or CommandKind.RemoveBusStop
            or CommandKind.RemoveBusRoute or CommandKind.SetBusRouteParams or CommandKind.PaintZone or CommandKind.UpgradeService or CommandKind.SetServiceUpgrade => true,
        _ => false,
    };

    public static InvCtx Begin(World w, in Command cmd)
    {
        InverseLog log = w.Inv;
        var ctx = new InvCtx { Start = log.CmdCount, NodeA = -1, NodeB = -1 };
        log.BuiltSeg = -1;
        log.BuiltNode = -1;
        log.BuiltRoute = -1;
        log.BuiltStop = -1;
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
            case CommandKind.SetStreetAccess:
                BeginStreetAccess(w, in cmd, ref ctx);
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
            case CommandKind.UpgradeService:
                // SR5: обратная - понижение (та же команда с битом Inverse; пристройки те же); обратной к обратной нет
                if ((cmd.Flags & CommandFlags.Inverse) != 0)
                {
                    ctx.NoInverse = true;
                }
                else
                {
                    UpgradeServiceCmd u = cmd.UpgradeService;
                    Add(w.Inv, ref ctx, Command.MakeUpgradeService(u.Building, u.Annex0, u.Annex1, u.Annex2));
                }

                break;
            case CommandKind.SetServiceUpgrade:
                if ((cmd.Flags & CommandFlags.Inverse) != 0)
                {
                    ctx.NoInverse = true;
                }
                else
                {
                    SetServiceUpgradeCmd su = cmd.SetServiceUpgrade;
                    Add(w.Inv, ref ctx, Command.MakeSetServiceUpgrade(su.Building, su.Id, su.On == 0));
                }

                break;
            case CommandKind.SplitRoad:
                ctx.Ref = cmd.SplitRoad.Seg.Index >= 0 && w.Net.SegmentAlive(cmd.SplitRoad.Seg.Index)
                    ? new EntityRef(cmd.SplitRoad.Seg.Index, w.Net.Segments[cmd.SplitRoad.Seg.Index].Generation)
                    : EntityRef.None;
                break;
            case CommandKind.MergeAtNode:
                ctx.Ref = cmd.MergeAtNode.Node.Index >= 0 && w.Net.NodeAlive(cmd.MergeAtNode.Node.Index)
                    ? new EntityRef(cmd.MergeAtNode.Node.Index, w.Net.Nodes[cmd.MergeAtNode.Node.Index].Generation)
                    : EntityRef.None;
                break;
            case CommandKind.RemoveBusStop:
                BeginRemoveBusStop(w, in cmd, ref ctx);
                break;
            case CommandKind.RemoveBusRoute:
                BeginRemoveBusRoute(w, in cmd, ref ctx);
                break;
            case CommandKind.SetBusRouteParams:
                BeginBusRouteParams(w, in cmd, ref ctx);
                break;
            case CommandKind.CreateBusRoute:
            case CommandKind.AppendBusStop:
            case CommandKind.InsertBusStop:
                break;   // T9: обратная (снять созданное) - в End, индекс известен после применения
            default:
                ctx.NoInverse = true;
                break;
        }

        return ctx;
    }

    /// <summary>Остановка <paramref name="si"/> как команда вставки на позицию <paramref name="pos"/> (-1 - в конец) линии <paramref name="route"/> с восстановлением handle'а (T9).</summary>
    private static Command RestoreStop(NetworkStore n, int si, EntityRef route, int pos)
    {
        ref readonly BusStopData st = ref n.BusStops[si];
        ref readonly LaneData ld = ref n.Lanes[st.Lane];
        var ins = new InsertBusStopCmd
        {
            Route = route,
            Position = pos,
            Seg = new EntityRef(ld.Segment, n.Segments[ld.Segment].Generation),
            Dir = ld.Direction,
            LaneNo = ld.LaneNo,
            Kind = (byte)st.Kind,
            S = st.S,
            RestoreOf = new EntityRef(si, st.Generation),
        };
        return Command.MakeInsertBusStop(in ins);
    }

    private static void BeginRemoveBusStop(World w, in Command cmd, ref InvCtx ctx)
    {
        NetworkStore n = w.Net;
        RemoveBusStopCmd b = cmd.RemoveBusStop;
        if (!n.ResolveBusStop(in b.Stop) || (uint)n.BusStops[b.Stop.Index].Lane >= (uint)n.LaneCount)
        {
            return;   // будет отклонена
        }

        int r = n.BusStops[b.Stop.Index].Route;
        int pos = n.StopPos(b.Stop.Index);
        if (pos < 0)
        {
            ctx.NoInverse = true;   // остановка вне линии (не создаётся инструментами)
            return;
        }

        Add(w.Inv, ref ctx, RestoreStop(n, b.Stop.Index, new EntityRef(r, n.BusRoutes[r].Generation), pos));
    }

    private static void BeginRemoveBusRoute(World w, in Command cmd, ref InvCtx ctx)
    {
        NetworkStore n = w.Net;
        RemoveBusRouteCmd b = cmd.RemoveBusRoute;
        if (!n.ResolveBusRoute(in b.Route))
        {
            return;
        }

        int r = b.Route.Index;
        ref readonly BusRouteData br = ref n.BusRoutes[r];
        var route = new EntityRef(r, br.Generation);
        var create = new CreateBusRouteCmd
        {
            HeadwayTicks = br.HeadwayTicks < 0 ? -br.HeadwayTicks : br.HeadwayTicks,
            VehicleType = br.VehicleType,
            Flags = br.HeadwayTicks < 0 ? BusRouteFlags.Suspend : 0,
            RestoreOf = route,
        };
        InverseLog log = w.Inv;
        Add(log, ref ctx, Command.MakeCreateBusRoute(in create));
        for (int k = 0; k < br.StopCount; k++)
        {
            int si = n.BusRouteStops[br.FirstStop + k];
            if ((uint)n.BusStops[si].Lane < (uint)n.LaneCount)
            {
                Add(log, ref ctx, RestoreStop(n, si, route, -1));   // ссылка на линию переводится на пересозданную (BusRouteRemapped, та же фаза 1)
            }
        }
    }

    private static void BeginBusRouteParams(World w, in Command cmd, ref InvCtx ctx)
    {
        NetworkStore n = w.Net;
        SetBusRouteParamsCmd b = cmd.SetBusRouteParams;
        if (!n.ResolveBusRoute(in b.Route))
        {
            return;
        }

        ref readonly BusRouteData br = ref n.BusRoutes[b.Route.Index];
        var prev = new SetBusRouteParamsCmd
        {
            Route = new EntityRef(b.Route.Index, br.Generation),
            HeadwayTicks = br.HeadwayTicks < 0 ? -br.HeadwayTicks : br.HeadwayTicks,
            VehicleType = br.VehicleType,
            Flags = br.HeadwayTicks < 0 ? BusRouteFlags.Suspend : BusRouteFlags.Resume,
        };
        Add(w.Inv, ref ctx, Command.MakeSetBusRouteParams(in prev));
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

        if (cmd.Kind == CommandKind.CreateBusRoute && log.BuiltRoute >= 0)
        {
            Add(log, ref ctx, Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = new EntityRef(log.BuiltRoute, w.Net.BusRoutes[log.BuiltRoute].Generation) }));
        }
        else if (cmd.Kind is CommandKind.InsertBusStop or CommandKind.AppendBusStop && log.BuiltStop >= 0)
        {
            Add(log, ref ctx, Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = new EntityRef(log.BuiltStop, w.Net.BusStops[log.BuiltStop].Generation) }));
        }

        if (cmd.Kind == CommandKind.SplitRoad && log.BuiltNode >= 0)
        {
            // Разрез: обратная - слияние в новом узле (RestoreSeg - разрезанный сегмент, хост переведёт ссылки по SegmentRemapped).
            var m = new MergeAtNodeCmd { Node = new EntityRef(log.BuiltNode, w.Net.Nodes[log.BuiltNode].Generation), RestoreSeg = ctx.Ref };
            Add(log, ref ctx, Command.MakeMergeAtNode(in m));
        }
        else if (cmd.Kind == CommandKind.MergeAtNode && log.BuiltSeg >= 0)
        {
            // Слияние: обратная - разрез общего сегмента в точке бывшего узла (RestoreNode - бывший узел).
            var sr = new SplitRoadCmd
            {
                Seg = new EntityRef(log.BuiltSeg, w.Net.Segments[log.BuiltSeg].Generation),
                S = log.BuiltS,
                RestoreNode = ctx.Ref,
                RestoreSeg1 = log.MergedA,
                RestoreSeg2 = log.MergedB,
            };
            Add(log, ref ctx, Command.MakeSplitRoad(in sr));
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
        or CommandKind.SetLaneFlags or CommandKind.SetStreetAccess or CommandKind.SetNodeControl or CommandKind.SetSignalPlan or CommandKind.UpgradeRoad or CommandKind.PaintZone
        or CommandKind.SplitRoad or CommandKind.MergeAtNode or CommandKind.CreateBusRoute or CommandKind.AppendBusStop or CommandKind.InsertBusStop
        or CommandKind.RemoveBusStop or CommandKind.RemoveBusRoute or CommandKind.SetBusRouteParams or CommandKind.UpgradeService or CommandKind.SetServiceUpgrade;

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
        if (chk.CancelShrink)
        {
            // Отмена дренажа полос (T5): обратная - снова та правка, что ждала (redo), со скоростью до отмены.
            int k = w.Ops.FindShrink(chk.Seg);
            ToolOps.UnpackReshape(w.Ops.TargetGen[k], out int ab, out int ba, out RoadKind kind, out int profile, out bool mirrored);
            var redo = new UpgradeRoadCmd
            {
                Seg = new EntityRef(chk.Seg, sd.Generation),
                SpeedLimit = sd.SpeedLimit,
                LanesAB = (byte)ab,
                LanesBA = (byte)ba,
                Kind = kind,
                Profile = (ushort)(profile + 1),
                Flags = (byte)(profile >= 0 ? (mirrored ? UpgradeRoadFlags.Mirrored : 0) : (sd.Profile >= 0 ? UpgradeRoadFlags.NoProfile : 0)),
            };
            Add(w.Inv, ref ctx, Command.MakeUpgradeRoad(in redo));
            return;
        }

        if (chk.Reshape == ReshapeMode.None)
        {
            return;
        }

        // Прежние значения: профиль (с зеркальностью) или явные полосы и тип; сегмент без профиля, получающий профиль, возвращается с NoProfile (T5).
        // Занятость на обратную не влияет: возврат проходит тот же валидатор (перенос машин или дренаж ShrinkLanes).
        var u = new UpgradeRoadCmd
        {
            Seg = new EntityRef(chk.Seg, sd.Generation),
            SpeedLimit = sd.SpeedLimit,
            LanesAB = sd.LanesAB,
            LanesBA = sd.LanesBA,
            Kind = sd.Kind,
            Profile = (ushort)(sd.Profile + 1),
            Flags = (byte)(sd.Profile >= 0 ? ((sd.Flags & SegmentFlags.Mirrored) != 0 ? UpgradeRoadFlags.Mirrored : 0) : (chk.Profile >= 0 ? UpgradeRoadFlags.NoProfile : 0)),
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
        if (!n.ResolveSegment(in c.Seg) || w.Ops.FindAny(c.Seg.Index) >= 0)
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
            b.SetControl(sd.Cx, sd.Cz);   // T5: та же кривая (точность команды - 10 см)
        }

        if (sd.Profile >= 0)
        {
            // Профиль и зеркальность возвращаются вместе с сегментом (RP7): иначе дорога вернулась бы без профиля.
            b.Profile = (ushort)(sd.Profile + 1);
            if ((sd.Flags & SegmentFlags.Mirrored) != 0)
            {
                b.Flags |= BuildRoadFlags.Mirrored;
            }
        }

        if ((sd.Flags & SegmentFlags.Bridge) != 0)
        {
            b.Flags |= BuildRoadFlags.Bridge;   // TR3: мост возвращается мостом
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

        byte street = (byte)(sd.Deny & LaneDeny.StreetWritable);
        if (street != 0)
        {
            Add(log, ref ctx, Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = self, Deny = street, Mask = LaneDeny.StreetWritable }));   // VC2: запрет улицы возвращается
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

    private static void BeginStreetAccess(World w, in Command cmd, ref InvCtx ctx)
    {
        SetStreetAccessCmd a = cmd.SetStreetAccess;
        NetworkStore n = w.Net;
        if (!n.ResolveSegment(in a.Seg))
        {
            return;
        }

        ref readonly SegmentData sd = ref n.Segments[a.Seg.Index];
        Add(w.Inv, ref ctx, Command.MakeSetStreetAccess(new SetStreetAccessCmd { Seg = new EntityRef(a.Seg.Index, sd.Generation), Deny = (byte)(sd.Deny & a.Mask), Mask = a.Mask }));
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
