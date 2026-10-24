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
        ReadOnlySpan<int> busRouteStops)
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
