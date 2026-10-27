using System;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Динамика инфраструктуры пешеходов: маркеры Walk/Clear/DontWalk у светофорных переходов (по <c>CrossingStates</c>) и столбики ожидающих
/// на остановках (по <c>BusStopWaiting</c>). Статика (тротуары, зебры, heatmap <c>PedEdgeCounts</c>) - <see cref="PedChunks"/>.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены и освобождаются вместе с ними
public sealed class CrosswalkMesh
{
    private const float MarkerSize = 0.5f;

    private static readonly Color _dontWalk = Palette.SignalDontWalk;
    private static readonly Color _walk = Palette.SignalWalk;
    private static readonly Color _clear = Palette.SignalClear;
    private static readonly Color _stopQueue = Palette.StopQueue;

    private readonly PedEdgeData[] _crossings;
    private readonly Vec2[] _points;
    private readonly int _armBase;
    private readonly Vec2[] _stopPos;

    private readonly BoxBatch _markers;
    private readonly BoxBatch _queues;

    public CrosswalkMesh(Simulation sim)
    {
        NetworkView net = sim.Network;
        _crossings = net.Crossings.ToArray();
        _points = net.PedEdgePoints.ToArray();
        for (int i = 0; i < net.Nodes.Length; i++)
        {
            _armBase += net.Nodes[i].ArmCount;
        }

        ReadOnlySpan<BusStopData> stops = net.BusStops;
        _stopPos = new Vec2[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            _stopPos[i] = LanePointAt(net, stops[i].Lane, stops[i].S);
        }

        _markers = new BoxBatch("CrossSignals", new Vector3(MarkerSize, 1.8f, MarkerSize), true);
        _queues = new BoxBatch("StopQueues", new Vector3(1f, 1f, 1f), false);
    }

    public MultiMeshInstance3D Markers => _markers.Node;

    public MultiMeshInstance3D Queues => _queues.Node;

    /// <summary>Сумма по <c>BusStopWaiting</c> (для HUD), пересчитывается в <see cref="UpdateQueues"/>.</summary>
    public int StopWaitingTotal { get; private set; }

    /// <summary>Маркеры Walk/Clear/DontWalk у светофорных переходов внутри прямоугольника вида (мировые координаты).</summary>
    public void UpdateMarkers(Simulation sim, float x0, float z0, float x1, float z1)
    {
        ReadOnlySpan<byte> states = sim.CrossingStates;
        int needed = 0;
        for (int j = 0; j < _crossings.Length; j++)
        {
            if ((_crossings[j].Flags & 1) != 0 && InView(j, x0, z0, x1, z1))
            {
                needed += 2;
            }
        }

        _markers.Begin(needed);
        for (int j = 0; j < _crossings.Length; j++)
        {
            ref readonly PedEdgeData e = ref _crossings[j];
            if ((e.Flags & 1) == 0 || !InView(j, x0, z0, x1, z1))
            {
                continue;
            }

            int x = e.Kind == PedEdgeKind.Crossing ? e.Slot : _armBase + e.Slot;
            int sig = x >= 0 && x < states.Length ? (states[x] >> 2) & 3 : 0;
            Color col = sig switch
            {
                1 => _walk,
                2 => _clear,
                _ => _dontWalk,
            };
            Vec2 a = _points[e.FirstPoint];
            Vec2 b = _points[e.FirstPoint + e.PointCount - 1];
            _markers.Add(a.X, 1.1f, a.Z, 0f, col);
            _markers.Add(b.X, 1.1f, b.Z, 0f, col);
        }

        _markers.End();
    }

    /// <summary>Кубы над остановками: размер растёт с числом ожидающих (<c>BusStopWaiting</c>).</summary>
    public void UpdateQueues(Simulation sim)
    {
        ReadOnlySpan<ushort> waiting = sim.BusStopWaiting;
        int n = Math.Min(waiting.Length, _stopPos.Length);
        int needed = 0;
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += waiting[i];
            if (waiting[i] > 0)
            {
                needed++;
            }
        }

        StopWaitingTotal = total;
        _queues.Begin(needed);
        for (int i = 0; i < n; i++)
        {
            if (waiting[i] == 0)
            {
                continue;
            }

            float s = 1.5f + MathF.Sqrt(waiting[i]);
            _queues.Add(_stopPos[i].X, (s * 0.5f) + 2f, _stopPos[i].Z, 0f, _stopQueue, s);
        }

        _queues.End();
    }

    private static Vec2 LanePointAt(NetworkView net, int lane, float s)
    {
        if ((uint)lane >= (uint)net.Lanes.Length)
        {
            return default;
        }

        LaneData ld = net.Lanes[lane];
        if (ld.PointCount == 0)
        {
            return default;
        }

        int last = ld.FirstPoint + ld.PointCount - 1;
        for (int k = ld.FirstPoint; k < last; k++)
        {
            float s0 = net.LanePointS[k];
            float s1 = net.LanePointS[k + 1];
            if (s <= s1)
            {
                float t = s1 > s0 ? Math.Clamp((s - s0) / (s1 - s0), 0f, 1f) : 0f;
                Vec2 p0 = net.LanePoints[k];
                Vec2 p1 = net.LanePoints[k + 1];
                return new Vec2(p0.X + ((p1.X - p0.X) * t), p0.Z + ((p1.Z - p0.Z) * t));
            }
        }

        return net.LanePoints[last];
    }

    private bool InView(int crossing, float x0, float z0, float x1, float z1)
    {
        PedEdgeData e = _crossings[crossing];
        Vec2 a = _points[e.FirstPoint];
        return a.X >= x0 && a.X <= x1 && a.Z >= z0 && a.Z <= z1;
    }
}
