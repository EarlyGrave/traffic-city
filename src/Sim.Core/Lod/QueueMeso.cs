using System;
using System.Diagnostics;
using System.Numerics;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Lod;

/// <summary>
/// Q-meso (ADR-0019, sim-lod.md §13.5, §13.9; прототип Q0, только <c>all_meso</c>): meso-машина на обычной полосе - член очереди полосы, который между
/// событиями не трогает ни одна фаза. Машина остаётся в списке своей полосы (FIFO); её <c>S</c>/<c>Speed</c> - значения на момент въезда (<c>S0</c>),
/// позиция «сейчас» синтезируется (<see cref="TailS"/>, <see cref="ViewLane"/>, <see cref="SynthFirst"/>).
/// <para>Состояния (биты <c>VehicleStore.Lod</c>, в хэше):</para>
/// <list type="bullet">
/// <item><b>очередь</b> (<see cref="LodBits.Queue"/>): ждёт события <c>QReady</c> в колесе таймеров; <see cref="LodBits.Parked"/> - событие наступило, но
/// впереди недопущенная машина (ждёт её допуска / ухода с полосы);</item>
/// <item><b>голова</b> (<see cref="LodBits.Queue"/> | <see cref="LodBits.Head"/>): материализована у стоп-линии полосы с воротами, кандидат ворот в такт цвета;
/// позиция и скорость обновляются синтезом раз в такт (<see cref="Run"/>), фазы 5-6 её не трогают;</item>
/// <item><b>кинематика</b> (бит <c>Queue</c> снят): connector'ы, допущенные на полосе, автобусы, полосы без ворот после события - как meso L2 (M2), фазы 5-6.</item>
/// </list>
/// События (начало фазы 4, после сигналов; корзина колеса текущего тика -> маска -> обход по возрастанию индекса): «готова» (голова/материализация или
/// парковка), «прибытие» (полоса назначения). Выход головы (допуск в фазе 4) и уход с полосы (фазы 7, 8) освобождают ведомого: <c>QReady = max(QReady, now + h)</c>.
/// Всё - на главном потоке; параллельные фазы 5, 6, 10 только читают (<see cref="TailS"/>, <see cref="SynthMember"/>, <see cref="ViewS"/>).
/// </summary>
internal sealed class QueueMeso
{
    public const int WheelSize = 4096;
    private const int WheelMask = WheelSize - 1;
    private const int StuckStride = 100;     // stuck по полосе: каждая полоса раз в 100 тиков (размазано по номеру полосы)

    /// <summary>Выбор полосы при въезде (sim-lod.md §13.5 п. 3): в желаемой полосе должно оставаться столько свободной длины, м.</summary>
    public const float EntryChoiceReserveM = 25f;

    private readonly World _w;
    private readonly VehicleStore _v;
    private readonly NetworkStore _net;
    private readonly LaneOccupancy _occ;
    private readonly byte[] _laneColor;
    private readonly float _dt;
    private readonly float _queueHeadwayScale;

    // ---- машины ----
    public readonly int[] QEnter;           // тик въезда на полосу (в хэше для машин очереди)
    public readonly int[] QReady;           // очередь: раньше этого тика к концу полосы не доедет; голова: тик последнего синтеза (в хэше)
    public readonly ulong[] QBits;          // бит i = машина в очереди (очередь или голова): фазы 5-6 её пропускают; производное (= Lod & Queue)
    public readonly float[] ViewS;          // синтез для снапшота/тестов (вне хэша), пишет ViewLane
    public readonly float[] ViewSpeed;
    private readonly int[] _wNext;
    private readonly int[] _wPrev;
    private readonly int[] _wTick;          // тик корзины колеса или -1 (не в колесе)
    private readonly ulong[] _ev;           // scratch: маска событий тика / событий stuck
    private readonly int[] _wheel = new int[WheelSize];

    // ---- полосы (производные, вне хэша) ----
    public readonly int[] LaneQCount;       // машин очереди (очередь + голова) на полосе
    public readonly int[] LaneSpanCm;       // Σ (L + s0) всех членов списка полосы, см
    public readonly int[] LaneLastExit;     // тик последнего продвижения очереди (материализация головы, уход с полосы)
    public readonly ulong[] KinMask;        // полосы с кинематическими членами (допущенные, connector'ы, автобусы)
    public readonly ulong[][] HeadMask;     // [цвет] полосы с головой очереди этого цвета
    private readonly int[] _kinCount;
    private readonly int[] _headCount;

    // ---- агрегаты для метрик (фаза 9 берёт их в начале фазы 6) ----
    public int Total;                       // машин в очереди (очередь + голова)
    public int Parked;
    public int StuckCount;                  // машин очереди с флагом Stuck
    public long PendingDistMm;              // путь машин очереди, начисленный событиями с прошлой фазы 6
    public long Events;                     // событий колеса за всё время (диагностика)
    public long Wakes;                      // материализаций

    public QueueMeso(World w, LodCells cells)
    {
        _w = w;
        _v = w.Vehicles;
        _net = w.Net;
        _occ = w.Occupancy;
        _laneColor = cells.LaneColor;
        _dt = PhysicsParams.DefaultDt;
        _queueHeadwayScale = w.LodCfg.QueueHeadwayScale;
        int n = _v.Capacity;
        QEnter = new int[n];
        QReady = new int[n];
        QBits = new ulong[(n + 63) >> 6];
        ViewS = new float[n];
        ViewSpeed = new float[n];
        _wNext = new int[n];
        _wPrev = new int[n];
        _wTick = new int[n];
        Array.Fill(_wTick, -1);
        _ev = new ulong[(n + 63) >> 6];
        Array.Fill(_wheel, -1);
        int lanes = _net.Lanes.Length;
        LaneQCount = new int[lanes];
        LaneSpanCm = new int[lanes];
        LaneLastExit = new int[lanes];
        _kinCount = new int[lanes];
        _headCount = new int[lanes];
        int words = (lanes + 63) >> 6;
        KinMask = new ulong[words];
        HeadMask = new ulong[4][];
        for (int c = 0; c < 4; c++)
        {
            HeadMask[c] = new ulong[words];
        }
    }

    private int Now => (int)_w.Tick;

    /// <summary>Текущий тик (для синтеза из фаз; тик внутри фаз не меняется).</summary>
    public int TickNow => (int)_w.Tick;

    public bool IsQ(int i) => (_v.Lod[i] & LodBits.Queue) != 0;

    public bool IsHead(int i) => (_v.Lod[i] & LodBits.Head) != 0;

    /// <summary>Машина очереди, ещё не материализованная (её <c>S</c>/<c>Speed</c> - на момент въезда).</summary>
    public bool IsQueued(int i) => (_v.Lod[i] & (LodBits.Queue | LodBits.Head)) == LodBits.Queue;

    // ------------------------------------------------------------------ хуки LaneOccupancy

    /// <summary>Член добавлен в список полосы (любая роль).</summary>
    public void OnInsert(int lane, int i)
    {
        LaneSpanCm[lane] += SpanCm(i);
        if (!IsQ(i))
        {
            KinInc(lane);
        }
    }

    /// <summary>Член снят со списка полосы; <paramref name="behind"/> - его бывший ведомый на полосе. Ведомый очереди освобождается к следующему тику.</summary>
    public void OnRemove(int lane, int i, int behind)
    {
        LaneSpanCm[lane] -= SpanCm(i);
        if (IsQ(i))
        {
            LeaveQ(i, lane);
        }
        else
        {
            KinDec(lane);
        }

        LaneLastExit[lane] = Now;
        if (behind != -1)
        {
            Notify(behind, Now + 1);
        }
    }

    /// <summary>Въезд на полосу (после <c>LodTransitions.SetLevel</c>): обычная полоса, не автобус - в очередь.</summary>
    public void OnEnterLane(int lane, int i)
    {
        if (_net.Lanes[lane].Kind != LaneKind.Normal || (_v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 || IsQ(i))
        {
            return;
        }

        EnterQ(i, lane);
    }

    // ------------------------------------------------------------------ фаза 4 (до ворот)

    /// <summary>Начало фазы 4 (после <c>SignalPhase</c>): синтез голов цвета такта, затем события колеса текущего тика по возрастанию индекса.</summary>
    public void Run()
    {
        int now = Now;
        int beat = now & 3;
        ulong[] heads = HeadMask[beat];
        for (int wi = 0; wi < heads.Length; wi++)
        {
            ulong m = heads[wi];
            while (m != 0UL)
            {
                int lane = (wi << 6) + BitOperations.TrailingZeroCount(m);
                m &= m - 1UL;
                int h = FindHead(lane);
                if (h != -1)
                {
                    Refresh(h, lane);
                }
            }
        }

        int b = now & WheelMask;
        int lo = int.MaxValue;
        int hi = -1;
        for (int x = _wheel[b]; x != -1;)
        {
            int next = _wNext[x];
            Debug.Assert(_wTick[x] == now, "QueueMeso: событие колеса не своего тика");
            _wTick[x] = -1;
            int word = x >> 6;
            _ev[word] |= 1UL << (x & 63);
            lo = Math.Min(lo, word);
            hi = Math.Max(hi, word);
            x = next;
        }

        _wheel[b] = -1;
        for (int k = lo; k <= hi; k++)
        {
            ulong m = _ev[k];
            _ev[k] = 0UL;
            while (m != 0UL)
            {
                int i = (k << 6) + BitOperations.TrailingZeroCount(m);
                m &= m - 1UL;
                Events++;
                Event(i);
            }
        }
    }

    private void Event(int i)
    {
        VehicleStore v = _v;
        if (!v.Pool.IsAlive(i) || !IsQueued(i))
        {
            return;
        }

        if (QReady[i] > Now)
        {
            WheelInsert(i, QReady[i]);
            return;
        }

        int lane = v.Lane[i];
        if (v.DestLane[i] == lane && (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0)
        {
            Arrive(i, lane);
            return;
        }

        TryWake(i, lane);
    }

    private void TryWake(int i, int lane)
    {
        VehicleStore v = _v;
        int a = AheadPrimary(i, lane);
        bool gated = _net.LaneGateNode[lane] >= 0;
        if (a != -1 && (IsQ(a) || (gated && !GateRules.IsGranted(v, a))))
        {
            Park(i);
            return;
        }

        SynthFirst(i, lane, a, out float s, out float speed);
        if (s < 0f)
        {
            // Полоса переполнена относительно синтеза (въезд по оценке хвоста): ждём, пока лидер освободит место.
            Unpark(i);
            WheelInsert(i, Now + 4);
            return;
        }

        Wakes++;
        float ds = s - v.S[i];
        if (ds > 0f)
        {
            PendingDistMm += (long)((ds * 1000f) + 0.5f);
        }

        v.S[i] = s;
        v.Speed[i] = speed;
        v.Accel[i] = 0f;
        LaneLastExit[lane] = Now;
        Unpark(i);
        if (gated)
        {
            v.Lod[i] |= LodBits.Head;
            HeadInc(lane);
            QReady[i] = Now;
            MarkLineFlags(i, lane);
        }
        else
        {
            ToKinematic(i, lane);
            Release(i, lane, useHeadway: true);
        }
    }

    /// <summary>Голова в такт своего цвета: синтез позиции от прошлого синтеза (свободно к стоп-линии, за допущенным лидером), монотонно.</summary>
    private void Refresh(int i, int lane)
    {
        VehicleStore v = _v;
        int dtTicks = Now - QReady[i];
        if (dtTicks <= 0)
        {
            return;
        }

        QReady[i] = Now;
        ref readonly VehicleTypeDesc t = ref _w.Types[v.Type[i]];
        float vff = Vff(i);
        float s = v.S[i];
        float minPhys = _w.Rules.MinPhysicalGap;

        // Ленивая кинематика M2 (sim-lod.md §2.2) раз в такт: сначала путь за прошедшие тики со скоростью прошлого решения (не дальше стоп-линии и
        // зазора до лидера), затем новое решение v' = min(v_ff, (g - s0)/T', v + a·τ) по зазору до лидера / стоп-линии - те же правила, что у M2.
        float line = _net.LaneLength[lane] - GateRules.StopLineOffset;
        int a = AheadPrimary(i, lane);
        float leadRear = a != -1 ? v.S[a] - v.Length[a] : float.MaxValue;
        float move = v.Speed[i] * dtTicks * _dt;
        float lim = line - s;
        if (leadRear - minPhys - s < lim)
        {
            lim = leadRear - minPhys - s;
        }

        if (move > lim)
        {
            move = lim;
        }

        float ns = move > 0f ? s + move : s;
        float g = line - ns;
        if (leadRear - ns < g)
        {
            g = leadRear - ns;
        }

        float vT = (g - t.MinGap) / (t.HeadwayTime * _w.LodCfg.MesoHeadwayScale);
        float sp = v.Speed[i] + (t.Accel * _w.LodCfg.MesoAccelScale * BehaviourJob.MesoPeriodTicks * _dt);
        if (vT < sp)
        {
            sp = vT;
        }

        if (sp > vff)
        {
            sp = vff;
        }

        if (sp < 0f)
        {
            sp = 0f;
        }

        if (ns > s)
        {
            PendingDistMm += (long)(((ns - s) * 1000f) + 0.5f);
        }

        v.S[i] = ns;
        v.Speed[i] = sp;
        MarkLineFlags(i, lane);
    }

    /// <summary>Метки, которые полный проход фазы 4 ставит по позиции/скорости (StoppedAtLine в хэше, StoppedBeforeSignal - диагностика): у головы - при синтезе.</summary>
    private void MarkLineFlags(int i, int lane)
    {
        VehicleStore v = _v;
        if (GateRules.IsGranted(v, i))
        {
            return;
        }

        ref readonly VehicleTypeDesc t = ref _w.Types[v.Type[i]];
        float speed = v.Speed[i];
        if (speed < 0.1f && _net.LaneLength[lane] - v.S[i] <= t.MinGap + GateRules.StopLineTolerance)
        {
            v.GateState[i] |= GateRules.StoppedAtLine;
        }

        int node = _net.LaneGateNode[lane];
        if (speed < 1f && node >= 0 && _net.Nodes[node].Control == NodeControl.Signal)
        {
            v.GateState[i] |= GateRules.StoppedBeforeSignal;
        }
    }

    private void Arrive(int i, int lane)
    {
        World w = _w;
        VehicleStore v = _v;
        float ds = v.DestS[i] - v.S[i];
        if (ds > 0f)
        {
            PendingDistMm += (long)((ds * 1000f) + 0.5f);
        }

        v.S[i] = v.DestS[i];
        VehicleHandle h = v.HandleOf(i);
        w.Counters.TripsCompleted++;
        w.Counters.TripTimeTicksTotal += w.Tick - v.SpawnTick[i];
        w.Emit(SimEventKind.TripCompleted, 0, h.Index, h.Generation);
        _occ.UnlinkAll(i);   // OnRemove: выход из очереди, ведомый освобождается
        if (v.Route[i] >= 0)
        {
            w.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
        }

        v.Despawn(h);
        Debug.Assert(lane >= 0, "Arrive: нет полосы");
    }

    /// <summary>Допуск ворот (фаза 4) машине <paramref name="i"/> на её обычной полосе: голова становится кинематической, ведомый очереди освобождается через <c>h</c>.</summary>
    public void OnGranted(int i)
    {
        int lane = _v.Lane[i];
        if (lane < 0)
        {
            return;
        }

        if (IsQ(i))
        {
            ToKinematic(i, lane);
        }

        Release(i, lane, useHeadway: true);
    }

    // ------------------------------------------------------------------ переходы состояний

    private void EnterQ(int i, int lane)
    {
        VehicleStore v = _v;
        v.Lod[i] |= LodBits.Queue;
        QBits[i >> 6] |= 1UL << (i & 63);
        KinDec(lane);
        LaneQCount[lane]++;
        Total++;
        int now = Now;
        QEnter[i] = now;
        v.StillTicks[i] = 0;
        v.Accel[i] = 0f;
        int ready = ComputeReady(i, lane, now);
        QReady[i] = ready;
        WheelInsert(i, ready);
    }

    private int ComputeReady(int i, int lane, int now)
    {
        VehicleStore v = _v;
        ref readonly VehicleTypeDesc t = ref _w.Types[v.Type[i]];
        float vff = VffOnLane(i, lane);
        float s0 = v.S[i];
        int a = AheadPrimary(i, lane);
        if (v.DestLane[i] == lane && (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0)
        {
            int r = now + CeilTicks((v.DestS[i] - s0) / vff);
            if (a != -1 && IsQueued(a) && QReady[a] > r)
            {
                r = QReady[a];
            }

            return r;
        }

        float d = _net.LaneLength[lane] - s0 - GateRules.DecisionDistance(vff, t.ComfortDecel);
        int ready = now + CeilTicks(d / vff);
        if (a != -1 && IsQ(a))
        {
            int after = QReady[a] + Headway(i, a, vff);
            if (after > ready)
            {
                ready = after;
            }
        }

        return ready;
    }

    private void ToKinematic(int i, int lane)
    {
        VehicleStore v = _v;
        if (IsHead(i))
        {
            HeadDec(lane);
        }

        if ((v.Flags[i] & (byte)VehicleFlags.Stuck) != 0)
        {
            StuckCount--;   // флаг снимет фаза 9 (StillTicks = 0 расходится с флагом - событие Unstuck)
        }

        Unpark(i);
        WheelRemove(i);
        v.Lod[i] = (byte)((v.Lod[i] & ~(LodBits.Queue | LodBits.Head | LodBits.Parked)) | LodBits.Fresh);
        QBits[i >> 6] &= ~(1UL << (i & 63));
        LaneQCount[lane]--;
        Total--;
        KinInc(lane);
        v.StillTicks[i] = 0;
        v.MesoAllow[i] = 0f;
        v.MesoNext[i] = -1f;
        v.Accel[i] = 0f;
    }

    private void LeaveQ(int i, int lane)
    {
        VehicleStore v = _v;
        if (IsHead(i))
        {
            HeadDec(lane);
        }

        if ((v.Flags[i] & (byte)VehicleFlags.Stuck) != 0)
        {
            StuckCount--;
        }

        Unpark(i);
        WheelRemove(i);
        v.Lod[i] = (byte)(v.Lod[i] & ~(LodBits.Queue | LodBits.Head | LodBits.Parked));
        QBits[i >> 6] &= ~(1UL << (i & 63));
        LaneQCount[lane]--;
        Total--;
    }

    private void Park(int i)
    {
        if ((_v.Lod[i] & LodBits.Parked) == 0)
        {
            _v.Lod[i] |= LodBits.Parked;
            Parked++;
        }
    }

    private void Unpark(int i)
    {
        if ((_v.Lod[i] & LodBits.Parked) != 0)
        {
            _v.Lod[i] = (byte)(_v.Lod[i] & ~LodBits.Parked);
            Parked--;
        }
    }

    /// <summary>Ведомый <paramref name="b"/> (машина очереди): готов не раньше <paramref name="t"/>; припаркованный возвращается в колесо.</summary>
    private void Notify(int b, int t)
    {
        if (!IsQueued(b))
        {
            return;
        }

        if (QReady[b] < t)
        {
            QReady[b] = t;
        }

        if ((_v.Lod[b] & LodBits.Parked) != 0)
        {
            Unpark(b);
            WheelInsert(b, QReady[b]);
        }
        else if (_wTick[b] == -1)
        {
            WheelInsert(b, QReady[b]);
        }
    }

    private void Release(int i, int lane, bool useHeadway)
    {
        int b = BehindPrimary(i, lane);
        if (b != -1 && IsQueued(b))
        {
            Notify(b, Now + (useHeadway ? Headway(b, i, Vff(b)) : 1));
        }
    }

    // ------------------------------------------------------------------ колесо таймеров

    private void WheelInsert(int i, int t)
    {
        int now = Now;
        if (t <= now)
        {
            t = now + 1;
        }
        else if (t > now + WheelMask)
        {
            t = now + WheelMask;   // дальше горизонта: перепроверка при срабатывании (QReady > tick -> снова в колесо)
        }

        WheelRemove(i);
        int b = t & WheelMask;
        int head = _wheel[b];
        _wNext[i] = head;
        _wPrev[i] = -1;
        if (head != -1)
        {
            _wPrev[head] = i;
        }

        _wheel[b] = i;
        _wTick[i] = t;
    }

    private void WheelRemove(int i)
    {
        int t = _wTick[i];
        if (t == -1)
        {
            return;
        }

        int p = _wPrev[i];
        int n = _wNext[i];
        if (p != -1)
        {
            _wNext[p] = n;
        }
        else
        {
            _wheel[t & WheelMask] = n;
        }

        if (n != -1)
        {
            _wPrev[n] = p;
        }

        _wTick[i] = -1;
    }

    // ------------------------------------------------------------------ синтез (чистые функции, читают только; можно с воркеров)

    /// <summary>
    /// Позиция и скорость первой недопущенной машины очереди <paramref name="i"/> (впереди только кинематические или никого) «сейчас»:
    /// <c>min(S0 + v_ff·(t - QEnter)·dt, стоп-линия - s0 (полоса с воротами) / конец полосы, S(лидер) - L(лидер) - s0)</c>.
    /// </summary>
    public void SynthFirst(int i, int lane, int a, out float s, out float speed)
    {
        VehicleStore v = _v;
        ref readonly VehicleTypeDesc t = ref _w.Types[v.Type[i]];
        float vff = Vff(i);
        float free = v.S[i] + (vff * (Now - QEnter[i]) * _dt);
        float len = _net.LaneLength[lane];
        float cap = _net.LaneGateNode[lane] >= 0 ? len - GateRules.StopLineOffset - t.MinGap : len;
        float capSpeed = 0f;
        if (a != -1)
        {
            float lc = v.S[a] - v.Length[a] - FollowGap(in t, Math.Min(vff, v.Speed[a]));
            if (lc < cap)
            {
                cap = lc;
                capSpeed = Math.Min(vff, v.Speed[a]);
            }
        }

        if (free <= cap)
        {
            s = free;
            speed = vff;
        }
        else
        {
            s = cap;
            speed = capSpeed;
        }
    }

    /// <summary>То же для первой недопущенной на полосе (ворота, «подъезжающие» §5.4 п. 4b): лидер ищется сам.</summary>
    public void SynthFirst(int i, int lane, out float s, out float speed) => SynthFirst(i, lane, AheadPrimary(i, lane), out s, out speed);

    /// <summary>
    /// Оценка позиции хвоста полосы за O(1) (sim-lod.md §13.5): хвост очереди не дальше свободного хода и не дальше плотной очереди от стоп-линии
    /// (<c>Length - StopLineOffset - Σ(L + s0) + L_хвоста</c>), не ближе своего въезда. Кинематический хвост - его <c>S</c>.
    /// </summary>
    public float TailS(int lane)
    {
        VehicleStore v = _v;
        int x = _occ.LaneTail[lane];
        if (x == -1 || v.Lane[x] != lane || !IsQueued(x))
        {
            return x == -1 ? _net.LaneLength[lane] : v.S[x];
        }

        float free = v.S[x] + (Vff(x) * (Now - QEnter[x]) * _dt);
        float jam = _net.LaneLength[lane] - GateRules.StopLineOffset - (LaneSpanCm[lane] * 0.01f) + v.Length[x];
        float r = free < jam ? free : jam;
        return r > v.S[x] ? r : v.S[x];
    }

    /// <summary>Скорость хвоста полосы (оценка <see cref="TailS"/>): свободный ход - <c>v_ff</c>, упёрся в очередь - 0.</summary>
    public float TailSpeed(int lane)
    {
        VehicleStore v = _v;
        int x = _occ.LaneTail[lane];
        if (x == -1)
        {
            return 0f;
        }

        if (v.Lane[x] != lane || !IsQueued(x))
        {
            return v.Speed[x];
        }

        float free = v.S[x] + (Vff(x) * (Now - QEnter[x]) * _dt);
        float jam = _net.LaneLength[lane] - GateRules.StopLineOffset - (LaneSpanCm[lane] * 0.01f) + v.Length[x];
        return free < jam ? Vff(x) : 0f;
    }

    /// <summary>
    /// Синтез позиции члена очереди <paramref name="x"/> на полосе <paramref name="lane"/> (обход от головы, O(места в очереди)): то же правило, что у
    /// <see cref="ViewLane"/>. Для кинематической машины за машиной очереди (автобус) - лидер в поиске лидера.
    /// </summary>
    public void SynthMember(int x, int lane, float time, out float s, out float speed)
    {
        VehicleStore v = _v;
        float prevS = 0f, prevL = 0f, prevV = 0f;
        bool havePrev = false;
        s = v.S[x];
        speed = v.Speed[x];
        for (int y = _occ.LaneHead[lane]; y != -1;)
        {
            if (v.Lane[y] != lane)
            {
                y = v.GhostBehind[y];
                continue;
            }

            Member(y, lane, time, havePrev, prevS, prevL, prevV, out float ys, out float yv);
            if (y == x)
            {
                s = ys;
                speed = yv;
                return;
            }

            prevS = ys;
            prevL = v.Length[y];
            prevV = yv;
            havePrev = true;
            y = v.Behind[y];
        }
    }

    /// <summary>
    /// Синтез полосы для вида (снапшот, тесты): от головы, кинематические и головы - их <c>S</c>, очередь - <c>min(S0 + v_ff·(t - QEnter)·dt, ограничение)</c>,
    /// где ограничение - стоп-линия у первой либо <c>S(впереди) - L - s0</c>. Ограничение лидером применяется последним, поэтому I6 (<c>S[Ahead] - L &gt;= S</c>)
    /// выполняется по построению. Пишет <see cref="ViewS"/>/<see cref="ViewSpeed"/> машин полосы; <paramref name="time"/> - тик, на начало которого синтез.
    /// </summary>
    public void ViewLane(int lane, float time)
    {
        VehicleStore v = _v;
        float prevS = 0f, prevL = 0f, prevV = 0f;
        bool havePrev = false;
        for (int y = _occ.LaneHead[lane]; y != -1;)
        {
            if (v.Lane[y] != lane)
            {
                y = v.GhostBehind[y];
                continue;
            }

            Member(y, lane, time, havePrev, prevS, prevL, prevV, out float ys, out float yv);
            ViewS[y] = ys;
            ViewSpeed[y] = yv;
            prevS = ys;
            prevL = v.Length[y];
            prevV = yv;
            havePrev = true;
            y = v.Behind[y];
        }
    }

    /// <summary>Синтез всех занятых полос (<see cref="ViewS"/>) на начало тика <paramref name="time"/>.</summary>
    public void SynthesizeAll(float time)
    {
        ulong[] occ = _occ.OccMask;
        int lanes = _net.LaneCount;
        for (int wi = 0; wi < occ.Length; wi++)
        {
            ulong m = occ[wi];
            while (m != 0UL)
            {
                int lane = (wi << 6) + BitOperations.TrailingZeroCount(m);
                m &= m - 1UL;
                if (lane < lanes)
                {
                    ViewLane(lane, time);
                }
            }
        }
    }

    private void Member(int y, int lane, float time, bool havePrev, float prevS, float prevL, float prevV, out float s, out float speed)
    {
        VehicleStore v = _v;
        if (!IsQueued(y))
        {
            s = v.S[y];
            speed = v.Speed[y];
            return;
        }

        ref readonly VehicleTypeDesc t = ref _w.Types[v.Type[y]];
        float vff = Vff(y);
        float free = v.S[y] + (vff * (time - QEnter[y]) * _dt);
        float cap;
        float capSpeed;
        if (havePrev)
        {
            cap = prevS - prevL - t.MinGap;
            capSpeed = Math.Min(vff, prevV);
        }
        else
        {
            float len = _net.LaneLength[lane];
            cap = _net.LaneGateNode[lane] >= 0 ? len - GateRules.StopLineOffset - t.MinGap : len;
            capSpeed = 0f;
        }

        if (free <= cap)
        {
            s = free;
            speed = vff;
        }
        else
        {
            s = cap;
            speed = capSpeed;
        }
    }

    /// <summary>Скорость машины очереди для агрегатов (нагрузка сегментов): голова - её синтез, очередь - <c>v_ff</c> до <c>QReady</c>, затем 0.</summary>
    public float SpeedFor(int i)
    {
        if (IsHead(i))
        {
            return _v.Speed[i];
        }

        return QReady[i] > Now ? Vff(i) : 0f;
    }

    // ------------------------------------------------------------------ метрики

    /// <summary>Начало фазы 6 (после <c>TickMasks.Begin</c>): вклад машин очереди в накопители метрик фазы 9 (машины очереди фаза 6 не обходит).</summary>
    public void AddTallies(TickMasks m)
    {
        m.Active += Total;
        m.MesoNow += Total;
        m.Moving += Total - Parked;
        m.Stuck += StuckCount;
        m.DistMm += PendingDistMm;
        m.MesoDistMm += PendingDistMm;
        PendingDistMm = 0;
    }

    /// <summary>
    /// Фаза 9: stuck по полосе (sim-lod.md §13.5, уточнение §13.9): полоса раз в 100 тиков; машина очереди «застряла», если она ждёт (голова; очередь с
    /// наступившим <c>QReady</c>) и очередь не продвигалась (<c>LaneLastExit</c>) не меньше порога. События - по возрастанию индекса.
    /// </summary>
    public void StuckPass()
    {
        World w = _w;
        VehicleStore v = _v;
        int now = Now;
        int thr = w.Rules.StuckThresholdTicks;
        int lanes = _net.LaneCount;
        int lo = int.MaxValue;
        int hi = -1;
        for (int lane = now % StuckStride; lane < lanes; lane += StuckStride)
        {
            if (LaneQCount[lane] == 0)
            {
                continue;
            }

            int last = LaneLastExit[lane];
            for (int x = _occ.LaneHead[lane]; x != -1;)
            {
                if (v.Lane[x] != lane)
                {
                    x = v.GhostBehind[x];
                    continue;
                }

                if (IsQ(x))
                {
                    bool want;
                    if (IsHead(x))
                    {
                        want = now - last >= thr;
                    }
                    else
                    {
                        int r = QReady[x];
                        want = r <= now && now - Math.Max(r, last) >= thr;
                    }

                    if (want != ((v.Flags[x] & (byte)VehicleFlags.Stuck) != 0))
                    {
                        int word = x >> 6;
                        _ev[word] |= 1UL << (x & 63);
                        lo = Math.Min(lo, word);
                        hi = Math.Max(hi, word);
                    }
                }

                x = v.Behind[x];
            }
        }

        for (int k = lo; k <= hi; k++)
        {
            ulong m = _ev[k];
            _ev[k] = 0UL;
            while (m != 0UL)
            {
                int i = (k << 6) + BitOperations.TrailingZeroCount(m);
                m &= m - 1UL;
                VehicleHandle h = v.HandleOf(i);
                if ((v.Flags[i] & (byte)VehicleFlags.Stuck) == 0)
                {
                    v.Flags[i] |= (byte)VehicleFlags.Stuck;
                    StuckCount++;
                    w.Counters.StuckEventsTotal++;
                    w.Emit(SimEventKind.VehicleStuck, 0, h.Index, h.Generation);
                }
                else
                {
                    v.Flags[i] &= unchecked((byte)~(byte)VehicleFlags.Stuck);
                    StuckCount--;
                    w.Emit(SimEventKind.VehicleUnstuck, 0, h.Index, h.Generation);
                }
            }
        }
    }

    // ------------------------------------------------------------------ вспомогательное

    private int FindHead(int lane)
    {
        VehicleStore v = _v;
        for (int x = _occ.LaneHead[lane]; x != -1;)
        {
            if (v.Lane[x] != lane)
            {
                x = v.GhostBehind[x];
                continue;
            }

            if (IsHead(x))
            {
                return x;
            }

            x = v.Behind[x];
        }

        return -1;
    }

    private int AheadPrimary(int i, int lane)
    {
        VehicleStore v = _v;
        int a = v.Ahead[i];
        while (a != -1 && v.Lane[a] != lane)
        {
            a = v.GhostAhead[a];
        }

        return a;
    }

    private int BehindPrimary(int i, int lane)
    {
        VehicleStore v = _v;
        int b = v.Lane[i] == lane ? v.Behind[i] : v.GhostBehind[i];
        while (b != -1 && v.Lane[b] != lane)
        {
            b = v.GhostBehind[b];
        }

        return b;
    }

    /// <summary>
    /// Интервал материализации ведомого после выхода лидера из очереди, тиков: <c>ceil(queue_headway_scale · (T + (L_впереди + s0)/v_ff) / dt)</c>
    /// (sim-lod.md §13.9: выезд с линии дальше кинематический, поэтому это задержка старта ведомого, а не весь насыщенный интервал).
    /// </summary>
    private int Headway(int i, int ahead, float vff)
    {
        ref readonly VehicleTypeDesc t = ref _w.Types[_v.Type[i]];
        return CeilTicks(_queueHeadwayScale * (t.HeadwayTime + ((_v.Length[ahead] + t.MinGap) / vff)));
    }

    /// <summary>Зазор бампер-в-бампер, на котором кинематика meso (M2) едет за лидером со скоростью <paramref name="v"/>: <c>s0 + v·T·meso_headway_scale</c>.</summary>
    private float FollowGap(in VehicleTypeDesc t, float v) => t.MinGap + (v * t.HeadwayTime * _w.LodCfg.MesoHeadwayScale);

    private int CeilTicks(float seconds)
    {
        if (!(seconds > 0f))
        {
            return 0;
        }

        float ticks = MathF.Ceiling(seconds / _dt);
        return ticks < 1e8f ? (int)ticks : 100_000_000;
    }

    private float Vff(int i)
    {
        float vff = _v.DesiredSpeed[i];
        return vff > 0.5f ? vff : 0.5f;
    }

    private float VffOnLane(int i, int lane)
    {
        float vff = Idm.DesiredSpeed(in _w.Types[_v.Type[i]], _net.Lanes[lane].SpeedLimit);
        _v.DesiredSpeed[i] = vff;   // фаза 7 перепишет тем же значением (Idm.DesiredSpeed по полосе въезда)
        return vff > 0.5f ? vff : 0.5f;
    }

    private int SpanCm(int i) => (int)(((_v.Length[i] + _w.Types[_v.Type[i]].MinGap) * 100f) + 0.5f);

    private void KinInc(int lane)
    {
        if (_kinCount[lane]++ == 0)
        {
            KinMask[lane >> 6] |= 1UL << (lane & 63);
        }
    }

    private void KinDec(int lane)
    {
        if (--_kinCount[lane] == 0)
        {
            KinMask[lane >> 6] &= ~(1UL << (lane & 63));
        }

        Debug.Assert(_kinCount[lane] >= 0, "QueueMeso: счётчик кинематических членов полосы < 0");
    }

    private void HeadInc(int lane)
    {
        if (_headCount[lane]++ == 0)
        {
            HeadMask[_laneColor[lane]][lane >> 6] |= 1UL << (lane & 63);
        }
    }

    private void HeadDec(int lane)
    {
        if (--_headCount[lane] == 0)
        {
            HeadMask[_laneColor[lane]][lane >> 6] &= ~(1UL << (lane & 63));
        }

        Debug.Assert(_headCount[lane] >= 0, "QueueMeso: счётчик голов полосы < 0");
    }
}
