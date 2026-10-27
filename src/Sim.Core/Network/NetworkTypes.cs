using Sim.Core.Api;

namespace Sim.Core.Network;

/// <summary>Результат операций построения сети. Исключений нет: ёмкости и некорректные аргументы — статусы (core-api §10).</summary>
internal enum NetworkStatus : int
{
    Ok = 0,
    InvalidArgument = 1,    // нет такого узла, A == B, число полос/лимит скорости вне диапазона
    SegmentTooShort = 2,    // ось короче 2 * NodeSetback + MinLaneLength (увеличенные отступы ADR-0028 сжимаются, а не отказывают)
    TooManyArms = 3,        // узел с > MaxArms сегментами
    TooManyConnectors = 4,  // план connector'ов узла не влез в рабочий буфер
    NodeAlreadyBuilt = 5,   // BuildConnectors для узла уже вызывался
    NodesFull = 6,          // MaxNodes
    SegmentsFull = 7,       // MaxSegments
    LanesFull = 8,          // MaxLanes
    PointsFull = 9,         // MaxLanePoints (или буфер осевых точек сегментов / NodeArms / LaneNext)
    ConnectorsFull = 10,    // MaxConnectors (ConnectorStore)
    ConflictsFull = 11,     // MaxConflicts (ConnectorStore)
    Busy = 12,              // перестройка узла: исчезающий connector занят машинами (4.3, T3); ёмкостью не является
    Trap = 13,              // перестройка узла оставила бы входную полосу без выезда (запрет манёвра); ёмкостью не является
}

internal static class NetworkStatusExtensions
{
    public static bool IsCapacity(this NetworkStatus s) => s >= NetworkStatus.NodesFull && s <= NetworkStatus.ConflictsFull;

    /// <summary>Отображение на статус <c>Simulation.Create</c> (core-api §10).</summary>
    public static CreateStatus ToCreateStatus(this NetworkStatus s) => s switch
    {
        NetworkStatus.Ok => CreateStatus.Ok,
        >= NetworkStatus.NodesFull and <= NetworkStatus.ConflictsFull => CreateStatus.NetworkTooLarge,
        _ => CreateStatus.InvalidNetwork,
    };
}

/// <summary>Геометрические параметры построения сети.</summary>
internal struct NetworkOptions
{
    public float LaneWidth;        // м
    public float SnapRadius;       // м: узлы ближе прилипают друг к другу
    public float NodeSetback;      // м: базовый (минимальный) отступ конца сегмента от оси узла; тупики и изгибы - ровно он (ADR-0028)
    public int ConnectorSamples;   // точек в полилинии connector'а (>= 2)
    public float MinLaneLength;    // м: минимальная длина полосы между отступами
    public float CornerRadius;     // м: радиус скругления бордюра (ADR-0028); отступ рукава узла степени >= 3 = max(NodeSetback, полуширина поперечной + CornerRadius); 0 - фиксированный NodeSetback (до ADR-0028)

    /// <summary>Радиус скругления по умолчанию (ADR-0028, `city.corner_radius_m`).</summary>
    public const float DefaultCornerRadius = 6f;

    public static NetworkOptions Default => new()
    {
        LaneWidth = 3.5f,
        SnapRadius = 5f,
        NodeSetback = 7f,
        ConnectorSamples = 8,
        MinLaneLength = 1f,
        CornerRadius = DefaultCornerRadius,
    };

    /// <summary>Значение опции из поля сценария (<c>CityGenDesc.CornerRadius</c>): 0 - дефолт, &lt; 0 - выключено (фиксированный отступ), иначе радиус.</summary>
    public static float CornerRadiusFromDesc(float desc) => desc == 0f ? DefaultCornerRadius : desc < 0f ? 0f : desc;
}

internal enum Maneuver : byte
{
    Straight = 0,
    Right = 1,
    Left = 2,
    UTurn = 3,
}

/// <summary>Контекст решения «какая полоса входа в какую полосу выхода» для одного манёвра.</summary>
internal readonly struct ManeuverContext
{
    public ManeuverContext(Maneuver maneuver, int inLaneCount, int outLaneCount, NodeControl control, RoadKind inKind, RoadKind outKind, bool relaxed)
    {
        Maneuver = maneuver;
        InLaneCount = inLaneCount;
        OutLaneCount = outLaneCount;
        Control = control;
        InKind = inKind;
        OutKind = outKind;
        Relaxed = relaxed;
    }

    public Maneuver Maneuver { get; }

    public int InLaneCount { get; }

    public int OutLaneCount { get; }

    public NodeControl Control { get; }

    public RoadKind InKind { get; }

    public RoadKind OutKind { get; }

    /// <summary>true — повторный проход для полосы, которой основные правила не дали ни одного connector'а.</summary>
    public bool Relaxed { get; }
}

/// <summary>
/// Точка расширения (шаг 3): правила манёвров. Возвращает номер выходной полосы (LaneNo) или -1, если
/// манёвр с этой входной полосы запрещён. Не вызывается для пустых направлений.
/// </summary>
internal interface IConnectorRules
{
    int TargetLane(in ManeuverContext ctx, int inLaneNo);
}

/// <summary>Правила по умолчанию (intersections.md §2.2): правая → направо и прямо, левая → налево и прямо, средние → прямо; разворот — с левой полосы.</summary>
internal sealed class DefaultConnectorRules : IConnectorRules
{
    public static readonly DefaultConnectorRules Instance = new();

    public int TargetLane(in ManeuverContext ctx, int inLaneNo)
    {
        int last = ctx.OutLaneCount - 1;
        switch (ctx.Maneuver)
        {
            case Maneuver.Straight:
                return inLaneNo < last ? inLaneNo : last;
            case Maneuver.UTurn:
                return ctx.Relaxed || inLaneNo == ctx.InLaneCount - 1 ? last : -1;
            case Maneuver.Right:
                return ctx.Relaxed || inLaneNo == 0 ? 0 : -1;
            case Maneuver.Left:
                return ctx.Relaxed || inLaneNo == ctx.InLaneCount - 1 ? last : -1;
            default:
                return -1;
        }
    }
}
