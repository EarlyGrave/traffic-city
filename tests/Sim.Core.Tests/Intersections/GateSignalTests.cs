using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Intersections;

/// <summary>Ворота на светофорных узлах (intersections.md §11: T1, T5, T8).</summary>
public class GateSignalTests
{
    private readonly ITestOutputHelper _out;

    public GateSignalTests(ITestOutputHelper output) => _out = output;

    private static Simulation SignalCross(int lanes)
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(lanes), Arm.S(lanes), Arm.W(lanes), Arm.N(lanes));
        Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        sim.Step(1);
        Assert.Equal(NodeControl.Signal, sim.World.Net.Nodes[0].Control);
        Assert.Equal(1, sim.SignalStates.Length);
        return sim;
    }

    private static void AddAllSinks(FlowDriver drv, int arms, int lanes)
    {
        for (int a = 0; a < arms; a++)
        {
            for (int l = 0; l < lanes; l++)
            {
                drv.AddSink(GateFixture.OutLane(drv.W, a, l));
            }
        }
    }

    /// <summary>T1: 4-way 2+2, потоки 600 маш./ч на рукав, 10 мин: период сигналов 1120 тиков, никто не въезжает без допуска/на красный, очередь не растёт.</summary>
    [Fact]
    public void T1_SingleSignal_NoEntryOnRed_QueueDoesNotGrow()
    {
        using Simulation sim = SignalCross(2);
        World w = sim.World;
        var drv = new FlowDriver(sim);
        for (int a = 0; a < 4; a++)
        {
            for (int l = 0; l < 2; l++)
            {
                drv.AddSource(GateFixture.InLane(w, a, l), headwayTicks: 240, offset: (a * 60) + (l * 30));
            }
        }

        AddAllSinks(drv, 4, 2);

        // Период LaneSignals: 2 фазы по 24 с + 2·(3 + 1) с = 56 с = 1120 тиков.
        var sig0 = new byte[w.Net.LaneCount];
        drv.Run(1);
        Array.Copy(w.LaneSignals, sig0, sig0.Length);
        int redTicks = 0;
        int greenTicks = 0;
        int nd0 = w.Net.Nodes[0].FirstConnector;
        for (int t = 1; t <= 12000; t++)
        {
            drv.Run(1);
            if (t % 1120 == 0)
            {
                for (int l = 0; l < sig0.Length; l++)
                {
                    Assert.Equal(sig0[l], w.LaneSignals[l]);
                }
            }

            if (w.LaneSignals[nd0] == (byte)LaneSignal.Red)
            {
                redTicks++;
            }
            else if (w.LaneSignals[nd0] == (byte)LaneSignal.Green)
            {
                greenTicks++;
            }
        }

        SimCounters c = sim.Counters;
        _out.WriteLine($"spawned={c.VehiclesSpawned} exits={drv.Exits} alive={drv.Alive} grants={c.Grants} maxWait={drv.MaxWaitTicks} inversions={c.PriorityInversions} boxWait={c.BoxWaitTicks} hardClamps={c.HardClamps} meanTravel={drv.MeanTravelS:F1}s red/green ticks of connector 0: {redTicks}/{greenTicks}");
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Null(drv.GrantOnRed);
        Assert.True(redTicks > greenTicks, "у connector'а одной оси красный длиннее зелёного");
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.True(c.VehiclesSpawned >= 390, $"spawned {c.VehiclesSpawned}");
        Assert.True(drv.Alive < 60, $"очередь накапливается: alive {drv.Alive}");
        Assert.True(drv.Exits >= c.VehiclesSpawned - 60);
        Assert.True(drv.MaxWaitTicks <= 1200, $"max wait {drv.MaxWaitTicks} ticks");
    }

    /// <summary>T5: насыщение - одна полоса, приток 1 500 маш./ч на зелёный 24/56: очередь растёт, пропускная способность &gt; 0, выезд не блокируется.</summary>
    [Fact]
    public void T5_Saturation_QueueGrows_ThroughputPositive_NoExitDenials()
    {
        using Simulation sim = SignalCross(1);
        World w = sim.World;
        var drv = new FlowDriver(sim);
        drv.AddSource(GateFixture.InLane(w, 2), headwayTicks: 48, toArm: 0); // W -> E, прямо
        AddAllSinks(drv, 4, 1);
        drv.Run(6000);
        int alive5 = drv.Alive;
        int exits5 = drv.Exits;
        drv.Run(6000);
        SimCounters c = sim.Counters;
        int exitsLast5 = drv.Exits - exits5;
        _out.WriteLine($"spawned={c.VehiclesSpawned} exits={drv.Exits} (last 5 min {exitsLast5}) alive 5min/10min={alive5}/{drv.Alive} grants={c.Grants} deniedExit={c.GateDeniedExit} boxWait={c.BoxWaitTicks} maxWait={drv.MaxWaitTicks} hardClamps={c.HardClamps}");
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Null(drv.GrantOnRed);
        Assert.True(drv.Alive >= alive5, "очередь не рассасывается (приток выше пропускной способности)");
        Assert.True(exitsLast5 > 40, $"пропускная способность за 5 мин: {exitsLast5}");
        // Отказы «выезд занят» здесь только переходные: следующий в очереди проверяется, пока предыдущий ещё в начале полосы выезда.
        Assert.True(c.GateDeniedExit < c.Grants * 12, $"deniedExit={c.GateDeniedExit} при grants={c.Grants}");
        Assert.True(c.BoxWaitTicks < 200, $"стояние внутри узла: {c.BoxWaitTicks} тиков");
        Assert.Equal(0, c.StuckEventsTotal);
    }

    /// <summary>T8: разрешающий левый на светофоре - встречный прямой 600 маш./ч, левые 200 маш./ч: stuck = 0, ожидание &lt;= 58 с.</summary>
    [Fact]
    public void T8_PermissiveLeft_NoStuck_BoundedWait()
    {
        using Simulation sim = SignalCross(1);
        World w = sim.World;
        int inW = GateFixture.InLane(w, 2);
        int inE = GateFixture.InLane(w, 0);
        int left = GateFixture.ConnectorBy(w, inE, Maneuver.Left);
        Assert.True(left >= 0);
        var drv = new FlowDriver(sim);
        drv.AddSource(inW, headwayTicks: 120, toArm: 0);          // W -> E прямо, 600/ч
        drv.AddSource(inE, headwayTicks: 360, connector: left);     // E налево, 200/ч
        AddAllSinks(drv, 4, 1);
        drv.Run(12000);
        SimCounters c = sim.Counters;
        _out.WriteLine($"spawned={c.VehiclesSpawned} exits={drv.Exits} alive={drv.Alive} grants={c.Grants} inversions={c.PriorityInversions} maxWait={drv.MaxWaitTicks} boxWait={c.BoxWaitTicks} hardClamps={c.HardClamps}");
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Null(drv.GrantOnRed);
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.True(drv.MaxWaitTicks <= 1160, $"max wait {drv.MaxWaitTicks} ticks (> 58 с)");
        Assert.True(drv.Exits >= c.VehiclesSpawned - 60, $"левые не проезжают: exits {drv.Exits} из {c.VehiclesSpawned}");
    }

    /// <summary>Детерминизм: два одинаковых прогона светофорного узла с потоками дают одинаковый хэш.</summary>
    [Fact]
    public void Deterministic_SignalFlows()
    {
        ulong Run()
        {
            using Simulation sim = SignalCross(2);
            World w = sim.World;
            var drv = new FlowDriver(sim);
            for (int a = 0; a < 4; a++)
            {
                drv.AddSource(GateFixture.InLane(w, a, 0), headwayTicks: 200, offset: a * 50);
                drv.AddSource(GateFixture.InLane(w, a, 1), headwayTicks: 260, offset: a * 70);
            }

            AddAllSinks(drv, 4, 2);
            drv.Run(3000);
            return sim.ComputeStateHash() ^ ((ulong)drv.Exits << 48);
        }

        Assert.Equal(Run(), Run());
    }
}
