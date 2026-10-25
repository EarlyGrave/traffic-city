using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Util;

// Публичные read-only структуры сети (core-api §6.1, §6.6). Лежат физически в Api/, потому что на них
// ссылаются Command и NetworkView; namespace остаётся Sim.Core.Network, как в спеке.
// Задача C (Network/) НЕ должна определять их повторно.
namespace Sim.Core.Network;

public enum RoadKind : byte
{
    Street = 0,
    Arterial = 1,
}

public enum NodeControl : int
{
    None = 0,
    Yield = 1,
    Stop = 2,
    Signal = 3,
    RightHandRule = 4,
}

public enum LaneKind : byte
{
    Normal = 0,
    Connector = 1,
    BusBay = 2,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NodeData
{
    public Vec2 Position;           // ось узла
    public float Elevation;         // Y, этап 1 = 0
    public float Radius;            // м: отступ стоп-линий от оси узла; этап 1 = NetworkOptions.NodeSetback для всех узлов (intersections.md §1)
    public NodeControl Control;
    public int FirstArm, ArmCount;  // диапазон в NodeArms (сегменты, инцидентные узлу, по часовой стрелке)
    public int FirstConnector, ConnectorCount; // диапазон полос-connector'ов узла (подряд в Lanes)
    public int Signal;              // индекс в SignalStates или -1
    public int Zone;                // ближайшая зона или -1
    public int Generation;          // копия из пула, для NodeHandle
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SegmentData
{
    public int NodeA, NodeB;
    public float Length;            // длина оси, м
    public float SpeedLimit;        // м/с
    public RoadKind Kind;
    public byte LanesAB, LanesBA;   // число полос; 0 допустимо для односторонней
    public byte Reserved;
    public int FirstLaneAB;         // полосы A→B: FirstLaneAB .. +LanesAB-1, LaneNo 0 = правая
    public int FirstLaneBA;
    public int FirstPoint, PointCount; // осевая полилиния (SegmentPoints)
    public int Generation;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LaneData
{
    public LaneKind Kind;
    public byte LaneNo;             // 0 = крайняя правая (по ходу движения)
    public byte Direction;          // 0 = A→B, 1 = B→A (у connector'а — 0)
    public byte Flags;              // бит0 BusOnly, бит1 NoLaneChangeOut
    public int Segment;             // владелец (Normal/BusBay) или -1
    public int Node;                // владелец (Connector) или -1
    public int FromLane, ToLane;    // только Connector; иначе -1
    public float Length;            // м; для Normal == Segment.Length
    public float SpeedLimit;        // м/с
    public int FirstPoint, PointCount; // диапазон в LanePoints/LanePointS (>= 2)
    public int Left, Right;         // соседние полосы того же направления или -1
    public int FirstNext, NextCount;// исходящие connector'ы (диапазон в LaneNext)
    public int ConflictGroup;       // [шаг 3] или -1
    public int Generation;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ZoneData
{
    public Vec2 Center;
    public float HalfSize;          // квадратная ячейка
    public float Weight;            // вес генерации/притяжения
    public int FirstSpawnLane, SpawnLaneCount; // диапазон в ZoneLanes
    public byte IsExternal;         // краевая зона
    public byte Reserved0, Reserved1, Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BusStopData
{
    public int Lane;
    public float S;
    public BusStopKind Kind;
    public int Route;
    public ushort Waiting;
    public ushort Reserved;
    public long LastArrivalTick;

    /// <summary>Тик последнего отправления автобуса с остановки (удержание, ADR-0008); -1 - не было.</summary>
    public long LastDepartTick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BusRouteData
{
    public int FirstStop, StopCount;
    public int HeadwayTicks;
    public int VehicleType;
    public int ActiveBuses;
    public long LastDispatchTick;
    public int Generation;
}

/// <summary>Вид ребра графа пешеходов (pedestrians.md §2.1).</summary>
public enum PedEdgeKind : byte
{
    Sidewalk = 0,       // тротуар вдоль стороны сегмента (или его отрезок между переходами на перегоне)
    EndCap = 1,         // обход тупика: соединяет два угла вокруг конца дороги
    Crossing = 2,       // переход через рукав узла
    MidCrossing = 3,    // переход на перегоне у остановки
}

/// <summary>
/// Ребро графа пешеходов (статика, производное от сети; pedestrians.md §2.1). Порядок рёбер: тротуары (по сегменту и стороне),
/// обходы тупиков, переходы узлов (по слоту рукава), переходы на перегоне (по номеру).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PedEdgeData
{
    public int A, B;                // узлы графа пешеходов (углы и «средние» узлы)
    public int LenMm;               // длина по полилинии (переход: 2·h + ширина тротуара), мм
    public int CostTicks;           // статический приор стоимости, тики (длина / 1.3 м/с + ожидание); измеренную часть добавит P3
    public PedEdgeKind Kind;
    public byte Side;               // тротуар: 0 справа от A→B, 1 слева; иначе 0
    public byte Flags;              // бит0 - у перехода есть светофор (узел Signal)
    public byte Reserved;
    public int Segment;             // сегмент-владелец (тротуар, переход на перегоне, рукав перехода) или -1
    public int Node;                // узел (Crossing, EndCap) или -1
    public int Slot;                // Crossing/EndCap: слот рукава в NodeArms; MidCrossing: номер перехода m; иначе -1
    public float S0, S1;            // тротуар: диапазон по оси сегмента, м; MidCrossing: S0 = S1 = s перехода
    public int FirstPoint, PointCount; // полилиния в PedEdgePoints
}
