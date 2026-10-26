using System.Numerics;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Автомат фаз светофоров (intersections.md 6.1, 6.5), часть фазы 4 тика: продвигает таймеры и пишет
/// <c>World.SignalStates</c> и <c>World.LaneSignals</c> connector'ов регулируемых узлов. Без аллокаций.
/// Прежний <c>Fixed</c> (без стрелки): при <c>TicksLeft == 0</c> интервал переключается (Green -> Yellow -> AllRed -> следующая фаза),
/// нулевые интервалы пропускаются; затем состояние публикуется (<c>TicksToChange = TicksLeft</c>) и <c>TicksLeft--</c>. Так интервал длиной L
/// виден ровно L тиков, а позиция в цикле на n-м вызове = (offset + n) mod Cycle.
/// 2.5 (ADR-0018): <c>Adaptive</c> и <c>Fixed</c> со стрелкой ведёт «автомат с подфазами» (<see cref="SignalStore.Machine"/>): детекторы
/// читают состояние ядра на конец прошлого тика (головы входных полос, <c>WaitTicks</c>, <c>DenyReason</c>, следующий connector), поэтому
/// фаза идёт до ворот; слоты - по возрастанию, однопоточно.
/// </summary>
internal static class SignalPhase
{
    /// <summary>Дальность детектора от стоп-линии, м (intersections.md 6.6).</summary>
    private const float DetectM = 60f;

    private const int MaxFirstSteps = 4;

    /// <summary>Анти-спилловер (6.8): столько тиков подряд весь спрос зелёной оси упёрт в стоящий выезд -> досрочный конец (3 с).</summary>
    private const int SpillOutTicks = 60;

    public static void Run(World w)
    {
        SignalStore s = w.Signals;
        NetworkStore net = w.Net;
        long arrows0 = s.ArrowEntries;
        long spills0 = s.SpillOutEntries;
        RuleEnv env = default;
        bool envReady = false;
        bool peds = w.Peds != null && PedSignals.Enabled;
        if (peds)
        {
            PedSignals.EnsureTables(w);
        }

        for (int slot = 0; slot < s.Count; slot++)
        {
            SignalMode mode = s.Mode[slot];
            if (s.Machine[slot] != 0 && (mode == SignalMode.Fixed || mode == SignalMode.Adaptive))
            {
                if (mode == SignalMode.Adaptive)
                {
                    if (!envReady)
                    {
                        env = new RuleEnv(net, w.Vehicles, w.Occupancy, RouteAccess.From(w));
                        envReady = true;
                    }

                    StepAdaptive(w, s, slot, in env, peds && PedSignals.Hold(s, slot));
                }
                else
                {
                    StepFixedArrow(s, slot);
                }

                Publish(w, s, slot);
                WriteLanesMachine(w, net, s, slot);
                if (peds)
                {
                    PedSignals.StepSlot(w, s, slot);
                }

                continue;
            }

            if (mode == SignalMode.Fixed)
            {
                if (s.TicksLeft[slot] == 0)
                {
                    Advance(s, slot);
                }

                w.SignalStates[slot] = new SignalState
                {
                    Node = s.Node[slot],
                    Phase = s.Phase[slot],
                    Interval = s.Interval[slot],
                    TicksToChange = s.TicksLeft[slot],
                };
                if (peds)
                {
                    PedSignals.StepSlot(w, s, slot);   // до уменьшения TicksLeft: первый тик зелёного виден как TicksLeft == Green
                }

                s.TicksLeft[slot]--;
            }
            else
            {
                w.SignalStates[slot] = new SignalState
                {
                    Node = s.Node[slot],
                    Phase = 0,
                    Interval = mode == SignalMode.Off ? SignalInterval.Off : SignalInterval.Yellow,
                    TicksToChange = 0,
                };
                if (peds)
                {
                    PedSignals.StepSlot(w, s, slot);   // не цветной режим: сигнал перехода сбрасывается (переход нерегулируемый)
                }
            }

            WriteLanes(w, net, s, slot, mode);
        }

        w.Counters.ArrowPhases += s.ArrowEntries - arrows0;
        w.Counters.SpillOuts += s.SpillOutEntries - spills0;
    }

    private static void Advance(SignalStore s, int slot)
    {
        int pc = s.PhaseCount[slot];
        // Green_k >= 1 гарантирует остановку цикла не позже чем за 3 шага.
        while (s.TicksLeft[slot] == 0)
        {
            switch (s.Interval[slot])
            {
                case SignalInterval.Green:
                    s.Interval[slot] = SignalInterval.Yellow;
                    s.TicksLeft[slot] = s.YellowTicks[slot];
                    break;
                case SignalInterval.Yellow:
                    s.Interval[slot] = SignalInterval.AllRed;
                    s.TicksLeft[slot] = s.AllRedTicks[slot];
                    break;
                default:
                    int next = s.Phase[slot] + 1;
                    s.Phase[slot] = (byte)(next >= pc ? 0 : next);
                    s.Interval[slot] = SignalInterval.Green;
                    s.TicksLeft[slot] = s.GreenTicks[(slot * SignalStore.MaxPhases) + s.Phase[slot]];
                    break;
            }
        }
    }

    private static void WriteLanes(World w, NetworkStore net, SignalStore s, int slot, SignalMode mode)
    {
        ref readonly NodeData nd = ref net.Nodes[s.Node[slot]];
        byte[] lanes = w.LaneSignals;
        int first = nd.FirstConnector;
        int count = nd.ConnectorCount;
        if (mode == SignalMode.Off)
        {
            for (int c = 0; c < count; c++)
            {
                lanes[first + c] = (byte)LaneSignal.None;
            }

            return;
        }

        if (mode == SignalMode.FlashYellow)
        {
            for (int c = 0; c < count; c++)
            {
                lanes[first + c] = (byte)LaneSignal.Yellow;
            }

            return;
        }

        SignalInterval iv = s.Interval[slot];
        byte open = iv == SignalInterval.Green ? (byte)LaneSignal.Green : iv == SignalInterval.Yellow ? (byte)LaneSignal.Yellow : (byte)LaneSignal.Red;
        ulong mask = s.PhaseMask[(slot * SignalStore.MaxPhases) + s.Phase[slot]];
        for (int c = 0; c < count; c++)
        {
            lanes[first + c] = c < SignalMasks.MaxMaskedConnectors && ((mask >> c) & 1UL) != 0 ? open : (byte)LaneSignal.Red;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Автомат с подфазами (Adaptive и Fixed со стрелкой), intersections.md 6.5
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Конец перехода: вход в подфазу <c>Next*</c>. Жёлтый/all-red позади.</summary>
    private static void EnterNext(SignalStore s, int slot)
    {
        s.Interval[slot] = SignalInterval.Green;
        s.Phase[slot] = s.NextAxis[slot];
        s.Sub[slot] = s.NextSub[slot];
        s.GreenMask[slot] = s.NextMask[slot];
        s.GreenElapsed[slot] = 0;
        s.SpillTicks[slot] = 0;
        s.TicksLeft[slot] = 0;
        bool adaptive = s.Mode[slot] == SignalMode.Adaptive;
        if (s.NextSub[slot] != 0)
        {
            ushort arms = s.NextArrowArms[slot];
            s.ArrowArms[slot] = arms;
            s.GapLeft[slot] = adaptive ? s.ArrowGapTicks[slot] : (ushort)0;
            s.ArrowEntries++;
            int b = slot * SignalStore.ArmCap;
            for (int a = 0; a < s.ArmCountOf[slot]; a++)
            {
                if (((arms >> a) & 1) != 0)
                {
                    s.ArmLeftYield[b + a] = 0;   // стрелка отработана: счёт уступания начнётся заново, иначе она вызывалась бы снова и снова
                    s.ArmLeftHead[b + a] = -1;
                }
            }
        }
        else
        {
            s.ArrowArms[slot] = 0;
            s.GapLeft[slot] = adaptive ? s.GapTicks[slot] : (ushort)0;
        }
    }

    /// <summary>Идти по жёлтому/all-red, пока интервал исчерпан; нулевые интервалы пропускаются, в конце - вход в следующую подфазу.</summary>
    internal static void AdvanceTransition(SignalStore s, int slot)
    {
        while (s.Interval[slot] != SignalInterval.Green && s.TicksLeft[slot] == 0)
        {
            if (s.Interval[slot] == SignalInterval.Yellow)
            {
                s.Interval[slot] = SignalInterval.AllRed;
                s.TicksLeft[slot] = s.AllRedTicks[slot];
            }
            else
            {
                EnterNext(s, slot);
            }
        }
    }

    /// <summary>
    /// Начало перехода к подфазе (<paramref name="axis"/>, <paramref name="sub"/>, <paramref name="arms"/>): жёлтый уходящим (<c>P \ Q</c>), all-red,
    /// затем <c>Q</c>; при <c>P \ Q = ∅</c> переход мгновенный (6.5).
    /// </summary>
    private static void Begin(SignalStore s, int slot, int axis, int sub, int arms)
    {
        ulong q = sub == 0 ? s.PhaseMask[(slot * SignalStore.MaxPhases) + axis] : s.ArrowMaskOf(slot, arms);
        s.NextAxis[slot] = (byte)axis;
        s.NextSub[slot] = (byte)sub;
        s.NextArrowArms[slot] = (ushort)arms;
        s.NextMask[slot] = q;
        if ((s.GreenMask[slot] & ~q) == 0UL)
        {
            EnterNext(s, slot);
            return;
        }

        s.Interval[slot] = SignalInterval.Yellow;
        s.TicksLeft[slot] = s.YellowTicks[slot];
        AdvanceTransition(s, slot);
    }

    /// <summary><c>Fixed</c> со стрелкой: Main_k(Green_k) -> Arrow(оба рукава, ArrowMax) -> Main_{k+1}; стрелка - каждый цикл у осей, где она возможна.</summary>
    internal static void StepFixedArrow(SignalStore s, int slot)
    {
        if (s.Interval[slot] != SignalInterval.Green && s.TicksLeft[slot] == 0)
        {
            AdvanceTransition(s, slot);
        }

        if (s.Interval[slot] != SignalInterval.Green)
        {
            return;
        }

        if (s.GreenElapsed[slot] < ushort.MaxValue)
        {
            s.GreenElapsed[slot]++;
        }

        int k = s.Phase[slot];
        int n = s.PhaseCount[slot];
        int elapsed = s.GreenElapsed[slot];
        if (s.Sub[slot] == 0)
        {
            if (elapsed >= s.GreenTicks[(slot * SignalStore.MaxPhases) + k])
            {
                int arms = s.ArmsOfAxis(slot, k) & s.HasArrow[slot];
                if (arms != 0 && s.ArrowMaxTicks[slot] > 0)
                {
                    Begin(s, slot, k, 1, arms);
                }
                else
                {
                    Begin(s, slot, k + 1 >= n ? 0 : k + 1, 0, 0);
                }
            }
        }
        else if (elapsed >= s.ArrowMaxTicks[slot])
        {
            Begin(s, slot, k + 1 >= n ? 0 : k + 1, 0, 0);
        }
    }

    private static void StepAdaptive(World w, SignalStore s, int slot, in RuleEnv e, bool hold)
    {
        Detect(w, s, slot, in e, out int call, out bool ext, out int leftWait, out int leftDemand, out int spillAxes);
        if (w.Peds != null && PedSignals.Enabled)
        {
            call |= PedSignals.CallMask(w, s, slot);   // вызов пешехода: ось перехода обслуживается и без машин (pedestrians.md §6)
        }

        if (s.Interval[slot] != SignalInterval.Green && s.TicksLeft[slot] == 0)
        {
            AdvanceTransition(s, slot);
        }

        if (s.Interval[slot] != SignalInterval.Green)
        {
            return;
        }

        if (s.GreenElapsed[slot] < ushort.MaxValue)
        {
            s.GreenElapsed[slot]++;
        }

        int k = s.Phase[slot];
        int n = s.PhaseCount[slot];
        int elapsed = s.GreenElapsed[slot];

        // Координация (6.7): ось-владелец окна и принудительный конец зелёного, начатого до границы окон (даже без вызова).
        // Зелёный, начатый после границы (ранний возврат), идёт по обычным правилам внутри окна чужой оси.
        int owner = k;
        int sinceStart = 0;
        int remaining = 0;
        bool coord = s.CycleTicks[slot] > 0 && n == 2;
        if (coord)
        {
            s.WindowClock(slot, w.Tick, out owner, out sinceStart, out remaining);
        }

        bool forceOff = coord && owner != k && elapsed > sinceStart && elapsed >= s.MinGreenTicks[slot];
        if (s.Sub[slot] == 0)
        {
            s.GapLeft[slot] = ext ? s.GapTicks[slot] : (ushort)(s.GapLeft[slot] > 0 ? s.GapLeft[slot] - 1 : 0);
            bool spill = (spillAxes & (1 << k)) != 0 && (call & (1 << k)) == 0;
            s.SpillTicks[slot] = spill ? (ushort)(s.SpillTicks[slot] < ushort.MaxValue ? s.SpillTicks[slot] + 1 : ushort.MaxValue) : (ushort)0;
            int otherCall = call & ~(1 << k);
            int myLeft = leftDemand & s.ArmsOfAxis(slot, k);
            bool others = otherCall != 0 || myLeft != 0;
            if (hold)
            {
                return;   // идут Walk/Clear оси: Main не завершается (эффективный MinGreen = старт + Walk + Clear)
            }

            if (forceOff)
            {
                Begin(s, slot, owner, 0, 0);
                return;
            }

            // Конец окна своей оси с левым спросом: стрелка успевает до границы (Main -> Arrow: Yellow + AllRed, затем ArrowMax).
            bool arrowBeforeEnd = coord && owner == k && myLeft != 0 && elapsed >= s.MinGreenTicks[slot]
                && remaining <= s.YellowTicks[slot] + s.AllRedTicks[slot] + s.ArrowMaxTicks[slot];
            bool spillEnd = s.SpillTicks[slot] >= SpillOutTicks;
            bool normalEnd = s.GapLeft[slot] == 0 || elapsed >= s.GreenTicks[(slot * SignalStore.MaxPhases) + k];
            if (arrowBeforeEnd || (others && elapsed >= s.MinGreenTicks[slot] && (normalEnd || spillEnd)))
            {
                if (myLeft != 0)
                {
                    Begin(s, slot, k, 1, myLeft);
                }
                else
                {
                    if (spillEnd && !normalEnd)
                    {
                        s.SpillOutEntries++;
                    }

                    Begin(s, slot, NextCalled(k, n, otherCall), 0, 0);
                }
            }
        }
        else
        {
            bool waiting = (leftWait & s.ArrowArms[slot]) != 0;
            s.GapLeft[slot] = waiting ? s.ArrowGapTicks[slot] : (ushort)(s.GapLeft[slot] > 0 ? s.GapLeft[slot] - 1 : 0);
            if (forceOff)
            {
                Begin(s, slot, owner, 0, 0);
                return;
            }

            if (elapsed >= s.ArrowMinTicks[slot] && (s.GapLeft[slot] == 0 || elapsed >= s.ArrowMaxTicks[slot]))
            {
                int otherCall = call & ~(1 << k);
                Begin(s, slot, otherCall != 0 ? NextCalled(k, n, otherCall) : k, 0, 0);
            }
        }
    }

    /// <summary>Ближайшая по кругу после <paramref name="k"/> ось с вызовом из <paramref name="callMask"/> (оси без вызова пропускаются); <paramref name="callMask"/> != 0.</summary>
    private static int NextCalled(int k, int axes, int callMask)
    {
        for (int step = 1; step < axes; step++)
        {
            int j = k + step;
            if (j >= axes)
            {
                j -= axes;
            }

            if (((callMask >> j) & 1) != 0)
            {
                return j;
            }
        }

        return k;
    }

    /// <summary>
    /// Детекторы (intersections.md 6.6) по входным полосам слота: читают головы полос, <c>WaitTicks</c>, <c>DenyReason</c> и следующий connector.
    /// <paramref name="call"/> - биты осей с вызовом, <paramref name="ext"/> - есть продлевающий спрос на зелёных полосах,
    /// <paramref name="leftWait"/> - биты рукавов, где первая недопущенная в дальности хочет налево/разворот, <paramref name="leftDemand"/> - биты рукавов
    /// со спросом на стрелку (уступание левого на зелёном не короче <c>ArrowTrigger</c>), <paramref name="spillAxes"/> - биты осей, у которых есть голова,
    /// упёршаяся в стоящий выезд (6.8; спил оси = бит есть и вызова оси нет). Обновляет <c>ArmLeftYield/ArmLeftHead</c>.
    /// </summary>
    private static void Detect(World w, SignalStore s, int slot, in RuleEnv e, out int call, out bool ext, out int leftWait, out int leftDemand, out int spillAxes)
    {
        VehicleStore v = w.Vehicles;
        LaneOccupancy occ = e.Occ;
        NetworkStore net = w.Net;
        byte[] deny = w.Gate.DenyReason;
        call = 0;
        ext = false;
        leftWait = 0;
        leftDemand = 0;
        spillAxes = 0;
        bool arrowsOn = (s.Flags[slot] & SignalPlanFlags.Arrows) != 0 && s.HasArrow[slot] != 0;
        System.Span<int> qual = stackalloc int[SignalStore.ArmCap];
        qual.Fill(-1);
        int baseLane = slot * SignalStore.MaxSigLanes;
        int n = s.SigLaneCount[slot];
        for (int k = 0; k < n; k++)
        {
            int lane = s.SigLane[baseLane + k];
            int axisBit = 1 << s.SigLaneAxis[baseLane + k];
            int f = occ.LaneHead[lane];
            int steps = 0;
            bool prefix = false;
            while (f != -1)
            {
                if (v.Lane[f] != lane)
                {
                    f = GateRules.BehindOn(v, f, lane);   // ghost уходящей машины
                    continue;
                }

                if (!GateRules.IsGranted(v, f))
                {
                    break;
                }

                if (++steps >= MaxFirstSteps)
                {
                    prefix = true;   // слишком длинный префикс допущенных: считаем, что за ним есть стоящий спрос
                    break;
                }

                f = GateRules.BehindOn(v, f, lane);
            }

            if (f == -1)
            {
                continue;
            }

            if (prefix)
            {
                call |= axisBit;
                int c0 = net.LaneNext[net.Lanes[lane].FirstNext];
                if (w.LaneSignals[c0] == (byte)LaneSignal.Green)
                {
                    ext = true;
                }

                continue;
            }

            if (net.LaneLength[lane] - v.S[f] > DetectM)
            {
                continue;
            }

            int conn = e.FirstHop(f, lane);
            if (conn < 0)
            {
                call |= axisBit;
                continue;
            }

            bool candidate = v.WaitTicks[f] > 0;
            byte dr = candidate ? deny[f] : (byte)GateDeny.None;
            if (dr == (byte)GateDeny.Exit && StandingExit(w, occ, net.Lanes[conn].ToLane))
            {
                spillAxes |= axisBit;   // 6.6/6.8: голова упёрлась в стоящий выезд - не вызывает и не продлевает зелёный
                continue;
            }

            call |= axisBit;
            bool green = w.LaneSignals[conn] == (byte)LaneSignal.Green;
            bool yieldDeny = dr == (byte)GateDeny.Yield || dr == (byte)GateDeny.Box || dr == (byte)GateDeny.Ped;   // Ped не продлевает зелёный, но считается уступанием (pedestrians.md §5.6)
            if (green && !yieldDeny)
            {
                ext = true;
            }

            if (s.SigLaneLeft[baseLane + k])
            {
                int g = net.Lanes[conn].ConflictGroup;
                var man = g >= 0 ? (Maneuver)w.Connectors.Maneuver[g] : Maneuver.Straight;
                if (man == Maneuver.Left || man == Maneuver.UTurn)
                {
                    int arm = s.SigLaneArm[baseLane + k];
                    leftWait |= 1 << arm;
                    if (arrowsOn && green && yieldDeny && qual[arm] < 0)
                    {
                        qual[arm] = f;
                    }
                }
            }
        }

        if (!arrowsOn)
        {
            return;
        }

        int b = slot * SignalStore.ArmCap;
        ref readonly NodeData nd = ref net.Nodes[s.Node[slot]];
        int trigger = s.ArrowTriggerTicks[slot];
        for (int a = 0; a < s.ArmCountOf[slot]; a++)
        {
            if (((s.HasArrow[slot] >> a) & 1) == 0)
            {
                continue;
            }

            int q = qual[a];
            if (q >= 0)
            {
                if (s.ArmLeftHead[b + a] != q)
                {
                    s.ArmLeftHead[b + a] = q;
                    s.ArmLeftYield[b + a] = 0;
                }

                if (s.ArmLeftYield[b + a] < ushort.MaxValue)
                {
                    s.ArmLeftYield[b + a]++;
                }
            }
            else
            {
                ulong lm = s.LeftMask[b + a];
                bool leftGreen = lm != 0 && w.LaneSignals[nd.FirstConnector + BitOperations.TrailingZeroCount(lm)] == (byte)LaneSignal.Green;
                if (leftGreen)
                {
                    s.ArmLeftYield[b + a] = 0;   // красный счёт не меняет; на зелёном без уступающей головы - сброс
                    s.ArmLeftHead[b + a] = -1;
                }
            }

            int head = s.ArmLeftHead[b + a];
            if (s.ArmLeftYield[b + a] >= trigger && head >= 0 && v.Lane[head] >= 0 && !GateRules.IsGranted(v, head))
            {
                leftDemand |= 1 << a;
            }
        }
    }

    /// <summary>«Стоящий выезд» (6.6): хвост выездной полосы стоит (<c>v &lt; v_creep</c>) - тот же признак, что в правиле 4b-bis.</summary>
    private static bool StandingExit(World w, LaneOccupancy occ, int exitLane)
    {
        int tail = occ.LaneTail[exitLane];
        return tail != -1 && (w.Queue != null ? w.Queue.TailSpeed(exitLane) : w.Vehicles.Speed[tail]) < GateRules.CreepSpeed;
    }

    private static void Publish(World w, SignalStore s, int slot)
    {
        SignalInterval iv = s.Interval[slot];
        ushort ttc;
        if (iv == SignalInterval.Green)
        {
            ttc = GreenRemainder(s, slot);
        }
        else
        {
            ttc = s.TicksLeft[slot];
            s.TicksLeft[slot]--;
        }

        w.SignalStates[slot] = new SignalState
        {
            Node = s.Node[slot],
            Phase = (byte)(s.Phase[slot] | (s.Sub[slot] << 7)),
            Interval = iv,
            TicksToChange = ttc,
        };
    }

    /// <summary>
    /// <c>TicksToChange</c> зелёного машины: <c>Fixed</c> со стрелкой - точный остаток (как у прежнего Fixed: первый тик = длительность);
    /// <c>Adaptive</c> - гарантированный остаток <c>max(Min - elapsed, GapLeft)</c> (продление не обещано, ворота консервативны), не больше остатка до max-out.
    /// </summary>
    private static ushort GreenRemainder(SignalStore s, int slot)
    {
        int elapsed = s.GreenElapsed[slot];
        int k = s.Phase[slot];
        bool arrow = s.Sub[slot] != 0;
        if (s.Mode[slot] == SignalMode.Fixed)
        {
            int total = arrow ? s.ArrowMaxTicks[slot] : s.GreenTicks[(slot * SignalStore.MaxPhases) + k];
            return (ushort)System.Math.Max(1, total - elapsed + 1);
        }

        int min = arrow ? s.ArrowMinTicks[slot] : s.MinGreenTicks[slot];
        int max = arrow ? s.ArrowMaxTicks[slot] : s.GreenTicks[(slot * SignalStore.MaxPhases) + k];
        int g = System.Math.Max(min - elapsed, s.GapLeft[slot]);
        int cap = max - elapsed;
        if (cap > 0 && g > cap)
        {
            g = cap;
        }

        return (ushort)System.Math.Max(1, g);
    }

    /// <summary>Цвета connector'ов по маскам перехода (6.5): Green - в <c>P</c> (в жёлтом/all-red - только пересечение <c>P ∩ Q</c>), Yellow - <c>P \ Q</c> в жёлтом.</summary>
    private static void WriteLanesMachine(World w, NetworkStore net, SignalStore s, int slot)
    {
        ref readonly NodeData nd = ref net.Nodes[s.Node[slot]];
        byte[] lanes = w.LaneSignals;
        int first = nd.FirstConnector;
        int count = nd.ConnectorCount;
        ulong p = s.GreenMask[slot];
        ulong q = s.NextMask[slot];
        SignalInterval iv = s.Interval[slot];
        for (int c = 0; c < count; c++)
        {
            byte color = (byte)LaneSignal.Red;
            if (c < SignalMasks.MaxMaskedConnectors && ((p >> c) & 1UL) != 0)
            {
                bool inQ = ((q >> c) & 1UL) != 0;
                if (iv == SignalInterval.Green || inQ)
                {
                    color = (byte)LaneSignal.Green;
                }
                else if (iv == SignalInterval.Yellow)
                {
                    color = (byte)LaneSignal.Yellow;
                }
            }

            lanes[first + c] = color;
        }
    }
}
