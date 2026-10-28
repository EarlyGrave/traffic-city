using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Rules;
using Sim.Core.Util;

namespace Sim.Core.Tests.Tools;

/// <summary>
/// Фаззер правок (4.3, T3; player-tools.md §14 <c>EditFuzz_*</c>, TL4/TL5): случайные запреты поворотов, врезки рукавов, знаки и флаги полос на малом городе с машинами;
/// после каждой команды <c>VerifyDerived</c> пуст, сейв-загрузка-сейв побайтно равны, хэш не зависит от числа потоков.
/// </summary>
public sealed class EditFuzzTests
{
    private static readonly RejectReason[] _allowed =
    {
        RejectReason.None, RejectReason.Busy, RejectReason.CreatesTrap, RejectReason.NodeLimit, RejectReason.NotSupported, RejectReason.InvalidHandle, RejectReason.Capacity, RejectReason.InvalidArgument,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NodeEdits_Fuzz_KeepDerivedConsistent_AndSaveRoundTrips(int seed)
    {
        ulong hash = Run(seed, threads: 1, commands: 70, roundTrips: true);
        Assert.NotEqual(0UL, hash);
    }

    // Длинный прогон (TC_LONG=1): больше сидов и команд; в pre-commit не входит.
    [Fact]
    public void NodeEdits_Fuzz_Long()
    {
        if (Environment.GetEnvironmentVariable("TC_LONG") != "1")
        {
            return;
        }

        for (int seed = 100; seed < 112; seed++)
        {
            Run(seed, threads: 1, commands: 400, roundTrips: true);
        }

        for (int seed = 200; seed < 204; seed++)
        {
            Run(seed, threads: 1, commands: 150, roundTrips: true, peds: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void NodeEdits_Fuzz_FourThreads_KeepDerivedConsistent_AndSaveRoundTrips(int seed)
    {
        ulong hash = Run(seed, threads: 4, commands: 70, roundTrips: true);
        Assert.NotEqual(0UL, hash);
    }

    [Fact]
    public void NodeEdits_Fuzz_WithPedestrians()
    {
        Run(21, threads: 1, commands: 50, roundTrips: true, peds: true);
    }

    [Fact]
    public void NodeEdits_Fuzz_HashIsThreadIndependent()
    {
        ulong h1 = Run(7, threads: 1, commands: 40, roundTrips: false);
        ulong h2 = Run(7, threads: 2, commands: 40, roundTrips: false);
        ulong h3 = Run(7, threads: 3, commands: 40, roundTrips: false);
        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3);
    }

    // Пакет параллельных запросов фазы 3 (ADR-0032): правки сети (BuildRoad -> SyncGraph, волны оверлея) при 1 и 4 потоках дают тот же хэш,
    // сбор и повтор не расходятся (Mismatch = 0).
    [Fact]
    public void NodeEdits_Fuzz_BatchOneAndFourThreads_SameHash_NoMismatch()
    {
        long mm1 = 0, mm4 = 0, c4 = 0;
        ulong off = Run(7, threads: 1, commands: 40, roundTrips: false, setup: w => w.Router.Batch.MaxCandidates = 0);
        ulong h4 = Run(7, threads: 4, commands: 40, roundTrips: false, probe: w => { mm4 = w.Router.Batch.Mismatch; c4 = w.Router.Batch.Committed; });
        ulong h1 = Run(7, threads: 1, commands: 40, roundTrips: false, setup: w => w.Router.Batch.SingleThread = true, probe: w => mm1 = w.Router.Batch.Mismatch);
        Assert.Equal(off, h4);
        Assert.Equal(off, h1);
        Assert.Equal(0, mm1);
        Assert.Equal(0, mm4);
        Assert.True(c4 > 0, "пакет использовался");
    }

    private static ulong Run(int seed, int threads, int commands, bool roundTrips, bool peds = false, Action<World>? setup = null, Action<World>? probe = null)
    {
        using Simulation sim = ToolsFixture.Small(300, threads, buses: !peds, peds: peds);
        setup?.Invoke(sim.World);
        World w = sim.World;
        NetworkStore n = w.Net;
        var rng = new Random(seed);
        int applied = 0;
        int upgrades = 0;
        int drains = 0;
        for (int step = 0; step < commands; step++)
        {
            Command cmd = Pick(sim, rng, (uint)(step + 1));
            if (cmd.Kind != 0)
            {
                if (Environment.GetEnvironmentVariable("TC_FUZZ_TRACE") == "1")
                {
                    Console.WriteLine($"seed {seed} step {step} tick {sim.World.Tick}: {cmd.Kind}");
                }

                RejectReason r;
                try
                {
                    r = ToolsFixture.Run(sim, in cmd);
                }
                catch (Exception ex) when (ex.GetType().Name.Contains("DebugAssert", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"seed {seed} step {step} ({cmd.Kind}): {NextConnMismatch(w)}", ex);
                }

                Assert.True(Array.IndexOf(_allowed, r) >= 0, $"seed {seed} step {step}: {cmd.Kind} отклонена {r}");
                applied += r == RejectReason.None ? 1 : 0;
                if (r == RejectReason.None && cmd.Kind == CommandKind.UpgradeRoad)
                {
                    upgrades++;
                    drains += w.Ops.FindShrink(cmd.UpgradeRoad.Seg.Index) >= 0 ? 1 : 0;
                }
                Assert.Null(w.VerifyDerived());
                Assert.Null(NextConnMismatch(w));
            }

            try
            {
                sim.Step(1 + rng.Next(12));
            }
            catch (Exception ex) when (ex.GetType().Name.Contains("DebugAssert", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"seed {seed} step {step}: {NextConnMismatch(w)}", ex);
            }

            Assert.Null(NextConnMismatch(w));
            if (roundTrips && (step % 17 == 16 || Environment.GetEnvironmentVariable("TC_FUZZ_RT") == "1"))
            {
                using Simulation twin = ToolsFixture.RoundTrip(sim);
                twin.Step(30);
                sim.Step(30);
                Assert.Equal(sim.ComputeStateHash(), twin.ComputeStateHash());
            }
        }

        sim.Step(300);
        Assert.Null(w.VerifyDerived());

        // Дренаж завершается либо о застрявшем сообщено: за разумное время операций не остаётся, а те, что остались, дали DrainStalled.
        var stalled = new System.Collections.Generic.HashSet<int>();
        for (int t = 0; t < 5000 && w.Ops.Count > 0; t += 50)
        {
            sim.Step(50);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.DrainStalled)
                {
                    stalled.Add(e.A);
                }
            }
        }

        for (int k = 0; k < w.Ops.Count; k++)
        {
            Assert.True(stalled.Contains(w.Ops.Id[k]) || w.Tick - w.Ops.StartTick[k] < ToolOps.StallTicks * 2, $"операция {w.Ops.Id[k]} не кончилась и не сообщила о застревании");
        }

        Assert.Null(w.VerifyDerived());
        Assert.True(applied > 5, $"применилось всего {applied} команд из {commands}");
        Assert.True(n.ToolsTouched == 1);
        Assert.True(w.Work.NodeRebuilds >= 8, $"перестроек узлов {w.Work.NodeRebuilds}");
        Assert.True(w.Work.ToolCommandsApplied >= 8);
        Console.WriteLine($"[fuzz] seed {seed}: команд {applied}, перестроек узлов {w.Work.NodeRebuilds}, операций начато {w.Work.ToolOpsStarted}, закончено {w.Work.ToolOpsFinished}; правок полос {upgrades}, из них с дренажом {drains}");
        probe?.Invoke(w);
        return sim.ComputeStateHash();
    }

    // Кэш NextConn каждой машины на обычной полосе равен полному расчёту NextLane (то, что в Debug проверяет RuleEnv.FirstHop).
    private static string? NextConnMismatch(World w)
    {
        var e = new RuleEnv(w.Net, w.Vehicles, w.Occupancy, RouteAccess.From(w));
        for (int i = 0; i < w.Vehicles.HighWater; i++)
        {
            int lane = w.Vehicles.Lane[i];
            int nc = w.Vehicles.NextConn[i];
            if (!w.Vehicles.Pool.IsAlive(i) || lane < 0 || nc < 0 || w.Net.Lanes[lane].Kind != LaneKind.Normal)
            {
                continue;
            }

            int full = e.NextLane(i, lane, 1, useRoute: true);
            if (full != nc)
            {
                int r = w.Vehicles.Route[i];
                string win = string.Empty;
                for (int k = 0; r >= 0 && k < w.Routes.WinCount[r]; k++)
                {
                    int wl = w.Routes.Window[(r * w.Routes.WindowSteps) + k].Lane;
                    win += $" {wl}{(w.Net.Lanes[wl].Kind == LaneKind.Connector ? $"c[{w.Net.Lanes[wl].FromLane}->{w.Net.Lanes[wl].ToLane}]" : string.Empty)}";
                }

                LaneData a = w.Net.Lanes[nc];
                LaneData b = w.Net.Lanes[full];
                return $"тик {w.Tick}: машина {i} на полосе {lane}: NextConn {nc} ({a.FromLane}->{a.ToLane} узел {a.Node}), полный расчёт {full} ({b.FromLane}->{b.ToLane}); маршрут {r}, курсор {w.Vehicles.RouteCursor[i]}, флаги {w.Vehicles.Flags[i]}, окно:{win}; plan {(r >= 0 ? w.Routes.PlanCount[r] : -1)}";
            }
        }

        return null;
    }

    // Случайная команда по состоянию мира (детерминированно: обходы по возрастанию индекса, rng - единственный источник случайности).
    internal static Command Pick(Simulation sim, Random rng, uint tag)
    {
        NetworkStore n = sim.World.Net;
        int built = 0;
        for (int i = 0; i < n.NodeCount; i++)
        {
            built += n.NodeAlive(i) && n.Nodes[i].FirstArm >= 0 ? 1 : 0;
        }

        int pick = rng.Next(built);
        int node = -1;
        for (int i = 0; i < n.NodeCount && node < 0; i++)
        {
            if (n.NodeAlive(i) && n.Nodes[i].FirstArm >= 0 && pick-- == 0)
            {
                node = i;
            }
        }

        ref readonly NodeData nd = ref n.Nodes[node];
        int kind = rng.Next(100);
        if (kind >= 90 && n.AliveSegments > 12)
        {
            return PickRoadOp(sim, rng, tag);
        }

        kind %= 90;
        if (kind < 36)
        {
            int from = rng.Next(nd.ArmCount);
            int to = rng.Next(nd.ArmCount);
            bool ban = ((nd.TurnBan >> ((from * 8) + to)) & 1UL) == 0UL || rng.Next(3) == 0;
            return Command.MakeSetTurnBan(new SetTurnBanCmd { Node = ToolsFixture.Node(sim, node), FromArm = (byte)from, ToArm = (byte)to, Banned = (byte)(ban ? 1 : 0) }, tag);
        }

        if (kind < 58)
        {
            return Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = (byte)rng.Next(nd.ArmCount), Control = (byte)rng.Next(4) }, tag);
        }

        if (kind < 72)
        {
            int seg = n.NodeArms[nd.FirstArm + rng.Next(nd.ArmCount)];
            int dir = rng.Next(2);
            int lanes = dir == 0 ? n.Segments[seg].LanesAB : n.Segments[seg].LanesBA;
            if (lanes == 0)
            {
                return default;
            }

            byte bit = rng.Next(2) == 0 ? LaneFlags.BusOnly : LaneFlags.NoLaneChangeOut;
            return Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Dir = (byte)dir, LaneNo = (byte)rng.Next(lanes), Flags = rng.Next(2) == 0 ? bit : (byte)0, Mask = bit }, tag);
        }

        if (kind < 80)
        {
            return PickUpgrade(sim, rng, n.NodeArms[nd.FirstArm + rng.Next(nd.ArmCount)], tag);
        }

        // Врезка нового рукава в построенный узел под одним из восьми углов, без пересечения чужих сегментов.
        Vec2 p = nd.Position;
        int a0 = rng.Next(8);
        for (int a = 0; a < 8; a++)
        {
            float ang = ((a0 + a) % 8) * (MathF.PI / 4f);
            float len = 70f + rng.Next(40);
            var q = new Vec2(p.X + (MathF.Cos(ang) * len), p.Z + (MathF.Sin(ang) * len));
            if (n.AliveNodes < n.NodePool.Capacity - 4 && Free(n, node, p, q))
            {
                BuildRoadCmd road = ToolsFixture.Road(p, q, 1 + rng.Next(2));
                return Command.MakeBuildRoad(in road, tag);
            }
        }

        return default;
    }

    // T5: правка полос (+/-, по направлениям) и иногда типа или скорости сегмента - на занятых сегментах это перенос машин или дренаж ShrinkLanes.
    internal static Command PickUpgrade(Simulation sim, Random rng, int seg, uint tag)
    {
        ref readonly SegmentData sd = ref sim.World.Net.Segments[seg];
        var u = new UpgradeRoadCmd
        {
            Seg = ToolsFixture.Segment(sim, seg),
            SpeedLimit = rng.Next(4) == 0 ? 8f + rng.Next(9) : 0f,
            LanesAB = sd.LanesAB == 0 ? (byte)255 : (byte)(1 + rng.Next(3)),
            LanesBA = sd.LanesBA == 0 ? (byte)255 : (byte)(1 + rng.Next(3)),
            Kind = sd.Kind == RoadKind.Street ? RoadKind.Arterial : RoadKind.Street,
            Flags = rng.Next(4) == 0 ? (byte)0 : UpgradeRoadFlags.KeepKind,
        };
        return Command.MakeUpgradeRoad(in u, tag);
    }

    // Снос случайного сегмента без остановок (или отмена идущего сноса).
    private static Command PickRoadOp(Simulation sim, Random rng, uint tag)
    {
        World w = sim.World;
        NetworkStore n = w.Net;
        if (w.Ops.Count > 0 && rng.Next(3) == 0)
        {
            int k = rng.Next(w.Ops.Count);
            if (w.Ops.Kind[k] != (byte)ToolOpKind.RemoveSegment)
            {
                return default;
            }

            var undo = new BuildRoadCmd { RestoreOf = new EntityRef(w.Ops.TargetIndex[k], w.Ops.TargetGen[k]), SpeedLimit = 1f, LanesAB = 1 };
            return Command.MakeBuildRoad(in undo, tag);
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            int seg = rng.Next(n.SegmentCount);
            if (!n.SegmentAlive(seg) || (n.Segments[seg].Flags & SegmentFlags.Closing) != 0)
            {
                continue;
            }

            bool stop = false;
            for (int i = 0; i < n.BusStopCount && !stop; i++)
            {
                stop = n.BusStopAlive(i) && n.Lanes[n.BusStops[i].Lane].Segment == seg;
            }

            if (!stop)
            {
                return Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg), Flags = rng.Next(4) == 0 ? 1 : 0 }, tag);
            }
        }

        return default;
    }

    // Отрезок p-q не пересекает чужие сегменты, не ближе 14 м к чужим узлам (иначе прилипание) и не идёт вдоль соседних рукавов (угол от существующих >= 25 градусов).
    private static bool Free(NetworkStore n, int node, Vec2 p, Vec2 q)
    {
        for (int i = 0; i < n.NodeCount; i++)
        {
            if (i != node && n.NodeAlive(i) && Dist(n.Nodes[i].Position, q) < 14f)
            {
                return false;
            }
        }

        Vec2 d = q - p;
        float dl = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            SegmentData sd = n.Segments[s];
            bool incident = sd.NodeA == node || sd.NodeB == node;
            if (incident)
            {
                Vec2 far = n.Nodes[sd.NodeA == node ? sd.NodeB : sd.NodeA].Position - p;
                float fl = MathF.Sqrt((far.X * far.X) + (far.Z * far.Z));
                float cos = ((far.X * d.X) + (far.Z * d.Z)) / (fl * dl);
                if (cos > 0.9f)
                {
                    return false;
                }

                continue;
            }

            Vec2 a = n.Nodes[sd.NodeA].Position;
            Vec2 b = n.Nodes[sd.NodeB].Position;
            if (Cross(p, q, a, b) || Dist(Closest(a, b, q), q) < 14f)
            {
                return false;
            }
        }

        return true;
    }

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static Vec2 Closest(Vec2 a, Vec2 b, Vec2 p)
    {
        Vec2 ab = b - a;
        float t = (((p.X - a.X) * ab.X) + ((p.Z - a.Z) * ab.Z)) / MathF.Max(1e-6f, (ab.X * ab.X) + (ab.Z * ab.Z));
        t = MathF.Max(0f, MathF.Min(1f, t));
        return a + (ab * t);
    }

    private static bool Cross(Vec2 p, Vec2 q, Vec2 a, Vec2 b)
    {
        float d1 = Side(p, q, a);
        float d2 = Side(p, q, b);
        float d3 = Side(a, b, p);
        float d4 = Side(a, b, q);
        return d1 * d2 < 0f && d3 * d4 < 0f;
    }

    private static float Side(Vec2 a, Vec2 b, Vec2 c) => ((b.X - a.X) * (c.Z - a.Z)) - ((b.Z - a.Z) * (c.X - a.X));
}
