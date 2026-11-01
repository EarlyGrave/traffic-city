using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Host;

namespace TrafficCity;

/// <summary>
/// Данные инспектора узла (UI3, перенос прежней <c>NodePanel</c>): рукава узла с направлением, сигнал по манёврам каждого рукава, очередь
/// (стоящие машины у рукава), состояние пешеходного перехода. Только кадр хоста и копия сети; без Godot и без аллокаций после конструктора.
/// </summary>
public sealed class NodeArms
{
    public const int MaxArms = 8;

    /// <summary>Манёвры в порядке <c>SignalMetrics</c>: 0 прямо, 1 направо, 2 налево, 3 разворот.</summary>
    public const int Maneuvers = 4;

    private const float QueueRadius = 70f;
    private const float QueueSpeed = 0.5f;

    private readonly int[] _seg = new int[MaxArms];
    private readonly float[] _dx = new float[MaxArms];
    private readonly float[] _dz = new float[MaxArms];
    private readonly byte[] _sig = new byte[MaxArms * Maneuvers];
    private readonly int[] _queue = new int[MaxArms];
    private readonly byte[] _ped = new byte[MaxArms];

    public enum NodePreset
    {
        Adaptive,
        Fixed,
        Flash,
    }

    /// <summary>Рукавов в последнем <see cref="Collect"/>.</summary>
    public int Count { get; private set; }

    /// <summary>У узла есть пешеходные переходы (по последнему <see cref="Collect"/>).</summary>
    public bool AnyCrossing { get; private set; }

    public float DirX(int arm) => _dx[arm];

    public float DirZ(int arm) => _dz[arm];

    /// <summary>Сигнал манёвра рукава (<see cref="LaneSignal"/>, 0 - манёвра нет или светофора нет).</summary>
    public byte Signal(int arm, int maneuver) => _sig[(arm * Maneuvers) + maneuver];

    /// <summary>Стоящих машин у рукава (в радиусе 70 м от узла, скорость &lt; 0.5 м/с).</summary>
    public int Queue(int arm) => _queue[arm];

    /// <summary>Есть переход через рукав.</summary>
    public bool HasCrossing(int arm) => (_ped[arm] & 0x80) != 0;

    /// <summary>Сигнал перехода: 0 стоп, 1 «идите», 2 «заканчивайте».</summary>
    public int CrossingSignal(int arm) => (_ped[arm] >> 2) & 3;

    /// <summary>Занятость перехода: 0 пусто, 1 вызов, 2 занят.</summary>
    public int CrossingOccupancy(int arm) => _ped[arm] & 3;

    /// <summary>Сторона света рукава по экрану (X вправо = восток, Z вниз = юг).</summary>
    public string ArmName(int arm)
    {
        float dx = _dx[arm];
        float dz = _dz[arm];
        if (MathF.Abs(dx) >= MathF.Abs(dz))
        {
            return dx >= 0 ? "В" : "З";
        }

        return dz >= 0 ? "Ю" : "С";
    }

    /// <summary>Команда пресета светофора (ADR-0018 п. 3): Adaptive со стрелкой по спросу, Fixed 24/24 с, мигающий жёлтый.</summary>
    public static Command MakePreset(int node, int generation, NodePreset preset)
    {
        var p = new SetSignalPlanCmd { Node = node, NodeGen = generation, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20 };
        switch (preset)
        {
            case NodePreset.Adaptive:
                p.Mode = SignalMode.Adaptive;
                p.Flags = SignalPlanFlags.Arrows;
                break;
            case NodePreset.Fixed:
                p.Mode = SignalMode.Fixed;
                p.Green0 = 480;
                p.Green1 = 480;
                break;
            default:
                p.Mode = SignalMode.FlashYellow;
                p.Green0 = 480;
                p.Green1 = 480;
                break;
        }

        return Command.MakeSetSignalPlan(in p);
    }

    /// <summary>Собрать рукава, сигналы по манёврам, очереди и переходы узла <paramref name="node"/> по кадру.</summary>
    public void Collect(SimFrame frame, NetworkView net, int node)
    {
        ref readonly NodeData nd = ref net.Nodes[node];
        int armCount = CollectArms(net, node, in nd);
        Count = armCount;
        Array.Clear(_sig, 0, _sig.Length);
        Array.Clear(_queue, 0, _queue.Length);
        Array.Clear(_ped, 0, _ped.Length);

        // сигнал по манёврам: лучший (Green > Yellow > Red) среди connector'ов рукава и манёвра
        ReadOnlySpan<byte> ls = frame.LaneSignals;
        for (int c = nd.FirstConnector; c < nd.FirstConnector + nd.ConnectorCount; c++)
        {
            if (!net.IsLaneAlive(c) || c >= ls.Length)
            {
                continue;
            }

            LaneData cn = net.Lanes[c];
            if (!net.IsLaneAlive(cn.FromLane) || !net.IsLaneAlive(cn.ToLane))
            {
                continue;
            }

            int a = ArmOfSegment(net.Lanes[cn.FromLane].Segment, armCount);
            int b = ArmOfSegment(net.Lanes[cn.ToLane].Segment, armCount);
            if (a < 0 || b < 0)
            {
                continue;
            }

            int m = Maneuver(a, b);
            byte sig = ls[c];
            if (Rank(sig) > Rank(_sig[(a * Maneuvers) + m]))
            {
                _sig[(a * Maneuvers) + m] = sig;
            }
        }

        // очередь: стоящие машины в радиусе у узла, по рукаву с наибольшим косинусом
        ReadOnlySpan<VehicleRenderData> veh = frame.Vehicles;
        for (int i = 0; i < veh.Length; i++)
        {
            ref readonly VehicleRenderData v = ref veh[i];
            if ((v.Flags & (byte)VehicleRenderFlags.Active) == 0 || v.Speed >= QueueSpeed)
            {
                continue;
            }

            float dx = v.X - nd.Position.X;
            float dz = v.Z - nd.Position.Z;
            float d2 = (dx * dx) + (dz * dz);
            if (d2 > QueueRadius * QueueRadius || d2 < 1f)
            {
                continue;
            }

            float len = MathF.Sqrt(d2);
            int best = -1;
            float bestCos = 0.5f;
            for (int a = 0; a < armCount; a++)
            {
                float cs = ((dx * _dx[a]) + (dz * _dz[a])) / len;
                if (cs > bestCos)
                {
                    bestCos = cs;
                    best = a;
                }
            }

            if (best >= 0)
            {
                _queue[best]++;
            }
        }

        // пешеходные переходы узла
        ReadOnlySpan<byte> states = frame.CrossingStates;
        ReadOnlySpan<PedEdgeData> crossings = net.Crossings;
        bool any = false;
        for (int j = 0; j < crossings.Length; j++)
        {
            ref readonly PedEdgeData e = ref crossings[j];
            if (e.Kind != PedEdgeKind.Crossing || e.Node != node || e.Slot < 0 || e.Slot >= states.Length)
            {
                continue;
            }

            int a = ArmOfSegment(e.Segment, armCount);
            if (a >= 0)
            {
                _ped[a] = (byte)(states[e.Slot] | 0x80);
                any = true;
            }
        }

        AnyCrossing = any;
    }

    private static int Rank(byte sig) => sig switch
    {
        (byte)LaneSignal.Green => 3,
        (byte)LaneSignal.Yellow => 2,
        (byte)LaneSignal.Red => 1,
        _ => 0,
    };

    private int ArmOfSegment(int seg, int armCount)
    {
        for (int a = 0; a < armCount; a++)
        {
            if (_seg[a] == seg)
            {
                return a;
            }
        }

        return -1;
    }

    /// <summary>Индекс манёвра как в <c>SignalMetrics</c>: 0 прямо, 1 направо, 2 налево, 3 разворот (экранные оси: X вправо, Z вниз).</summary>
    private int Maneuver(int from, int to)
    {
        if (from == to)
        {
            return 3;
        }

        float inX = -_dx[from];
        float inZ = -_dz[from];
        float outX = _dx[to];
        float outZ = _dz[to];
        float dot = (inX * outX) + (inZ * outZ);
        if (dot > 0.7f)
        {
            return 0;
        }

        float cross = (inX * outZ) - (inZ * outX);
        return cross < 0 ? 2 : 1;
    }

    private int CollectArms(NetworkView net, int node, in NodeData nd)
    {
        int n = 0;
        for (int s = 0; s < net.Segments.Length && n < MaxArms; s++)
        {
            if (!net.IsSegmentAlive(s))
            {
                continue;
            }

            ref readonly SegmentData sg = ref net.Segments[s];
            int other;
            if (sg.NodeA == node)
            {
                other = sg.NodeB;
            }
            else if (sg.NodeB == node)
            {
                other = sg.NodeA;
            }
            else
            {
                continue;
            }

            if (other < 0 || other >= net.Nodes.Length)
            {
                continue;
            }

            float dx = net.Nodes[other].Position.X - nd.Position.X;
            float dz = net.Nodes[other].Position.Z - nd.Position.Z;
            float len = MathF.Sqrt((dx * dx) + (dz * dz));
            if (len < 1e-3f)
            {
                continue;
            }

            _seg[n] = s;
            _dx[n] = dx / len;
            _dz[n] = dz / len;
            n++;
        }

        return n;
    }
}
