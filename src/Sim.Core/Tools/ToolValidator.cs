using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Tools;

/// <summary>Привязка конца новой дороги (<see cref="PreviewResult.SnapKindA"/>).</summary>
internal static class SnapKind
{
    public const byte NewNode = 0;
    public const byte ExistingNode = 1;
    public const byte SplitSegment = 2;
}

/// <summary>
/// Результат проверки команды инструмента (player-tools.md §7): вердикт, привязка, размеры, стоимость и точка проблемы для подсветки.
/// Заполняет <see cref="ToolValidator.Check"/>; поля, не относящиеся к команде, остаются нулями.
/// </summary>
internal struct ToolCheck
{
    public RejectReason Reason;

    /// <summary>Исчерпанная ёмкость при <see cref="RejectReason.Capacity"/> (0 - не определена: применение событие не выдаёт).</summary>
    public CapacityKind CapKind;

    public byte Mechanism;                  // 0 на месте, 1 перенос, 2 дренаж
    public byte SnapKindA, SnapKindB;
    public int NodeA, NodeB;                // существующий узел конца или -1 (новый)
    public float AX, AZ, BX, BZ, LengthM;   // концы после привязки
    public int NewNodes, NewSegments, NewLanes;
    public int Seg;                         // целевой сегмент (снос, правка полос) или -1
    public int Node;                        // целевой узел или -1
    public bool Reopen;                     // BuildRoad(RestoreOf) = отмена сноса
    public long Cost;                       // > 0 трата, < 0 возврат; при city_budget - эффективная (undo-кредит учтён, ToolCosts.Resolve)
    public int CreditSlot;                  // запись UndoCredits, которую погасит применение (-1 - нет)

    // Профили (RP7): итоговые значения команды после разбора (-1 - без профиля).
    public int Profile;
    public bool Mirrored;
    public byte LanesAB, LanesBA;
    public RoadKind Kind;
    public float Speed;
    public ReshapeMode Reshape;             // UpgradeRoad: что делать с сегментом
    public int Drain;                       // UpgradeRoad занятого сегмента: машин ждать (> 0 - операция ShrinkLanes, Mechanism 2)
    public bool CancelShrink;               // UpgradeRoad в прежние полосы во время дренажа полос: отмена операции (undo)
    public int SplitSegA, SplitSegB;        // BuildRoad с AutoSplit: разрезаемый сегмент у конца A/B или -1 (T5)
    public float SplitSA, SplitSB;          // точка разреза по полной оси сегмента, м от его узла A

    public EntityKind IssueKind;            // объект и точка проблемы (подсветка) для отказа
    public int IssueIndex, IssueGen;
    public float IssueX, IssueZ;
}

/// <summary>
/// Валидатор команд инструментов (player-tools.md §7.1): одна чистая функция на превью и применение. Не меняет мир (единственное исключение - временная правка
/// <c>TurnBan</c> узла на время планирования перестройки, значение возвращается до выхода). Ошибки только локальные и детерминированные; глобальные предупреждения
/// (связность) считает только превью (<see cref="ToolPreview"/>). Без аллокаций.
/// </summary>
internal static partial class ToolValidator
{
    /// <summary>Поддерживает ли валидатор команду (иначе превью даёт <see cref="RejectReason.NotSupported"/>, как и применение).</summary>
    public static bool Handles(CommandKind k) => k is CommandKind.BuildRoad or CommandKind.RemoveRoad or CommandKind.SetArmControl or CommandKind.SetTurnBan
        or CommandKind.SetLaneFlags or CommandKind.SetNodeControl or CommandKind.SetSignalPlan or CommandKind.UpgradeRoad or CommandKind.SplitRoad or CommandKind.MergeAtNode;

    public static RejectReason Check(World w, in Command cmd, ref ToolCheck c)
    {
        c = default;
        c.NodeA = -1;
        c.NodeB = -1;
        c.Seg = -1;
        c.Node = -1;
        c.Profile = -1;
        c.SplitSegA = -1;
        c.SplitSegB = -1;
        c.Reason = cmd.Kind switch
        {
            CommandKind.BuildRoad => CheckBuildRoad(w, in cmd, ref c),
            CommandKind.UpgradeRoad => CheckUpgradeRoad(w, in cmd, ref c),
            CommandKind.RemoveRoad => CheckRemoveRoad(w, in cmd, ref c),
            CommandKind.SetArmControl => CheckSetArmControl(w, in cmd, ref c),
            CommandKind.SetTurnBan => CheckSetTurnBan(w, in cmd, ref c),
            CommandKind.SetLaneFlags => CheckSetLaneFlags(w, in cmd, ref c),
            CommandKind.SetNodeControl => CheckSetNodeControl(w, in cmd, ref c),
            CommandKind.SetSignalPlan => CheckSetSignalPlan(w, in cmd, ref c),
            CommandKind.SplitRoad => CheckSplitRoad(w, in cmd, ref c),
            CommandKind.MergeAtNode => CheckMergeAtNode(w, in cmd, ref c),
            _ => RejectReason.NotSupported,
        };
        if (c.Reason == RejectReason.None)
        {
            c.Reason = ToolCosts.Resolve(w, in cmd, ref c);   // E4: цена с undo-кредитом и InsufficientFunds - одно место для превью и применения
        }
        else
        {
            c.CreditSlot = -1;
        }

        return c.Reason;
    }

    // ---------------------------------------------------------------- BuildRoad

    private static RejectReason CheckBuildRoad(World w, in Command cmd, ref ToolCheck c)
    {
        BuildRoadCmd b = cmd.BuildRoad;
        NetworkStore n = w.Net;
        if ((b.Flags & ~BuildRoadFlags.Supported) == 0 && b.RestoreOf.Generation != 0 &&
            n.ResolveSegment(in b.RestoreOf) && w.Ops.FindSegment(b.RestoreOf.Index) >= 0)
        {
            c.Reopen = true;   // отмена сноса: живой закрывающийся сегмент переоткрывается (player-tools.md §8.2)
            c.Seg = b.RestoreOf.Index;
            return RejectReason.None;
        }

        if ((b.Flags & ~BuildRoadFlags.Supported) != 0)
        {
            return RejectReason.NotSupported;   // Curve (T5: кривые ещё не сделаны)
        }

        if (b.RestoreOf.Generation != 0 && n.ResolveSegment(in b.RestoreOf))
        {
            return RejectReason.InvalidHandle;   // RestoreOf жив, но не в дренаже: восстанавливать нечего (хост перепутал handle)
        }

        if (!float.IsFinite(b.Ax) || !float.IsFinite(b.Az) || !float.IsFinite(b.Bx) || !float.IsFinite(b.Bz) ||
            !float.IsFinite(b.SpeedLimit) || b.SpeedLimit <= 0f || b.LanesAB + b.LanesBA == 0 ||
            b.LanesAB > NetworkBuilder.MaxLanesPerDirection || b.LanesBA > NetworkBuilder.MaxLanesPerDirection)
        {
            return RejectReason.InvalidArgument;
        }

        RejectReason prof = CheckBuildProfile(w, in b, ref c);
        if (prof != RejectReason.None)
        {
            return prof;
        }

        var a = new Vec2(b.Ax, b.Az);
        var z = new Vec2(b.Bx, b.Bz);
        if (!InBounds(w, a) || !InBounds(w, z))
        {
            Mark(ref c, EntityKind.None, -1, 0, InBounds(w, a) ? z : a);
            return RejectReason.OutOfBounds;
        }

        NetworkBuilder bld = w.Builder;
        NetworkOptions o = bld.Options;
        int na = bld.FindNode(a);
        int nb = bld.FindNode(z);
        if ((b.Flags & BuildRoadFlags.AutoSplit) != 0)
        {
            // T5: конец без узла рядом, но на оси сегмента (в SnapRadius) - разрез этого сегмента, конец - новый узел разреза.
            RejectReason sa = na < 0 ? FindSplit(w, ref a, ref c, out c.SplitSegA, out c.SplitSA) : RejectReason.None;
            RejectReason sb = sa == RejectReason.None && nb < 0 ? FindSplit(w, ref z, ref c, out c.SplitSegB, out c.SplitSB) : RejectReason.None;
            if (sa != RejectReason.None || sb != RejectReason.None)
            {
                return sa != RejectReason.None ? sa : sb;
            }

            if (c.SplitSegA >= 0 && c.SplitSegA == c.SplitSegB)
            {
                return RejectReason.InvalidArgument;   // оба конца на одном сегменте - дорога вдоль него
            }
        }

        float r2 = o.SnapRadius * o.SnapRadius;
        if (na >= 0 && na == nb)
        {
            Mark(ref c, EntityKind.Node, na, n.Nodes[na].Generation, a);
            return RejectReason.InvalidArgument;
        }

        if (na < 0)
        {
            // Конец A станет новым узлом; конец B может прилипнуть к нему (ближе существующего) - тогда сегмент вырожден (ровно как в AddSegment).
            float dx = z.X - a.X;
            float dz = z.Z - a.Z;
            float dab = (dx * dx) + (dz * dz);
            if (dab <= r2)
            {
                int fresh = n.NodePool.PeekIndex();
                bool existingCloser = false;
                if (nb >= 0)
                {
                    float ex = n.Nodes[nb].Position.X - z.X;
                    float ez = n.Nodes[nb].Position.Z - z.Z;
                    float d = (ex * ex) + (ez * ez);
                    existingCloser = d < dab || (d == dab && nb < fresh);
                }

                if (!existingCloser)
                {
                    Mark(ref c, EntityKind.None, -1, 0, a);
                    return RejectReason.InvalidArgument;
                }
            }
        }

        c.NodeA = na;
        c.NodeB = nb;
        c.SnapKindA = na >= 0 ? SnapKind.ExistingNode : c.SplitSegA >= 0 ? SnapKind.SplitSegment : SnapKind.NewNode;
        c.SnapKindB = nb >= 0 ? SnapKind.ExistingNode : c.SplitSegB >= 0 ? SnapKind.SplitSegment : SnapKind.NewNode;
        Vec2 pa = na >= 0 ? n.Nodes[na].Position : a;
        Vec2 pb = nb >= 0 ? n.Nodes[nb].Position : z;
        c.AX = pa.X;
        c.AZ = pa.Z;
        c.BX = pb.X;
        c.BZ = pb.Z;
        float len = Dist(pa, pb);
        c.LengthM = len;
        if (len < 0.01f)
        {
            Mark(ref c, EntityKind.None, -1, 0, pa);
            return RejectReason.InvalidArgument;
        }

        // Минимум оси - по базовому отступу: увеличенные отступы ADR-0028 у коротких сегментов сжимаются построителем, а не отклоняют их.
        if (len < (2f * o.NodeSetback) + o.MinLaneLength)
        {
            Mark(ref c, EntityKind.None, -1, 0, pa);
            return RejectReason.TooShort;
        }

        // Q-meso не умеет перестраивать построенный узел (состояние очередей по полосам).
        if (w.Queue != null && ((na >= 0 && n.Nodes[na].FirstArm >= 0) || (nb >= 0 && n.Nodes[nb].FirstArm >= 0)))
        {
            return RejectReason.NotSupported;
        }

        // Дорога по профилю (RP7): полосы ±край профиля не должны накладываться на соседние дороги вне дисков общих узлов.
        if (c.Profile >= 0)
        {
            Vec2 dir = new Vec2(pb.X - pa.X, pb.Z - pa.Z) * (1f / len);
            float sa = na >= 0 ? bld.IncrementalSetback(na, pb, 0f) : o.NodeSetback;
            float sb = nb >= 0 ? bld.IncrementalSetback(nb, pa, 0f) : o.NodeSetback;
            sa = MathF.Min(sa, len * 0.5f);
            sb = MathF.Min(sb, len * 0.5f);
            Span<Vec2> axis = stackalloc Vec2[2];
            axis[0] = pa + (dir * sa);
            axis[1] = pb - (dir * sb);
            RejectReason ov = CheckFootprint(w, axis, n.Profiles!.EdgeMm[c.Profile] * 0.001f, na, nb, c.SplitSegA >= 0 ? c.SplitSegA : c.SplitSegB, ref c);
            if (ov != RejectReason.None)
            {
                return ov;
            }
        }

        // Рукава: каждый конец добавляет один.
        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? na : nb;
            if (node >= 0 && bld.ArmCount(node) + 1 > NetworkStore.MaxArms)
            {
                Mark(ref c, EntityKind.Node, node, n.Nodes[node].Generation, n.Nodes[node].Position);
                return RejectReason.NodeLimit;
            }
        }

        int lanesAB = b.LanesAB;
        int lanesBA = b.LanesBA;
        RejectReason trap = LocalTrap(w, lanesAB, lanesBA, na, nb, ref c, pa, pb);
        if (trap != RejectReason.None)
        {
            return trap;
        }

        // Ёмкости (порядок и числа как в AddNode/AddSegment: прямой сегмент = 2 точки оси).
        int newNodes = (na < 0 ? 1 : 0) + (nb < 0 ? 1 : 0);
        int laneTotal = lanesAB + lanesBA;
        c.NewNodes = newNodes;
        c.NewSegments = 1;
        c.NewLanes = laneTotal;
        if (n.NodePool.Capacity - n.NodePool.AliveCount < newNodes)
        {
            c.CapKind = CapacityKind.Nodes;
            return RejectReason.Capacity;
        }

        if (n.SegmentPool.PeekIndex() < 0 || ((c.SplitSegA >= 0 || c.SplitSegB >= 0) && n.SegmentPool.Capacity - n.SegmentPool.AliveCount < 5))
        {
            c.CapKind = CapacityKind.Segments;
            return RejectReason.Capacity;
        }

        for (int e = 0; e < 2; e++)
        {
            int ss = e == 0 ? c.SplitSegA : c.SplitSegB;
            if (ss >= 0)
            {
                int tl = n.Segments[ss].LanesAB + n.Segments[ss].LanesBA;
                if (ChainCapacity(w, 1, 2, 2 * tl, (2 * n.Segments[ss].PointCount) + 2, tl) != RejectReason.None)
                {
                    c.CapKind = CapacityKind.Lanes;
                    return RejectReason.Capacity;
                }
            }
        }

        if (!n.CanAllocBlock(n.LaneArena, laneTotal))
        {
            c.CapKind = CapacityKind.Lanes;
            return RejectReason.Capacity;
        }

        if (!n.CanAllocBlock(n.LanePointArena, laneTotal * 2) || !n.CanAllocBlock(n.SegmentPointArena, 2))
        {
            c.CapKind = CapacityKind.LanePoints;
            return RejectReason.Capacity;
        }

        c.Mechanism = (byte)((na >= 0 && n.Nodes[na].FirstArm >= 0) || (nb >= 0 && n.Nodes[nb].FirstArm >= 0) ? 1 : 0);
        c.Cost = RoadCost(in w.ToolsCfg.Costs, b.Kind, len, laneTotal, curve: false);
        return RejectReason.None;
    }

    /// <summary>
    /// <c>CreatesTrap</c> (player-tools.md §7.3): односторонний сегмент, въезжающий в узел, из которого после постройки нет выезда
    /// (двусторонний тупик - не ловушка: разворот есть). Закрывающиеся сегменты выездом не считаются.
    /// </summary>
    private static RejectReason LocalTrap(World w, int lanesAB, int lanesBA, int na, int nb, ref ToolCheck c, Vec2 pa, Vec2 pb)
    {
        if (lanesAB > 0 && lanesBA > 0)
        {
            return RejectReason.None;
        }

        int into = lanesAB > 0 ? nb : na;   // узел, в который въезжает единственное направление
        if (into >= 0 && HasExit(w.Net, into))
        {
            return RejectReason.None;
        }

        EntityKind kind = into >= 0 ? EntityKind.Node : EntityKind.None;
        Mark(ref c, kind, into, into >= 0 ? w.Net.Nodes[into].Generation : 0, lanesAB > 0 ? pb : pa);
        return RejectReason.CreatesTrap;
    }

    // Есть ли у узла выезд по существующему сегменту (не закрывающемуся).
    private static bool HasExit(NetworkStore n, int node)
    {
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            int seg = code >> 1;
            ref readonly SegmentData sd = ref n.Segments[seg];
            if ((sd.Flags & SegmentFlags.Closing) != 0)
            {
                continue;
            }

            int lanesOut = (code & 1) == 0 ? sd.LanesAB : sd.LanesBA;   // конец 0 = NodeA: уход A->B
            if (lanesOut > 0)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- RemoveRoad

    private static RejectReason CheckRemoveRoad(World w, in Command cmd, ref ToolCheck c)
    {
        RemoveRoadCmd r = cmd.RemoveRoad;
        if ((r.Flags & ~1) != 0)
        {
            return RejectReason.InvalidArgument;
        }

        NetworkStore n = w.Net;
        if (!n.ResolveSegment(in r.Seg))
        {
            return RejectReason.InvalidHandle;
        }

        int seg = r.Seg.Index;
        c.Seg = seg;
        ref readonly SegmentData sd = ref n.Segments[seg];
        Vec2 mid = n.SegmentPoints[sd.FirstPoint + (sd.PointCount / 2)];
        Mark(ref c, EntityKind.Segment, seg, sd.Generation, mid);
        if ((sd.Flags & SegmentFlags.Closing) != 0 || w.Ops.FindShrink(seg) >= 0)
        {
            return RejectReason.Busy;   // уже сносится или в дренаже полос (T5)
        }

        if (w.Queue != null || HasBusStop(n, seg))
        {
            return RejectReason.NotSupported;   // остановка на сегменте - до T9; Q-meso
        }

        if (w.Ops.Count >= w.Ops.Capacity)
        {
            return RejectReason.Capacity;
        }

        c.Mechanism = 2;
        c.LengthM = w.Builder.FullAxisLength(seg);   // от узла до узла: отступы концов по рукавам (ADR-0028), не 2 * NodeSetback
        long build = RoadCost(in w.ToolsCfg.Costs, sd.Kind, c.LengthM, sd.LanesAB + sd.LanesBA, (sd.Flags & SegmentFlags.Curve) != 0);
        c.Cost = -(build * w.ToolsCfg.Costs.DemolishRefundPct / 100);
        return RejectReason.None;
    }

    internal static bool HasBusStop(NetworkStore n, int seg)
    {
        for (int s = 0; s < n.BusStopCount; s++)
        {
            if (n.BusStopAlive(s) && n.BusStops[s].Lane >= 0 && n.Lanes[n.BusStops[s].Lane].Segment == seg)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- знаки, запреты, флаги

    private static RejectReason CheckSetArmControl(World w, in Command cmd, ref ToolCheck c)
    {
        SetArmControlCmd a = cmd.SetArmControl;
        NetworkStore n = w.Net;
        if (!n.ResolveNode(in a.Node))
        {
            return RejectReason.InvalidHandle;
        }

        int node = a.Node.Index;
        c.Node = node;
        ref readonly NodeData nd = ref n.Nodes[node];
        Mark(ref c, EntityKind.Node, node, nd.Generation, nd.Position);
        if (nd.FirstArm < 0 || a.ArmSlot >= nd.ArmCount || a.Control > 3 || a.R != 0)
        {
            return RejectReason.InvalidArgument;
        }

        if (a.Control != 0 && w.Builder.ArmOverrideOf(node, a.ArmSlot) != a.Control)
        {
            c.Cost = w.ToolsCfg.Costs.ArmSign;
        }

        return RejectReason.None;
    }

    private static RejectReason CheckSetTurnBan(World w, in Command cmd, ref ToolCheck c)
    {
        SetTurnBanCmd t = cmd.SetTurnBan;
        NetworkStore n = w.Net;
        if (!n.ResolveNode(in t.Node))
        {
            return RejectReason.InvalidHandle;
        }

        int node = t.Node.Index;
        c.Node = node;
        ref NodeData nd = ref n.Nodes[node];
        Mark(ref c, EntityKind.Node, node, nd.Generation, nd.Position);
        if (nd.FirstArm < 0 || t.FromArm >= nd.ArmCount || t.ToArm >= nd.ArmCount || t.FromArm >= 8 || t.ToArm >= 8 || t.Banned > 1 || t.R != 0)
        {
            return RejectReason.InvalidArgument;
        }

        if (w.Queue != null)
        {
            return RejectReason.NotSupported;
        }

        ulong bit = 1UL << ((t.FromArm * 8) + t.ToArm);
        ulong old = nd.TurnBan;
        ulong want = t.Banned == 1 ? old | bit : old & ~bit;
        if (want == old)
        {
            return RejectReason.None;
        }

        c.Mechanism = 1;   // перестройка узла
        nd.TurnBan = want;   // на время планирования: PlanRebuild читает запреты из узла; значение возвращается ниже
        NetworkStatus st = w.Builder.PlanRebuild(w, node, rejectTrap: t.Banned == 1, out _);
        nd.TurnBan = old;
        if (st != NetworkStatus.Ok)
        {
            return RejectOf(st);
        }

        if (t.Banned == 1)
        {
            c.Cost = w.ToolsCfg.Costs.TurnBan;
        }

        return RejectReason.None;
    }

    private static RejectReason CheckSetLaneFlags(World w, in Command cmd, ref ToolCheck c)
    {
        SetLaneFlagsCmd f = cmd.SetLaneFlags;
        NetworkStore n = w.Net;
        if (!n.ResolveSegment(in f.Seg))
        {
            return RejectReason.InvalidHandle;
        }

        c.Seg = f.Seg.Index;
        ref readonly SegmentData sd = ref n.Segments[f.Seg.Index];
        Mark(ref c, EntityKind.Segment, f.Seg.Index, sd.Generation, n.SegmentPoints[sd.FirstPoint + (sd.PointCount / 2)]);
        int lanes = f.Dir == 0 ? sd.LanesAB : f.Dir == 1 ? sd.LanesBA : 0;
        const byte writable = LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut;
        if (f.Dir > 1 || f.LaneNo >= lanes || (f.Mask & ~writable) != 0 || (f.Flags & ~f.Mask) != 0)
        {
            return RejectReason.InvalidArgument;
        }

        return RejectReason.None;
    }

    private static RejectReason CheckSetNodeControl(World w, in Command cmd, ref ToolCheck c)
    {
        SetNodeControlCmd s = cmd.SetNodeControl;
        NetworkStore n = w.Net;
        if ((uint)s.Node >= (uint)n.NodeCount || (uint)s.Control > (uint)NodeControl.RightHandRule)
        {
            return RejectReason.InvalidArgument;
        }

        if (!n.ResolveNode(s.Node, s.NodeGen))
        {
            return RejectReason.InvalidHandle;
        }

        c.Node = s.Node;
        ref readonly NodeData nd = ref n.Nodes[s.Node];
        Mark(ref c, EntityKind.Node, s.Node, nd.Generation, nd.Position);
        if (s.Control == NodeControl.Signal && nd.Signal < 0)
        {
            if (w.Signals.Count >= w.Signals.Capacity)
            {
                return RejectReason.Capacity;
            }

            if (SignalStore.AxesOf(w, s.Node) < 1)
            {
                return RejectReason.InvalidArgument;
            }

            c.Cost = w.ToolsCfg.Costs.NodeSignal;
        }

        return RejectReason.None;
    }

    private static RejectReason CheckSetSignalPlan(World w, in Command cmd, ref ToolCheck c)
    {
        SetSignalPlanCmd p = cmd.SetSignalPlan;
        NetworkStore n = w.Net;
        if ((uint)p.Node >= (uint)n.NodeCount)
        {
            return RejectReason.InvalidArgument;
        }

        if (!n.ResolveNode(p.Node, p.NodeGen))
        {
            return RejectReason.InvalidHandle;
        }

        c.Node = p.Node;
        ref readonly NodeData nd = ref n.Nodes[p.Node];
        Mark(ref c, EntityKind.Node, p.Node, nd.Generation, nd.Position);
        if (nd.Control != NodeControl.Signal || nd.Signal < 0 || nd.Signal >= w.Signals.Count)
        {
            return RejectReason.InvalidArgument;
        }

        int axes = w.Signals.PhaseCount[nd.Signal];
        SetSignalPlanCmd resolved = SignalStore.Resolve(in p, axes, in w.Rules);
        if (!SignalStore.IsValid(in resolved, axes, enforceRed: true, pedGreenTicks: Sim.Core.Peds.PedSignals.CoordMinGreenTicks(w, p.Node)))
        {
            return RejectReason.InvalidArgument;
        }

        return RejectReason.None;
    }

    // ---------------------------------------------------------------- общие помощники

    /// <summary>Отказ узла/сегмента перестройки: общий перевод статуса сети в причину команды.</summary>
    internal static RejectReason RejectOf(NetworkStatus st) => st switch
    {
        NetworkStatus.Busy => RejectReason.Busy,
        NetworkStatus.Trap => RejectReason.CreatesTrap,
        NetworkStatus.TooManyArms or NetworkStatus.TooManyConnectors => RejectReason.NodeLimit,
        _ when st.IsCapacity() => RejectReason.Capacity,
        _ => RejectReason.InvalidArgument,
    };

    /// <summary>
    /// Стоимость дороги (целые единицы): длина в мм x полосы x ставка за метр-полосу, кривая - <c>curve_factor_pct</c> (0 = 100). Округление - к ближайшему целому.
    /// </summary>
    public static long RoadCost(in ToolCostsDesc costs, RoadKind kind, float lengthM, int lanes, bool curve)
    {
        long rate = kind == RoadKind.Arterial ? costs.RoadPerMLaneArterial : costs.RoadPerMLaneStreet;
        long mm = (long)MathF.Round(lengthM * 1000f);
        long v = (((mm * lanes * rate) + 500) / 1000);
        if (curve)
        {
            int pct = costs.CurveFactorPct == 0 ? 100 : costs.CurveFactorPct;
            v = v * pct / 100;
        }

        return v;
    }

    /// <summary>Границы мира: <c>tools.world_bounds</c> или bounding box живых узлов + 2 км (кэш по версии сети). Пустая сеть - без ограничения.</summary>
    public static bool InBounds(World w, Vec2 p)
    {
        ToolsDesc t = w.ToolsCfg;
        if (t.HasWorldBounds)
        {
            return p.X >= t.BoundsMinX && p.X <= t.BoundsMaxX && p.Z >= t.BoundsMinZ && p.Z <= t.BoundsMaxZ;
        }

        ToolScratch s = w.ToolScratch;
        NetworkStore n = w.Net;
        if (s.BoundsVersion != n.Version || s.BoundsNodes != n.AliveNodes)
        {
            float minX = float.MaxValue;
            float minZ = float.MaxValue;
            float maxX = float.MinValue;
            float maxZ = float.MinValue;
            bool any = false;
            for (int i = 0; i < n.NodeCount; i++)
            {
                if (!n.NodeAlive(i))
                {
                    continue;
                }

                Vec2 q = n.Nodes[i].Position;
                any = true;
                minX = MathF.Min(minX, q.X);
                maxX = MathF.Max(maxX, q.X);
                minZ = MathF.Min(minZ, q.Z);
                maxZ = MathF.Max(maxZ, q.Z);
            }

            s.BoundsAny = any;
            s.BoundsMinX = minX - ToolsDesc.DefaultBoundsMarginM;
            s.BoundsMaxX = maxX + ToolsDesc.DefaultBoundsMarginM;
            s.BoundsMinZ = minZ - ToolsDesc.DefaultBoundsMarginM;
            s.BoundsMaxZ = maxZ + ToolsDesc.DefaultBoundsMarginM;
            s.BoundsVersion = n.Version;
            s.BoundsNodes = n.AliveNodes;
        }

        return !s.BoundsAny || (p.X >= s.BoundsMinX && p.X <= s.BoundsMaxX && p.Z >= s.BoundsMinZ && p.Z <= s.BoundsMaxZ);
    }

    private static float Dist(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static void Mark(ref ToolCheck c, EntityKind kind, int index, int gen, Vec2 p)
    {
        c.IssueKind = kind;
        c.IssueIndex = index;
        c.IssueGen = gen;
        c.IssueX = p.X;
        c.IssueZ = p.Z;
    }
}

/// <summary>
/// Scratch инструментов (один экземпляр на мир, вне воркеров, вне хэша и сейва): кэш границ мира и связности. Массивы связности выделяются лениво при первом превью
/// с глобальными предупреждениями и дальше не растут.
/// </summary>
internal sealed class ToolScratch
{
    public int BoundsVersion = -1;
    public int BoundsNodes = -1;
    public bool BoundsAny;
    public float BoundsMinX, BoundsMinZ, BoundsMaxX, BoundsMaxZ;

    public Connectivity? Conn;
}
