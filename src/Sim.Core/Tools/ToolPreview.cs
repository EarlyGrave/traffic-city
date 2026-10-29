using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tools;

/// <summary>Писатель предупреждений превью: считает все, записывает сколько влезло (<see cref="PreviewResult.IssueCount"/> / <see cref="PreviewResult.IssuesWritten"/>).</summary>
internal ref struct IssueWriter
{
    private readonly Span<PreviewIssue> _span;
    public int Total;
    public int Written;

    public IssueWriter(Span<PreviewIssue> span)
    {
        _span = span;
        Total = 0;
        Written = 0;
    }

    public void Add(IssueCode code, IssueSeverity severity, EntityKind kind, int index, int gen, float x, float z, int value = 0)
    {
        Total++;
        if (Written < _span.Length)
        {
            _span[Written++] = new PreviewIssue
            {
                Code = code,
                Severity = severity,
                ObjKind = (byte)kind,
                Obj = kind == EntityKind.None ? EntityRef.None : new EntityRef(index, gen),
                X = x,
                Z = z,
                Value = value,
            };
        }
    }
}

/// <summary>
/// Превью команды инструмента (player-tools.md §7): вердикт общего валидатора (<see cref="ToolValidator"/>) плюс стоимость, привязка, ось, последствия и предупреждения.
/// Не меняет мир; scratch выделяется один раз (связность - лениво при первом превью, требующем её). Вне <c>Step</c>, на том же потоке.
/// </summary>
internal static class ToolPreview
{
    private const int NearCapacityPct = 90;

    public static RejectReason Run(World w, in Command cmd, out PreviewResult r, Span<PreviewIssue> issues, Span<Vec2> axis, PreviewOptions flags)
    {
        r = default;
        r.RoutesThrough = -1;
        if (cmd.Kind == CommandKind.PaintZone)
        {
            return RunPaintZone(w, in cmd, ref r, issues);
        }

        var c = default(ToolCheck);
        RejectReason v = ToolValidator.Check(w, in cmd, ref c);
        var iw = new IssueWriter(issues);
        r.Verdict = v;
        r.Mechanism = c.Mechanism;
        r.SnapKindA = c.SnapKindA;
        r.SnapKindB = c.SnapKindB;
        r.SnapA = c.NodeA >= 0 ? new EntityRef(c.NodeA, w.Net.Nodes[c.NodeA].Generation) : EntityRef.None;
        r.SnapB = c.NodeB >= 0 ? new EntityRef(c.NodeB, w.Net.Nodes[c.NodeB].Generation) : EntityRef.None;
        if (c.SplitSegA >= 0)
        {
            r.SnapA = new EntityRef(c.SplitSegA, w.Net.Segments[c.SplitSegA].Generation);   // T5: разрезаемый сегмент (SnapKind 2)
        }

        if (c.SplitSegB >= 0)
        {
            r.SnapB = new EntityRef(c.SplitSegB, w.Net.Segments[c.SplitSegB].Generation);
        }
        r.Cost = c.Cost;
        r.AX = c.AX;
        r.AZ = c.AZ;
        r.BX = c.BX;
        r.BZ = c.BZ;
        r.LengthM = c.LengthM;
        r.NewNodes = c.NewNodes;
        r.NewSegments = c.NewSegments;
        r.NewLanes = c.NewLanes;

        if (cmd.Kind == CommandKind.BuildRoad && c.LengthM > 0f)
        {
            // Ось будущей дороги тем же сэмплированием, что у Builder: концы после привязки (у разреза - точка на оси), кривая - сэмплы Безье (T5).
            Span<Vec2> pts = stackalloc Vec2[NetworkBuilder.MaxCurvePoints];
            int np = 2;
            pts[0] = new Vec2(c.AX, c.AZ);
            pts[1] = new Vec2(c.BX, c.BZ);
            if (c.Curve)
            {
                np = NetworkBuilder.SampleQuadratic(pts[0], new Vec2(cmd.BuildRoad.ControlX, cmd.BuildRoad.ControlZ), pts[1], pts);
            }

            r.AxisPointCount = np;
            for (int i = 0; i < np && i < axis.Length; i++)
            {
                axis[i] = pts[i];
            }
        }

        if (v != RejectReason.None)
        {
            iw.Add((IssueCode)(int)v, IssueSeverity.Error, c.IssueKind, c.IssueIndex, c.IssueGen, c.IssueX, c.IssueZ, v == RejectReason.Locked ? c.LockMilestone : c.RejectValue);
        }
        else
        {
            // Обратная запись для undo: нет вовсе или не влезет в запись (> InverseLog.MaxPerRecord команд) - предупреждаем заранее (player-tools.md §8.1).
            if (!InverseBuilder.HasInverse(cmd.Kind) ||
                (cmd.Kind == CommandKind.RemoveRoad && c.Seg >= 0 && InverseBuilder.MaxRemoveRoadInverse(w, c.Seg) > InverseLog.MaxPerRecord))
            {
                iw.Add(IssueCode.UndoUnavailable, IssueSeverity.Warning, EntityKind.None, -1, 0, c.AX, c.AZ);
            }

            switch (cmd.Kind)
            {
                case CommandKind.BuildRoad:
                    BuildRoadWarnings(w, in cmd, in c, ref r, ref iw);
                    TerrainInfo(in c, ref iw);
                    break;
                case CommandKind.RemoveRoad:
                    RemoveRoadWarnings(w, in c, flags, ref r, ref iw);
                    break;
                case CommandKind.UpgradeRoad:
                    UpgradeRoadWarnings(w, in c, ref r, ref iw);
                    break;
                case CommandKind.SetTurnBan:
                case CommandKind.SetNodeControl:
                    NodeWarnings(w, in c, ref iw);
                    break;
            }
        }

        r.IssueCount = iw.Total;
        r.IssuesWritten = iw.Written;
        return v;
    }

    // ---------------------------------------------------------------- PaintZone (UI-C1)

    /// <summary>
    /// Превью <c>PaintZone</c> (ui-spec.md §5.2): вердикт и счётчики дают те же <c>Zoning.Check</c> и <c>Zoning.Scan</c>, что у применения. Стоимости нет (зонирование бесплатно).
    /// Подсветка по участкам - <see cref="PreviewMarks"/>.
    /// </summary>
    private static RejectReason RunPaintZone(World w, in Command cmd, ref PreviewResult r, Span<PreviewIssue> issues)
    {
        var iw = new IssueWriter(issues);
        RejectReason v = Zoning.Check(w, in cmd.PaintZone, out ZonePlan plan);
        int lockMs = -1;
        if (v is not (RejectReason.InvalidArgument or RejectReason.NotSupported))
        {
            RejectReason lk = Progression.Check(w, in cmd, out lockMs);   // PG3: замок зонирования - тем же вызовом, что в применении
            v = lk != RejectReason.None ? lk : v;
        }

        r.Verdict = v;
        PaintZoneCmd p = cmd.PaintZone;
        float cx = (p.X0 + p.X1) * 0.5f;
        float cz = (p.Z0 + p.Z1) * 0.5f;
        if (v != RejectReason.None)
        {
            iw.Add((IssueCode)(int)v, IssueSeverity.Error, EntityKind.None, -1, 0, cx, cz, v == RejectReason.Locked ? lockMs : 0);
        }
        else
        {
            Zoning.Scan(w, w.City!, in p, ref plan, apply: false, default, out _);
            r.AX = plan.X0;
            r.AZ = plan.Z0;
            r.BX = plan.X1;
            r.BZ = plan.Z1;
            r.ZoneLots = plan.Lots;
            r.ZoneLotsChanged = plan.Changed;
            r.ZoneCells = plan.Cells;
            r.ZoneAbandon = plan.Abandon;
            r.ZoneUnavailableCells = Math.Max(0, plan.RectCells - plan.Cells);
            if (plan.Lots == 0)
            {
                iw.Add(IssueCode.ZoneNothingToPaint, IssueSeverity.Info, EntityKind.None, -1, 0, cx, cz);
            }

            if (plan.Abandon > 0)
            {
                iw.Add(IssueCode.ZoneBuildingsAbandoned, IssueSeverity.Warning, EntityKind.None, -1, 0, cx, cz, plan.Abandon);
            }

            if (Zoning.BuildInverse(w, in p, default) > InverseLog.MaxPerRecord)
            {
                iw.Add(IssueCode.UndoUnavailable, IssueSeverity.Warning, EntityKind.None, -1, 0, cx, cz);
            }
        }

        r.IssueCount = iw.Total;
        r.IssuesWritten = iw.Written;
        return v;
    }

    /// <summary>Подсветка превью <c>PaintZone</c> по участкам; возвращает, сколько участков под прямоугольником (записано не больше <c>marks.Length</c>). Отказ - 0.</summary>
    public static int PreviewMarks(World w, in PaintZoneCmd p, Span<PaintLotMark> marks)
    {
        if (Zoning.Check(w, in p, out ZonePlan plan) != RejectReason.None)
        {
            return 0;
        }

        Zoning.Scan(w, w.City!, in p, ref plan, apply: false, marks, out int total);
        return total;
    }

    // ---------------------------------------------------------------- BuildRoad

    // Рельеф (TR3): объём земляных работ (Info, м³) и автоматический мост (Info, длина, м).
    private static void TerrainInfo(in ToolCheck c, ref IssueWriter iw)
    {
        float cx = (c.AX + c.BX) * 0.5f;
        float cz = (c.AZ + c.BZ) * 0.5f;
        if (c.EarthworkM3 > 0)
        {
            iw.Add(IssueCode.Earthwork, IssueSeverity.Info, EntityKind.None, -1, 0, cx, cz, (int)Math.Min(c.EarthworkM3, int.MaxValue));
        }

        if (c.BridgeAuto)
        {
            iw.Add(IssueCode.BridgeAuto, IssueSeverity.Info, EntityKind.None, -1, 0, cx, cz, DetMath.ToIntSat(MathF.Round(c.LengthM)));
        }
    }

    private static void BuildRoadWarnings(World w, in Command cmd, in ToolCheck c, ref PreviewResult r, ref IssueWriter iw)
    {
        if (c.Reopen)
        {
            return;   // отмена сноса: сегмент уже в сети, связность не меняется
        }

        NetworkStore n = w.Net;
        BuildRoadCmd b = cmd.BuildRoad;
        r.NewNodes = c.NewNodes;
        NearCapacity(n.NodePool.AliveCount + c.NewNodes, n.NodePool.Capacity, ref iw);
        NearCapacity(n.SegmentPool.AliveCount + 1, n.SegmentPool.Capacity, ref iw);

        if (c.Profile >= 0)
        {
            ProfileBuildWarnings(w, in c, ref iw);
        }

        Connectivity cn = ConnOf(w);
        cn.EnsureBase();
        bool oneWay = b.LanesAB == 0 || b.LanesBA == 0;
        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? c.NodeA : c.NodeB;
            float x = e == 0 ? c.AX : c.BX;
            float z = e == 0 ? c.AZ : c.BZ;
            if (node >= 0)
            {
                // Существующий узел вне эталонной компоненты: новая дорога подключает «остров».
                if (cn.RefBase >= 0 && n.IncHead[node] >= 0 && !NodeInComp(n, cn, node, cn.RefBase))
                {
                    iw.Add(IssueCode.DeadEnd, IssueSeverity.Info, EntityKind.Node, node, n.Nodes[node].Generation, x, z);
                }

                if (n.Nodes[node].Signal >= 0)
                {
                    iw.Add(IssueCode.SignalPlanReset, IssueSeverity.Info, EntityKind.Node, node, n.Nodes[node].Generation, x, z);
                }
            }
            else
            {
                // Новый свободный конец: тупик; односторонний, начинающийся в изолированном узле - в него никто не въедет.
                bool origin = oneWay && (b.LanesAB > 0 ? e == 0 : e == 1);
                iw.Add(IssueCode.DeadEnd, origin ? IssueSeverity.Warning : IssueSeverity.Info, EntityKind.None, -1, 0, x, z);
            }
        }
    }

    // ---------------------------------------------------------------- профили (RP7)

    // Дорога по профилю: отступы соседних рукавов в существующих узлах концов (SetbackShort) и участки, которые она накроет (ZoneLotsLost).
    private static void ProfileBuildWarnings(World w, in ToolCheck c, ref IssueWriter iw)
    {
        NetworkStore n = w.Net;
        float half = n.Profiles!.HalfRoadM(c.Profile);
        var pa = new Vec2(c.AX, c.AZ);
        var pb = new Vec2(c.BX, c.BZ);
        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? c.NodeA : c.NodeB;
            if (node >= 0 && n.Nodes[node].FirstArm >= 0)
            {
                SetbackWarnings(w, node, e == 0 ? Unit(pa, pb) : Unit(pb, pa), half, -1, 1, ref iw);
            }
        }

        Span<Vec2> axis = stackalloc Vec2[2];
        axis[0] = pa;
        axis[1] = pb;
        LostLotsWarning(w, axis, n.Profiles.EdgeMm[c.Profile] * 0.001f, -1, 0, c.AX, c.AZ, ref iw);
    }

    // ADR-0028: рукава уже стоящих в узле дорог не перестраиваются. Если дорога с полушириной newHalf пересекает рукав и его отступ меньше newHalf + corner_radius, стоп-линия рукава
    // попадёт на скругление: SetbackShort, Value - нехватка, мм. skipSeg - сам правимый сегмент; extraArms - сколько рукавов добавит команда (1 у BuildRoad).
    private static void SetbackWarnings(World w, int node, Vec2 newDir, float newHalf, int skipSeg, int extraArms, ref IssueWriter iw)
    {
        NetworkStore n = w.Net;
        float cr = w.Builder.Options.CornerRadius;
        if (!(cr > 0f))
        {
            return;
        }

        int degree = extraArms;
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            degree++;
        }

        if (degree < 3)
        {
            return;
        }

        Vec2 np = n.Nodes[node].Position;
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            int seg = code >> 1;
            if (seg == skipSeg)
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[seg];
            int p0 = sd.FirstPoint;
            Vec2 first = (code & 1) == 0 ? n.SegmentPoints[p0] : n.SegmentPoints[p0 + sd.PointCount - 1];
            Vec2 arm = Unit(np, first);
            if (((newDir.X * arm.X) + (newDir.Z * arm.Z)) <= -NetworkBuilder.StraightCos)
            {
                continue;   // продолжение «прямо»: поперечной дороги для этого рукава нет
            }

            float have = Dist(np, first);
            float need = newHalf + cr;
            if (have < need - 0.01f)
            {
                iw.Add(IssueCode.SetbackShort, IssueSeverity.Warning, EntityKind.Segment, seg, sd.Generation, first.X, first.Z, (int)MathF.Ceiling((need - have) * 1000f));
            }
        }
    }

    // Участки на сетке, которые накроет дорога: Value - число участков.
    private static void LostLotsWarning(World w, scoped ReadOnlySpan<Vec2> axis, float edgeM, int seg, int gen, float x, float z, ref IssueWriter iw)
    {
        if (w.City == null || !w.City.LotGen.GridMode)
        {
            return;
        }

        int lost = w.City.LotGen.PreviewLost(w, w.City, axis, edgeM, out _);
        if (lost > 0)
        {
            iw.Add(IssueCode.ZoneLotsLost, IssueSeverity.Warning, seg >= 0 ? EntityKind.Segment : EntityKind.None, seg, gen, x, z, lost);
        }
    }

    private static void UpgradeRoadWarnings(World w, in ToolCheck c, ref PreviewResult r, ref IssueWriter iw)
    {
        if (c.Reshape == ReshapeMode.None || c.Reshape == ReshapeMode.Speed)
        {
            return;
        }

        NetworkStore n = w.Net;
        int seg = c.Seg;
        ref readonly SegmentData sd = ref n.Segments[seg];
        r.NewLanes = c.Reshape == ReshapeMode.Swap ? c.LanesAB + c.LanesBA : 0;
        r.DrainVehicles = c.Drain;
        if (c.Drain > 0)
        {
            // T5: занятый сегмент - правка после опустения убираемых полос и исчезающих connector'ов (операция ShrinkLanes).
            iw.Add(IssueCode.DrainNeeded, IssueSeverity.Warning, EntityKind.Segment, seg, sd.Generation, c.IssueX, c.IssueZ, c.Drain);
        }

        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? sd.NodeA : sd.NodeB;
            ref readonly NodeData nd = ref n.Nodes[node];
            if (nd.Signal >= 0)
            {
                iw.Add(IssueCode.SignalPlanReset, IssueSeverity.Info, EntityKind.Node, node, nd.Generation, nd.Position.X, nd.Position.Z);
            }

            if (c.Profile >= 0 && nd.FirstArm >= 0)
            {
                Vec2 toward = n.SegmentPoints[e == 0 ? sd.FirstPoint : sd.FirstPoint + sd.PointCount - 1];
                SetbackWarnings(w, node, Unit(nd.Position, toward), n.Profiles!.HalfRoadM(c.Profile), seg, 0, ref iw);
            }
        }

        if (c.Profile >= 0)
        {
            Span<Vec2> axis = stackalloc Vec2[NetworkBuilder.MaxVia + 2];
            axis = axis[..sd.PointCount];
            for (int i = 0; i < axis.Length; i++)
            {
                axis[i] = n.SegmentPoints[sd.FirstPoint + i];
            }

            LostLotsWarning(w, axis, n.Profiles!.EdgeMm[c.Profile] * 0.001f, seg, sd.Generation, c.IssueX, c.IssueZ, ref iw);
        }
    }

    private static Vec2 Unit(Vec2 from, Vec2 to)
    {
        float dx = to.X - from.X;
        float dz = to.Z - from.Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        return len > 0f ? new Vec2(dx / len, dz / len) : default;
    }

    private static float Dist(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    // ---------------------------------------------------------------- RemoveRoad

    private static void RemoveRoadWarnings(World w, in ToolCheck c, PreviewOptions flags, ref PreviewResult r, ref IssueWriter iw)
    {
        NetworkStore n = w.Net;
        int seg = c.Seg;
        ref readonly SegmentData sd = ref n.Segments[seg];
        float mx = c.IssueX;
        float mz = c.IssueZ;
        int drain = ToolOpsPhase.CountOn(w, seg);
        r.DrainVehicles = drain;
        if (drain > 0)
        {
            iw.Add(IssueCode.DrainNeeded, IssueSeverity.Warning, EntityKind.Segment, seg, sd.Generation, mx, mz, drain);
        }

        for (int e = 0; e < 2; e++)
        {
            int node = e == 0 ? sd.NodeA : sd.NodeB;
            if (n.Nodes[node].Signal >= 0)
            {
                iw.Add(IssueCode.SignalPlanReset, IssueSeverity.Info, EntityKind.Node, node, n.Nodes[node].Generation, n.Nodes[node].Position.X, n.Nodes[node].Position.Z);
            }
        }

        if (w.City != null)
        {
            // Z6: участки вдоль сегмента станут Lost, их здания - заброшенными (ADR-0011 §8.4). Value - число зданий.
            LotStore l = w.City.Lots;
            int buildings = 0;
            for (int lot = 0; lot < l.Count; lot++)
            {
                if (l.Seg[lot] == seg && l.SegGen[lot] == sd.Generation && (l.Flags[lot] & LotFlags.Lost) == 0 && l.Building[lot] >= 0)
                {
                    buildings++;
                }
            }

            if (buildings > 0)
            {
                iw.Add(IssueCode.ZoneLotsLost, IssueSeverity.Warning, EntityKind.Segment, seg, sd.Generation, mx, mz, buildings);
            }
        }

        if ((flags & PreviewOptions.Detail) != 0)
        {
            r.RoutesThrough = CountRoutesThrough(w, seg);
            if (r.RoutesThrough > 0)
            {
                iw.Add(IssueCode.RoutesAffected, IssueSeverity.Info, EntityKind.Segment, seg, sd.Generation, mx, mz, r.RoutesThrough);
            }
        }

        // Связность (Tarjan на scratch без сегмента): зоны и остановки, выпавшие из эталонной компоненты.
        Connectivity cn = ConnOf(w);
        cn.EnsureBase();
        if (cn.RefBase < 0)
        {
            return;
        }

        cn.RunWithout(seg);
        for (int z = 0; z < n.ZoneCount; z++)
        {
            ref readonly ZoneData zd = ref n.Zones[z];
            bool before = false;
            bool after = false;
            for (int k = 0; k < zd.SpawnLaneCount; k++)
            {
                int u = cn.DirSegOf(n.ZoneLanes[zd.FirstSpawnLane + k]);
                if (u < 0)
                {
                    continue;
                }

                before |= cn.CompBase[u] == cn.RefBase;
                after |= cn.RefAlt >= 0 && cn.CompAlt[u] == cn.RefAlt;
            }

            if (before && !after)
            {
                r.ZonesLosingAccess++;
                iw.Add(IssueCode.ZoneAccessLost, IssueSeverity.Warning, EntityKind.Zone, z, 0, zd.Center.X, zd.Center.Z);
            }
        }

        for (int s = 0; s < n.BusStopCount; s++)
        {
            if (!n.BusStopAlive(s) || n.BusStops[s].Lane < 0)
            {
                continue;
            }

            int u = cn.DirSegOf(n.BusStops[s].Lane);
            if (u >= 0 && cn.CompBase[u] == cn.RefBase && !(cn.RefAlt >= 0 && cn.CompAlt[u] == cn.RefAlt))
            {
                Vec2 p = n.Nodes[sd.NodeA].Position;
                iw.Add(IssueCode.StopAccessLost, IssueSeverity.Warning, EntityKind.BusStop, s, n.BusStops[s].Generation, p.X, p.Z);
            }
        }
    }

    // Окна маршрутов машин (от текущего шага) с полосой сегмента; планы за окном не считаются (O(машин x окно)).
    private static int CountRoutesThrough(World w, int seg)
    {
        NetworkStore n = w.Net;
        Agents.VehicleStore v = w.Vehicles;
        Routing.RouteStore rs = w.Routes;
        int count = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            int slot = v.Route[i];
            if (slot < 0 || !v.Pool.IsAlive(i) || !rs.IsAlive(new RouteHandle(slot, v.RouteGen[i])))
            {
                continue;
            }

            int b = slot * rs.WindowSteps;
            int wc = rs.WinCount[slot];
            for (int k = Math.Max(0, v.RouteCursor[i] + 1); k < wc; k++)
            {
                int lane = rs.Window[b + k].Lane;
                if (n.Lanes[lane].Segment == seg)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static void NodeWarnings(World w, in ToolCheck c, ref IssueWriter iw)
    {
        if (c.Node >= 0 && w.Net.Nodes[c.Node].Signal >= 0)
        {
            ref readonly NodeData nd = ref w.Net.Nodes[c.Node];
            iw.Add(IssueCode.SignalPlanReset, IssueSeverity.Info, EntityKind.Node, c.Node, nd.Generation, nd.Position.X, nd.Position.Z);
        }
    }

    // ---------------------------------------------------------------- помощники

    private static Connectivity ConnOf(World w) => w.ToolScratch.Conn ??= new Connectivity(w.Net);

    private static void NearCapacity(int used, int capacity, ref IssueWriter iw)
    {
        if (capacity > 0 && (long)used * 100 >= (long)capacity * NearCapacityPct)
        {
            iw.Add(IssueCode.NearCapacity, IssueSeverity.Warning, EntityKind.None, -1, 0, 0f, 0f, (int)((long)used * 100 / capacity));
        }
    }

    // Есть ли у узла сегмент, любая вершина которого лежит в компоненте comp.
    private static bool NodeInComp(NetworkStore n, Connectivity cn, int node, int comp)
    {
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            int seg = code >> 1;
            if (cn.CompBase[seg * 2] == comp || cn.CompBase[(seg * 2) + 1] == comp)
            {
                return true;
            }
        }

        return false;
    }
}
