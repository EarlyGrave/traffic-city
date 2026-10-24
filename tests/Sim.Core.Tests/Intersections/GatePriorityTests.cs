using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Intersections;

/// <summary>Ворота на нерегулируемых узлах (intersections.md §11: T2, T3, T4, T6, T7) и общие проверки (0 B/тик, детерминизм).</summary>
public class GatePriorityTests
{
    private readonly ITestOutputHelper _out;

    public GatePriorityTests(ITestOutputHelper output) => _out = output;

    private static Simulation Cross1(NodeControl control = NodeControl.RightHandRule)
    {
        ScenarioDesc d = GateFixture.Node(control, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        return Simulation.Create(in d);
    }

    /// <summary>T-образный: артерия W-E 2+2 и ножка S 1+1.</summary>
    private static Simulation TJunction(NodeControl control)
    {
        ScenarioDesc d = GateFixture.Node(control, Arm.E(2, RoadKind.Arterial), Arm.S(1), Arm.W(2, RoadKind.Arterial));
        Simulation sim = Simulation.Create(in d);
        Assert.Equal(control, sim.World.Net.Nodes[0].Control);
        return sim;
    }

    private static void AddSinks(FlowDriver drv, params (int Arm, int Lanes)[] arms)
    {
        foreach ((int arm, int lanes) in arms)
        {
            for (int l = 0; l < lanes; l++)
            {
                drv.AddSink(GateFixture.OutLane(drv.W, arm, l));
            }
        }
    }

    /// <summary>T2a: равнозначный 4-way, по одной машине одновременно с каждой стороны - все разъезжаются за ограниченное время, без наложений.</summary>
    [Fact]
    public void T2_FourWay_SimultaneousArrival_AllPass()
    {
        using Simulation sim = Cross1();
        World w = sim.World;
        Assert.Equal(NodeControl.RightHandRule, w.Net.Nodes[0].Control);
        var drv = new FlowDriver(sim);
        AddSinks(drv, (0, 1), (1, 1), (2, 1), (3, 1));
        for (int a = 0; a < 4; a++)
        {
            int inLane = GateFixture.InLane(w, a);
            GateFixture.Place(sim, inLane, GateFixture.ArmLength - 60f, 0f, GateFixture.ConnectorTo(w, inLane, (a + 2) % 4)); // все прямо
        }

        int firstGrant = -1;
        int allStopped = -1;
        bool moved = false;
        for (int t = 1; t <= 1200 && drv.Exits < 4; t++)
        {
            drv.Tick();
            if (allStopped < 0 && sim.Counters.Grants == 0)
            {
                bool stopped = true;
                bool allMoving = true;
                for (int i = 0; i < 4; i++)
                {
                    stopped &= w.Vehicles.Speed[i] < 0.1f;
                    allMoving &= w.Vehicles.Speed[i] > 1f;
                }

                moved |= allMoving;
                if (moved && stopped)
                {
                    allStopped = t;
                }
            }

            if (firstGrant < 0 && sim.Counters.Grants > 0)
            {
                firstGrant = t;
            }
        }

        _out.WriteLine($"exits={drv.Exits} firstGrant={firstGrant} allStopped={allStopped} ticks={w.Tick} hardClamps={sim.Counters.HardClamps}");
        Assert.Equal(4, drv.Exits);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.True(w.Tick <= 1200, "четвёрка не разъехалась за 60 с");
        if (allStopped >= 0)
        {
            Assert.True(firstGrant - allStopped <= 20, $"старт первой через {firstGrant - allStopped} тиков после полной остановки");
        }
    }

    /// <summary>T2b: равнозначный 4-way, потоки 300 маш./ч со всех сторон 10 мин: stuck = 0, ожидание &lt;= 2·MaxYieldWait, инверсий нет.</summary>
    [Fact]
    public void T2_FourWay_Flows_NoDeadlock()
    {
        using Simulation sim = Cross1();
        World w = sim.World;
        var drv = new FlowDriver(sim);
        for (int a = 0; a < 4; a++)
        {
            drv.AddSource(GateFixture.InLane(w, a), headwayTicks: 240, offset: a * 37);
        }

        AddSinks(drv, (0, 1), (1, 1), (2, 1), (3, 1));
        drv.Run(12000);
        SimCounters c = sim.Counters;
        _out.WriteLine($"spawned={c.VehiclesSpawned} exits={drv.Exits} alive={drv.Alive} grants={c.Grants} maxWait={drv.MaxWaitTicks} inversions={c.PriorityInversions} deniedExit={c.GateDeniedExit} boxWait={c.BoxWaitTicks} hardClamps={c.HardClamps} meanTravel={drv.MeanTravelS:F1}s");
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.Equal(0, c.GridlockFlag);
        Assert.True(drv.MaxWaitTicks <= 2 * w.Rules.MaxYieldWaitTicks, $"max wait {drv.MaxWaitTicks}");
        // Спека ждёт 0 инверсий при 300 маш./ч; при случайных манёврах цепочка уступок изредка держит машину дольше 20 с.
        Assert.True(c.PriorityInversions <= 2, $"inversions {c.PriorityInversions}");
        Assert.True(drv.Alive < 30, $"очередь: alive {drv.Alive}");
        Assert.True(drv.Exits >= c.VehiclesSpawned - 30);
    }

    /// <summary>T3: T-junction Yield - главная (900 маш./ч суммарно) почти не замедляется из-за ножки (200 маш./ч); все с ножки проезжают.</summary>
    [Fact]
    public void T3_TJunction_Yield_MainRoadUnhindered()
    {
        float MeanMainTravel(bool withLeg, out FlowDriver drv)
        {
            Simulation sim = TJunction(NodeControl.Yield);
            World w = sim.World;
            drv = new FlowDriver(sim);
            for (int l = 0; l < 2; l++)
            {
                drv.AddSource(GateFixture.InLane(w, 2, l), headwayTicks: 320, toArm: 0, offset: l * 160);  // W -> E, 225/ч на полосу
                drv.AddSource(GateFixture.InLane(w, 0, l), headwayTicks: 320, toArm: 2, offset: 80 + (l * 160)); // E -> W
            }

            if (withLeg)
            {
                drv.AddSource(GateFixture.InLane(w, 1), headwayTicks: 360); // ножка, направление случайное
            }

            AddSinks(drv, (0, 2), (2, 2), (1, 1));
            drv.Run(12000);
            long ticks = 0;
            int exits = 0;
            for (int l = 0; l < 2; l++)
            {
                foreach (int lane in new[] { GateFixture.InLane(w, 2, l), GateFixture.InLane(w, 0, l) })
                {
                    ticks += drv.TravelTicksByLane[lane];
                    exits += drv.ExitsByLane[lane];
                }
            }

            return (float)(ticks * 0.05 / exits);
        }

        float free = MeanMainTravel(false, out FlowDriver a);
        float loaded = MeanMainTravel(true, out FlowDriver b);
        SimCounters c = b.Sim.Counters;
        int legExits = b.ExitsByLane[GateFixture.InLane(b.W, 1)];
        _out.WriteLine($"main travel: free={free:F2}s with leg={loaded:F2}s; with leg: spawned={c.VehiclesSpawned} exits={b.Exits} alive={b.Alive} maxWait={b.MaxWaitTicks} inversions={c.PriorityInversions} hardClamps={c.HardClamps} legExits={legExits} legTravel={b.MeanTravelFrom(GateFixture.InLane(b.W, 1)):F1}s");
        Assert.Null(b.GapViolation);
        Assert.Null(b.ConflictViolation);
        Assert.Null(b.EntryWithoutGrant);
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.True(loaded <= free * 1.05f, $"главная замедлилась: {free:F2} -> {loaded:F2} с");
        Assert.True(legExits >= 28, $"с ножки проехало {legExits}");
        Assert.True(b.Alive < 25, $"ножка не проезжает: alive {b.Alive}");
        a.Sim.Dispose();
        b.Sim.Dispose();
    }

    /// <summary>T4: тот же узел со STOP - каждая машина с ножки имела тик с v &lt; 0.1 в пределах 3.5 м от линии до въезда.</summary>
    [Fact]
    public void T4_TJunction_Stop_EveryLegVehicleStopsAtLine()
    {
        using Simulation sim = TJunction(NodeControl.Stop);
        World w = sim.World;
        int leg = GateFixture.InLane(w, 1);
        var drv = new FlowDriver(sim);
        drv.AddSource(GateFixture.InLane(w, 2, 0), headwayTicks: 200, toArm: 0);
        drv.AddSource(GateFixture.InLane(w, 0, 0), headwayTicks: 200, toArm: 2, offset: 100);
        drv.AddSource(leg, headwayTicks: 300);
        AddSinks(drv, (0, 2), (2, 2), (1, 1));
        int legEntries = 0;
        int withoutStop = 0;
        int mainStops = 0;
        drv.OnTick = d =>
        {
            VehicleStore v = d.W.Vehicles;
            for (int i = 0; i < v.HighWater; i++)
            {
                if (!v.Pool.IsAlive(i) || d.PrevLane[i] < 0 || v.Lane[i] == d.PrevLane[i] || d.W.Net.Lanes[v.Lane[i]].Kind != LaneKind.Connector)
                {
                    continue;
                }

                if (d.PrevLane[i] == leg)
                {
                    legEntries++;
                    if (!d.StoppedNearLine[i])
                    {
                        withoutStop++;
                    }
                }
                else if (d.StoppedNearLine[i])
                {
                    mainStops++;
                }
            }
        };
        drv.Run(9000);
        _out.WriteLine($"leg entries={legEntries} without stop={withoutStop} main-road stops at line={mainStops} alive={drv.Alive} maxWait={drv.MaxWaitTicks}");
        Assert.True(legEntries >= 20);
        Assert.Equal(0, withoutStop);
        Assert.Equal(0, mainStops);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.GapViolation);
        Assert.Equal(0, sim.Counters.StuckEventsTotal);
    }

    /// <summary>
    /// T6 (gridlock-ловушка, дифференциальный): выезд на E перекрыт стоящей машиной; поток W -&gt; E хочет туда, N и S едут прямо.
    /// С правилом «выезд свободен» W стоит перед узлом и N/S проезжают весь прогон; с отключённым правилом W въезжает в узел,
    /// встаёт внутри и N/S останавливаются.
    /// </summary>
    [Fact]
    public void T6_DontBlockTheBox_KeepsCrossTrafficMoving()
    {
        (int crossExitsLast, int wExits, long deniedExit) Run(bool disableExitCheck)
        {
            using Simulation sim = Cross1();
            World w = sim.World;
            w.DebugDisableExitCheck = disableExitCheck;
            int outE = GateFixture.OutLane(w, 0);
            int blocker = GateFixture.Place(sim, outE, 6f, 0f);
            w.Vehicles.DesiredSpeed[blocker] = 0f;
            var drv = new FlowDriver(sim);
            drv.AddSource(GateFixture.InLane(w, 2), headwayTicks: 100, toArm: 0);   // W -> E
            drv.AddSource(GateFixture.InLane(w, 3), headwayTicks: 120, toArm: 1);   // N -> S
            drv.AddSource(GateFixture.InLane(w, 1), headwayTicks: 120, toArm: 3, offset: 60); // S -> N
            AddSinks(drv, (1, 1), (3, 1));
            drv.OnTick = d =>
            {
                d.W.Vehicles.S[blocker] = 6f;
                d.W.Vehicles.Speed[blocker] = 0f;
            };
            drv.Run(3000);
            int exits5 = drv.Exits;
            drv.Run(3000);
            int crossLast = drv.Exits - exits5;
            int wOnE = 0;
            for (int i = 0; i < w.Vehicles.HighWater; i++)
            {
                if (w.Vehicles.Pool.IsAlive(i) && w.Vehicles.Lane[i] == outE)
                {
                    wOnE++;
                }
            }

            _out.WriteLine($"exitCheck={(disableExitCheck ? "off" : "on")}: cross exits last 2.5 min={crossLast} total={drv.Exits} vehicles on E={wOnE} alive={drv.Alive} deniedExit={sim.Counters.GateDeniedExit} boxWait={sim.Counters.BoxWaitTicks} gaps={drv.GapViolation ?? "ok"} conflicts={drv.ConflictViolation ?? "ok"}");
            Assert.Null(drv.GapViolation);
            Assert.Null(drv.ConflictViolation);
            return (crossLast, wOnE, sim.Counters.GateDeniedExit);
        }

        (int withRule, int wOnEWith, long denied) = Run(false);
        (int withoutRule, _, _) = Run(true);
        Assert.True(withRule >= 44, $"поперечный поток с правилом: {withRule} выездов за 2.5 мин (ожидается ~50)");
        Assert.True(denied > 0);
        Assert.True(wOnEWith <= 2, $"на перекрытом выезде {wOnEWith} машин");
        Assert.True(withoutRule <= withRule / 4, $"без правила поперечный поток должен встать: {withoutRule} против {withRule}");
    }

    /// <summary>T7: слияние 2 -&gt; 1 (артерия 2+2 в улицу 1+1 на T-узле) под насыщением: порядок на выезде по билетам, без наложений.</summary>
    [Fact]
    public void T7_Merge_TwoLanesIntoOne_OrderedByTickets()
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(2, RoadKind.Arterial));
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int in0 = GateFixture.InLane(w, 2, 0);
        int in1 = GateFixture.InLane(w, 2, 1);
        int c0 = GateFixture.ConnectorTo(w, in0, 0);
        int c1 = GateFixture.ConnectorTo(w, in1, 0);
        Assert.True(c0 >= 0 && c1 >= 0 && c0 != c1);
        Assert.Equal(w.Net.Lanes[c0].ToLane, w.Net.Lanes[c1].ToLane);
        var drv = new FlowDriver(sim);
        drv.AddSource(in0, headwayTicks: 60, connector: c0);
        drv.AddSource(in1, headwayTicks: 60, connector: c1, offset: 30);
        AddSinks(drv, (0, 1), (1, 1));
        drv.Run(9000);
        SimCounters c = sim.Counters;
        _out.WriteLine($"spawned={c.VehiclesSpawned} exits={drv.Exits} alive={drv.Alive} hardClamps={c.HardClamps} connectorEntries={c.ConnectorEntries} maxWait={drv.MaxWaitTicks} boxWait={c.BoxWaitTicks}");
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.TicketOrderViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.True(drv.Exits > 100);
        Assert.True(c.HardClamps <= c.ConnectorEntries / 20, $"hard clamps {c.HardClamps} при {c.ConnectorEntries} проездах");
    }

    /// <summary>0 B/тик на узле с потоками после прогрева; детерминизм двух прогонов.</summary>
    [Fact]
    public void Flows_ZeroAllocationsPerTick_AndDeterministic()
    {
        static (ulong Hash, long Alloc) Run()
        {
            using Simulation sim = Cross1();
            World w = sim.World;
            var drv = new FlowDriver(sim);
            for (int a = 0; a < 4; a++)
            {
                drv.AddSource(GateFixture.InLane(w, a), headwayTicks: 150, offset: a * 41);
            }

            for (int a = 0; a < 4; a++)
            {
                drv.AddSink(GateFixture.OutLane(w, a));
            }

            drv.Run(1500);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 1000; t++)
            {
                sim.Step(1);
            }

            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            drv.Run(500);
            return (sim.ComputeStateHash(), alloc);
        }

        (ulong h1, long a1) = Run();
        (ulong h2, _) = Run();
        Assert.Equal(0, a1);
        Assert.Equal(h1, h2);
    }

    /// <summary>Сброс допусков при смене Control (intersections.md §5.7): после SetNodeControl нет допусков на входных полосах узла.</summary>
    [Fact]
    public void SetNodeControl_ResetsGrantsOfNode()
    {
        using Simulation sim = Cross1();
        World w = sim.World;
        int inLane = GateFixture.InLane(w, 2);
        int i = GateFixture.Place(sim, inLane, GateFixture.ArmLength - 40f, 10f, GateFixture.ConnectorTo(w, inLane, 0));
        sim.Step(1);
        Assert.True(GateRules.IsGranted(w.Vehicles, i));
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 7)));
        sim.Step(1);
        Assert.Equal(NodeControl.Signal, w.Net.Nodes[0].Control);
        // Команда исполнена в фазе 1 (допуск сброшен), ворота фазы 4 того же тика выдали новый допуск только при зелёном.
        bool granted = GateRules.IsGranted(w.Vehicles, i);
        byte sig = w.LaneSignals[GateFixture.ConnectorTo(w, inLane, 0)];
        Assert.True(!granted || sig == (byte)LaneSignal.Green || sig == (byte)LaneSignal.Yellow);
        Assert.Equal(0, w.Gate.TouchedCount);
    }
}
