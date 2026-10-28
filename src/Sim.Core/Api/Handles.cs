using System;
using System.Runtime.InteropServices;

namespace Sim.Core.Api;

// Все handle одинаковы по раскладке (8 байт): int Index; int Generation (core-api 2.1).
// Типы - копии одного шаблона; менять все синхронно.

/// <summary>Handle сущности Vehicle (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct VehicleHandle : IEquatable<VehicleHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public VehicleHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static VehicleHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static VehicleHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(VehicleHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is VehicleHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(VehicleHandle a, VehicleHandle b) => a.Equals(b);

    public static bool operator !=(VehicleHandle a, VehicleHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности Node (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct NodeHandle : IEquatable<NodeHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public NodeHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static NodeHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static NodeHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(NodeHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is NodeHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(NodeHandle a, NodeHandle b) => a.Equals(b);

    public static bool operator !=(NodeHandle a, NodeHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности Segment (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct SegmentHandle : IEquatable<SegmentHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public SegmentHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static SegmentHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static SegmentHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(SegmentHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is SegmentHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(SegmentHandle a, SegmentHandle b) => a.Equals(b);

    public static bool operator !=(SegmentHandle a, SegmentHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности Lane (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct LaneHandle : IEquatable<LaneHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public LaneHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static LaneHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static LaneHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(LaneHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is LaneHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(LaneHandle a, LaneHandle b) => a.Equals(b);

    public static bool operator !=(LaneHandle a, LaneHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности Zone (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct ZoneHandle : IEquatable<ZoneHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public ZoneHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static ZoneHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static ZoneHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(ZoneHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is ZoneHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(ZoneHandle a, ZoneHandle b) => a.Equals(b);

    public static bool operator !=(ZoneHandle a, ZoneHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности BusRoute (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct BusRouteHandle : IEquatable<BusRouteHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public BusRouteHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static BusRouteHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static BusRouteHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(BusRouteHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is BusRouteHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(BusRouteHandle a, BusRouteHandle b) => a.Equals(b);

    public static bool operator !=(BusRouteHandle a, BusRouteHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности BusStop (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct BusStopHandle : IEquatable<BusStopHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public BusStopHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static BusStopHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static BusStopHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(BusStopHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is BusStopHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(BusStopHandle a, BusStopHandle b) => a.Equals(b);

    public static bool operator !=(BusStopHandle a, BusStopHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности Route (index + generation; Generation == 0 - Null).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct RouteHandle : IEquatable<RouteHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public RouteHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static RouteHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static RouteHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(RouteHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is RouteHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(RouteHandle a, RouteHandle b) => a.Equals(b);

    public static bool operator !=(RouteHandle a, RouteHandle b) => !a.Equals(b);
}

/// <summary>Handle сущности Pedestrian (index + generation; Generation == 0 - Null). Пешеходы живут только при секции <c>peds</c> (ADR-0020).</summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct PedHandle : IEquatable<PedHandle>
{
    public readonly int Index;
    public readonly int Generation;

    public PedHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static PedHandle Null => default;

    public bool IsNull => Generation == 0;

    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;

    public static PedHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(PedHandle o) => Index == o.Index && Generation == o.Generation;

    public override bool Equals(object? obj) => obj is PedHandle h && Equals(h);

    public override int GetHashCode() => Index ^ (Generation * 397);

    public static bool operator ==(PedHandle a, PedHandle b) => a.Equals(b);

    public static bool operator !=(PedHandle a, PedHandle b) => !a.Equals(b);
}
