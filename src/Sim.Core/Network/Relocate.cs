using System;
using System.Diagnostics;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Network;

/// <summary>
/// Таблица «старый индекс -> новый» для полос или строк connector'ов (4.3, T3; player-tools.md §4.2): до <see cref="Capacity"/> пар, новый индекс -1 = «исчезает» (элемент обязан быть
/// пуст, ссылки на него обнуляются). Старые индексы - по возрастанию (поиск двоичный), диапазон <see cref="MinOld"/>..<see cref="MaxOld"/> отсекает почти все чужие значения одним сравнением.
/// Старые и новые индексы не пересекаются (новый блок выделяется, пока старый жив), поэтому повторное применение к уже переписанному значению безопасно.
/// </summary>
internal sealed class IndexRemap
{
    public const int Capacity = 128;

    public readonly int[] Old = new int[Capacity];
    public readonly int[] New = new int[Capacity];
    public int Count;
    public int MinOld = int.MaxValue;
    public int MaxOld = -1;

    public void Clear()
    {
        Count = 0;
        MinOld = int.MaxValue;
        MaxOld = -1;
    }

    /// <summary>Добавляет пару; <paramref name="old"/> строго больше всех добавленных раньше.</summary>
    public void Add(int old, int @new)
    {
        Debug.Assert(Count < Capacity && (Count == 0 || old > Old[Count - 1]));
        Old[Count] = old;
        New[Count] = @new;
        Count++;
        MinOld = Math.Min(MinOld, old);
        MaxOld = Math.Max(MaxOld, old);
    }

    /// <summary>Есть ли <paramref name="idx"/> среди старых индексов (значение может быть переписано).</summary>
    public bool Contains(int idx) => idx >= MinOld && idx <= MaxOld && Array.BinarySearch(Old, 0, Count, idx) >= 0;

    /// <summary>Новый индекс или <paramref name="idx"/>, если его нет в таблице; -1 - исчезает. Отрицательные значения («нет») не трогает.</summary>
    public int Map(int idx)
    {
        if (idx < MinOld || idx > MaxOld)
        {
            return idx;
        }

        int k = Array.BinarySearch(Old, 0, Count, idx);
        return k >= 0 ? New[k] : idx;
    }
}

/// <summary>
/// Перенос ссылок на полосы и строки connector'ов по таблицам <see cref="IndexRemap"/> (player-tools.md §4.2, TL8): один проход по всем владельцам ссылок.
/// Не перемещает сами <c>LaneData</c> и их блоки (это делает вызывающий: перестройка узла строит новый блок connector'ов, перенос блока полос сегмента копирует данные), а
/// переписывает всё остальное: машины, списки занятости полосы, окна маршрутов, очереди поездок, строки connector'ов, остановки, зоны, светофоры, накопители ворот.
/// Реестр столбцов-ссылок схемы сейва (<c>SaveSchema.Refs</c>) сверяется с <see cref="Handlers"/> тестом <c>Relocate_Complete</c>: новый столбец с индексом полосы или connector'а без записи здесь - красный тест.
/// </summary>
internal static class Relocate
{
    /// <summary>Как столбец реестра ссылок обрабатывается при переносе. <c>Stable</c> - индекс не меняется переносом полос/connector'ов (причина в строке).</summary>
    internal readonly record struct Handler(string Block, string Column, string How);

    private const string Moved = "перенос Relocate.Apply";
    private const string Stable = "не меняется переносом (T3/T4: индексы узлов, сегментов, остановок и DirSeg стабильны)";

    /// <summary>Обработчик для каждой записи <c>SaveSchema.Refs</c>; тест сверяет списки (блок + столбец).</summary>
    internal static readonly Handler[] Handlers =
    {
        new("NSEG", "NodeA", Stable), new("NSEG", "NodeB", Stable),
        new("NSEG", "FirstLaneAB", "блок полос сегмента: переписывает владелец переноса блока (NetworkEdit)"),
        new("NSEG", "FirstLaneBA", "блок полос сегмента: переписывает владелец переноса блока (NetworkEdit)"),
        new("NLAN", "Segment", Stable), new("NLAN", "Node", Stable),
        new("NLAN", "FromLane", Moved), new("NLAN", "ToLane", Moved), new("NLAN", "Left", Moved), new("NLAN", "Right", Moved),
        new("NLAN", "ConflictGroup", "строки connector'ов: Relocate.Apply (rows)"),
        new("NARM", "NodeArms", Stable),
        new("NLNX", "LaneNext", Moved),
        new("NZON", "ZoneLanes", Moved),
        new("NBUS", "StopLane", Moved),
        new("NBUS", "BusRouteStops", Stable),
        new("CONN", "Lane", Moved), new("CONN", "Node", Stable), new("CONN", "InLane", Moved), new("CONN", "OutLane", Moved),
        new("SIGS", "Node", Stable), new("SIGS", "StateNode", Stable),
        new("VEHC", "Lane", Moved), new("VEHC", "GhostLane", Moved), new("VEHC", "DestLane", Moved), new("VEHC", "NextConn", Moved), new("VEHC", "GrantConnector", Moved),
        new("TRIP", "FromLane", Moved), new("TRIP", "DestLane", Moved),
        new("ROUT", "WinLane", Moved), new("ROUT", "WinNode", Stable),
        new("OVLY", "Entries", Stable), new("OVLY", "Exits", Stable),
        new("PEDS", "AlightStop", Stable),
        new("RESI", "StopAOut", Stable), new("RESI", "StopBOut", Stable), new("RESI", "StopABack", Stable), new("RESI", "StopBBack", Stable),
        new("RESD", "LegStopA", Stable), new("RESD", "LegStopB", Stable),
        new("LOTS", "Seg", Stable),
    };

    /// <summary>
    /// Применяет таблицы: <paramref name="lanes"/> - полосы (в том числе connector'ы), <paramref name="rows"/> - строки <c>ConnectorStore</c> (накопители ворот переезжают вместе со строкой).
    /// Машины на исчезающих полосах и допуски в исчезающие connector'ы не допускаются: допуски и кэш <c>NextConn</c> сбрасываются, маршруты с исчезающим шагом освобождаются (перемаршрутизация).
    /// </summary>
    public static void Apply(World w, IndexRemap lanes, IndexRemap rows)
    {
        if (lanes.Count == 0 && rows.Count == 0)
        {
            return;
        }

        if (lanes.Count > 0)
        {
            Occupancy(w, lanes);
            Vehicles(w, lanes);
            Trips(w, lanes);
            Stops(w, lanes);
            Zones(w, lanes);
            LaneArrays(w, lanes);
        }

        if (rows.Count > 0)
        {
            RowAccumulators(w, rows);
        }
    }

    // Списки машин и счётчики занятости: значения полосы переезжают на новый индекс, старый становится пустым.
    private static void Occupancy(World w, IndexRemap map)
    {
        LaneOccupancy occ = w.Occupancy;
        Span<int> head = stackalloc int[IndexRemap.Capacity];
        Span<int> tail = stackalloc int[IndexRemap.Capacity];
        Span<int> cnt = stackalloc int[IndexRemap.Capacity];
        for (int k = 0; k < map.Count; k++)
        {
            int o = map.Old[k];
            head[k] = occ.LaneHead[o];
            tail[k] = occ.LaneTail[o];
            cnt[k] = occ.LaneCount[o];
        }

        for (int k = 0; k < map.Count; k++)
        {
            int o = map.Old[k];
            occ.LaneHead[o] = -1;
            occ.LaneTail[o] = -1;
            occ.LaneCount[o] = 0;
            occ.OccMask[o >> 6] &= ~(1UL << (o & 63));
        }

        for (int k = 0; k < map.Count; k++)
        {
            int n = map.New[k];
            if (n < 0)
            {
                Debug.Assert(cnt[k] == 0, "Relocate: на исчезающей полосе есть машины");
                continue;
            }

            occ.LaneHead[n] = head[k];
            occ.LaneTail[n] = tail[k];
            occ.LaneCount[n] = cnt[k];
            if (cnt[k] > 0)
            {
                occ.OccMask[n >> 6] |= 1UL << (n & 63);
            }
        }
    }

    // Столбцы машин и окна их маршрутов. Маршрут с исчезающим шагом освобождается сразу (так же, как промах маршрута: RouteNav.ProcessMiss).
    private static void Vehicles(World w, IndexRemap map)
    {
        VehicleStore v = w.Vehicles;
        RouteStore rs = w.Routes;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            v.Lane[i] = MapLane(map, v.Lane[i]);
            v.GhostLane[i] = MapLane(map, v.GhostLane[i]);
            v.DestLane[i] = MapLane(map, v.DestLane[i]);
            v.NextConn[i] = MapLane(map, v.NextConn[i]);
            v.GrantConnector[i] = MapLane(map, v.GrantConnector[i]);
            if (v.GrantConnector[i] < 0 && (v.GateState[i] & 1) != 0 && v.Lane[i] >= 0 && w.Net.Lanes[v.Lane[i]].Kind == LaneKind.Normal)
            {
                v.GateState[i] = 0;   // допуск в исчезнувший connector потерян
            }

            int r = v.Route[i];
            if (r >= 0 && rs.IsAlive(new RouteHandle(r, v.RouteGen[i])) && RemapWindow(rs, r, map))
            {
                RouteNav.ReleaseRoute(w, i);
            }
        }
    }

    // Поездки в очереди: полоса старта/прибытия и окно готового маршрута.
    private static void Trips(World w, IndexRemap map)
    {
        PendingTrips trips = w.Trips;
        for (int k = 0; k < trips.Count; k++)
        {
            PendingTrip t = trips.At(k);
            bool changed = false;
            int fl = MapLane(map, t.FromLane);
            int dl = MapLane(map, t.DestLane);
            if (fl != t.FromLane || dl != t.DestLane)
            {
                t.FromLane = fl;
                t.DestLane = dl;
                changed = true;
            }

            if (t.HasRoute && w.Routes.IsAlive(new RouteHandle(t.Route, t.RouteGen)) && RemapWindow(w.Routes, t.Route, map))
            {
                w.Routes.Free(new RouteHandle(t.Route, t.RouteGen));
                t.Route = -1;
                t.RouteGen = 0;
                changed = true;
            }

            if (changed)
            {
                trips.Set(k, in t);
            }
        }
    }

    // true - в окне есть шаг на исчезающей полосе (окно оставлено как есть: маршрут освободит вызывающий).
    private static bool RemapWindow(RouteStore rs, int slot, IndexRemap map)
    {
        int b = slot * rs.WindowSteps;
        int n = rs.WinCount[slot];
        for (int s = 0; s < n; s++)
        {
            int l = rs.Window[b + s].Lane;
            if (map.Contains(l) && map.Map(l) < 0)
            {
                return true;
            }
        }

        for (int s = 0; s < n; s++)
        {
            rs.Window[b + s].Lane = map.Map(rs.Window[b + s].Lane);
        }

        return false;
    }

    private static void Stops(World w, IndexRemap map)
    {
        NetworkStore n = w.Net;
        for (int s = 0; s < n.BusStopCount; s++)
        {
            if (n.BusStopAlive(s))
            {
                n.BusStops[s].Lane = map.Map(n.BusStops[s].Lane);
            }
        }
    }

    private static void Zones(World w, IndexRemap map)
    {
        NetworkStore n = w.Net;
        for (int z = 0; z < n.ZoneCount; z++)
        {
            int first = n.Zones[z].FirstSpawnLane;
            for (int k = 0; k < n.Zones[z].SpawnLaneCount; k++)
            {
                n.ZoneLanes[first + k] = map.Map(n.ZoneLanes[first + k]);
            }
        }

        w.Spawn.Ready = false;
    }

    // Статика сети и производные массивы полос: ссылки полос друг на друга, LaneNext, строки connector'ов, сигналы, накопители.
    private static void LaneArrays(World w, IndexRemap map)
    {
        NetworkStore n = w.Net;
        for (int l = 0; l < n.LaneCount; l++)
        {
            if (!n.LaneAlive(l))
            {
                continue;
            }

            ref LaneData ld = ref n.Lanes[l];
            ld.FromLane = MapLane(map, ld.FromLane);
            ld.ToLane = MapLane(map, ld.ToLane);
            ld.Left = MapLane(map, ld.Left);
            ld.Right = MapLane(map, ld.Right);
        }

        for (int l = 0; l < n.LaneCount; l++)
        {
            if (!n.LaneAlive(l))
            {
                continue;
            }

            ref readonly LaneData ld = ref n.Lanes[l];
            for (int k = 0; k < ld.NextCount; k++)
            {
                n.LaneNext[ld.FirstNext + k] = map.Map(n.LaneNext[ld.FirstNext + k]);
            }
        }

        ConnectorStore cs = w.Connectors;
        for (int g = 0; g < cs.Count; g++)
        {
            if (cs.Lane[g] < 0)
            {
                continue;
            }

            cs.Lane[g] = map.Map(cs.Lane[g]);
            cs.InLane[g] = map.Map(cs.InLane[g]);
            cs.OutLane[g] = map.Map(cs.OutLane[g]);
        }

        // Выходные массивы и состояние светофора переезжают вместе с полосой (значения пересоберёт ближайший тик, но хэш видит их сразу).
        Span<byte> sig = stackalloc byte[IndexRemap.Capacity];
        Span<float> res = stackalloc float[IndexRemap.Capacity];
        for (int k = 0; k < map.Count; k++)
        {
            sig[k] = w.LaneSignals[map.Old[k]];
            res[k] = n.OutReserved[map.Old[k]];
        }

        for (int k = 0; k < map.Count; k++)
        {
            w.LaneSignals[map.Old[k]] = 0;
            n.OutReserved[map.Old[k]] = 0f;
        }

        for (int k = 0; k < map.Count; k++)
        {
            if (map.New[k] >= 0)
            {
                w.LaneSignals[map.New[k]] = sig[k];
                n.OutReserved[map.New[k]] = res[k];
            }
        }

        w.Gate.FullScan = true;
    }

    // Накопители ворот по строкам connector'ов (в хэше): значения выживших строк переезжают, остальные обнуляются.
    private static void RowAccumulators(World w, IndexRemap rows)
    {
        GateScratch g = w.Gate;
        Span<long> wait = stackalloc long[IndexRemap.Capacity];
        Span<int> cnt = stackalloc int[IndexRemap.Capacity];
        Span<float> delay = stackalloc float[IndexRemap.Capacity];
        for (int k = 0; k < rows.Count; k++)
        {
            int o = rows.Old[k];
            wait[k] = g.ConnWaitSum[o];
            cnt[k] = g.ConnGrantCount[o];
            delay[k] = g.ConnDelay[o];
        }

        for (int k = 0; k < rows.Count; k++)
        {
            int o = rows.Old[k];
            g.ConnWaitSum[o] = 0;
            g.ConnGrantCount[o] = 0;
            g.ConnDelay[o] = 0f;
        }

        for (int k = 0; k < rows.Count; k++)
        {
            int n = rows.New[k];
            if (n >= 0)
            {
                g.ConnWaitSum[n] = wait[k];
                g.ConnGrantCount[n] = cnt[k];
                g.ConnDelay[n] = delay[k];
            }
        }
    }

    private static int MapLane(IndexRemap map, int lane) => lane < 0 ? lane : map.Map(lane);
}
