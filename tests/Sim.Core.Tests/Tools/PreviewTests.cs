using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Worlds;
using Sim.Core.Tools;
using Sim.Core.Util;

namespace Sim.Core.Tests.Tools;

/// <summary>
/// Превью и общий валидатор (4.3, T7; player-tools.md §7, §14 <c>Preview_DoesNotMutate</c>, <c>Connectivity_Preview</c>): превью не меняет мир, вердикт совпадает с применением,
/// стоимость берётся из таблицы, связность считается Tarjan'ом и кэшируется по версии сети.
/// </summary>
public sealed class PreviewTests
{
    private static readonly ToolsDesc _costs = new()
    {
        Costs = new ToolCostsDesc
        {
            RoadPerMLaneStreet = 10,
            RoadPerMLaneArterial = 20,
            CurveFactorPct = 120,
            NodeSignal = 500,
            ArmSign = 50,
            TurnBan = 100,
            DemolishRefundPct = 50,
        },
    };

    [Fact]
    public void Preview_DoesNotMutate()
    {
        using Simulation sim = ToolsFixture.Small(400);
        sim.SetToolsConfig(_costs);
        var rng = new Random(11);
        ulong hash = sim.ComputeStateHash();
        byte[] save = WorldSaveFixtures.SaveBytes(sim);
        long tick = sim.Tick;
        var issues = new PreviewIssue[16];
        var axis = new Vec2[4];
        int rejected = 0;
        int ok = 0;
        for (int i = 0; i < 600; i++)
        {
            Command cmd = RandomCommand(sim, rng, (uint)(i + 1));
            RejectReason v = sim.Preview(in cmd, out PreviewResult r, issues, axis, PreviewOptions.Detail);
            Assert.Equal(v, r.Verdict);
            Assert.True(r.IssuesWritten <= r.IssueCount && r.IssuesWritten <= issues.Length);
            rejected += v == RejectReason.None ? 0 : 1;
            ok += v == RejectReason.None ? 1 : 0;
        }

        Assert.True(ok > 50 && rejected > 50, $"выборка вырожденная: принято {ok}, отклонено {rejected}");
        Assert.Equal(tick, sim.Tick);
        Assert.Equal(hash, sim.ComputeStateHash());
        Assert.True(save.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(sim)), "байты Save до/после превью различаются");
        Assert.Null(sim.World.VerifyDerived());
    }

    [Fact]
    public void Preview_AllocatesNothing_WhenWarm()
    {
        using Simulation sim = ToolsFixture.Small(400);
        sim.SetToolsConfig(_costs);
        World w = sim.World;
        int seg = ToolsFixture.BusiestSegment(sim);
        int node = ToolsFixture.WestmostNode(sim);
        Assert.True(seg >= 0 && node >= 0 && w.Net.Nodes[node].ArmCount >= 2);
        Vec2 p = w.Net.Nodes[node].Position;
        BuildRoadCmd road = ToolsFixture.Road(p, new Vec2(p.X + 0f, p.Z - 90f));
        Command[] cmds =
        {
            Command.MakeBuildRoad(in road),
            Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }),
            Command.MakeSetTurnBan(new SetTurnBanCmd { Node = ToolsFixture.Node(sim, node), FromArm = 0, ToArm = 1, Banned = 1 }),
            Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 0, Control = 2 }),
        };
        var issues = new PreviewIssue[32];
        var axis = new Vec2[8];
        long bytes = AllocationProbe.Measure(() =>
        {
            for (int i = 0; i < cmds.Length; i++)
            {
                sim.Preview(in cmds[i], out _, issues, axis, PreviewOptions.Detail);
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Preview_VerdictMatchesApply_On10000RandomCommands()
    {
        int count = Environment.GetEnvironmentVariable("TC_LONG") == "1" ? 20000 : 10000;
        using Simulation sim = ToolsFixture.Small(400);
        var rng = new Random(5);
        var issues = new PreviewIssue[8];
        int agree = 0;
        int rebuildDependent = 0;
        var seen = new System.Collections.Generic.Dictionary<RejectReason, int>();
        for (int i = 0; i < count; i++)
        {
            Command cmd = RandomCommand(sim, rng, (uint)(i + 1));
            if (cmd.Kind == 0)
            {
                continue;
            }

            RejectReason pv = sim.Preview(in cmd, out PreviewResult pr, issues);
            bool opsPending = sim.World.Ops.Count > 0;
            RejectReason av = ToolsFixture.Run(sim, in cmd);
            seen.TryGetValue(av, out int cnt);
            seen[av] = cnt + 1;
            if (pv == av)
            {
                agree++;
            }
            else
            {
                // Единственное допустимое расхождение: врезка в построенный узел (Mechanism = 1) - перестройку узла превью не планирует (нужен сегмент в сети),
                // применение может ответить Busy / CreatesTrap / NodeLimit / Capacity.
                // Второе: операции дренажа могли завершиться в фазе 1а того же тика до команды (превью - против состояния до тика) и убрать сегмент/рукав.
                bool rebuild = cmd.Kind == CommandKind.BuildRoad && pv == RejectReason.None && pr.Mechanism == 1 &&
                    av is RejectReason.Busy or RejectReason.CreatesTrap or RejectReason.NodeLimit or RejectReason.Capacity;
                Assert.True(rebuild || opsPending, $"команда {i} ({cmd.Kind}): превью {pv}, применение {av}");
                rebuildDependent += rebuild ? 1 : 0;
            }

            Assert.Equal(pv, pr.Verdict);
        }

        Assert.Null(sim.World.VerifyDerived());
        Assert.True(agree > count / 2, $"совпало лишь {agree} из {count}");
        foreach (RejectReason need in new[] { RejectReason.None, RejectReason.InvalidHandle, RejectReason.InvalidArgument, RejectReason.TooShort, RejectReason.OutOfBounds, RejectReason.CreatesTrap })
        {
            Assert.True(seen.ContainsKey(need), $"в выборке нет исхода {need}");
        }

        Console.WriteLine($"[preview] команд {count}, совпали {agree}, расхождений по перестройке узла {rebuildDependent}; исходы: {string.Join(", ", seen)}");
    }

    [Fact]
    public void Connectivity_Preview()
    {
        using Simulation sim = Simulation.Create(TestScenarios.Line());
        sim.SetToolsConfig(_costs);
        sim.Step(5);
        World w = sim.World;
        var issues = new PreviewIssue[16];

        // Явные зоны Line без полос появления (TODO генератора): назначаем по одной полосе на зону - A->B сегмента 0 и сегмента 1.
        NetworkStore net = w.Net;
        net.ZoneLanes[0] = net.Segments[0].FirstLaneAB;
        net.ZoneLanes[1] = net.Segments[1].FirstLaneAB;
        net.Zones[0].FirstSpawnLane = 0;
        net.Zones[0].SpawnLaneCount = 1;
        net.Zones[1].FirstSpawnLane = 1;
        net.Zones[1].SpawnLaneCount = 1;

        // Ловушка: односторонний сегмент в новый узел без выезда.
        BuildRoadCmd trap = ToolsFixture.Road(new Vec2(400f, 0f), new Vec2(400f, 100f));
        trap.LanesBA = 0;
        Command c = Command.MakeBuildRoad(in trap);
        Assert.Equal(RejectReason.CreatesTrap, sim.Preview(in c, out PreviewResult r, issues));
        Assert.Equal(RejectReason.CreatesTrap, r.Verdict);
        Assert.Equal(1, r.IssueCount);
        Assert.Equal((IssueCode)(int)RejectReason.CreatesTrap, issues[0].Code);
        Assert.Equal(IssueSeverity.Error, issues[0].Severity);
        Assert.Equal(100f, issues[0].Z);
        Assert.Equal(RejectReason.CreatesTrap, ToolsFixture.Run(sim, in c));

        // Двусторонний тупик - не ловушка (разворот есть); новый свободный конец - Info DeadEnd.
        BuildRoadCmd twoWay = ToolsFixture.Road(new Vec2(400f, 0f), new Vec2(400f, 100f));
        c = Command.MakeBuildRoad(in twoWay);
        Span<Vec2> axis = stackalloc Vec2[4];
        Assert.Equal(RejectReason.None, sim.Preview(in c, out r, issues, axis));
        Assert.Equal(2, r.AxisPointCount);
        Assert.Equal(400f, axis[1].X);
        Assert.Equal(100f, axis[1].Z);
        Assert.Equal(1, r.NewSegments);
        Assert.Equal(1, r.NewNodes);
        Assert.Equal(SnapKind.ExistingNode, r.SnapKindA);
        Assert.Equal(SnapKind.NewNode, r.SnapKindB);
        Assert.Equal(2000L, r.Cost);   // 100 м x 2 полосы x 10
        bool deadEnd = false;
        for (int i = 0; i < r.IssuesWritten; i++)
        {
            deadEnd |= issues[i].Code == IssueCode.DeadEnd && issues[i].Severity == IssueSeverity.Info && issues[i].Z == 100f;
        }

        Assert.True(deadEnd);

        // Односторонний въезд в узел с выездом - не ловушка.
        BuildRoadCmd into = ToolsFixture.Road(new Vec2(200f, 100f), new Vec2(200f, 0f));
        into.LanesBA = 0;
        c = Command.MakeBuildRoad(in into);
        Assert.Equal(RejectReason.None, sim.Preview(in c, out r, issues));

        // Снос моста: зона на дальнем от остальной сети конце теряет доступ (объект и точка в предупреждении).
        int cornerSeg = 1;   // сегмент между узлами 1 и 2 (x = 200..400), зона 1 стоит в x = 400
        Command rm = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, cornerSeg) });
        Assert.Equal(RejectReason.None, sim.Preview(in rm, out r, issues));
        Assert.Equal(2, r.Mechanism);
        Assert.Equal(1, r.ZonesLosingAccess);
        Assert.True(r.Cost < 0, "снос возвращает часть стоимости");
        bool zoneLost = false;
        for (int i = 0; i < r.IssuesWritten; i++)
        {
            if (issues[i].Code == IssueCode.ZoneAccessLost)
            {
                zoneLost = true;
                Assert.Equal((byte)EntityKind.Zone, issues[i].ObjKind);
                Assert.Equal(1, issues[i].Obj.Index);
                Assert.Equal(400f, issues[i].X);
            }
        }

        Assert.True(zoneLost);

        // Кэш компонент: два превью подряд пересчитывают граф один раз, правка сети - ещё раз.
        Connectivity cn = w.ToolScratch.Conn!;
        int before = cn.Rebuilds;
        sim.Preview(in rm, out _, issues);
        sim.Preview(in rm, out _, issues);
        Assert.Equal(before, cn.Rebuilds);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in c));
        sim.Preview(in rm, out _, issues);
        Assert.Equal(before + 1, cn.Rebuilds);
    }

    [Fact]
    public void Preview_Rejections_MatchApply_AndSpecificReasons()
    {
        using Simulation sim = Simulation.Create(TestScenarios.Line());
        sim.Step(5);
        var issues = new PreviewIssue[4];

        // Короче 2 x отступ + мин. длины.
        BuildRoadCmd shortRoad = ToolsFixture.Road(new Vec2(400f, 0f), new Vec2(400f, 12f));
        Command c = Command.MakeBuildRoad(in shortRoad);
        Assert.Equal(RejectReason.TooShort, sim.Preview(in c, out _, issues));
        Assert.Equal(RejectReason.TooShort, ToolsFixture.Run(sim, in c));

        // Вне границ по умолчанию (bbox сети + 2 км).
        BuildRoadCmd far = ToolsFixture.Road(new Vec2(400f, 0f), new Vec2(400f, 9000f));
        c = Command.MakeBuildRoad(in far);
        Assert.Equal(RejectReason.OutOfBounds, sim.Preview(in c, out PreviewResult r, issues));
        Assert.Equal(1, r.IssueCount);
        Assert.Equal(RejectReason.OutOfBounds, ToolsFixture.Run(sim, in c));

        // Явные границы мира.
        sim.SetToolsConfig(new ToolsDesc { HasWorldBounds = true, BoundsMinX = -10f, BoundsMinZ = -10f, BoundsMaxX = 500f, BoundsMaxZ = 50f });
        BuildRoadCmd outside = ToolsFixture.Road(new Vec2(400f, 0f), new Vec2(400f, 90f));
        c = Command.MakeBuildRoad(in outside);
        Assert.Equal(RejectReason.OutOfBounds, sim.Preview(in c, out _, issues));

        // Конец в тот же узел.
        BuildRoadCmd same = ToolsFixture.Road(new Vec2(400f, 0f), new Vec2(402f, 0f));
        c = Command.MakeBuildRoad(in same);
        Assert.Equal(RejectReason.InvalidArgument, sim.Preview(in c, out _, issues));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, in c));

        // Мёртвый handle сегмента / устаревшее поколение.
        Command stale = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(0, ToolsFixture.Segment(sim, 0).Generation + 2) });
        Assert.Equal(RejectReason.InvalidHandle, sim.Preview(in stale, out _, issues));
        Assert.Equal(RejectReason.InvalidHandle, ToolsFixture.Run(sim, in stale));

        // Команда, которой валидатор не знает: как и применение, NotSupported.
        Command trip = Command.MakeSelectVehicle(VehicleHandle.Null);
        Assert.Equal(RejectReason.NotSupported, sim.Preview(in trip, out _, issues));
    }

    [Fact]
    public void Preview_RemoveRoad_Counts_Drain_AndLotsFromTables()
    {
        using Simulation sim = ToolsFixture.Small(600);
        sim.SetToolsConfig(_costs);
        int seg = ToolsFixture.BusiestSegment(sim);
        Assert.True(seg >= 0);
        var issues = new PreviewIssue[16];
        Command rm = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) });
        Assert.Equal(RejectReason.None, sim.Preview(in rm, out PreviewResult r, issues, default, PreviewOptions.Detail));
        Assert.True(r.DrainVehicles > 0, "на самом загруженном сегменте есть машины");
        Assert.True(r.RoutesThrough >= 0);
        bool drain = false;
        for (int i = 0; i < r.IssuesWritten; i++)
        {
            drain |= issues[i].Code == IssueCode.DrainNeeded && issues[i].Value == r.DrainVehicles;
        }

        Assert.True(drain);

        // Без Detail маршруты не считаются.
        sim.Preview(in rm, out r, issues);
        Assert.Equal(-1, r.RoutesThrough);

        // Стоимость сноса = -(стоимость постройки) x возврат; после применения повторный снос - Busy.
        SegmentData sd = sim.World.Net.Segments[seg];
        long build = ToolValidator.RoadCost(_costs.Costs, sd.Kind, sd.Length + (2f * sim.World.Builder.Options.NodeSetback), sd.LanesAB + sd.LanesBA, curve: false);
        Assert.Equal(-(build / 2), r.Cost);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in rm));
        Assert.Equal(RejectReason.Busy, sim.Preview(in rm, out _, issues));
    }

    // ---------------------------------------------------------------- случайные команды

    private static Command RandomCommand(Simulation sim, Random rng, uint tag)
    {
        World w = sim.World;
        NetworkStore n = w.Net;
        int built = 0;
        for (int i = 0; i < n.NodeCount; i++)
        {
            built += n.NodeAlive(i) && n.Nodes[i].FirstArm >= 0 ? 1 : 0;
        }

        int node = -1;
        int pick = rng.Next(Math.Max(1, built));
        for (int i = 0; i < n.NodeCount && node < 0; i++)
        {
            if (n.NodeAlive(i) && n.Nodes[i].FirstArm >= 0 && pick-- == 0)
            {
                node = i;
            }
        }

        int gen = node >= 0 ? n.Nodes[node].Generation : 1;
        int arms = node >= 0 ? n.Nodes[node].ArmCount : 1;
        int kind = rng.Next(100);
        if (kind < 30)
        {
            return RandomBuild(sim, rng, tag, node);
        }

        if (kind < 44)
        {
            int seg = rng.Next(Math.Max(1, n.SegmentCount) + 2);
            int sg = seg < n.SegmentCount ? n.Segments[seg].Generation : 1;
            if (rng.Next(8) == 0)
            {
                sg += 2;   // чужое поколение
            }

            if (rng.Next(6) == 0 && n.SegmentCount > 0)
            {
                seg = BusStopSegmentOrAny(n, rng);
                sg = n.Segments[seg].Generation;
            }

            return Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(seg, sg), Flags = rng.Next(10) == 0 ? 2 : rng.Next(2) }, tag);
        }

        if (kind < 62)
        {
            return Command.MakeSetTurnBan(new SetTurnBanCmd
            {
                Node = new EntityRef(Math.Max(node, 0), rng.Next(12) == 0 ? gen + 2 : gen),
                FromArm = (byte)rng.Next(arms + (rng.Next(10) == 0 ? 3 : 0)),
                ToArm = (byte)rng.Next(arms),
                Banned = (byte)(rng.Next(15) == 0 ? 2 : rng.Next(2)),
            }, tag);
        }

        if (kind < 74)
        {
            return Command.MakeSetArmControl(new SetArmControlCmd
            {
                Node = new EntityRef(Math.Max(node, 0), rng.Next(12) == 0 ? gen + 2 : gen),
                ArmSlot = (byte)rng.Next(arms + (rng.Next(10) == 0 ? 2 : 0)),
                Control = (byte)rng.Next(5),
            }, tag);
        }

        if (kind < 86)
        {
            int seg = rng.Next(Math.Max(1, n.SegmentCount));
            int sg = n.SegmentCount > 0 ? n.Segments[seg].Generation : 1;
            byte bit = rng.Next(2) == 0 ? LaneFlags.BusOnly : LaneFlags.NoLaneChangeOut;
            return Command.MakeSetLaneFlags(new SetLaneFlagsCmd
            {
                Seg = new EntityRef(seg, rng.Next(12) == 0 ? sg + 2 : sg),
                Dir = (byte)rng.Next(3),
                LaneNo = (byte)rng.Next(4),
                Flags = rng.Next(2) == 0 ? bit : (byte)0,
                Mask = rng.Next(10) == 0 ? (byte)(bit | 8) : bit,
            }, tag);
        }

        if (kind < 94)
        {
            return Command.MakeSetNodeControl(new SetNodeControlCmd
            {
                Node = rng.Next(20) == 0 ? n.NodeCount + 3 : Math.Max(node, 0),
                Control = (NodeControl)rng.Next(6),
                NodeGen = rng.Next(12) == 0 ? gen + 2 : gen,
            }, tag);
        }

        return Command.MakeSetSignalPlan(new SetSignalPlanCmd
        {
            Node = Math.Max(node, 0),
            NodeGen = gen,
            Mode = (SignalMode)rng.Next(4),
            PhaseCount = (byte)rng.Next(9),
            Green0 = (ushort)(100 + rng.Next(400)),
            Green1 = (ushort)(100 + rng.Next(400)),
            YellowTicks = 60,
            AllRedTicks = 40,
        }, tag);
    }

    private static int BusStopSegmentOrAny(NetworkStore n, Random rng)
    {
        for (int i = 0; i < n.BusStopCount; i++)
        {
            if (n.BusStopAlive(i) && n.BusStops[i].Lane >= 0)
            {
                return n.Lanes[n.BusStops[i].Lane].Segment;
            }
        }

        return rng.Next(n.SegmentCount);
    }

    // Дорога из узла под случайным углом; часть - заведомо плохие (короткие, вне границ, нулевые полосы, односторонние в никуда, NaN, отмена сноса).
    private static Command RandomBuild(Simulation sim, Random rng, uint tag, int node)
    {
        NetworkStore n = sim.World.Net;
        Vec2 p = node >= 0 ? n.Nodes[node].Position : default;
        float ang = rng.Next(8) * (MathF.PI / 4f);
        float len = 60f + rng.Next(80);
        var q = new Vec2(p.X + (MathF.Cos(ang) * len), p.Z + (MathF.Sin(ang) * len));
        BuildRoadCmd b = ToolsFixture.Road(p, q, 1 + rng.Next(2));
        switch (rng.Next(14))
        {
            case 0:
                b.Bx = p.X + 4f;
                b.Bz = p.Z + (rng.Next(20));
                break;
            case 1:
                b.Bx = p.X + 9000f;
                break;
            case 2:
                b.LanesAB = 0;
                b.LanesBA = 0;
                break;
            case 3:
                b.LanesBA = 0;
                break;
            case 4:
                b.Bx = float.NaN;
                break;
            case 5:
                b.SpeedLimit = 0f;
                break;
            case 6:
                b.LanesAB = 12;
                break;
            case 7:
                int rs = rng.Next(Math.Max(1, n.SegmentCount));   // случайный handle (мёртвый - пересоздание сегмента в новом слоте, T8; живой не в дренаже - InvalidHandle); регрессия: кэш NextConn после перестройки узла
                b.RestoreOf = new EntityRef(rs, 1 + (2 * rng.Next(3)));
                break;
            case 8:
                b.Flags = BuildRoadFlags.AutoSplit;
                break;
            case 9:
                b.LanesAB = 0;   // односторонний B -> A
                break;
        }

        if (!FreeOfCrossings(n, node, p, new Vec2(b.Bx, b.Bz)))
        {
            b.Bx = p.X;
            b.Bz = p.Z;   // пересекает чужое: заменяем вырожденной (отказ), чтобы не плодить пересечения без узлов
        }

        return Command.MakeBuildRoad(in b, tag);
    }

    private static bool FreeOfCrossings(NetworkStore n, int node, Vec2 p, Vec2 q)
    {
        if (!float.IsFinite(q.X) || !float.IsFinite(q.Z))
        {
            return true;
        }

        for (int i = 0; i < n.NodeCount; i++)
        {
            if (i != node && n.NodeAlive(i) && Dist(n.Nodes[i].Position, q) < 14f && Dist(n.Nodes[i].Position, q) > 0.5f)
            {
                return false;
            }
        }

        Vec2 d = q - p;
        float dl = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        if (dl < 1f)
        {
            return true;
        }

        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            SegmentData sd = n.Segments[s];
            Vec2 a = n.Nodes[sd.NodeA].Position;
            Vec2 b = n.Nodes[sd.NodeB].Position;
            if (sd.NodeA == node || sd.NodeB == node)
            {
                Vec2 far = (sd.NodeA == node ? b : a) - p;
                float fl = MathF.Sqrt((far.X * far.X) + (far.Z * far.Z));
                if (((far.X * d.X) + (far.Z * d.Z)) / (fl * dl) > 0.9f)
                {
                    return false;
                }

                continue;
            }

            if (Crosses(p, q, a, b) || Dist(Closest(a, b, q), q) < 14f)
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

    private static bool Crosses(Vec2 p, Vec2 q, Vec2 a, Vec2 b) => Side(p, q, a) * Side(p, q, b) < 0f && Side(a, b, p) * Side(a, b, q) < 0f;

    private static float Side(Vec2 a, Vec2 b, Vec2 c) => ((b.X - a.X) * (c.Z - a.Z)) - ((b.Z - a.Z) * (c.X - a.X));
}
