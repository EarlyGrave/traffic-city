using System;
using Sim.Core.Util;

namespace Sim.Core.Network;

internal sealed partial class NetworkBuilder
{
    /// <summary>cos(30°): поворот до 30° — «прямо».</summary>
    internal const float StraightCos = 0.8660254f;

    /// <summary>Предел connector'ов на узел (LocalBit — 6 бит, PhaseMask — ulong; intersections.md §2.2).</summary>
    internal const int MaxConnectorsPerNode = 64;

    private const float LateralAccel = 2f;     // м/с², intersections.md §1
    private const float MinConnectorSpeed = 2.5f;
    private const float CosTenDegrees = 0.9848078f;

    /// <summary>BuildConnectors для всех узлов, где он ещё не вызывался; первая ошибка прерывает обход.</summary>
    public NetworkStatus BuildAllConnectorsEx()
    {
        for (int n = 0; n < _s.NodeCount; n++)
        {
            if (_s.Nodes[n].FirstArm >= 0)
            {
                continue;
            }

            NetworkStatus st = BuildConnectorsEx(n);
            if (st != NetworkStatus.Ok)
            {
                return st;
            }
        }

        return NetworkStatus.Ok;
    }

    /// <summary>
    /// Строит рукава узла (NodeArms, по часовой стрелке) и connector-полосы для всех манёвров
    /// «въезд → выезд» по <see cref="IConnectorRules"/>; разворот — только на тупике (узел с одним сегментом).
    /// Connector — кубическая Безье P0 = конец входной полосы, P3 = начало выходной, P1/P2 = P0 + t0·d / P3 − t3·d,
    /// d = |P3 − P0| / 3, сэмплируется в ConnectorSamples точек. Атомарно: при ошибке хранилище не меняется.
    /// Повторный вызов для узла — NodeAlreadyBuilt (перестройка узла — шаг 3).
    /// </summary>
    public NetworkStatus BuildConnectorsEx(int node)
    {
        if ((uint)node >= (uint)_s.NodeCount)
        {
            return NetworkStatus.InvalidArgument;
        }

        ref NodeData nd = ref _s.Nodes[node];
        if (nd.FirstArm >= 0)
        {
            return NetworkStatus.NodeAlreadyBuilt;
        }

        int arms = GatherArms(node);
        if (arms < 0)
        {
            return NetworkStatus.TooManyArms;
        }

        if (_s.NodeArmCount + arms > _s.NodeArms.Length)
        {
            return NetworkStatus.PointsFull;
        }

        int plan = PlanConnectors(nd.Control, arms);
        if (plan < 0)
        {
            return NetworkStatus.TooManyConnectors;
        }

        int samples = _o.ConnectorSamples;
        if (_s.LaneCount + plan > _s.Lanes.Length)
        {
            return NetworkStatus.LanesFull;
        }

        if (_s.LanePointCount + (plan * samples) > _s.LanePoints.Length || _s.LaneNextCount + (2 * plan) > _s.LaneNext.Length)
        {
            return NetworkStatus.PointsFull;
        }

        // ---- запись (ошибок дальше нет) ----
        int firstArm = _s.NodeArmCount;
        for (int a = 0; a < arms; a++)
        {
            _s.NodeArms[firstArm + a] = _armSeg[a];
        }

        _s.NodeArmCount += arms;

        int baseLane = _s.LaneCount;
        int blockIn = _s.LaneNextCount;
        int blockOwn = blockIn + plan;
        for (int p = 0; p < plan; p++)
        {
            int inLane = _planIn[p];
            int outLane = _planOut[p];
            int laneIdx = baseLane + p;

            ref LaneData li = ref _s.Lanes[inLane];
            ref LaneData lo = ref _s.Lanes[outLane];
            if (li.NextCount == 0)
            {
                li.FirstNext = blockIn + p;
            }

            li.NextCount++;
            _s.LaneNext[blockIn + p] = laneIdx;
            _s.LaneNext[blockOwn + p] = outLane;

            int first = _s.LanePointCount + (p * samples);
            float length = EmitBezier(li, lo, first, samples, _planMan[p] == Maneuver.UTurn, out float speed);
            _s.Lanes[laneIdx] = new LaneData
            {
                Kind = LaneKind.Connector,
                LaneNo = 0,
                Direction = 0,
                Flags = 0,
                Segment = -1,
                Node = node,
                FromLane = inLane,
                ToLane = outLane,
                Length = length,
                SpeedLimit = speed,
                FirstPoint = first,
                PointCount = samples,
                Left = -1,
                Right = -1,
                FirstNext = blockOwn + p,
                NextCount = 1,
                ConflictGroup = -1,
                Generation = 1,
            };
        }

        _s.LaneNextCount += 2 * plan;
        _s.LaneCount += plan;
        _s.LanePointCount += plan * samples;

        for (int a = 0; a < arms; a++)
        {
            _s.ArmDir[firstArm + a] = _armDir[a];
        }

        nd.FirstArm = firstArm;
        nd.ArmCount = arms;
        nd.FirstConnector = baseLane;
        nd.ConnectorCount = plan;
        RefreshArmFlags(node);
        _s.Version++;
        return NetworkStatus.Ok;
    }

    /// <summary>Собирает рукава узла в _arm* по возрастанию «псевдоугла» (по часовой стрелке в X-Z); -1 при переполнении.</summary>
    private int GatherArms(int node)
    {
        int arms = 0;
        for (int code = _s.IncHead[node]; code >= 0; code = _s.IncNext[code])
        {
            if (arms == NetworkStore.MaxArms)
            {
                return -1;
            }

            ref readonly SegmentData sd = ref _s.Segments[code >> 1];
            int p0 = sd.FirstPoint;
            Vec2 v = (code & 1) == 0
                ? _s.SegmentPoints[p0 + 1] - _s.SegmentPoints[p0]
                : _s.SegmentPoints[p0 + sd.PointCount - 2] - _s.SegmentPoints[p0 + sd.PointCount - 1];
            float inv = 1f / MathF.Sqrt((v.X * v.X) + (v.Z * v.Z));
            v *= inv;
            float key = PseudoAngle(v);

            int pos = arms++;
            while (pos > 0 && (_armKey[pos - 1] > key || (_armKey[pos - 1] == key && _armCode[pos - 1] > code)))
            {
                _armKey[pos] = _armKey[pos - 1];
                _armCode[pos] = _armCode[pos - 1];
                _armSeg[pos] = _armSeg[pos - 1];
                _armDir[pos] = _armDir[pos - 1];
                pos--;
            }

            _armKey[pos] = key;
            _armCode[pos] = code;
            _armSeg[pos] = code >> 1;
            _armDir[pos] = v;
        }

        return arms;
    }

    /// <summary>Монотонная по углу функция [0, 4) без тригонометрии (кросс-платформенный детерминизм).</summary>
    private static float PseudoAngle(Vec2 v)
    {
        float p = v.Z / (MathF.Abs(v.X) + MathF.Abs(v.Z));
        if (v.X < 0f)
        {
            return 2f - p;
        }

        return v.Z < 0f ? 4f + p : p;
    }

    private void ArmLanes(int arm, bool incoming, out int first, out int count)
    {
        ref readonly SegmentData sd = ref _s.Segments[_armSeg[arm]];
        bool atA = (_armCode[arm] & 1) == 0;
        // Входящие в узел: у конца A едут B→A, у конца B — A→B. Исходящие — наоборот.
        bool ab = atA != incoming;
        first = ab ? sd.FirstLaneAB : sd.FirstLaneBA;
        count = ab ? sd.LanesAB : sd.LanesBA;
    }

    /// <summary>
    /// Класс манёвра по направлениям рукавов от узла (intersections.md §2.1): |turn| ≤ 30° — прямо, иначе по знаку
    /// cross вправо/влево. Поворот круче 150° остаётся Right/Left (а не UTurn), чтобы острые Y-развилки сохраняли связность.
    /// На узле из двух рукавов (изгиб) любой проезд — Straight.
    /// </summary>
    internal static Maneuver ClassifyTurn(Vec2 armDirIn, Vec2 armDirOut, bool twoArms)
    {
        if (twoArms)
        {
            return Maneuver.Straight;
        }

        Vec2 u = armDirIn * -1f;
        float dot = (u.X * armDirOut.X) + (u.Z * armDirOut.Z);
        if (dot >= StraightCos)
        {
            return Maneuver.Straight;
        }

        float cross = (u.X * armDirOut.Z) - (u.Z * armDirOut.X);
        return cross >= 0f ? Maneuver.Right : Maneuver.Left;
    }

    private Maneuver Classify(int armIn, int armOut, int arms) => ClassifyTurn(_armDir[armIn], _armDir[armOut], arms == 2);

    /// <summary>
    /// ArmMinor рукавов и нормализация Control (intersections.md §2.3). Для узлов с ArmCount ≤ 2 Control не меняется.
    /// </summary>
    private void RefreshArmFlags(int node)
    {
        ref NodeData nd = ref _s.Nodes[node];
        int arms = nd.ArmCount;
        int f = nd.FirstArm;
        bool anyArterial = false;
        bool anyStreet = false;
        for (int a = 0; a < arms; a++)
        {
            if (_s.Segments[_s.NodeArms[f + a]].Kind == RoadKind.Arterial)
            {
                anyArterial = true;
            }
            else
            {
                anyStreet = true;
            }

            _s.ArmFlags[f + a] = 0;
        }

        bool anyMinor = false;
        if (anyArterial && anyStreet)
        {
            for (int a = 0; a < arms; a++)
            {
                if (_s.Segments[_s.NodeArms[f + a]].Kind == RoadKind.Street)
                {
                    _s.ArmFlags[f + a] = ArmMinorBit;
                    anyMinor = true;
                }
            }
        }
        else if (arms == 3)
        {
            int lonely = -1;
            int lonelyCount = 0;
            for (int a = 0; a < arms; a++)
            {
                Vec2 u = _s.ArmDir[f + a] * -1f;
                bool hasOpposite = false;
                for (int b = 0; b < arms && !hasOpposite; b++)
                {
                    if (b != a)
                    {
                        Vec2 v = _s.ArmDir[f + b];
                        hasOpposite = (u.X * v.X) + (u.Z * v.Z) >= StraightCos;
                    }
                }

                if (!hasOpposite)
                {
                    lonely = a;
                    lonelyCount++;
                }
            }

            if (lonelyCount == 1)
            {
                _s.ArmFlags[f + lonely] = ArmMinorBit;
                anyMinor = true;
            }
        }

        if (arms >= 3)
        {
            bool noRules = nd.Control == NodeControl.None;
            bool needMinor = nd.Control == NodeControl.Yield || nd.Control == NodeControl.Stop;
            if (noRules || (needMinor && !anyMinor))
            {
                nd.Control = NodeControl.RightHandRule;
            }
        }
    }

    internal const byte ArmMinorBit = 1;

    /// <summary>Заполняет _planIn/_planOut в порядке (рукав, полоса, манёвр: прямо, направо, налево, разворот); -1 при переполнении.</summary>
    private int PlanConnectors(NodeControl control, int arms)
    {
        int plan = 0;
        for (int ai = 0; ai < arms; ai++)
        {
            ArmLanes(ai, true, out int inFirst, out int inCount);
            RoadKind inKind = _s.Segments[_armSeg[ai]].Kind;
            for (int li = 0; li < inCount; li++)
            {
                int before = plan;
                for (int pass = 0; pass < 2 && plan == before; pass++)
                {
                    bool relaxed = pass == 1;
                    for (int mi = 0; mi < 4; mi++)
                    {
                        var m = (Maneuver)mi;
                        for (int aj = 0; aj < arms; aj++)
                        {
                            if (aj == ai)
                            {
                                if (m != Maneuver.UTurn || arms != 1)
                                {
                                    continue;
                                }
                            }
                            else if (m == Maneuver.UTurn || Classify(ai, aj, arms) != m)
                            {
                                continue;
                            }

                            ArmLanes(aj, false, out int outFirst, out int outCount);
                            if (outCount == 0)
                            {
                                continue;
                            }

                            RoadKind outKind = _s.Segments[_armSeg[aj]].Kind;
                            var ctx = new ManeuverContext(m, inCount, outCount, control, inKind, outKind, relaxed);
                            int target = _rules.TargetLane(ctx, li);
                            if (target < 0 || target >= outCount)
                            {
                                continue;
                            }

                            // Прямо при n_out > n_in: старшая входная полоса расходится (diverge) на все лишние выходные.
                            int hi = m == Maneuver.Straight && li == inCount - 1 && outCount > inCount ? outCount - 1 : target;
                            for (int t = target; t <= hi; t++)
                            {
                                if (plan == MaxConnectorsPerNode)
                                {
                                    return -1;
                                }

                                _planIn[plan] = inFirst + li;
                                _planOut[plan] = outFirst + t;
                                _planMan[plan] = m;
                                plan++;
                            }
                        }
                    }
                }
            }
        }

        return plan;
    }

    private float EmitBezier(in LaneData laneIn, in LaneData laneOut, int first, int samples, bool uturn, out float speed)
    {
        int iEnd = laneIn.FirstPoint + laneIn.PointCount - 1;
        Vec2 p0 = _s.LanePoints[iEnd];
        Vec2 t0 = Normalize(p0 - _s.LanePoints[iEnd - 1]);
        Vec2 p3 = _s.LanePoints[laneOut.FirstPoint];
        Vec2 t3 = Normalize(_s.LanePoints[laneOut.FirstPoint + 1] - p3);
        float chord = Dist(p0, p3);
        float d = uturn ? MathF.Max(chord, 2f * _o.LaneWidth) : chord * (1f / 3f);
        Vec2 p1 = p0 + (t0 * d);
        Vec2 p2 = p3 - (t3 * d);

        float s = 0f;
        Vec2 prev = p0;
        for (int k = 0; k < samples; k++)
        {
            Vec2 pt;
            if (k == 0)
            {
                pt = p0;
            }
            else if (k == samples - 1)
            {
                pt = p3;
            }
            else
            {
                float t = k / (float)(samples - 1);
                float u = 1f - t;
                float b0 = u * u * u;
                float b1 = 3f * u * u * t;
                float b2 = 3f * u * t * t;
                float b3 = t * t * t;
                pt = new Vec2(
                    (b0 * p0.X) + (b1 * p1.X) + (b2 * p2.X) + (b3 * p3.X),
                    (b0 * p0.Z) + (b1 * p1.Z) + (b2 * p2.Z) + (b3 * p3.Z));
            }

            s += Dist(prev, pt);
            prev = pt;
            _s.LanePoints[first + k] = pt;
            _s.LanePointS[first + k] = s;
        }

        // Лимит скорости: sqrt(a_lat · R), R = chord / (2 sin(|turn|/2)) при |turn| ≥ 10°; sin(turn/2) = sqrt((1 − cos)/2) без тригонометрии.
        float cosTurn = (t0.X * t3.X) + (t0.Z * t3.Z);
        float limit = laneIn.SpeedLimit;
        if (uturn || cosTurn <= CosTenDegrees)
        {
            float r = uturn ? _o.LaneWidth : chord / (2f * MathF.Sqrt(MathF.Max(0f, (1f - cosTurn) * 0.5f)));
            float v = MathF.Max(MinConnectorSpeed, MathF.Sqrt(LateralAccel * r));
            limit = MathF.Min(limit, v);
        }

        speed = limit;
        return s;
    }

    private static Vec2 Normalize(Vec2 v)
    {
        float inv = 1f / MathF.Sqrt((v.X * v.X) + (v.Z * v.Z));
        return v * inv;
    }
}
