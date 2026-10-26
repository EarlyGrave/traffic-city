using System.Numerics;
using Sim.Core.Agents;
using Sim.Core.Util;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Задание «сбор кандидатов по узлам» фазы 4 (scale.md §4, ADR-0017). Элемент - номер в списке активных узлов; для узла обходятся его занятые полосы
/// (CSR, по возрастанию индекса) и connector'ы. Пишет только данные своего узла и буфер воркера; слияние - на главном потоке (<c>GatePhase</c>).
/// </summary>
internal sealed class GateScanJob : IRangeJob
{
    private World _w = null!;
    private RuleEnv _e;
    private byte[] _lodBits = System.Array.Empty<byte>();
    private byte[] _laneColor = System.Array.Empty<byte>();
    private int _beat;
    private int _stamp;
    private float _far;
    private float _minDecel;

    public void Prepare(World w, in RuleEnv e, int stamp, float farDistance, float minDecel, int beat)
    {
        _w = w;
        _e = e;
        _lodBits = w.Vehicles.Lod;
        _laneColor = w.Lod?.LaneColor ?? System.Array.Empty<byte>();
        _beat = beat;
        _stamp = stamp;
        _far = farDistance;
        _minDecel = minDecel;
    }

    public void RunRange(int worker, int begin, int end)
    {
        World w = _w;
        GateScratch g = w.Gate;
        GateWorker gw = g.Workers[worker];
        ulong[] occMask = w.Occupancy.OccMask;
        ulong[] conn = g.ConnMask;
        Lod.QueueMeso? q = w.Queue;
        ulong[]? qKin = q?.KinMask;
        ulong[]? qHead = q?.HeadMask[_beat];
        int[] start = g.NodeLaneStart;
        int[] lanes = g.NodeLanes;
        int cand = 0;
        for (int k = begin; k < end; k++)
        {
            int node = g.ActiveNodes[k];
            for (int c = start[node]; c < start[node + 1]; c++)
            {
                int lane = lanes[c];
                int wi = lane >> 6;
                ulong bit = 1UL << (lane & 63);
                if ((occMask[wi] & bit) == 0UL || (qKin != null && ((qKin[wi] | qHead![wi]) & bit) == 0UL))
                {
                    continue;
                }

                if ((conn[wi] & bit) != 0UL)
                {
                    GatePhase.ScanConnector(w, gw, lane);
                }
                else
                {
                    cand += GatePhase.ScanGatedLane(w, in _e, gw, lane, _lodBits, _laneColor, _beat, _stamp, _far, _minDecel);
                }
            }
        }

        gw.Candidates += cand;
    }
}

/// <summary>Задание «разрешение узлов» фазы 4: сортировка кандидатов узла и <c>TryGrant</c> по порядку (элемент - номер в <c>TouchedNodes</c>).</summary>
internal sealed class GateResolveJob : IRangeJob
{
    private World _w = null!;
    private RuleEnv _e;

    public void Prepare(World w, in RuleEnv e)
    {
        _w = w;
        _e = e;
    }

    public void RunRange(int worker, int begin, int end) => GatePhase.ResolveNodes(_w, in _e, _w.Gate.Workers[worker], begin, end);
}
