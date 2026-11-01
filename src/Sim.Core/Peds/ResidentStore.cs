using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Житель-заготовка (pedestrians.md §3, ADR-0011): дом и работа на тротуарах, способ на оба направления, время готовности. SoA по индексу
/// <c>0..Count-1</c>, жители не создаются и не удаляются. Всё - состояние (хэш, сейв <c>RESI</c>), кроме анкеров <c>*Anchor*</c> (scratch для <c>BuildRoad</c>).
/// <para>
/// <c>Where</c>: <see cref="AtHome"/>, <see cref="AtWork"/>, <see cref="ToWork"/> (идёт из дома на работу), <see cref="ToHome"/> (обратно). Спека знает только «2 в пути»;
/// два кода нужны, чтобы после сброса пешеходов (<c>BuildRoad</c>) житель вернулся туда, откуда вышел. Очередь готовых - интрузивная FIFO через
/// <see cref="ReadyNext"/>: <c>-2</c> - жителя в очереди нет, <c>-1</c> - хвост, иначе следующий.
/// </para>
/// <para>
/// P4: при модели пассажиров <c>agents</c> и наличии маршрутов способ и пара остановок выбираются в <see cref="Create"/> (<see cref="BusPlanner"/>, §3 п. 2-3); индексы остановок - глобальные
/// в <c>Net.BusStops</c>. Без автобусов (или при <c>counter</c>) <c>ModeOut = ModeBack = 0</c>, маршруты и остановки -1, работа - в радиусе <c>walk_only_m</c>.
/// </para>
/// </summary>
internal sealed class ResidentStore
{
    public const byte AtHome = 0;
    public const byte AtWork = 1;
    public const byte ToWork = 2;
    public const byte ToHome = 3;

    public const int NotQueued = -2;

    public readonly int Capacity;
    public int Count;

    public readonly int[] HomeEdge;
    public readonly int[] HomeSMm;
    public readonly int[] WorkEdge;
    public readonly int[] WorkSMm;
    public readonly int[] HomeZone;
    public readonly int[] WorkZone;
    public readonly byte[] ModeOut;       // 0 пешком, 1 автобус (P4)
    public readonly byte[] ModeBack;
    public readonly int[] RouteOut;       // маршрут и остановки посадки/высадки (индексы в BusRouteStops) или -1 (P4)
    public readonly int[] StopAOut;
    public readonly int[] StopBOut;
    public readonly int[] RouteBack;
    public readonly int[] StopABack;
    public readonly int[] StopBBack;
    public readonly byte[] Where;
    public readonly long[] ReadyTick;     // раньше этого тика поездку не начинать
    public readonly int[] Ped;            // индекс активного пешехода или -1
    public readonly int[] ReadyNext;      // FIFO готовых (см. выше)
    public int ReadyHead = -1;
    public int ReadyTail = -1;

    // ---- scratch для BuildRoad: точка на тротуаре как (сегмент·2 + сторона, s по оси); вне хэша и сейва ----
    public readonly int[] HomeAnchor;
    public readonly float[] HomeAnchorS;
    public readonly int[] WorkAnchor;
    public readonly float[] WorkAnchorS;

    public ResidentStore(int capacity)
    {
        Capacity = capacity;
        HomeEdge = new int[capacity];
        HomeSMm = new int[capacity];
        WorkEdge = new int[capacity];
        WorkSMm = new int[capacity];
        HomeZone = new int[capacity];
        WorkZone = new int[capacity];
        ModeOut = new byte[capacity];
        ModeBack = new byte[capacity];
        RouteOut = new int[capacity];
        StopAOut = new int[capacity];
        StopBOut = new int[capacity];
        RouteBack = new int[capacity];
        StopABack = new int[capacity];
        StopBBack = new int[capacity];
        Where = new byte[capacity];
        ReadyTick = new long[capacity];
        Ped = new int[capacity];
        ReadyNext = new int[capacity];
        HomeAnchor = new int[capacity];
        HomeAnchorS = new float[capacity];
        WorkAnchor = new int[capacity];
        WorkAnchorS = new float[capacity];
        Array.Fill(HomeEdge, -1);
        Array.Fill(WorkEdge, -1);
        Array.Fill(HomeZone, -1);
        Array.Fill(WorkZone, -1);
        Array.Fill(RouteOut, -1);
        Array.Fill(StopAOut, -1);
        Array.Fill(StopBOut, -1);
        Array.Fill(RouteBack, -1);
        Array.Fill(StopABack, -1);
        Array.Fill(StopBBack, -1);
        Array.Fill(Ped, -1);
        Array.Fill(ReadyNext, NotQueued);
    }

    /// <summary>Жители, не вышедшие из дома или с работы (<c>Where</c> 0 или 1); <c>Residents = Idle + Active</c> (PD2).</summary>
    public int IdleCount()
    {
        int n = 0;
        for (int r = 0; r < Count; r++)
        {
            if (Where[r] <= AtWork)
            {
                n++;
            }
        }

        return n;
    }

    public void EnqueueReady(int r)
    {
        ReadyNext[r] = -1;
        if (ReadyTail >= 0)
        {
            ReadyNext[ReadyTail] = r;
        }
        else
        {
            ReadyHead = r;
        }

        ReadyTail = r;
    }

    /// <summary>Снимает голову очереди готовых (очередь не пуста).</summary>
    public int DequeueReady()
    {
        int r = ReadyHead;
        ReadyHead = ReadyNext[r];
        if (ReadyHead < 0)
        {
            ReadyTail = -1;
        }

        ReadyNext[r] = NotQueued;
        return r;
    }

    // ------------------------------------------------------------------ Create (pedestrians.md §3)

    /// <summary>
    /// Создаёт <c>PedCfg.Residents</c> жителей (PRNG <c>Peds</c>, по индексу жителя). Зона дома - внутренняя, пропорционально <c>Weight</c>;
    /// работа - гравитация <c>Weight / d^α</c> среди внутренних зон в радиусе <c>walk_only_m</c> той же компоненты связности (T6); нет кандидата - работа в зоне дома.
    /// Нет внутренних зон с полосами появления - «зоной» считается вся сеть. Нет тротуаров - жителей нет.
    /// </summary>
    public void Create(World w)
    {
        PedNetwork pn = w.Net.PedNet!;
        NetworkStore net = w.Net;
        PedDesc cfg = w.PedCfg;
        Count = 0;
        if (pn.SidewalkEdgeCount == 0 || net.SegmentCount == 0)
        {
            return;
        }

        int n = Math.Min(cfg.Residents, Capacity);
        int zc = net.ZoneCount;
        var usable = new bool[zc];
        var comp = new int[zc];
        var cum = new float[zc + 1];
        int usableCount = 0;
        for (int z = 0; z < zc; z++)
        {
            comp[z] = -1;
            ref readonly ZoneData zd = ref net.Zones[z];
            if (zd.IsExternal != 0 || zd.SpawnLaneCount <= 0 || !(zd.Weight > 0f))
            {
                cum[z + 1] = cum[z];
                continue;
            }

            int seg = net.Lanes[net.ZoneLanes[zd.FirstSpawnLane]].Segment;
            comp[z] = seg >= 0 ? SegmentComponent(pn, seg) : -1;
            usable[z] = comp[z] >= 0;
            cum[z + 1] = cum[z] + (usable[z] ? zd.Weight : 0f);
            if (usable[z])
            {
                usableCount++;
            }
        }

        float alpha = w.Demand.GravityExponent > 0f ? w.Demand.GravityExponent : 1f;
        float walkOnlyM = cfg.WalkOnlyMm * 0.001f;
        BusPlanner? bus = BusPlanner.TryCreate(w, usable, comp);
        Pcg32 rng = w.RngPeds;
        var candW = new float[Math.Max(1, zc)];
        for (int r = 0; r < n; r++)
        {
            int hz = -1;
            int homeComp = -1;
            int he = -1;
            int hs = 0;
            for (int attempt = 0; attempt < 16 && he < 0; attempt++)
            {
                hz = usableCount > 0 ? PickZone(cum, zc, ref rng) : -1;
                if (PickPoint(w, hz, ref rng, out he, out hs))
                {
                    homeComp = pn.PedComp[pn.Edges[he].A];
                }

                if (he >= 0 && homeComp < 0)
                {
                    he = -1;
                }
            }

            if (he < 0)
            {
                continue;   // сеть без пригодных тротуаров: житель не создаётся (индексы подряд - Count ниже)
            }

            int wz = hz;
            int we = -1;
            int ws = 0;
            if (hz >= 0)
            {
                float total = 0f;
                Vec2 hc = net.Zones[hz].Center;
                for (int z = 0; z < zc; z++)
                {
                    candW[z] = 0f;
                    if (!usable[z] || comp[z] != homeComp)
                    {
                        continue;
                    }

                    Vec2 d = net.Zones[z].Center - hc;
                    float dist = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
                    if (dist > walkOnlyM && !(bus != null && z != hz && bus.TryPair(hz, z, out _) && bus.TryPair(z, hz, out _)))
                    {
                        continue;   // дальше walk_only_m - только если автобус есть в обе стороны (§3 п. 2)
                    }

                    float dd = MathF.Max(dist, 100f);
                    candW[z] = net.Zones[z].Weight / (alpha == 1f ? dd : MathF.Pow(dd, alpha));
                    total += candW[z];
                }

                if (total > 0f)
                {
                    float x = rng.NextFloat01() * total;
                    int pick = -1;
                    for (int z = 0; z < zc; z++)
                    {
                        if (candW[z] <= 0f)
                        {
                            continue;
                        }

                        pick = z;
                        x -= candW[z];
                        if (x < 0f)
                        {
                            break;
                        }
                    }

                    wz = pick >= 0 ? pick : hz;
                }
            }

            for (int attempt = 0; attempt < 8 && we < 0; attempt++)
            {
                if (PickPoint(w, wz, ref rng, out we, out ws) && pn.PedComp[pn.Edges[we].A] != homeComp)
                {
                    we = -1;
                }

                if (we < 0 && attempt == 5)
                {
                    wz = hz;   // зона работы недостижима: работа в зоне дома
                }
            }

            if (we < 0)
            {
                we = he;
                ws = hs;
                wz = hz;
            }

            int i = Count++;
            ModeOut[i] = 0;
            ModeBack[i] = 0;
            if (bus != null && hz >= 0 && wz >= 0 && wz != hz)
            {
                bus.Choose(w, hz, wz, homeComp, out Plan po, out Plan pb);
                ApplyPlan(in po, ModeOut, RouteOut, StopAOut, StopBOut, i);
                ApplyPlan(in pb, ModeBack, RouteBack, StopABack, StopBBack, i);
            }

            HomeEdge[i] = he;
            HomeSMm[i] = hs;
            WorkEdge[i] = we;
            WorkSMm[i] = ws;
            HomeZone[i] = hz;
            WorkZone[i] = wz;
            Where[i] = AtHome;
            ReadyTick[i] = rng.NextBelow((uint)Math.Max(1, cfg.ActivityMaxTicks));
            Ped[i] = -1;
            ReadyNext[i] = NotQueued;
        }

        w.RngPeds = rng;
    }

    private static void ApplyPlan(in Plan plan, byte[] mode, int[] route, int[] stopA, int[] stopB, int i)
    {
        mode[i] = plan.Bus ? (byte)1 : (byte)0;
        route[i] = plan.Bus ? plan.Route : -1;
        stopA[i] = plan.Bus ? plan.A : -1;
        stopB[i] = plan.Bus ? plan.B : -1;
    }

    private static int PickZone(float[] cum, int zc, ref Pcg32 rng)
    {
        float total = cum[zc];
        float x = rng.NextFloat01() * total;
        for (int z = 0; z < zc; z++)
        {
            if (cum[z + 1] > cum[z] && x < cum[z + 1])
            {
                return z;
            }
        }

        for (int z = zc - 1; z >= 0; z--)
        {
            if (cum[z + 1] > cum[z])
            {
                return z;
            }
        }

        return -1;
    }

    /// <summary>Компонента связности тротуаров сегмента или -1, если у него нет тротуаров.</summary>
    private static int SegmentComponent(PedNetwork pn, int seg)
    {
        for (int side = 0; side < 2; side++)
        {
            pn.SideEdges(seg, side, out int first, out int count);
            if (count > 0)
            {
                return pn.PedComp[pn.Edges[first].A];
            }
        }

        return -1;
    }

    /// <summary>
    /// Случайная точка на тротуаре зоны <paramref name="zone"/> (или любого сегмента, если зоны нет): полоса появления зоны -> сегмент,
    /// сторона, ось <c>s</c> равномерно в <c>[5 м, Len - 5 м]</c> -> ребро и смещение от <c>A</c>, мм.
    /// </summary>
    internal static bool PickPoint(World w, int zone, ref Pcg32 rng, out int edge, out int sMm)
    {
        NetworkStore net = w.Net;
        PedNetwork pn = net.PedNet!;
        edge = -1;
        sMm = 0;
        int seg;
        if (zone >= 0)
        {
            ref readonly ZoneData zd = ref net.Zones[zone];
            int lane = net.ZoneLanes[zd.FirstSpawnLane + (int)rng.NextBelow((uint)zd.SpawnLaneCount)];
            seg = net.Lanes[lane].Segment;
        }
        else
        {
            seg = (int)rng.NextBelow((uint)net.SegmentCount);
        }

        int side = (int)rng.NextBelow(2);
        if (!net.SegmentAlive(seg))   // вне диапазона или мёртвый слот сети с дырами (4.3, T2)
        {
            return false;
        }

        float len = net.Segments[seg].Length;
        float s = len > 12f ? rng.NextRange(5f, len - 5f) : len * 0.5f;
        return pn.PointOnSide(seg, side, s, out edge, out sMm);
    }
}

/// <summary>Способ одного направления поездки: автобус (маршрут, остановки посадки <c>A</c> и высадки <c>B</c> - глобальные индексы) или пешком.</summary>
internal struct Plan
{
    public bool Bus;
    public int Route, A, B;
}

/// <summary>
/// Выбор автобусных ног жителей (pedestrians.md §3, п. 2-3): предрасчёт остановок в радиусе <c>max_walk_to_stop_m</c> от центра каждой зоны (<c>ZoneStops</c>, CSR) и лучшая пара
/// <c>(A, B)</c> на упорядоченную пару зон <c>(дом, работа)</c> - минимум <c>walk(A) + walk(B) + ride + headway/2</c> (walk = евклид центр зоны - остановка / <c>v_nom</c>, ride = сумма
/// расстояний между остановками по маршруту / 8 м/с); при равенстве меньший <c>(r, A, B)</c>. Производное, живёт только в <see cref="ResidentStore.Create"/>, PRNG не использует.
/// </summary>
internal sealed class BusPlanner
{
    private const float RideSpeedMps = 8f;

    private readonly NetworkStore _net;
    private readonly int _zones;
    private readonly float[] _stopX, _stopZ;
    private readonly int[] _stopComp;
    private readonly float[] _cum;            // расстояние вдоль маршрута от его первой остановки, м
    private readonly int[] _zsFirst;          // CSR остановок в радиусе зоны (по возрастанию индекса)
    private readonly int[] _zsList;
    private readonly byte[] _state;           // [hz * zones + wz]: 0 не считали, 1 нет пары, 2 есть
    private readonly Plan[] _plan;
    private readonly float[] _cost;
    private readonly float _vNomMps;
    private readonly float[] _zx, _zz;

    private BusPlanner(World w, bool[] usable, int[] comp)
    {
        _net = w.Net;
        PedNetwork pn = _net.PedNet!;
        PedDesc cfg = w.PedCfg;
        int ns = _net.BusStopCount;
        _zones = _net.ZoneCount;
        _vNomMps = ((cfg.SpeedMinMmPerTick + cfg.SpeedMaxMmPerTick) * 0.5f) * 20f * 0.001f;
        _stopX = new float[ns];
        _stopZ = new float[ns];
        _stopComp = new int[ns];
        _cum = new float[ns];
        for (int s = 0; s < ns; s++)
        {
            _stopComp[s] = -1;
            if (Peds.Stops.AccessPoint(w, s, out int e, out int m))
            {
                int hint = 0;
                pn.Evaluate(e, m, false, ref hint, out _stopX[s], out _stopZ[s], out _);
                _stopComp[s] = pn.PedComp[pn.Edges[e].A];
            }
        }

        for (int r = 0; r < _net.BusRouteCount; r++)
        {
            ref readonly BusRouteData br = ref _net.BusRoutes[r];
            for (int k = 1; k < br.StopCount; k++)
            {
                int a = _net.BusRouteStops[br.FirstStop + k - 1];
                int b = _net.BusRouteStops[br.FirstStop + k];
                _cum[b] = _cum[a] + Dist(_stopX[a], _stopZ[a], _stopX[b], _stopZ[b]);
            }
        }

        float radius = cfg.MaxWalkToStopMm * 0.001f;
        _zx = new float[_zones];
        _zz = new float[_zones];
        _zsFirst = new int[_zones + 1];
        var list = new System.Collections.Generic.List<int>();
        for (int z = 0; z < _zones; z++)
        {
            _zx[z] = _net.Zones[z].Center.X;
            _zz[z] = _net.Zones[z].Center.Z;
            _zsFirst[z] = list.Count;
            if (!usable[z])
            {
                continue;
            }

            for (int s = 0; s < ns; s++)
            {
                if (_stopComp[s] >= 0 && _stopComp[s] == comp[z] && Dist(_zx[z], _zz[z], _stopX[s], _stopZ[s]) <= radius)
                {
                    list.Add(s);
                }
            }
        }

        _zsFirst[_zones] = list.Count;
        _zsList = list.ToArray();
        _state = new byte[_zones * _zones];
        _plan = new Plan[_zones * _zones];
        _cost = new float[_zones * _zones];
    }

    /// <summary>null, если автобусных ног не может быть: модель пассажиров <c>counter</c>, нет маршрутов или остановок, радиус нулевой.</summary>
    public static BusPlanner? TryCreate(World w, bool[] usable, int[] comp)
    {
        NetworkStore net = w.Net;
        if (w.TransitCfg.PassengerModel != PassengerModel.Agents || net.BusRouteCount == 0 || net.BusStopCount == 0 || w.PedCfg.MaxWalkToStopMm <= 0 || w.PedCfg.SpeedMaxMmPerTick <= 0)
        {
            return null;
        }

        return new BusPlanner(w, usable, comp);
    }

    private static float Dist(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Есть ли автобусная пара из зоны <paramref name="hz"/> в зону <paramref name="wz"/>; <paramref name="cost"/> - оценка поездки, с.</summary>
    public bool TryPair(int hz, int wz, out float cost)
    {
        int key = (hz * _zones) + wz;
        if (_state[key] == 0)
        {
            Compute(hz, wz, key);
        }

        cost = _cost[key];
        return _state[key] == 2;
    }

    private void Compute(int hz, int wz, int key)
    {
        _state[key] = 1;
        float best = float.MaxValue;
        for (int ia = _zsFirst[hz]; ia < _zsFirst[hz + 1]; ia++)
        {
            int a = _zsList[ia];
            int route = _net.BusStops[a].Route;
            ref readonly BusRouteData br = ref _net.BusRoutes[route];
            float walkA = Dist(_zx[hz], _zz[hz], _stopX[a], _stopZ[a]) / _vNomMps;
            for (int ib = _zsFirst[wz]; ib < _zsFirst[wz + 1]; ib++)
            {
                int b = _zsList[ib];
                if (_net.BusStops[b].Route != route || (_net.ToolsTouched == 0 ? b <= a || b >= br.FirstStop + br.StopCount : _net.StopPos(b) <= _net.StopPos(a)))
                {
                    continue;   // по ходу линии после a (T9: после правок - по позиции в списке маршрута)
                }

                float c = walkA + (Dist(_zx[wz], _zz[wz], _stopX[b], _stopZ[b]) / _vNomMps) + ((_cum[b] - _cum[a]) / RideSpeedMps) + (br.HeadwayTicks * 0.05f * 0.5f);
                if (c < best)
                {
                    best = c;
                    _plan[key] = new Plan { Bus = true, Route = route, A = a, B = b };
                    _state[key] = 2;
                    _cost[key] = c;
                }
            }
        }
    }

    /// <summary>
    /// Способ на оба направления для выбранной работы: автобус, если пара есть и поездка на автобусе по оценке быстрее, чем пешком по прямой, либо работа дальше <c>walk_only_m</c>;
    /// остановки должны лежать в компоненте связности дома (проверка уровня жителя).
    /// </summary>
    public void Choose(World w, int hz, int wz, int homeComp, out Plan outPlan, out Plan backPlan)
    {
        float walkS = Dist(_zx[hz], _zz[hz], _zx[wz], _zz[wz]);
        bool far = walkS > w.PedCfg.WalkOnlyMm * 0.001f;
        walkS /= _vNomMps;
        outPlan = One(hz, wz, homeComp, far, walkS);
        backPlan = One(wz, hz, homeComp, far, walkS);
    }

    private Plan One(int from, int to, int homeComp, bool far, float walkS)
    {
        if (!TryPair(from, to, out float cost) || (!far && !(cost < walkS)))
        {
            return default;
        }

        Plan p = _plan[(from * _zones) + to];
        return _stopComp[p.A] == homeComp && _stopComp[p.B] == homeComp ? p : default;
    }
}
