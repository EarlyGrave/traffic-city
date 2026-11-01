using System.Numerics;
using Sim.Core.Agents;
using Sim.Core.Network;
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

/// <summary>
/// Задание «сброс по списку прошлого тика» фазы 4 (S4c): элемент - позиция в <c>PrevList</c>. Сбрасывает <c>WaitTicks</c>/<c>DenyReason</c>/допуск машины,
/// которая кандидатом больше не является; пишет только данные своей машины и <c>PrevKeep[k]</c>.
/// </summary>
internal sealed class GatePrevResetJob : IRangeJob
{
    private World _w = null!;
    private byte[] _lodBits = System.Array.Empty<byte>();
    private byte[] _laneColor = System.Array.Empty<byte>();
    private int _stamp;
    private int _beat;

    public void Prepare(World w, int stamp, int beat)
    {
        _w = w;
        _lodBits = w.Vehicles.Lod;
        _laneColor = w.Lod?.LaneColor ?? System.Array.Empty<byte>();
        _stamp = stamp;
        _beat = beat;
    }

    public void RunRange(int worker, int begin, int end)
    {
        World w = _w;
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        GateScratch g = w.Gate;
        int[] prev = g.PrevList;
        byte[] keep = g.PrevKeep;
        for (int k = begin; k < end; k++)
        {
            keep[k] = 0;
            int i = prev[k];
            if (g.CandStamp[i] == _stamp)
            {
                continue;   // кандидат и в этом тике: WaitTicks уже увеличен, запись в NextList есть
            }

            int lane = v.Lane[i];
            if (!v.Pool.IsAlive(i) || lane < 0)
            {
                v.WaitTicks[i] = 0;
                g.DenyReason[i] = (byte)GateDeny.None;
                continue;
            }

            if (net.Lanes[lane].Kind == LaneKind.Connector)
            {
                v.WaitTicks[i] = 0;
                g.DenyReason[i] = (byte)GateDeny.None;
                continue;
            }

            if (GateRules.GateNodeOfNormal(net, lane) < 0)
            {
                v.WaitTicks[i] = 0;
                v.GateState[i] = 0;
                v.GrantConnector[i] = -1;
                g.DenyReason[i] = (byte)GateDeny.None;
                continue;
            }

            if (!GateRules.IsGranted(v, i) && _lodBits.Length != 0 && (_lodBits[i] & Lod.LodBits.Meso) != 0 && _laneColor[lane] != _beat)
            {
                keep[k] = 1;   // meso не в свой такт: WaitTicks и DenyReason не трогаем (sim-lod.md §2.4), запись остаётся в списке
                continue;
            }

            v.WaitTicks[i] = 0;
            g.DenyReason[i] = (byte)GateDeny.None;
        }
    }
}
