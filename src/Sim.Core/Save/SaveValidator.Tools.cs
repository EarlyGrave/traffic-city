using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Save;

// Сеть с дырами (4.3, T2): проверка пулов, арен и канонических значений мёртвых слотов (player-tools.md §3, §10).
internal static partial class SaveValidator
{
    internal static bool DeadNodeIsCanonical(in NodeData nd)
    {
        NodeData d = NetworkStore.DeadNode(nd.Generation);
        return nd.Position.X == 0f && nd.Position.Z == 0f && nd.Elevation == 0f && nd.Radius == 0f && nd.Control == NodeControl.None && nd.FirstArm == d.FirstArm &&
            nd.ArmCount == 0 && nd.FirstConnector == 0 && nd.ConnectorCount == 0 && nd.Signal == d.Signal && nd.Zone == d.Zone && nd.Flags == 0 && nd.TurnBan == 0;
    }

    internal static bool DeadSegmentIsCanonical(in SegmentData sd) =>
        sd.NodeA == -1 && sd.NodeB == -1 && sd.Length == 0f && sd.SpeedLimit == 0f && sd.Kind == RoadKind.Street && sd.LanesAB == 0 && sd.LanesBA == 0 && sd.Flags == 0 &&
        sd.FirstLaneAB == -1 && sd.FirstLaneBA == -1 && sd.FirstPoint == 0 && sd.PointCount == 0 && sd.Cx == 0f && sd.Cz == 0f;

    internal static bool DeadBusStopIsCanonical(in BusStopData s) =>
        s.Lane == -1 && s.S == 0f && s.Kind == 0 && s.Route == -1 && s.Waiting == 0 && s.BayUsed == 0 && s.BayFlags == 0 && s.LastArrivalTick == -1 && s.LastDepartTick == -1 && s.BayExitFrom == -1;

    internal static bool DeadBusRouteIsCanonical(in BusRouteData r) =>
        r.FirstStop == 0 && r.StopCount == 0 && r.HeadwayTicks == 0 && r.VehicleType == 0 && r.ActiveBuses == 0 && r.LastDispatchTick == -1;

    /// <summary>
    /// Пулы (поколения и free-list'ы), арены блоков и признак <c>ToolsTouched</c>. Сеть без правок обязана быть «плотной»: все слоты живы, дыр, потерь и битов лестницы нет,
    /// арены строк и конфликтов connector'ов не использованы (их задействует T3). Дыры арен - в пределах хвоста, без взаимных пересечений, каноническое содержимое.
    /// </summary>
    private static LoadStatus ValidateToolState(LoadContext c, World w, NetworkStore n)
    {
        LoadStatus st = ValidatePool(c, n.NodePool);
        if (st != LoadStatus.Ok)
        {
            return Bad(c, c.FailRow, 18);
        }

        st = ValidatePool(c, n.SegmentPool);
        if (st != LoadStatus.Ok)
        {
            return Bad(c, c.FailRow, 19);
        }

        st = ValidatePool(c, n.BusStopPool);
        if (st != LoadStatus.Ok)
        {
            return Bad(c, c.FailRow, 20);
        }

        st = ValidatePool(c, n.BusRoutePool);
        if (st != LoadStatus.Ok)
        {
            return Bad(c, c.FailRow, 21);
        }

        if (n.ToolsTouched > 1)
        {
            return Bad(c, 0, 13);
        }

        BlockArena[] arenas = { n.LaneArena, n.LanePointArena, n.SegmentPointArena, n.ArmArena, n.LaneNextArena, n.ZoneLaneArena, n.BusRouteStopArena };
        for (int i = 0; i < arenas.Length; i++)
        {
            st = ValidateArena(c, arenas[i], n.ToolsTouched != 0);
            if (st != LoadStatus.Ok)
            {
                return Bad(c, c.FailRow, (uint)(22 + (4 * i)));
            }
        }

        // Строки connector'ов и конфликты (T3): те же арены, что у сети; без правок - подряд без дыр.
        BlockArena[] cArenas = { w.Connectors.RowArena, w.Connectors.ConflictArena };
        for (int i = 0; i < cArenas.Length; i++)
        {
            st = ValidateArena(c, cArenas[i], n.ToolsTouched != 0);
            if (st != LoadStatus.Ok)
            {
                return Bad(c, c.FailRow, (uint)(18 + (4 * i)));
            }
        }

        st = ValidateOps(c, w, n);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        if (n.ToolsTouched == 0 && (n.AliveNodes != n.NodeCount || n.AliveSegments != n.SegmentCount || n.AliveBusStops != n.BusStopCount || n.AliveBusRoutes != n.BusRouteCount))
        {
            return Bad(c, 0, 13);   // дыры в сети, не отмеченной правками
        }

        // Содержимое дыр и запаса блоков - каноническое.
        if (!HolesCanonical(n.LaneArena, i => NetworkStore.IsDeadLane(in n.Lanes[i])) ||
            !HolesCanonical(n.LanePointArena, i => n.LanePoints[i].X == 0f && n.LanePoints[i].Z == 0f && n.LanePointS[i] == 0f) ||
            !HolesCanonical(n.SegmentPointArena, i => n.SegmentPoints[i].X == 0f && n.SegmentPoints[i].Z == 0f) ||
            !HolesCanonical(n.ArmArena, i => n.NodeArms[i] == 0 && n.ArmFlags[i] == 0 && n.ArmDir[i].X == 0f && n.ArmDir[i].Z == 0f) ||
            !HolesCanonical(n.LaneNextArena, i => n.LaneNext[i] == 0) ||
            !HolesCanonical(n.ZoneLaneArena, i => n.ZoneLanes[i] == 0) ||
            !HolesCanonical(n.BusRouteStopArena, i => n.BusRouteStops[i] == 0) ||
            !HolesCanonical(w.Connectors.RowArena, i => w.Connectors.DeadRowIsCanonical(i)) ||
            !HolesCanonical(w.Connectors.ConflictArena, i => w.Connectors.Conflicts[i].OtherLocal == 0 && w.Connectors.Conflicts[i].Flags == 0 && w.Connectors.Conflicts[i].SMineCm == 0 &&
                w.Connectors.Conflicts[i].SOtherCm == 0 && w.Connectors.Conflicts[i].Reserved == 0))
        {
            return Bad(c, 0, 22);
        }

        return LoadStatus.Ok;
    }

    /// <summary>
    /// Операции дренажа (T4): без правок их нет, <c>NextId = 1</c>; id строго растут и меньше <c>NextId</c>; цель - живой сегмент того же поколения в состоянии «закрывается»,
    /// по одной операции на сегмент; каждый закрывающийся сегмент имеет операцию.
    /// </summary>
    private static LoadStatus ValidateOps(LoadContext c, World w, NetworkStore n)
    {
        ToolOps ops = w.Ops;
        if (ops.Count < 0 || ops.Count > ops.Capacity || ops.NextId < 1 || (n.ToolsTouched == 0 && (ops.Count != 0 || ops.NextId != 1)))
        {
            return Bad(c, 0, 50);
        }

        int prev = 0;
        for (int k = 0; k < ops.Count; k++)
        {
            int seg = ops.TargetIndex[k];
            if (ops.Id[k] <= prev || ops.Id[k] >= ops.NextId || ops.Kind[k] != (byte)ToolOpKind.RemoveSegment || (ops.Flags[k] & ~ToolOpFlags.KeepNodes) != 0 ||
                !n.ResolveSegment(seg, ops.TargetGen[k]) || ops.TargetGen[k] == 0 || (n.Segments[seg].Flags & SegmentFlags.Closing) == 0 ||
                ops.FindSegment(seg) != k || ops.StartTick[k] > w.Tick || ops.LastProgressTick[k] < ops.StartTick[k] || ops.LastProgressTick[k] > w.Tick ||
                ops.LastStallTick[k] < ops.StartTick[k] || ops.LastStallTick[k] > w.Tick || ops.BestRemaining[k] < 0)
            {
                return Bad(c, k, 51);
            }

            prev = ops.Id[k];
        }

        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (n.SegmentAlive(s) && (n.Segments[s].Flags & SegmentFlags.Closing) != 0 && ops.FindSegment(s) < 0)
            {
                return Bad(c, s, 52);
            }
        }

        return LoadStatus.Ok;
    }

    private static bool HasLadderBits(BlockArena a)
    {
        for (int i = 0; i < a.LadderWordCount; i++)
        {
            if (a.LadderBits[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Дыры арены: внутри хвоста, не пересекаются; биты лестницы - только в пределах хвоста и не на дырах. Без правок - ничего этого нет.</summary>
    private static LoadStatus ValidateArena(LoadContext c, BlockArena a, bool touched)
    {
        if (a.Tail < 0 || a.Tail > a.Length)
        {
            return Bad(c, a.Tail, 0, LoadStatus.CapacityExceeded);
        }

        if (!touched)
        {
            return a.FreeBlockCount != 0 || a.LeakedElements != 0 || HasLadderBits(a) ? Bad(c, a.FreeBlockCount) : LoadStatus.Ok;
        }

        // Слова за хвостом - нули (хвост кончается внутри слова: старшие биты слова тоже нули).
        int words = a.LadderWordCount;
        if ((a.Tail & 63) != 0 && words > 0 && (a.LadderBits[words - 1] >> (a.Tail & 63)) != 0)
        {
            return Bad(c, words);
        }

        var packed = new ulong[a.FreeBlockCount];
        a.CopyFree(packed);
        var mark = new ulong[(a.Tail + 63) >> 6];
        for (int k = 0; k < packed.Length; k++)
        {
            int cls = (int)(packed[k] >> 32);
            int first = (int)(uint)packed[k];
            int cap = BlockArena.Steps[cls];
            if (a.IsLadder(first))
            {
                return Bad(c, k);   // дыра не может быть началом живого блока лестницы
            }

            for (int e = first; e < first + cap; e++)
            {
                ulong bit = 1UL << (e & 63);
                if ((mark[e >> 6] & bit) != 0)
                {
                    return Bad(c, k);
                }

                mark[e >> 6] |= bit;
            }
        }

        return LoadStatus.Ok;
    }

    /// <summary>Элементы всех дыр арены удовлетворяют <paramref name="canonical"/>.</summary>
    private static bool HolesCanonical(BlockArena a, Func<int, bool> canonical)
    {
        if (a.FreeBlockCount == 0)
        {
            return true;
        }

        var packed = new ulong[a.FreeBlockCount];
        a.CopyFree(packed);
        for (int k = 0; k < packed.Length; k++)
        {
            int first = (int)(uint)packed[k];
            int cap = BlockArena.Steps[(int)(packed[k] >> 32)];
            for (int e = first; e < first + cap; e++)
            {
                if (!canonical(e))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Диапазон ссылок всех столбцов с тегом <see cref="RefKind"/> (<c>SaveSchema.Refs</c>): значение в <c>[0, граница вида)</c>, либо -1 у допускающих «нет».
    /// Только диапазон: живость целей проверяют владельцы диапазонов (в дырах арен лежат канонические нули, они не ссылки). Нарушение - <see cref="LoadStatus.InvalidReference"/>,
    /// <c>FailedColumn</c> - id столбца, <c>FailedRow</c> - строка.
    /// </summary>
    public static LoadStatus ValidateRefs(LoadContext c)
    {
        World w = c.W;
        NetworkStore n = w.Net;
        foreach (BlockDef b in SaveSchema.PresentBlocks(w))
        {
            foreach (ColumnDef col in b.Columns)
            {
                if (col.Ref == RefKind.None || col.Type != ColumnType.I32)
                {
                    continue;
                }

                long count = col.Count(w);
                if (count <= 0 || count > int.MaxValue)
                {
                    continue;
                }

                long limit = col.Ref switch
                {
                    RefKind.Lane => n.LaneCount,
                    RefKind.Connector => w.Connectors.Count,
                    RefKind.Segment => n.SegmentCount,
                    RefKind.DirSeg => 2L * n.SegmentCount,
                    RefKind.Node => n.NodeCount,
                    RefKind.Stop => n.BusStopCount,
                    _ => 0,
                };
                var values = new int[count];
                col.Copy(w, 0, (int)count, MemoryMarshal.AsBytes(values.AsSpan()));
                int min = col.RefNullable ? -1 : 0;
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] < min || values[i] >= limit)
                    {
                        c.FailBlock = b.Id;
                        return Bad(c, i, col.Id, LoadStatus.InvalidReference);
                    }
                }
            }
        }

        return LoadStatus.Ok;
    }
}
