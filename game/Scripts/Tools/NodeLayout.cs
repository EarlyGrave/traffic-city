using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity.Tools;

/// <summary>Попадание по полосе сегмента (флаги полос, ui-spec §5.4 п. 6).</summary>
public readonly record struct LaneHit(EntityRef Seg, byte Dir, byte LaneNo, byte Flags, Vec2 Point);

/// <summary>
/// Раскладка узла для инструмента «Узлы» (UI10, ui-spec §5.4): рукава в порядке слотов ядра (по часовой, тот же «псевдоугол», что
/// <c>NetworkBuilder.GatherArms</c>), точки иконок рукавов и стрелок манёвров в мире. Чистый C#, без Godot и без аллокаций после конструктора.
/// Слот рукава в командах <c>SetArmControl</c>/<c>SetTurnBan</c> - индекс здесь.
/// </summary>
public sealed class NodeLayout
{
    public const int MaxArms = 8;

    /// <summary>Иконка рукава - на этом расстоянии за стоп-линией, м.</summary>
    public const float ArmIconM = 16f;

    /// <summary>Стрелки манёвров - ближе к узлу, чем иконка рукава, м за стоп-линией.</summary>
    public const float ArrowM = 7f;

    /// <summary>Шаг стрелок поперёк рукава, м.</summary>
    public const float ArrowStepM = 4f;

    private readonly int[] _seg = new int[MaxArms];
    private readonly int[] _code = new int[MaxArms];
    private readonly float[] _key = new float[MaxArms];
    private readonly float[] _dx = new float[MaxArms];
    private readonly float[] _dz = new float[MaxArms];

    public int Node { get; private set; } = -1;

    public int Generation { get; private set; }

    public int Count { get; private set; }

    public Vec2 Center { get; private set; }

    public float Radius { get; private set; }

    /// <summary>Запреты манёвров узла на момент <see cref="Build"/> (бит <c>from * 8 + to</c>).</summary>
    public ulong TurnBan { get; private set; }

    public NodeControl Control { get; private set; }

    public EntityRef Ref => new(Node, Generation);

    public int Segment(int arm) => _seg[arm];

    public float DirX(int arm) => _dx[arm];

    public float DirZ(int arm) => _dz[arm];

    public bool Banned(int from, int to) => (TurnBan & (1UL << ((from * 8) + to))) != 0;

    public void Clear()
    {
        Node = -1;
        Count = 0;
    }

    /// <summary>Собрать рукава узла <paramref name="node"/> в порядке слотов ядра. false - узла нет или у него больше 8 рукавов.</summary>
    public bool Build(NetworkView net, int node)
    {
        Clear();
        if (!net.IsNodeAlive(node))
        {
            return false;
        }

        ref readonly NodeData nd = ref net.Nodes[node];
        int n = 0;
        for (int s = 0; s < net.Segments.Length; s++)
        {
            if (!net.IsSegmentAlive(s))
            {
                continue;
            }

            ref readonly SegmentData sg = ref net.Segments[s];
            for (int end = 0; end < 2; end++)
            {
                int at = end == 0 ? sg.NodeA : sg.NodeB;
                if (at != node)
                {
                    continue;
                }

                if (n == MaxArms)
                {
                    return false;
                }

                // направление от узла: к контрольной точке кривой или к другому концу
                int other = end == 0 ? sg.NodeB : sg.NodeA;
                float tx;
                float tz;
                if ((sg.Flags & SegmentFlags.Curve) != 0)
                {
                    tx = sg.Cx;
                    tz = sg.Cz;
                }
                else if ((uint)other < (uint)net.Nodes.Length)
                {
                    tx = net.Nodes[other].Position.X;
                    tz = net.Nodes[other].Position.Z;
                }
                else
                {
                    continue;
                }

                float dx = tx - nd.Position.X;
                float dz = tz - nd.Position.Z;
                float len = MathF.Sqrt((dx * dx) + (dz * dz));
                if (len < 1e-4f)
                {
                    continue;
                }

                dx /= len;
                dz /= len;
                Insert(ref n, (s * 2) + end, s, dx, dz);
            }
        }

        Node = node;
        Generation = nd.Generation;
        Count = n;
        Center = nd.Position;
        Radius = nd.Radius;
        TurnBan = nd.TurnBan;
        Control = nd.Control;
        return true;
    }

    /// <summary>Точка иконки рукава в мире.</summary>
    public Vec2 ArmIcon(int arm)
    {
        float d = Radius + ArmIconM;
        return new Vec2(Center.X + (_dx[arm] * d), Center.Z + (_dz[arm] * d));
    }

    /// <summary>
    /// Точка стрелки манёвра <paramref name="from"/> → <paramref name="to"/> (from != to): над входом рукава <paramref name="from"/>, стрелки в ряд поперёк рукава
    /// в порядке слотов цели.
    /// </summary>
    public Vec2 Arrow(int from, int to)
    {
        int k = to < from ? to : to - 1;
        float lateral = (k - ((Count - 2) * 0.5f)) * ArrowStepM;
        float d = Radius + ArrowM;
        float px = -_dz[from];
        float pz = _dx[from];
        return new Vec2(Center.X + (_dx[from] * d) + (px * lateral), Center.Z + (_dz[from] * d) + (pz * lateral));
    }

    /// <summary>Манёвр как в <c>SignalMetrics</c>: 0 прямо, 1 направо, 2 налево, 3 разворот (экранные оси: X вправо, Z вниз).</summary>
    public int Maneuver(int from, int to)
    {
        if (from == to)
        {
            return 3;
        }

        float inX = -_dx[from];
        float inZ = -_dz[from];
        float outX = _dx[to];
        float outZ = _dz[to];
        if ((inX * outX) + (inZ * outZ) > 0.7f)
        {
            return 0;
        }

        return (inX * outZ) - (inZ * outX) < 0 ? 2 : 1;
    }

    /// <summary>Ближайшая иконка рукава в радиусе; -1 - нет.</summary>
    public int PickArm(float x, float z, float radius)
    {
        int best = -1;
        float bd = radius * radius;
        for (int a = 0; a < Count; a++)
        {
            float d = Dist2(ArmIcon(a), x, z);
            if (d <= bd)
            {
                bd = d;
                best = a;
            }
        }

        return best;
    }

    /// <summary>Ближайшая стрелка манёвра в радиусе; false - нет.</summary>
    public bool PickArrow(float x, float z, float radius, out int from, out int to)
    {
        from = -1;
        to = -1;
        float bd = radius * radius;
        for (int a = 0; a < Count; a++)
        {
            for (int b = 0; b < Count; b++)
            {
                if (a == b)
                {
                    continue;
                }

                float d = Dist2(Arrow(a, b), x, z);
                if (d <= bd)
                {
                    bd = d;
                    from = a;
                    to = b;
                }
            }
        }

        return from >= 0;
    }

    /// <summary>Ближайший живой узел в радиусе (плюс радиус узла); -1 - нет.</summary>
    public static int PickNode(NetworkView net, float x, float z, float radius)
    {
        int best = -1;
        float bd = float.MaxValue;
        for (int i = 0; i < net.Nodes.Length; i++)
        {
            if (!net.IsNodeAlive(i))
            {
                continue;
            }

            ref readonly NodeData nd = ref net.Nodes[i];
            float r = radius + nd.Radius;
            float d = Dist2(nd.Position, x, z);
            if (d <= r * r && d < bd)
            {
                bd = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Ближайшая обычная полоса в радиусе (по полилинии полосы); false - нет.</summary>
    public static bool PickLane(NetworkView net, float x, float z, float radius, out LaneHit hit)
    {
        hit = default;
        float bd = radius * radius;
        int bestLane = -1;
        Vec2 bestPt = default;
        ReadOnlySpan<Vec2> pts = net.LanePoints;
        for (int l = 0; l < net.Lanes.Length; l++)
        {
            ref readonly LaneData ln = ref net.Lanes[l];
            if (ln.Kind != LaneKind.Normal || !net.IsLaneAlive(l) || !net.IsSegmentAlive(ln.Segment))
            {
                continue;
            }

            for (int p = ln.FirstPoint; p + 1 < ln.FirstPoint + ln.PointCount && p + 1 < pts.Length; p++)
            {
                Vec2 q = Closest(pts[p], pts[p + 1], x, z);
                float d = Dist2(q, x, z);
                if (d < bd)
                {
                    bd = d;
                    bestLane = l;
                    bestPt = q;
                }
            }
        }

        if (bestLane < 0)
        {
            return false;
        }

        ref readonly LaneData b = ref net.Lanes[bestLane];
        hit = new LaneHit(net.SegmentRef(b.Segment), b.Direction, b.LaneNo, b.Flags, bestPt);
        return true;
    }

    private static Vec2 Closest(Vec2 a, Vec2 b, float x, float z)
    {
        float vx = b.X - a.X;
        float vz = b.Z - a.Z;
        float l2 = (vx * vx) + (vz * vz);
        float t = l2 < 1e-6f ? 0f : Math.Clamp((((x - a.X) * vx) + ((z - a.Z) * vz)) / l2, 0f, 1f);
        return new Vec2(a.X + (vx * t), a.Z + (vz * t));
    }

    private static float Dist2(Vec2 p, float x, float z)
    {
        float dx = p.X - x;
        float dz = p.Z - z;
        return (dx * dx) + (dz * dz);
    }

    private static float PseudoAngle(float x, float z)
    {
        float p = z / (MathF.Abs(x) + MathF.Abs(z));
        if (x < 0f)
        {
            return 2f - p;
        }

        return z < 0f ? 4f + p : p;
    }

    private void Insert(ref int n, int code, int seg, float dx, float dz)
    {
        float key = PseudoAngle(dx, dz);
        int pos = n++;
        while (pos > 0 && (_key[pos - 1] > key || (_key[pos - 1] == key && _code[pos - 1] > code)))
        {
            _key[pos] = _key[pos - 1];
            _code[pos] = _code[pos - 1];
            _seg[pos] = _seg[pos - 1];
            _dx[pos] = _dx[pos - 1];
            _dz[pos] = _dz[pos - 1];
            pos--;
        }

        _key[pos] = key;
        _code[pos] = code;
        _seg[pos] = seg;
        _dx[pos] = dx;
        _dz[pos] = dz;
    }
}
