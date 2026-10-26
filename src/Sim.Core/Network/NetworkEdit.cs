using Sim.Core.Api;

namespace Sim.Core.Network;

/// <summary>
/// Структурные примитивы сети с дырами (4.3, T2; player-tools.md §3): освобождение слота пула и его блоков с каноническими значениями. Это только «структура»:
/// условие «пусто, можно освобождать» (дренаж, T3), закрытие, перенос и пересчёт производных (<c>VerifyDerived</c>, граф, оверлей, пешеходы) - у вызывающего.
/// Каждый примитив отклоняет (<c>false</c>, мир не тронут), если предусловие нарушено: на сегменте стоят машины, у концов построены connector'ы и т.п.
/// Первый же успешный вызов ставит <see cref="NetworkStore.ToolsTouched"/> = 1: с этого момента раскладка, free-list'ы и новые поля входят в хэш и сейв.
/// </summary>
internal static class NetworkEdit
{
    /// <summary>
    /// Освобождает сегмент: пул (поколение +1), блоки полос, точек полос и осевых точек (в арены, канонические значения), строка в списках инцидентности узлов.
    /// Предусловия: блоки сегмента - как у построенного <c>AddSegment</c> (полосы AB, затем BA подряд; один блок точек полос), полосы пусты, на них не ссылаются connector'ы
    /// (у концов нет построенных рукавов с этим сегментом, <c>NextCount == 0</c>).
    /// </summary>
    public static bool ReleaseSegment(World w, int seg)
    {
        NetworkStore n = w.Net;
        if (!n.SegmentAlive(seg))
        {
            return false;
        }

        SegmentData sd = n.Segments[seg];
        int laneTotal = sd.LanesAB + sd.LanesBA;
        int firstLane = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
        if (laneTotal == 0 || firstLane < 0 || (sd.LanesAB > 0 && sd.LanesBA > 0 && sd.FirstLaneBA != sd.FirstLaneAB + sd.LanesAB))
        {
            return false;
        }

        int firstPoint = n.Lanes[firstLane].FirstPoint;
        for (int k = 0; k < laneTotal; k++)
        {
            ref readonly LaneData l = ref n.Lanes[firstLane + k];
            if (!n.LaneAlive(firstLane + k) || l.Segment != seg || l.NextCount != 0 || w.Occupancy.LaneCount[firstLane + k] != 0 ||
                l.PointCount != sd.PointCount || l.FirstPoint != firstPoint + (k * sd.PointCount))
            {
                return false;
            }
        }

        if (w.City != null)
        {
            // Z6: участки сегмента - Lost, здания заброшены (до освобождения слота). TODO(T7): IssueCode.ZoneLotsLost в превью RemoveRoad - число зданий на этих участках.
            int lostLots = City.LotBuilder.OnSegmentRemoved(w, w.City, seg, sd.Generation);
            if (lostLots > 0)
            {
                w.Emit(SimEventKind.ZoneLotsLost, 0, seg, lostLots);
            }
        }

        n.ToolsTouched = 1;
        w.Builder.UnlinkSegment(seg);
        n.FreeLaneBlock(firstLane, laneTotal);
        n.FreePointBlock(firstPoint, laneTotal * sd.PointCount);
        n.FreeSegmentPointBlock(sd.FirstPoint, sd.PointCount);
        for (int k = 0; k < laneTotal; k++)
        {
            w.LaneSignals[firstLane + k] = 0;
        }

        n.SegmentPool.Free(seg);
        n.Segments[seg] = NetworkStore.DeadSegment(n.SegmentPool.GenerationOf(seg));
        w.SegmentLoads[seg] = new SegmentLoad { SpeedRatio = 1f };
        n.Version++;
        return true;
    }

    /// <summary>
    /// Освобождает узел без рукавов и connector'ов (после <see cref="ReleaseSegment"/> всех сегментов и перестройки узла в «пустой»): пул (поколение +1), сетка прилипания.
    /// Предусловия: <c>FirstArm &lt; 0</c> (не построен), нет сегментов у узла, нет светофора.
    /// </summary>
    public static bool ReleaseNode(World w, int node)
    {
        NetworkStore n = w.Net;
        if (!n.NodeAlive(node) || n.Nodes[node].FirstArm >= 0 || n.Nodes[node].Signal >= 0 || n.IncHead[node] >= 0)
        {
            return false;
        }

        n.ToolsTouched = 1;
        w.Builder.UnlinkNode(node);
        n.NodePool.Free(node);
        n.Nodes[node] = NetworkStore.DeadNode(n.NodePool.GenerationOf(node));
        n.NodeTicket[node] = 0;
        w.NodeThroughput[node] = 0;
        n.IncHead[node] = -1;
        n.Version++;
        return true;
    }

    /// <summary>Освобождает остановку, которой нет ни в одном списке маршрутов (<c>BusRouteStops</c>) и нет пешеходов (<c>w.Peds == null</c>).</summary>
    public static bool ReleaseBusStop(World w, int stop)
    {
        NetworkStore n = w.Net;
        if (!n.BusStopAlive(stop) || w.Peds != null || StopListed(n, stop))
        {
            return false;
        }

        n.ToolsTouched = 1;
        n.BusStopPool.Free(stop);
        n.BusStops[stop] = NetworkStore.DeadBusStop(n.BusStopPool.GenerationOf(stop));
        n.Version++;
        return true;
    }

    /// <summary>Освобождает маршрут без остановок и автобусов: пул (поколение +1), блок <c>BusRouteStops</c> (пустой - ничего не освобождает), статистика.</summary>
    public static bool ReleaseBusRoute(World w, int route)
    {
        NetworkStore n = w.Net;
        if (!n.BusRouteAlive(route) || n.BusRoutes[route].ActiveBuses != 0 || w.Peds != null)
        {
            return false;
        }

        for (int s = 0; s < n.BusStopCount; s++)
        {
            if (n.BusStopAlive(s) && n.BusStops[s].Route == route)
            {
                return false;
            }
        }

        n.ToolsTouched = 1;
        BusRouteData r = n.BusRoutes[route];
        n.FreeBusRouteStopBlock(r.FirstStop, r.StopCount);
        n.BusRoutePool.Free(route);
        n.BusRoutes[route] = NetworkStore.DeadBusRoute(n.BusRoutePool.GenerationOf(route));
        w.BusRouteStats[route] = new BusRouteStats { LastDispatchTick = -1 };
        n.Version++;
        return true;
    }

    /// <summary>
    /// Пакетный пересчёт производных после структурных правок (player-tools.md §12 шаг 1c): сеть пешеходов (при <paramref name="pedsChanged"/>; сейчас - полная пересборка со сбросом
    /// пешеходов, как после <c>BuildRoad</c>; T11), LOD-привязки, граф маршрутов целиком и веса по нагрузке, оверлей (при <c>Routing.Levels &gt; 0</c> - полная перестройка, T6 сделает её
    /// инкрементальной), пометка маршрутов грязными, сброс кэша спавна. Скан закрытых DirSeg - T4.
    /// </summary>
    public static void RefreshDerived(World w, bool pedsChanged = true)
    {
        if (pedsChanged)
        {
            w.Peds?.OnNetworkGrown(w);
        }

        w.Lod?.OnNetworkGrown();
        w.Vehicles.MarkAllRouteDirty();
        w.Graph.Rebuild();
        w.Graph.RecalcCosts(w.EnsureSegmentLoads(), w.Net.SegmentCount);
        w.Spawn.Ready = false;
        Routing.Overlay? ov = w.Overlay;
        if (ov != null && !ov.Disabled)
        {
            ov.RebuildAfterEdit();
            w.Counters.OverlayRebuilds = ov.Rebuilds;
            w.Counters.OverlayDisabled = ov.Disabled ? 1 : 0;
        }
    }

    private static bool StopListed(NetworkStore n, int stop)
    {
        for (int r = 0; r < n.BusRouteCount; r++)
        {
            if (!n.BusRouteAlive(r))
            {
                continue;
            }

            ref readonly BusRouteData rd = ref n.BusRoutes[r];
            for (int k = 0; k < rd.StopCount; k++)
            {
                if (n.BusRouteStops[rd.FirstStop + k] == stop)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
