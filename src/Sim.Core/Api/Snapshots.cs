using System;
using System.Runtime.InteropServices;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Api;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VehicleRenderData
{
    public float X, Y, Z;            // 0..11  геометрический центр машины, м
    public float Heading;            // 12..15 atan2(dz, dx), радианы
    public float Speed;              // 16..19 м/с
    public int Index;                // 20..23 слот = индекс в этом же массиве
    public ushort Generation;        // 24..25 младшие 16 бит поколения
    public byte Type;                // 26     индекс VehicleTypeDesc
    public byte Flags;               // 27     VehicleRenderFlags
    public float LateralOffset;      // 28..31 м, смещение влево(+)/вправо(-) от оси полосы (уже учтено в X, Z)
}

[Flags]
public enum VehicleRenderFlags : byte
{
    None = 0,
    Active = 1,          // слот живой; если 0 - остальные поля не читать
    Braking = 2,         // a < -1 м/с²
    ChangingLane = 4,
    Stuck = 8,
    AtStop = 16,         // автобус стоит на остановке
    Selected = 32,
    OnConnector = 64,
    Meso = 128,          // машина в meso-ячейке (ADR-0013); L1: бит зарезервирован, ядро его пока не ставит
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public struct SegmentLoad
{
    public float Density;      // машин на полосо-километр (все направления)
    public float SpeedRatio;   // средняя скорость / лимит; 1.0 если машин нет
    public ushort Count;       // машин на сегменте сейчас
    public ushort Reserved;
}

public enum SignalInterval : byte
{
    Green = 0,
    Yellow = 1,
    AllRed = 2,
    Off = 3,
}

public enum LaneSignal : byte
{
    None = 0,
    Red = 1,
    Yellow = 2,
    Green = 3,
}

[StructLayout(LayoutKind.Sequential, Pack = 2, Size = 8)]
public struct SignalState
{
    public int Node;             // индекс узла
    public byte Phase;           // текущая фаза
    public SignalInterval Interval;
    public ushort TicksToChange;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public struct RouteStep
{
    public int Lane;             // полоса (обычная или connector)
    public int Node;             // узел, в который она ведёт (-1 для последнего)
}

/// <summary>Маршрут выбранной машины: срез существующего массива ядра, валиден до следующего Step.</summary>
public readonly ref struct RouteView
{
    public readonly VehicleHandle Vehicle;         // Null, если ничего не выбрано / машина прибыла
    public readonly ReadOnlySpan<RouteStep> Steps; // полный маршрут от начала
    public readonly int Cursor;                    // индекс шага, на котором машина сейчас

    public RouteView(VehicleHandle vehicle, ReadOnlySpan<RouteStep> steps, int cursor)
    {
        Vehicle = vehicle;
        Steps = steps;
        Cursor = cursor;
    }

    public bool IsEmpty => Steps.Length == 0;
}

/// <summary>Статическая геометрия сети для клиента (read-only срезы массивов ядра).</summary>
public readonly ref struct NetworkView
{
    public readonly int Version;                        // ++ при каждом изменении сети
    public readonly ReadOnlySpan<NodeData> Nodes;
    public readonly ReadOnlySpan<SegmentData> Segments;
    public readonly ReadOnlySpan<LaneData> Lanes;
    public readonly ReadOnlySpan<Vec2> LanePoints;      // полилинии полос, диапазоны из LaneData
    public readonly ReadOnlySpan<float> LanePointS;     // кумулятивная длина в каждой точке
    public readonly ReadOnlySpan<ZoneData> Zones;
    public readonly ReadOnlySpan<BusStopData> BusStops;
    public readonly ReadOnlySpan<BusRouteData> BusRoutes;
    public readonly ReadOnlySpan<int> BusRouteStops;    // индексы остановок, диапазоны из BusRouteData

    // Сеть пешеходов (ADR-0020, pedestrians.md §2.4): пусто, если секции peds нет. Статика: читать после Create/Load и при смене Version.
    public readonly ReadOnlySpan<PedEdgeData> Sidewalks;   // тротуары и обходы тупиков (полилинии в PedEdgePoints)
    public readonly ReadOnlySpan<PedEdgeData> Crossings;   // переходы узлов и перегонов (по два конца в PedEdgePoints)
    public readonly ReadOnlySpan<Vec2> PedEdgePoints;
    public readonly ReadOnlySpan<Vec2> PedCorners;         // позиции углов узлов, индекс = слот рукава в NodeArms (углы обходов тупиков - в полилиниях)
    public readonly float SidewalkWidthM;
    public readonly float CrosswalkWidthM;

    // ---- живость и handle'ы (4.3, T2; player-tools.md §3 п. 5): Nodes/Segments/... идут до HighWater пула, мёртвые слоты (Generation чётное) клиент пропускает ----

    /// <summary>Узел <paramref name="i"/> существует (<c>Generation</c> нечётное). Индекс вне диапазона - false.</summary>
    public bool IsNodeAlive(int i) => (uint)i < (uint)Nodes.Length && NetworkAlive.IsAlive(Nodes[i].Generation);

    public bool IsSegmentAlive(int i) => (uint)i < (uint)Segments.Length && NetworkAlive.IsAlive(Segments[i].Generation);

    /// <summary>Полоса живая (мёртвая и запасная полоса блока имеет <c>Generation == 0</c>).</summary>
    public bool IsLaneAlive(int i) => (uint)i < (uint)Lanes.Length && NetworkAlive.IsAlive(Lanes[i].Generation);

    public bool IsBusStopAlive(int i) => (uint)i < (uint)BusStops.Length && NetworkAlive.IsAlive(BusStops[i].Generation);

    public bool IsBusRouteAlive(int i) => (uint)i < (uint)BusRoutes.Length && NetworkAlive.IsAlive(BusRoutes[i].Generation);

    /// <summary>Handle узла для команд и после <c>NodeRemapped</c>: индекс + текущее поколение. Вне диапазона - <see cref="EntityRef.None"/>.</summary>
    public EntityRef NodeRef(int i) => (uint)i < (uint)Nodes.Length ? new EntityRef(i, Nodes[i].Generation) : EntityRef.None;

    public EntityRef SegmentRef(int i) => (uint)i < (uint)Segments.Length ? new EntityRef(i, Segments[i].Generation) : EntityRef.None;

    public EntityRef BusStopRef(int i) => (uint)i < (uint)BusStops.Length ? new EntityRef(i, BusStops[i].Generation) : EntityRef.None;

    public EntityRef BusRouteRef(int i) => (uint)i < (uint)BusRoutes.Length ? new EntityRef(i, BusRoutes[i].Generation) : EntityRef.None;

    public NetworkView(
        int version,
        ReadOnlySpan<NodeData> nodes,
        ReadOnlySpan<SegmentData> segments,
        ReadOnlySpan<LaneData> lanes,
        ReadOnlySpan<Vec2> lanePoints,
        ReadOnlySpan<float> lanePointS,
        ReadOnlySpan<ZoneData> zones,
        ReadOnlySpan<BusStopData> busStops,
        ReadOnlySpan<BusRouteData> busRoutes,
        ReadOnlySpan<int> busRouteStops,
        ReadOnlySpan<PedEdgeData> sidewalks = default,
        ReadOnlySpan<PedEdgeData> crossings = default,
        ReadOnlySpan<Vec2> pedEdgePoints = default,
        ReadOnlySpan<Vec2> pedCorners = default,
        float sidewalkWidthM = 0f,
        float crosswalkWidthM = 0f)
    {
        Version = version;
        Nodes = nodes;
        Segments = segments;
        Lanes = lanes;
        LanePoints = lanePoints;
        LanePointS = lanePointS;
        Zones = zones;
        BusStops = busStops;
        BusRoutes = busRoutes;
        BusRouteStops = busRouteStops;
        Sidewalks = sidewalks;
        Crossings = crossings;
        PedEdgePoints = pedEdgePoints;
        PedCorners = pedCorners;
        SidewalkWidthM = sidewalkWidthM;
        CrosswalkWidthM = crosswalkWidthM;
    }
}

/// <summary>Публичная копия подмножества полей одной машины (для инспектора/HUD).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VehicleInfo
{
    public int Lane;
    public float S;
    public float Speed;
    public float Accel;
    public int RouteCursor;
    public int DestZone;
    public byte Flags;            // VehicleRenderFlags
    public byte Type;
    public ushort Reserved;
    public long SpawnTick;
}
