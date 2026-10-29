using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;

namespace Sim.Core.Metrics;

/// <summary>
/// События с местом (EV1, core-api.md §5.7): затор на сегменте (<see cref="SimEventKind.JamAt"/>), проблемы автобусной линии
/// (<see cref="SimEventKind.BusLineProblem"/>), нехватка парковки у здания (<see cref="SimEventKind.ParkingFull"/>) и место у
/// <c>GridlockOn</c>/<c>DrainStalled</c>/<c>ZoneAccessLost</c>. Только вывод: на поведение не влияет, вне хэша и сейва (после <c>Load</c>
/// остывание пустое - событие может прийти раньше, чем без сейва). Всё однопоточно (фаза 9 и точки эмиссии вне параллельных фаз),
/// порядок - по возрастанию индекса, PRNG нет; массивы выделены при создании мира, 0 Б/тик.
/// Обход машин - раз в <see cref="ScanPeriodTicks"/> и только при застрявших (<c>Stuck &gt; 0</c>) либо при <c>GridlockOn</c>.
/// </summary>
internal sealed class PlaceEvents
{
    /// <summary>Период проверки заторов и линий (5 с).</summary>
    public const int ScanPeriodTicks = 100;

    /// <summary>Затор: застрявших машин на сегменте (с connector'ами, въезжающими из него) не меньше.</summary>
    public const int JamMinStuck = 5;

    /// <summary>Не больше стольких <c>JamAt</c> за проверку (самые большие, ничья - меньший индекс).</summary>
    public const int MaxJamsPerScan = 3;

    /// <summary>Остывание места: тот же сегмент - не чаще раза в 2 мин симуляции.</summary>
    public const int JamCooldownTicks = 2400;

    /// <summary>Остывание (линия, вид проблемы) - 5 мин симуляции.</summary>
    public const int BusCooldownTicks = 6000;

    /// <summary>Нет автобусов: выпуск просрочен не меньше чем на 60 с.</summary>
    public const int NoBusGraceTicks = 1200;

    /// <summary>Перегруз: ждущих на остановке не меньше (полторы вместимости автобуса).</summary>
    public const int OverloadWaiting = 120;

    /// <summary>Парковка: отказов в месте у здания за окно (игровой час) не меньше.</summary>
    public const int ParkingMinRefusals = 3;

    /// <summary>Записей таблицы зданий с отказами (вытесняется самая старая).</summary>
    public const int ParkingSlots = 32;

    private readonly int[] _segStuck;        // scratch: застрявших по сегменту (обнулён между проверками)
    private readonly int[] _touched;         // scratch: сегменты с ненулевым _segStuck
    private readonly long[] _segLast;        // тик последнего JamAt сегмента
    private readonly int[] _routeStuck;      // scratch: застрявших автобусов линии
    private readonly int[] _routeStuckBus;   // scratch: первый (по индексу) застрявший автобус линии
    private readonly long[] _routeLast;      // [маршрут * 4 + вид] тик последнего BusLineProblem
    private readonly int[] _top = new int[MaxJamsPerScan];

    private readonly int[] _pkBuilding = new int[ParkingSlots];
    private readonly int[] _pkGen = new int[ParkingSlots];
    private readonly long[] _pkWindow = new long[ParkingSlots];
    private readonly int[] _pkCount = new int[ParkingSlots];
    private readonly long[] _pkLast = new long[ParkingSlots];

    public PlaceEvents(int maxSegments, int maxBusRoutes)
    {
        _segStuck = new int[maxSegments];
        _touched = new int[maxSegments];
        _segLast = new long[maxSegments];
        _routeStuck = new int[maxBusRoutes];
        _routeStuckBus = new int[maxBusRoutes];
        _routeLast = new long[maxBusRoutes * 4];
        System.Array.Fill(_segLast, long.MinValue / 2);
        System.Array.Fill(_routeLast, long.MinValue / 2);
        System.Array.Fill(_pkBuilding, -1);
        System.Array.Fill(_pkLast, long.MinValue / 2);
    }

    /// <summary>Фаза 9, после gridlock: заторы и линии раз в период. <paramref name="stuck"/> - застрявших сейчас.</summary>
    public void Run(World w, int stuck)
    {
        if (w.Tick % ScanPeriodTicks != 0)
        {
            return;
        }

        int jams = stuck > 0 ? Scan(w) : 0;
        EmitJams(w, jams, stuck);
        CheckBusLines(w, stuck > 0);
        Reset(w, jams);
    }

    /// <summary>Место для <c>GridlockOn</c>: сегмент с наибольшим числом застрявших (ничья - меньший индекс) или без места.</summary>
    public EventPlace GridlockPlace(World w)
    {
        int n = Scan(w);
        int best = -1;
        for (int k = 0; k < n; k++)
        {
            int s = _touched[k];
            if (best < 0 || _segStuck[s] > _segStuck[best] || (_segStuck[s] == _segStuck[best] && s < best))
            {
                best = s;
            }
        }

        EventPlace p = best >= 0 ? SegmentPlace(w.Net, best) : default;
        Reset(w, n);
        return p;
    }

    /// <summary>Первый отказ в месте на ногу (<c>TripLegs.SubmitParked</c>): счёт по зданию цели за окно, событие с остыванием.</summary>
    public void OnNoSpot(World w, CityWorld c, int building)
    {
        int gen = c.Buildings.Pool.GenerationOf(building);
        int window = c.DayTicks / 24 > ScanPeriodTicks ? c.DayTicks / 24 : ScanPeriodTicks;
        int k = -1;
        int oldest = 0;
        for (int j = 0; j < ParkingSlots; j++)
        {
            if (_pkBuilding[j] == building && _pkGen[j] == gen)
            {
                k = j;
                break;
            }

            if (_pkBuilding[j] < 0 || (_pkBuilding[oldest] >= 0 && _pkWindow[j] < _pkWindow[oldest]))
            {
                oldest = j;
            }
        }

        long now = w.Tick;
        if (k < 0 || now - _pkWindow[k] >= window)
        {
            if (k < 0)
            {
                k = oldest;
                _pkLast[k] = long.MinValue / 2;
            }

            _pkBuilding[k] = building;
            _pkGen[k] = gen;
            _pkWindow[k] = now;
            _pkCount[k] = 0;
        }

        if (++_pkCount[k] < ParkingMinRefusals || now - _pkLast[k] < 2L * window)
        {
            return;
        }

        _pkLast[k] = now;
        int lot = c.Buildings.Lot[building];
        float x = (uint)lot < (uint)c.Lots.X.Length ? c.Lots.X[lot] : 0f;
        float z = (uint)lot < (uint)c.Lots.Z.Length ? c.Lots.Z[lot] : 0f;
        w.EmitAt(SimEventKind.ParkingFull, 0, _pkCount[k], lot, new EventPlace(EntityKind.Building, building, gen, x, z));
    }

    /// <summary>Место сегмента: середина осевой полилинии (без точек - середина между узлами).</summary>
    public static EventPlace SegmentPlace(NetworkStore net, int seg)
    {
        ref readonly SegmentData sd = ref net.Segments[seg];
        float x;
        float z;
        if (sd.PointCount > 0)
        {
            Util.Vec2 a = net.SegmentPoints[sd.FirstPoint + ((sd.PointCount - 1) / 2)];
            Util.Vec2 b = net.SegmentPoints[sd.FirstPoint + (sd.PointCount / 2)];
            x = (a.X + b.X) * 0.5f;
            z = (a.Z + b.Z) * 0.5f;
        }
        else
        {
            Util.Vec2 a = net.Nodes[sd.NodeA].Position;
            Util.Vec2 b = net.Nodes[sd.NodeB].Position;
            x = (a.X + b.X) * 0.5f;
            z = (a.Z + b.Z) * 0.5f;
        }

        return new EventPlace(EntityKind.Segment, seg, sd.Generation, x, z);
    }

    /// <summary>Место зоны: центр (без поколения).</summary>
    public static EventPlace ZonePlace(NetworkStore net, int zone)
    {
        ref readonly ZoneData zd = ref net.Zones[zone];
        return new EventPlace(EntityKind.Zone, zone, 0, zd.Center.X, zd.Center.Z);
    }

    // Сегмент машины: полоса - её сегмент; connector - сегмент полосы, из которой он ведёт (очередь у узла); иначе -1.
    private static int SegmentOfLane(NetworkStore net, int lane)
    {
        if ((uint)lane >= (uint)net.LaneCount)
        {
            return -1;
        }

        ref readonly LaneData ld = ref net.Lanes[lane];
        if (ld.Kind == LaneKind.Connector)
        {
            return (uint)ld.FromLane < (uint)net.LaneCount ? net.Lanes[ld.FromLane].Segment : -1;
        }

        return ld.Segment;
    }

    // Обход машин: застрявшие по сегментам (_segStuck/_touched) и застрявшие автобусы по линиям. Возвращает число затронутых сегментов.
    private int Scan(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int segs = net.SegmentCount;
        int routes = net.BusRouteCount;
        int n = 0;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            byte f = v.Flags[i];
            if ((f & (byte)VehicleFlags.Stuck) == 0 || !v.Pool.IsAlive(i))
            {
                continue;
            }

            if ((f & (byte)VehicleFlags.IsBus) != 0 && (f & (byte)VehicleFlags.AtStop) == 0)
            {
                int r = v.BusRoute[i];
                if ((uint)r < (uint)routes && _routeStuck[r]++ == 0)
                {
                    _routeStuckBus[r] = i;
                }
            }

            int s = SegmentOfLane(net, v.Lane[i]);
            if ((uint)s < (uint)segs && _segStuck[s]++ == 0)
            {
                _touched[n++] = s;
            }
        }

        return n;
    }

    private void Reset(World w, int touched)
    {
        for (int k = 0; k < touched; k++)
        {
            _segStuck[_touched[k]] = 0;
        }

        int routes = w.Net.BusRouteCount;
        for (int r = 0; r < routes; r++)
        {
            _routeStuck[r] = 0;
        }
    }

    private void EmitJams(World w, int touched, int stuck)
    {
        int found = 0;
        long now = w.Tick;
        for (int k = 0; k < touched; k++)
        {
            int s = _touched[k];
            int cnt = _segStuck[s];
            if (cnt < JamMinStuck || now - _segLast[s] < JamCooldownTicks)
            {
                continue;
            }

            // Вставка в тройку лучших: больше застрявших, при равенстве - меньший индекс.
            int pos = found < MaxJamsPerScan ? found++ : MaxJamsPerScan;
            while (pos > 0 && Better(s, _top[pos - 1]))
            {
                if (pos < MaxJamsPerScan)
                {
                    _top[pos] = _top[pos - 1];
                }

                pos--;
            }

            if (pos < MaxJamsPerScan)
            {
                _top[pos] = s;
            }
        }

        for (int k = 0; k < found; k++)
        {
            int s = _top[k];
            _segLast[s] = now;
            w.EmitAt(SimEventKind.JamAt, 0, _segStuck[s], stuck, SegmentPlace(w.Net, s));
        }
    }

    private bool Better(int a, int b) => _segStuck[a] > _segStuck[b] || (_segStuck[a] == _segStuck[b] && a < b);

    private void CheckBusLines(World w, bool scanned)
    {
        NetworkStore net = w.Net;
        int rc = net.BusRouteCount;
        long now = w.Tick;
        for (int r = 0; r < rc; r++)
        {
            if (!net.BusRouteAlive(r))
            {
                continue;
            }

            ref readonly BusRouteData br = ref net.BusRoutes[r];
            if (br.StopCount < 1 || br.HeadwayTicks <= 0 || (uint)br.FirstStop >= (uint)net.BusStopCount)
            {
                continue;
            }

            // Нет автобусов: выпуск просрочен (BusDispatch не смог поставить автобус или построить путь).
            long due = br.LastDispatchTick < 0 ? (long)r * br.HeadwayTicks / rc : br.LastDispatchTick + br.HeadwayTicks;
            long late = now - due;
            if (br.ActiveBuses == 0 && late >= NoBusGraceTicks && Ready(r, BusLineIssue.NoBuses, now))
            {
                EventPlace p = StopPoint(net, r, in br, br.FirstStop);
                w.EmitAt(SimEventKind.BusLineProblem, 0, (int)BusLineIssue.NoBuses, late > int.MaxValue ? int.MaxValue : (int)late, in p, br.FirstStop);
            }

            if (scanned && _routeStuck[r] > 0 && Ready(r, BusLineIssue.BusesStuck, now))
            {
                int bus = _routeStuckBus[r];
                VehicleStore v = w.Vehicles;
                int lane = v.Lane[bus];
                float x = 0f;
                float z = 0f;
                if ((uint)lane < (uint)net.LaneCount)
                {
                    int hint = v.PointHint[bus];
                    LaneGeometry.Evaluate(net, lane, v.S[bus], ref hint, out Util.Vec2 pos, out _);
                    x = pos.X;
                    z = pos.Z;
                }

                w.EmitAt(SimEventKind.BusLineProblem, 0, (int)BusLineIssue.BusesStuck, _routeStuck[r], new EventPlace(EntityKind.BusRoute, r, br.Generation, x, z), -1);
            }

            int worst = -1;
            int waiting = 0;
            for (int k = 0; k < br.StopCount; k++)
            {
                int si = br.FirstStop + k;
                if ((uint)si < (uint)net.BusStopCount && net.BusStops[si].Waiting > waiting)
                {
                    waiting = net.BusStops[si].Waiting;
                    worst = si;
                }
            }

            if (worst >= 0 && waiting >= OverloadWaiting && Ready(r, BusLineIssue.Overloaded, now))
            {
                EventPlace p = StopPoint(net, r, in br, worst);
                w.EmitAt(SimEventKind.BusLineProblem, 0, (int)BusLineIssue.Overloaded, waiting, in p, worst);
            }
        }
    }

    private bool Ready(int route, BusLineIssue issue, long now)
    {
        int k = (route * 4) + (int)issue;
        if (now - _routeLast[k] < BusCooldownTicks)
        {
            return false;
        }

        _routeLast[k] = now;
        return true;
    }

    // Место линии в точке остановки (полоса, S).
    private static EventPlace StopPoint(NetworkStore net, int route, in BusRouteData br, int stop)
    {
        ref readonly BusStopData sd = ref net.BusStops[stop];
        float x = 0f;
        float z = 0f;
        if ((uint)sd.Lane < (uint)net.LaneCount)
        {
            int hint = 0;
            LaneGeometry.Evaluate(net, sd.Lane, sd.S, ref hint, out Util.Vec2 pos, out _);
            x = pos.X;
            z = pos.Z;
        }

        return new EventPlace(EntityKind.BusRoute, route, br.Generation, x, z);
    }
}
