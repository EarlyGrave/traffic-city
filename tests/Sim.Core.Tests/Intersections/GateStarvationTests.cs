using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Intersections;

/// <summary>Голодание разрешающего левого на светофоре перед насыщенным встречным потоком (intersections.md §5.4 п. 4b-bis, 2.4a2).</summary>
public class GateStarvationTests
{
    private readonly ITestOutputHelper _out;

    public GateStarvationTests(ITestOutputHelper output) => _out = output;

    private static Simulation SignalCross()
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        sim.Step(1);
        return sim;
    }

    private static void AddAllSinks(FlowDriver drv)
    {
        for (int a = 0; a < 4; a++)
        {
            drv.AddSink(GateFixture.OutLane(drv.W, a));
        }
    }

    /// <summary>Встречный прямой поток насыщен (интервал 1.25 с), левые редкие: ни один левый не ждёт дольше 2*max_yield_wait + запас на ящик.</summary>
    [Fact]
    public void SaturatedOncoming_LeftTurnPassesWithinBound()
    {
        using Simulation sim = SignalCross();
        World w = sim.World;
        int inW = GateFixture.InLane(w, 2);
        int inE = GateFixture.InLane(w, 0);
        int left = GateFixture.ConnectorBy(w, inE, Maneuver.Left);
        Assert.True(left >= 0);
        var drv = new FlowDriver(sim);
        drv.AddSource(inW, headwayTicks: 25, toArm: 0, routed: true);
        drv.AddSource(inE, headwayTicks: 300, connector: left, routed: true);
        AddAllSinks(drv);
        drv.Run(24000);
        int leftExits = drv.ExitsPerSink[w.Net.Lanes[left].ToLane];
        SimCounters c = sim.Counters;
        _out.WriteLine($"src={drv.Sources[0].Spawned}/{drv.Sources[1].Spawned} deferred={drv.Sources[0].Deferred}/{drv.Sources[1].Deferred} sinks={drv.ExitsPerSink[GateFixture.OutLane(w, 0)]},{drv.ExitsPerSink[GateFixture.OutLane(w, 1)]},{drv.ExitsPerSink[GateFixture.OutLane(w, 2)]},{drv.ExitsPerSink[GateFixture.OutLane(w, 3)]} spawned={c.VehiclesSpawned} exits={drv.Exits} leftExits={leftExits} maxWait={drv.MaxWaitTicks} inversions={c.PriorityInversions} stuck={c.StuckEventsTotal}");
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Null(drv.GrantOnRed);
        Assert.Equal(0, c.StuckEventsTotal);
        int maxWait = w.Rules.MaxYieldWaitTicks;
        Assert.True(drv.MaxWaitTicks <= 2 * maxWait, $"левый ждал {drv.MaxWaitTicks} тиков (> 40 с, intersections.md §5.5)");
        Assert.True(leftExits >= 40, $"левых проехало {leftExits}");
    }

    /// <summary>Два долго ждущих встречных левых (E и W), оба напротив насыщенных прямых потоков: взаимной блокировки нет, оба потока левых проходят.</summary>
    [Fact]
    public void TwoLongWaitingOpposingLefts_NoMutualBlock()
    {
        using Simulation sim = SignalCross();
        World w = sim.World;
        int inW = GateFixture.InLane(w, 2);
        int inE = GateFixture.InLane(w, 0);
        int leftE = GateFixture.ConnectorBy(w, inE, Maneuver.Left);
        int leftW = GateFixture.ConnectorBy(w, inW, Maneuver.Left);
        Assert.True(leftE >= 0 && leftW >= 0);
        var drv = new FlowDriver(sim);
        drv.AddSource(inW, headwayTicks: 280, connector: leftW, offset: 13, routed: true);
        drv.AddSource(inE, headwayTicks: 280, connector: leftE, offset: 101, routed: true);
        drv.AddSource(inW, headwayTicks: 30, toArm: 0, routed: true);
        drv.AddSource(inE, headwayTicks: 30, toArm: 2, offset: 7, routed: true);
        AddAllSinks(drv);
        drv.Run(24000);
        SimCounters c = sim.Counters;
        int toS = drv.ExitsPerSink[w.Net.Lanes[leftW].ToLane];
        int toN = drv.ExitsPerSink[w.Net.Lanes[leftE].ToLane];
        _out.WriteLine($"exits={drv.Exits} toS={toS} toN={toN} maxWait={drv.MaxWaitTicks} inversions={c.PriorityInversions} stuck={c.StuckEventsTotal}");
        Assert.Null(drv.GapViolation);
        Assert.Null(drv.ConflictViolation);
        Assert.Null(drv.EntryWithoutGrant);
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.True(drv.MaxWaitTicks <= 2 * w.Rules.MaxYieldWaitTicks + 400, $"max wait {drv.MaxWaitTicks}");
        Assert.True(toS >= 30 && toN >= 30, $"левые: toS={toS} toN={toN}");
    }

    [Fact]
    public void Deterministic_Starvation()
    {
        ulong Run()
        {
            using Simulation sim = SignalCross();
            World w = sim.World;
            int inW = GateFixture.InLane(w, 2);
            int inE = GateFixture.InLane(w, 0);
            var drv = new FlowDriver(sim);
            drv.AddSource(inW, headwayTicks: 30, toArm: 0, routed: true);
            drv.AddSource(inE, headwayTicks: 280, connector: GateFixture.ConnectorBy(w, inE, Maneuver.Left), routed: true);
            AddAllSinks(drv);
            drv.Run(4000);
            return sim.ComputeStateHash() ^ ((ulong)drv.Exits << 48);
        }

        Assert.Equal(Run(), Run());
    }
}
