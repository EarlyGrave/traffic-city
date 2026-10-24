using BenchmarkDotNet.Attributes;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Bench;

/// <summary>LaneGeometry.Evaluate по 10 000 полос (сетка 2+2, connector'ы), s и PointHint как у машины.</summary>
[MemoryDiagnoser]
public class NetworkBenchmarks
{
    private NetworkStore _store = null!;
    private int[] _hints = null!;
    private float[] _s = null!;
    private int _lanes;

    [GlobalSetup]
    public void Setup()
    {
        var caps = new SimCapacities
        {
            MaxNodes = 2000,
            MaxSegments = 4000,
            MaxLanes = 40000,
            MaxLanePoints = 400000,
        };
        _store = new NetworkStore(caps);
        var b = new NetworkBuilder(_store);
        const int N = 24;
        for (int i = 0; i < N; i++)
        {
            for (int j = 0; j < N; j++)
            {
                if (i + 1 < N)
                {
                    b.AddSegment(new Vec2(i * 120, j * 120), new Vec2((i + 1) * 120, j * 120), 2, 2, 13.9f, RoadKind.Arterial, out _);
                }

                if (j + 1 < N)
                {
                    b.AddSegment(new Vec2(i * 120, j * 120), new Vec2(i * 120, (j + 1) * 120), 2, 2, 13.9f, RoadKind.Arterial, out _);
                }
            }
        }

        b.BuildAllConnectorsEx();
        _lanes = System.Math.Min(10_000, _store.LaneCount);
        _hints = new int[_lanes];
        _s = new float[_lanes];
        for (int i = 0; i < _lanes; i++)
        {
            _s[i] = _store.Lanes[i].Length * ((i % 97) / 97f);
        }
    }

    [Benchmark]
    public float Evaluate10kLanes()
    {
        float acc = 0f;
        for (int i = 0; i < _lanes; i++)
        {
            LaneGeometry.Evaluate(_store, i, _s[i], ref _hints[i], out Vec2 p, out Vec2 d);
            acc += p.X + d.Z;
        }

        return acc;
    }
}
