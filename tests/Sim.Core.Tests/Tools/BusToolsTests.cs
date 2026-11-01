using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Scenarios.Tools;

namespace Sim.Core.Tests.Tools;

/// <summary>
/// Автобусные инструменты игрока (T9, player-tools.md §8.7, §15 T9): создать линию, вставить/добавить/удалить остановку, снять линию, параметры и приостановка,
/// <c>Unreachable</c>; правки на ходу (автобус, ехавший к удалённой остановке, не застревает), обратные записи, сейв-загрузка и потоки.
/// </summary>
public sealed class BusToolsTests
{
    private static Simulation Small(int threads = 1, int warm = 400) => ToolsFixture.Small(warm, threads, buses: true);

    private static EntityRef Route(Simulation sim, int r) => new(r, sim.World.Net.BusRoutes[r].Generation);

    private static EntityRef Stop(Simulation sim, int s) => new(s, sim.World.Net.BusStops[s].Generation);

    private static int StopAt(Simulation sim, int r, int k) => sim.World.Net.RouteStop(r, k);

    // Вставка остановки на полосу lane в точку s на позицию pos линии r.
    internal static Command Insert(Simulation sim, int r, int pos, int lane, float s, uint tag = 0, byte kind = 0)
    {
        NetworkStore n = sim.World.Net;
        ref readonly LaneData ld = ref n.Lanes[lane];
        var c = new InsertBusStopCmd
        {
            Route = Route(sim, r),
            Position = pos,
            Seg = ToolsFixture.Segment(sim, ld.Segment),
            Dir = ld.Direction,
            LaneNo = ld.LaneNo,
            Kind = kind,
            S = s,
        };
        return Command.MakeInsertBusStop(in c, tag);
    }

    // Новая линия по остановкам 0, 2, 4, ... генераторной линии src (те же полосы, на 10 м раньше): все соседние пары достижимы.
    private static int NewLine(Simulation sim, int src, int stops, int headway = 300)
    {
        NetworkStore n = sim.World.Net;
        var create = new CreateBusRouteCmd { HeadwayTicks = headway, VehicleType = n.BusRoutes[src].VehicleType };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeCreateBusRoute(in create, 900)));
        int r = -1;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.BusRouteCreated)
            {
                r = e.A;
            }
        }

        Assert.True(r >= 0, "нет BusRouteCreated");
        for (int k = 0; k < stops; k++)
        {
            BusStopData st = n.BusStops[StopAt(sim, src, 2 * k)];
            Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Insert(sim, r, -1, st.Lane, MathF.Max(0f, st.S - 10f), (uint)(901 + k))));
        }

        Assert.Equal(stops, n.BusRoutes[r].StopCount);
        return r;
    }

    private static bool Arrived(Simulation sim, int route, int stop = -1)
    {
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.BusStopArrived && e.A == route && (stop < 0 || e.B == stop))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void CreateLine_AppendInsertRemove_OnLiveWorld_BusesServeIt()
    {
        using Simulation sim = Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        int r = NewLine(sim, 0, 3);
        Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);

        // Выпуск и прибытия на новой линии.
        bool served = false;
        for (int t = 0; t < 6000 && !served; t++)
        {
            sim.Step(1);
            served = Arrived(sim, r, StopAt(sim, r, 1));
        }

        Assert.True(served, "автобус новой линии не дошёл до второй остановки");

        // Вставка в середину, добавление (AppendBusStop, сырой индекс полосы) и удаление - на ходу.
        BusStopData mid = n.BusStops[StopAt(sim, 0, 3)];
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Insert(sim, r, 2, mid.Lane, MathF.Max(0f, mid.S - 5f), 950)));
        Assert.Equal(4, n.BusRoutes[r].StopCount);
        BusStopData tail = n.BusStops[StopAt(sim, 0, 5)];
        var app = new AppendBusStopCmd { Route = r, Lane = tail.Lane, S = tail.S, Kind = BusStopKind.Curb };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeAppendBusStop(in app, 951)));
        Assert.Equal(5, n.BusRoutes[r].StopCount);
        int removed = StopAt(sim, r, 1);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = Stop(sim, removed) }, 952)));
        Assert.Equal(4, n.BusRoutes[r].StopCount);
        Assert.False(n.BusStopAlive(removed));
        Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);

        // Старый handle остановки больше не годится.
        var stale = new RemoveBusStopCmd { Stop = new EntityRef(removed, n.BusStops[removed].Generation - 1) };
        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, Command.MakeRemoveBusStop(in stale, 953)));

        sim.Step(3000);
        Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);
        using Simulation twin = ToolsFixture.RoundTrip(sim);
        twin.Step(200);
        sim.Step(200);
        Assert.Equal(sim.ComputeStateHash(), twin.ComputeStateHash());
    }

    [Fact]
    public void RemoveTargetStop_BusHeadingThere_GoesOn_NotStuck()
    {
        using Simulation sim = Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        int bus = -1;
        for (int t = 0; t < 8000 && bus < 0; t++)
        {
            sim.Step(1);
            for (int i = 0; i < v.HighWater && bus < 0; i++)
            {
                if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 && (v.Flags[i] & (byte)VehicleFlags.AtStop) == 0 && v.Lane[i] >= 0 &&
                    v.BusNextStop[i] >= 1 && v.BusNextStop[i] < n.BusRoutes[v.BusRoute[i]].StopCount - 2)
                {
                    bus = i;
                }
            }
        }

        Assert.True(bus >= 0, "нет автобуса в пути к промежуточной остановке");
        int gen = v.Pool.GenerationOf(bus);
        int r = v.BusRoute[bus];
        int k = v.BusNextStop[bus];
        int target = StopAt(sim, r, k);
        int after = StopAt(sim, r, k + 1);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = Stop(sim, target) }, 10)));
        Assert.False(n.BusStopAlive(target));
        Assert.True(!v.Pool.IsAlive(bus, gen) || v.BusNextStop[bus] >= k);
        bool next = false;
        for (int t = 0; t < 6000 && !next && v.Pool.IsAlive(bus, gen); t++)
        {
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                next |= e.Kind == SimEventKind.BusStopArrived && e.A == r && e.B == after && (v.Flags[bus] & (byte)VehicleFlags.AtStop) != 0;
            }
        }

        Assert.True(next, "автобус не доехал до следующей остановки после удалённой");
        Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);
    }

    [Fact]
    public void RemoveTerminalStop_BusHeadingThere_FinishesRun()
    {
        using Simulation sim = Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        int bus = -1;
        for (int t = 0; t < 12000 && bus < 0; t++)
        {
            sim.Step(1);
            for (int i = 0; i < v.HighWater && bus < 0; i++)
            {
                if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 && (v.Flags[i] & (byte)VehicleFlags.AtStop) == 0 && v.Lane[i] >= 0 &&
                    v.BusNextStop[i] == n.BusRoutes[v.BusRoute[i]].StopCount - 1)
                {
                    bus = i;
                }
            }
        }

        Assert.True(bus >= 0, "нет автобуса в пути к конечной");
        int gen = v.Pool.GenerationOf(bus);
        int r = v.BusRoute[bus];
        int last = StopAt(sim, r, n.BusRoutes[r].StopCount - 1);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = Stop(sim, last) }, 11)));
        for (int t = 0; t < 6000 && v.Pool.IsAlive(bus, gen); t++)
        {
            sim.Step(1);
        }

        Assert.False(v.Pool.IsAlive(bus, gen), "автобус без конечной не завершил рейс");
    }

    [Fact]
    public void RemoveRoute_BusesFinish_SlotsFreed_UndoRestoresLine()
    {
        using Simulation sim = Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        VehicleStore v = w.Vehicles;
        for (int t = 0; t < 4000 && n.BusRoutes[0].ActiveBuses < 2; t++)
        {
            sim.Step(1);
        }

        Assert.True(n.BusRoutes[0].ActiveBuses >= 1);
        string before = LineCanon(n, 0);
        int stops = n.BusRoutes[0].StopCount;
        EntityRef route = Route(sim, 0);
        Assert.True(sim.Enqueue(Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = route }, 20)));
        sim.Step(1);
        Assert.Equal(1, sim.Inverses.Length);
        Command[] inverse = sim.InverseCommands.Slice(sim.Inverses[0].First, sim.Inverses[0].Count).ToArray();
        Assert.Equal(1 + stops, inverse.Length);
        Assert.False(n.BusRouteAlive(0));
        bool removedEv = false;
        foreach (SimEvent e in sim.Events)
        {
            removedEv |= e.Kind == SimEventKind.BusRouteRemoved && e.A == 0;
        }

        Assert.True(removedEv);
        for (int t = 0; t < 6000 && Orphans(v) > 0; t++)
        {
            sim.Step(1);
        }

        Assert.Equal(0, Orphans(v));
        Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);

        // Undo: линия и остановки пересоздаются (перенумерация - события *Remapped), порядок и места остановок - прежние.
        foreach (Command c in inverse)
        {
            Assert.True(sim.Enqueue(c));
        }

        sim.Step(1);
        int now = -1;
        foreach (SimEvent e in sim.Events)
        {
            Assert.NotEqual(SimEventKind.CommandRejected, e.Kind);
            if (e.Kind == SimEventKind.BusRouteRemapped)
            {
                now = e.B;
            }
        }

        Assert.True(now >= 0);
        Assert.Equal(before, LineCanon(n, now));
        Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);
    }

    private static int Orphans(VehicleStore v)
    {
        int c = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            c += v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 && v.BusRoute[i] < 0 ? 1 : 0;
        }

        return c;
    }

    private static string LineCanon(NetworkStore n, int r)
    {
        BusRouteData br = n.BusRoutes[r];
        string s = $"h{br.HeadwayTicks} t{br.VehicleType} n{br.StopCount}:";
        for (int k = 0; k < br.StopCount; k++)
        {
            BusStopData st = n.BusStops[n.RouteStop(r, k)];
            s += $" {st.Lane}@{(int)MathF.Round(st.S * 1000f)}k{(int)st.Kind}";
        }

        return s;
    }

    [Fact]
    public void Suspend_StopsDispatch_ResumeRestarts()
    {
        using Simulation sim = Small();
        World w = sim.World;
        NetworkStore n = w.Net;
        int r = NewLine(sim, 0, 3, headway: 200);
        var p = new SetBusRouteParamsCmd { Route = Route(sim, r), VehicleType = -1, Flags = BusRouteFlags.Suspend };
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBusRouteParams(in p, 30)));
        Assert.True(n.BusRoutes[r].Suspended);
        Assert.Equal(-200, n.BusRoutes[r].HeadwayTicks);
        long last = n.BusRoutes[r].LastDispatchTick;
        sim.Step(1000);
        Assert.Equal(last, n.BusRoutes[r].LastDispatchTick);

        p.Flags = BusRouteFlags.Resume;
        p.HeadwayTicks = 150;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBusRouteParams(in p, 31)));
        Assert.Equal(150, n.BusRoutes[r].HeadwayTicks);
        sim.Step(400);
        Assert.True(n.BusRoutes[r].LastDispatchTick > last, "после возобновления нет выпуска");

        p.Flags = BusRouteFlags.Suspend | BusRouteFlags.Resume;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeSetBusRouteParams(in p, 32)));

        // Сейв приостановленной линии загружается.
        p.Flags = BusRouteFlags.Suspend;
        p.HeadwayTicks = 0;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSetBusRouteParams(in p, 33)));
        using Simulation twin = ToolsFixture.RoundTrip(sim);
        Assert.True(twin.World.Net.BusRoutes[r].Suspended);
    }

    [Fact]
    public void Insert_IsolatedRoad_Unreachable_AndArgumentChecks()
    {
        using Simulation sim = Small();
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        World w = sim.World;
        NetworkStore n = w.Net;
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 p = n.Nodes[west].Position;
        BuildRoadCmd road = ToolsFixture.Road(new Vec2(p.X - 3000f, p.Z), new Vec2(p.X - 2800f, p.Z), 1);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeBuildRoad(in road, 40)));
        int seg = n.SegmentCount - 1;
        while (!n.SegmentAlive(seg) || n.Segments[seg].LanesAB == 0)
        {
            seg--;
        }

        int lane = n.Segments[seg].FirstLaneAB;
        Assert.Equal(RejectReason.Unreachable, ToolsFixture.Run(sim, Insert(sim, 0, -1, lane, 50f, 41)));
        Assert.Equal(RejectReason.Unreachable, ToolsFixture.Run(sim, Insert(sim, 0, 0, lane, 50f, 42)));

        // Превью даёт тот же вердикт и точку проблемы.
        Span<PreviewIssue> issues = stackalloc PreviewIssue[4];
        Assert.Equal(RejectReason.Unreachable, sim.Preview(Insert(sim, 0, -1, lane, 50f), out PreviewResult pr, issues));
        Assert.True(pr.IssueCount >= 1);

        int l0 = n.BusStops[StopAt(sim, 0, 0)].Lane;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Insert(sim, 0, 99, l0, 1f, 43)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Insert(sim, 0, 0, l0, -1f, 44)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Insert(sim, 0, 0, l0, 1f, 45, kind: 7)));
        var bad = new CreateBusRouteCmd { HeadwayTicks = 0, VehicleType = 0 };
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeCreateBusRoute(in bad, 46)));
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(sim, Command.MakeSpawnBus(0, 47)));
    }

    [Fact]
    public void Inverses_RoundTrip_EachCommand()
    {
        using Simulation sim = Small();
        NetworkStore n = sim.World.Net;
        var stack = new UndoStack();
        string[] c0 = Lines(n);

        // Создать линию с остановками одной группой, затем вставка, удаление, параметры, снятие - каждая отменяется и повторяется.
        var create = new CreateBusRouteCmd { HeadwayTicks = 250, VehicleType = n.BusRoutes[0].VehicleType };
        Assert.True(stack.Execute(sim, new[] { Command.MakeCreateBusRoute(in create) }).Ok);
        int r = n.BusRouteCount - 1;
        while (!n.BusRouteAlive(r) || n.BusRoutes[r].StopCount != 0)
        {
            r--;
        }

        for (int k = 0; k < 3; k++)
        {
            BusStopData st = n.BusStops[StopAt(sim, 1, 2 * k)];
            Assert.True(stack.Execute(sim, new[] { Insert(sim, r, -1, st.Lane, st.S) }).Ok);
        }

        BusStopData m = n.BusStops[StopAt(sim, 1, 1)];
        Assert.True(stack.Execute(sim, new[] { Insert(sim, r, 1, m.Lane, m.S) }).Ok);
        Assert.True(stack.Execute(sim, new[] { Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = Stop(sim, StopAt(sim, r, 2)) }) }).Ok);
        var par = new SetBusRouteParamsCmd { Route = Route(sim, r), HeadwayTicks = 500, VehicleType = -1, Flags = BusRouteFlags.Suspend };
        Assert.True(stack.Execute(sim, new[] { Command.MakeSetBusRouteParams(in par) }).Ok);
        Assert.True(stack.Execute(sim, new[] { Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = Route(sim, 2) }) }).Ok);
        string[] cEnd = Lines(n);
        sim.Step(50);

        var history = new List<string[]>();
        while (stack.UndoCount > 0)
        {
            UndoResult u = stack.Undo(sim);
            Assert.True(u.Rejected == 0, $"undo отклонён: {u.FirstReason} {u.FirstKind}");
            history.Add(Lines(n));
            sim.Step(20);
        }

        Assert.Equal(c0, Lines(n));
        while (stack.RedoCount > 0)
        {
            UndoResult u = stack.Redo(sim);
            Assert.True(u.Rejected == 0, $"redo отклонён: {u.FirstReason} {u.FirstKind}");
            sim.Step(20);
        }

        Assert.Equal(cEnd, Lines(n));
        Assert.Equal(string.Empty, sim.World.VerifyDerived() ?? string.Empty);
    }

    // Линии без индексов: параметры и остановки по месту (полоса, S, вид), отсортировано.
    private static string[] Lines(NetworkStore n)
    {
        var list = new List<string>();
        for (int r = 0; r < n.BusRouteCount; r++)
        {
            if (n.BusRouteAlive(r))
            {
                list.Add(LineCanon(n, r));
            }
        }

        list.Sort(StringComparer.Ordinal);
        return list.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 4)]
    public void BusFuzz_KeepDerivedConsistent_AndSaveRoundTrips(int seed, int threads)
    {
        ulong h = BusFuzz(seed, threads, roundTrips: true);
        Assert.NotEqual(0UL, h);
    }

    [Fact]
    public void BusFuzz_HashIsThreadIndependent()
    {
        ulong h1 = BusFuzz(5, 1, roundTrips: false);
        ulong h4 = BusFuzz(5, 4, roundTrips: false);
        Assert.Equal(h1, h4);
    }

    [Fact]
    public void BusFuzz_WithPedestrians()
    {
        ScenarioDesc d = ToolsFixture.SmallDesc(buses: true, peds: true);
        using Simulation sim = Simulation.Create(d, new SimRuntimeOptions { WorkerThreads = 1 });
        sim.Step(300);
        var rng = new Random(9);
        for (int step = 0; step < 60; step++)
        {
            Command c = PickBus(sim, rng, (uint)(step + 1));
            if (c.Kind != 0)
            {
                ToolsFixture.Run(sim, in c);
                Assert.Equal(string.Empty, sim.World.VerifyDerived() ?? string.Empty);
            }

            sim.Step(1 + rng.Next(40));
        }

        sim.Step(2000);
        Assert.Equal(string.Empty, sim.World.VerifyDerived() ?? string.Empty);
    }

    private static readonly RejectReason[] _allowed =
    {
        RejectReason.None, RejectReason.Busy, RejectReason.CreatesTrap, RejectReason.NodeLimit, RejectReason.NotSupported, RejectReason.InvalidHandle, RejectReason.Capacity,
        RejectReason.InvalidArgument, RejectReason.TooShort, RejectReason.Unreachable,
    };

    private static ulong BusFuzz(int seed, int threads, bool roundTrips)
    {
        using Simulation sim = Small(threads, 300);
        World w = sim.World;
        var rng = new Random(seed);
        int applied = 0;
        for (int step = 0; step < 90; step++)
        {
            Command cmd = rng.Next(5) == 0 ? EditFuzzTests.Pick(sim, rng, (uint)(step + 1), chain: false) : PickBus(sim, rng, (uint)(step + 1));
            if (cmd.Kind != 0)
            {
                RejectReason r = ToolsFixture.Run(sim, in cmd);
                Assert.True(Array.IndexOf(_allowed, r) >= 0, $"seed {seed} step {step}: {cmd.Kind} отклонена {r}");
                applied += r == RejectReason.None && cmd.Kind is CommandKind.CreateBusRoute or CommandKind.InsertBusStop or CommandKind.RemoveBusStop
                    or CommandKind.RemoveBusRoute or CommandKind.SetBusRouteParams ? 1 : 0;
                Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);
            }

            sim.Step(1 + rng.Next(30));
            if (roundTrips && step % 15 == 14)
            {
                using Simulation twin = ToolsFixture.RoundTrip(sim, threads);
                twin.Step(40);
                sim.Step(40);
                Assert.Equal(sim.ComputeStateHash(), twin.ComputeStateHash());
            }
        }

        sim.Step(1500);
        Assert.Equal(string.Empty, w.VerifyDerived() ?? string.Empty);
        Assert.True(applied > 15, $"автобусных команд применилось {applied}");
        Console.WriteLine($"[bus fuzz] seed {seed} threads {threads}: автобусных команд {applied}, линий {w.Net.AliveBusRoutes}, остановок {w.Net.AliveBusStops}");
        return sim.ComputeStateHash();
    }

    // Случайная автобусная команда: новая линия, вставка остановки (на полосу одной из остановок линий, со сдвигом), удаление остановки, параметры, снятие линии.
    internal static Command PickBus(Simulation sim, Random rng, uint tag)
    {
        NetworkStore n = sim.World.Net;
        int routes = n.AliveBusRoutes;
        int kind = rng.Next(100);
        if (routes == 0 || kind < 8)
        {
            var c = new CreateBusRouteCmd { HeadwayTicks = 150 + rng.Next(400), VehicleType = n.BusRoutes[0].VehicleType, Flags = rng.Next(5) == 0 ? BusRouteFlags.Suspend : 0 };
            if (!n.BusRouteAlive(0))
            {
                c.VehicleType = sim.World.Types.Length - 1;
            }

            return Command.MakeCreateBusRoute(in c, tag);
        }

        int r = PickAlive(n, rng, routes);
        ref readonly BusRouteData br = ref n.BusRoutes[r];
        if (kind < 55)
        {
            int any = n.AliveBusStops;
            if (any == 0)
            {
                return default;
            }

            int pick = rng.Next(any);
            int src = -1;
            for (int s = 0; s < n.BusStopCount && src < 0; s++)
            {
                if (n.BusStopAlive(s) && pick-- == 0)
                {
                    src = s;
                }
            }

            BusStopData st = n.BusStops[src];
            float s0 = MathF.Max(0f, MathF.Min(n.Lanes[st.Lane].Length, st.S + rng.Next(-30, 30)));
            return Insert(sim, r, rng.Next(4) == 0 ? -1 : rng.Next(br.StopCount + 1), st.Lane, s0, tag, (byte)rng.Next(2));
        }

        if (kind < 80)
        {
            if (br.StopCount == 0)
            {
                return default;
            }

            int si = n.RouteStop(r, rng.Next(br.StopCount));
            return Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = new EntityRef(si, n.BusStops[si].Generation) }, tag);
        }

        if (kind < 94)
        {
            var p = new SetBusRouteParamsCmd
            {
                Route = new EntityRef(r, br.Generation),
                HeadwayTicks = rng.Next(3) == 0 ? 0 : 150 + rng.Next(400),
                VehicleType = -1,
                Flags = rng.Next(3) switch { 0 => BusRouteFlags.Suspend, 1 => BusRouteFlags.Resume, _ => 0 },
            };
            return Command.MakeSetBusRouteParams(in p, tag);
        }

        return Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = new EntityRef(r, br.Generation) }, tag);
    }

    private static int PickAlive(NetworkStore n, Random rng, int alive)
    {
        int pick = rng.Next(alive);
        for (int r = 0; r < n.BusRouteCount; r++)
        {
            if (n.BusRouteAlive(r) && pick-- == 0)
            {
                return r;
            }
        }

        return -1;
    }

    [Fact]
    public void BusTools_SteadyState_ZeroBytesPerTick()
    {
        using Simulation sim = Small();
        NetworkStore n = sim.World.Net;
        int r = NewLine(sim, 0, 3);
        sim.Step(400);
        BusStopData st = n.BusStops[StopAt(sim, 1, 1)];

        // Прогрев: первая правка выделяет scratch связности.
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Insert(sim, r, -1, st.Lane, st.S, 60)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = Stop(sim, StopAt(sim, r, 3)) }, 61)));
        sim.Step(200);
        long bytes = Fixtures.AllocationProbe.Measure(() =>
        {
            for (int t = 0; t < 4; t++)
            {
                sim.Enqueue(Insert(sim, r, -1, st.Lane, st.S, (uint)(70 + t)));
                sim.Step(25);
                int si = n.RouteStop(r, n.BusRoutes[r].StopCount - 1);
                sim.Enqueue(Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = new EntityRef(si, n.BusStops[si].Generation) }, (uint)(80 + t)));
                sim.Step(25);
            }
        });
        Assert.Equal(0, bytes);
    }
}
