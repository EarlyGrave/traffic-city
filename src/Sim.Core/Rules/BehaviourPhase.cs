using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;
using Sim.Core.Util;

namespace Sim.Core.Rules;

/// <summary>
/// Фаза 5 (core-api §7): поведение. Read-only по позициям/скоростям/спискам; пишет только
/// <c>Accel[]</c>, <c>LaneChangeDir[]</c> (намерение смены полосы, исполняет фаза 7) и зазоры клэмпа I6 для фазы 6
/// (<c>ClampLeadGap</c>, <c>ClampFlags</c>, <c>ClampGateGap</c>, ADR-0016 D2) - только элементы <c>[i]</c> своей машины,
/// поэтому фаза считается чанками индексов на воркерах ядра (ADR-0017, <see cref="BehaviourJob"/>).
/// IDM считается каждый тик; MOBIL - раз в <c>MobilEveryTicks</c> на машину, разнесено по индексу: <c>(tick + i) % N == 0</c>.
/// Перекрёстки (intersections.md §7, при переданном <see cref="ConnectorStore"/>): IDM берёт минимум ускорения по обычному
/// лидеру и по виртуальному препятствию ворот - стоп-линии без допуска (в пределах дистанции решения) либо конфликтной точке,
/// занятой машиной с меньшим билетом (<see cref="GateRules.ObstacleGap"/>).
/// </summary>
internal static class BehaviourPhase
{
    // Задание для перегрузок без World (тесты, микробенчмарки): один экземпляр на поток, без аллокаций после первого вызова.
    [ThreadStatic]
    private static BehaviourJob? _single;

    /// <summary>Готовит предвыделенное задание фазы 5 к текущему тику (для <see cref="Run(World)"/> и тестов порядка обхода).</summary>
    public static BehaviourJob Prepare(World w)
    {
        BehaviourJob job = w.Behaviour;
        job.Prepare(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w), w.Types, PhysicsParams.From(w), w.Tick, w.Connectors, w.Gate.NodeBusy);
        job.PrepareCross(w.Peds?.X, w.Peds?.Net);
        job.PrepareYield(w.BayYieldActive > 0 ? w.LaneYieldS : null);
        if (w.Lod != null)
        {
            job.PrepareLod(w.Lod.LaneColor, w.LodCfg.MesoHeadwayScale, w.LodCfg.MesoAccelScale);
        }

        return job;
    }

    public static void Run(World w)
    {
        BehaviourJob job = Prepare(w);
        int hw = w.Vehicles.HighWater;
        if (w.Pool != null)
        {
            w.Pool.Run(job, hw);
        }
        else
        {
            job.RunRange(0, 0, hw);
        }

        job.Collect(ref w.Work);
    }

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        VehicleTypeDesc[] types,
        in PhysicsParams p,
        long tick,
        ConnectorStore? connectors = null)
    {
        SimWork scratch = default;
        Run(net, v, occ, routes, types, p, tick, ref scratch, connectors, null);
    }

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        VehicleTypeDesc[] types,
        in PhysicsParams p,
        long tick,
        ref SimWork work,
        ConnectorStore? connectors,
        ulong[]? nodeBusy)
    {
        BehaviourJob job = _single ??= new BehaviourJob(1);
        job.Prepare(net, v, occ, routes, types, p, tick, connectors, nodeBusy);
        job.RunRange(0, 0, v.HighWater);
        job.Collect(ref work);
    }
}

/// <summary>
/// Задание фазы 5 для <see cref="WorkerPool"/> (ADR-0017): предвыделено в <see cref="World"/>, на тик только <see cref="Prepare"/>.
/// Функция машины <see cref="Vehicle"/> читает состояние, которое в фазе никто не пишет, и пишет только свои слоты; счётчики работы
/// копятся в экземпляре <see cref="SimWork"/> воркера (целые, сумма от порядка не зависит) и сводятся в <see cref="Collect"/>.
/// </summary>
internal sealed partial class BehaviourJob : IRangeJob
{
    private readonly SimWork[] _acc;
    private RuleEnv _e;
    private VehicleTypeDesc[] _types = Array.Empty<VehicleTypeDesc>();
    private PhysicsParams _p;
    private long _tick;
    private ConnectorStore? _connectors;
    private ulong[]? _nodeBusy;
    private byte[] _lod = Array.Empty<byte>();           // VehicleStore.Lod; длина 0 - LOD выключен (ветка meso не исполняется)
    private byte[] _laneColor = Array.Empty<byte>();     // LodCells.LaneColor (meso, MesoBehaviour.cs)
    private float _mesoHeadwayScale = 1f;
    private float _mesoAccelScale = 1f;
    private Peds.CrossingState? _xs;                     // состояние переходов (pedestrians.md §5.6); null - пешеходов нет
    private Peds.PedNetwork? _pn;

    /// <summary>Задаёт переходы (null без <c>peds</c>) к текущему тику: препятствия закрытых переходов на перегоне.</summary>
    private float[]? _yield;                             // World.LaneYieldS: стена уступки автобусу из кармана по полосам (ADR-0029); null - уступок нет

    public void PrepareYield(float[]? laneYieldS) => _yield = laneYieldS;

    public void PrepareCross(Peds.CrossingState? xs, Peds.PedNetwork? pn)
    {
        _xs = xs;
        _pn = pn;
    }

    public BehaviourJob(int workers)
    {
        _acc = new SimWork[Math.Max(1, workers)];
    }

    public void Prepare(
        NetworkStore net,
        VehicleStore v,
        LaneOccupancy occ,
        in RouteAccess routes,
        VehicleTypeDesc[] types,
        in PhysicsParams p,
        long tick,
        ConnectorStore? connectors,
        ulong[]? nodeBusy)
    {
        _e = new RuleEnv(net, v, occ, routes);
        _types = types;
        _p = p;
        _tick = tick;
        _connectors = connectors;
        _nodeBusy = nodeBusy;
        _lod = v.Lod;
        _laneColor = Array.Empty<byte>();
    }

    public void RunRange(int worker, int begin, int end)
    {
        SimWork cw = default;
        int vehicles = 0;
        int hops = 0;
        int obstacleCalls = 0;
        ulong[] queue = _e.V.QueueBits;
        if (queue.Length == 0)
        {
            for (int i = begin; i < end; i++)
            {
                Vehicle(i, ref cw, ref vehicles, ref hops, ref obstacleCalls);
            }
        }
        else
        {
            // Q-meso: машины очереди фаза 5 не трогает - обход по маске «не очередь» (чанки кратны 64 по началу, конец - отсекается).
            for (int k = begin >> 6; k << 6 < end; k++)
            {
                ulong m = ~queue[k];
                int hi = end - (k << 6);
                if (hi < 64)
                {
                    m &= (1UL << hi) - 1UL;
                }

                int lo = begin - (k << 6);
                if (lo > 0)
                {
                    m &= ulong.MaxValue << lo;
                }

                while (m != 0UL)
                {
                    int i = (k << 6) + System.Numerics.BitOperations.TrailingZeroCount(m);
                    m &= m - 1UL;
                    Vehicle(i, ref cw, ref vehicles, ref hops, ref obstacleCalls);
                }
            }
        }

        cw.BehaviourVehicles += vehicles;
        cw.LeaderLookaheadHops += hops;
        cw.ObstacleGapCalls += obstacleCalls;
        cw.ConflictGapCallsBehaviour += cw.ConflictGapCallsRaw;
        _acc[worker].Add(in cw);
    }

    /// <summary>Складывает счётчики воркеров в <paramref name="work"/> и обнуляет их.</summary>
    public void Collect(ref SimWork work)
    {
        for (int k = 0; k < _acc.Length; k++)
        {
            work.Add(in _acc[k]);
            _acc[k] = default;
        }
    }

    private void Vehicle(int i, ref SimWork work, ref int vehicles, ref int hops, ref int obstacleCalls)
    {
        RuleEnv e = _e;
        NetworkStore net = e.Net;
        VehicleStore v = e.V;
        if (!v.Pool.IsAlive(i))
        {
            return;
        }

        if (v.Lane[i] < 0)
        {
            v.Accel[i] = 0f;
            v.LaneChangeDir[i] = 0;
            return;
        }

        if (_lod.Length != 0 && (_lod[i] & Lod.LodBits.Meso) != 0)
        {
            MesoVehicle(i, ref work, ref vehicles, ref hops, ref obstacleCalls);
            return;
        }

        ref readonly VehicleTypeDesc t = ref _types[v.Type[i]];
        bool atStop = (v.Flags[i] & (byte)VehicleFlags.AtStop) != 0;
        if (!atStop)
        {
            vehicles++;
        }

        // Зазоры клэмпа I6 для фазы 6 (ADR-0016, D2): по позициям на начало тика, для каждой машины фазы 6, включая автобус на остановке.
        LeaderInfo li = LeaderSearch.FindFor(e, v.S, i);
        hops += li.Hops;
        v.ClampLeadGap[i] = li.Gap;
        v.ClampFlags[i] = (byte)((li.Vehicle != -1 || li.Virtual ? IntegratePhase.ClampLeaderPresent : 0) | (li.Virtual ? IntegratePhase.ClampLeaderVirtual : 0));
        float vi = v.Speed[i];
        float gateGap = Idm.FreeGap;
        float gateSpeed = 0f;
        bool beyondDecision = false;
        if (_connectors != null)
        {
            obstacleCalls++;
            gateGap = GateRules.ObstacleGap(net, v, e.Occ, _connectors, _nodeBusy, ref work, v.S, i, t.ComfortDecel, out beyondDecision, out gateSpeed);
        }

        if (_xs != null)
        {
            float cg = Peds.PedRules.MidblockGap(net, _pn!, _xs, v.Lane[i], v.S[i], vi, t.ComfortDecel);
            if (cg < gateGap)
            {
                gateGap = cg;
                gateSpeed = 0f;
                beyondDecision = false;
                v.ClampFlags[i] |= IntegratePhase.ClampCross;
            }
        }

        v.ClampGateGap[i] = gateGap;
        if (atStop)
        {
            v.Accel[i] = 0f; // автобус стоит на остановке (посадка)
            v.LaneChangeDir[i] = 0;
            return;
        }

        float a = Idm.Accel(t, vi, v.DesiredSpeed[i], li.Gap, vi - li.Speed);
        if (gateGap < Idm.FreeGap && !beyondDecision)
        {
            float ag = Idm.Accel(t, vi, v.DesiredSpeed[i], gateGap, vi - gateSpeed);
            if (ag < a)
            {
                a = ag;
            }
        }

        if (_yield != null)
        {
            // Уступка автобусу, выезжающему из кармана (ПДД 18.3, bus-bays.md §2 п. 5): стена у тыла автобуса; только тем, кто успевает встать с комфортным торможением.
            // Не безопасность (клэмп I6 её не знает), а вежливость.
            float wall = _yield[v.Lane[i]];
            if (wall >= 0f && !GateRules.IsGranted(v, i))   // допущенная через ворота машина стену не видит: иначе она держала бы чужой проезд, пока автобус ждёт
            {
                float yg = wall - v.S[i];
                if (yg > t.MinGap && (vi * vi) <= 2f * t.ComfortDecel * (yg - t.MinGap))
                {
                    float ay = Idm.Accel(t, vi, v.DesiredSpeed[i], yg, vi);
                    if (ay < a)
                    {
                        a = ay;
                    }
                }
            }
        }

        if ((v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
        {
            float stopGap = BusStopRules.ObstacleGap(net, v, i, t.MinGap);
            if (stopGap < Idm.FreeGap)
            {
                float ab = Idm.Accel(t, vi, v.DesiredSpeed[i], stopGap, vi);
                if (ab < a)
                {
                    a = ab;
                }
            }
        }

        v.Accel[i] = a;

        int dir = 0;
        if ((_tick + i) % _p.MobilEveryTicks == 0)
        {
            dir = Mobil.Decide(e, _types, _p, i, a);
        }

        v.LaneChangeDir[i] = (sbyte)dir;
    }
}
