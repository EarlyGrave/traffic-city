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

    /// <summary>Узлы, созданные последними вызовами <c>AddNode</c> с момента <see cref="ResetCreated"/> (по порядку создания; не больше двух - концы одного сегмента): индексы слотов не обязаны расти (дыры).</summary>
    internal readonly int[] CreatedNodes = new int[2];

    internal int CreatedNodeCount;

    internal void ResetCreated() => CreatedNodeCount = 0;

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
        : this(store, WithGeometry(laneWidth, connectorSamples, NetworkOptions.DefaultCornerRadius), DefaultConnectorRules.Instance)
    {
    }

    /// <summary>То же с радиусом скругления (<see cref="NetworkOptions.CornerRadius"/>, 0 - фиксированный отступ; ADR-0028).</summary>
    public NetworkBuilder(NetworkStore store, float laneWidth, int connectorSamples, float cornerRadius)
        : this(store, WithGeometry(laneWidth, connectorSamples, cornerRadius), DefaultConnectorRules.Instance)
    {
    }

    public int NodeCount => _s.NodeCount;

    public int SegmentCount => _s.SegmentCount;

    public int LaneCount => _s.LaneCount;

    public int LanePointCount => _s.LanePointCount;

    public NetworkStore Store => _s;

    private static NetworkOptions WithGeometry(float laneWidth, int connectorSamples, float cornerRadius)
    {
        NetworkOptions o = NetworkOptions.Default;
        o.LaneWidth = laneWidth;
        o.ConnectorSamples = connectorSamples;
        o.CornerRadius = cornerRadius > 0f ? cornerRadius : 0f;
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
        if (_s.NodeAlive(node))
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
            _s.IncHead[n] = -1;
            _s.CellNext[n] = -1;
        }

        for (int n = 0; n < _s.NodeCount; n++)
        {
            if (!_s.NodeAlive(n))
            {
                continue;
            }

            if (!LinkNode(n))
            {
                return NetworkStatus.NodesFull;
            }
        }

        for (int seg = 0; seg < _s.SegmentCount; seg++)
        {
            if (_s.SegmentAlive(seg))
            {
                LinkSegment(seg);
            }
        }

        return NetworkStatus.Ok;
    }

    // ---- служебные индексы: вставка и удаление в порядке «по убыванию индекса» (равно полной перестройке по возрастанию индекса живых слотов) ----

    /// <summary>Узел <paramref name="idx"/> в цепочку своей ячейки сетки прилипания (цепочка идёт по убыванию индекса). false - таблица ячеек полна.</summary>
    internal bool LinkNode(int idx)
    {
        Vec2 p = _s.Nodes[idx].Position;
        long key = CellKey(Cell(p.X), Cell(p.Z));
        bool has = _s.Cells.TryGet(key, out int head);
        if (!has)
        {
            head = -1;
        }

        if (head < 0 || idx > head)
        {
            if (!_s.Cells.TrySet(key, idx))
            {
                return false;
            }

            _s.CellNext[idx] = head;
            return true;
        }

        int prev = head;
        while (_s.CellNext[prev] > idx)
        {
            prev = _s.CellNext[prev];
        }

        _s.CellNext[idx] = _s.CellNext[prev];
        _s.CellNext[prev] = idx;
        return true;
    }

    /// <summary>Убирает узел из цепочки ячейки; опустевшая ячейка уходит из таблицы (как после полной перестройки, и ёмкость таблицы не утекает).</summary>
    internal void UnlinkNode(int idx)
    {
        Vec2 p = _s.Nodes[idx].Position;
        long key = CellKey(Cell(p.X), Cell(p.Z));
        if (!_s.Cells.TryGet(key, out int head))
        {
            return;
        }

        if (head == idx)
        {
            if (_s.CellNext[idx] < 0)
            {
                _s.Cells.Remove(key);
            }
            else
            {
                _s.Cells.TrySet(key, _s.CellNext[idx]);
            }
        }
        else
        {
            int prev = head;
            while (prev >= 0 && _s.CellNext[prev] != idx)
            {
                prev = _s.CellNext[prev];
            }

            if (prev >= 0)
            {
                _s.CellNext[prev] = _s.CellNext[idx];
            }
        }

        _s.CellNext[idx] = -1;
    }

    /// <summary>Концы сегмента в списки инцидентности своих узлов (по убыванию кода <c>2*seg+end</c>).</summary>
    internal void LinkSegment(int seg)
    {
        LinkInc(_s.Segments[seg].NodeA, 2 * seg);
        LinkInc(_s.Segments[seg].NodeB, (2 * seg) + 1);
    }

    internal void UnlinkSegment(int seg)
    {
        UnlinkInc(_s.Segments[seg].NodeA, 2 * seg);
        UnlinkInc(_s.Segments[seg].NodeB, (2 * seg) + 1);
    }

    private void LinkInc(int node, int code)
    {
        int head = _s.IncHead[node];
        if (head < 0 || code > head)
        {
            _s.IncNext[code] = head;
            _s.IncHead[node] = code;
            return;
        }

        int prev = head;
        while (_s.IncNext[prev] > code)
        {
            prev = _s.IncNext[prev];
        }

        _s.IncNext[code] = _s.IncNext[prev];
        _s.IncNext[prev] = code;
    }

    private void UnlinkInc(int node, int code)
    {
        int head = _s.IncHead[node];
        if (head == code)
        {
            _s.IncHead[node] = _s.IncNext[code];
        }
        else
        {
            int prev = head;
            while (prev >= 0 && _s.IncNext[prev] != code)
            {
                prev = _s.IncNext[prev];
            }

            if (prev >= 0)
            {
                _s.IncNext[prev] = _s.IncNext[code];
            }
        }

        _s.IncNext[code] = -1;
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

        int idx = _s.NodePool.PeekIndex();
        if (idx < 0)
        {
            node = -1;
            return NetworkStatus.NodesFull;
        }

        _s.Nodes[idx] = new NodeData { Position = position };   // позиция нужна LinkNode (ячейка); остальное - ниже
        if (!LinkNode(idx))
        {
            _s.Nodes[idx] = NetworkStore.DeadNode(_s.NodePool.GenerationOf(idx));
            node = -1;
            return NetworkStatus.NodesFull;
        }

        _s.NodePool.TryAllocate(out idx, out int gen);
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
            Generation = gen,
        };
        _s.Version++;
        if (CreatedNodeCount < CreatedNodes.Length)
        {
            CreatedNodes[CreatedNodeCount++] = idx;
        }

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
    /// Сегмент A→B, ось проходит через <paramref name="via"/>. Отступы концов (ADR-0028) - по рукавам, которые уже есть у узлов
    /// (<see cref="IncrementalSetback"/>): так строят инструменты игрока; генератор и явная сеть знают все рукава заранее и зовут перегрузку с отступами.
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
        float sa = _o.NodeSetback;
        float sb = _o.NodeSetback;
        if (_s.NodeAlive(nodeA) && _s.NodeAlive(nodeB) && nodeA != nodeB && via.Length <= MaxVia && lanesAB >= 0 && lanesBA >= 0)
        {
            Vec2 pa = _s.Nodes[nodeA].Position;
            Vec2 pb = _s.Nodes[nodeB].Position;
            float half = HalfWidth(lanesAB + lanesBA);
            sa = IncrementalSetback(nodeA, via.Length > 0 ? via[0] : pb, half);
            sb = IncrementalSetback(nodeB, via.Length > 0 ? via[^1] : pa, half);
        }

        return AddSegment(nodeA, nodeB, via, lanesAB, lanesBA, speedLimit, kind, sa, sb, out segment);
    }

    /// <summary>
    /// Сегмент A→B с заданными отступами концов от осей узлов (<paramref name="setbackA"/>, <paramref name="setbackB"/>; не меньше <c>NodeSetback</c>).
    /// Ось короче <c>2 * NodeSetback + MinLaneLength</c> - <see cref="NetworkStatus.SegmentTooShort"/>; если короче суммы отступов + <c>MinLaneLength</c>,
    /// надбавки сверх <c>NodeSetback</c> сжимаются пропорционально (ADR-0028: увеличенный отступ не отклоняет сегмент, который строился до него).
    /// <c>NodeData.Radius</c> концов - максимум отступов их рукавов. SegmentData.Length — длина обрезанной оси (= Length всех Normal-полос сегмента).
    /// </summary>
    public NetworkStatus AddSegment(
        int nodeA,
        int nodeB,
        ReadOnlySpan<Vec2> via,
        int lanesAB,
        int lanesBA,
        float speedLimit,
        RoadKind kind,
        float setbackA,
        float setbackB,
        out int segment) =>
        AddSegmentCore(nodeA, nodeB, via, lanesAB, lanesBA, speedLimit, kind, setbackA, setbackB, -1, false, out segment);

    /// <summary>
    /// Сегмент по профилю (ADR-0030, road-profiles.md §3.2): число полос и тип дороги берутся из профиля (<paramref name="mirrored"/> - построен против направления рисования профиля),
    /// смещение и ширина каждой полосы - из его элементов. Отступы концов - по рукавам, которые уже есть у узлов (<see cref="IncrementalSetback"/>).
    /// Нет каталога или профиль вне диапазона - <see cref="NetworkStatus.InvalidArgument"/>.
    /// </summary>
    public NetworkStatus AddProfileSegment(int nodeA, int nodeB, ReadOnlySpan<Vec2> via, int profile, bool mirrored, float speedLimit, out int segment)
    {
        float sa = _o.NodeSetback;
        float sb = _o.NodeSetback;
        if (_s.Profiles != null && (uint)profile < (uint)_s.Profiles.Count && _s.NodeAlive(nodeA) && _s.NodeAlive(nodeB) && nodeA != nodeB && via.Length <= MaxVia)
        {
            Vec2 pa = _s.Nodes[nodeA].Position;
            Vec2 pb = _s.Nodes[nodeB].Position;
            sa = IncrementalSetback(nodeA, via.Length > 0 ? via[0] : pb, 0f);
            sb = IncrementalSetback(nodeB, via.Length > 0 ? via[^1] : pa, 0f);
        }

        return AddProfileSegment(nodeA, nodeB, via, profile, mirrored, speedLimit, sa, sb, out segment);
    }

    /// <summary>То же с заданными отступами концов (<see cref="PlanSetbacks(ReadOnlySpan{int}, ReadOnlySpan{int}, ReadOnlySpan{int}, ReadOnlySpan{int}, Span{float}, Span{float})"/>: генератор и явная сеть знают все рукава заранее).</summary>
    public NetworkStatus AddProfileSegment(int nodeA, int nodeB, ReadOnlySpan<Vec2> via, int profile, bool mirrored, float speedLimit, float setbackA, float setbackB, out int segment)
    {
        RoadProfileStore? ps = _s.Profiles;
        if (ps == null || (uint)profile >= (uint)ps.Count)
        {
            segment = -1;
            return NetworkStatus.InvalidArgument;
        }

        return AddSegmentCore(nodeA, nodeB, via, ps.LanesAB(profile, mirrored), ps.LanesBA(profile, mirrored), speedLimit, ps.Kind[profile], setbackA, setbackB, profile, mirrored, out segment);
    }

    private NetworkStatus AddSegmentCore(
        int nodeA,
        int nodeB,
        ReadOnlySpan<Vec2> via,
        int lanesAB,
        int lanesBA,
        float speedLimit,
        RoadKind kind,
        float setbackA,
        float setbackB,
        int profile,
        bool mirrored,
        out int segment)
    {
        segment = -1;
        RoadProfileStore? ps = profile >= 0 ? _s.Profiles : null;
        if (!_s.NodeAlive(nodeA) || !_s.NodeAlive(nodeB) || nodeA == nodeB ||
            lanesAB < 0 || lanesBA < 0 || lanesAB > MaxLanesPerDirection || lanesBA > MaxLanesPerDirection ||
            lanesAB + lanesBA == 0 || !(speedLimit > 0f) || float.IsInfinity(speedLimit) || via.Length > MaxVia ||
            !float.IsFinite(setbackA) || !float.IsFinite(setbackB) ||
            (profile >= 0 && (ps == null || profile >= ps.Count || lanesAB != ps.LanesAB(profile, mirrored) || lanesBA != ps.LanesBA(profile, mirrored))))
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

        ClampSetbacks(total, ref setbackA, ref setbackB);

        // Обрезка по отступам.
        float start = setbackA;
        float end = total - setbackB;
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
        if (_s.SegmentPool.PeekIndex() < 0)
        {
            return NetworkStatus.SegmentsFull;
        }

        if (!_s.CanAllocBlock(_s.LaneArena, laneTotal))
        {
            return NetworkStatus.LanesFull;
        }

        if (!_s.CanAllocBlock(_s.LanePointArena, pointTotal) || !_s.CanAllocBlock(_s.SegmentPointArena, m))
        {
            return NetworkStatus.PointsFull;
        }

        // Miter-векторы оси: смещение вправо (по ходу A→B) на 1 м.
        ComputeMiters(m);

        // Запись (ошибок дальше нет): слот сегмента из пула, блоки - из арен (у сети без правок подряд в хвосте, как раньше).
        _s.SegmentPool.TryAllocate(out int seg, out int gen);
        int firstLane = _s.AllocLaneBlock(laneTotal);
        int firstPoint = _s.AllocBlock(_s.LanePointArena, pointTotal);
        int firstSegPoint = _s.AllocBlock(_s.SegmentPointArena, m);
        for (int i = 0; i < m; i++)
        {
            _s.SegmentPoints[firstSegPoint + i] = _axis[i];
        }

        float halfWidth = laneTotal * _o.LaneWidth * 0.5f;
        for (int k = 0; k < lanesAB; k++)
        {
            // Вправо от оси в системе A→B: профиль - из его элементов, legacy - прежняя формула одной ширины на сеть.
            float x = ps != null ? ps.LaneCenterM(profile, mirrored, 0, k) : halfWidth - ((k + 0.5f) * _o.LaneWidth);
            float w = ps != null ? ps.LaneWidthM(profile, mirrored, 0, k) : _o.LaneWidth;
            EmitLane(firstLane + k, firstPoint + (k * m), seg, k, 0, lanesAB, x, w, m, length, speedLimit);
        }

        for (int k = 0; k < lanesBA; k++)
        {
            float x = ps != null ? ps.LaneCenterM(profile, mirrored, 1, k) : -(halfWidth - ((k + 0.5f) * _o.LaneWidth));
            float w = ps != null ? ps.LaneWidthM(profile, mirrored, 1, k) : _o.LaneWidth;
            EmitLane(firstLane + lanesAB + k, firstPoint + ((lanesAB + k) * m), seg, k, 1, lanesBA, x, w, m, length, speedLimit);
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
            Flags = ps != null && mirrored ? SegmentFlags.Mirrored : (byte)0,
            Profile = ps != null ? profile : -1,
            FirstLaneAB = lanesAB > 0 ? firstLane : -1,
            FirstLaneBA = lanesBA > 0 ? firstLane + lanesAB : -1,
            FirstPoint = firstSegPoint,
            PointCount = m,
            Generation = gen,
        };

        // Отступ стоп-линий узла (NodeData.Radius) - наибольший из отступов его рукавов (клиент рисует по нему площадку узла).
        ref NodeData ndA = ref _s.Nodes[nodeA];
        if (setbackA > ndA.Radius)
        {
            ndA.Radius = setbackA;
        }

        ref NodeData ndB = ref _s.Nodes[nodeB];
        if (setbackB > ndB.Radius)
        {
            ndB.Radius = setbackB;
        }

        // Инцидентность (для BuildConnectors).
        LinkSegment(seg);
        _s.Version++;
        segment = seg;
        return NetworkStatus.Ok;
    }

    private void EmitLane(int laneIdx, int first, int seg, int laneNo, int direction, int laneCountInDir, float x, float width, int m, float length, float limit)
    {
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
            Width = width,
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

    /// <summary>Ключ ячейки сетки прилипания для точки (для сверки производных: <c>World.VerifyDerived</c>).</summary>
    internal long CellKeyOf(Vec2 p) => CellKey(Cell(p.X), Cell(p.Z));

    private int Cell(float v) => DetMath.ToIntSat(MathF.Floor(v / _o.SnapRadius));

    private static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
}
