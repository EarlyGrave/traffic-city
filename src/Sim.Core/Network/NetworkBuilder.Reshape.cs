using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>Что делает <see cref="NetworkBuilder.ReshapeSegment"/> с сегментом (RP7, <c>UpgradeRoad</c>).</summary>
internal enum ReshapeMode : byte
{
    /// <summary>Ничего не изменилось.</summary>
    None = 0,

    /// <summary>Только скорость: поля сегмента и его полос, узлы не перестраиваются.</summary>
    Speed = 1,

    /// <summary>Те же полосы (число и индексы), другая геометрия (профиль, <c>Mirrored</c>) и/или тип: точки и ширины полос переписываются на месте, узлы концов перестраиваются.</summary>
    InPlace = 2,

    /// <summary>Другое число полос: новый блок полос, перенос ссылок (<see cref="Relocate"/>), перестройка узлов концов, старый блок освобождается. Сегмент и его концы обязаны быть пустыми.</summary>
    Swap = 3,
}

/// <summary>
/// Правка полос существующего сегмента (RP7, player-tools.md §4.5, road-profiles.md §6): ось (<c>SegmentPoints</c>, <c>Length</c>, отступы концов) не меняется - профиль двигает только боковые смещения и ширины
/// полос (ADR-0028: отступы рукавов не перестраиваются, у соседей это <c>SetbackShort</c> в превью). Handle сегмента сохраняется.
/// </summary>
internal sealed partial class NetworkBuilder
{
    /// <summary>
    /// Определяет, что нужно сделать, чтобы сегмент <paramref name="seg"/> стал таким: <paramref name="profile"/> (-1 - без профиля), <paramref name="mirrored"/>, полосы, тип и скорость.
    /// Чистая функция состояния; её же зовёт валидатор.
    /// </summary>
    internal ReshapeMode PlanReshape(int seg, int profile, bool mirrored, int lanesAB, int lanesBA, float speed, RoadKind kind)
    {
        ref readonly SegmentData sd = ref _s.Segments[seg];
        bool oldMirrored = (sd.Flags & SegmentFlags.Mirrored) != 0;
        if (sd.LanesAB != lanesAB || sd.LanesBA != lanesBA)
        {
            return ReshapeMode.Swap;
        }

        if (sd.Profile != profile || (profile >= 0 && oldMirrored != mirrored) || sd.Kind != kind)
        {
            return ReshapeMode.InPlace;
        }

        return sd.SpeedLimit != speed ? ReshapeMode.Speed : ReshapeMode.None;
    }

    /// <summary>
    /// Применяет правку (предусловия проверил валидатор: профиль и полосы согласованы, ёмкости хватает, для <see cref="ReshapeMode.Swap"/> сегмент и connector'ы его концов пусты).
    /// Узлы концов перестраиваются здесь; пересчёт производных мира (граф, оверлей, пешеходы, участки) - у вызывающего. Возвращает статус перестройки узла (не-Ok - ёмкость: мир остаётся согласованным по структуре).
    /// </summary>
    internal NetworkStatus ReshapeSegment(World w, int seg, ReshapeMode mode, int profile, bool mirrored, int lanesAB, int lanesBA, float speed, RoadKind kind, uint tag)
    {
        NetworkStore n = _s;
        if (mode == ReshapeMode.None)
        {
            return NetworkStatus.Ok;
        }

        n.ToolsTouched = 1;
        ref SegmentData sd = ref n.Segments[seg];
        if (mode == ReshapeMode.Speed)
        {
            sd.SpeedLimit = speed;
            for (int k = 0; k < sd.LanesAB; k++)
            {
                n.Lanes[sd.FirstLaneAB + k].SpeedLimit = speed;
            }

            for (int k = 0; k < sd.LanesBA; k++)
            {
                n.Lanes[sd.FirstLaneBA + k].SpeedLimit = speed;
            }

            n.Version++;
            return NetworkStatus.Ok;
        }

        int m = sd.PointCount;
        for (int i = 0; i < m; i++)
        {
            _axis[i] = n.SegmentPoints[sd.FirstPoint + i];
        }

        // Параметр s вдоль оси берём у существующей полосы (та же ось): не пересчитываем, чтобы не плодить расхождений в последнем бите.
        if (sd.LanesAB > 0)
        {
            int fp = n.Lanes[sd.FirstLaneAB].FirstPoint;
            for (int i = 0; i < m; i++)
            {
                _axisS[i] = n.LanePointS[fp + i];
            }
        }
        else
        {
            int fp = n.Lanes[sd.FirstLaneBA].FirstPoint;
            for (int i = 0; i < m; i++)
            {
                _axisS[i] = sd.Length - n.LanePointS[fp + (m - 1 - i)];
            }
        }

        ComputeMiters(m);
        RoadProfileStore? ps = profile >= 0 ? n.Profiles : null;
        float length = sd.Length;
        int total = lanesAB + lanesBA;
        float half = total * _o.LaneWidth * 0.5f;
        int oldAB = sd.LanesAB;
        int oldBA = sd.LanesBA;
        int oldTotal = oldAB + oldBA;
        int oldFirst = oldAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
        int oldPoint = n.Lanes[oldFirst].FirstPoint;
        int firstLane;
        int firstPoint;
        if (mode == ReshapeMode.InPlace)
        {
            firstLane = oldFirst;
            firstPoint = oldPoint;
        }
        else
        {
            if (!n.CanAllocBlock(n.LaneArena, total))
            {
                return NetworkStatus.LanesFull;
            }

            if (!n.CanAllocBlock(n.LanePointArena, total * m))
            {
                return NetworkStatus.PointsFull;
            }

            firstLane = n.AllocLaneBlock(total);
            firstPoint = n.AllocBlock(n.LanePointArena, total * m);
        }

        for (int dir = 0; dir < 2; dir++)
        {
            int cnt = dir == 0 ? lanesAB : lanesBA;
            int dirFirst = dir == 0 ? 0 : lanesAB;
            for (int k = 0; k < cnt; k++)
            {
                float x = ps != null
                    ? ps.LaneCenterM(profile, mirrored, dir, k)
                    : (dir == 0 ? half - ((k + 0.5f) * _o.LaneWidth) : -(half - ((k + 0.5f) * _o.LaneWidth)));
                float lw = ps != null ? ps.LaneWidthM(profile, mirrored, dir, k) : _o.LaneWidth;
                int lane = firstLane + dirFirst + k;
                int pts = firstPoint + ((dirFirst + k) * m);
                if (mode == ReshapeMode.InPlace)
                {
                    RewriteLane(lane, pts, dir, x, lw, m, speed);
                }
                else
                {
                    EmitLane(lane, pts, seg, k, dir, cnt, x, lw, m, length, speed);
                    int prev = dir == 0 ? (k < oldAB ? sd.FirstLaneAB + k : -1) : (k < oldBA ? sd.FirstLaneBA + k : -1);
                    if (prev >= 0)
                    {
                        n.Lanes[lane].Flags = (byte)(n.Lanes[prev].Flags & (LaneFlags.BusOnly | LaneFlags.NoLaneChangeOut));
                    }
                }
            }
        }

        // Заголовок сегмента: профиль, зеркальность, тип, скорость (индексы полос - после переноса).
        byte flags = (byte)(sd.Flags & ~SegmentFlags.Mirrored);
        if (ps != null && mirrored)
        {
            flags |= SegmentFlags.Mirrored;
        }

        if (mode == ReshapeMode.Swap)
        {
            var map = LaneRemap;
            map.Clear();
            RowRemap.Clear();

            // Переносятся только уцелевшие полосы (номер в направлении сохраняется). Исчезающие остаются на месте до перестройки узлов: на них ещё ссылаются connector'ы, а ссылки
            // машин, поездок, зон и маршрутов уже сняты (RetargetVanishing, PruneZoneLanes, AfterClosing).
            for (int k = 0; k < Math.Min(oldAB, lanesAB); k++)
            {
                map.Add(sd.FirstLaneAB + k, firstLane + k);
            }

            for (int k = 0; k < Math.Min(oldBA, lanesBA); k++)
            {
                map.Add(sd.FirstLaneBA + k, firstLane + lanesAB + k);
            }

            var shape = new VanishShape(sd.FirstLaneAB, oldAB, lanesAB, sd.FirstLaneBA, oldBA, lanesBA);
            PruneZoneLanes(n, in shape);
            RetargetVanishing(w, in shape);

            // Маршруты, проходящие через сегмент (окна и планы), и кэш первого хода освобождаются, как при закрытии: направление могло исчезнуть, номера полос - сместиться.
            byte segFlags = sd.Flags;
            sd.Flags |= SegmentFlags.Closing;
            for (int k = 0; k < oldTotal; k++)
            {
                n.Lanes[oldFirst + k].Flags |= LaneFlags.Closed;
            }

            SegmentClosure.AfterClosing(w);
            sd.Flags = segFlags;
            for (int k = 0; k < oldTotal; k++)
            {
                n.Lanes[oldFirst + k].Flags &= unchecked((byte)~LaneFlags.Closed);
            }

            Relocate.Apply(w, map, RowRemap);

            // Старые уцелевшие полосы живы до освобождения блока, но их списки выездов (LaneNext блока узла) уже принадлежат новым полосам: перестройка первого узла освободит блок,
            // а перенос второго иначе переписал бы его дыру по устаревшему FirstNext (T5: ловил фаззер - неканоническая дыра LaneNext в сейве).
            for (int k = 0; k < map.Count; k++)
            {
                n.Lanes[map.Old[k]].FirstNext = 0;
                n.Lanes[map.Old[k]].NextCount = 0;
            }

            // OutReserved переехал вместе с полосой, а список «затронутых в прошлом тике» ворот хранит старые индексы: фаза 4 не обнулила бы новую полосу. Состояние всё равно пересобирается
            // с нуля в начале следующей фазы 4 (допущенные машины), поэтому сбрасываем его целиком по списку и на новых полосах.
            GateScratch gate = w.Gate;
            for (int k = 0; k < gate.OutTouchedCount; k++)
            {
                n.OutReserved[gate.OutTouched[k]] = 0f;
            }

            gate.OutTouchedCount = 0;
            for (int k = 0; k < total; k++)
            {
                n.OutReserved[firstLane + k] = 0f;
            }

            sd.FirstLaneAB = lanesAB > 0 ? firstLane : -1;
            sd.FirstLaneBA = lanesBA > 0 ? firstLane + lanesAB : -1;
            sd.LanesAB = (byte)lanesAB;
            sd.LanesBA = (byte)lanesBA;
        }

        sd.Kind = kind;
        sd.SpeedLimit = speed;
        sd.Flags = flags;
        sd.Profile = ps != null ? profile : -1;
        n.Version++;

        // Узлы концов: connector'ы и геометрия поворотов заново (в порядке возрастания индекса узла, как раскладка построенной сети).
        int lo = Math.Min(sd.NodeA, sd.NodeB);
        int hi = Math.Max(sd.NodeA, sd.NodeB);
        NetworkStatus st = NetworkStatus.Ok;
        for (int e = 0; e < 2 && st == NetworkStatus.Ok; e++)
        {
            int node = e == 0 ? lo : hi;
            if (n.Nodes[node].FirstArm >= 0)
            {
                st = NodeEdit.Rebuild(w, node, rejectTrap: false, tag);
            }
        }

        if (mode == ReshapeMode.Swap && st == NetworkStatus.Ok)
        {
            for (int k = 0; k < oldTotal; k++)
            {
                w.LaneSignals[oldFirst + k] = 0;
            }

            n.FreeLaneBlock(oldFirst, oldTotal);
            n.FreePointBlock(oldPoint, oldTotal * m);
        }

        w.Gate.FullScan = true;
        w.Vehicles.MarkAllRouteDirty();
        return st;
    }

    // Индексы «нового блока» для пробного плана перестройки узлов (T5): за пределами любых настоящих полос, только сравниваются.
    private const int FakeLaneBase = 1 << 29;

    /// <summary>
    /// Пробная перестройка узлов концов сегмента под новые полосы и тип (T5, правка занятого сегмента): сколько занятых машинами connector'ов исчезнет. Сопоставление старых connector'ов
    /// с планом - как после переноса в <see cref="ReshapeSegment"/> (уцелевшая полоса - тот же номер в направлении, исчезающая не совпадает ни с чем), поэтому 0 здесь означает, что
    /// <c>ReshapeSegment</c> перестроит узлы без <c>Busy</c>. <paramref name="close"/> - закрыть обречённые connector'ы для въезда (кроме выездов с убираемых полос: по ним уезжают
    /// оставшиеся на них машины). Не-Ok - лимит рукавов/connector'ов или ёмкость арен (ждать нельзя - ответ тот же). Хранилище не меняется, кроме флагов <c>Closed</c> при <paramref name="close"/>.
    /// </summary>
    internal NetworkStatus PlanReshapeEnds(World w, int seg, int lanesAB, int lanesBA, RoadKind kind, bool close, out int busy)
    {
        busy = 0;
        NetworkStore n = _s;
        ref SegmentData sd = ref n.Segments[seg];
        SegmentData saved = sd;
        var shape = new VanishShape(saved.FirstLaneAB, saved.LanesAB, lanesAB, saved.FirstLaneBA, saved.LanesBA, lanesBA);
        bool closed = false;
        NetworkStatus result = NetworkStatus.Ok;
        int lo = Math.Min(saved.NodeA, saved.NodeB);
        int hi = Math.Max(saved.NodeA, saved.NodeB);
        for (int e = 0; e < 2 && result == NetworkStatus.Ok; e++)
        {
            int node = e == 0 ? lo : hi;
            if ((e == 1 && hi == lo) || n.Nodes[node].FirstArm < 0)
            {
                continue;
            }

            sd.FirstLaneAB = lanesAB > 0 ? FakeLaneBase : -1;
            sd.FirstLaneBA = lanesBA > 0 ? FakeLaneBase + lanesAB : -1;
            sd.LanesAB = (byte)lanesAB;
            sd.LanesBA = (byte)lanesBA;
            sd.Kind = kind;
            NetworkStatus st = PlanRebuild(w, node, rejectTrap: false, out RebuildPlan rp);
            sd = saved;
            if (st != NetworkStatus.Ok && st != NetworkStatus.Busy)
            {
                result = st;
                break;
            }

            if (!PlanFits(w, in rp))
            {
                result = NetworkStatus.LanesFull;
                break;
            }

            for (int q = 0; q < rp.OConnN; q++)
            {
                _oldKept[q] = false;
            }

            for (int p = 0; p < rp.Plan; p++)
            {
                for (int q = 0; q < rp.OConnN; q++)
                {
                    if (!_oldKept[q] && FakeMap(in saved, lanesAB, lanesBA, _oldIn[q]) == _planIn[p] && FakeMap(in saved, lanesAB, lanesBA, _oldOut[q]) == _planOut[p])
                    {
                        _oldKept[q] = true;
                        break;
                    }
                }
            }

            for (int q = 0; q < rp.OConnN; q++)
            {
                if (_oldKept[q])
                {
                    continue;
                }

                int c = rp.OConn + q;
                busy += w.Occupancy.LaneCount[c] > 0 ? 1 : 0;
                if (close && !shape.Vanishes(_oldIn[q]) && (n.Lanes[c].Flags & LaneFlags.Closed) == 0)
                {
                    n.Lanes[c].Flags |= LaneFlags.Closed;
                    closed = true;
                }
            }
        }

        if (closed)
        {
            n.ToolsTouched = 1;
            n.Version++;
            w.Gate.FullScan = true;
            SegmentClosure.AfterClosing(w);
        }

        return result;
    }

    // Индекс полосы так, как его увидит перестройка узла после переноса блока полос сегмента: уцелевшая - в пробный блок, исчезающая - ни с чем не совпадает.
    private static int FakeMap(in SegmentData sd, int lanesAB, int lanesBA, int lane)
    {
        if (sd.LanesAB > 0 && lane >= sd.FirstLaneAB && lane < sd.FirstLaneAB + sd.LanesAB)
        {
            int k = lane - sd.FirstLaneAB;
            return k < lanesAB ? FakeLaneBase + k : -2;
        }

        if (sd.LanesBA > 0 && lane >= sd.FirstLaneBA && lane < sd.FirstLaneBA + sd.LanesBA)
        {
            int k = lane - sd.FirstLaneBA;
            return k < lanesBA ? FakeLaneBase + lanesAB + k : -2;
        }

        return lane;
    }

    // Те же условия ёмкости, что в начале CommitRebuild.
    private bool PlanFits(World w, in RebuildPlan rp)
    {
        NetworkStore s = _s;
        int samples = _o.ConnectorSamples;
        return s.CanAllocBlock(s.ArmArena, rp.Arms) && s.CanAllocBlock(s.LaneArena, rp.Plan) && s.CanAllocBlock(s.LanePointArena, rp.Plan * samples) &&
            s.CanAllocBlock(s.LaneNextArena, 2 * rp.Plan) && (rp.Plan == 0 || s.CanAllocBlock(w.Connectors.RowArena, rp.Plan));
    }

    /// <summary>Хватит ли арен на новый блок полос и точек сегмента (<see cref="ReshapeMode.Swap"/>).</summary>
    internal bool SwapFits(int seg, int lanesAB, int lanesBA)
    {
        ref readonly SegmentData sd = ref _s.Segments[seg];
        int total = lanesAB + lanesBA;
        return _s.CanAllocBlock(_s.LaneArena, total) && _s.CanAllocBlock(_s.LanePointArena, total * sd.PointCount);
    }

    /// <summary>Машины (с ghost'ами) на полосах сегмента, которые исчезнут при новых числах полос.</summary>
    internal int VanishingOccupancy(World w, int seg, int lanesAB, int lanesBA)
    {
        ref readonly SegmentData sd = ref _s.Segments[seg];
        int count = 0;
        for (int k = lanesAB; k < sd.LanesAB; k++)
        {
            count += w.Occupancy.LaneCount[sd.FirstLaneAB + k];
        }

        for (int k = lanesBA; k < sd.LanesBA; k++)
        {
            count += w.Occupancy.LaneCount[sd.FirstLaneBA + k];
        }

        return count;
    }

    /// <summary>
    /// Начало дренажа полос (T5, <c>ShrinkLanes</c>): убираемые полосы - <c>Closed | Draining</c> (въезд запрещён, MOBIL уводит машины на оставшиеся), допуски на концах сброшены,
    /// назначения на них заменены, маршруты через них освобождены. Пересчёт графа - у вызывающего.
    /// </summary>
    internal void BeginShrink(World w, int seg, int lanesAB, int lanesBA)
    {
        NetworkStore n = _s;
        ref readonly SegmentData sd = ref n.Segments[seg];
        n.ToolsTouched = 1;
        SetDraining(n, in sd, lanesAB, lanesBA, on: true);
        n.Version++;
        GatePhase.ResetNode(w, sd.NodeA);
        GatePhase.ResetNode(w, sd.NodeB);
        w.Gate.FullScan = true;
        var shape = new VanishShape(sd.FirstLaneAB, sd.LanesAB, lanesAB, sd.FirstLaneBA, sd.LanesBA, lanesBA);
        RetargetVanishing(w, in shape);
        SegmentClosure.AfterClosing(w);
    }

    /// <summary>Отмена дренажа полос: флаги убираемых полос и закрытых им connector'ов концов сняты (undo во время дренажа).</summary>
    internal void CancelShrink(World w, int seg, int lanesAB, int lanesBA)
    {
        NetworkStore n = _s;
        ref readonly SegmentData sd = ref n.Segments[seg];
        SetDraining(n, in sd, lanesAB, lanesBA, on: false);
        SegmentClosure.ReopenEnds(w, seg);
    }

    private static void SetDraining(NetworkStore n, in SegmentData sd, int lanesAB, int lanesBA, bool on)
    {
        const byte bits = LaneFlags.Closed | LaneFlags.Draining;
        for (int dir = 0; dir < 2; dir++)
        {
            int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            int from = dir == 0 ? lanesAB : lanesBA;
            int to = dir == 0 ? sd.LanesAB : sd.LanesBA;
            for (int k = from; k < to; k++)
            {
                if (on)
                {
                    n.Lanes[first + k].Flags |= bits;
                }
                else
                {
                    n.Lanes[first + k].Flags &= unchecked((byte)~bits);
                }
            }
        }
    }

    // Точки существующей полосы заново от оси: те же индексы, тот же параметр s; ширина и скорость.
    private void RewriteLane(int lane, int first, int direction, float x, float width, int m, float limit)
    {
        for (int i = 0; i < m; i++)
        {
            int src = direction == 0 ? i : m - 1 - i;
            _s.LanePoints[first + i] = _axis[src] + (_miter[src] * x);
        }

        _s.FillPieceHeadings(first, m);
        _s.Lanes[lane].Width = width;
        _s.Lanes[lane].SpeedLimit = limit;
    }

    // Полосы, которые исчезают (новый индекс < 0), уходят из списков зон; зона без полос получает ближайшую открытую (LaneNo 0, ничья - меньший индекс), как при сносе сегмента.
    private static void PruneZoneLanes(NetworkStore n, in VanishShape shape)
    {
        for (int z = 0; z < n.ZoneCount; z++)
        {
            ref ZoneData zd = ref n.Zones[z];
            int cnt = zd.SpawnLaneCount;
            int write = 0;
            for (int q = 0; q < cnt; q++)
            {
                int lane = n.ZoneLanes[zd.FirstSpawnLane + q];
                if (!shape.Vanishes(lane))
                {
                    n.ZoneLanes[zd.FirstSpawnLane + write++] = lane;
                }
            }

            if (write == cnt)
            {
                continue;
            }

            for (int q = write; q < cnt; q++)
            {
                n.ZoneLanes[zd.FirstSpawnLane + q] = 0;
            }

            zd.SpawnLaneCount = write;
            if (write == 0)
            {
                int nearest = NearestOpenLaneOutside(n, zd.Center, in shape);
                if (nearest >= 0)
                {
                    n.ZoneLanes[zd.FirstSpawnLane] = nearest;
                    zd.SpawnLaneCount = 1;
                }
            }
        }
    }

    // Назначения машин и поездок на исчезающих полосах (направление пропало или убрана старшая полоса): машина получает полосу прибытия своей зоны, поездка с явной полосой старта без зоны
    // снимается (как при закрытии сегмента, SegmentClosure). Выбор детерминирован (без PRNG). Вызывается до переноса: все индексы ещё старые.
    private static void RetargetVanishing(World w, in VanishShape shape)
    {
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            int d = v.DestLane[i];
            if (!v.Pool.IsAlive(i) || d < 0 || !shape.Vanishes(d))
            {
                continue;
            }

            int dest = -1;
            int zone = v.DestZone[i];
            if ((uint)zone < (uint)n.ZoneCount)
            {
                int cnt = n.OpenZoneLaneCount(zone);
                for (int t = 0; t < cnt && dest < 0; t++)
                {
                    int lane = n.OpenZoneLane(zone, (i + t) % cnt);
                    int opp = Destinations.OppositeLane(n, lane);
                    dest = shape.Vanishes(opp) ? lane : opp;
                }
            }

            if (dest < 0)
            {
                dest = NearestOpenLaneOutside(n, n.LanePoints[n.Lanes[d].FirstPoint], in shape);
            }

            if (dest >= 0)
            {
                v.DestLane[i] = dest;
                v.DestS[i] = n.Lanes[dest].Length * Destinations.ArrivalFraction;
                if (v.Route[i] >= 0)
                {
                    RouteNav.ReleaseRoute(w, i);   // план вёл на прежнюю полосу прибытия
                }
            }
        }

        PendingTrips trips = w.Trips;
        for (int k = 0; k < trips.Count; k++)
        {
            PendingTrip t = trips.At(k);
            if (t.FromLane >= 0 && t.FromZone < 0 && shape.Vanishes(t.FromLane))
            {
                trips.RemoveAt(k--);   // старт на исчезнувшей полосе без зоны: стартовать негде
                w.Counters.TripsDropped++;
            }
        }
    }

    private static int NearestOpenLaneOutside(NetworkStore n, Vec2 center, in VanishShape shape)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int l = 0; l < n.LaneCount; l++)
        {
            if (!n.LaneAlive(l) || n.Lanes[l].Kind != LaneKind.Normal || n.Lanes[l].LaneNo != 0 || n.LaneClosed(l) || shape.Vanishes(l))
            {
                continue;
            }

            Vec2 p = n.LanePoints[n.Lanes[l].FirstPoint];
            float dx = p.X - center.X;
            float dz = p.Z - center.Z;
            float d = (dx * dx) + (dz * dz);
            if (d < bestD)
            {
                bestD = d;
                best = l;
            }
        }

        return best;
    }
}

/// <summary>Какие полосы старого блока сегмента исчезают при смене числа полос: номер в направлении не меньше нового числа полос.</summary>
internal readonly struct VanishShape
{
    private readonly int _firstAb, _oldAb, _newAb, _firstBa, _oldBa, _newBa;

    public VanishShape(int firstAb, int oldAb, int newAb, int firstBa, int oldBa, int newBa)
    {
        _firstAb = firstAb;
        _oldAb = oldAb;
        _newAb = newAb;
        _firstBa = firstBa;
        _oldBa = oldBa;
        _newBa = newBa;
    }

    public bool Vanishes(int lane) =>
        (_oldAb > 0 && lane >= _firstAb + _newAb && lane < _firstAb + _oldAb) || (_oldBa > 0 && lane >= _firstBa + _newBa && lane < _firstBa + _oldBa);
}
