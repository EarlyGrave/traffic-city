using System;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Построение сети в <see cref="NetworkStore"/> (core-api §6.1, §6.2): узлы со snap, сегменты с N полосами
/// в каждую сторону (боковой офсет от оси), connector'ы (см. NetworkBuilder.Connectors.cs).
/// Исключений нет: нехватка ёмкости / неверные аргументы возвращаются как <see cref="NetworkStatus"/>,
/// при ошибке хранилище не изменяется (кроме узлов, созданных snap-перегрузкой <c>AddSegment(Vec2, Vec2, ...)</c>).
/// Рабочие буферы выделяются один раз в конструкторе.
/// </summary>
internal sealed partial class NetworkBuilder
{
    public const int MaxVia = 12;                 // промежуточных точек оси сегмента
    public const int MaxLanesPerDirection = 8;
    private const int MaxAxis = MaxVia + 2;

    private readonly NetworkStore _s;
    private readonly NetworkOptions _o;
    private readonly IConnectorRules _rules;

    private readonly Vec2[] _raw = new Vec2[MaxAxis];
    private readonly float[] _rawS = new float[MaxAxis];
    private readonly Vec2[] _axis = new Vec2[MaxAxis];
    private readonly float[] _axisS = new float[MaxAxis];
    private readonly Vec2[] _miter = new Vec2[MaxAxis];

    private readonly int[] _armSeg = new int[NetworkStore.MaxArms];
    private readonly int[] _armCode = new int[NetworkStore.MaxArms];
    private readonly float[] _armKey = new float[NetworkStore.MaxArms];
    private readonly Vec2[] _armDir = new Vec2[NetworkStore.MaxArms];

    private readonly int[] _planIn = new int[MaxConnectorsPerNode];
    private readonly int[] _planOut = new int[MaxConnectorsPerNode];
    private readonly Maneuver[] _planMan = new Maneuver[MaxConnectorsPerNode];

    public NetworkBuilder(NetworkStore store)
        : this(store, NetworkOptions.Default, DefaultConnectorRules.Instance)
    {
    }

    public NetworkBuilder(NetworkStore store, NetworkOptions options, IConnectorRules rules)
    {
        _s = store;
        _o = options;
        _rules = rules;
        if (_o.ConnectorSamples < 2)
        {
            _o.ConnectorSamples = 2;
        }
    }

    /// <summary>Конструктор по контракту генератора (city-generator §7.1).</summary>
    public NetworkBuilder(NetworkStore store, float laneWidth, int connectorSamples)
        : this(store, WithGeometry(laneWidth, connectorSamples), DefaultConnectorRules.Instance)
    {
    }

    public int NodeCount => _s.NodeCount;

    public int SegmentCount => _s.SegmentCount;

    public int LaneCount => _s.LaneCount;

    public int LanePointCount => _s.LanePointCount;

    public NetworkStore Store => _s;

    private static NetworkOptions WithGeometry(float laneWidth, int connectorSamples)
    {
        NetworkOptions o = NetworkOptions.Default;
        o.LaneWidth = laneWidth;
        o.ConnectorSamples = connectorSamples;
        return o;
    }

    // ---- контракт генератора (city-generator §7.1): индекс или -1 / bool вместо NetworkStatus ----

    public int AddNode(Vec2 position, NodeControl control) =>
        AddNode(position, control, out int n) == NetworkStatus.Ok ? n : -1;

    public int AddSegment(int a, int b, byte lanesAB, byte lanesBA, float speedLimit, RoadKind kind) =>
        AddSegment(a, b, ReadOnlySpan<Vec2>.Empty, lanesAB, lanesBA, speedLimit, kind, out int seg) == NetworkStatus.Ok ? seg : -1;

    public bool BuildConnectors(int node) => BuildConnectorsEx(node) == NetworkStatus.Ok;

    public bool BuildAllConnectors() => BuildAllConnectorsEx() == NetworkStatus.Ok;

    public void SetNodeControl(int node, NodeControl control)
    {
        if ((uint)node < (uint)_s.NodeCount)
        {
            _s.Nodes[node].Control = control;
            if (_s.Nodes[node].FirstArm >= 0)
            {
                RefreshArmFlags(node);
            }

            _s.RefreshLaneGateNode(node);

            _s.Version++;
        }
    }

    public Vec2 NodePosition(int node) => _s.Nodes[node].Position;

    /// <summary>Число рукавов узла (до BuildConnectors — по списку инцидентности).</summary>
    public int ArmCount(int node)
    {
        if (_s.Nodes[node].FirstArm >= 0)
        {
            return _s.Nodes[node].ArmCount;
        }

        int n = 0;
        for (int code = _s.IncHead[node]; code >= 0; code = _s.IncNext[code])
        {
            n++;
        }

        return n;
    }

    /// <summary>Сегмент k-го рукава по часовой стрелке (до BuildConnectors сортирует во временном буфере); -1 вне диапазона.</summary>
    public int ArmSegment(int node, int k)
    {
        ref readonly NodeData nd = ref _s.Nodes[node];
        if (nd.FirstArm >= 0)
        {
            return (uint)k < (uint)nd.ArmCount ? _s.NodeArms[nd.FirstArm + k] : -1;
        }

        int arms = GatherArms(node);
        return (uint)k < (uint)arms ? _armSeg[k] : -1;
    }

    public NetworkOptions Options => _o;

    /// <summary>
    /// Загрузка сейва: служебные индексы построения (сетка прилипания узлов, список инцидентности) заново по узлам и сегментам в порядке индекса -
    /// тем же порядком вставки, что у <see cref="AddNode(Vec2, NodeControl, out int)"/> и <see cref="AddSegment(int, int, ReadOnlySpan{Vec2}, int, int, float, RoadKind, out int)"/>.
    /// Узлы вне сетки по ёмкости (<c>Cells</c> полна) - <see cref="NetworkStatus.NodesFull"/>.
    /// </summary>
    internal NetworkStatus RebuildIndexes()
    {
        for (int n = 0; n < _s.NodeCount; n++)
        {
            Vec2 p = _s.Nodes[n].Position;
            long key = CellKey(Cell(p.X), Cell(p.Z));
            int head = _s.Cells.TryGet(key, out int h) ? h : -1;
            if (!_s.Cells.TrySet(key, n))
            {
                return NetworkStatus.NodesFull;
            }

            _s.CellNext[n] = head;
            _s.IncHead[n] = -1;
        }

        for (int seg = 0; seg < _s.SegmentCount; seg++)
        {
            int a = _s.Segments[seg].NodeA;
            int b = _s.Segments[seg].NodeB;
            _s.IncNext[2 * seg] = _s.IncHead[a];
            _s.IncHead[a] = 2 * seg;
            _s.IncNext[(2 * seg) + 1] = _s.IncHead[b];
            _s.IncHead[b] = (2 * seg) + 1;
        }

        return NetworkStatus.Ok;
    }

    // ---- узлы ----

    /// <summary>Индекс существующего узла в радиусе SnapRadius (ближайший, при равенстве — меньший индекс) или -1.</summary>
    public int FindNode(Vec2 p)
    {
        int cx = Cell(p.X);
        int cz = Cell(p.Z);
        float r2 = _o.SnapRadius * _o.SnapRadius;
        int best = -1;
        float bestD = float.MaxValue;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!_s.Cells.TryGet(CellKey(cx + dx, cz + dz), out int n))
                {
                    continue;
                }

                for (; n >= 0; n = _s.CellNext[n])
                {
                    Vec2 q = _s.Nodes[n].Position;
                    float ex = q.X - p.X;
                    float ez = q.Z - p.Z;
                    float d = (ex * ex) + (ez * ez);
                    if (d <= r2 && (d < bestD || (d == bestD && n < best)))
                    {
                        best = n;
                        bestD = d;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Добавляет узел или возвращает существующий в радиусе SnapRadius (snap). Control существующего не меняется.</summary>
    public NetworkStatus AddNode(Vec2 position, NodeControl control, out int node)
    {
        if (!IsFinite(position))
        {
            node = -1;
            return NetworkStatus.InvalidArgument;
        }

        int found = FindNode(position);
        if (found >= 0)
        {
            node = found;
            return NetworkStatus.Ok;
        }

        if (_s.NodeCount >= _s.Nodes.Length)
        {
            node = -1;
            return NetworkStatus.NodesFull;
        }

        int idx = _s.NodeCount;
        long key = CellKey(Cell(position.X), Cell(position.Z));
        int head = _s.Cells.TryGet(key, out int h) ? h : -1;
        if (!_s.Cells.TrySet(key, idx))
        {
            node = -1;
            return NetworkStatus.NodesFull;
        }

        _s.CellNext[idx] = head;
        _s.IncHead[idx] = -1;
        _s.Nodes[idx] = new NodeData
        {
            Position = position,
            Elevation = 0f,
            Control = control,
            FirstArm = -1,
            ArmCount = 0,
            FirstConnector = 0,
            ConnectorCount = 0,
            Signal = -1,
            Zone = -1,
            Radius = _o.NodeSetback,
            Generation = 1,
        };
        _s.NodeCount = idx + 1;
        _s.Version++;
        node = idx;
        return NetworkStatus.Ok;
    }

    // ---- сегменты ----

    /// <summary>Сегмент между координатами; концы прилипают к существующим узлам в SnapRadius, иначе создаются узлы.</summary>
    public NetworkStatus AddSegment(Vec2 a, Vec2 b, int lanesAB, int lanesBA, float speedLimit, RoadKind kind, out int segment)
    {
        segment = -1;
        NetworkStatus st = AddNode(a, NodeControl.None, out int na);
        if (st != NetworkStatus.Ok)
        {
            return st;
        }

        st = AddNode(b, NodeControl.None, out int nb);
        if (st != NetworkStatus.Ok)
        {
            return st;
        }

        return AddSegment(na, nb, ReadOnlySpan<Vec2>.Empty, lanesAB, lanesBA, speedLimit, kind, out segment);
    }

    public NetworkStatus AddSegment(int nodeA, int nodeB, int lanesAB, int lanesBA, float speedLimit, RoadKind kind, out int segment) =>
        AddSegment(nodeA, nodeB, ReadOnlySpan<Vec2>.Empty, lanesAB, lanesBA, speedLimit, kind, out segment);

    /// <summary>
    /// Сегмент A→B, ось проходит через <paramref name="via"/>. Ось обрезается на NodeSetback у обоих узлов;
    /// SegmentData.Length — длина обрезанной оси (= Length всех Normal-полос сегмента, s параметризован по оси).
    /// </summary>
    public NetworkStatus AddSegment(
        int nodeA,
        int nodeB,
        ReadOnlySpan<Vec2> via,
        int lanesAB,
        int lanesBA,
        float speedLimit,
        RoadKind kind,
        out int segment)
    {
        segment = -1;
        if ((uint)nodeA >= (uint)_s.NodeCount || (uint)nodeB >= (uint)_s.NodeCount || nodeA == nodeB ||
            lanesAB < 0 || lanesBA < 0 || lanesAB > MaxLanesPerDirection || lanesBA > MaxLanesPerDirection ||
            lanesAB + lanesBA == 0 || !(speedLimit > 0f) || float.IsInfinity(speedLimit) || via.Length > MaxVia)
        {
            return NetworkStatus.InvalidArgument;
        }

        // Сырая ось.
        int n = 0;
        _raw[n++] = _s.Nodes[nodeA].Position;
        for (int i = 0; i < via.Length; i++)
        {
            if (!IsFinite(via[i]))
            {
                return NetworkStatus.InvalidArgument;
            }

            _raw[n++] = via[i];
        }

        _raw[n++] = _s.Nodes[nodeB].Position;
        _rawS[0] = 0f;
        for (int i = 1; i < n; i++)
        {
            float len = Dist(_raw[i - 1], _raw[i]);
            if (len < 0.01f)
            {
                return NetworkStatus.InvalidArgument;
            }

            _rawS[i] = _rawS[i - 1] + len;
        }

        float total = _rawS[n - 1];
        float setback = _o.NodeSetback;
        if (total < (2f * setback) + _o.MinLaneLength)
        {
            return NetworkStatus.SegmentTooShort;
        }

        // Обрезка по отступам.
        float start = setback;
        float end = total - setback;
        int m = 0;
        _axis[m] = PointAt(start, n);
        _axisS[m++] = 0f;
        for (int i = 1; i < n - 1; i++)
        {
            if (_rawS[i] > start + 0.01f && _rawS[i] < end - 0.01f)
            {
                _axis[m] = _raw[i];
                _axisS[m++] = _rawS[i] - start;
            }
        }

        float length = end - start;
        _axis[m] = PointAt(end, n);
        _axisS[m++] = length;

        int laneTotal = lanesAB + lanesBA;
        int pointTotal = laneTotal * m;
        if (_s.SegmentCount >= _s.Segments.Length)
        {
            return NetworkStatus.SegmentsFull;
        }

        if (_s.LaneCount + laneTotal > _s.Lanes.Length)
        {
            return NetworkStatus.LanesFull;
        }

        if (_s.LanePointCount + pointTotal > _s.LanePoints.Length || _s.SegmentPointCount + m > _s.SegmentPoints.Length)
        {
            return NetworkStatus.PointsFull;
        }

        // Miter-векторы оси: смещение вправо (по ходу A→B) на 1 м.
        ComputeMiters(m);

        int seg = _s.SegmentCount;
        int firstLane = _s.LaneCount;
        int firstSegPoint = _s.SegmentPointCount;
        for (int i = 0; i < m; i++)
        {
            _s.SegmentPoints[firstSegPoint + i] = _axis[i];
        }

        _s.SegmentPointCount += m;

        float halfWidth = laneTotal * _o.LaneWidth * 0.5f;
        for (int k = 0; k < lanesAB; k++)
        {
            float x = halfWidth - ((k + 0.5f) * _o.LaneWidth); // вправо от оси в системе A→B
            EmitLane(firstLane + k, seg, k, 0, lanesAB, x, m, length, speedLimit);
        }

        for (int k = 0; k < lanesBA; k++)
        {
            float x = -(halfWidth - ((k + 0.5f) * _o.LaneWidth));
            EmitLane(firstLane + lanesAB + k, seg, k, 1, lanesBA, x, m, length, speedLimit);
        }

        _s.Segments[seg] = new SegmentData
        {
            NodeA = nodeA,
            NodeB = nodeB,
            Length = length,
            SpeedLimit = speedLimit,
            Kind = kind,
            LanesAB = (byte)lanesAB,
            LanesBA = (byte)lanesBA,
            FirstLaneAB = lanesAB > 0 ? firstLane : -1,
            FirstLaneBA = lanesBA > 0 ? firstLane + lanesAB : -1,
            FirstPoint = firstSegPoint,
            PointCount = m,
            Generation = 1,
        };

        // Инцидентность (для BuildConnectors).
        _s.IncNext[2 * seg] = _s.IncHead[nodeA];
        _s.IncHead[nodeA] = 2 * seg;
        _s.IncNext[(2 * seg) + 1] = _s.IncHead[nodeB];
        _s.IncHead[nodeB] = (2 * seg) + 1;

        _s.SegmentCount = seg + 1;
        _s.LaneCount += laneTotal;
        _s.LanePointCount += pointTotal;
        _s.Version++;
        segment = seg;
        return NetworkStatus.Ok;
    }

    private void EmitLane(int laneIdx, int seg, int laneNo, int direction, int laneCountInDir, float x, int m, float length, float limit)
    {
        int first = _s.LanePointCount + ((laneIdx - _s.LaneCount) * m);
        for (int i = 0; i < m; i++)
        {
            // Направление B→A: те же точки в обратном порядке, s = Length - s_оси.
            int src = direction == 0 ? i : m - 1 - i;
            _s.LanePoints[first + i] = _axis[src] + (_miter[src] * x);
            _s.LanePointS[first + i] = direction == 0 ? _axisS[src] : length - _axisS[src];
        }

        _s.LanePointS[first] = 0f;
        _s.LanePointS[first + m - 1] = length;
        _s.FillPieceHeadings(first, m);
        _s.LaneLength[laneIdx] = length;
        _s.Lanes[laneIdx] = new LaneData
        {
            Kind = LaneKind.Normal,
            LaneNo = (byte)laneNo,
            Direction = (byte)direction,
            Flags = 0,
            Segment = seg,
            Node = -1,
            FromLane = -1,
            ToLane = -1,
            Length = length,
            SpeedLimit = limit,
            FirstPoint = first,
            PointCount = m,
            Right = laneNo > 0 ? laneIdx - 1 : -1,
            Left = laneNo < laneCountInDir - 1 ? laneIdx + 1 : -1,
            FirstNext = 0,
            NextCount = 0,
            ConflictGroup = -1,
            Generation = 1,
        };
    }

    private void ComputeMiters(int m)
    {
        // Правая нормаль направления d = (dx, dz): (-dz, dx) (в плоскости X-Z: для +X вправо = +Z).
        for (int i = 0; i < m; i++)
        {
            Vec2 nPrev = i > 0 ? RightNormal(_axis[i - 1], _axis[i]) : default;
            Vec2 nNext = i < m - 1 ? RightNormal(_axis[i], _axis[i + 1]) : default;
            if (i == 0)
            {
                _miter[i] = nNext;
            }
            else if (i == m - 1)
            {
                _miter[i] = nPrev;
            }
            else
            {
                float denom = 1f + (nPrev.X * nNext.X) + (nPrev.Z * nNext.Z);
                if (denom < 0.25f)
                {
                    denom = 0.25f;
                }

                _miter[i] = (nPrev + nNext) * (1f / denom);
            }
        }
    }

    private static Vec2 RightNormal(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float inv = 1f / MathF.Sqrt((dx * dx) + (dz * dz));
        return new Vec2(-dz * inv, dx * inv);
    }

    private Vec2 PointAt(float dist, int n)
    {
        int i = 1;
        while (i < n - 1 && _rawS[i] < dist)
        {
            i++;
        }

        float t = (dist - _rawS[i - 1]) / (_rawS[i] - _rawS[i - 1]);
        return _raw[i - 1] + ((_raw[i] - _raw[i - 1]) * t);
    }

    private static float Dist(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static bool IsFinite(Vec2 p) => float.IsFinite(p.X) && float.IsFinite(p.Z);

    private int Cell(float v) => DetMath.ToIntSat(MathF.Floor(v / _o.SnapRadius));

    private static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
}
