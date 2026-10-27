#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Intersections;

namespace Sim.Core.Tests.Peds;

/// <summary>Пешеходная фаза светофора (pedestrians.md §6, §15 «Переходы»): план без пешеходов не меняется, Walk по вызову, удержание, Clear не перекрывается стрелкой.</summary>
public sealed class PedSignalTests
{
    private static ScenarioDesc CrossDesc(bool peds)
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        if (peds)
        {
            d.Peds = new PedDesc { Enabled = true, Count = 0, Residents = 40, MidblockAtStops = false, ActivityMinTicks = 100_000, ActivityMaxTicks = 200_000 };
            d.Capacities.MaxPeds = 40;
            d.Capacities.MaxResidents = 40;
            d.Capacities.MaxPedPathSteps = 8;
        }

        return d;
    }

    private static Simulation Cross(bool peds, bool adaptive)
    {
        ScenarioDesc d = CrossDesc(peds);
        Simulation sim = Simulation.Create(in d);
        if (peds)
        {
            sim.World.Peds!.Wheel.Clear();
        }

        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        if (!adaptive)
        {
            SetSignalPlanCmd p = SignalFixture.Plan(480, 480, 60, 20);
            p.Node = 0;
            Assert.True(sim.Enqueue(Command.MakeSetSignalPlan(in p, 2)));
        }

        sim.Step(1);
        Assert.Equal(adaptive ? SignalMode.Adaptive : SignalMode.Fixed, sim.World.Signals.Mode[0]);
        return sim;
    }

    /// <summary>Номер сегмента-рукава E=0, S=1, W=2, N=3: переход через рукав E лежит поперёк оси X, его обслуживает ось Z (фаза 1).</summary>
    private static int XOf(World w, int arm) => PedCrossingTests.CrossingOfArm(w, arm);

    private static int ClearOf(SignalStore s, int x)
    {
        for (int j = 0; j < s.SigXCount[0]; j++)
        {
            if (s.SigXIdx[j] == x)
            {
                return s.SigXClear[j];
            }
        }

        throw new InvalidOperationException("crossing not in slot");
    }

    [Fact]
    public void NoPedestrians_SignalsAndVehiclesAreIdenticalToWorldWithoutPeds()
    {
        foreach (bool adaptive in new[] { true, false })
        {
            using Simulation a = Cross(peds: false, adaptive);
            using Simulation b = Cross(peds: true, adaptive);
            var da = new FlowDriver(a);
            var db = new FlowDriver(b);
            foreach (FlowDriver drv in new[] { da, db })
            {
                drv.AddSource(GateFixture.InLane(drv.W, 1), headwayTicks: 90, toArm: 3);
                drv.AddSource(GateFixture.InLane(drv.W, 0), headwayTicks: 140, toArm: 2);
                drv.AddSource(GateFixture.InLane(drv.W, 3), headwayTicks: 400, toArm: 0);
                for (int arm = 0; arm < 4; arm++)
                {
                    drv.AddSink(GateFixture.OutLane(drv.W, arm));
                }
            }

            for (int t = 0; t < 4000; t++)
            {
                a.Step(1);
                b.Step(1);
                SignalState sa = a.World.SignalStates[0];
                SignalState sb = b.World.SignalStates[0];
                Assert.True(sa.Phase == sb.Phase && sa.Interval == sb.Interval && sa.TicksToChange == sb.TicksToChange, $"сигнал разошёлся на тике {a.Tick}");
            }

            Assert.True(a.Vehicles.SequenceEqual(b.Vehicles));
            Assert.Equal(a.Counters.Grants, b.Counters.Grants);
        }
    }

    [Fact]
    public void Adaptive_WalkByCall_ServesAxisWithoutCars_Holds_AndClearIsNotOverlappedByArrow()
    {
        using Simulation sim = Cross(peds: true, adaptive: true);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        SignalStore s = w.Signals;
        int x = XOf(w, 0);   // поперёк оси X: ходить можно в зелёном оси Z (фаза 1)
        Assert.Equal(0, s.Phase[0]);   // Adaptive отдыхает на оси 0
        int p = PedCrossingTests.AddPed(w, x, 0);
        long releasedAt = -1;
        for (int t = 0; t < 1200; t++)
        {
            sim.Step(1);
            int idx = 0;
            for (int j = 0; j < s.SigXCount[0]; j++)
            {
                if (s.SigXIdx[j] == x)
                {
                    idx = j;
                }
            }

            bool walking = s.XWalkLeft[idx] != 0 || s.XClearLeft[idx] != 0;
            if (walking)
            {
                // Удержание: пока Walk/Clear, ось Z остаётся в Main-зелёном (без стрелки, без жёлтого).
                Assert.Equal(SignalInterval.Green, w.SignalStates[0].Interval);
                Assert.Equal(0, s.Sub[0]);
                Assert.Equal(1, s.Phase[0]);
            }

            if (releasedAt < 0 && pw.P.Pool.IsAlive(p) && pw.P.State[p] == (byte)PedState.Crossing)
            {
                releasedAt = sim.Tick;
                Assert.Equal(1, s.Phase[0]);
                Assert.Equal(SignalInterval.Green, w.SignalStates[0].Interval);
                Assert.Equal(PedSignals.Walk, (byte)((pw.X.CrossingSignals[x] >> 2) & 3));
            }
        }

        Assert.True(releasedAt > 0, "пешеход так и не вышел: ось без машин не обслужена по вызову");
        // MinGreen 7 с + Gap 2.5 с на оси 0, затем жёлтый и all-red: выход не позже 25 с.
        Assert.True(releasedAt < 25 * 20, $"выход на тике {releasedAt}");
        Assert.Equal(0, pw.P.AliveCount);
    }

    [Fact]
    public void Adaptive_LateCall_StartsWalkInsideGreen_WhenWalkAndClearFit()
    {
        using Simulation sim = Cross(peds: true, adaptive: true);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        SignalStore s = w.Signals;
        int xe = XOf(w, 0);
        int xw = XOf(w, 2);   // тот же фазовый набор: оба поперёк оси X, обслуживает ось Z
        PedCrossingTests.AddPed(w, xe, 0);
        int guard = 0;
        while (guard++ < 1200 && !(s.Phase[0] == 1 && s.Interval[0] == SignalInterval.Green && s.GreenElapsed[0] >= 40))
        {
            sim.Step(1);
        }

        Assert.Equal(1, s.Phase[0]);
        int elapsed = s.GreenElapsed[0];
        int q = PedCrossingTests.AddPed(w, xw, 1);
        Assert.True(elapsed + ClearOf(s, xw) + pw.Cfg.WalkTicks <= s.GreenTicks[1], "фикстура: Walk и Clear должны помещаться до max-out");
        sim.Step(1);
        Assert.Equal((byte)PedState.Crossing, pw.P.State[q]);
    }

    [Fact]
    public void Adaptive_GreenRestingPastMaxGreen_PedestrianArrivingLaterStillGetsWalk()
    {
        // L14: ось отдыхала в зелёном дольше MaxGreen (встречного спроса нет), Walk уже был обслужен: поздний пешеход не помещался в «до max-out»
        // и не вызывал ось (она и так зелёная) - ждал часами. Теперь при отдыхе дольше MaxGreen Walk стартует снова.
        using Simulation sim = Cross(peds: true, adaptive: true);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        SignalStore s = w.Signals;
        int xe = XOf(w, 0);
        int xw = XOf(w, 2);
        PedCrossingTests.AddPed(w, xe, 0);
        int guard = 0;
        while (guard++ < 4000 && !(s.Phase[0] == 1 && s.Interval[0] == SignalInterval.Green && s.GreenElapsed[0] >= s.GreenTicks[1] + 200))
        {
            sim.Step(1);
        }

        Assert.Equal(1, s.Phase[0]);
        Assert.True(s.GreenElapsed[0] >= s.GreenTicks[1] + 200, "фикстура: зелёная ось должна отдыхать дольше MaxGreen");
        Assert.Equal(0, pw.X.QCount[xe]);
        int q = PedCrossingTests.AddPed(w, xw, 1);
        for (int t = 0; t < 400 && pw.P.State[q] == (byte)PedState.AtCrossing; t++)
        {
            sim.Step(1);
        }

        Assert.NotEqual((byte)PedState.AtCrossing, pw.P.State[q]);
    }

    [Fact]
    public void Adaptive_GreenResting_PedestrianStreamDoesNotStarveCrossAxisCarCall()
    {
        // L15: на отдыхе дольше MaxGreen каждый новый пешеход заново запускал Walk + Hold, и непрерывный поток пешеходов держал зелёную ось бесконечно,
        // пока на встречной оси стояла машина. Теперь вызов другой оси запрещает Walk при отдыхе: Main кончается, ось переключается.
        using Simulation sim = Cross(peds: true, adaptive: true);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        SignalStore s = w.Signals;
        int xe = XOf(w, 0);
        int xw = XOf(w, 2);
        PedCrossingTests.AddPed(w, xe, 0);
        int guard = 0;
        while (guard++ < 4000 && !(s.Phase[0] == 1 && s.Interval[0] == SignalInterval.Green && s.GreenElapsed[0] >= s.GreenTicks[1] + 200))
        {
            sim.Step(1);
        }

        Assert.Equal(1, s.Phase[0]);
        Assert.True(s.GreenElapsed[0] >= s.GreenTicks[1] + 200, "фикстура: зелёная ось должна отдыхать дольше MaxGreen");

        int resident = 1;
        int walks = 0;
        bool switched = false;
        bool carPlaced = false;
        for (int t = 0; t < 3000 && !switched; t++)
        {
            if (t == 1200)
            {
                // ось X (фаза 0) на красном, машина у стоп-линии: вызов; к этому моменту поток пешеходов уже держит зелёную ось Z
                Assert.Equal(1, s.Phase[0]);
                Assert.True(walks > 100, $"фикстура: поток пешеходов должен непрерывно запускать Walk на отдыхающей оси (walks={walks} res={resident} ge={s.GreenElapsed[0]})");
                int laneX = GateFixture.InLane(w, 0);
                GateFixture.Place(sim, laneX, w.Net.LaneLength[laneX] - 6f, 0f);
                carPlaced = true;
            }

            // поток без пауз: пока идёт Clear, у каждого перехода уже стоит новый пешеход (на Walk предыдущих он не успел), Walk перезапускается в тот же тик
            bool clearing = false;
            for (int j = 0; j < s.SigXCount[0]; j++)
            {
                clearing |= s.XClearLeft[j] != 0;
            }

            foreach (int x in new[] { xe, xw })
            {
                if ((t == 0 || clearing) && pw.X.QCount[x] == 0 && resident < 40)
                {
                    PedCrossingTests.AddPed(w, x, resident++);
                }
            }

            sim.Step(1);
            if (PedSignals.IsWalk(pw.X, xe) || PedSignals.IsWalk(pw.X, xw))
            {
                walks++;
            }

            switched = s.Phase[0] != 1 || s.Interval[0] != SignalInterval.Green;
            Assert.False(switched && !carPlaced, "до вызова оси X зелёная ось Z не должна уступать");
        }

        Assert.True(switched, "зелёная ось Z должна уступить оси X: поток пешеходов не вправе продлевать её бесконечно");
    }

    [Fact]
    public void Fixed_WalkOnlyAtGreenStart_PedestrianWaitsForNextGreen()
    {
        using Simulation sim = Cross(peds: true, adaptive: false);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        SignalStore s = w.Signals;
        int x = XOf(w, 0);
        int p = PedCrossingTests.AddPed(w, x, 0);
        long releasedAt = -1;
        for (int t = 0; t < 2400 && releasedAt < 0; t++)
        {
            sim.Step(1);
            if (pw.P.State[p] == (byte)PedState.Crossing)
            {
                releasedAt = sim.Tick;
                Assert.Equal(1, s.Phase[0]);
                Assert.Equal(SignalInterval.Green, s.Interval[0]);
                Assert.True(s.TicksLeft[0] >= s.GreenTicks[1] - 1, $"выход не в начале зелёного: осталось {s.TicksLeft[0]}");
            }
        }

        Assert.True(releasedAt > 0);
    }

    [Fact]
    public void Coordination_RequiresRoomForWalkAndClear_OnlyWithPeds()
    {
        // C/2 = 420 тиков: MinGreen 140 + ArrowMax 120 + 2·(60 + 20) = 420 - без пешеходов помещается; с пешеходами нужно ещё Walk 100 + Clear (переход ≈ 10 м, 167) вместо MinGreen.
        foreach (bool peds in new[] { false, true })
        {
            using Simulation sim = Cross(peds, adaptive: true);
            var p = new SetSignalPlanCmd { Node = 0, Mode = SignalMode.Adaptive, PhaseCount = 2, YellowTicks = 60, AllRedTicks = 20, CycleTicks = 840, Flags = SignalPlanFlags.Arrows };
            long before = sim.Counters.CommandsRejected;
            Assert.True(sim.Enqueue(Command.MakeSetSignalPlan(in p, 9)));
            sim.Step(1);
            Assert.Equal(peds ? before + 1 : before, sim.Counters.CommandsRejected);
        }
    }

    [Fact]
    public void WalkCounters_AreInStateHash()
    {
        using Simulation sim = Cross(peds: true, adaptive: true);
        sim.Step(5);
        ulong h0 = sim.ComputeStateHash();
        sim.World.Signals.XWalkLeft[0] = 7;
        ulong h1 = sim.ComputeStateHash();
        sim.World.Signals.XWalkLeft[0] = 0;
        sim.World.Signals.XServed[0] = 1;
        ulong h2 = sim.ComputeStateHash();
        sim.World.Signals.XServed[0] = 0;
        Assert.NotEqual(h0, h1);
        Assert.NotEqual(h0, h2);
        Assert.Equal(h0, sim.ComputeStateHash());
    }
}
