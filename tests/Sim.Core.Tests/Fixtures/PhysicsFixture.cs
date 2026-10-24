using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Util;

namespace Sim.Core.Tests.Fixtures;

/// <summary>
/// Фикстура физики (задача F) без World/Simulation: сеть из NetworkBuilder + VehicleStore + LaneOccupancy и ручной
/// прогон фаз 5-8. Прямая дорога (без connector'ов - концы полос это тупики) и однонаправленное кольцо из сегментов.
/// </summary>
internal sealed class PhysicsFixture
{
    public const float Limit = 15f;
    public const int Car = 0;
    public const int Slow = 1;

    public readonly NetworkStore Net;
    public readonly VehicleStore V;
    public readonly LaneOccupancy Occ;
    public readonly VehicleTypeDesc[] Types;
    public readonly SimEvent[] Events = new SimEvent[256];
    public readonly uint[] NodeThroughput;
    public PhysicsParams P;
    public RouteAccess Routes = RouteAccess.None;
    public SimCounters Counters;
    public int EventCount;
    public long Tick;
    private readonly float[] _pinS;
    private bool _anyPinned;

    /// <summary>Фиксирует машину на месте (неподвижное препятствие): после каждой интеграции S возвращается, v = 0.</summary>
    public void Pin(int i)
    {
        _pinS[i] = V.S[i];
        _anyPinned = true;
    }

    private PhysicsFixture(NetworkStore net, int maxVehicles)
    {
        Net = net;
        V = new VehicleStore(maxVehicles);
        Occ = new LaneOccupancy(V, net.Lanes.Length);
        Types = new[] { CarType(), SlowType() };
        NodeThroughput = new uint[net.Nodes.Length];
        _pinS = new float[maxVehicles];
        Array.Fill(_pinS, float.NaN);
        P = new PhysicsParams(PhysicsParams.DefaultDt, 0.5f, 10, 0.1f, PhysicsParams.DefaultLaneWidth);
    }

    /// <summary>Единственный маршрут с индексом 0 из переданных шагов.</summary>
    public void SetSingleRoute(RouteStep[] steps) => Routes = new RouteAccess(new int[1], new[] { steps.Length }, steps);

    public int LaneCount => Net.LaneCount;

    public static VehicleTypeDesc CarType() => new()
    {
        Category = VehicleCategory.Car,
        Length = 4.5f,
        Width = 1.8f,
        MaxSpeed = 130f / 3.6f,
        SpeedFactor = 1f,
        Accel = 1f,
        ComfortDecel = 1.5f,
        HeadwayTime = 1.2f,
        MinGap = 2f,
        Delta = 4f,
        Politeness = 0.3f,
        LaneChangeThreshold = 0.2f,
        SafeDecel = 4f,
        RightBias = 0.3f,
        LaneChangeDurationTicks = 40,
    };

    /// <summary>Медленный тип: желаемая скорость вдвое ниже лимита, длиннее (грузовик).</summary>
    public static VehicleTypeDesc SlowType()
    {
        VehicleTypeDesc t = CarType();
        t.Length = 8f;
        t.SpeedFactor = 0.4f;
        return t;
    }

    private static SimCapacities Caps(int nodes, int segs, int lanes) => new()
    {
        MaxNodes = nodes,
        MaxSegments = segs,
        MaxLanes = lanes,
        MaxLanePoints = 4096 + (lanes * 12),
        MaxZones = 1,
        MaxBusRoutes = 1,
        MaxBusStops = 1,
        MaxBusStopsPerRoute = 1,
    };

    /// <summary>Прямая дорога длиной <paramref name="laneLength"/> по оси X, lanes полос в каждую сторону, без connector'ов.</summary>
    public static PhysicsFixture Straight(float laneLength = 2000f, int lanes = 2, int maxVehicles = 256)
    {
        var net = new NetworkStore(Caps(8, 8, 32));
        var b = new NetworkBuilder(net);
        float setback = b.Options.NodeSetback;
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(laneLength + (2 * setback), 0), lanes, lanes, Limit, RoadKind.Street, out _));
        return new PhysicsFixture(net, maxVehicles);
    }

    /// <summary>Узел посередине: два одинаковых односторонних сегмента (lanes полос) и connector'ы в среднем узле.</summary>
    public static PhysicsFixture TwoSegments(float laneLength, int lanes, out int firstSegLane, out int secondSegLane)
    {
        var net = new NetworkStore(Caps(8, 8, 64));
        var b = new NetworkBuilder(net);
        float setback = b.Options.NodeSetback;
        float step = laneLength + (2 * setback);
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(0, 0), new Vec2(step, 0), lanes, 0, Limit, RoadKind.Street, out int s0));
        Assert.Equal(NetworkStatus.Ok, b.AddSegment(new Vec2(step, 0), new Vec2(2 * step, 0), lanes, 0, Limit, RoadKind.Street, out int s1));
        Assert.Equal(NetworkStatus.Ok, b.BuildConnectorsEx(b.FindNode(new Vec2(step, 0))));
        firstSegLane = net.Segments[s0].FirstLaneAB;
        secondSegLane = net.Segments[s1].FirstLaneAB;
        return new PhysicsFixture(net, 64);
    }

    /// <summary>Однонаправленное кольцо из <paramref name="nodes"/> сегментов радиуса <paramref name="radius"/>, lanes полос.</summary>
    public static PhysicsFixture Ring(int nodes, float radius, int lanes, int maxVehicles)
    {
        var net = new NetworkStore(Caps(nodes + 2, nodes + 2, 4 + (nodes * lanes * 4)));
        var b = new NetworkBuilder(net);
        for (int i = 0; i < nodes; i++)
        {
            double a = 2.0 * Math.PI * i / nodes;
            Assert.Equal(NetworkStatus.Ok, b.AddNode(new Vec2((float)(radius * Math.Cos(a)), (float)(radius * Math.Sin(a))), NodeControl.None, out _));
        }

        for (int i = 0; i < nodes; i++)
        {
            Assert.Equal(NetworkStatus.Ok, b.AddSegment(i, (i + 1) % nodes, lanes, 0, Limit, RoadKind.Street, out _));
        }

        Assert.Equal(NetworkStatus.Ok, b.BuildAllConnectorsEx());
        return new PhysicsFixture(net, maxVehicles);
    }

    public int RingSegmentLane(int segment, int laneNo) => Net.Segments[segment].FirstLaneAB + laneNo;

    /// <summary>Длина полосы + её connector'а: период кольца для одной полосы.</summary>
    public float RingPeriod()
    {
        int lane = Net.Segments[0].FirstLaneAB;
        int conn = Net.LaneNext[Net.Lanes[lane].FirstNext];
        return Net.Lanes[lane].Length + Net.Lanes[conn].Length;
    }

    public int Spawn(int lane, float s, float speed, int type = Car)
    {
        Assert.True(V.TrySpawn(out VehicleHandle h));
        int i = h.Index;
        V.S[i] = s;
        V.Speed[i] = speed;
        V.Type[i] = (byte)type;
        V.Length[i] = Types[type].Length;
        V.DesiredSpeed[i] = Idm.DesiredSpeed(Types[type], Net.Lanes[lane].SpeedLimit);
        V.SpawnTick[i] = Tick;
        Occ.InsertSorted(lane, i);
        return i;
    }

    /// <summary>Равномерно расставляет <paramref name="count"/> машин вдоль цепочки полос+connector'ов кольца на полосе laneNo.</summary>
    public void FillRing(int laneNo, int count, float speed, int type = Car, int offsetIndex = 0)
    {
        int segs = Net.SegmentCount;
        float period = RingPeriod();
        float total = segs * period;
        float spacing = total / count;
        for (int k = count - 1; k >= 0; k--)
        {
            float pos = ((k * spacing) + (offsetIndex * 0.5f * spacing)) % total;
            int seg = (int)(pos / period);
            float off = pos - (seg * period);
            int lane = RingSegmentLane(seg, laneNo);
            float len = Net.Lanes[lane].Length;
            if (off >= len)
            {
                off -= len;
                lane = Net.LaneNext[Net.Lanes[lane].FirstNext];
            }

            Spawn(lane, off, speed, type);
        }
    }

    /// <summary>Фазы 5-8 одного тика. Без аллокаций.</summary>
    public void Step()
    {
        BehaviourPhase.Run(Net, V, Occ, Routes, Types, P, Tick);
        IntegratePhase.Run(Net, V, Occ, Routes, Types, P, ref Counters);
        if (_anyPinned)
        {
            for (int i = 0; i < V.HighWater; i++)
            {
                if (!float.IsNaN(_pinS[i]) && V.Pool.IsAlive(i))
                {
                    V.S[i] = _pinS[i];
                    V.Speed[i] = 0f;
                }
            }
        }

        TransitionPhase.Run(Net, V, Occ, Routes, Types, P, NodeThroughput, ref Counters);
        EventCount = 0;
        ArrivalPhase.Run(V, Occ, Tick, Events, ref EventCount, ref Counters);
        Tick++;
    }

    public void Run(int ticks)
    {
        for (int n = 0; n < ticks; n++)
        {
            Step();
        }
    }

    /// <summary>
    /// Проверка инвариантов после тика: в списках всех полос (ghost включительно) S не убывает от хвоста к голове, зазор
    /// <c>s_leader - L_leader - s &gt;= 0</c> для каждой соседней пары, счётчики сходятся; зазор от головы до хвоста следующей
    /// полосы (по умолчанию-цепочке) неотрицателен. Возвращает описание нарушения или null.
    /// </summary>
    public string? CheckInvariants()
    {
        int members = 0;
        for (int lane = 0; lane < LaneCount; lane++)
        {
            int count = 0;
            int prev = -1;
            for (int m = Occ.LaneTail[lane]; m != -1; prev = m, m = AheadOf(m, lane))
            {
                count++;
                if (prev != -1)
                {
                    float gap = V.S[m] - V.Length[m] - V.S[prev];
                    if (gap < 0f)
                    {
                        return $"tick {Tick}: lane {lane}: vehicle {prev} (s={V.S[prev]:F3}) overlaps leader {m} (s={V.S[m]:F3}, L={V.Length[m]}) gap={gap:F4}";
                    }
                }

                if (count > V.Capacity + 1)
                {
                    return $"tick {Tick}: lane {lane}: cycle in list";
                }
            }

            if (prev != Occ.LaneHead[lane])
            {
                return $"tick {Tick}: lane {lane}: head mismatch";
            }

            if (count != Occ.LaneCount[lane])
            {
                return $"tick {Tick}: lane {lane}: LaneCount {Occ.LaneCount[lane]} != walked {count}";
            }

            members += count;

            int head = Occ.LaneHead[lane];
            if (head != -1 && Net.Lanes[lane].Kind == LaneKind.Connector)
            {
                int next = Net.Lanes[lane].ToLane;
                int tail = Occ.LaneTail[next];
                if (tail != -1 && tail != head)
                {
                    float gap = Net.Lanes[lane].Length - V.S[head] + V.S[tail] - V.Length[tail];
                    if (gap < 0f)
                    {
                        return $"tick {Tick}: connector {lane}->{next}: gap {gap:F4} between {head} and {tail}";
                    }
                }
            }
            else if (head != -1 && Net.Lanes[lane].NextCount > 0 && V.Lane[head] == lane)
            {
                int next = Net.LaneNext[Net.Lanes[lane].FirstNext];
                int tail = Occ.LaneTail[next];
                if (tail != -1 && tail != head)
                {
                    float gap = Net.Lanes[lane].Length - V.S[head] + V.S[tail] - V.Length[tail];
                    if (gap < 0f)
                    {
                        return $"tick {Tick}: lane {lane}->{next}: gap {gap:F4} between {head} and {tail}";
                    }
                }
            }
        }

        int expected = V.AliveCount;
        int ghosts = 0;
        for (int i = 0; i < V.HighWater; i++)
        {
            if (V.Pool.IsAlive(i) && V.GhostLane[i] != -1)
            {
                ghosts++;
            }
        }

        return members == expected + ghosts ? null : $"tick {Tick}: members {members} != alive {expected} + ghosts {ghosts}";
    }

    public ulong Hash()
    {
        var h = Fnv1a64.Create();
        for (int i = 0; i < V.HighWater; i++)
        {
            if (!V.Pool.IsAlive(i))
            {
                continue;
            }

            h.Add(i);
            h.Add(V.Lane[i]);
            h.Add(V.GhostLane[i]);
            h.Add(V.S[i]);
            h.Add(V.Speed[i]);
        }

        return h.Value;
    }

    public float MeanSpeed()
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < V.HighWater; i++)
        {
            if (V.Pool.IsAlive(i))
            {
                sum += V.Speed[i];
                n++;
            }
        }

        return n == 0 ? 0f : (float)(sum / n);
    }

    private int AheadOf(int m, int lane) => V.Lane[m] == lane ? V.Ahead[m] : V.GhostAhead[m];
}
