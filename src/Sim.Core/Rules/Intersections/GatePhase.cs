using System;
using System.Diagnostics;
using System.Numerics;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Ворота перекрёстков (intersections.md §5, §8) - вторая половина фазы 4 (после <see cref="SignalPhase"/>). Единственное
/// место, выдающее допуски: <c>GateState.Granted</c>, <c>GrantConnector</c>, <c>Ticket</c>; ведёт <c>WaitTicks</c>, <c>NodeTicket</c>
/// и пересобирает <c>OutReserved</c> (Σ длин машин, допущенных или находящихся в узле с выездом на полосу).
/// Порядок: (0) OutReserved по допущенным; (1) кандидаты в intrusive-списки узлов - обход занятых полос по маскам (ADR-0019, A2; эталон -
/// полный проход по машинам по возрастанию индекса, <c>ScanAll</c>);
/// (2) узлы в порядке первого касания, внутри узла кандидаты по ключу (WaitTicks desc, Index asc); условия §5.4 по очереди.
/// PRNG не используется, аллокаций нет (сортировка в stackalloc).
/// Уточнения реализации: кандидатом может быть не только голова полосы, но и машина, у которой все впереди на полосе уже
/// допущены (очередь на зелёный получает билеты по порядку и трогается без задержки «по одному»); дистанция решения -
/// <see cref="GateRules.DecisionDistance"/>; на зелёном допуск только если машина успевает к стоп-линии до конца жёлтого.
/// </summary>
internal static class GatePhase
{
    private const int MaxCandidatesPerNode = 128;
    private const int PrevResetChunk = 1024;

    public static void Run(World w)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        GateScratch g = w.Gate;
        var e = new RuleEnv(net, v, occ, RouteAccess.From(w));

        // 0. OutReserved: обнулить затронутые в прошлом тике полосы; заново соберём по допущенным и находящимся в узле.
        for (int k = 0; k < g.OutTouchedCount; k++)
        {
            net.OutReserved[g.OutTouched[k]] = 0f;
        }

        g.OutTouchedCount = 0;

        // 0b. NodeBusy (ADR-0016, D3): обнулить затронутые в прошлом тике узлы; заново соберём по машинам на connector'ах и допущенным.
        for (int k = 0; k < g.BusyTouchedCount; k++)
        {
            g.NodeBusy[g.BusyTouched[k]] = 0UL;
        }

        g.BusyTouchedCount = 0;

        // 0c. Снятие допуска на красном (§5.4 п. 6, ADR-0019): до сбора кандидатов и пересборки OutReserved/NodeBusy по допущенным.
        RefreshMasks(w);
        RevokeOnRed(w);
        if (w.Peds != null)
        {
            RevokeOnPed(w);
        }

        // 1. Сбор кандидатов. Обычно - по активным множествам (маски занятых полос, ADR-0019 A2); полный проход по машинам - в первый тик и
        // после смены сети/узлов (RefreshMasks), а в Debug - как эталон, с которым сверяется активный проход на каждом тике.
        int candidates;
        if (g.FullScan)
        {
            g.FullScan = false;
            candidates = ScanAll(w, in e);
        }
        else
        {
#if DEBUG
            DebugSnapshotInput(w);
#endif
            candidates = ScanActive(w, in e);
#if DEBUG
            candidates = DebugCompareWithFullScan(w, in e, candidates);
#endif
        }

        occ.ClearLinked();
        (g.PrevList, g.NextList) = (g.NextList, g.PrevList);
        g.PrevCount = g.NextCount;
        w.Work.GateCandidates += candidates;

        // 2. Разрешение по узлам: параллельно (scale.md §4), узлы друг с другом не связаны; слияние буферов воркеров - на главном потоке.
        Resolve(w, in e);
    }

    private static void Resolve(World w, in RuleEnv e)
    {
        GateScratch g = w.Gate;
        GateResolveJob job = w.GateResolve;
        job.Prepare(w, in e);
        int count = g.TouchedCount;
        RunJob(w, job, count);
        GateWorker[] ws = g.Workers;
        for (int k = 0; k < ws.Length; k++)
        {
            GateWorker gw = ws[k];
            Merge(g, gw);
            for (int q = 0; q < gw.GrantedCount; q++)
            {
                w.Queue?.OnGranted(gw.Granted[q]);   // Q-meso: голова -> кинематика, ведомый очереди освобождается (колесо и маски - только на главном потоке)
            }

            gw.GrantedCount = 0;
            gw.Collect(w);
        }

        g.TouchedCount = 0;
    }

    /// <summary>Запуск задания по узлам: на пуле ядра либо последовательно (без пула; в тестах - в обратном порядке).</summary>
    internal static void RunJob(World w, Util.IRangeJob job, int count) => RunJob(w, job, count, Util.WorkerPool.ChunkSize);

    internal static void RunJob(World w, Util.IRangeJob job, int count, int chunkSize)
    {
        if (w.Gate.ReverseOrder)
        {
            for (int k = count - 1; k >= 0; k--)
            {
                job.RunRange(0, k, k + 1);
            }
        }
        else if (w.Pool != null)
        {
            w.Pool.Run(job, count, chunkSize);
        }
        else if (count > 0)
        {
            job.RunRange(0, 0, count);
        }
    }

    /// <summary>Дописывает списки «тронутых» полос/узлов воркера в общие (порядок потребителям не важен: обнуление по списку) и обнуляет воркера.</summary>
    private static void Merge(GateScratch g, GateWorker gw)
    {
        Array.Copy(gw.OutTouched, 0, g.OutTouched, g.OutTouchedCount, gw.OutTouchedCount);
        g.OutTouchedCount += gw.OutTouchedCount;
        gw.OutTouchedCount = 0;
        Array.Copy(gw.BusyTouched, 0, g.BusyTouched, g.BusyTouchedCount, gw.BusyTouchedCount);
        g.BusyTouchedCount += gw.BusyTouchedCount;
        gw.BusyTouchedCount = 0;
    }

    internal static void ResolveNodes(World w, in RuleEnv e, GateWorker gw, int begin, int end)
    {
        GateScratch g = w.Gate;
        VehicleStore v = w.Vehicles;
        Span<int> buf = stackalloc int[MaxCandidatesPerNode];
        for (int k = begin; k < end; k++)
        {
            int node = g.TouchedNodes[k];
            int count = 0;
            for (int i = g.NodeCandHead[node]; i != -1; i = g.CandNext[i])
            {
                if (count == MaxCandidatesPerNode)
                {
                    break;
                }

                // Вставка по ключу (WaitTicks desc, Index asc).
                int pos = count;
                while (pos > 0 && Before(v, i, buf[pos - 1]))
                {
                    buf[pos] = buf[pos - 1];
                    pos--;
                }

                buf[pos] = i;
                count++;
            }

            for (int q = 0; q < count; q++)
            {
                int i = buf[q];
                GateDeny deny = TryGrant(w, in e, gw, node, i, g.CandConn[i]);
                g.DenyReason[i] = (byte)deny;
                if (deny == GateDeny.Exit)
                {
                    gw.DeniedExit++;
                }
            }

            g.NodeCandHead[node] = -1;
        }
    }

    /// <summary>
    /// Снятие допуска на красном (intersections.md §5.4 п. 6). На входных Normal-полосах узлов <c>Signal</c> в цветном режиме (<c>Fixed</c>/<c>Adaptive</c>) допущенные машины
    /// обходятся от головы (допущенные - префикс списка полосы): первая, у которой сигнал её connector'а <c>Red</c> и которая ещё комфортно
    /// останавливается (<c>Length - S - StopLineOffset &gt; v^2/(2*ComfortDecel) + 1 м</c>), теряет допуск вместе со всеми допущенными за ней.
    /// Машины на connector'ах и не успевающие остановиться не трогаются. Обход - по маске занятых полос со светофором (по возрастанию индекса).
    /// </summary>
    private static void RevokeOnRed(World w)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        GateScratch g = w.Gate;
        ulong[] occMask = occ.OccMask;
        ulong[] signal = g.SignalMask;
        ulong[]? kin = w.Queue?.KinMask;   // Q-meso: допущенные - только кинематические машины
        for (int wi = 0; wi < occMask.Length; wi++)
        {
            ulong active = occMask[wi] & signal[wi];
            if (kin != null)
            {
                active &= kin[wi];
            }

            while (active != 0UL)
            {
                int bit = BitOperations.TrailingZeroCount(active);
                active &= active - 1UL;
                int lane = (wi << 6) + bit;
                int slot = net.Nodes[net.LaneGateNode[lane]].Signal;
                if (slot < 0 || slot >= w.Signals.Count || !IsColored(w.Signals.Mode[slot]))
                {
                    continue;
                }

                bool revoking = false;
                int x = occ.LaneHead[lane];
                while (x != -1 && GateRules.IsGranted(v, x))
                {
                    int next = GateRules.BehindOn(v, x, lane);
                    if (v.Lane[x] == lane)   // ghost: её допуск ведёт основная полоса
                    {
                        int gc = v.GrantConnector[x];
                        if (!revoking && gc >= 0 && w.LaneSignals[gc] == (byte)LaneSignal.Red && (v.Flags[x] & (byte)VehicleFlags.OnCall) == 0)   // VC3: допуск спецмашины на вызове не снимается
                        {
                            ref readonly VehicleTypeDesc t = ref w.Types[v.Type[x]];
                            float d = net.LaneLength[lane] - v.S[x] - GateRules.StopLineOffset;
                            float sp = v.Speed[x];
                            revoking = d > (sp * sp / (2f * t.ComfortDecel)) + 1f;
                        }

                        if (revoking)
                        {
                            v.GateState[x] = 0;
                            v.GrantConnector[x] = -1;
                            w.Counters.GrantsRevoked++;
                            occ.MarkLinked(x);   // метка StoppedBeforeSignal для остановившихся в хвосте (активный проход их не обходит)
                            w.Queue?.OnRevoked(x);   // голова очереди: полоса больше не обходится каждый тик - метки у линии ставит синтез
                        }
                    }

                    x = next;
                }
            }
        }
    }

    /// <summary>
    /// Снятие допуска при закрытии перехода (pedestrians.md §5.6, обобщение п. 6): для переходов узлов из <c>XClosedNow</c> (закрылись в этом тике) на входных полосах
    /// connector'ов в масках <c>XIn | XOut</c> первая допущенная машина с connector'ом из маски, которая ещё <see cref="PedRules.CanStop"/> перед стоп-линией, теряет допуск
    /// вместе с допущенными за ней. Машины, не успевающие остановиться, обязались и едут (их ждут пешеходы). Счётчик <c>GrantsRevokedPed</c>.
    /// </summary>
    private static void RevokeOnPed(World w)
    {
        PedWorld pw = w.Peds!;
        CrossingState xs = pw.X;
        PedNetwork pn = pw.Net;
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        ConnectorStore cs = w.Connectors;
        Span<int> lanes = stackalloc int[2 * NetworkBuilder.MaxConnectorsPerNode];
        for (int k = 0; k < xs.XClosedNowCount; k++)
        {
            int x = xs.XClosedNow[k];
            if (x >= pn.MidXBase)
            {
                continue;
            }

            ref readonly NodeData nd = ref net.Nodes[pn.Edges[pn.XEdge[x]].Node];
            byte closed = xs.XClosedNowBits[k];
            ulong mask = ((closed & 1) != 0 ? pn.XIn[x] : 0UL) | ((closed & 2) != 0 ? pn.XOut[x] : 0UL);
            ulong rest = mask;
            int done = 0;
            while (rest != 0UL)
            {
                int b = BitOperations.TrailingZeroCount(rest);
                rest &= rest - 1UL;
                int g = net.Lanes[nd.FirstConnector + b].ConflictGroup;
                if (g < 0)
                {
                    continue;
                }

                int inLane = cs.InLane[g];
                bool seen = false;
                for (int q = 0; q < done; q++)
                {
                    seen |= lanes[q] == inLane;
                }

                if (seen)
                {
                    continue;
                }

                lanes[done++] = inLane;
                float laneLen = net.LaneLength[inLane];
                bool revoking = false;
                int i = occ.LaneHead[inLane];
                while (i != -1 && GateRules.IsGranted(v, i))
                {
                    int next = GateRules.BehindOn(v, i, inLane);
                    if (v.Lane[i] == inLane)   // ghost: её допуск ведёт основная полоса
                    {
                        int gc = v.GrantConnector[i];
                        if (!revoking && gc >= nd.FirstConnector && gc - nd.FirstConnector < 64 && ((mask >> (gc - nd.FirstConnector)) & 1UL) != 0UL)
                        {
                            float d = laneLen - v.S[i] - GateRules.StopLineOffset;
                            revoking = PedRules.CanStop(d, v.Speed[i], w.Types[v.Type[i]].ComfortDecel);
                        }

                        if (revoking)
                        {
                            v.GateState[i] = 0;
                            v.GrantConnector[i] = -1;
                            w.Counters.GrantsRevokedPed++;
                            occ.MarkLinked(i);
                        }
                    }

                    i = next;
                }
            }
        }
    }

    /// <summary>«Цветной» режим светофора (<c>Fixed</c> или <c>Adaptive</c>, 2.5): есть красный/зелёный, действуют п. 1, 4b-bis и снятие допуска на красном; у <c>FlashYellow</c>/<c>Off</c> красного нет.</summary>
    private static bool IsColored(SignalMode m) => m == SignalMode.Fixed || m == SignalMode.Adaptive;

    /// <summary>
    /// Статические маски полос (какие полосы - connector'ы, с воротами, со светофором) пересобираются, когда изменилась сеть
    /// (<see cref="NetworkStore.Version"/>: BuildRoad, SetNodeControl). Смена сети меняет и классы машин на полосах (полоса потеряла ворота -
    /// <c>GateState</c> её машин сбрасывается полным проходом), поэтому тот же тик идёт полным проходом.
    /// </summary>
    private static void RefreshMasks(World w)
    {
        NetworkStore net = w.Net;
        GateScratch g = w.Gate;
        if (g.MaskVersion == net.Version)
        {
            return;
        }

        Array.Clear(g.GatedMask);
        Array.Clear(g.ConnMask);
        Array.Clear(g.SignalMask);
        Array.Fill(g.LaneNode, -1);
        int n = Math.Min(net.LaneCount, g.GatedMask.Length << 6);
        for (int lane = 0; lane < n; lane++)
        {
            ulong bit = 1UL << (lane & 63);
            if (net.Lanes[lane].Kind == LaneKind.Connector)
            {
                g.ConnMask[lane >> 6] |= bit;
                continue;
            }

            int node = net.LaneGateNode[lane];
            if (node >= 0)
            {
                g.GatedMask[lane >> 6] |= bit;
                if (net.Nodes[node].Control == NodeControl.Signal)
                {
                    g.SignalMask[lane >> 6] |= bit;
                }
            }
        }

        // CSR «узел -> его полосы (Normal с воротами и connector'ы)», по возрастанию индекса полосы (порядок сумм OutReserved как у прохода по маске).
        for (int lane = 0; lane < n; lane++)
        {
            int node = net.Lanes[lane].Kind == LaneKind.Connector ? net.Lanes[lane].Node : net.LaneGateNode[lane];
            if (node >= 0 && node < g.NodeLaneStart.Length - 1 && (((g.GatedMask[lane >> 6] | g.ConnMask[lane >> 6]) >> (lane & 63)) & 1UL) != 0UL)
            {
                g.LaneNode[lane] = node;
            }
        }

        Array.Clear(g.NodeLaneStart);
        for (int lane = 0; lane < n; lane++)
        {
            if (g.LaneNode[lane] >= 0)
            {
                g.NodeLaneStart[g.LaneNode[lane] + 1]++;
            }
        }

        for (int k = 0; k < g.NodeLaneStart.Length - 1; k++)
        {
            g.NodeLaneStart[k + 1] += g.NodeLaneStart[k];
        }

        Array.Copy(g.NodeLaneStart, g.NodeFill, g.NodeFill.Length);
        for (int lane = 0; lane < n; lane++)
        {
            int node = g.LaneNode[lane];
            if (node >= 0)
            {
                g.NodeLanes[g.NodeFill[node]++] = lane;
            }
        }

        g.MaskVersion = net.Version;
        g.FullScan = true;
    }

    /// <summary>
    /// Активный проход (ADR-0019, A2): те же эффекты, что у <see cref="ScanAll"/>, но обходятся только занятые connector'ы и занятые Normal-полосы
    /// с воротами (маска <see cref="LaneOccupancy.OccMask"/>, по возрастанию индекса полосы). На полосе с воротами достаточно префикса допущенных
    /// машин (они образуют голову списка: допуск получает только машина, перед которой все допущены) и первой недопущенной - единственного кандидата
    /// полосы. Состояние остальных машин (<c>WaitTicks</c>, <c>DenyReason</c>) сбрасывается по списку прошлого тика (<c>PrevList</c>: у всех
    /// остальных оно уже нулевое), а метка <c>StoppedBeforeSignal</c> ставится в фазе 6 и при связывании со списком полосы (<see cref="LaneOccupancy.Linked"/>).
    /// Порядок кандидатов в списках узлов иной, чем у полного прохода, но результат от него не зависит: внутри узла сортировка по ключу
    /// (<c>WaitTicks desc, Index asc</c>), узлы друг с другом не связаны.
    /// </summary>
    private static int ScanActive(World w, in RuleEnv e)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        GateScratch g = w.Gate;
        byte[] lodBits = v.Lod;                       // длина 0 - LOD выключен (ветка meso не исполняется)
        byte[] laneColor = w.Lod?.LaneColor ?? System.Array.Empty<byte>();
        int mesoBeat = (int)(w.Tick & 3);
        int stamp = ++g.Stamp;
        g.NextCount = 0;
        int candidates = 0;

        // Быстрый отсев головы далеко от стоп-линии: дальше max(30 м, MinGap + допуск метки остановки) и дальше DecisionDistance при самой малой
        // комфортной тормозной способности типов (DecisionDistance убывает по ней: оценка сверху, сравнение в том же float).
        float farDistance = GateRules.MinDecisionDistance;
        float minDecel = float.MaxValue;
        for (int k = 0; k < w.Types.Length; k++)
        {
            farDistance = Math.Max(farDistance, w.Types[k].MinGap + GateRules.StopLineTolerance);
            minDecel = Math.Min(minDecel, w.Types[k].ComfortDecel);
        }

        if (minDecel < 0.1f)
        {
            minDecel = 0.1f;   // как в DecisionDistance
        }

        ulong[] occMask = occ.OccMask;
        ulong[] gated = g.GatedMask;
        ulong[] conn = g.ConnMask;
        Lod.QueueMeso? q = w.Queue;
        ulong[]? qKin = q?.KinMask;                   // Q-meso: полосы с кинематическими машинами (connector'ы, допущенные)
        ulong[]? qHead = q?.HeadMask[mesoBeat];       // и с головой очереди цвета такта; полосы только с очередью не обходятся

        // Активные узлы: узлы занятых полос (по возрастанию индекса). Последовательно и дёшево: по маске полос, без чтения машин.
        int[] laneNode = g.LaneNode;
        ulong[] nodeMask = g.NodeActiveMask;
        for (int wi = 0; wi < occMask.Length; wi++)
        {
            ulong active = occMask[wi] & (gated[wi] | conn[wi]);
            if (qKin != null)
            {
                active &= qKin[wi] | qHead![wi];
            }

            while (active != 0UL)
            {
                int bit = BitOperations.TrailingZeroCount(active);
                active &= active - 1UL;
                int n = laneNode[(wi << 6) + bit];
                nodeMask[n >> 6] |= 1UL << (n & 63);
            }
        }

        int[] activeNodes = g.ActiveNodes;
        int activeCount = 0;
        for (int wi = 0; wi < nodeMask.Length; wi++)
        {
            ulong m = nodeMask[wi];
            nodeMask[wi] = 0UL;
            while (m != 0UL)
            {
                activeNodes[activeCount++] = (wi << 6) + BitOperations.TrailingZeroCount(m);
                m &= m - 1UL;
            }
        }

        g.ActiveCount = activeCount;
        GateScanJob job = w.GateScan;
        job.Prepare(w, in e, stamp, farDistance, minDecel, mesoBeat);
        RunJob(w, job, activeCount);
        GateWorker[] ws = g.Workers;
        for (int k = 0; k < ws.Length; k++)
        {
            GateWorker gw = ws[k];
            Merge(g, gw);
            candidates += gw.Candidates;
            gw.Candidates = 0;
            w.Counters.BoxWaitTicks += gw.BoxWaitTicks;
            gw.BoxWaitTicks = 0;
            Array.Copy(gw.TouchedNodes, 0, g.TouchedNodes, g.TouchedCount, gw.TouchedCount);
            g.TouchedCount += gw.TouchedCount;
            gw.TouchedCount = 0;
            Array.Copy(gw.NextList, 0, g.NextList, g.NextCount, gw.NextCount);
            g.NextCount += gw.NextCount;
            gw.NextCount = 0;
        }

        // Машины, связанные со списком полосы после прошлой фазы 4 (въезд, вставка, смена полосы, спавн): метка остановки перед светофором
        // по их скорости на начало этого тика - как у полного прохода (дальше её ставит фаза 6 по скорости конца тика).
        ulong[] signal = g.SignalMask;
        int[] linked = occ.Linked;
        for (int k = 0; k < occ.LinkedCount; k++)
        {
            int i = linked[k];
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int lane = v.Lane[i];
            if (lane >= 0 && ((signal[lane >> 6] >> (lane & 63)) & 1UL) != 0UL && !GateRules.IsGranted(v, i) && v.Speed[i] < 1f)
            {
                v.GateState[i] |= GateRules.StoppedBeforeSignal;
            }
        }

        // Сброс по списку прошлого тика: всё, что было кандидатом (или хранит отказ), а теперь им не стало. Записи независимы (по машине) - задание по
        // элементам списка (S4c); «оставить в списке» (meso не в свой такт) - флаг элемента, перенос в NextList - последовательно, в порядке списка.
        if (g.PrevCount > 0)
        {
            GatePrevResetJob rj = w.GatePrevReset;
            rj.Prepare(w, stamp, mesoBeat);
            RunJob(w, rj, g.PrevCount, PrevResetChunk);
            int[] prev = g.PrevList;
            byte[] keep = g.PrevKeep;
            for (int k = 0; k < g.PrevCount; k++)
            {
                if (keep[k] != 0)
                {
                    g.NextList[g.NextCount++] = prev[k];
                }
            }
        }

        return candidates;
    }

    /// <summary>Машины connector'а: узел занят (NodeBusy), допущенная резервирует выезд (то же, что ветка connector'а в <see cref="ScanAll"/>).</summary>
    internal static void ScanConnector(World w, GateWorker gw, int lane)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        GateScratch g = w.Gate;
        ConnectorStore cs = w.Connectors;
        ref readonly LaneData ld = ref net.Lanes[lane];
        Debug.Assert(ld.Kind == LaneKind.Connector, "ScanConnector: не connector");
        float stuckSpeed = w.Rules.StuckSpeedThreshold;
        for (int x = w.Occupancy.LaneHead[lane]; x != -1; x = GateRules.BehindOn(v, x, lane))
        {
            if (v.Lane[x] != lane)
            {
                continue;   // ghost: у connector'а смены полосы нет, но обход единообразен с Normal-полосой
            }

            MarkBusy(gw.BusyTouched, ref gw.BusyTouchedCount, g.NodeBusy, cs, ld.ConflictGroup);
            if (GateRules.IsGranted(v, x))
            {
                AddReserved(net, gw.OutTouched, ref gw.OutTouchedCount, ld.ToLane, v.Length[x]);
                if (v.Speed[x] < stuckSpeed)
                {
                    gw.BoxWaitTicks++;
                }
            }
        }
    }

    /// <summary>Полоса с воротами: префикс допущенных и первая недопущенная (кандидат). Возвращает 1, если кандидат есть.</summary>
    internal static int ScanGatedLane(World w, in RuleEnv e, GateWorker gw, int lane, byte[] lodBits, byte[] laneColor, int mesoBeat, int stamp, float farDistance, float minDecel)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        GateScratch g = w.Gate;
        ConnectorStore cs = w.Connectors;
        int node = GateRules.GateNodeOfNormal(net, lane);
        Debug.Assert(node >= 0 && net.Lanes[lane].Kind != LaneKind.Connector, "ScanGatedLane: полоса без ворот в маске");
        float laneLen = net.LaneLength[lane];
        bool signalNode = ((g.SignalMask[lane >> 6] >> (lane & 63)) & 1UL) != 0UL;
        bool aheadOk = true;   // впереди на полосе (без ghost) нет машины либо она допущена
        int x = occ.LaneHead[lane];
        while (x != -1)
        {
            if (v.Lane[x] != lane)
            {
                x = v.GhostBehind[x];   // ghost: её допуск и порядок ведёт основная полоса
                continue;
            }

            if (GateRules.IsGranted(v, x))
            {
                int behind = v.Behind[x];
                int gc = v.GrantConnector[x];
                if (gc < 0 || net.Lanes[gc].FromLane != lane)
                {
                    v.GateState[x] = 0;   // допуск от другой полосы: сброс (машина допущенной впереди больше не считается)
                    v.GrantConnector[x] = -1;
                    aheadOk = false;
                }
                else
                {
                    AddReserved(net, gw.OutTouched, ref gw.OutTouchedCount, net.Lanes[gc].ToLane, v.Length[x]);
                    MarkBusy(gw.BusyTouched, ref gw.BusyTouchedCount, g.NodeBusy, cs, net.Lanes[gc].ConflictGroup);
                }

                x = behind;
                continue;
            }

            if (lodBits.Length != 0 && (lodBits[x] & (Lod.LodBits.Queue | Lod.LodBits.Head)) == Lod.LodBits.Queue)
            {
                return 0;   // Q-meso: машина очереди (не материализована) - не кандидат и меток не получает (как в ScanAll)
            }

            float speed = v.Speed[x];
            if (signalNode && speed < 1f)
            {
                v.GateState[x] |= GateRules.StoppedBeforeSignal;   // метрика остановок на светофоре (2.5 S1); сбрасывается допуском
            }

            float d = laneLen - v.S[x];
            if (d > farDistance && d > (speed * speed / (2f * minDecel)) + (1.5f * speed) && d > 3f * speed)
            {
                return 0;   // дальше дистанции решения при любой комфортной тормозной способности и дальше StoppedAtLine: ни кандидата, ни метки
            }

            ref readonly VehicleTypeDesc t = ref w.Types[v.Type[x]];
            if (speed < 0.1f && d <= t.MinGap + GateRules.StopLineTolerance)
            {
                v.GateState[x] |= GateRules.StoppedAtLine;
            }

            bool meso = lodBits.Length != 0 && (lodBits[x] & Lod.LodBits.Meso) != 0;
            if (meso && laneColor[lane] != mesoBeat)
            {
                return 0;   // meso: кандидатом бывает только в такт цвета своей ячейки (sim-lod.md §2.4)
            }

            if (!aheadOk || d > GateRules.DecisionDistance(speed, t.ComfortDecel))
            {
                return 0;   // WaitTicks и DenyReason сбросит список прошлого тика
            }

            int conn = e.FirstHop(x, lane);
            if (conn < 0 || net.Lanes[conn].Kind != LaneKind.Connector || net.Lanes[conn].FromLane != lane)
            {
                return 0;   // маршрут требует другую полосу - connector'а с этой полосы нет (routing.md §6.2)
            }

            // Единицы WaitTicks - тики: meso-кандидат видится раз в 4 тика (sim-lod.md §2.4).
            int waitStep = meso ? 4 : 1;
            v.WaitTicks[x] = (ushort)Math.Min(ushort.MaxValue, v.WaitTicks[x] + waitStep);
            g.CandConn[x] = conn;
            g.CandStamp[x] = stamp;
            gw.NextList[gw.NextCount++] = x;
            if (g.NodeCandHead[node] == -1)
            {
                gw.TouchedNodes[gw.TouchedCount++] = node;
            }

            g.CandNext[x] = g.NodeCandHead[node];
            g.NodeCandHead[node] = x;
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Полный проход по машинам (эталон до ADR-0019 A2): первый тик, смена сети/узлов, Debug-сверка. Заполняет <c>NextList</c> всеми машинами с
    /// ненулевыми <c>WaitTicks</c>/<c>DenyReason</c> (надмножество списка активного прохода).
    /// </summary>
    private static int ScanAll(World w, in RuleEnv e)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        GateScratch g = w.Gate;
        float stuckSpeed = w.Rules.StuckSpeedThreshold;
        byte[] lodBits = v.Lod;                       // длина 0 - LOD выключен (ветка meso не исполняется)
        byte[] laneColor = w.Lod?.LaneColor ?? System.Array.Empty<byte>();
        int mesoBeat = (int)(w.Tick & 3);
        ConnectorStore cs = w.Connectors;
        int hw = v.HighWater;
        int candidates = 0;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int lane = v.Lane[i];
            if (lane < 0)
            {
                v.WaitTicks[i] = 0;
                continue;
            }

            ref readonly LaneData ld = ref net.Lanes[lane];
            if (ld.Kind == LaneKind.Connector)
            {
                v.WaitTicks[i] = 0;
                MarkBusy(g, cs, ld.ConflictGroup);
                if (GateRules.IsGranted(v, i))
                {
                    AddReserved(net, g, ld.ToLane, v.Length[i]);
                    if (v.Speed[i] < stuckSpeed)
                    {
                        w.Counters.BoxWaitTicks++;
                    }
                }

                continue;
            }

            if (lodBits.Length != 0 && (lodBits[i] & (Lod.LodBits.Queue | Lod.LodBits.Head)) == Lod.LodBits.Queue)
            {
                continue;   // Q-meso: машина очереди (не материализована) фазой 4 не трогается (WaitTicks = 0, допуска нет)
            }

            int node = GateRules.GateNodeOfNormal(net, lane);
            if (node < 0)
            {
                v.WaitTicks[i] = 0;
                v.GateState[i] = 0;
                v.GrantConnector[i] = -1;
                continue;
            }

            if (GateRules.IsGranted(v, i))
            {
                v.WaitTicks[i] = 0;
                int gc = v.GrantConnector[i];
                if (gc < 0 || net.Lanes[gc].FromLane != lane)
                {
                    v.GateState[i] = 0;   // допуск от другой полосы (смена полосы после допуска): сброс
                    v.GrantConnector[i] = -1;
                    continue;
                }

                AddReserved(net, g, net.Lanes[gc].ToLane, v.Length[i]);
                MarkBusy(g, cs, net.Lanes[gc].ConflictGroup);
                continue;
            }

            if (v.Speed[i] < 1f && net.Nodes[node].Control == NodeControl.Signal)
            {
                v.GateState[i] |= GateRules.StoppedBeforeSignal;   // метрика остановок на светофоре (2.5 S1); сбрасывается допуском
            }

            ref readonly VehicleTypeDesc t = ref w.Types[v.Type[i]];
            float d = net.LaneLength[lane] - v.S[i];
            if (v.Speed[i] < 0.1f && d <= t.MinGap + GateRules.StopLineTolerance)
            {
                v.GateState[i] |= GateRules.StoppedAtLine;
            }

            // Порядок допусков идёт по машинам, чей основной список - эта полоса. Ghost (машина, уходящая на соседнюю полосу) здесь не в счёт:
            // допуск он получил на целевой полосе, и если считать его «допущенным впереди», ведомый получит билет раньше недопущенного лидера
            // за ghost'ом (лидер физически впереди, но билет позже -> взаимная блокировка «лидер ждёт ведомого по билету, ведомый - лидера физически»).
            int ahead = v.Ahead[i];
            while (ahead != -1 && v.Lane[ahead] != lane)
            {
                ahead = v.GhostAhead[ahead];
            }

            bool meso = lodBits.Length != 0 && (lodBits[i] & Lod.LodBits.Meso) != 0;
            if (meso && laneColor[lane] != mesoBeat)
            {
                continue; // meso: кандидатом бывает только в такт цвета своей ячейки (sim-lod.md §2.4); WaitTicks и DenyReason в чужой такт не трогаем
            }

            if ((ahead != -1 && !GateRules.IsGranted(v, ahead)) || d > GateRules.DecisionDistance(v.Speed[i], t.ComfortDecel))
            {
                v.WaitTicks[i] = 0;
                g.DenyReason[i] = (byte)GateDeny.None;
                continue;
            }

            int conn = e.FirstHop(i, lane);
            if (conn < 0 || net.Lanes[conn].Kind != LaneKind.Connector || net.Lanes[conn].FromLane != lane)
            {
                v.WaitTicks[i] = 0;   // маршрут требует другую полосу - connector'а с этой полосы нет (routing.md §6.2)
                g.DenyReason[i] = (byte)GateDeny.None;
                continue;
            }

            // Единицы WaitTicks - тики: meso-кандидат видится раз в 4 тика (sim-lod.md §2.4).
            int waitStep = meso ? 4 : 1;
            v.WaitTicks[i] = (ushort)Math.Min(ushort.MaxValue, v.WaitTicks[i] + waitStep);

            g.CandConn[i] = conn;
            candidates++;
            if (g.NodeCandHead[node] == -1)
            {
                g.TouchedNodes[g.TouchedCount++] = node;
            }

            g.CandNext[i] = g.NodeCandHead[node];
            g.NodeCandHead[node] = i;
        }

        g.NextCount = 0;
        for (int i = 0; i < hw; i++)
        {
            if (v.WaitTicks[i] != 0 || g.DenyReason[i] != 0)
            {
                g.NextList[g.NextCount++] = i;
            }
        }

        return candidates;
    }

#if DEBUG
    private static void DebugSnapshotInput(World w)
    {
        VehicleStore v = w.Vehicles;
        GateScratch g = w.Gate;
        int hw = v.HighWater;
        Array.Copy(v.WaitTicks, g.DbgWait0, hw);
        Array.Copy(g.DenyReason, g.DbgDeny0, hw);
        Array.Copy(v.GateState, g.DbgGate0, hw);
        Array.Copy(v.GrantConnector, g.DbgGrant0, hw);
        g.DbgBox0 = w.Counters.BoxWaitTicks;
    }

    /// <summary>
    /// Debug-сверка (ADR-0019 риск 4: пропуск в маске - молчаливая ошибка): после активного прохода снимается результат, вход восстанавливается,
    /// выполняется полный проход <see cref="ScanAll"/>, и всё, что читают дальше фазы 4-6 (WaitTicks, GateState, GrantConnector, DenyReason
    /// недопущенных на полосах с воротами, OutReserved, NodeBusy, число кандидатов, BoxWaitTicks), сравнивается. Метка StoppedBeforeSignal
    /// (диагностика, вне хэша) - односторонне: всё, что поставил полный проход в этом тике, должен был поставить и активный. Дальше живёт результат
    /// полного прохода (он равен активному). Кроме того, список <c>NextList</c> активного прохода обязан содержать всех живых с ненулевым состоянием.
    /// </summary>
    private static int DebugCompareWithFullScan(World w, in RuleEnv e, int candidatesFast)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        GateScratch g = w.Gate;
        int hw = v.HighWater;

        Array.Clear(g.DbgListed);
        for (int k = 0; k < g.NextCount; k++)
        {
            int i = g.NextList[k];
            g.DbgListed[i >> 6] |= 1UL << (i & 63);
        }

        for (int i = 0; i < hw; i++)
        {
            if (v.Pool.IsAlive(i) && (v.WaitTicks[i] != 0 || g.DenyReason[i] != 0) && (g.DbgListed[i >> 6] & (1UL << (i & 63))) == 0)
            {
                Debug.Fail($"GatePhase: машина {i} с WaitTicks={v.WaitTicks[i]}/Deny={g.DenyReason[i]} не в списке прошлого тика");
            }
        }

        Array.Copy(v.WaitTicks, g.DbgWait1, hw);
        Array.Copy(g.DenyReason, g.DbgDeny1, hw);
        Array.Copy(v.GateState, g.DbgGate1, hw);
        Array.Copy(v.GrantConnector, g.DbgGrant1, hw);
        Array.Copy(net.OutReserved, g.DbgOut1, net.OutReserved.Length);
        Array.Copy(g.NodeBusy, g.DbgBusy1, g.NodeBusy.Length);
        long boxFast = w.Counters.BoxWaitTicks;

        for (int k = 0; k < g.TouchedCount; k++)
        {
            g.NodeCandHead[g.TouchedNodes[k]] = -1;
        }

        g.TouchedCount = 0;
        for (int k = 0; k < g.OutTouchedCount; k++)
        {
            net.OutReserved[g.OutTouched[k]] = 0f;
        }

        g.OutTouchedCount = 0;
        for (int k = 0; k < g.BusyTouchedCount; k++)
        {
            g.NodeBusy[g.BusyTouched[k]] = 0UL;
        }

        g.BusyTouchedCount = 0;
        Array.Copy(g.DbgWait0, v.WaitTicks, hw);
        Array.Copy(g.DbgDeny0, g.DenyReason, hw);
        Array.Copy(g.DbgGate0, v.GateState, hw);
        Array.Copy(g.DbgGrant0, v.GrantConnector, hw);
        w.Counters.BoxWaitTicks = g.DbgBox0;

        int candidates = ScanAll(w, in e);

        if (candidates != candidatesFast)
        {
            Debug.Fail($"GatePhase: кандидатов {candidatesFast} (активный проход) против {candidates} (полный)");
        }

        if (w.Counters.BoxWaitTicks != boxFast)
        {
            Debug.Fail($"GatePhase: BoxWaitTicks {boxFast} (активный проход) против {w.Counters.BoxWaitTicks} (полный)");
        }

        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            if (g.DbgWait1[i] != v.WaitTicks[i])
            {
                Debug.Fail($"GatePhase: WaitTicks[{i}] {g.DbgWait1[i]} против {v.WaitTicks[i]} (полный проход)");
            }

            if (g.DbgGrant1[i] != v.GrantConnector[i])
            {
                Debug.Fail($"GatePhase: GrantConnector[{i}] {g.DbgGrant1[i]} против {v.GrantConnector[i]} (полный проход)");
            }

            byte full = v.GateState[i];
            const byte sbs = GateRules.StoppedBeforeSignal;
            if ((g.DbgGate1[i] & ~sbs) != (full & ~sbs))
            {
                Debug.Fail($"GatePhase: GateState[{i}] {g.DbgGate1[i]} против {full} (полный проход)");
            }

            if ((full & sbs) != 0 && (g.DbgGate0[i] & sbs) == 0 && (g.DbgGate1[i] & sbs) == 0)
            {
                Debug.Fail($"GatePhase: StoppedBeforeSignal[{i}] не поставлена активным проходом");
            }

            int lane = v.Lane[i];
            if (lane >= 0 && net.Lanes[lane].Kind != LaneKind.Connector && GateRules.GateNodeOfNormal(net, lane) >= 0
                && !GateRules.IsGranted(v, i) && g.DbgDeny1[i] != g.DenyReason[i])
            {
                Debug.Fail($"GatePhase: DenyReason[{i}] {g.DbgDeny1[i]} против {g.DenyReason[i]} (полный проход)");
            }
        }

        for (int l = 0; l < net.OutReserved.Length; l++)
        {
            if (BitConverter.SingleToInt32Bits(g.DbgOut1[l]) != BitConverter.SingleToInt32Bits(net.OutReserved[l]))
            {
                Debug.Fail($"GatePhase: OutReserved[{l}] {g.DbgOut1[l]} против {net.OutReserved[l]} (полный проход)");
            }
        }

        for (int n = 0; n < g.NodeBusy.Length; n++)
        {
            if (g.DbgBusy1[n] != g.NodeBusy[n])
            {
                Debug.Fail($"GatePhase: NodeBusy[{n}] {g.DbgBusy1[n]:x} против {g.NodeBusy[n]:x} (полный проход)");
            }
        }

        return candidates;
    }
#endif

    /// <summary>
    /// Сброс допусков узла (intersections.md §5.7) при смене <c>Control</c> / перестройке connector'ов: машины на входных
    /// полосах теряют допуск; машины уже на connector'ах узла доезжают (билеты сохраняются). <c>OutReserved</c> пересоберёт
    /// следующий тик; <c>NodeTicket</c> не сбрасывается.
    /// </summary>
    public static void ResetNode(World w, int node)
    {
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0 || !GateRules.IsGranted(v, i))
            {
                continue;
            }

            if (net.Lanes[v.Lane[i]].Kind != LaneKind.Normal)
            {
                continue;
            }

            int gc = v.GrantConnector[i];
            if (gc >= 0 && net.Lanes[gc].Node == node)
            {
                v.GateState[i] = 0;
                v.GrantConnector[i] = -1;
            }
        }
    }

    private static bool Before(VehicleStore v, int a, int b)
    {
        ushort wa = v.WaitTicks[a];
        ushort wb = v.WaitTicks[b];
        return wa > wb || (wa == wb && a < b);
    }

    /// <summary>Ставит бит connector'а группы <paramref name="group"/> в <c>NodeBusy</c> его узла (D3); группа &lt; 0 - connector без записи в store.</summary>
    private static void MarkBusy(GateScratch g, ConnectorStore cs, int group) => MarkBusy(g.BusyTouched, ref g.BusyTouchedCount, g.NodeBusy, cs, group);

    private static void MarkBusy(int[] touched, ref int touchedCount, ulong[] nodeBusy, ConnectorStore cs, int group)
    {
        if (group < 0)
        {
            return;
        }

        int node = cs.Node[group];
        ulong m = nodeBusy[node];
        if (m == 0UL)
        {
            touched[touchedCount++] = node;
        }

        nodeBusy[node] = m | (1UL << cs.LocalBit[group]);
    }

    private static void AddReserved(NetworkStore net, GateScratch g, int lane, float length) => AddReserved(net, g.OutTouched, ref g.OutTouchedCount, lane, length);

    private static void AddReserved(NetworkStore net, int[] touched, ref int touchedCount, int lane, float length)
    {
        if ((uint)lane >= (uint)net.OutReserved.Length)
        {
            return;
        }

        if (net.OutReserved[lane] == 0f)
        {
            touched[touchedCount++] = lane;
        }

        net.OutReserved[lane] += length;
    }

    /// <summary>Условия допуска §5.4 по порядку; первое нарушенное - код отказа. <see cref="GateDeny.None"/> = допуск выдан.</summary>
    private static GateDeny TryGrant(World w, in RuleEnv e, GateWorker gw, int node, int i, int conn)
    {
        gw.TryGrantCalls++;
        NetworkStore net = w.Net;
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = w.Occupancy;
        ConnectorStore cs = w.Connectors;
        ref readonly NodeData nd = ref net.Nodes[node];
        ref readonly LaneData cl = ref net.Lanes[conn];
        ref readonly VehicleTypeDesc t = ref w.Types[v.Type[i]];
        int lane = v.Lane[i];
        float lenIn = net.LaneLength[lane];
        float dLine = lenIn - v.S[i];
        float vi = v.Speed[i];
        float v0 = v.DesiredSpeed[i];
        float li = v.Length[i];
        int g = cl.ConflictGroup;

        // 0. Закрытие дренажем (4.3, T4): въезд на connector или на его выходную полосу закрыт - ждать нельзя, пока не сменится маршрут/connector по умолчанию.
        if (net.ToolsTouched != 0 && ((cl.Flags & LaneFlags.Closed) != 0 || (net.Lanes[cl.ToLane].Flags & LaneFlags.Closed) != 0))
        {
            return GateDeny.Closed;
        }

        // 1. Сигнал.
        bool onRedGrant = false;   // VC3: спецмашина на вызове допущена на красный (п. 1) - в п. 4b конфликты для неё Yield
        int slot = nd.Signal;
        bool fixedSignal = nd.Control == NodeControl.Signal && slot >= 0 && slot < w.Signals.Count && IsColored(w.Signals.Mode[slot]);
        if (fixedSignal)
        {
            byte sig = w.LaneSignals[conn];
            bool cannotStop = dLine < vi * vi / (2f * t.ComfortDecel);
            if (sig == (byte)LaneSignal.Green)
            {
                float window = (w.SignalStates[slot].TicksToChange + w.Signals.YellowTicks[slot]) * PhysicsParams.DefaultDt;
                if (!cannotStop && GateRules.Eta(dLine, vi, t.Accel, v0) > window)
                {
                    return GateDeny.Signal;
                }
            }
            else if (sig == (byte)LaneSignal.Yellow)
            {
                float window = w.SignalStates[slot].TicksToChange * PhysicsParams.DefaultDt;
                if (!cannotStop && GateRules.Eta(dLine, vi, t.Accel, v0) > window)
                {
                    return GateDeny.Signal;
                }
            }
            else if ((v.Flags[i] & (byte)VehicleFlags.OnCall) != 0 && vi <= w.Rules.EmergencyRedSpeed)
            {
                onRedGrant = true;   // красный: машина на вызове сбросила скорость до порога - сигнал её не держит (vehicle-classes.md §2.4 п. 2)
            }
            else
            {
                return GateDeny.Signal;
            }
        }

        // 2. STOP на второстепенном рукаве: полная остановка у линии.
        if (g >= 0 && ((nd.Control == NodeControl.Stop && (cs.Flags[g] & 1) != 0) || ((cs.Flags[g] & 2) != 0 && nd.Control != NodeControl.Signal)) && (v.GateState[i] & GateRules.StoppedAtLine) == 0)
        {
            return GateDeny.Stop;
        }

        // 2-bis. Переход (pedestrians.md §5.6): connector в маске закрытого перехода узла - отказ, пока пешеходы не пройдут; машины, уже допущенные и не успевающие
        // остановиться, едут (они обязались), остальные потеряли допуск в начале фазы (RevokeOnPed).
        if (w.Peds != null && g >= 0)
        {
            CrossingState xs = w.Peds.X;
            if (((xs.NodeXIn[node] | xs.NodeXOut[node]) >> cs.LocalBit[g] & 1UL) != 0UL)
            {
                int cin = cs.CrossInX![g];
                int cout = cs.CrossOutX![g];
                if (cin >= 0)
                {
                    xs.CarsWaiting[cin] = 1;   // машина ждёт пару переходов (вход и выход): окно машин у открытого из них держится, пока она не проедет
                }

                if (cout >= 0)
                {
                    xs.CarsWaiting[cout] = 1;
                }

                gw.CarCrossDelayTicks++;
                return GateDeny.Ped;
            }
        }

        // 3. Выезд свободен (don't-block-the-box) и место на самом connector'е.
        int outLane = cl.ToLane;
        if (!w.DebugDisableExitCheck)
        {
            float need = li + t.MinGap;
            int tail = occ.LaneTail[outLane];
            if (tail != -1 && (w.Queue != null ? w.Queue.TailS(outLane) : v.S[tail]) - v.Length[tail] - net.OutReserved[outLane] < need)
            {
                return GateDeny.Exit;
            }

            if (tail == -1 && net.OutReserved[outLane] > 0f && net.Lanes[outLane].Length - net.OutReserved[outLane] < need)
            {
                return GateDeny.Exit;
            }

            int ctail = occ.LaneTail[conn];
            if (ctail != -1 && v.S[ctail] - v.Length[ctail] < need)
            {
                return GateDeny.Exit;
            }
        }

        // 4. Конфликты.
        bool inversion = false;
        if (g >= 0 && cs.ConflictCount[g] > 0)
        {
            int local = cs.LocalBit[g];
            int firstConn = cs.Lane[g] - local;
            int end = cs.FirstConflict[g] + cs.ConflictCount[g];
            float yieldGap = w.Rules.YieldGapTime;
            int maxWait = w.Rules.MaxYieldWaitTicks;
            int starveWait = maxWait;
            int wait = v.WaitTicks[i];
            for (int k = cs.FirstConflict[g]; k < end; k++)
            {
                gw.TryGrantEntries++;
                ref readonly ConflictEntry ce = ref cs.Conflicts[k];
                int other = firstConn + ce.OtherLocal;
                int gOther = g - local + ce.OtherLocal;
                float sMine = ce.SMineCm * 0.01f;
                float sOther = ce.SOtherCm * 0.01f;
                bool merge = ce.Kind == ConflictKind.Merge;
                float dMe = dLine + sMine;
                float tMe = GateRules.Eta(dMe, vi, t.Accel, v0);
                float tMeClear = GateRules.Eta(dMe + li + GateRules.ClearMargin, vi, t.Accel, v0);
                int inLane = cs.InLane[gOther];

                // 4a. Внутри узла / допущенные: проезжают первыми независимо от приоритета.
                if (!merge)
                {
                    for (int x = occ.LaneTail[other]; x != -1; x = v.Ahead[x])
                    {
                        if (BlocksBox(w, x, v.S[x], sOther, tMe, inside: true))
                        {
                            return GateDeny.Box;
                        }
                    }

                    float lenOther = cs.InLength[gOther];
                    for (int x = occ.LaneHead[inLane]; x != -1 && GateRules.IsGranted(v, x); x = GateRules.BehindOn(v, x, inLane))
                    {
                        if (x != i && v.GrantConnector[x] == other && BlocksBox(w, x, v.S[x] - lenOther, sOther, tMe, inside: false))
                        {
                            return GateDeny.Box;
                        }
                    }
                }

                // 4b. Подъезжающие: первая недопущенная на входной полосе чужого connector'а.
                ConflictRel rel = onRedGrant ? ConflictRel.Yield : ce.Rel;   // VC3: на красном спецмашина сама ищет окно (gap acceptance) при любом Rel таблицы
                int y = occ.LaneHead[inLane];
                while (y != -1 && GateRules.IsGranted(v, y))
                {
                    y = GateRules.BehindOn(v, y, inLane);
                }

                if (y == -1 || y == i)
                {
                    continue;
                }

                int wy = v.WaitTicks[y];
                bool starving = fixedSignal && wy >= starveWait && (wy > wait || (wy == wait && y < i));
                if (rel == ConflictRel.Priority && !starving)
                {
                    continue; // у меня приоритет и Y не голодает: Y после моего допуска увидит меня допущенной и уступит
                }

                if (e.NextLane(y, inLane, 1, useRoute: true) != other)
                {
                    continue;
                }

                if (fixedSignal)
                {
                    byte sy = w.LaneSignals[other];
                    if (sy != (byte)LaneSignal.Green && sy != (byte)LaneSignal.Yellow)
                    {
                        continue;
                    }
                }

                // 4b-bis (правило A, 2.4a2): долго ждущая (WaitTicks >= MaxYieldWaitTicks, 20 с) голова Y, старше меня по (WaitTicks, -Index) и блокируемая
                // именно конфликтами (не выездом/сигналом/STOP), не должна голодать перед потоком, который проходит по приоритету или gap acceptance:
                // новые допуски через её точку ей отказывают (любое Rel), допущенные проезжают (4a), ящик очищается, Y проходит. Порядок по
                // (WaitTicks, -Index) строгий: отказывает только более младшему, поэтому взаимный отказ невозможен. На узлах не-Signal (RightHandRule, Yield, Stop, FlashYellow/Off) правило не действует (на RightHandRule (все
                // равнозначны) ломает T2, на Yield/Stop даёт -20% поездок на s10k).
                if (starving)
                {
                    // Отказ Y по выезду оправдывает пропуск потока (иначе перекрытый выезд запирал бы пересекающиеся потоки, don't-block-the-box),
                    // только если хвост выезда стоит. Если хвост движется, занятость выезда мимолётна, и без учёта очередь встречных «перепрыгивала»
                    // бы Y в каждый тик, когда выезд на миг занят.
                    byte dy = w.Gate.DenyReason[y];
                    bool excused = dy == (byte)GateDeny.Signal || dy == (byte)GateDeny.Stop || dy == (byte)GateDeny.Ped;
                    if (dy == (byte)GateDeny.Exit)
                    {
                        int exitLane = cs.OutLane[gOther];
                        int exitTail = occ.LaneTail[exitLane];
                        excused = exitTail != -1 && (w.Queue != null ? w.Queue.TailSpeed(exitLane) : v.Speed[exitTail]) < GateRules.CreepSpeed;
                    }

                    if (!excused)
                    {
                        gw.StarveDenials++;
                        return GateDeny.Yield;
                    }
                }

                if (rel == ConflictRel.Priority)
                {
                    continue;
                }

                float vy = v.Speed[y];
                float posY = v.S[y];
                if (w.Queue != null && w.Queue.IsQueued(y))
                {
                    w.Queue.SynthFirst(y, inLane, out posY, out vy);   // Q-meso: подъезжающая машина очереди - синтез «сейчас», а не позиция въезда
                }

                bool stationary = vy < GateRules.CreepSpeed;
                bool senior = v.WaitTicks[i] > v.WaitTicks[y] || (v.WaitTicks[i] == v.WaitTicks[y] && i < y);
                if (stationary && senior)
                {
                    continue; // (a) тай-брейк: стоящий, ждущий меньше меня, пропускает
                }

                if (stationary && (w.Gate.DenyReason[y] == (byte)GateDeny.Exit || w.Gate.DenyReason[y] == (byte)GateDeny.Ped))
                {
                    continue; // стоящий перед занятым выездом (don't-block-the-box) не угроза: он не въедет, пока его выезд не освободится
                }

                ref readonly VehicleTypeDesc ty = ref w.Types[v.Type[y]];
                float tY = GateRules.Eta(sOther + cs.InLength[gOther] - posY, vy, ty.Accel, v.DesiredSpeed[y]);
                if (rel == ConflictRel.Yield)
                {
                    if (tY >= tMeClear + yieldGap)
                    {
                        continue; // (b) gap acceptance
                    }
                }
                else if (!(tY < tMe || (tY == tMe && y < i)))
                {
                    continue; // ничья: я раньше
                }

                if (wait >= maxWait && tY >= tMeClear + GateRules.InversionGapTime)
                {
                    inversion = true; // (c)
                    continue;
                }

                if (wait >= 2 * maxWait)
                {
                    inversion = true; // (d)
                    continue;
                }

                return rel == ConflictRel.Yield ? GateDeny.Yield : GateDeny.Tie;
            }
        }

        // 5. Допуск.
        if (nd.Control == NodeControl.Signal)
        {
            RecordSignalGrant(w, gw, i, g, lane);
        }

        if (onRedGrant)
        {
            gw.OnCallRedGrants++;
        }

        v.GateState[i] = GateRules.Granted;
        v.GrantConnector[i] = conn;
        v.Ticket[i] = net.NodeTicket[node]++;
        AddReserved(net, gw.OutTouched, ref gw.OutTouchedCount, outLane, li);
        MarkBusy(gw.BusyTouched, ref gw.BusyTouchedCount, w.Gate.NodeBusy, cs, g);   // новый допуск: участник ConflictGap фазы 5
        gw.GateWaitTicksTotal += v.WaitTicks[i];
        v.WaitTicks[i] = 0;
        gw.Grants++;
        if (inversion)
        {
            gw.PriorityInversions++;
        }

        gw.Granted[gw.GrantedCount++] = i;   // Q-meso: OnGranted (голова -> кинематика, ведомый освобождается) - после задания, на главном потоке
        return GateDeny.None;
    }

    /// <summary>
    /// Метрики допуска на светофорном узле (2.5 S1, вне хэша): ожидание по манёвру (сумма, гистограмма, максимум левых),
    /// доля остановок на артерии, накопление <c>ConnWaitSum/ConnGrantCount</c> (§6.9). Вызывается до сброса <c>GateState</c>/<c>WaitTicks</c>.
    /// </summary>
    private static void RecordSignalGrant(World w, GateWorker gw, int i, int group, int lane)
    {
        VehicleStore v = w.Vehicles;
        int wait = v.WaitTicks[i];
        if (group >= 0)
        {
            int m = w.Connectors.Maneuver[group];
            gw.Signal.Add(m, wait);
            gw.SignalDirty = true;
            if (m >= (int)Maneuver.Left && wait > gw.LeftWaitMax)
            {
                gw.LeftWaitMax = wait;
            }

            int slot = w.Net.Nodes[w.Connectors.Node[group]].Signal;
            if ((uint)group < (uint)w.Gate.ConnGrantCount.Length && slot >= 0 && slot < w.Signals.Count && w.Signals.Mode[slot] == SignalMode.Adaptive)
            {
                w.Gate.ConnWaitSum[group] += wait;
                w.Gate.ConnGrantCount[group]++;
            }
        }

        int seg = w.Net.Lanes[lane].Segment;
        if (seg >= 0 && w.Net.Segments[seg].Kind == RoadKind.Arterial)
        {
            gw.ArterialGrants++;
            if ((v.GateState[i] & GateRules.StoppedBeforeSignal) != 0)
            {
                gw.ArterialStops++;
            }
        }
    }

    /// <summary>
    /// Машина <paramref name="x"/> (позиция <paramref name="sx"/> в координатах своего connector'а) ещё занимает точку и освободит её позже,
    /// чем я к ней подъеду. Стоящая машина <paramref name="inside"/> узла ждёт чего-то (билет, выезд) - время освобождения не определено
    /// (+inf); допущенная, но ещё стоящая у своей линии трогается свободно - её время считается ETA от нуля.
    /// </summary>
    private static bool BlocksBox(World w, int x, float sx, float sOther, float tMe, bool inside)
    {
        VehicleStore v = w.Vehicles;
        float lx = v.Length[x];
        if (sx - lx >= sOther + GateRules.ClearMargin)
        {
            return false;
        }

        float vx = v.Speed[x];
        if (inside && vx < GateRules.CreepSpeed)
        {
            return true;
        }

        ref readonly VehicleTypeDesc tx = ref w.Types[v.Type[x]];
        float tClear = GateRules.Eta(sOther + lx + GateRules.ClearMargin - sx, vx, tx.Accel, v.DesiredSpeed[x]);
        return tClear + GateRules.BoxClearMargin > tMe;
    }
}
