using System;
using BenchmarkDotNet.Attributes;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Util;

namespace Sim.Bench;

/// <summary>Шаг фаз 5-7 (поведение, интеграция, переходы) на 1000 машинах на двухполосном кольце.</summary>
[MemoryDiagnoser]
public class IdmBenchmarks
{
    private const int Cars = 1000;
    private const int Nodes = 48;

    private NetworkStore _net = null!;
    private VehicleStore _v = null!;
    private LaneOccupancy _occ = null!;
    private VehicleTypeDesc[] _types = null!;
    private uint[] _throughput = null!;
    private PhysicsParams _p;
    private SimCounters _c;
    private long _tick;

    [GlobalSetup]
    public void Setup()
    {
        var caps = new SimCapacities { MaxNodes = Nodes + 2, MaxSegments = Nodes + 2, MaxLanes = 1024, MaxLanePoints = 32768 };
        _net = new NetworkStore(caps);
        var b = new NetworkBuilder(_net);
        const float radius = 1900f; // ~12 км по оси, 2 полосы: ~24 м полосы на машину
        for (int i = 0; i < Nodes; i++)
        {
            double a = 2.0 * Math.PI * i / Nodes;
            b.AddNode(new Vec2((float)(radius * Math.Cos(a)), (float)(radius * Math.Sin(a))), NodeControl.None, out _);
        }

        for (int i = 0; i < Nodes; i++)
        {
            b.AddSegment(i, (i + 1) % Nodes, 2, 0, 15f, RoadKind.Street, out _);
        }

        b.BuildAllConnectorsEx();
        _types = new[] { Car() };
        _throughput = new uint[Nodes + 2];
        _p = new PhysicsParams(PhysicsParams.DefaultDt, 0.5f, 10, 0.1f, 3.5f);
        _v = new VehicleStore(Cars + 8);
        _occ = new LaneOccupancy(_v, _net.LaneCount);

        int lane0 = _net.Segments[0].FirstLaneAB;
        float period = _net.Lanes[lane0].Length + _net.Lanes[_net.LaneNext[_net.Lanes[lane0].FirstNext]].Length;
        float total = Nodes * period;
        int perLane = Cars / 2;
        float spacing = total / perLane;
        for (int laneNo = 0; laneNo < 2; laneNo++)
        {
            for (int k = perLane - 1; k >= 0; k--)
            {
                float pos = (k * spacing) + (laneNo * 0.5f * spacing);
                int seg = (int)(pos / period) % Nodes;
                float off = pos - (seg * period);
                int lane = _net.Segments[seg].FirstLaneAB + laneNo;
                if (off >= _net.Lanes[lane].Length)
                {
                    off -= _net.Lanes[lane].Length;
                    lane = _net.LaneNext[_net.Lanes[lane].FirstNext];
                }

                _v.TrySpawn(out VehicleHandle h);
                _v.S[h.Index] = off;
                _v.Speed[h.Index] = 10f;
                _v.Length[h.Index] = _types[0].Length;
                _v.DesiredSpeed[h.Index] = 15f;
                _occ.InsertSorted(lane, h.Index);
            }
        }

        for (int n = 0; n < 400; n++)
        {
            Step();
        }
    }

    [Benchmark]
    public void Phases5To7_1000Cars() => Step();

    private void Step()
    {
        BehaviourPhase.Run(_net, _v, _occ, RouteAccess.None, _types, _p, _tick);
        IntegratePhase.Run(_net, _v, _types, _p, ref _c);
        TransitionPhase.Run(_net, _v, _occ, RouteAccess.None, _types, _p, _throughput, ref _c);
        _tick++;
    }

    private static VehicleTypeDesc Car() => new()
    {
        Category = VehicleCategory.Car,
        Length = 4.5f,
        Width = 1.8f,
        MaxSpeed = 36f,
        SpeedFactor = 1f,
        Accel = 1f,
        ComfortDecel = 1.5f,
        HeadwayTime = 1.2f,
        MinGap = 2f,
        Delta = 4f,
        Politeness = 0.3f,
        LaneChangeThreshold = 0.2f,
        SafeDecel = 4f,
        RightBias = 0.3f,
        LaneChangeDurationTicks = 40,
    };
}
