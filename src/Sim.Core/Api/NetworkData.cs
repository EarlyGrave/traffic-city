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
    public int HubNode;             // узел-хаб зоны для кэша магистралей (routing.md §4.1); -1 — нет
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
