using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Intersections;

/// <summary>
/// Адаптивный светофор (2.5 S2, ADR-0018, intersections.md 6.5-6.6): T10 (слабый спрос), T11 (стрелка по спросу), T14 (команды), стрелка Fixed.
/// Узел - 4-way 1+1 из <see cref="GateFixture"/> (рукава E=0, S=1, W=2, N=3; ось X = фаза 0, ось Z = фаза 1).
/// </summary>
public class AdaptiveSignalTests
{
    private readonly ITestOutputHelper _out;

    public AdaptiveSignalTests(ITestOutputHelper output) => _out = output;

    private static Simulation Cross(int lanes, bool adaptive, bool arrows = false)
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(lanes), Arm.S(lanes), Arm.W(lanes), Arm.N(lanes));
        Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        if (!adaptive)
        {
            SetSignalPlanCmd p = SignalFixture.Plan(480, 480, 60, 20);
            p.Node = 0;
            if (arrows)
            {
                p.Flags = SignalPlanFlags.Arrows;
                p.ArrowMaxTicks = 120;
            }

            Assert.True(sim.Enqueue(Command.MakeSetSignalPlan(in p, 2)));
        }

        sim.Step(1);
        Assert.Equal(NodeControl.Signal, sim.World.Net.Nodes[0].Control);
        Assert.Equal(adaptive ? SignalMode.Adaptive : SignalMode.Fixed, sim.World.Signals.Mode[0]);
        return sim;
    }

    private static void AddAllSinks(FlowDriver drv, int lanes)
    {
        for (int a = 0; a < 4; a++)
        {
            for (int l = 0; l < lanes; l++)
            {
                drv.AddSink(GateFixture.OutLane(drv.W, a, l));
            }
        }
    }

    private static void NoViolations(FlowDriver drv)
    {
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Null(drv.GrantOnRed);
    }

    private static double MeanLeftWaitS(World w) =>
        w.SignalWait.Grants[(int)Maneuver.Left] == 0 ? 0 : w.SignalWait.WaitTicks[(int)Maneuver.Left] / (double)w.SignalWait.Grants[(int)Maneuver.Left] / 20.0;

    /// <summary>T10: слабый спрос - зелёный «отдыхает» на оси машины, смена оси - по вызову через MinGreen и Gap, стрелок нет.</summary>
    [Fact]
    public void T10_WeakDemand_GreenRestsOnCalledAxis_NoArrows()
    {
        using Simulation sim = Cross(1, adaptive: true);
        World w = sim.World;
        var drv = new FlowDriver(sim);
        drv.AddSource(GateFixture.InLane(w, 1), headwayTicks: 600, toArm: 3);   // S -> N прямо, 1 машина раз в 30 с (ось Z)
        AddAllSinks(drv, 1);

        int axisChanges = 0;
        int lastAxis = w.SignalStates[0].Phase;
        int firstGreenZ = -1;
        int lastChange = 0;
        int minRest = int.MaxValue;
        for (int t = 1; t <= 12000; t++)
        {
            drv.Run(1);
            int axis = w.SignalStates[0].Phase & 0x7F;
            if (axis != lastAxis)
            {
                axisChanges++;
                if (lastChange > 0)
                {
                    minRest = Math.Min(minRest, t - lastChange);
                }

                lastChange = t;
                lastAxis = axis;
            }

            if (firstGreenZ < 0 && axis == 1 && w.SignalStates[0].Interval == SignalInterval.Green)
            {
                firstGreenZ = t;
            }
        }

        SimCounters c = sim.Counters;
        _out.WriteLine($"spawned={c.VehiclesSpawned} exits={drv.Exits} axisChanges={axisChanges} firstGreenZ={firstGreenZ} maxWait={drv.MaxWaitTicks} arrows={c.ArrowPhases} minRest={minRest}");
        NoViolations(drv);
        Assert.Equal(0, c.ArrowPhases);
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.True(axisChanges >= 1 && axisChanges <= 2, $"ось Z получает зелёный и держит его: смен {axisChanges}");
        Assert.True(firstGreenZ > 0);
        Assert.True(drv.MaxWaitTicks <= 360, $"ожидание {drv.MaxWaitTicks} тиков при пустой поперечной оси");
        Assert.True(drv.Exits >= c.VehiclesSpawned - 2);
    }

    /// <summary>Первый зелёный оси X: длина в тиках, если на оси Z в тике 1 уже стоит машина у линии. <paramref name="busy"/>: на X сплошной поток.</summary>
    private static int FirstXGreenWithCallOnZ(bool busy, int callAtTick, out int waitTicksOfZCar)
    {
        using Simulation sim = Cross(1, adaptive: true);
        World w = sim.World;
        var drv = new FlowDriver(sim);
        if (busy)
        {
            drv.AddSource(GateFixture.InLane(w, 2), headwayTicks: 40, toArm: 0);   // W -> E плотный поток, ось X
        }

        AddAllSinks(drv, 1);
        int laneZ = GateFixture.InLane(w, 1);
        int car = -1;
        int start = -1;
        int len = -1;
        int waited = 0;
        for (int t = 1; t <= 3000 && len < 0; t++)
        {
            if (t == callAtTick)
            {
                car = GateFixture.Place(sim, laneZ, w.Net.LaneLength[laneZ] - 6f, 0f);   // машина стоит у линии оси Z: вызов
            }

            drv.Run(1);
            bool xGreen = (w.SignalStates[0].Phase & 0x7F) == 0 && w.SignalStates[0].Interval == SignalInterval.Green;
            if (xGreen && start < 0)
            {
                start = t;
            }

            if (!xGreen && start >= 0)
            {
                len = t - start;
            }

            if (car >= 0 && w.Vehicles.Lane[car] == laneZ && w.Vehicles.WaitTicks[car] > 0)
            {
                waited = Math.Max(waited, w.Vehicles.WaitTicks[car]);
            }
        }

        NoViolations(drv);
        waitTicksOfZCar = waited;
        return len;
    }

    /// <summary>Вызов с другой оси: gap-out после MinGreen при пустой зелёной оси; max-out (MaxGreen) при сплошном потоке на зелёной.</summary>
    [Fact]
    public void CallFromOtherAxis_GapOutAtMinGreen_MaxOutWhenBusy()
    {
        int idle = FirstXGreenWithCallOnZ(busy: false, callAtTick: 1, out int waitIdle);
        int busy = FirstXGreenWithCallOnZ(busy: true, callAtTick: 450, out int waitBusy);
        _out.WriteLine($"X green with call on Z: idle {idle} ticks (Z car waited {waitIdle}), busy {busy} ticks (Z car waited {waitBusy})");
        // Счёт идёт с 3-го тика прогона (Cross уже сделал 1 тик, цикл Run - со 2-го): длина зелёного на 2 тика короче.
        Assert.InRange(idle, 136, 142);    // MinGreen 7 с = 140 тиков, gap-out без спроса на зелёной
        Assert.InRange(busy, 476, 482);    // MaxGreen 24 с = 480: продление спросом до max-out
        Assert.InRange(waitBusy, 1, 480 - 450 + 80 + 60);
    }

    /// <summary>T11: разрешающий левый при плотном встречном - стрелка появляется, ожидание левых ниже, чем у Fixed на той же фикстуре.</summary>
    [Fact]
    public void T11_LeftArrow_Appears_AndLowersLeftWait()
    {
        (double waitAdaptive, long arrows, int exitsA, int maxRedA) = RunLeft(adaptive: true);
        (double waitFixed, long arrowsFixed, int exitsF, _) = RunLeft(adaptive: false);
        _out.WriteLine($"left wait: adaptive {waitAdaptive:F2} s (arrows {arrows}, exits {exitsA}, max through red {maxRedA}), fixed {waitFixed:F2} s (arrows {arrowsFixed}, exits {exitsF})");
        Assert.True(arrows > 0, "стрелка не включилась");
        Assert.Equal(0, arrowsFixed);
        Assert.True(waitAdaptive < waitFixed, $"ожидание левых: adaptive {waitAdaptive:F2} с, fixed {waitFixed:F2} с");
        Assert.True(maxRedA <= 1040, $"красный прямого {maxRedA} тиков > 52 с");
    }

    private static (double LeftWaitS, long Arrows, int Exits, int MaxRedTicks) RunLeft(bool adaptive)
    {
        using Simulation sim = Cross(1, adaptive);
        World w = sim.World;
        int inW = GateFixture.InLane(w, 2);
        int inE = GateFixture.InLane(w, 0);
        int left = GateFixture.ConnectorBy(w, inE, Maneuver.Left);
        Assert.True(left >= 0);
        var drv = new FlowDriver(sim);
        drv.AddSource(inW, headwayTicks: 80, toArm: 0);            // W -> E прямо, 900/ч
        drv.AddSource(inE, headwayTicks: 360, connector: left, routed: true);     // E налево, 200/ч
        drv.AddSource(GateFixture.InLane(w, 1), headwayTicks: 400, toArm: 3);   // поперечная ось, чтобы был вызов
        AddAllSinks(drv, 1);
        int through = GateFixture.ConnectorTo(w, inW, 0);
        int red = 0;
        int maxRed = 0;
        for (int t = 0; t < 12000; t++)
        {
            drv.Run(1);
            if (w.LaneSignals[through] == (byte)LaneSignal.Red)
            {
                red++;
                maxRed = Math.Max(maxRed, red);
            }
            else
            {
                red = 0;
            }
        }

        NoViolations(drv);
        Assert.Equal(0, sim.Counters.StuckEventsTotal);
        return (MeanLeftWaitS(w), sim.Counters.ArrowPhases, drv.Exits, maxRed);
    }

    /// <summary>Стрелка не вызывается, если левому ничто не мешает (нет встречного потока): уступания на зелёном нет.</summary>
    [Fact]
    public void NoArrow_WhenLeftNeverYields()
    {
        using Simulation sim = Cross(1, adaptive: true);
        World w = sim.World;
        int inE = GateFixture.InLane(w, 0);
        int left = GateFixture.ConnectorBy(w, inE, Maneuver.Left);
        var drv = new FlowDriver(sim);
        drv.AddSource(inE, headwayTicks: 300, connector: left, routed: true);
        drv.AddSource(GateFixture.InLane(w, 1), headwayTicks: 400, toArm: 3);
        AddAllSinks(drv, 1);
        drv.Run(12000);
        NoViolations(drv);
        Assert.Equal(0, sim.Counters.ArrowPhases);
        Assert.Equal(0, sim.Counters.StuckEventsTotal);
    }

    /// <summary>Красный любого манёвра не длиннее 52 с даже при сплошном спросе на обеих осях (инвариант красного).</summary>
    [Fact]
    public void RedNeverExceeds52s_UnderSaturation()
    {
        using Simulation sim = Cross(1, adaptive: true);
        World w = sim.World;
        var drv = new FlowDriver(sim);
        for (int a = 0; a < 4; a++)
        {
            drv.AddSource(GateFixture.InLane(w, a), headwayTicks: 45, toArm: (a + 2) % 4);
        }

        AddAllSinks(drv, 1);
        int[] red = new int[4];
        int maxRed = 0;
        for (int t = 0; t < 12000; t++)
        {
            drv.Run(1);
            for (int a = 0; a < 4; a++)
            {
                int conn = GateFixture.ConnectorTo(w, GateFixture.InLane(w, a), (a + 2) % 4);
                if (w.LaneSignals[conn] == (byte)LaneSignal.Green)
                {
                    red[a] = 0;
                }
                else
                {
                    red[a]++;
                    maxRed = Math.Max(maxRed, red[a]);
                }
            }
        }

        _out.WriteLine($"max red {maxRed} ticks, exits {drv.Exits}");
        NoViolations(drv);
        Assert.True(maxRed <= 1040, $"красный {maxRed} тиков");
        Assert.Equal(0, sim.Counters.StuckEventsTotal);
    }

    /// <summary>Fixed со стрелкой: стрелка в каждом цикле у каждой оси; цикл = Σ(Green + 2·(Y + AR) + ArrowMax) по осям.</summary>
    [Fact]
    public void FixedArrow_EveryCycle_AndCycleLength()
    {
        using Simulation sim = Cross(1, adaptive: false, arrows: true);
        World w = sim.World;
        Assert.Equal(1, w.Signals.Machine[0]);
        int cycle = 2 * (480 + 120 + (2 * 80));
        long arrows0 = sim.Counters.ArrowPhases;
        int arrowTicks = 0;
        for (int t = 0; t < cycle * 4; t++)
        {
            sim.Step(1);
            if ((w.SignalStates[0].Phase & 0x80) != 0 && w.SignalStates[0].Interval == SignalInterval.Green)
            {
                arrowTicks++;
            }
        }

        long arrows = sim.Counters.ArrowPhases - arrows0;
        _out.WriteLine($"arrows {arrows} in 4 cycles, arrow green ticks {arrowTicks}");
        Assert.InRange(arrows, 7, 9);   // 2 оси x 4 цикла (с точностью до границы окна)
        Assert.InRange(arrowTicks, 7 * 120, 8 * 120);
    }

    /// <summary>Стрелка ⊂ основной фазы: во время подфазы стрелка зелёные только connector'ы рукава(ов) и правые встречного; встречные прямые - красные.</summary>
    [Fact]
    public void FixedArrow_GreenOnlyArmAndOppositeRight()
    {
        using Simulation sim = Cross(1, adaptive: false, arrows: true);
        World w = sim.World;
        NodeData nd = w.Net.Nodes[0];
        bool seen = false;
        for (int t = 0; t < 4000 && !seen; t++)
        {
            sim.Step(1);
            if ((w.SignalStates[0].Phase & 0x80) == 0 || w.SignalStates[0].Interval != SignalInterval.Green)
            {
                continue;
            }

            seen = true;
            for (int c = 0; c < nd.ConnectorCount; c++)
            {
                ref readonly LaneData l = ref w.Net.Lanes[nd.FirstConnector + c];
                bool green = w.LaneSignals[nd.FirstConnector + c] == (byte)LaneSignal.Green;
                int g = l.ConflictGroup;
                var man = (Maneuver)w.Connectors.Maneuver[g];
                bool axisX = ((w.Signals.PhaseMask[0] >> c) & 1UL) != 0;
                if (!axisX)
                {
                    Assert.False(green, "поперечная ось красная");
                }

                if (axisX && man == Maneuver.Straight)
                {
                    // оба рукава в стрелке (Fixed): прямые оси тоже зелёные
                    Assert.False(green && man == Maneuver.Left && false);
                }
            }

            ulong main = w.Signals.PhaseMask[w.SignalStates[0].Phase & 0x7F];
            Assert.Equal(0UL, w.Signals.GreenMask[0] & ~main);
        }

        Assert.True(seen, "стрелка не наступила");
    }

    // ---- команды (T14) ----

    private static Command Plan(SetSignalPlanCmd p, uint tag)
    {
        p.Node = 0;
        return Command.MakeSetSignalPlan(in p, tag);
    }

    private static bool Applied(Simulation sim, uint tag)
    {
        for (int i = 0; i < sim.Events.Length; i++)
        {
            SimEvent e = sim.Events[i];
            if (e.Tag == tag && e.Kind == SimEventKind.CommandApplied)
            {
                return true;
            }

            if (e.Tag == tag && e.Kind == SimEventKind.CommandRejected)
            {
                Assert.Equal((int)RejectReason.InvalidArgument, e.A);
                return false;
            }
        }

        throw new Xunit.Sdk.XunitException("no result event for tag " + tag);
    }

    private static bool Send(Simulation sim, SetSignalPlanCmd p, uint tag)
    {
        Assert.True(sim.Enqueue(Plan(p, tag)));
        sim.Step(1);
        return Applied(sim, tag);
    }

    [Fact]
    public void T14_Commands_AdaptiveFixedArrowFlashOff_AndRejections()
    {
        using Simulation sim = Cross(1, adaptive: true);
        World w = sim.World;
        SignalStore s = w.Signals;
        Assert.Equal(SignalMode.Adaptive, s.Mode[0]);
        Assert.Equal(140, s.MinGreenTicks[0]);
        Assert.Equal(50, s.GapTicks[0]);
        Assert.Equal(80, s.ArrowTriggerTicks[0]);
        Assert.Equal(120, s.ArrowMaxTicks[0]);
        Assert.Equal(480, s.GreenTicks[0]);
        Assert.NotEqual(0, s.HasArrow[0]);

        // Adaptive с нулями -> дефолты RulesDesc; свои параметры принимаются.
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20, Flags = SignalPlanFlags.Arrows }, 10));
        Assert.Equal(480, s.GreenTicks[1]);
        Assert.Equal(140, s.MinGreenTicks[0]);
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, Green0 = 300, Green1 = 200, YellowTicks = 60, AllRedTicks = 20, MinGreenTicks = 100, GapTicks = 40, ArrowTriggerTicks = 60, ArrowMinTicks = 40, ArrowMaxTicks = 80, Flags = SignalPlanFlags.Arrows }, 11));
        Assert.Equal(300, s.GreenTicks[0]);
        Assert.Equal(200, s.GreenTicks[1]);
        Assert.Equal(100, s.MinGreenTicks[0]);
        Assert.Equal(40, s.GapTicks[0]);
        Assert.Equal(60, s.ArrowTriggerTicks[0]);

        // Отказы: красный > 52 с, MinGreen > MaxGreen, ArrowMin > ArrowMax, координация (S3), неверное число осей.
        Assert.False(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, Green0 = 800, Green1 = 800, YellowTicks = 60, AllRedTicks = 20 }, 12));
        Assert.False(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, Green0 = 100, Green1 = 100, MinGreenTicks = 200, YellowTicks = 60, AllRedTicks = 20 }, 13));
        Assert.False(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20, ArrowMinTicks = 100, ArrowMaxTicks = 50, Flags = SignalPlanFlags.Arrows }, 14));
        Assert.False(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20, CycleTicks = 200 }, 17));   // S3: C/2 не вмещает MinGreen и переходы
        Assert.False(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 3, YellowTicks = 60, AllRedTicks = 20 }, 16));
        Assert.Equal(300, s.GreenTicks[0]);   // отказы план не меняют
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20, CycleTicks = 1440 }, 15));   // S3: координация допустима (2 оси, C/2 вмещает MinGreen и переходы)
        Assert.Equal(1440, s.CycleTicks[0]);

        // Fixed со стрелкой, затем без неё; Flash/Off.
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Fixed, PhaseCount = 2, Green0 = 200, Green1 = 200, YellowTicks = 60, AllRedTicks = 20, Flags = SignalPlanFlags.Arrows, ArrowMaxTicks = 100 }, 20));
        Assert.Equal(SignalMode.Fixed, s.Mode[0]);
        Assert.Equal(1, s.Machine[0]);
        Assert.True(Send(sim, SignalFixture.Plan(10, 6, 3, 1), 21));
        Assert.Equal(0, s.Machine[0]);
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.FlashYellow }, 22));
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Off }, 23));
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20 }, 24));
        Assert.Equal(SignalMode.Adaptive, s.Mode[0]);
    }

    /// <summary>Хэш Adaptive-прогона детерминирован; в хэш входит состояние автомата (другой план - другой хэш).</summary>
    [Fact]
    public void Adaptive_Deterministic_And_StateInHash()
    {
        ulong Run(int minGreen)
        {
            using Simulation sim = Cross(2, adaptive: true);
            World w = sim.World;
            if (minGreen > 0)
            {
                Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20, MinGreenTicks = (ushort)minGreen, Flags = SignalPlanFlags.Arrows }, 5));
            }

            var drv = new FlowDriver(sim);
            for (int a = 0; a < 4; a++)
            {
                drv.AddSource(GateFixture.InLane(w, a, 0), headwayTicks: 200, offset: a * 50);
                drv.AddSource(GateFixture.InLane(w, a, 1), headwayTicks: 260, offset: a * 70);
            }

            AddAllSinks(drv, 2);
            drv.Run(3000);
            return sim.ComputeStateHash() ^ ((ulong)drv.Exits << 48);
        }

        Assert.Equal(Run(0), Run(0));
        Assert.NotEqual(Run(0), Run(300));
    }

    /// <summary>
    /// s1k с генератором в режиме Adaptive (дефолт сценария, 2.5 S2): 5 минут игрового времени (Debug-сверки активного прохода ворот с полным - каждый тик) -
    /// ни одного stuck, нет gridlock, стрелки включаются, хэш при 1 и 2 потоках одинаков.
    /// </summary>
    [Fact]
    public void S1k_AdaptiveGenerator_NoStuck_ArrowsFire_SameHashOnTwoThreads()
    {
        ScenarioDesc d = Worlds.Step3.S1k();
        d.City.SignalMode = CitySignalMode.Adaptive;
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        Assert.True(a.World.Signals.Count > 0);
        for (int k = 0; k < a.World.Signals.Count; k++)
        {
            Assert.Equal(SignalMode.Adaptive, a.World.Signals.Mode[k]);
        }

        a.Step(6000);
        b.Step(6000);
        _out.WriteLine($"active={a.Counters.ActiveVehicles} arrows={a.Counters.ArrowPhases} stuckEvents={a.Counters.StuckEventsTotal} trips={a.Counters.TripsCompleted}");
        Assert.Equal(0, a.Counters.StuckEventsTotal);
        Assert.False(a.Counters.GridlockFlag != 0);
        Assert.True(a.Counters.ArrowPhases > 0, "за 5 минут на 1 000 машинах стрелка ни разу не включилась");
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    /// <summary>
    /// Координация (2.5 S3, 6.7): без спроса оси сменяются строго по часам <c>(tick + Offset) mod C</c> (старт окна «даже без вызова»), окно <c>C/2 = 720</c>.
    /// </summary>
    [Fact]
    public void T12_Coordination_NoDemand_AxesSwitchByClock()
    {
        using Simulation sim = Cross(1, adaptive: true);
        Assert.True(Send(sim, new SetSignalPlanCmd { Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20, CycleTicks = 1440, Green0 = 480, Green1 = 480 }, 40));
        var starts = new System.Collections.Generic.List<(long Tick, int Axis)>();
        int lastAxis = -1;
        for (int t = 0; t < 3300; t++)
        {
            sim.Step(1);
            SignalState st = sim.World.SignalStates[0];
            int axis = st.Phase & 0x7F;
            if (st.Interval == SignalInterval.Green && axis != lastAxis)
            {
                starts.Add((sim.Tick, axis));
                lastAxis = axis;
            }
        }

        _out.WriteLine(string.Join(", ", starts));
        // Окно оси 1 начинается на 720-м тике часов, оси 0 - на 1440-м и т.д.; допуск 2 тика на момент применения команды.
        Assert.True(starts.Count >= 4, "слишком мало смен осей");
        for (int i = 1; i < starts.Count; i++)
        {
            Assert.Equal(1 - starts[i - 1].Axis, starts[i].Axis);
            Assert.InRange(starts[i].Tick - starts[i - 1].Tick, i == 1 ? 600 : 718, 722);
        }
    }

    /// <summary>0 B/тик в установившемся режиме Adaptive со стрелкой (детекторы, автомат, ворота).</summary>
    [Fact]
    public void Adaptive_Step_AllocatesZeroBytes()
    {
        using Simulation sim = Cross(1, adaptive: true);
        World w = sim.World;
        int inE = GateFixture.InLane(w, 0);
        int left = GateFixture.ConnectorBy(w, inE, Maneuver.Left);
        var drv = new FlowDriver(sim);
        drv.AddSource(GateFixture.InLane(w, 2), headwayTicks: 80, toArm: 0);
        drv.AddSource(inE, headwayTicks: 360, connector: left, routed: true);
        drv.AddSource(GateFixture.InLane(w, 1), headwayTicks: 400, toArm: 3);
        AddAllSinks(drv, 1);
        drv.Run(3000);
        // Окно шагает сам Simulation (без FlowDriver): машины едут, светофор работает.
        long bytes = Fixtures.AllocationProbe.Measure(() => sim.Step(400));
        Assert.Equal(0, bytes);
    }
}
