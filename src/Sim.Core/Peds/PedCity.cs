using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Заявка ноги жителя города (<c>peds.source = "city"</c>, zoning-demand.md §4.1): точки на тротуарах (ребро, смещение от <c>A</c>, мм), способ и остановки посадки/высадки.
/// Лежит в FIFO <see cref="PedWorld.LegRequests"/>; запись устарела, если житель уже не ждёт именно эту заявку (<see cref="TripLegs.IsLiveRequest"/>) - такие пропускаются при выборке.
/// </summary>
internal struct LegRequest
{
    public int Resident;
    public long ReqTick;
    public int FromEdge, FromSMm;
    public int ToEdge, ToSMm;
    public byte Mode;          // TravelMode: Walk или Bus
    public int StopA, StopB;   // Bus: глобальные индексы остановок посадки и высадки, иначе -1
}

/// <summary>
/// Ноги жителей города в мире пешеходов (Z2): очередь заявок, выпуск пешеходов фазой 2d вместо заготовки, выбор пары остановок по правилу P4
/// (<c>pedestrians.md</c> §3 п. 3: минимум <c>walk(A) + walk(B) + ride + headway/2</c>, при равенстве меньший <c>(A, B)</c>; точки - участки дома и цели, не центры зон).
/// </summary>
internal sealed partial class PedWorld
{
    private const float RideSpeedMps = 8f;

    /// <summary>FIFO заявок ног города; пуст при <c>peds.source = "preform"</c>.</summary>
    public readonly RingBuffer<LegRequest> LegRequests;

    // Геометрия остановок для выбора пары (производное, пересчитывается при смене сети пешеходов).
    private float[] _stopX = Array.Empty<float>();
    private float[] _stopZ = Array.Empty<float>();
    private int[] _stopComp = Array.Empty<int>();
    private float[] _stopCum = Array.Empty<float>();
    private long _stopStamp = -1;

    /// <summary>Источник поездок - жители города.</summary>
    public bool CitySource => Cfg.Source == PedSource.City;

    /// <summary>
    /// Заявка ноги жителя города (FIFO, фаза 2d выпускает пешеходов по порядку). false - очередь полна (вызывающий оставляет жителя в <c>WaitDepart</c> и повторит заявку позже).
    /// </summary>
    public bool RequestLeg(int resident, long reqTick, int fromEdge, int fromSMm, int toEdge, int toSMm, TravelMode mode, int stopA, int stopB)
    {
        var req = new LegRequest
        {
            Resident = resident,
            ReqTick = reqTick,
            FromEdge = fromEdge,
            FromSMm = fromSMm,
            ToEdge = toEdge,
            ToSMm = toSMm,
            Mode = (byte)mode,
            StopA = stopA,
            StopB = stopB,
        };
        return LegRequests.TryEnqueue(in req);
    }

    /// <summary>Фаза 2d при <c>peds.source = "city"</c>: до 64 пешеходов за тик из головы FIFO заявок, ограничение - <c>max_peds</c> (пул); устаревшие заявки пропускаются.</summary>
    public void SpawnCityLegs(World w)
    {
        int budget = PedSpawn.PerTickLimit;
        while (budget > 0 && LegRequests.TryPeek(out LegRequest req))
        {
            if (!TripLegs.IsLiveRequest(w, in req))
            {
                LegRequests.TryDequeue(out _);
                continue;
            }

            if (!P.TryAllocate(out int p))
            {
                w.Counters.PedAllocFailed++;
                break;
            }

            LegRequests.TryDequeue(out _);
            budget--;
            P.Resident[p] = req.Resident;
            P.State[p] = (byte)PedState.PendingPath;
            P.Leg[p] = (byte)PedLeg.Direct;
            P.Edge[p] = req.FromEdge;
            P.S0Mm[p] = req.FromSMm;
            P.GoalEdge[p] = req.ToEdge;
            P.GoalSMm[p] = req.ToSMm;
            if (req.Mode == (byte)TravelMode.Bus && Stops.AccessPoint(w, req.StopA, out int edge, out int sMm))
            {
                P.Leg[p] = (byte)PedLeg.ToStop;
                P.GoalEdge[p] = edge;
                P.GoalSMm[p] = sMm;
            }

            P.T0[p] = w.Tick;
            P.TripTick[p] = w.Tick;
            P.VMmT[p] = Cfg.SpeedMinMmPerTick + (int)w.RngPeds.NextBelow((uint)(Cfg.SpeedMaxMmPerTick - Cfg.SpeedMinMmPerTick + 1));
            w.Counters.PedSpawned++;
            EnqueuePath(p);
            TripLegs.OnPedSpawned(w, req.Resident, p);
        }
    }

    /// <summary>Пара остановок для ноги между двумя точками тротуара; false - автобуса нет (нет маршрутов, остановки не в радиусе или другая компонента).</summary>
    public bool FindBusPair(World w, int fromEdge, int fromSMm, int toEdge, int toSMm, out int stopA, out int stopB)
    {
        stopA = -1;
        stopB = -1;
        NetworkStore net = w.Net;
        if (w.TransitCfg.PassengerModel != PassengerModel.Agents || net.BusRouteCount == 0 || net.BusStopCount == 0 || Cfg.MaxWalkToStopMm <= 0 || Cfg.SpeedMaxMmPerTick <= 0 ||
            (uint)fromEdge >= (uint)Net.EdgeCount || (uint)toEdge >= (uint)Net.EdgeCount)
        {
            return false;
        }

        EnsureStopGeometry(w);
        int hint = 0;
        Net.Evaluate(fromEdge, fromSMm, false, ref hint, out float fx, out float fz, out _);
        hint = 0;
        Net.Evaluate(toEdge, toSMm, false, ref hint, out float tx, out float tz, out _);
        int compF = Net.PedComp[Net.Edges[fromEdge].A];
        int compT = Net.PedComp[Net.Edges[toEdge].A];
        if (compF < 0 || compF != compT)
        {
            return false;
        }

        float radius = Cfg.MaxWalkToStopMm * 0.001f;
        float vNom = ((Cfg.SpeedMinMmPerTick + Cfg.SpeedMaxMmPerTick) * 0.5f) * 20f * 0.001f;
        float best = float.MaxValue;
        int ns = net.BusStopCount;
        for (int a = 0; a < ns; a++)
        {
            if (_stopComp[a] != compF)
            {
                continue;
            }

            float walkA = Dist(fx, fz, _stopX[a], _stopZ[a]);
            if (walkA > radius)
            {
                continue;
            }

            int route = net.BusStops[a].Route;
            ref readonly BusRouteData br = ref net.BusRoutes[route];
            int last = br.FirstStop + br.StopCount;
            for (int b = a + 1; b < last; b++)
            {
                if (_stopComp[b] != compF || net.BusStops[b].Route != route)
                {
                    continue;
                }

                float walkB = Dist(tx, tz, _stopX[b], _stopZ[b]);
                if (walkB > radius)
                {
                    continue;
                }

                float cost = ((walkA + walkB) / vNom) + ((_stopCum[b] - _stopCum[a]) / RideSpeedMps) + (br.HeadwayTicks * 0.05f * 0.5f);
                if (cost < best)
                {
                    best = cost;
                    stopA = a;
                    stopB = b;
                }
            }
        }

        return stopA >= 0;
    }

    private static float Dist(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private void EnsureStopGeometry(World w)
    {
        NetworkStore net = w.Net;
        long stamp = ((long)w.Counters.PedNetResets << 32) | (uint)net.BusStopCount;
        if (stamp == _stopStamp)
        {
            return;
        }

        int ns = net.BusStopCount;
        if (_stopX.Length < ns)
        {
            _stopX = new float[ns];
            _stopZ = new float[ns];
            _stopComp = new int[ns];
            _stopCum = new float[ns];
        }

        for (int s = 0; s < ns; s++)
        {
            _stopComp[s] = -1;
            _stopCum[s] = 0f;
            if (Stops.AccessPoint(w, s, out int e, out int m))
            {
                int hint = 0;
                Net.Evaluate(e, m, false, ref hint, out _stopX[s], out _stopZ[s], out _);
                _stopComp[s] = Net.PedComp[Net.Edges[e].A];
            }
        }

        for (int r = 0; r < net.BusRouteCount; r++)
        {
            ref readonly BusRouteData br = ref net.BusRoutes[r];
            for (int k = 1; k < br.StopCount; k++)
            {
                int a = br.FirstStop + k - 1;
                int b = br.FirstStop + k;
                _stopCum[b] = _stopCum[a] + Dist(_stopX[a], _stopZ[a], _stopX[b], _stopZ[b]);
            }
        }

        _stopStamp = stamp;
    }
}
