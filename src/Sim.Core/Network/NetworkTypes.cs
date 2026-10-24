using Sim.Core.Api;

namespace Sim.Core.Network;

/// <summary>Результат операций построения сети. Исключений нет: ёмкости и некорректные аргументы — статусы (core-api §10).</summary>
internal enum NetworkStatus : int
{
    Ok = 0,
    InvalidArgument = 1,    // нет такого узла, A == B, число полос/лимит скорости вне диапазона
    SegmentTooShort = 2,    // ось короче 2 * NodeSetback + MinLaneLength
    TooManyArms = 3,        // узел с > MaxArms сегментами
    TooManyConnectors = 4,  // план connector'ов узла не влез в рабочий буфер
    NodeAlreadyBuilt = 5,   // BuildConnectors для узла уже вызывался
    NodesFull = 6,          // MaxNodes
    SegmentsFull = 7,       // MaxSegments
    LanesFull = 8,          // MaxLanes
    PointsFull = 9,         // MaxLanePoints (или буфер осевых точек сегментов / NodeArms / LaneNext)
}

internal static class NetworkStatusExtensions
{
    public static bool IsCapacity(this NetworkStatus s) => s >= NetworkStatus.NodesFull;

    /// <summary>Отображение на статус <c>Simulation.Create</c> (core-api §10).</summary>
    public static CreateStatus ToCreateStatus(this NetworkStatus s) => s switch
    {
        NetworkStatus.Ok => CreateStatus.Ok,
        >= NetworkStatus.NodesFull => CreateStatus.NetworkTooLarge,
        _ => CreateStatus.InvalidNetwork,
    };
}

/// <summary>Геометрические параметры построения сети.</summary>
internal struct NetworkOptions
{
    public float LaneWidth;        // м
    public float SnapRadius;       // м: узлы ближе прилипают друг к другу
    public float NodeSetback;      // м: отступ конца сегмента от оси узла (длина connector'а прямо = 2 * NodeSetback)
    public int ConnectorSamples;   // точек в полилинии connector'а (>= 2)
    public float MinLaneLength;    // м: минимальная длина полосы между отступами

    public static NetworkOptions Default => new()
    {
        LaneWidth = 3.5f,
        SnapRadius = 5f,
        NodeSetback = 7f,
        ConnectorSamples = 8,
        MinLaneLength = 1f,
    };
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

/// <summary>Правила по умолчанию (core-api §6.1): правая → направо и прямо, левая → налево и прямо, средние → прямо.</summary>
internal sealed class DefaultConnectorRules : IConnectorRules
{
    public static readonly DefaultConnectorRules Instance = new();

    public int TargetLane(in ManeuverContext ctx, int inLaneNo)
    {
        int last = ctx.OutLaneCount - 1;
        switch (ctx.Maneuver)
        {
            case Maneuver.Straight:
            case Maneuver.UTurn:
                return inLaneNo < last ? inLaneNo : last;
            case Maneuver.Right:
                return ctx.Relaxed || inLaneNo == 0 ? 0 : -1;
            case Maneuver.Left:
                return ctx.Relaxed || inLaneNo == ctx.InLaneCount - 1 ? last : -1;
            default:
                return -1;
        }
    }
}
