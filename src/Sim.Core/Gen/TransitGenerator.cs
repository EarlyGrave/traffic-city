using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Gen;

/// <summary>Результат генерации автобусных маршрутов (core-api §6.6). Выделяется один раз.</summary>
internal sealed class GenTransit
{
    public readonly BusRouteData[] Routes;
    public readonly BusStopData[] Stops;
    public readonly int[] RouteStops;       // подряд: RouteStops[k] == k (индексы остановок маршрута идут подряд)
    public readonly int MaxStopsPerRoute;
    public readonly int[] PathFirst;        // по маршруту: диапазон рёбер в PathSeg/PathDir
    public readonly int[] PathCount;
    public int[] PathSeg = new int[256];    // сегменты маршрутов подряд (растёт при генерации)
    public byte[] PathDir = new byte[256];  // 0 — проезд A→B, 1 — B→A

    public GenTransit(int maxRoutes, int maxStops, int maxStopsPerRoute)
    {
        Routes = new BusRouteData[Math.Max(0, maxRoutes)];
        Stops = new BusStopData[Math.Max(0, maxStops)];
        MaxStopsPerRoute = Math.Max(0, maxStopsPerRoute);
        RouteStops = new int[Stops.Length];
        PathFirst = new int[Routes.Length];
        PathCount = new int[Routes.Length];
    }

    public int RouteCount { get; internal set; }

    public int StopCount { get; internal set; }

    public int PathUsed { get; internal set; }
}

internal struct TransitGenResult
{
    public int Routes, Stops, BayStops, CurbStops, RoutesFailed, RoutesBelowMinStops;
}

/// <summary>Автобусные маршруты (city-generator §6): прогулка по артериям с уличными хвостами, стрим Transit.</summary>
internal static class TransitGenerator
{
    private const float Deg60 = MathF.PI / 3f;
    private const float Deg45 = MathF.PI / 4f;
    private const float Deg135 = 3f * MathF.PI / 4f;

    public static CreateStatus Generate(
        in TransitDesc transit,
        float width,
        float height,
        NetworkStore net,
        GenTransit store,
        ref Pcg32 rng,
        out TransitGenResult result)
    {
        result = default;
        store.RouteCount = 0;
        store.StopCount = 0;
        store.PathUsed = 0;
        if (transit.Routes <= 0)
        {
            return CreateStatus.Ok;
        }

        int stopsMin = transit.StopsPerRouteMin;
        int stopsMax = transit.StopsPerRouteMax;
        if (transit.Routes > store.Routes.Length || stopsMin < 2 || stopsMax < stopsMin || !(transit.StopSpacing > 0f) ||
            transit.HeadwayTicks <= 0)
        {
            return CreateStatus.InvalidTransit;
        }

        var w = new Walker(net, width, height);
        if (w.StartCount == 0)
        {
            result.RoutesFailed = transit.Routes;
            return CreateStatus.Ok;
        }

        int perRouteCap = Math.Min(stopsMax, store.MaxStopsPerRoute);
        if (perRouteCap < 2)
        {
            return CreateStatus.InvalidTransit;
        }

        for (int r = 0; r < transit.Routes; r++)
        {
            if (!w.BuildRoute(transit, ref rng))
            {
                result.RoutesFailed++;
                continue;
            }

            if (store.RouteCount >= store.Routes.Length)
            {
                return CreateStatus.InvalidTransit;
            }

            int firstStop = store.StopCount;
            int placed = PlaceStops(net, w, in transit, store, perRouteCap, stopsMax, store.RouteCount, ref result);
            if (placed < 0)
            {
                return CreateStatus.InvalidTransit;
            }

            if (placed < stopsMin)
            {
                result.RoutesBelowMinStops++;
            }

            if (store.PathUsed + w.Count > store.PathSeg.Length)
            {
                int newLen = Math.Max(store.PathSeg.Length * 2, store.PathUsed + w.Count);
                Array.Resize(ref store.PathSeg, newLen);
                Array.Resize(ref store.PathDir, newLen);
            }

            Array.Copy(w.Seg, 0, store.PathSeg, store.PathUsed, w.Count);
            Array.Copy(w.Dir, 0, store.PathDir, store.PathUsed, w.Count);
            store.PathFirst[store.RouteCount] = store.PathUsed;
            store.PathCount[store.RouteCount] = w.Count;
            store.PathUsed += w.Count;
            store.Routes[store.RouteCount] = new BusRouteData
            {
                FirstStop = firstStop,
                StopCount = placed,
                HeadwayTicks = transit.HeadwayTicks,
                VehicleType = transit.BusType,
                ActiveBuses = 0,
                LastDispatchTick = -1,
                Generation = 1,
            };
            store.RouteCount++;
        }

        if (transit.Bidirectional)
        {
            CreateStatus bs = AddReverseRoutes(net, store, ref result);
            if (bs != CreateStatus.Ok)
            {
                return bs;
            }
        }

        result.Routes = store.RouteCount;
        result.Stops = store.StopCount;
        return CreateStatus.Ok;
    }

    /// <summary>
    /// <c>transit.bidirectional</c> (pedestrians.md §17 п. 3): к каждому из <c>n</c> прямых маршрутов добавляется парный обратный (индекс <c>n + r</c>) - те же сегменты и остановки
    /// в обратном порядке на встречной полосе (<c>S' = Len - S</c>, тот же вид остановки). Прямые маршруты и поток PRNG не меняются. Если на каком-то сегменте нет встречной
    /// полосы, обратный маршрут пропускается (<c>RoutesFailed++</c>); не хватает ёмкости - <c>InvalidTransit</c>.
    /// </summary>
    private static CreateStatus AddReverseRoutes(NetworkStore net, GenTransit store, ref TransitGenResult result)
    {
        int fwd = store.RouteCount;
        for (int r = 0; r < fwd; r++)
        {
            ref readonly BusRouteData br = ref store.Routes[r];
            int n = br.StopCount;
            bool ok = n >= 2;
            for (int k = 0; k < n && ok; k++)
            {
                ref readonly BusStopData st = ref store.Stops[br.FirstStop + k];
                ok = OppositeLane(net, st.Lane) >= 0;
            }

            if (!ok)
            {
                result.RoutesFailed++;
                continue;
            }

            if (store.RouteCount >= store.Routes.Length || store.StopCount + n > store.Stops.Length)
            {
                return CreateStatus.InvalidTransit;
            }

            int pc = store.PathCount[r];
            if (store.PathUsed + pc > store.PathSeg.Length)
            {
                int newLen = Math.Max(store.PathSeg.Length * 2, store.PathUsed + pc);
                Array.Resize(ref store.PathSeg, newLen);
                Array.Resize(ref store.PathDir, newLen);
            }

            int ri = store.RouteCount;
            int first = store.StopCount;
            for (int k = n - 1; k >= 0; k--)
            {
                BusStopData st = store.Stops[br.FirstStop + k];
                int opp = OppositeLane(net, st.Lane);
                float len = net.Segments[net.Lanes[st.Lane].Segment].Length;
                st.Lane = opp;
                st.S = MathF.Max(0f, len - st.S);
                st.Route = ri;
                st.Waiting = 0;
                st.LastArrivalTick = -1;
                st.LastDepartTick = -1;
                int idx = store.StopCount++;
                store.Stops[idx] = st;
                store.RouteStops[idx] = idx;
                if (st.Kind == BusStopKind.Bay)
                {
                    result.BayStops++;
                }
                else
                {
                    result.CurbStops++;
                }
            }

            int pf = store.PathFirst[r];
            for (int k = 0; k < pc; k++)
            {
                store.PathSeg[store.PathUsed + k] = store.PathSeg[pf + pc - 1 - k];
                store.PathDir[store.PathUsed + k] = (byte)(1 - store.PathDir[pf + pc - 1 - k]);
            }

            store.PathFirst[ri] = store.PathUsed;
            store.PathCount[ri] = pc;
            store.PathUsed += pc;
            store.Routes[ri] = new BusRouteData
            {
                FirstStop = first,
                StopCount = n,
                HeadwayTicks = br.HeadwayTicks,
                VehicleType = br.VehicleType,
                ActiveBuses = 0,
                LastDispatchTick = -1,
                Generation = 1,
            };
            store.RouteCount++;
        }

        return CreateStatus.Ok;
    }

    /// <summary>Встречная полоса того же сегмента с тем же номером от правого края или -1 (нет встречных полос).</summary>
    private static int OppositeLane(NetworkStore net, int lane)
    {
        ref readonly LaneData ld = ref net.Lanes[lane];
        if (ld.Segment < 0)
        {
            return -1;
        }

        ref readonly SegmentData sd = ref net.Segments[ld.Segment];
        int count = ld.Direction == 0 ? sd.LanesBA : sd.LanesAB;
        if (count <= 0)
        {
            return -1;
        }

        return (ld.Direction == 0 ? sd.FirstLaneBA : sd.FirstLaneAB) + Math.Min(ld.LaneNo, count - 1);
    }

    private static int PlaceStops(
        NetworkStore net,
        Walker w,
        in TransitDesc transit,
        GenTransit store,
        int perRouteCap,
        int stopsMax,
        int routeIndex,
        ref TransitGenResult result)
    {
        int n = w.Count;
        int start = store.StopCount;
        float effSpacing = MathF.Max(transit.StopSpacing, w.Length / (stopsMax - 1));
        float cum = 0f;
        float prevPos = 0f;
        int placed = 0;
        for (int k = 0; k < n; k++)
        {
            int seg = w.Seg[k];
            float len = net.Segments[seg].Length;
            float s;
            if (k == 0)
            {
                s = MathF.Min(CityGenerator.TerminalStopS, len * 0.5f);
            }
            else if (k == n - 1)
            {
                s = MathF.Max(len - CityGenerator.TerminalStopS, len * 0.5f);
            }
            else
            {
                s = len * 0.5f;
                if (placed >= perRouteCap - 1 || (cum + s) - prevPos < CityGenerator.StopSpacingTolerance * effSpacing)
                {
                    cum += len;
                    continue;
                }
            }

            if (store.StopCount >= store.Stops.Length)
            {
                return -1;
            }

            ref readonly SegmentData sd = ref net.Segments[seg];
            BusStopKind kind = transit.StopKind switch
            {
                StopKindMode.Curb => BusStopKind.Curb,
                StopKindMode.Bay => BusStopKind.Bay,
                _ => sd.Kind == RoadKind.Arterial ? BusStopKind.Bay : BusStopKind.Curb,
            };
            int lane = w.Dir[k] == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            int idx = store.StopCount;
            store.Stops[idx] = new BusStopData
            {
                Lane = lane,
                S = s,
                Kind = kind,
                Route = routeIndex,
                Waiting = 0,
                LastArrivalTick = -1,
                LastDepartTick = -1,
            };
            store.RouteStops[idx] = idx;
            store.StopCount++;
            if (kind == BusStopKind.Bay)
            {
                result.BayStops++;
            }
            else
            {
                result.CurbStops++;
            }

            prevPos = cum + s;
            cum += len;
            placed++;
        }

        return store.StopCount - start;
    }

    /// <summary>Рабочие буферы и обход графа для маршрутов (чтение NetworkStore).</summary>
    private sealed class Walker
    {
        private readonly NetworkStore _net;
        private readonly float _halfW;
        private readonly float _halfH;
        private readonly int[] _start;
        private readonly int[] _visit;
        private readonly int[] _curSeg;
        private readonly byte[] _curDir;
        private readonly int[] _bestSeg;
        private readonly byte[] _bestDir;
        private int _stamp;

        public Walker(NetworkStore net, float width, float height)
        {
            _net = net;
            _halfW = width * 0.5f;
            _halfH = height * 0.5f;
            int segs = Math.Max(1, net.SegmentCount);
            _visit = new int[segs];
            _curSeg = new int[segs];
            _curDir = new byte[segs];
            _bestSeg = new int[segs];
            _bestDir = new byte[segs];
            _start = new int[net.NodeCount];
            for (int n = 0; n < net.NodeCount; n++)
            {
                if (!IsBorder(n) && !IsArterialNode(n) && net.Nodes[n].ArmCount >= 3)
                {
                    _start[StartCount++] = n;
                }
            }

            Seg = _bestSeg;
            Dir = _bestDir;
        }

        public int StartCount { get; }

        public int Count { get; private set; }

        public float Length { get; private set; }

        /// <summary>Результат последнего удачного <see cref="BuildRoute"/>.</summary>
        public int[] Seg { get; }

        public byte[] Dir { get; }

        public bool BuildRoute(in TransitDesc t, ref Pcg32 rng)
        {
            int bestCount = 0;
            float bestLen = -1f;
            for (int a = 0; a < CityGenerator.RouteAttempts; a++)
            {
                _stamp++;
                int targetStops = rng.NextInt(t.StopsPerRouteMin, t.StopsPerRouteMax + 1);
                float targetLen = targetStops * t.StopSpacing;
                int node = _start[rng.NextInt(0, StartCount)];
                float heading = rng.NextInt(0, 4) * (MathF.PI / 2f);
                int prev = -1;
                int count = 0;
                float len = 0f;
                bool ok = true;

                // хвост 1: до первой артерии
                int t1 = 0;
                while (!IsArterialNode(node))
                {
                    int seg = t1 >= CityGenerator.Tail1MaxEdges ? -1 : Straight(node, prev, heading, -1, Deg60);
                    if (seg < 0)
                    {
                        ok = false;
                        break;
                    }

                    Move(ref node, ref prev, ref heading, ref count, ref len, seg);
                    t1++;
                }

                if (!ok)
                {
                    continue;
                }

                // артериальная прогулка
                while (len < targetLen && !IsBorder(node) && count < _curSeg.Length)
                {
                    float u = rng.NextFloat01();
                    float side = rng.NextFloat01();
                    int straight = -1;
                    int right = -1;
                    int left = -1;
                    ref readonly NodeData nd = ref _net.Nodes[node];
                    for (int k = 0; k < nd.ArmCount; k++)
                    {
                        int seg = _net.NodeArms[nd.FirstArm + k];
                        if (seg == prev || _visit[seg] == _stamp || _net.Segments[seg].Kind != RoadKind.Arterial)
                        {
                            continue;
                        }

                        float d = Delta(Angle(node, seg), heading);
                        if (MathF.Abs(d) <= Deg45)
                        {
                            if (straight < 0)
                            {
                                straight = seg;
                            }
                        }
                        else if (d > Deg45 && d < Deg135)
                        {
                            if (left < 0)
                            {
                                left = seg;
                            }
                        }
                        else if (d < -Deg45 && d > -Deg135)
                        {
                            if (right < 0)
                            {
                                right = seg;
                            }
                        }
                    }

                    int pick;
                    if (straight >= 0 && (u < CityGenerator.StraightProb || (right < 0 && left < 0)))
                    {
                        pick = straight;
                    }
                    else if (side < 0.5f && right >= 0)
                    {
                        pick = right;
                    }
                    else if (left >= 0)
                    {
                        pick = left;
                    }
                    else
                    {
                        pick = right;
                    }

                    if (pick < 0)
                    {
                        break;
                    }

                    Move(ref node, ref prev, ref heading, ref count, ref len, pick);
                }

                // хвост 2 по улицам
                int tt = rng.NextInt(0, 4);
                {
                    ref readonly NodeData nd = ref _net.Nodes[node];
                    int candCount = 0;
                    for (int k = 0; k < nd.ArmCount; k++)
                    {
                        int seg = _net.NodeArms[nd.FirstArm + k];
                        if (seg != prev && _visit[seg] != _stamp && _net.Segments[seg].Kind == RoadKind.Street)
                        {
                            candCount++;
                        }
                    }

                    if (candCount > 0)
                    {
                        int want = tt % candCount;
                        int pick = -1;
                        int idx = 0;
                        for (int k = 0; k < nd.ArmCount; k++)
                        {
                            int seg = _net.NodeArms[nd.FirstArm + k];
                            if (seg != prev && _visit[seg] != _stamp && _net.Segments[seg].Kind == RoadKind.Street)
                            {
                                if (idx == want)
                                {
                                    pick = seg;
                                    break;
                                }

                                idx++;
                            }
                        }

                        Move(ref node, ref prev, ref heading, ref count, ref len, pick);
                        for (int e = 1; e < CityGenerator.Tail2Edges; e++)
                        {
                            int seg = Straight(node, prev, heading, (int)RoadKind.Street, Deg60);
                            if (seg < 0)
                            {
                                break;
                            }

                            Move(ref node, ref prev, ref heading, ref count, ref len, seg);
                        }
                    }
                }

                if (len >= CityGenerator.MinRouteLenShare * targetLen && count >= 3)
                {
                    Commit(count, len);
                    return true;
                }

                if (count >= 2 && len > bestLen)
                {
                    bestLen = len;
                    bestCount = count;
                    Array.Copy(_curSeg, _bestSeg, count);
                    Array.Copy(_curDir, _bestDir, count);
                }
            }

            if (bestCount >= 2)
            {
                Count = bestCount;
                Length = bestLen;
                return true;
            }

            return false;
        }

        private void Commit(int count, float len)
        {
            Array.Copy(_curSeg, _bestSeg, count);
            Array.Copy(_curDir, _bestDir, count);
            Count = count;
            Length = len;
        }

        private void Move(ref int node, ref int prev, ref float heading, ref int count, ref float len, int seg)
        {
            ref readonly SegmentData sd = ref _net.Segments[seg];
            heading = Angle(node, seg);
            _curSeg[count] = seg;
            _curDir[count] = (byte)(sd.NodeA == node ? 0 : 1);
            count++;
            _visit[seg] = _stamp;
            len += sd.Length;
            node = sd.NodeA == node ? sd.NodeB : sd.NodeA;
            prev = seg;
        }

        /// <summary>Рукав с наименьшим |Δугла| к heading среди непосещённых и ≠ prev; принимается при |Δ| ≤ maxDelta.</summary>
        private int Straight(int node, int prev, float heading, int kindFilter, float maxDelta)
        {
            ref readonly NodeData nd = ref _net.Nodes[node];
            int best = -1;
            float bestD = float.MaxValue;
            for (int k = 0; k < nd.ArmCount; k++)
            {
                int seg = _net.NodeArms[nd.FirstArm + k];
                if (seg == prev || _visit[seg] == _stamp || (kindFilter >= 0 && (int)_net.Segments[seg].Kind != kindFilter))
                {
                    continue;
                }

                float d = MathF.Abs(Delta(Angle(node, seg), heading));
                if (d < bestD)
                {
                    bestD = d;
                    best = seg;
                }
            }

            return best >= 0 && bestD <= maxDelta ? best : -1;
        }

        private float Angle(int node, int seg)
        {
            ref readonly SegmentData sd = ref _net.Segments[seg];
            int other = sd.NodeA == node ? sd.NodeB : sd.NodeA;
            Vec2 a = _net.Nodes[node].Position;
            Vec2 b = _net.Nodes[other].Position;
            return MathF.Atan2(b.Z - a.Z, b.X - a.X);
        }

        private static float Delta(float angle, float heading)
        {
            float d = angle - heading;
            while (d > MathF.PI)
            {
                d -= 2f * MathF.PI;
            }

            while (d <= -MathF.PI)
            {
                d += 2f * MathF.PI;
            }

            return d;
        }

        private bool IsBorder(int n)
        {
            Vec2 p = _net.Nodes[n].Position;
            return MathF.Abs(p.X) >= _halfW - 0.5f || MathF.Abs(p.Z) >= _halfH - 0.5f;
        }

        private bool IsArterialNode(int n)
        {
            ref readonly NodeData nd = ref _net.Nodes[n];
            for (int k = 0; k < nd.ArmCount; k++)
            {
                if (_net.Segments[_net.NodeArms[nd.FirstArm + k]].Kind == RoadKind.Arterial)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
