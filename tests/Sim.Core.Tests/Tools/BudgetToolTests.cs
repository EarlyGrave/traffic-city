using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.City;
using Sim.Core.Util;

namespace Sim.Core.Tests.Tools;

/// <summary>Цены инструментов (E4, city-economy.md §8, §15 «Инструменты»): списание, возврат сноса, <c>InsufficientFunds</c> в превью и применении, окно undo, подделка бита <c>Inverse</c>.</summary>
public sealed class BudgetToolTests
{
    private static readonly ToolsDesc _costs = new()
    {
        HasWorldBounds = true,
        BoundsMinX = -1e6f,
        BoundsMinZ = -1e6f,
        BoundsMaxX = 1e6f,
        BoundsMaxZ = 1e6f,
        Costs = new ToolCostsDesc { RoadPerMLaneStreet = 10, RoadPerMLaneArterial = 20, CurveFactorPct = 100, NodeSignal = 500, ArmSign = 50, TurnBan = 100, DemolishRefundPct = 50 },
    };

    private static Simulation Make(bool budget = true)
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Economy.CityBudget = budget;
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        sim.SetToolsConfig(_costs);
        return sim;
    }

    private static long Balance(Simulation sim) => sim.World.City!.Ledger.City;

    private static Command Road(Simulation sim, float len = 300f, uint tag = 1)
    {
        World w = sim.World;
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 a = w.Net.Nodes[west].Position;
        return Command.MakeBuildRoad(ToolsFixture.Road(a, new Vec2(a.X - len, a.Z)), tag);
    }

    private static int LastSegment(Simulation sim)
    {
        int seg = -1;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.SegmentCreated)
            {
                seg = e.A;
            }
        }

        return seg;
    }

    private static Command[] InverseOf(Simulation sim, uint tag)
    {
        foreach (InverseRecord r in sim.Inverses)
        {
            if (r.Tag == tag)
            {
                Assert.Equal(0, r.Flags);
                return sim.InverseCommands.Slice(r.First, r.Count).ToArray();
            }
        }

        throw new InvalidOperationException("нет обратной записи");
    }

    private static PreviewResult Preview(Simulation sim, in Command cmd, out RejectReason verdict)
    {
        verdict = sim.Preview(in cmd, out PreviewResult r, new PreviewIssue[16]);
        return r;
    }

    [Fact]
    public void BuildRoad_ChargesPreviewCost_AndRemoveRoadRefundsShare()
    {
        using Simulation sim = Make();
        long before = Balance(sim);
        Command build = Road(sim);
        PreviewResult pv = Preview(sim, in build, out RejectReason v);
        Assert.Equal(RejectReason.None, v);
        Assert.True(pv.Cost > 0);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, build));
        Assert.Equal(before - pv.Cost, Balance(sim));
        Assert.Equal(pv.Cost, sim.World.City!.Ledger.FlowTotal[(int)FlowKind.Construction]);
        int seg = LastSegment(sim);

        // Окно undo прошло: снос идёт по таблице (50% цены).
        sim.Step((10 * Simulation.TicksPerSecond) + 5);
        long mid = Balance(sim);
        var rem = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 2);
        PreviewResult pr = Preview(sim, in rem, out v);
        Assert.Equal(RejectReason.None, v);
        Assert.Equal(-(pv.Cost * 50 / 100), pr.Cost);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, rem));
        Assert.Equal(mid - pr.Cost, Balance(sim));
        Assert.Equal(-pr.Cost, sim.World.City.Ledger.FlowTotal[(int)FlowKind.Refund]);
    }

    [Fact]
    public void NoBudget_ToolsStayFree_PreviewStillShowsTable()
    {
        using Simulation sim = Make(budget: false);
        Assert.Null(sim.World.City!.Budget);
        long before = Balance(sim);
        Command build = Road(sim);
        PreviewResult pv = Preview(sim, in build, out _);
        Assert.True(pv.Cost > 0);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, build));
        Assert.Equal(before, Balance(sim));
        Assert.Equal(0, sim.Counters.InsufficientFunds);
    }

    [Fact]
    public void InsufficientFunds_PreviewEqualsApply_AndNothingChanges()
    {
        using Simulation sim = Make();
        ToolsDesc huge = _costs;
        huge.Costs.RoadPerMLaneStreet = 100_000_000;
        sim.SetToolsConfig(huge);
        long before = Balance(sim);
        Command build = Road(sim);
        PreviewResult pv = Preview(sim, in build, out RejectReason v);
        Assert.Equal(RejectReason.InsufficientFunds, v);
        Assert.True(pv.Cost > 0);
        int segs = sim.World.Net.SegmentCount;
        Assert.Equal(RejectReason.InsufficientFunds, ToolsFixture.Run(sim, build));
        Assert.Equal(before, Balance(sim));
        Assert.Equal(segs, sim.World.Net.SegmentCount);
        Assert.Equal(1, sim.Counters.InsufficientFunds);

        // Граница: ровно по кредитному лимиту проходит, на единицу дороже - нет.
        CityBudget b = sim.World.City!.Budget!;
        long room = before + b.CreditLimit(in sim.World.City.Cfg.Economy);
        Assert.True(room > 0);
        long perM = 1;
        ToolsDesc cheap = _costs;
        cheap.Costs.RoadPerMLaneStreet = perM;
        sim.SetToolsConfig(cheap);
        PreviewResult one = Preview(sim, in build, out v);
        Assert.Equal(RejectReason.None, v);
        long scale = (room / one.Cost) + 1;
        cheap.Costs.RoadPerMLaneStreet = scale;
        sim.SetToolsConfig(cheap);
        PreviewResult over = Preview(sim, in build, out v);
        Assert.True(over.Cost > room);
        Assert.Equal(RejectReason.InsufficientFunds, v);
    }

    [Fact]
    public void Undo_WithinWindow_RefundsFully_AfterWindow_UsesTable()
    {
        using Simulation sim = Make();
        long start = Balance(sim);
        Command build = Road(sim, tag: 11);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, build));
        long afterBuild = Balance(sim);
        long cost = start - afterBuild;
        Assert.True(cost > 0);
        Command[] inv = InverseOf(sim, 11);
        Assert.NotEmpty(inv);
        Assert.All(inv, c => Assert.Equal(CommandFlags.Inverse, c.Flags & CommandFlags.Inverse));
        Assert.Contains(sim.World.City!.Budget!.UndoCredits, u => u.TargetGeneration != 0 && u.Amount == cost);

        // Внутри окна: превью обратной показывает возврат 100%, применение возвращает всё до единицы и гасит запись.
        sim.Step(60);
        Command first = inv[0];
        first.Tag = 12;
        PreviewResult pr = Preview(sim, in first, out RejectReason v);
        Assert.Equal(RejectReason.None, v);
        Assert.Equal(-cost, pr.Cost);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, first));
        Assert.Equal(start, Balance(sim));
        Assert.DoesNotContain(sim.World.City.Budget!.UndoCredits, u => u.TargetGeneration != 0);
    }

    [Fact]
    public void Undo_AfterWindow_PaysTable_AndExpiredCreditsArePurged()
    {
        using Simulation sim = Make();
        long start = Balance(sim);
        Command build = Road(sim, tag: 21);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, build));
        long cost = start - Balance(sim);
        Command inverse = InverseOf(sim, 21)[0];
        sim.Step((10 * Simulation.TicksPerSecond) + 2);
        Assert.DoesNotContain(sim.World.City!.Budget!.UndoCredits, u => u.TargetGeneration != 0);   // просроченное погашено до сейва
        inverse.Tag = 22;
        long before = Balance(sim);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, inverse));
        Assert.Equal(before + (cost * 50 / 100), Balance(sim));
    }

    [Fact]
    public void ForgedInverseBit_WithoutCredit_PaysOrdinaryPrice()
    {
        using Simulation sim = Make();
        Command build = Road(sim, tag: 31);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, build));
        int seg = LastSegment(sim);
        sim.Step((10 * Simulation.TicksPerSecond) + 2);   // кредит просрочен
        long cost = sim.World.City!.Ledger.FlowTotal[(int)FlowKind.Construction];
        long before = Balance(sim);
        Command rem = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 32);
        rem.Flags = CommandFlags.Inverse;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, rem));
        Assert.Equal(before + (cost * 50 / 100), Balance(sim));
    }

    [Fact]
    public void UndoOfDemolish_ChargesBackTheRefund()
    {
        using Simulation sim = Make();
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(sim, tag: 41)));
        int seg = LastSegment(sim);
        sim.Step((10 * Simulation.TicksPerSecond) + 2);
        long before = Balance(sim);
        Command rem = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 42);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, rem));
        long refunded = Balance(sim) - before;
        Assert.True(refunded > 0);
        Command[] inv = InverseOf(sim, 42);
        Assert.Equal(CommandKind.BuildRoad, inv[0].Kind);
        inv[0].Tag = 43;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, inv[0]));
        Assert.Equal(before, Balance(sim));
    }

    [Fact]
    public void NewForwardGroup_ClearsOlderCredits()
    {
        using Simulation sim = Make();
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(sim, 200f, tag: 51)));
        Command oldInverse = InverseOf(sim, 51)[0];
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(sim, 100f, tag: 52)));   // другой тик: возврат 100% - только за последнее действие
        int live = 0;
        foreach (UndoCredit u in sim.World.City!.Budget!.UndoCredits)
        {
            live += u.TargetGeneration != 0 ? 1 : 0;
        }

        Assert.Equal(1, live);
        oldInverse.Tag = 53;
        long before = Balance(sim);
        PreviewResult pr = Preview(sim, in oldInverse, out _);
        Assert.True(pr.Cost < 0);
        Assert.True(-pr.Cost < sim.World.City.Ledger.FlowTotal[(int)FlowKind.Construction]);   // по таблице, не 100%
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, oldInverse));
        Assert.Equal(before - pr.Cost, Balance(sim));
    }

    [Fact]
    public void SignalAndSign_AreCharged_AndUndoRefunds()
    {
        using Simulation sim = Make();
        int node = ToolsFixture.FindNode(sim, 3, NodeControl.RightHandRule);
        if (node < 0)
        {
            node = ToolsFixture.FindNode(sim, 4, NodeControl.RightHandRule);
        }

        Assert.True(node >= 0);
        long start = Balance(sim);
        var cmd = Command.MakeSetNodeControl(new SetNodeControlCmd { Node = node, NodeGen = sim.World.Net.Nodes[node].Generation, Control = NodeControl.Signal }, 61);
        PreviewResult pv = Preview(sim, in cmd, out RejectReason v);
        Assert.Equal(RejectReason.None, v);
        Assert.Equal(500, pv.Cost);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
        Assert.Equal(start - 500, Balance(sim));
        Command inv = InverseOf(sim, 61)[0];
        Assert.Equal(CommandKind.SetNodeControl, inv.Kind);
        inv.Tag = 62;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, inv));
        Assert.Equal(start, Balance(sim));
    }

    [Fact]
    public void ToolCharges_AreDeterministic_AcrossSaveLoad()
    {
        using Simulation a = Make();
        using Simulation b = Make();
        Assert.Equal(RejectReason.None, ToolsFixture.Run(a, Road(a, tag: 71)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(b, Road(b, tag: 71)));
        a.Step(40);
        b.Step(40);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Contains(a.World.City!.Budget!.UndoCredits, u => u.TargetGeneration != 0);
    }
}
