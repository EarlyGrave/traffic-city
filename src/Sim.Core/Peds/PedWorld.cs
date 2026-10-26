using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>Запрос пути в очереди <c>PedPathQueue</c> (индекс и поколение пешехода); сохраняется в блоке <c>PQUE</c>.</summary>
internal struct PathRequest
{
    public int Index;
    public int Generation;
}

/// <summary>
/// Всё состояние пешеходов, жителей, переходов и очередей остановок (ADR-0020, pedestrians.md). Создаётся <see cref="WorldBuilder"/> (и загрузчиком сейва) только
/// при секции <c>peds</c>: без неё <c>World.Peds == null</c> и ни одна ветка пешеходов не исполняется (PD1). Все массивы выделены в конструкторе.
/// <para>
/// Заморожено в P2 для параллельной работы P3 (переходы) и P4 (пассажиры): <see cref="PedStore"/>, <see cref="ResidentStore"/>, <see cref="CrossingState"/>,
/// очереди остановок <see cref="StopQHead"/>/<see cref="StopQTail"/>/<see cref="StopQCount"/> и <see cref="PaxHead"/>, гистограммы ожиданий,
/// счётчики <c>SimCounters</c> (группа «пешеходы»), столбцы сейва, точки расширения <see cref="Crossings"/> и <see cref="Stops"/>.
/// </para>
/// </summary>
internal sealed partial class PedWorld
{
    /// <summary>Корзин гистограмм ожидания (1 с, последняя - «&gt;= 255 с»).</summary>
    public const int HistBuckets = 256;

    public readonly PedDesc Cfg;
    public readonly PedNetwork Net;
    public readonly PedStore P;
    public readonly ResidentStore R;
    public readonly CrossingState X;
    public readonly TimerWheel Wheel;
    public readonly RingBuffer<PathRequest> PathQueue;
    public readonly PedRouter Router;
    public readonly PedSnapshot Snapshot;

    // ---- остановки (P4): FIFO ожидающих по остановкам (через PedStore.Next) и пассажиры автобусов ----
    public readonly int[] StopQHead;       // [BusStops.Length]
    public readonly int[] StopQTail;
    public readonly int[] StopQCount;
    public readonly int[] PaxHead;         // [MaxVehicles] голова списка пассажиров автобуса (через PedStore.Next) или -1
    public readonly ushort[] StopWaiting;  // производное: min(StopQCount, 65535) для API

    // ---- гистограммы ожидания, 1 с на корзину (метрики §9; состояние, сейв XING) ----
    public readonly long[] CrossWaitHist = new long[HistBuckets];
    public readonly long[] StopWaitHist = new long[HistBuckets];

    private readonly int[] _due;           // scratch фазы 4p: ключи колеса текущего тика

    public PedWorld(World w)
    {
        Cfg = w.PedCfg;
        Net = w.Net.PedNet!;
        SimCapacities c = w.Caps;
        P = new PedStore(c.MaxPeds, c.MaxPedPathSteps, Net.Edges.Length);
        R = new ResidentStore(c.MaxResidents);
        X = new CrossingState(Net.XCapacity, w.Net.Nodes.Length, Net.MidCap);
        Wheel = new TimerWheel(c.MaxPeds + c.MaxResidents);
        PathQueue = new RingBuffer<PathRequest>(c.MaxPeds);
        Router = new PedRouter(Net, c.MaxPedPathSteps);
        Snapshot = new PedSnapshot(c.MaxPeds);
        int stops = w.Net.BusStops.Length;
        StopQHead = new int[stops];
        StopQTail = new int[stops];
        StopQCount = new int[stops];
        StopWaiting = new ushort[stops];
        PaxHead = new int[c.MaxVehicles];
        _due = new int[c.MaxPeds + c.MaxResidents];
        Array.Fill(StopQHead, -1);
        Array.Fill(StopQTail, -1);
        Array.Fill(PaxHead, -1);
    }

    /// <summary>Число индексов переходов в состоянии мира: слоты рукавов + по одному на остановку (переход на перегоне <c>m</c> = <c>MidXBase + m</c>).</summary>
    public static int XCount(World w) => w.Net.NodeArmCount + (w.PedCfg.MidblockAtStops ? w.Net.BusStopCount : 0);

    /// <summary>Создание мира: жители, колесо, цель пешеходов. PRNG <c>Peds</c> - только здесь и в тике.</summary>
    public void Init(World w)
    {
        R.Create(w);
        w.TargetPeds = Cfg.Count;
        Wheel.Clear();
        for (int r = 0; r < R.Count; r++)
        {
            Wheel.Schedule(P.Capacity + r, R.ReadyTick[r], -1);
        }
    }

    // ------------------------------------------------------------------ движение по рёбрам

    /// <summary>Расстояние конца текущего шага вдоль хода, мм: длина ребра либо точка назначения на последнем шаге ноги.</summary>
    public int EndMm(int p, int step)
    {
        int e = step & PedStore.StepMask;
        int len = Net.Edges[e].LenMm;
        if ((step & PedStore.StepCross) == 0 && P.PathCursor[p] == P.PathCount[p] - 1 && P.GoalEdge[p] == e)
        {
            int g = P.GoalSMm[p];
            return (step & PedStore.StepBwd) != 0 ? len - g : g;
        }

        return len;
    }

    /// <summary>
    /// Пешеход входит на ребро <paramref name="step"/> (кодирование <see cref="PedStore"/>) в этом тике со смещением <paramref name="s0Mm"/>: ставит
    /// <c>State</c>, <c>T0</c>, <c>EvTick = T0 + max(1, ceil((end - s0) / v))</c>, событие в колесо и пешехода - в список ребра. Общая точка входа для
    /// фазы 3b, событий 4p и (P3) выпуска с перехода.
    /// </summary>
    public void StartEdge(World w, int p, int step, PedState state, int s0Mm)
    {
        long tick = w.Tick;
        P.Edge[p] = step;
        P.State[p] = (byte)state;
        P.T0[p] = tick;
        P.S0Mm[p] = s0Mm;
        int rem = EndMm(p, step) - s0Mm;
        int v = P.VMmT[p];
        int dur = rem <= 0 ? 1 : (rem + v - 1) / v;
        long ev = tick + dur;
        P.EvTick[p] = ev;
        Wheel.Schedule(p, ev, tick);
        P.AddToEdge(p, step & PedStore.StepMask);
    }

    /// <summary>Конец ребра (событие колеса): следующий шаг пути или конец ноги.</summary>
    public void OnEdgeEnd(World w, int p)
    {
        int step = P.Edge[p];
        P.RemoveFromEdge(p, step & PedStore.StepMask);
        P.EvTick[p] = -1;
        if ((step & PedStore.StepCross) != 0)
        {
            Crossings.OnLeave(w, p, Net.XOfEdge(step & PedStore.StepMask));
        }

        int cursor = P.PathCursor[p] + 1;
        if (cursor >= P.PathCount[p])
        {
            LegEnd(w, p);
            return;
        }

        P.PathCursor[p] = cursor;
        int next = P.Path[(p * P.MaxSteps) + cursor];
        if ((next & PedStore.StepCross) != 0)
        {
            Crossings.OnArrive(w, p, next);
        }
        else
        {
            StartEdge(w, p, next, PedState.Walking, 0);
        }
    }

    /// <summary>Конец ноги: к остановке посадки - <see cref="Stops.OnArrive"/> (P4), иначе прибытие к назначению.</summary>
    private void LegEnd(World w, int p)
    {
        if (P.Leg[p] == (byte)PedLeg.ToStop)
        {
            Stops.OnArrive(w, p);
        }
        else
        {
            Arrive(w, p);
        }
    }

    /// <summary>Прибытие к назначению поездки: метрики, житель - на место (<c>Where</c>) со временем занятий, пешеход освобождается.</summary>
    public void Arrive(World w, int p)
    {
        long tick = w.Tick;
        int r = P.Resident[p];
        w.Counters.PedTripsCompleted++;
        w.Counters.PedTripTicksTotal += tick - P.TripTick[p];
        if ((uint)r < (uint)R.Count)
        {
            R.Where[r] = R.Where[r] == ResidentStore.ToWork ? ResidentStore.AtWork : ResidentStore.AtHome;
            ScheduleResident(w, r);
        }

        P.Release(p);
    }

    /// <summary>Житель освободился от поездки (прибыл или сброс): <c>ReadyTick = tick + U(activity_min, activity_max)</c>, пробуждение в колесо.</summary>
    public void ScheduleResident(World w, int r)
    {
        long tick = w.Tick;
        uint span = (uint)(Cfg.ActivityMaxTicks - Cfg.ActivityMinTicks + 1);
        long ready = tick + Cfg.ActivityMinTicks + w.RngPeds.NextBelow(span);
        R.ReadyTick[r] = ready;
        R.Ped[r] = -1;
        R.ReadyNext[r] = ResidentStore.NotQueued;
        Wheel.Schedule(P.Capacity + r, ready, tick);
    }

    /// <summary>Ставит пешехода <c>PendingPath</c> в очередь запросов путей (пул очереди равен пулу пешеходов - переполнения нет).</summary>
    public void EnqueuePath(int p)
    {
        var req = new PathRequest { Index = p, Generation = P.Pool.GenerationOf(p) };
        PathQueue.TryEnqueue(in req);
    }

    /// <summary>Пути нет (в пределах стоимости или арены): поездка отменяется, житель остаётся там, откуда вышел (§4.4; норма - 0 случаев).</summary>
    public void NoPath(World w, int p)
    {
        w.Counters.PedNoPath++;
        int r = P.Resident[p];
        if ((uint)r < (uint)R.Count)
        {
            R.Where[r] = R.Where[r] == ResidentStore.ToWork ? ResidentStore.AtHome : ResidentStore.AtWork;
            ScheduleResident(w, r);
        }

        P.Release(p);
    }

    // ------------------------------------------------------------------ сеть изменилась / загрузка

    /// <summary>
    /// <c>BuildRoad</c> (фаза команд): сеть пешеходов пересобирается целиком, индексы рёбер меняются, поэтому все пешеходы сбрасываются
    /// (житель возвращается туда, откуда вышел), дома и работы переносятся на новые рёбра по анкерам (сегмент, сторона, ось). Подробности - pedestrians.md §2.6.
    /// </summary>
    public void OnNetworkGrown(World w)
    {
        for (int r = 0; r < R.Count; r++)
        {
            Anchor(R.HomeEdge[r], R.HomeSMm[r], out R.HomeAnchor[r], out R.HomeAnchorS[r]);
            Anchor(R.WorkEdge[r], R.WorkSMm[r], out R.WorkAnchor[r], out R.WorkAnchorS[r]);
        }

        Net.Rebuild(w);
        for (int r = 0; r < R.Count; r++)
        {
            Resnap(R.HomeAnchor[r], R.HomeAnchorS[r], ref R.HomeEdge[r], ref R.HomeSMm[r]);
            Resnap(R.WorkAnchor[r], R.WorkAnchorS[r], ref R.WorkEdge[r], ref R.WorkSMm[r]);
        }

        ResetAgents(w);
        w.Counters.PedNetResets++;
    }

    private void Anchor(int edge, int sMm, out int segSide, out float s)
    {
        if ((uint)edge < (uint)Net.EdgeCount)
        {
            ref readonly PedEdgeData e = ref Net.Edges[edge];
            segSide = (e.Segment * 2) + e.Side;
            s = Net.AxisOf(edge, sMm);
            return;
        }

        segSide = -1;
        s = 0f;
    }

    private void Resnap(int segSide, float s, ref int edge, ref int sMm)
    {
        if (segSide >= 0 && Net.PointOnSide(segSide >> 1, segSide & 1, s, out int e, out int m))
        {
            edge = e;
            sMm = m;
        }
        else
        {
            edge = Net.SidewalkEdgeCount > 0 ? 0 : -1;
            sMm = 0;
        }
    }

    /// <summary>Убирает всех пешеходов, очереди и состояние переходов; жители возвращаются в точку выхода и получают новое время занятий.</summary>
    private void ResetAgents(World w)
    {
        for (int p = 0; p < P.HighWater; p++)
        {
            if (P.Pool.IsAlive(p))
            {
                P.Release(p);
            }
        }

        P.ClearEdgeLists();
        PathQueue.Clear();
        X.Reset();
        Array.Fill(StopQHead, -1);
        Array.Fill(StopQTail, -1);
        Array.Clear(StopQCount);
        Array.Clear(StopWaiting);
        Array.Fill(PaxHead, -1);
        Wheel.Clear();
        long tick = w.Tick;
        for (int r = 0; r < R.Count; r++)
        {
            if (R.Where[r] >= ResidentStore.ToWork)
            {
                R.Where[r] = R.Where[r] == ResidentStore.ToWork ? ResidentStore.AtHome : ResidentStore.AtWork;
                R.Ped[r] = -1;
                R.ReadyNext[r] = ResidentStore.NotQueued;
                R.ReadyTick[r] = tick + Cfg.ActivityMinTicks + (r & 255);
            }

            if (R.ReadyNext[r] == ResidentStore.NotQueued)
            {
                Wheel.Schedule(P.Capacity + r, R.ReadyTick[r], tick - 1);   // команды идут до фазы 4p: событие этого тика ещё впереди
            }
        }

        Stops.ResetDerived(w);
        w.Counters.PedActive = 0;
    }

    /// <summary>
    /// Производные после <c>Load</c> (сеть пешеходов уже пересобрана): списки пешеходов по рёбрам, колесо таймеров из <c>EvTick</c>/<c>ReadyTick</c>,
    /// затем хуки <see cref="Crossings.RestoreDerived"/> и <see cref="Stops.RestoreDerived"/>.
    /// </summary>
    public void RestoreDerived(World w)
    {
        P.ClearEdgeLists();
        Wheel.Clear();
        long now = w.Tick - 1;
        for (int p = 0; p < P.HighWater; p++)
        {
            if (!P.Pool.IsAlive(p))
            {
                continue;
            }

            byte st = P.State[p];
            if (st == (byte)PedState.Walking || st == (byte)PedState.Crossing)
            {
                P.AddToEdge(p, P.Edge[p] & PedStore.StepMask);
            }

            if (P.EvTick[p] >= 0)
            {
                Wheel.Schedule(p, P.EvTick[p], now);
            }
        }

        for (int r = 0; r < R.Count; r++)
        {
            if (R.Ped[r] < 0 && R.ReadyNext[r] == ResidentStore.NotQueued)
            {
                Wheel.Schedule(P.Capacity + r, R.ReadyTick[r], now);
            }
        }

        Crossings.RestoreDerived(w);
        Stops.RestoreDerived(w);
    }

    // ------------------------------------------------------------------ тик

    /// <summary>Фаза 4p: события колеса таймеров текущего тика (пешеходы, затем жители, по возрастанию ключа), автоматы переходов (P3).</summary>
    public void RunEvents(World w)
    {
        long tick = w.Tick;
        int n = Wheel.Collect(tick, _due);
        int maxPeds = P.Capacity;
        for (int k = 0; k < n; k++)
        {
            int key = _due[k];
            if (key < maxPeds)
            {
                OnEdgeEnd(w, key);
            }
            else
            {
                R.EnqueueReady(key - maxPeds);
            }
        }

        w.Work.PedEvents += n;
        Crossings.Step(w);
        w.Counters.PedActive = P.AliveCount;
        w.Counters.PedActiveTicksTotal += P.AliveCount;
#if DEBUG
        if (tick % 100 == 0)
        {
            CheckInvariants(w);
        }
#endif
    }

    /// <summary>Debug-сверка PD2: жители и пешеходы согласованы (раз в 100 тиков).</summary>
    public void CheckInvariants(World w)
    {
        int idle = 0;
        for (int r = 0; r < R.Count; r++)
        {
            if (R.Where[r] <= ResidentStore.AtWork)
            {
                idle++;
                System.Diagnostics.Debug.Assert(R.Ped[r] < 0, "idle resident has a pedestrian");
            }
            else
            {
                int p = R.Ped[r];
                System.Diagnostics.Debug.Assert(p >= 0 && P.Pool.IsAlive(p) && P.Resident[p] == r, "active resident without its pedestrian");
            }
        }

        System.Diagnostics.Debug.Assert(idle + P.AliveCount == R.Count, "PD2: Residents != Idle + Active");
    }
}
