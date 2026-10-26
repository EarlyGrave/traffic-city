using System;
using System.Globalization;
using System.Text;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;

namespace TrafficCity;

/// <summary>
/// Панель узла по клику (ADR-0018, render.md §9, V10): режим управления, фаза и интервал светофора, сигнал по манёврам каждого рукава
/// (стрелки), очередь (стоящие машины у рукава), пешеходные вызовы (<c>CrossingStates</c>), ожидание (сеть целиком, <c>SignalMetrics</c>).
/// Данные - только публичное API ядра. Обновление 4 Гц; три кнопки-пресета ставят <c>SetSignalPlan</c> в очередь команд.
/// Визуальный стиль - нейтральная заглушка (дизайн за владельцем).
/// </summary>
public sealed partial class NodePanel : PanelContainer
{
    public const double RefreshInterval = 0.25;

    private const int MaxArms = 8;
    private const float QueueRadius = 70f;
    private const float QueueSpeed = 0.5f;
    private const int Maneuvers = 4; // straight, right, left, u-turn

    private readonly RichTextLabel _text;
    private readonly SignalMetrics _metrics = new();
    private readonly StringBuilder _sb = new(512);
    private readonly int[] _armSeg = new int[MaxArms];
    private readonly float[] _armDx = new float[MaxArms];
    private readonly float[] _armDz = new float[MaxArms];
    private readonly byte[] _armSig = new byte[MaxArms * Maneuvers];
    private readonly int[] _armQueue = new int[MaxArms];
    private readonly byte[] _armPed = new byte[MaxArms];
    private double _timer;
    private string _last = string.Empty;

    public NodePanel()
    {
        Name = "NodePanel";
        Visible = false;
        AnchorLeft = 1f;
        AnchorRight = 1f;
        OffsetLeft = -330f;
        OffsetRight = -8f;
        OffsetTop = 8f;
        GrowHorizontal = GrowDirection.Begin;
        var box = new VBoxContainer();
        AddChild(box);
        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(300f, 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(_text);
        var row = new HBoxContainer();
        box.AddChild(row);
        AddPreset(row, "Adaptive", NodePreset.Adaptive);
        AddPreset(row, "Fixed", NodePreset.Fixed);
        AddPreset(row, "Flash", NodePreset.Flash);
    }

    public enum NodePreset
    {
        Adaptive,
        Fixed,
        Flash,
    }

    /// <summary>Узел выбран (-1 - нет).</summary>
    public int Node { get; private set; } = -1;

    /// <summary>Нажата кнопка пресета (узел, пресет).</summary>
    public event Action<int, NodePreset>? PresetRequested;

    public void Open(int node)
    {
        Node = node;
        Visible = node >= 0;
        _timer = 0;
        _last = string.Empty;
    }

    public void Close()
    {
        Node = -1;
        Visible = false;
    }

    /// <summary>Перерисовка раз в 0.25 с; текст не пересобирается лишний раз, если строка не изменилась.</summary>
    public void Refresh(Simulation sim, double delta)
    {
        if (Node < 0)
        {
            return;
        }

        _timer -= delta;
        if (_timer > 0)
        {
            return;
        }

        _timer = RefreshInterval;
        NetworkView net = sim.Network;
        if (Node >= net.Nodes.Length)
        {
            Close();
            return;
        }

        string s = Build(sim, net, in net.Nodes[Node]);
        if (!string.Equals(s, _last, StringComparison.Ordinal))
        {
            _last = s;
            _text.Text = s;
        }
    }

    /// <summary>Команда пресета (ADR-0018 п. 3): Adaptive со стрелкой по спросу, Fixed 24/24 с, мигающий жёлтый.</summary>
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

    private static string ArmName(float dx, float dz)
    {
        if (MathF.Abs(dx) >= MathF.Abs(dz))
        {
            return dx >= 0 ? "E" : "W";
        }

        return dz >= 0 ? "S" : "N";
    }

    private static string Hex(byte sig) => sig switch
    {
        (byte)LaneSignal.Green => "3ee65a",
        (byte)LaneSignal.Yellow => "ffdb26",
        (byte)LaneSignal.Red => "ff4040",
        _ => "808080",
    };

    private void AddPreset(HBoxContainer row, string label, NodePreset preset)
    {
        var b = new Button { Text = label, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        b.Pressed += () =>
        {
            if (Node >= 0)
            {
                PresetRequested?.Invoke(Node, preset);
            }
        };
        row.AddChild(b);
    }

    private string Build(Simulation sim, NetworkView net, in NodeData nd)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        int armCount = CollectArms(net, Node, in nd);
        Array.Clear(_armSig, 0, _armSig.Length);
        Array.Clear(_armQueue, 0, _armQueue.Length);
        Array.Clear(_armPed, 0, _armPed.Length);

        // сигнал по манёврам: лучший (Green > Yellow > Red) среди connector'ов рукава и манёвра
        ReadOnlySpan<byte> ls = sim.LaneSignals;
        for (int c = nd.FirstConnector; c < nd.FirstConnector + nd.ConnectorCount; c++)
        {
            LaneData cn = net.Lanes[c];
            if (cn.FromLane < 0 || cn.ToLane < 0 || c >= ls.Length)
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
            if (Rank(sig) > Rank(_armSig[(a * Maneuvers) + m]))
            {
                _armSig[(a * Maneuvers) + m] = sig;
            }
        }

        // очередь: стоящие машины в радиусе у узла, по рукаву с наибольшим косинусом
        ReadOnlySpan<VehicleRenderData> veh = sim.Vehicles;
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
                float cs = (((dx * _armDx[a]) + (dz * _armDz[a])) / len);
                if (cs > bestCos)
                {
                    bestCos = cs;
                    best = a;
                }
            }

            if (best >= 0)
            {
                _armQueue[best]++;
            }
        }

        // пешеходные переходы узла
        ReadOnlySpan<byte> states = sim.CrossingStates;
        ReadOnlySpan<PedEdgeData> crossings = net.Crossings;
        bool anyPed = false;
        for (int j = 0; j < crossings.Length; j++)
        {
            ref readonly PedEdgeData e = ref crossings[j];
            if (e.Kind != PedEdgeKind.Crossing || e.Node != Node || e.Slot < 0 || e.Slot >= states.Length)
            {
                continue;
            }

            int a = ArmOfSegment(e.Segment, armCount);
            if (a >= 0)
            {
                _armPed[a] = (byte)(states[e.Slot] | 0x80);
                anyPed = true;
            }
        }

        _sb.Clear();
        _sb.Append("[b]Node #").Append(Node.ToString(inv)).Append("[/b]  ").Append(nd.Control.ToString())
            .Append("  arms ").Append(armCount.ToString(inv)).Append('\n');
        bool signal = nd.Control == NodeControl.Signal && nd.Signal >= 0;
        ReadOnlySpan<SignalState> ss = sim.SignalStates;
        if (signal && nd.Signal < ss.Length)
        {
            SignalState st = ss[nd.Signal];
            _sb.Append("phase ").Append(st.Phase.ToString(inv)).Append(' ').Append(st.Interval.ToString())
                .Append(", change in ").Append((st.TicksToChange / (float)Simulation.TicksPerSecond).ToString("0.0", inv)).Append(" s\n");
        }
        else if (signal)
        {
            _sb.Append("signal plan not active\n");
        }

        _sb.Append("[code]arm  S    R    L    U   queue  ped[/code]\n");
        for (int a = 0; a < armCount; a++)
        {
            _sb.Append("[code]").Append(ArmName(_armDx[a], _armDz[a])).Append("   [/code]");
            for (int m = 0; m < Maneuvers; m++)
            {
                byte sig = _armSig[(a * Maneuvers) + m];
                _sb.Append("[color=#").Append(Hex(sig)).Append(']').Append(sig == 0 ? "-" : "■").Append("[/color][code]    [/code]");
            }

            _sb.Append("[code] ").Append(_armQueue[a].ToString("00", inv)).Append("    [/code]");
            if ((_armPed[a] & 0x80) != 0)
            {
                int sig = (_armPed[a] >> 2) & 3;
                int occ = _armPed[a] & 3;
                _sb.Append(sig == 1 ? "[color=#3ee65a]walk[/color]" : sig == 2 ? "[color=#ffdb26]clear[/color]" : "[color=#ff4040]stop[/color]");
                _sb.Append(occ == 0 ? string.Empty : occ == 1 ? " call" : " busy");
            }
            else
            {
                _sb.Append('-');
            }

            _sb.Append('\n');
        }

        if (!anyPed)
        {
            _sb.Append("no pedestrian crossings\n");
        }

        ReadOnlySpan<uint> thr = sim.NodeThroughput;
        _sb.Append("passed ").Append(Node < thr.Length ? thr[Node].ToString(inv) : "0").Append(" veh\n");
        sim.CopySignalMetrics(_metrics);
        _sb.Append("mean signal wait, whole city:");
        string[] names = { "S", "R", "L", "U" };
        for (int m = 0; m < Maneuvers; m++)
        {
            long g = _metrics.Grants[m];
            double w = g > 0 ? _metrics.WaitTicks[m] / (double)g / Simulation.TicksPerSecond : 0.0;
            _sb.Append("  ").Append(names[m]).Append(' ').Append(w.ToString("0.0", inv)).Append(" s");
        }

        return _sb.ToString();
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
            if (_armSeg[a] == seg)
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

        float inX = -_armDx[from];
        float inZ = -_armDz[from];
        float outX = _armDx[to];
        float outZ = _armDz[to];
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

            _armSeg[n] = s;
            _armDx[n] = dx / len;
            _armDz[n] = dz / len;
            n++;
        }

        return n;
    }
}
