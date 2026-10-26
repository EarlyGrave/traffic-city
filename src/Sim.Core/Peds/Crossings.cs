using System;
using System.Numerics;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Peds;

/// <summary>
/// Переходы (pedestrians.md §5): очередь у бордюра, заявка, обязавшиеся машины, выпуск, анти-голодание машин. Точки расширения фазы 4p.
/// Состояние - <see cref="CrossingState"/> (сейв <c>XING</c>); автомат <c>Free -> Claimed -> Busy -> Free</c> идёт по активному множеству <c>XActive</c> по возрастанию индекса.
/// <para>
/// Контракт очереди: пешеход в очереди - <c>State = AtCrossing</c>, <c>Edge</c> = шаг-переход (с битами), <c>T0</c> = начало ожидания, <c>EvTick = -1</c>
/// (в колесе его нет), в списки рёбер не входит; FIFO идёт через <c>PedStore.Next</c> с <c>QHead/QTail/QCount</c> перехода. Выпуск - <c>PedWorld.StartEdge(..., Crossing, 0)</c>.
/// </para>
/// <para>
/// Отличие от спеки §5.6 (ADR-0020, «Дополнение P3»): машина на connector'е узла, уже въехавшая за стоп-линию, <b>обязана</b> безусловно (препятствия перед зоной
/// перехода на connector'е нет). Препятствие для остановившейся внутри узла машины замыкало бы цикл «пешеход ждёт допущенную машину, она ждёт по билету машину,
/// стоящую перед переходом, та ждёт пешеходов»; без него ждут пешеходов только недопущенные машины у стоп-линии (их держит <c>DenyPed</c>), а допущенные и внутри узла едут.
/// </para>
/// </summary>
internal static class Crossings
{
    /// <summary>Пешеход дошёл до конца тротуара и следующий шаг пути - переход <paramref name="step"/> (с битами <c>StepCross</c>, <c>StepBwd</c>): встаёт в конец очереди.</summary>
    public static void OnArrive(World w, int p, int step)
    {
        PedWorld pw = w.Peds!;
        PedStore ps = pw.P;
        CrossingState xs = pw.X;
        int x = pw.Net.XOfEdge(step & PedStore.StepMask);
        ps.State[p] = (byte)PedState.AtCrossing;
        ps.Edge[p] = step;
        ps.T0[p] = w.Tick;
        ps.S0Mm[p] = 0;
        ps.EvTick[p] = -1;
        ps.Next[p] = -1;
        if (xs.QTail[x] >= 0)
        {
            ps.Next[xs.QTail[x]] = p;
        }
        else
        {
            xs.QHead[x] = p;
        }

        xs.QTail[x] = p;
        xs.QCount[x]++;
        xs.SetActive(x);
    }

    /// <summary>Пешеход сошёл с полосы перехода <paramref name="x"/> (событие конца ребра-перехода, до следующего шага).</summary>
    public static void OnLeave(World w, int p, int x)
    {
        CrossingState xs = w.Peds!.X;
        if (xs.OnCross[x] > 0)
        {
            xs.OnCross[x]--;
        }
    }

    /// <summary>Шаг автоматов переходов (<c>XActive</c> по возрастанию индекса), конец фазы 4p после событий колеса.</summary>
    public static void Step(World w)
    {
        PedWorld pw = w.Peds!;
        CrossingState xs = pw.X;
        xs.XClosedNowCount = 0;
        long tick = w.Tick;
        ulong[] act = xs.XActive;
        for (int wi = 0; wi < act.Length; wi++)
        {
            ulong bits = act[wi];
            while (bits != 0UL)
            {
                int b = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1UL;
                StepOne(w, pw, xs, (wi << 6) + b, tick);
            }
        }

        w.Counters.CrossingObservedTicks += xs.ObservedCount;
    }

    private static void StepOne(World w, PedWorld pw, CrossingState xs, int x, long tick)
    {
        PedDesc cfg = pw.Cfg;
        PedStore ps = pw.P;
        bool carsWaiting = xs.CarsWaiting[x] != 0;
        xs.CarsWaiting[x] = 0;
        byte st = xs.XState[x];
        int signal = SignalSlotOf(w, pw, x);

        // Застрявшие: каждый пешеход отмечается ровно один раз, в тик, когда его ожидание достигает порога (очередь FIFO: голова ждёт дольше всех).
        if (xs.QCount[x] > 0 && cfg.StuckCrossTicks > 0 && tick - ps.T0[xs.QHead[x]] >= cfg.StuckCrossTicks)
        {
            for (int p = xs.QHead[x]; p != -1; p = ps.Next[p])
            {
                if (tick - ps.T0[p] == cfg.StuckCrossTicks)
                {
                    w.Counters.PedStuckEvents++;
                }
            }
        }

        if (st == XStateCode.Free && carsWaiting && xs.CarWindowUntil[x] > tick && xs.CarWindowUntil[x] != long.MaxValue)
        {
            // Машина ждёт пару переходов (вход и выход): окно открытого перехода держится, пока она не проедет, иначе окна двух переходов редко совпадают.
            xs.CarWindowUntil[x] = tick + pw.Cfg.CarWindowTicks;
        }

        if (st == XStateCode.Free && xs.QCount[x] > 0 && Permitted(w, pw, xs, x, signal, tick))
        {
            SetState(w, pw, xs, x, XStateCode.Claimed, tick);
            st = XStateCode.Claimed;
        }

        if (st == XStateCode.Claimed)
        {
            if (xs.QCount[x] == 0 || (signal >= 0 && !PedSignals.IsWalk(xs, x)))
            {
                // Очередь опустела (сброс/редкий случай) либо на светофоре Walk кончился, пока ждали обязавшихся: переход открывается, пешеходы ждут следующего Walk.
                Open(w, pw, xs, x, tick);
                st = XStateCode.Free;
            }
            else if (!HasObligated(w, pw, x))
            {
                Release(w, pw, xs, x, tick);
                st = XStateCode.Busy;
            }
        }
        else if (st == XStateCode.Busy)
        {
            // Анти-голодание машин (только без сигнала): закрыт дольше max_close_s и машины ждут - новых пешеходов не выпускаем, вышедшие доходят,
            // затем окно car_window_s отдаётся машинам (отсчёт окна начинается, когда переход освободился).
            if (signal < 0 && carsWaiting && tick - xs.ClosedSince[x] >= cfg.MaxCloseTicks && xs.CarWindowUntil[x] <= tick)
            {
                xs.CarWindowUntil[x] = long.MaxValue;
            }

            if (xs.QCount[x] > 0 && Permitted(w, pw, xs, x, signal, tick))
            {
                Release(w, pw, xs, x, tick);
            }

            if (xs.OnCross[x] == 0)
            {
                Open(w, pw, xs, x, tick);
                st = XStateCode.Free;
            }
        }

        if (xs.XState[x] != XStateCode.Free && xs.Releases[x] > 0)
        {
            w.Counters.CrossingClosedTicks++;
        }

        if (st == XStateCode.Free && xs.QCount[x] == 0)
        {
            xs.ClearActive(x);
        }
    }

    /// <summary>Индекс светофора в цветном режиме, управляющего переходом узла <paramref name="x"/>, или -1 (нерегулируемый: не узел <c>Signal</c>, мигающий жёлтый/выключен, перегон).</summary>
    internal static int SignalSlotOf(World w, PedWorld pw, int x)
    {
        if (x >= pw.Net.MidXBase)
        {
            return -1;
        }

        if (!PedSignals.Enabled)
        {
            return -1;
        }

        int node = pw.Net.Edges[pw.Net.XEdge[x]].Node;
        ref readonly NodeData nd = ref w.Net.Nodes[node];
        int slot = nd.Signal;
        if (nd.Control != NodeControl.Signal || slot < 0 || slot >= w.Signals.Count)
        {
            return -1;
        }

        SignalMode m = w.Signals.Mode[slot];
        return m == SignalMode.Fixed || m == SignalMode.Adaptive ? slot : -1;
    }

    /// <summary>Разрешение на выход (§5.5): без светофора - окно машин не открыто; светофор в цветном режиме - горит <c>Walk</c>.</summary>
    private static bool Permitted(World w, PedWorld pw, CrossingState xs, int x, int signal, long tick)
    {
        if (signal >= 0)
        {
            return PedSignals.IsWalk(xs, x);
        }

        return tick >= xs.CarWindowUntil[x];
    }

    // ------------------------------------------------------------------ смена состояния, маски

    private static void SetState(World w, PedWorld pw, CrossingState xs, int x, byte st, long tick)
    {
        byte old = xs.XState[x];
        xs.XState[x] = st;
        if (old == XStateCode.Free && st != XStateCode.Free)
        {
            xs.ClosedSince[x] = tick;
            xs.XClosedNow[xs.XClosedNowCount++] = x;
        }

        if ((old == XStateCode.Free) != (st == XStateCode.Free))
        {
            RecomputeMasks(w, pw, xs, x);
        }

        PedSignals.PublishState(xs, x);
    }

    /// <summary>Переход свободен (<c>Busy -> Free</c>): отложенное окно машин начинает отсчёт, маски узла пересчитываются.</summary>
    private static void Open(World w, PedWorld pw, CrossingState xs, int x, long tick)
    {
        if (xs.CarWindowUntil[x] == long.MaxValue)
        {
            xs.CarWindowUntil[x] = tick + pw.Cfg.CarWindowTicks;
        }

        SetState(w, pw, xs, x, XStateCode.Free, tick);
    }

    /// <summary>Маски <c>NodeXIn/NodeXOut</c> узла (OR масок закрытых переходов его рукавов) либо флаг <c>MidClosed</c> перехода на перегоне.</summary>
    internal static void RecomputeMasks(World w, PedWorld pw, CrossingState xs, int x)
    {
        PedNetwork pn = pw.Net;
        if (x >= pn.MidXBase)
        {
            xs.MidClosed[x - pn.MidXBase] = xs.XState[x] != XStateCode.Free ? (byte)1 : (byte)0;
            return;
        }

        int node = pn.Edges[pn.XEdge[x]].Node;
        ref readonly NodeData nd = ref w.Net.Nodes[node];
        ulong mi = 0UL;
        ulong mo = 0UL;
        for (int a = 0; a < nd.ArmCount; a++)
        {
            int slot = nd.FirstArm + a;
            if (pn.XEdge[slot] >= 0 && xs.XState[slot] != XStateCode.Free)
            {
                mi |= pn.XIn[slot];
                mo |= pn.XOut[slot];
            }
        }

        xs.NodeXIn[node] = mi;
        xs.NodeXOut[node] = mo;
    }

    // ------------------------------------------------------------------ выпуск

    /// <summary>Выпуск всей очереди: пешеходы выходят на полосу перехода, ожидание уходит в метрики и гистограмму, переход - <c>Busy</c>.</summary>
    private static void Release(World w, PedWorld pw, CrossingState xs, int x, long tick)
    {
        PedStore ps = pw.P;
        long maxEv = xs.BusyUntil[x];
        int count = 0;
        int p = xs.QHead[x];
        while (p != -1)
        {
            int next = ps.Next[p];
            long wait = tick - ps.T0[p];
            ps.Next[p] = -1;
            pw.StartEdge(w, p, ps.Edge[p], PedState.Crossing, 0);
            if (ps.EvTick[p] > maxEv)
            {
                maxEv = ps.EvTick[p];
            }

            w.Counters.PedCrossWaitTicksTotal += wait;
            w.Counters.PedCrossReleases++;
            long bucket = wait / 20;
            pw.CrossWaitHist[bucket < PedWorld.HistBuckets ? (int)bucket : PedWorld.HistBuckets - 1]++;
            xs.WaitSum[x] += wait;
            if (xs.Releases[x] == 0)
            {
                xs.ObservedCount++;
                xs.MeasuredWait[x] = (int)Math.Min(wait, int.MaxValue);
            }
            else
            {
                xs.MeasuredWait[x] += (int)((wait - xs.MeasuredWait[x]) / 8);
            }

            xs.Releases[x]++;
            count++;
            p = next;
        }

        xs.QHead[x] = -1;
        xs.QTail[x] = -1;
        xs.QCount[x] = 0;
        xs.OnCross[x] += count;
        xs.BusyUntil[x] = maxEv;
        SetState(w, pw, xs, x, XStateCode.Busy, tick);
    }

    // ------------------------------------------------------------------ обязавшиеся машины (§5.3)

    /// <summary>true: есть машина, обязавшаяся перед переходом <paramref name="x"/> (читает состояние конца прошлого тика).</summary>
    private static bool HasObligated(World w, PedWorld pw, int x) =>
        x >= pw.Net.MidXBase ? MidObligated(w, pw, x - pw.Net.MidXBase) : NodeObligated(w, pw, x);

    private static bool NodeObligated(World w, PedWorld pw, int x)
    {
        PedNetwork pn = pw.Net;
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        ConnectorStore cs = w.Connectors;
        ref readonly NodeData nd = ref net.Nodes[pn.Edges[pn.XEdge[x]].Node];
        ulong inMask = pn.XIn[x];
        ulong outMask = pn.XOut[x];
        ulong all = inMask | outMask;
        float zone = pn.CrosswalkWidthM;
        while (all != 0UL)
        {
            int b = BitOperations.TrailingZeroCount(all);
            all &= all - 1UL;
            int lane = nd.FirstConnector + b;
            int g = net.Lanes[lane].ConflictGroup;
            if (g < 0)
            {
                continue;
            }

            bool isIn = ((inMask >> b) & 1UL) != 0UL;
            bool isOut = ((outMask >> b) & 1UL) != 0UL;
            float lc = cs.Length[g];

            // (а) машины на connector'е: въехавшая обязана, пока не освободила полосу перехода (для выходной зоны - всегда).
            for (int i = occ.LaneHead[lane]; i != -1; i = GateRules.BehindOn(v, i, lane))
            {
                if (v.Lane[i] != lane)
                {
                    continue;
                }

                float s = v.S[i];
                float rear = s - v.Length[i];
                bool clearedIn = !isIn || rear >= zone + GateRules.ClearMargin;
                if (clearedIn && !isOut)
                {
                    continue;   // зона входа уже позади, выходной зоны у connector'а нет
                }

                bool inZone = (isIn && rear < zone) || (isOut && s > lc - zone);
                if (inZone && PedRules.IsStanding(v, i))
                {
                    continue;   // стоит на полосе перехода: пешеходы обходят
                }

                return true;
            }

            // (б) только выходная зона: машина уже на выходной полосе, задний бампер ещё в зоне перехода.
            if (isOut)
            {
                int outLane = cs.OutLane[g];
                for (int i = occ.LaneTail[outLane]; i != -1; i = v.Ahead[i])
                {
                    if (v.Lane[i] != outLane)
                    {
                        continue;
                    }

                    if (v.S[i] - v.Length[i] >= GateRules.ClearMargin)
                    {
                        break;   // эта и все впереди освободили зону
                    }

                    if (!PedRules.IsStanding(v, i))
                    {
                        return true;
                    }
                }
            }

            // (в) допущенные на входной полосе на этот connector, не успевающие остановиться перед стоп-линией.
            int inLane = cs.InLane[g];
            float laneLen = net.LaneLength[inLane];
            for (int i = occ.LaneHead[inLane]; i != -1 && GateRules.IsGranted(v, i); i = GateRules.BehindOn(v, i, inLane))
            {
                if (v.Lane[i] != inLane || v.GrantConnector[i] != lane)
                {
                    continue;
                }

                float d = laneLen - v.S[i] - GateRules.StopLineOffset;
                if (!PedRules.CanStop(d, v.Speed[i], w.Types[v.Type[i]].ComfortDecel))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MidObligated(World w, PedWorld pw, int m)
    {
        PedNetwork pn = pw.Net;
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        ref readonly SegmentData sd = ref net.Segments[pn.MidSegment[m]];
        float minDecel = 3f;
        for (int k = 0; k < w.Types.Length; k++)
        {
            minDecel = Math.Min(minDecel, w.Types[k].ComfortDecel);
        }

        minDecel = Math.Max(minDecel, 0.5f);
        for (int dir = 0; dir < 2; dir++)
        {
            int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            int count = dir == 0 ? sd.LanesAB : sd.LanesBA;
            for (int k = 0; k < count; k++)
            {
                int lane = first + k;
                if (net.Lanes[lane].Kind != LaneKind.Normal)
                {
                    continue;
                }

                pn.MidLaneRange(m, dir, net.LaneLength[lane], out float s0, out float s1);
                float vmax = 1.5f * net.Lanes[lane].SpeedLimit;
                float bound = s0 - 1f - (vmax * vmax / (2f * minDecel));
                for (int i = occ.LaneHead[lane]; i != -1; i = GateRules.BehindOn(v, i, lane))
                {
                    if (v.Lane[i] != lane)
                    {
                        continue;
                    }

                    float s = v.S[i];
                    if (s - v.Length[i] >= s1 + GateRules.ClearMargin)
                    {
                        continue;   // полосу перехода освободила
                    }

                    if (s > s0)
                    {
                        if (!PedRules.IsStanding(v, i))
                        {
                            return true;
                        }
                    }
                    else if (!PedRules.CanStop(s0 - s, v.Speed[i], w.Types[v.Type[i]].ComfortDecel))
                    {
                        return true;
                    }
                    else if (s < bound)
                    {
                        break;
                    }
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ после Load

    /// <summary>После <c>Load</c>: пересчитать производные (<c>NodeXIn/NodeXOut</c>, <c>MidClosed</c>, <c>XActive</c>, <c>CrossingSignals</c>) из <c>XState</c> и очередей.</summary>
    public static void RestoreDerived(World w)
    {
        PedWorld pw = w.Peds!;
        CrossingState xs = pw.X;
        Array.Clear(xs.NodeXIn);
        Array.Clear(xs.NodeXOut);
        Array.Clear(xs.MidClosed);
        Array.Clear(xs.XActive);
        Array.Clear(xs.CrossingSignals);
        xs.XClosedNowCount = 0;
        xs.ObservedCount = 0;
        int xn = PedWorld.XCount(w);
        for (int x = 0; x < xn; x++)
        {
            if (xs.XState[x] != XStateCode.Free)
            {
                RecomputeMasks(w, pw, xs, x);
            }

            if (xs.XState[x] != XStateCode.Free || xs.QCount[x] > 0)
            {
                xs.SetActive(x);
            }

            if (xs.Releases[x] > 0)
            {
                xs.ObservedCount++;
            }

            PedSignals.PublishState(xs, x);
        }
    }
}
