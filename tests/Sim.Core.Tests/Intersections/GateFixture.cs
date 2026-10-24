using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Intersections;

/// <summary>Рукав узла фикстуры: направление от центра, число полос в каждую сторону, вид дороги.</summary>
internal readonly struct Arm
{
    public readonly float Dx, Dz;
    public readonly int Lanes;
    public readonly RoadKind Kind;

    public Arm(float dx, float dz, int lanes, RoadKind kind = RoadKind.Street)
    {
        Dx = dx;
        Dz = dz;
        Lanes = lanes;
        Kind = kind;
    }

    public static Arm E(int lanes, RoadKind kind = RoadKind.Street) => new(1, 0, lanes, kind);
    public static Arm W(int lanes, RoadKind kind = RoadKind.Street) => new(-1, 0, lanes, kind);
    public static Arm N(int lanes, RoadKind kind = RoadKind.Street) => new(0, -1, lanes, kind);
    public static Arm S(int lanes, RoadKind kind = RoadKind.Street) => new(0, 1, lanes, kind);
}

/// <summary>
/// Один узел с воротами в (0, 0) и рукавами длиной <see cref="ArmLength"/> (intersections.md §11). Сегмент i = рукав i,
/// A = центр, B = конец рукава: полосы A→B - выезд из узла, B→A - въезд в узел.
/// </summary>
internal static class GateFixture
{
    public const float ArmLength = 300f;
    public const float SpeedLimit = 13.9f;

    public static ScenarioDesc Node(NodeControl control, params Arm[] arms)
    {
        ScenarioDesc d = TestScenarios.Line();
        SimCapacities c = d.Capacities;
        c.MaxVehicles = 512;
        c.MaxRoutes = 512;
        c.MaxNodes = 16;
        c.MaxSegments = 16;
        c.MaxLanes = 256;
        c.MaxLanePoints = 8192;
        c.MaxSignalizedNodes = 2;
        c.EventCapacity = 1024;
        c.CommandQueueCapacity = 32;
        d.Capacities = c;
        var nodes = new List<NodeDesc> { new() { X = 0, Z = 0, Control = control } };
        var segs = new List<SegmentDesc>();
        for (int i = 0; i < arms.Length; i++)
        {
            nodes.Add(new NodeDesc { X = ArmLength * arms[i].Dx, Z = ArmLength * arms[i].Dz, Control = NodeControl.None });
            segs.Add(new SegmentDesc { A = 0, B = i + 1, LanesAB = arms[i].Lanes, LanesBA = arms[i].Lanes, SpeedLimit = SpeedLimit, Kind = arms[i].Kind });
        }

        d.Network = new NetworkDesc { Nodes = nodes.ToArray(), Segments = segs.ToArray() };
        d.Rules.DefaultYellowTicks = 60;
        d.Rules.DefaultAllRedTicks = 20;
        d.VehicleTypes = new[] { Step2.RealCar() };
        return d;
    }

    /// <summary>Полоса въезда в узел с рукава <paramref name="arm"/> (LaneNo 0 - правая).</summary>
    public static int InLane(World w, int arm, int laneNo = 0) => w.Net.Segments[arm].FirstLaneBA + laneNo;

    /// <summary>Полоса выезда из узла на рукав <paramref name="arm"/>.</summary>
    public static int OutLane(World w, int arm, int laneNo = 0) => w.Net.Segments[arm].FirstLaneAB + laneNo;

    /// <summary>Connector с полосы <paramref name="inLane"/> на рукав <paramref name="toArm"/> (первый по порядку LaneNext) или -1.</summary>
    public static int ConnectorTo(World w, int inLane, int toArm)
    {
        ref readonly LaneData ld = ref w.Net.Lanes[inLane];
        for (int k = 0; k < ld.NextCount; k++)
        {
            int c = w.Net.LaneNext[ld.FirstNext + k];
            if (w.Net.Lanes[w.Net.Lanes[c].ToLane].Segment == toArm)
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>Connector с полосы по манёвру (Straight/Right/Left) или -1.</summary>
    public static int ConnectorBy(World w, int inLane, Maneuver m)
    {
        ref readonly LaneData ld = ref w.Net.Lanes[inLane];
        for (int k = 0; k < ld.NextCount; k++)
        {
            int c = w.Net.LaneNext[ld.FirstNext + k];
            int g = w.Net.Lanes[c].ConflictGroup;
            if (g >= 0 && w.Connectors.Maneuver[g] == (byte)m)
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>Ставит машину на полосу (без спавнера): позиция переднего бампера s, скорость, connector развилки (-1 = случайный STAGE2-выбор).</summary>
    public static int Place(Simulation sim, int lane, float s, float speed, int connector = -1, bool routed = false)
    {
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        Assert.True(v.TrySpawn(out VehicleHandle h));
        int i = h.Index;
        ref readonly VehicleTypeDesc t = ref w.Types[0];
        v.S[i] = s;
        v.Speed[i] = speed;
        v.Length[i] = t.Length;
        v.Type[i] = 0;
        v.DesiredSpeed[i] = Idm.DesiredSpeed(t, w.Net.Lanes[lane].SpeedLimit);
        v.SpawnTick[i] = w.Tick;
        v.DestZone[i] = -1;
        w.Occupancy.InsertSorted(lane, i);
        v.NextConn[i] = TransitionPhase.ChooseConnector(w, i, lane); // инвариант ADR-0016 D6: NextConn == NextLane(i, lane, 1) (без маршрута - connector по умолчанию)
        if (routed && connector >= 0)
        {
            // Без маршрута фазы 4-5 берут connector по умолчанию (PhysicsTypes.DefaultConnector), а не NextConn: настоящий манёвр задаёт только маршрут.
            int dest = w.Net.Lanes[connector].ToLane;
            v.DestLane[i] = dest;
            v.DestS[i] = w.Net.Lanes[dest].Length - 5f;
            Assert.Equal(BuildStatus.Ok, w.Router.Build(lane, s, dest, v.DestS[i], w.Tick, out RouteHandle h2));
            v.Route[i] = h2.Index;
            v.RouteGen[i] = h2.Generation;
            v.RouteCursor[i] = 0;
            v.NextConn[i] = TransitionPhase.ChooseConnector(w, i, lane);
            Assert.Equal(connector, v.NextConn[i]);
        }

        w.Counters.VehiclesSpawned++;
        return i;
    }
}

/// <summary>Источник потока: каждые <c>HeadwayTicks</c> ставит машину в начало полосы, если есть место; иначе ждёт (накапливает долг).</summary>
internal sealed class FlowSource
{
    public int Lane;
    public int HeadwayTicks;
    public int Offset;
    public int Connector = -1;     // -1 = случайный выбор STAGE2
    public bool Routed;            // true: машине строится маршрут через Connector/ToArm (иначе connector по умолчанию - прямо)
    public int ToArm = -1;         // >= 0: connector на этот рукав (приоритет над Connector)
    public int Pending;
    public int Spawned;
    public int Deferred;
}

/// <summary>
/// Драйвер прогона на фикстуре <see cref="GateFixture"/>: источники, стоки (деспаун на полосах выезда), проверки каждого тика
/// (зазоры в списках, I6 на connector'ах, въезд на connector только с допуском, допуск не на красный), статистика ожидания.
/// В горячем цикле не аллоцирует (после первого тика) - пригоден для замера 0 B/тик.
/// </summary>
internal sealed class FlowDriver
{
    public readonly Simulation Sim;
    public readonly World W;
    public readonly List<FlowSource> Sources = new();
    public readonly List<int> SinkLanes = new();
    public float SinkS = 20f;
    public int Exits;
    public long TravelTicksTotal;
    public int MaxWaitTicks;
    public int MaxWaitVehicle = -1;
    public string? GapViolation;
    public string? ConflictViolation;
    public string? EntryWithoutGrant;
    public string? GrantOnRed;
    public readonly int[] PrevLane;
    public readonly byte[] PrevGate;
    public readonly bool[] StoppedNearLine;   // T4: был тик с v < 0.1 в 3.5 м от линии
    public readonly int[] ExitsPerSink;
    public readonly int[] SinkExitsLast;
    public readonly uint[] LastTicketOnLane;   // T7: монотонность билетов на полосе выезда
    public readonly int[] SpawnLane;           // полоса источника машины (-1 = поставлена вручную)
    public readonly long[] TravelTicksByLane;  // Σ времени поездки по полосе источника
    public readonly int[] ExitsByLane;
    public string? TicketOrderViolation;
    public Action<FlowDriver>? OnTick { get; set; }

    public FlowDriver(Simulation sim)
    {
        Sim = sim;
        W = sim.World;
        int cap = W.Vehicles.Capacity;
        PrevLane = new int[cap];
        PrevGate = new byte[cap];
        StoppedNearLine = new bool[cap];
        Array.Fill(PrevLane, -1);
        ExitsPerSink = new int[W.Net.LaneCount];
        SinkExitsLast = new int[W.Net.LaneCount];
        LastTicketOnLane = new uint[W.Net.LaneCount];
        SpawnLane = new int[cap];
        Array.Fill(SpawnLane, -1);
        TravelTicksByLane = new long[W.Net.LaneCount];
        ExitsByLane = new int[W.Net.LaneCount];
    }

    /// <summary>Среднее время поездки (с) машин, стартовавших на полосе <paramref name="lane"/>.</summary>
    public float MeanTravelFrom(int lane) => ExitsByLane[lane] == 0 ? 0f : (float)(TravelTicksByLane[lane] * 0.05 / ExitsByLane[lane]);

    public FlowSource AddSource(int lane, int headwayTicks, int toArm = -1, int offset = 0, int connector = -1, bool routed = false)
    {
        var s = new FlowSource { Lane = lane, HeadwayTicks = headwayTicks, Offset = offset, ToArm = toArm, Connector = connector, Routed = routed };
        Sources.Add(s);
        return s;
    }

    public void AddSink(int lane) => SinkLanes.Add(lane);

    public void Run(int ticks)
    {
        for (int n = 0; n < ticks; n++)
        {
            Tick();
        }
    }

    public void Tick()
    {
        VehicleStore v = W.Vehicles;
        NetworkStore net = W.Net;
        long tick = W.Tick;

        // Источники.
        for (int k = 0; k < Sources.Count; k++)
        {
            FlowSource src = Sources[k];
            if (src.HeadwayTicks > 0 && (tick + src.Offset) % src.HeadwayTicks == 0)
            {
                src.Pending++;
            }

            if (src.Pending > 0)
            {
                if (TryPlace(src))
                {
                    src.Pending--;
                    src.Spawned++;
                }
                else
                {
                    src.Deferred++;
                }
            }
        }

        // Снимок состояния до тика.
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (v.Pool.IsAlive(i))
            {
                PrevLane[i] = v.Lane[i];
                PrevGate[i] = v.GateState[i];
            }
            else
            {
                PrevLane[i] = -1;
                PrevGate[i] = 0;
                StoppedNearLine[i] = false;
            }
        }

        Sim.Step(1);

        // Проверки.
        hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            int lane = v.Lane[i];
            ref readonly LaneData ld = ref net.Lanes[lane];
            if (v.WaitTicks[i] > MaxWaitTicks)
            {
                MaxWaitTicks = v.WaitTicks[i];
                MaxWaitVehicle = i;
            }

            if (ld.Kind == LaneKind.Normal)
            {
                if (v.Speed[i] < 0.1f && ld.Length - v.S[i] <= 3.5f)
                {
                    StoppedNearLine[i] = true;
                }

                bool wasGranted = (PrevGate[i] & GateRules.Granted) != 0;
                bool granted = GateRules.IsGranted(v, i);
                if (granted && !wasGranted && PrevLane[i] == lane && GrantOnRed == null)
                {
                    int gc = v.GrantConnector[i];
                    byte sig = W.LaneSignals[gc];
                    if (sig != (byte)LaneSignal.None && sig != (byte)LaneSignal.Green && sig != (byte)LaneSignal.Yellow)
                    {
                        GrantOnRed = $"tick {W.Tick}: vehicle {i} granted connector {gc} with signal {(LaneSignal)sig}";
                    }
                }
            }
            else if (ld.Kind == LaneKind.Connector)
            {
                int prev = PrevLane[i];
                if (prev >= 0 && prev != lane && net.Lanes[prev].Kind == LaneKind.Normal && GateRules.GateNode(net, prev) >= 0)
                {
                    if ((PrevGate[i] & GateRules.Granted) == 0 && EntryWithoutGrant == null)
                    {
                        EntryWithoutGrant = $"tick {W.Tick}: vehicle {i} entered connector {lane} from lane {prev} without grant";
                    }

                    if (!GateRules.IsGranted(v, i) && EntryWithoutGrant == null)
                    {
                        EntryWithoutGrant = $"tick {W.Tick}: vehicle {i} on connector {lane} lost grant";
                    }
                }
            }
        }

        GapViolation ??= Step2.CheckGaps(W);
        ConflictViolation ??= Step3Gates.CheckConnectorConflicts(W);
        OnTick?.Invoke(this);

        // Стоки: порядок билетов на полосе выезда и деспаун.
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            int lane = v.Lane[i];
            if (PrevLane[i] != lane && net.Lanes[lane].Kind == LaneKind.Normal && PrevLane[i] >= 0 && net.Lanes[PrevLane[i]].Kind == LaneKind.Connector)
            {
                uint t = v.Ticket[i];
                if (LastTicketOnLane[lane] != 0 && (int)(t - LastTicketOnLane[lane]) <= 0 && TicketOrderViolation == null)
                {
                    TicketOrderViolation = $"tick {W.Tick}: lane {lane}: vehicle {i} ticket {t} after {LastTicketOnLane[lane]}";
                }

                LastTicketOnLane[lane] = t;
            }

            if (!IsSink(lane) || v.S[i] < SinkS)
            {
                continue;
            }

            Exits++;
            ExitsPerSink[lane]++;
            TravelTicksTotal += W.Tick - v.SpawnTick[i];
            if (SpawnLane[i] >= 0)
            {
                ExitsByLane[SpawnLane[i]]++;
                TravelTicksByLane[SpawnLane[i]] += W.Tick - v.SpawnTick[i];
            }

            SpawnLane[i] = -1;
            W.Occupancy.UnlinkAll(i);
            if (v.Route[i] >= 0)
            {
                W.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
                v.Route[i] = -1;
            }

            v.Despawn(v.HandleOf(i));
        }
    }

    public int Alive => W.Vehicles.AliveCount;

    public float MeanTravelS => Exits == 0 ? 0f : (float)(TravelTicksTotal * 0.05 / Exits);

    private bool IsSink(int lane)
    {
        for (int k = 0; k < SinkLanes.Count; k++)
        {
            if (SinkLanes[k] == lane)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryPlace(FlowSource src)
    {
        VehicleStore v = W.Vehicles;
        ref readonly VehicleTypeDesc t = ref W.Types[0];
        float s = t.Length + t.MinGap;
        int tail = W.Occupancy.LaneTail[src.Lane];
        float speed = Idm.DesiredSpeed(t, W.Net.Lanes[src.Lane].SpeedLimit);
        if (tail != -1)
        {
            float gap = v.S[tail] - v.Length[tail] - s;
            if (gap < t.MinGap + 4f)
            {
                return false;
            }

            float vSafe = MathF.Sqrt(2f * t.ComfortDecel * (gap - t.MinGap));
            speed = MathF.Min(speed, MathF.Min(v.Speed[tail] + 2f, vSafe));
        }

        int conn = src.Connector;
        if (src.ToArm >= 0)
        {
            conn = GateFixture.ConnectorTo(W, src.Lane, src.ToArm);
        }

        int i = GateFixture.Place(Sim, src.Lane, s, speed, conn, src.Routed);
        SpawnLane[i] = src.Lane;
        return true;
    }
}
