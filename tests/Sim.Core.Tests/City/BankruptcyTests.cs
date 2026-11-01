using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Core.Util;
using Sim.Scenarios;
using Sim.Scenarios.Tools;
using Xunit.Abstractions;

namespace Sim.Core.Tests.City;

/// <summary>
/// Мягкое банкротство (PG5, progression.md §5): два уровня предупреждения по дням до лимита с гистерезисом (<c>BudgetWarning</c>, <c>WarnLevel</c> в хэше), в банкротстве закрыты только
/// платные команды (<c>InsufficientFunds</c>), а снос с возвратом, undo в окне, налоги, параметры и снятие линий работают; выход из банкротства повышением налога R до 10 %.
/// </summary>
public sealed class BankruptcyTests
{
    private const int Day = 72_000;
    private readonly ITestOutputHelper _out;

    public BankruptcyTests(ITestOutputHelper output) => _out = output;

    private static Simulation Make(in ScenarioDesc d) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });

    private static ScenarioDesc Aggregate()
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json")).Desc;
        d.Town.Economy.CityBudget = true;
        d.Town.Residents.TripExecution = TripExecution.Aggregate;
        return d;
    }

    /// <summary>Казна так, чтобы дней до лимита было ровно <paramref name="days"/> при дефиците <paramref name="deficit"/> в сутки.</summary>
    private static void SetDays(CityWorld c, long deficit, int days)
    {
        CityBudget b = c.Budget!;
        b.IncomeEwma = 10_000;
        b.ExpenseEwma = 10_000 + deficit;
        long target = (days * deficit) - b.CreditLimit(in c.Cfg.Economy, 0);
        Ledger lg = c.Ledger;
        long delta = target - lg.City;
        if (delta > 0)
        {
            lg.Transfer(lg.ExternalAccount, lg.CityAccount, delta, FlowKind.Endowment);
        }
        else if (delta < 0)
        {
            lg.Transfer(lg.CityAccount, lg.ExternalAccount, -delta, FlowKind.Leakage);
        }

        Assert.Equal(days, CityKpi.DaysToLimit(c, 0));
    }

    private static int Warn(Simulation sim, out int days)
    {
        World w = sim.World;
        int before = w.EventCount;
        BudgetFlows.Warn(w, w.City!, w.City!.Budget!);
        days = -1;
        int level = 0;
        for (int i = before; i < w.EventCount; i++)
        {
            if (w.Events[i].Kind == SimEventKind.BudgetWarning)
            {
                level = w.Events[i].A;
                days = w.Events[i].B;
            }
        }

        return level;
    }

    [Fact]
    public void Warning_TwoLevels_OnlyUpward_WithHysteresis()
    {
        using Simulation sim = Make(Aggregate());
        sim.Step(10);
        CityWorld c = sim.World.City!;
        CityBudget b = c.Budget!;
        ulong h0 = sim.ComputeStateHash();

        SetDays(c, 1000, 9);
        Assert.Equal(0, Warn(sim, out _));   // 9 дней - ещё не предупреждение
        SetDays(c, 1000, 6);
        Assert.Equal(1, Warn(sim, out int d1));
        Assert.Equal(6, d1);
        Assert.Equal(1, b.WarnLevel);
        Assert.Equal(0, Warn(sim, out _));   // уровень тот же - события нет
        SetDays(c, 1000, 2);
        Assert.Equal(2, Warn(sim, out int d2));
        Assert.Equal(2, d2);
        SetDays(c, 1000, 9);
        Assert.Equal(0, Warn(sim, out _));   // гистерезис: <= 10 дней - уровень держится
        Assert.Equal(2, b.WarnLevel);
        SetDays(c, 1000, 11);
        Assert.Equal(0, Warn(sim, out _));
        Assert.Equal(0, b.WarnLevel);   // > 10 дней - снят
        SetDays(c, 1000, 5);
        Assert.Equal(1, Warn(sim, out _));
        b.ExpenseEwma = b.IncomeEwma;   // тренд >= 0 - снят
        Assert.Equal(0, Warn(sim, out _));
        Assert.Equal(0, b.WarnLevel);

        // Уровень - состояние (гистерезис): входит в хэш.
        ulong h1 = sim.ComputeStateHash();
        b.WarnLevel = 1;
        Assert.NotEqual(h1, sim.ComputeStateHash());
        Assert.NotEqual(0UL, h0);
    }

    [Fact]
    public void Warning_EmittedAtDayBoundary()
    {
        using Simulation sim = Make(Aggregate());
        sim.Step(Day - 2);
        CityWorld c = sim.World.City!;
        SetDays(c, 1_000_000, 3);   // дефицит много больше потоков суток - через границу дней по-прежнему <= 7
        bool warned = false;
        sim.Step(2);
        foreach (SimEvent e in sim.Events)
        {
            warned |= e.Kind == SimEventKind.BudgetWarning && e.A >= 1 && e.Tick == Day - 1;
        }

        Assert.True(warned, "нет BudgetWarning на границе суток");
        Assert.True(c.Budget!.WarnLevel >= 1);
    }

    private static readonly ToolsDesc _costs = new()
    {
        HasWorldBounds = true,
        BoundsMinX = -1e6f,
        BoundsMinZ = -1e6f,
        BoundsMaxX = 1e6f,
        BoundsMaxZ = 1e6f,
        Costs = new ToolCostsDesc { RoadPerMLaneStreet = 10, RoadPerMLaneArterial = 20, CurveFactorPct = 100, NodeSignal = 500, ArmSign = 50, TurnBan = 100, DemolishRefundPct = 50 },
    };

    private static RejectReason PreviewOf(Simulation sim, in Command cmd, out long cost)
    {
        RejectReason v = sim.Preview(in cmd, out PreviewResult r, new PreviewIssue[8]);
        cost = r.Cost;
        return v;
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

    [Fact]
    public void Insolvent_BlocksPaidOnly_RefundsUndoPolicyAndLineCutsWork()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Economy.CityBudget = true;
        using Simulation sim = Make(d);
        sim.SetToolsConfig(_costs);
        World w = sim.World;
        CityBudget b = w.City!.Budget!;
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 a = w.Net.Nodes[west].Position;

        // Дорога до банкротства (вне окна undo к моменту сноса) и вторая - под undo в окне.
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeBuildRoad(ToolsFixture.Road(a, new Vec2(a.X - 300f, a.Z)), 1)));
        int old = LastSegment(sim);
        sim.Step((10 * Simulation.TicksPerSecond) + 5);
        var stack = new UndoStack();
        Assert.True(stack.Execute(sim, new[] { Command.MakeBuildRoad(ToolsFixture.Road(a, new Vec2(a.X, a.Z - 300f)), 2) }).Ok);

        b.Insolvent = 1;   // казна за лимитом на прошлой границе суток (баланс сам по себе - в лимите: блок держится до выхода на границе суток)
        Command paid = Command.MakeBuildRoad(ToolsFixture.Road(a, new Vec2(a.X, a.Z + 300f)), 3);
        Assert.Equal(RejectReason.InsufficientFunds, PreviewOf(sim, in paid, out long cost));
        Assert.True(cost > 0);
        long insufficient = sim.Counters.InsufficientFunds;
        Assert.Equal(RejectReason.InsufficientFunds, ToolsFixture.Run(sim, paid));
        Assert.Equal(insufficient + 1, sim.Counters.InsufficientFunds);

        UndoResult undo = stack.Undo(sim);   // undo в окне - возврат 100 %
        Assert.True(undo.Ok, $"undo: {undo.FirstReason}");

        var rem = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, old) }, 4);
        Assert.Equal(RejectReason.None, PreviewOf(sim, in rem, out long refund));
        Assert.True(refund < 0);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, rem));

        BudgetPolicy pol = b.Policy;
        pol.TaxRBp = 1000;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBudgetPolicy(in pol, 5)));

        NetworkStore n = w.Net;
        int route = -1;
        for (int r = 0; r < n.BusRouteCount && route < 0; r++)
        {
            if (n.BusRoutePool.IsAlive(r))
            {
                route = r;
            }
        }

        Assert.True(route >= 0, "в сценарии нет линий");
        var rp = new SetBusRouteParamsCmd { Route = new EntityRef(route, n.BusRoutes[route].Generation), HeadwayTicks = 12_000, VehicleType = -1 };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBusRouteParams(in rp, 6)));
        var rr = new RemoveBusRouteCmd { Route = new EntityRef(route, n.BusRoutes[route].Generation) };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeRemoveBusRoute(in rr, 7)));
        Assert.Equal(insufficient + 1, sim.Counters.InsufficientFunds);
    }

    [Fact]
    public void Bankruptcy_ExitByRaisingTaxR_To10Pct()
    {
        int Run(bool raise)
        {
            ScenarioDesc d = Aggregate();
            d.Town.Economy.CreditDays = 0;   // лимит 0: банкротство = долг
            using Simulation sim = Make(d);
            sim.Step(3 * Day);
            CityWorld c = sim.World.City!;
            CityBudget b = c.Budget!;
            Assert.True(b.IncomeEwma > 0);
            Ledger lg = c.Ledger;
            lg.Transfer(lg.CityAccount, lg.ExternalAccount, lg.City + (30 * b.IncomeEwma), FlowKind.Leakage);   // долг - 30 сглаженных суточных доходов (EWMA после трёх суток ещё отстаёт от потока)
            sim.Step(Day);
            Assert.Equal(1, b.Insolvent);
            if (raise)
            {
                BudgetPolicy p = b.Policy;
                p.TaxRBp = 1000;
                Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBudgetPolicy(in p, 1)));
            }

            for (int day = 1; day <= 60; day++)
            {
                sim.Step(Day);
                foreach (SimEvent e in sim.Events)
                {
                    if (e.Kind == SimEventKind.BudgetRecovered)
                    {
                        Assert.Equal(0, b.Insolvent);
                        return day;
                    }
                }
            }

            return -1;
        }

        int withTax = Run(true);
        int baseline = Run(false);
        _out.WriteLine($"выход из банкротства: налог R 10 % - {withTax} сут., без правки - {baseline} сут.");
        Assert.InRange(withTax, 1, 30);
        Assert.True(baseline < 0 || withTax < baseline);
    }
}
