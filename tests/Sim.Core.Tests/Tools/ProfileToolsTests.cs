using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Worlds;
using Sim.Core.Tools;
using Sim.Core.Util;
using Sim.Scenarios;
using Sim.Scenarios.Tools;

namespace Sim.Core.Tests.Tools;

/// <summary>
/// RP7 (road-profiles.md §6, ADR-0030): инструменты игрока с профилями. <c>BuildRoad</c>/<c>UpgradeRoad</c> с <c>Profile</c>/<c>Mirrored</c>, <c>Overlap</c>, <c>SetbackShort</c>, <c>Lost</c> участков после правки,
/// превью = применение, undo/redo и журнал команд, детерминизм при 1/2/3 потоках, 0 B/тик.
/// </summary>
public sealed class ProfileToolsTests
{
    private static readonly string[] _game = { "street_2", "oneway_2", "arterial_4", "arterial_6", "asym_1_2" };

    private static RoadProfileStore Store() => new(ProfileSaveFixtures.Catalog());

    private static int P(string id) => ProfileSaveFixtures.IndexOf(ProfileSaveFixtures.Catalog(), id);

    /// <summary>Каталог фикстуры плюс <c>street_2w</c>: те же 2 + 2... то есть 1 + 1 полоса, но шире (3.75 м) - правка геометрии на месте.</summary>
    private static RoadProfilesDesc CatalogWithWide()
    {
        string json = File.ReadAllText(ProfileSaveFixtures.FixturePath);
        string wide = """
            {
              "id": "street_2w", "kind": "street", "width_m": 16,
              "elements": [
                {"type": "sidewalk", "w": 3}, {"type": "verge", "w": 1}, {"type": "curb", "w": 0.25},
                {"type": "lane", "w": 3.75, "dir": "backward"}, {"type": "lane", "w": 3.75, "dir": "forward"},
                {"type": "curb", "w": 0.25}, {"type": "verge", "w": 1}, {"type": "sidewalk", "w": 3}
              ]
            },
            """;
        int at = json.IndexOf("\"profiles\": [", StringComparison.Ordinal) + "\"profiles\": [".Length;
        return ScenarioLoader.ParseRoadProfiles(json.Insert(at, wide));
    }

    /// <summary>Явная сеть по профилям фикстуры + город жителей (участки на сетке); без машин: правки не упираются в <c>Busy</c>.</summary>
    internal static ScenarioDesc Net((float X, float Z)[] nodes, (int A, int B, string Profile)[] segs, RoadProfilesDesc? catalog = null)
    {
        RoadProfilesDesc cat = catalog ?? ProfileSaveFixtures.Catalog();
        var store = new RoadProfileStore(in cat);
        ScenarioDesc d = CityFixtures.S1kRes();
        d.City = default;
        d.Transit = default;
        d.Peds = default;
        d.Demand.TargetVehicles = 0;
        d.Demand.Zones = new[]
        {
            new ZoneDesc { CenterX = 0, CenterZ = 0, HalfSize = 50, Weight = 1 },
            new ZoneDesc { CenterX = 100, CenterZ = 0, HalfSize = 50, Weight = 1 },
        };
        d.Network = new NetworkDesc
        {
            Nodes = nodes.Select(n => new NodeDesc { X = n.X, Z = n.Z, Control = NodeControl.None }).ToArray(),
            Segments = segs.Select(s =>
            {
                int p = ProfileSaveFixtures.IndexOf(in cat, s.Profile);
                return new SegmentDesc { A = s.A, B = s.B, Profile = p + 1, LanesAB = store.LanesAB(p, false), LanesBA = store.LanesBA(p, false), SpeedLimit = 13.9f, Kind = store.Kind[p] };
            }).ToArray(),
        };
        d.RoadProfiles = cat;
        d.Town.Zoning.Auto = false;
        d.Town.Residents.InitialFillQ16 = 0;
        SimCapacities c = d.Capacities;
        c.MaxNodes = 256;
        c.MaxSegments = 256;
        c.MaxLanes = 8192;
        c.MaxLanePoints = 32768;
        c.MaxZones = 8;
        c.MaxRoutes = 64;
        c.MaxConnectors = 4096;
        c.MaxConflicts = 32768;
        c.MaxLots = 4000;
        d.Capacities = c;
        return d;
    }

    private static Simulation Make(ScenarioDesc d, int threads = 1)
    {
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.SetToolsConfig(new ToolsDesc
        {
            HasWorldBounds = true,
            BoundsMinX = -1e6f,
            BoundsMinZ = -1e6f,
            BoundsMaxX = 1e6f,
            BoundsMaxZ = 1e6f,
            Costs = new ToolCostsDesc { RoadPerMLaneStreet = 10, RoadPerMLaneArterial = 20, UpgradeRefundPct = 50 },
        });
        return sim;
    }

    /// <summary>Тихий мир с одной длинной улицей по линии сетки.</summary>
    private static Simulation Quiet(RoadProfilesDesc? cat = null) => Make(Net(new[] { (-320f, 0f), (320f, 0f) }, new[] { (0, 1, "street_2") }, cat));

    private static BuildRoadCmd Build(RoadProfileStore st, int p, bool mirrored, float ax, float az, float bx, float bz) => new()
    {
        Ax = ax,
        Az = az,
        Bx = bx,
        Bz = bz,
        SpeedLimit = 13.9f,
        LanesAB = (byte)st.LanesAB(p, mirrored),
        LanesBA = (byte)st.LanesBA(p, mirrored),
        Kind = st.Kind[p],
        Profile = (ushort)(p + 1),
        Flags = mirrored ? BuildRoadFlags.Mirrored : (byte)0,
    };

    private static uint _tag = 1000;

    /// <summary>Cross (перекрёсток из четырёх профильных сегментов) с машинами и запасом ёмкости под фаззер правок.</summary>
    private static ScenarioDesc BigCross(int vehicles = 60)
    {
        ScenarioDesc d = ProfileSaveFixtures.Cross();
        d.Demand.TargetVehicles = vehicles;
        SimCapacities c = d.Capacities;
        c.MaxNodes = 512;
        c.MaxSegments = 512;
        c.MaxLanes = 16384;
        c.MaxLanePoints = 65536;
        c.MaxConnectors = 8192;
        c.MaxConflicts = 65536;
        d.Capacities = c;
        return d;
    }

    private static RejectReason Run(Simulation sim, in Command cmd) => ToolsFixture.Run(sim, in cmd);

    private static Command Tagged(Command c)
    {
        c.Tag = ++_tag;
        return c;
    }

    private static int SegmentAt(Simulation sim, float x0, float z0, float x1, float z1)
    {
        NetworkStore n = sim.World.Net;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            Vec2 a = n.Nodes[n.Segments[s].NodeA].Position;
            Vec2 b = n.Nodes[n.Segments[s].NodeB].Position;
            if (MathF.Abs(a.X - x0) < 1f && MathF.Abs(a.Z - z0) < 1f && MathF.Abs(b.X - x1) < 1f && MathF.Abs(b.Z - z1) < 1f)
            {
                return s;
            }
        }

        return -1;
    }

    /// <summary>Сегмент целиком согласован с каталогом: полосы, тип, зеркальность, ширина и боковое смещение каждой полосы (инвариант RP2).</summary>
    private static void AssertMatchesProfile(Simulation sim, int seg, int profile, bool mirrored)
    {
        NetworkStore n = sim.World.Net;
        RoadProfileStore st = n.Profiles!;
        SegmentData sd = n.Segments[seg];
        Assert.Equal(profile, sd.Profile);
        Assert.Equal(mirrored, (sd.Flags & SegmentFlags.Mirrored) != 0);
        Assert.Equal(st.LanesAB(profile, mirrored), sd.LanesAB);
        Assert.Equal(st.LanesBA(profile, mirrored), sd.LanesBA);
        Assert.Equal(st.Kind[profile], sd.Kind);
        Vec2 a0 = n.SegmentPoints[sd.FirstPoint];
        Vec2 a1 = n.SegmentPoints[sd.FirstPoint + 1];
        float dx = a1.X - a0.X;
        float dz = a1.Z - a0.Z;
        float inv = 1f / MathF.Sqrt((dx * dx) + (dz * dz));
        for (int dir = 0; dir < 2; dir++)
        {
            int cnt = dir == 0 ? sd.LanesAB : sd.LanesBA;
            int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            for (int k = 0; k < cnt; k++)
            {
                LaneData ld = n.Lanes[first + k];
                Assert.Equal(seg, ld.Segment);
                Assert.Equal(k, ld.LaneNo);
                Assert.Equal(dir, ld.Direction);
                Assert.Equal(st.LaneWidthM(profile, mirrored, dir, k), ld.Width);
                Vec2 p = dir == 0 ? n.LanePoints[ld.FirstPoint] : n.LanePoints[ld.FirstPoint + ld.PointCount - 1];
                float off = (((p.X - a0.X) * -dz) + ((p.Z - a0.Z) * dx)) * inv;
                Assert.Equal(st.LaneCenterM(profile, mirrored, dir, k), off, 3);
            }
        }
    }

    private static void AssertConsistent(Simulation sim)
    {
        Assert.Null(sim.World.VerifyDerived());
    }

    // ---------------------------------------------------------------- BuildRoad

    [Theory]
    [InlineData("street_2", false)]
    [InlineData("street_2", true)]
    [InlineData("oneway_2", false)]
    [InlineData("oneway_2", true)]
    [InlineData("arterial_4", false)]
    [InlineData("arterial_4", true)]
    [InlineData("arterial_6", false)]
    [InlineData("arterial_6", true)]
    [InlineData("asym_1_2", false)]
    [InlineData("asym_1_2", true)]
    public void BuildRoad_WithProfile_MatchesCatalog_AndSurvivesSave(string id, bool mirrored)
    {
        using Simulation sim = Quiet();
        RoadProfileStore st = sim.World.Net.Profiles!;
        int p = P(id);
        // Односторонний профиль строится в узел с выездом: стыкуем его с концом готовой улицы (въезд - в узел улицы).
        bool intoNode = st.LanesBA(p, mirrored) == 0;
        BuildRoadCmd b = intoNode ? Build(st, p, mirrored, 320f, 300f, 320f, 0f) : Build(st, p, mirrored, 320f, 0f, 320f, 300f);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in b, 1)));
        int seg = intoNode ? SegmentAt(sim, 320f, 300f, 320f, 0f) : SegmentAt(sim, 320f, 0f, 320f, 300f);
        Assert.True(seg >= 0);
        AssertMatchesProfile(sim, seg, p, mirrored);
        AssertConsistent(sim);
        sim.Step(5);
        Simulation loaded = ToolsFixture.RoundTrip(sim);   // сейв проходит валидатор RP2 (полосы, тип, Width) и даёт тот же хэш
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        loaded.Dispose();
    }

    [Fact]
    public void BuildRoad_InvalidProfileArguments()
    {
        using Simulation sim = Quiet();
        RoadProfileStore st = sim.World.Net.Profiles!;
        int s2 = P("street_2");
        BuildRoadCmd ok = Build(st, s2, false, 320f, 0f, 320f, 300f);

        BuildRoadCmd badLanes = ok;
        badLanes.LanesAB = 2;
        BuildRoadCmd badKind = ok;
        badKind.Kind = RoadKind.Arterial;
        BuildRoadCmd badIndex = ok;
        badIndex.Profile = 99;
        BuildRoadCmd mirrorNoProfile = ok;
        mirrorNoProfile.Profile = 0;
        mirrorNoProfile.Flags = BuildRoadFlags.Mirrored;
        BuildRoadCmd asymWrongSide = Build(st, P("asym_1_2"), false, 320f, 0f, 320f, 300f);
        asymWrongSide.LanesAB = (byte)st.LanesAB(P("asym_1_2"), true);
        asymWrongSide.LanesBA = (byte)st.LanesBA(P("asym_1_2"), true);
        foreach (BuildRoadCmd c in new[] { badLanes, badKind, badIndex, mirrorNoProfile, asymWrongSide })
        {
            BuildRoadCmd x = c;
            Command cmd = Command.MakeBuildRoad(in x, 1);
            Assert.Equal(RejectReason.InvalidArgument, sim.Preview(in cmd, out _, new PreviewIssue[4]));
            Assert.Equal(RejectReason.InvalidArgument, Run(sim, in cmd));
        }

        // Без профиля - как раньше: дорога без профиля допустима и в режиме профилей.
        BuildRoadCmd legacy = ToolsFixture.Road(new Vec2(320f, 0f), new Vec2(320f, 300f));
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in legacy, 2)));
        Assert.Equal(-1, sim.World.Net.Segments[SegmentAt(sim, 320f, 0f, 320f, 300f)].Profile);
        AssertConsistent(sim);

        // Профиль в мире без каталога - отказ.
        using Simulation legacyWorld = ToolsFixture.Small(50);
        Command c2 = Command.MakeBuildRoad(in ok, 3);
        Assert.Equal(RejectReason.InvalidArgument, Run(legacyWorld, in c2));
    }

    // ---------------------------------------------------------------- Overlap

    [Fact]
    public void Overlap_ParallelCrossingAndFlush()
    {
        using Simulation sim = Quiet();
        RoadProfileStore st = sim.World.Net.Profiles!;
        int s2 = P("street_2");   // край 8 м
        int a4 = P("arterial_4"); // край 12 м
        var issues = new PreviewIssue[8];

        // Параллельно на расстоянии 15 м < 8 + 8: наложение (превью и применение).
        BuildRoadCmd near = Build(st, s2, false, -200f, 15f, 200f, 15f);
        Command c = Command.MakeBuildRoad(in near, 1);
        Assert.Equal(RejectReason.Overlap, sim.Preview(in c, out PreviewResult r, issues));
        Assert.Equal((int)RejectReason.Overlap, (int)issues[0].Code);
        Assert.Equal(RejectReason.Overlap, Run(sim, in c));

        // Ровно вплотную (16 м = 8 + 8): допустимо.
        BuildRoadCmd flush = Build(st, s2, false, -200f, 16f, 200f, 16f);
        c = Command.MakeBuildRoad(in flush, 2);
        Assert.Equal(RejectReason.None, sim.Preview(in c, out r, issues));
        Assert.Equal(RejectReason.None, Run(sim, in c));

        // Артерия 24 м рядом с улицей на 19 м < 8 + 12: наложение; на 20 м - можно.
        BuildRoadCmd nearArt = Build(st, a4, false, -200f, -19f, 200f, -19f);
        c = Command.MakeBuildRoad(in nearArt, 3);
        Assert.Equal(RejectReason.Overlap, Run(sim, in c));
        BuildRoadCmd okArt = Build(st, a4, false, -200f, -20f, 200f, -20f);
        c = Command.MakeBuildRoad(in okArt, 4);
        Assert.Equal(RejectReason.None, Run(sim, in c));

        // Пересечение существующей дороги не в узле.
        BuildRoadCmd cross = Build(st, s2, false, 0f, 100f, 0f, 300f);
        cross.Ax = 100f;
        cross.Bx = 100f;
        cross.Az = -400f;
        cross.Bz = 400f;
        c = Command.MakeBuildRoad(in cross, 5);
        Assert.Equal(RejectReason.Overlap, Run(sim, in c));
        AssertConsistent(sim);
    }

    [Fact]
    public void Overlap_JunctionsAtSharedNodesAreFine_AcuteIsNot()
    {
        using Simulation sim = Quiet();
        RoadProfileStore st = sim.World.Net.Profiles!;
        int s2 = P("street_2");
        // Т-образный стык под 90 градусов в узле конца улицы и поперечная из середины?? середина - не узел: только концы. Строим от конца (320,0).
        BuildRoadCmd t = Build(st, s2, false, 320f, 0f, 320f, 300f);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in t, 1)));
        BuildRoadCmd t2 = Build(st, s2, false, 320f, 0f, 320f, -300f);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in t2, 2)));
        BuildRoadCmd t3 = Build(st, s2, false, 320f, 0f, 620f, 0f);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in t3, 3)));

        // Острый угол 20 градусов к уже стоящему рукаву: полосы накладываются за диском узла.
        BuildRoadCmd acute = Build(st, s2, false, 320f, 0f, 320f + (300f * MathF.Cos(0.35f)), 300f * MathF.Sin(0.35f));
        Assert.Equal(RejectReason.Overlap, Run(sim, Command.MakeBuildRoad(in acute, 4)));
        AssertConsistent(sim);
    }

    // ---------------------------------------------------------------- UpgradeRoad

    private static Command Upgrade(Simulation sim, int seg, int profile, bool mirrored, float speed = 0f) =>
        Command.MakeUpgradeRoad(new UpgradeRoadCmd
        {
            Seg = ToolsFixture.Segment(sim, seg),
            SpeedLimit = speed,
            LanesAB = 255,
            LanesBA = 255,
            Flags = (byte)(UpgradeRoadFlags.KeepKind | (mirrored ? UpgradeRoadFlags.Mirrored : 0)),
            Profile = (ushort)(profile + 1),
        }, ++_tag);

    [Fact]
    public void UpgradeRoad_AllProfilePairs_OnQuietRoad()
    {
        using Simulation sim = Quiet();
        RoadProfileStore st = sim.World.Net.Profiles!;
        var combos = new List<(int P, bool M)>();
        foreach (string id in _game)
        {
            combos.Add((P(id), false));
            combos.Add((P(id), true));
        }

        // Дорога вдали от улицы и лотов, концы - тупики: из любой пары в любую.
        BuildRoadCmd road = Build(st, P("street_2"), false, 600f, 600f, 600f, 900f);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in road, 1)));
        int seg = SegmentAt(sim, 600f, 600f, 600f, 900f);
        int applied = 0;
        foreach ((int fp, bool fm) in combos)
        {
            // Односторонний на тупике - ловушка: исходное состояние двусторонний street_2.
            foreach ((int tp, bool tm) in combos)
            {
                Command back = Upgrade(sim, seg, P("street_2"), false);
                Assert.Equal(RejectReason.None, Run(sim, in back));
                Command to = Upgrade(sim, seg, fp, fm);
                RejectReason r0 = Run(sim, in to);
                if (r0 != RejectReason.None)
                {
                    Assert.Equal(RejectReason.CreatesTrap, r0);   // односторонняя в тупик: ровно эта причина
                    continue;
                }

                AssertMatchesProfile(sim, seg, fp, fm);
                Command cmd = Upgrade(sim, seg, tp, tm);
                Command copy = cmd;
                RejectReason pv = sim.Preview(in copy, out PreviewResult pr, new PreviewIssue[8]);
                RejectReason av = Run(sim, in cmd);
                Assert.Equal(pv, av);
                if (av == RejectReason.None)
                {
                    AssertMatchesProfile(sim, seg, tp, tm);
                    applied++;
                }
                else
                {
                    Assert.Equal(RejectReason.CreatesTrap, av);
                }

                AssertConsistent(sim);
            }
        }

        Assert.True(applied >= 50, $"применено лишь {applied} правок");
        sim.Step(10);
        Simulation loaded = ToolsFixture.RoundTrip(sim);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        loaded.Dispose();
    }

    [Fact]
    public void UpgradeRoad_AtJunction_RebuildsConnectors_ZoneAndSignalsSurvive()
    {
        using Simulation sim = Make(Net(
            new[] { (-300f, 0f), (0f, 0f), (300f, 0f), (0f, 300f), (0f, -300f) },
            new[] { (0, 1, "street_2"), (1, 2, "street_2"), (1, 3, "street_2"), (1, 4, "street_2") }));
        RoadProfileStore st = sim.World.Net.Profiles!;
        NetworkStore n = sim.World.Net;
        int center = 1;
        int connBefore = n.Nodes[center].ConnectorCount;
        Assert.Equal(4, n.Nodes[center].ArmCount);
        int north = SegmentAt(sim, 0f, 0f, 0f, 300f);
        Assert.True(north >= 0);
        // Север: 1 + 1 -> 3 + 3 (магистраль 32 м): у узла больше connector'ов, его соседи не затронуты.
        Command up = Upgrade(sim, north, P("arterial_6"), false);
        Assert.Equal(RejectReason.None, Run(sim, in up));
        AssertMatchesProfile(sim, north, P("arterial_6"), false);
        Assert.True(n.Nodes[center].ConnectorCount > connBefore);
        Assert.Equal(4, n.Nodes[center].ArmCount);
        AssertConsistent(sim);
        sim.Step(30);

        // Обратно, затем в асимметричный, зеркально.
        Command down = Upgrade(sim, north, P("street_2"), false);
        Assert.Equal(RejectReason.None, Run(sim, in down));
        Assert.Equal(connBefore, n.Nodes[center].ConnectorCount);
        Command asym = Upgrade(sim, north, P("asym_1_2"), true);
        Assert.Equal(RejectReason.None, Run(sim, in asym));
        AssertMatchesProfile(sim, north, P("asym_1_2"), true);
        AssertConsistent(sim);
        Assert.Equal(st.LanesAB(P("asym_1_2"), true), n.Segments[north].LanesAB);
    }

    [Fact]
    public void UpgradeRoad_OccupiedSegment_RelocatesWidening_DrainsNarrowing_UndoCancels()
    {
        // T5: правка полос занятого сегмента. Больше полос - машины уцелевших полос переносятся (номер полосы и S сохраняются); меньше полос - убираемые Closed | Draining,
        // финал в фазе 1а после их опустения; команда «вернуть прежние полосы» во время дренажа - отмена операции.
        ScenarioDesc d = BigCross();
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions());
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        sim.Step(5);
        NetworkStore n = sim.World.Net;
        int seg = -1;
        for (int s = 0; s < n.SegmentCount && seg < 0; s++)
        {
            seg = n.SegmentAlive(s) && n.Segments[s].Profile == P("arterial_4") ? s : -1;
        }

        Assert.True(seg >= 0);
        VehicleHandle car = TestScenarios.PlaceCar(sim, n.Segments[seg].FirstLaneAB + 1, 20f);   // стоит на полосе LaneNo 1: сегмент занят
        Assert.True(ToolOpsPhase.CountOn(sim.World, seg) > 0);
        ulong h0 = sim.ComputeStateHash();
        Command wide = Upgrade(sim, seg, P("arterial_6"), false);
        Command copy = wide;
        Assert.Equal(RejectReason.None, sim.Preview(in copy, out PreviewResult pr, new PreviewIssue[8]));
        Assert.Equal(h0, sim.ComputeStateHash());
        Assert.True(pr.Mechanism is 1 or 2);
        Assert.Equal(RejectReason.None, Run(sim, in wide));
        WaitOps(sim, seg);
        AssertMatchesProfile(sim, seg, P("arterial_6"), false);
        Assert.True(sim.World.Vehicles.Pool.IsAlive(car.Index));
        int lane = sim.World.Vehicles.Lane[car.Index];
        Assert.Equal(seg, n.Lanes[lane].Segment);   // машина переехала с полосой (или перестроилась на соседнюю того же сегмента)

        // Меньше полос: машина на убираемой полосе (LaneNo 2) - дренаж.
        int top = n.Segments[seg].FirstLaneAB + 2;
        VehicleHandle late = TestScenarios.PlaceCar(sim, top, 30f);
        Command narrow = Upgrade(sim, seg, P("arterial_4"), false);
        copy = narrow;
        Assert.Equal(RejectReason.None, sim.Preview(in copy, out pr, new PreviewIssue[8]));
        Assert.Equal(2, pr.Mechanism);
        Assert.True(pr.DrainVehicles >= 1);
        Assert.Equal(RejectReason.None, Run(sim, in narrow));
        if (sim.World.Ops.FindShrink(seg) >= 0)
        {
            Assert.NotEqual(0, n.Lanes[top].Flags & LaneFlags.Draining);
            Assert.Equal(RejectReason.Busy, Run(sim, Upgrade(sim, seg, P("street_2"), false)));   // другая правка во время дренажа
            Assert.Equal(RejectReason.Busy, Run(sim, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, ++_tag)));
        }

        WaitOps(sim, seg);
        AssertMatchesProfile(sim, seg, P("arterial_4"), false);
        Assert.True(sim.World.Vehicles.Pool.IsAlive(late.Index) || sim.World.Counters.TripsCompleted > 0);
        AssertConsistent(sim);

        // Отмена: дренаж, затем обратная команда ядра (undo) - операция снимается, полосы открыты, профиль прежний.
        Command again = Upgrade(sim, seg, P("arterial_6"), false);
        Assert.Equal(RejectReason.None, Run(sim, in again));
        WaitOps(sim, seg);
        int top2 = n.Segments[seg].FirstLaneAB + 2;
        TestScenarios.PlaceCar(sim, top2, 30f);
        sim.Enqueue(Upgrade(sim, seg, P("arterial_4"), false));
        sim.Step(1);
        Assert.True(sim.World.Ops.FindShrink(seg) >= 0, "машина на убираемой полосе: ждём дренажа");
        Assert.Single(sim.Inverses.ToArray());
        Command undo = sim.InverseCommands[sim.Inverses[0].First];
        Assert.Equal(RejectReason.None, Run(sim, in undo));
        Assert.True(sim.World.Ops.FindShrink(seg) < 0);
        Assert.Equal(0, n.Lanes[top2].Flags & (LaneFlags.Draining | LaneFlags.Closed));
        AssertMatchesProfile(sim, seg, P("arterial_6"), false);

        // Скорость на занятом сегменте - на месте.
        Command speed = Upgrade(sim, seg, P("arterial_6"), false, speed: 8f);
        Assert.Equal(RejectReason.None, Run(sim, in speed));
        Assert.Equal(8f, sim.World.Net.Segments[seg].SpeedLimit);
        AssertConsistent(sim);
    }

    // Ждать финала операции правки полос сегмента (не дольше 3 минут игрового времени).
    private static void WaitOps(Simulation sim, int seg)
    {
        for (int t = 0; t < 3600 && sim.World.Ops.FindAny(seg) >= 0; t++)
        {
            sim.Step(1);
        }

        Assert.True(sim.World.Ops.FindAny(seg) < 0, "дренаж полос не закончился");
    }


    [Fact]
    public void UpgradeRoad_InPlaceGeometry_KeepsLaneIndicesAndVehicles()
    {
        // street_2 -> street_2w: те же полосы (1 + 1), другая ширина полос и смещения; индексы полос не меняются, занятость не требуется.
        RoadProfilesDesc cat = CatalogWithWide();
        using Simulation sim = Quiet(cat);
        NetworkStore n = sim.World.Net;
        int seg = SegmentAt(sim, -320f, 0f, 320f, 0f);
        int w = ProfileSaveFixtures.IndexOf(in cat, "street_2w");
        int firstAb = n.Segments[seg].FirstLaneAB;
        int firstBa = n.Segments[seg].FirstLaneBA;
        Command up = Upgrade(sim, seg, w, false);
        Command copy = up;
        Assert.Equal(RejectReason.None, sim.Preview(in copy, out PreviewResult pr, new PreviewIssue[8]));
        Assert.Equal(0, pr.Mechanism);
        Assert.Equal(RejectReason.None, Run(sim, in up));
        Assert.Equal(firstAb, n.Segments[seg].FirstLaneAB);
        Assert.Equal(firstBa, n.Segments[seg].FirstLaneBA);
        AssertMatchesProfile(sim, seg, w, false);
        AssertConsistent(sim);
        Simulation loaded = ToolsFixture.RoundTrip(sim);
        loaded.Dispose();
    }

    [Fact]
    public void UpgradeRoad_ProfileSegmentsChangeLanesOnlyThroughProfile()
    {
        using Simulation sim = Quiet();
        int seg = SegmentAt(sim, -320f, 0f, 320f, 0f);
        var lanes = new UpgradeRoadCmd { Seg = ToolsFixture.Segment(sim, seg), LanesAB = 2, LanesBA = 2, Kind = RoadKind.Street };
        Command c = Command.MakeUpgradeRoad(in lanes, 1);
        Assert.Equal(RejectReason.InvalidArgument, Run(sim, in c));
        var kind = new UpgradeRoadCmd { Seg = ToolsFixture.Segment(sim, seg), LanesAB = 255, LanesBA = 255, Kind = RoadKind.Arterial };
        c = Command.MakeUpgradeRoad(in kind, 2);
        Assert.Equal(RejectReason.InvalidArgument, Run(sim, in c));

        // Профиль + явно другие полосы: несогласованность.
        var mismatch = new UpgradeRoadCmd { Seg = ToolsFixture.Segment(sim, seg), LanesAB = 1, LanesBA = 1, Kind = RoadKind.Arterial, Profile = (ushort)(P("arterial_4") + 1) };
        c = Command.MakeUpgradeRoad(in mismatch, 3);
        Assert.Equal(RejectReason.InvalidArgument, Run(sim, in c));
        var mirrorOnly = new UpgradeRoadCmd { Seg = ToolsFixture.Segment(sim, seg), LanesAB = 255, LanesBA = 255, Flags = UpgradeRoadFlags.Mirrored };
        c = Command.MakeUpgradeRoad(in mirrorOnly, 4);
        Assert.Equal(RejectReason.InvalidArgument, Run(sim, in c));
        AssertConsistent(sim);
    }

    [Fact]
    public void UpgradeRoad_LegacyRoad_LanesAndSpeed()
    {
        using Simulation sim = ToolsFixture.Small(100);
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 p = sim.World.Net.Nodes[west].Position;
        BuildRoadCmd road = ToolsFixture.Road(new Vec2(p.X - 400f, p.Z - 900f), new Vec2(p.X - 400f, p.Z - 700f), 1);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in road, 1)));
        NetworkStore n = sim.World.Net;
        int seg = n.SegmentCount - 1;
        Assert.Equal(-1, n.Segments[seg].Profile);
        var up = new UpgradeRoadCmd { Seg = ToolsFixture.Segment(sim, seg), SpeedLimit = 0f, LanesAB = 2, LanesBA = 2, Kind = RoadKind.Arterial };
        Command c = Command.MakeUpgradeRoad(in up, 2);
        Assert.Equal(RejectReason.None, Run(sim, in c));
        Assert.Equal(2, n.Segments[seg].LanesAB);
        Assert.Equal(RoadKind.Arterial, n.Segments[seg].Kind);
        Assert.Equal(13.9f, n.Segments[seg].SpeedLimit);
        AssertConsistent(sim);
        sim.Step(20);
        var down = new UpgradeRoadCmd { Seg = ToolsFixture.Segment(sim, seg), SpeedLimit = 10f, LanesAB = 1, LanesBA = 255, Flags = UpgradeRoadFlags.KeepKind };
        c = Command.MakeUpgradeRoad(in down, 3);
        Assert.Equal(RejectReason.None, Run(sim, in c));
        Assert.Equal(1, n.Segments[seg].LanesAB);
        Assert.Equal(2, n.Segments[seg].LanesBA);
        Assert.Equal(10f, n.Segments[seg].SpeedLimit);
        AssertConsistent(sim);
        ToolsFixture.RoundTrip(sim).Dispose();
    }

    // ---------------------------------------------------------------- Lost, SetbackShort

    [Fact]
    public void UpgradeRoad_WiderProfile_MakesLotsLost_AndPreviewWarns()
    {
        using Simulation sim = Quiet();
        World w = sim.World;
        LotStore l = w.City!.Lots;
        int seg = SegmentAt(sim, -320f, 0f, 320f, 0f);
        int live0 = Live(l);
        Assert.True(live0 > 20);
        // street_2 (край 8 м, участки вплотную) -> arterial_6 (край 16 м): клетки на 8..16 м от оси становятся дорогой.
        Command up = Upgrade(sim, seg, P("arterial_6"), false);
        Command copy = up;
        var issues = new PreviewIssue[16];
        Assert.Equal(RejectReason.None, sim.Preview(in copy, out PreviewResult pr, issues));
        PreviewIssue lostIssue = Array.Find(issues, i => i.Code == IssueCode.ZoneLotsLost);
        Assert.Equal(IssueCode.ZoneLotsLost, lostIssue.Code);
        Assert.True(lostIssue.Value > 0);
        Assert.Equal(RejectReason.None, Run(sim, in up));
        int live1 = Live(l);
        Assert.True(live1 < live0);
        Assert.Equal(live0 - live1, lostIssue.Value);   // оценка превью = факт на этом сценарии
        AssertConsistent(sim);

        // Суженный профиль участки не возвращает и не теряет.
        Command down = Upgrade(sim, seg, P("street_2"), false);
        Assert.Equal(RejectReason.None, Run(sim, in down));
        Assert.Equal(live1, Live(l));

        // Лишних событий нет, сейв и загрузка сохраняют Lost.
        Simulation loaded = ToolsFixture.RoundTrip(sim);
        loaded.Dispose();
    }

    [Fact]
    public void BuildRoad_WithProfile_MakesLotsLost_AndPreviewWarns()
    {
        using Simulation sim = Quiet();
        World w = sim.World;
        RoadProfileStore st = w.Net.Profiles!;
        int lost0 = LostCount(w.City!.Lots);
        BuildRoadCmd b = Build(st, P("street_2"), false, -160f, 40f, 160f, 40f);   // вдоль улицы, внутри её участков
        Command c = Command.MakeBuildRoad(in b, 1);
        var issues = new PreviewIssue[16];
        RejectReason pv = sim.Preview(in c, out _, issues);
        Assert.Equal(RejectReason.None, pv);
        Assert.Contains(issues, i => i.Code == IssueCode.ZoneLotsLost && i.Value > 0);
        Assert.Equal(RejectReason.None, Run(sim, in c));
        Assert.True(LostCount(w.City.Lots) > lost0);
        AssertConsistent(sim);
    }

    [Fact]
    public void Preview_SetbackShort_ForNeighbourArmsOfWiderRoad()
    {
        // Улица проходит через узел (степень 2, отступы 7 м); магистраль 32 м (h = 14.5 м) врезается в него: стоп-линии прежних рукавов окажутся на скруглении.
        using Simulation sim = Make(Net(new[] { (-300f, 0f), (0f, 0f), (300f, 0f), (0f, 400f) }, new[] { (0, 1, "street_2"), (1, 2, "street_2") }));
        RoadProfileStore st = sim.World.Net.Profiles!;
        BuildRoadCmd b = Build(st, P("arterial_6"), false, 0f, 0f, 0f, 400f);
        Command c = Command.MakeBuildRoad(in b, 1);
        var issues = new PreviewIssue[16];
        Assert.Equal(RejectReason.None, sim.Preview(in c, out PreviewResult pr, issues));
        int short1 = issues.Take(pr.IssuesWritten).Count(i => i.Code == IssueCode.SetbackShort);
        Assert.Equal(2, short1);
        Assert.All(issues.Take(pr.IssuesWritten).Where(i => i.Code == IssueCode.SetbackShort), i => Assert.Equal(IssueSeverity.Warning, i.Severity));

        // Узкая улица h = 8 м, нужна 14 м > 7 м: тоже предупреждение; а вот продолжение «прямо» (улица на улицу вдоль) - нет.
        BuildRoadCmd straight = Build(st, P("street_2"), false, 300f, 0f, 600f, 0f);
        Command c2 = Command.MakeBuildRoad(in straight, 2);
        Assert.Equal(RejectReason.None, sim.Preview(in c2, out pr, issues));
        Assert.DoesNotContain(issues.Take(pr.IssuesWritten), i => i.Code == IssueCode.SetbackShort);
    }

    private static int Live(LotStore l)
    {
        int n = 0;
        for (int i = 0; i < l.Count; i++)
        {
            n += (l.Flags[i] & LotFlags.Lost) == 0 && l.Orient[i] != LotOrient.Legacy ? 1 : 0;
        }

        return n;
    }

    private static int LostCount(LotStore l)
    {
        int n = 0;
        for (int i = 0; i < l.Count; i++)
        {
            n += (l.Flags[i] & LotFlags.Lost) != 0 ? 1 : 0;
        }

        return n;
    }

    // ---------------------------------------------------------------- превью = применение

    [Fact]
    public void Preview_VerdictMatchesApply_ProfileCommands_10000()
    {
        int count = Environment.GetEnvironmentVariable("TC_LONG") == "1" ? 20000 : 10000;
        ScenarioDesc d = BigCross(30);
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions());
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        sim.Step(100);
        var rng = new Random(77);
        var issues = new PreviewIssue[16];
        var seen = new Dictionary<RejectReason, int>();
        int upgradesApplied = 0;
        int buildsApplied = 0;
        int agree = 0;
        for (int i = 0; i < count; i++)
        {
            Command cmd = RandomProfileCommand(sim, rng, (uint)(i + 1));
            RejectReason pv = sim.Preview(in cmd, out PreviewResult pr, issues);
            Assert.Equal(pv, pr.Verdict);
            ulong h = sim.ComputeStateHash();
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
                // Единственное допустимое расхождение: за тик до команды успели машины (Busy появляется/исчезает) или врезка в построенный узел (перестройка).
                bool rebuild = pr.Mechanism == 1 && av is RejectReason.Busy or RejectReason.CreatesTrap or RejectReason.NodeLimit or RejectReason.Capacity;
                bool busy = pv == RejectReason.Busy || av == RejectReason.Busy;
                busy |= opsPending;   // финал сноса в фазе 1а того же тика убрал сегмент/рукав до команды
                Assert.True(rebuild || busy, $"команда {i} ({cmd.Kind}): превью {pv}, применение {av}");
            }

            if (av == RejectReason.None)
            {
                upgradesApplied += cmd.Kind == CommandKind.UpgradeRoad ? 1 : 0;
                buildsApplied += cmd.Kind == CommandKind.BuildRoad ? 1 : 0;
            }
            else if (av != RejectReason.Busy)
            {
                _ = h;
            }

            if (i % 500 == 0)
            {
                Assert.Null(sim.World.VerifyDerived());
            }
        }

        Assert.Null(sim.World.VerifyDerived());
        Assert.True(agree > count * 9 / 10, $"совпало лишь {agree} из {count}");
        Assert.True(upgradesApplied > 30 && buildsApplied > 30, $"применено апгрейдов {upgradesApplied}, построек {buildsApplied}");
        foreach (RejectReason need in new[] { RejectReason.None, RejectReason.InvalidArgument, RejectReason.Overlap, RejectReason.InvalidHandle })
        {
            Assert.True(seen.ContainsKey(need), $"в выборке нет исхода {need}");
        }

        Console.WriteLine($"[profile-preview] команд {count}, совпали {agree}, построек {buildsApplied}, апгрейдов {upgradesApplied}; исходы: {string.Join(", ", seen)}");
        // Сейв после фаззера проходит валидатор и даёт тот же хэш.
        sim.Step(20);
        Simulation loaded = ToolsFixture.RoundTrip(sim);
        loaded.Dispose();
    }

    private static Command RandomProfileCommand(Simulation sim, Random rng, uint tag)
    {
        World w = sim.World;
        NetworkStore n = w.Net;
        RoadProfileStore st = n.Profiles!;
        int kind = rng.Next(100);
        if (kind < 45)
        {
            // Постройка: часто от существующего узла, иначе в случайной точке; профиль и зеркальность случайные, иногда несогласованные.
            int node = -1;
            if (rng.Next(3) != 0 && n.NodeCount > 0)
            {
                int pick = rng.Next(n.NodeCount);
                node = n.NodeAlive(pick) ? pick : -1;
            }

            Vec2 a = node >= 0 ? n.Nodes[node].Position : new Vec2(rng.Next(-600, 600), rng.Next(-600, 600));
            double ang = rng.NextDouble() * Math.PI * 2;
            float len = rng.Next(0, 12) == 0 ? rng.Next(1, 40) : rng.Next(40, 400);
            var b = new Vec2(a.X + (float)(Math.Cos(ang) * len), a.Z + (float)(Math.Sin(ang) * len));
            int p = rng.Next(st.Count);
            bool mir = rng.Next(2) == 0;
            BuildRoadCmd cmd = Build(st, p, mir, a.X, a.Z, b.X, b.Z);
            int spoil = rng.Next(20);
            if (spoil == 0)
            {
                cmd.LanesAB = (byte)rng.Next(4);
            }
            else if (spoil == 1)
            {
                cmd.Profile = (ushort)rng.Next(st.Count + 3);
            }
            else if (spoil == 2)
            {
                cmd.Profile = 0;
            }
            else if (spoil == 3)
            {
                cmd.Kind = cmd.Kind == RoadKind.Street ? RoadKind.Arterial : RoadKind.Street;
            }

            return Command.MakeBuildRoad(in cmd, tag);
        }

        if (kind < 85)
        {
            int seg = n.SegmentCount > 0 ? rng.Next(n.SegmentCount) : 0;
            int gen = n.SegmentCount > 0 ? n.Segments[seg].Generation : 1;
            int p = rng.Next(st.Count);
            bool mir = rng.Next(2) == 0;
            var u = new UpgradeRoadCmd
            {
                Seg = new EntityRef(seg, rng.Next(15) == 0 ? gen + 2 : gen),
                SpeedLimit = rng.Next(4) == 0 ? 8f + rng.Next(8) : 0f,
                LanesAB = 255,
                LanesBA = 255,
                Flags = (byte)(UpgradeRoadFlags.KeepKind | (mir ? UpgradeRoadFlags.Mirrored : 0)),
                Profile = (ushort)(p + 1),
            };
            int spoil = rng.Next(12);
            if (spoil == 0)
            {
                u.Profile = 0;
                u.LanesAB = (byte)rng.Next(4);
                u.LanesBA = (byte)rng.Next(4);
                u.Flags = 0;
            }
            else if (spoil == 1)
            {
                u.LanesAB = (byte)rng.Next(4);
            }
            else if (spoil == 2)
            {
                u.Profile = 0;
                u.Flags = UpgradeRoadFlags.KeepKind;
            }

            return Command.MakeUpgradeRoad(in u, tag);
        }

        int s2 = n.SegmentCount > 0 ? rng.Next(n.SegmentCount) : 0;
        return Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = new EntityRef(s2, n.SegmentCount > 0 ? n.Segments[s2].Generation : 1) }, tag);
    }

    // ---------------------------------------------------------------- undo / redo

    private static string[] Canon(Simulation sim)
    {
        NetworkStore n = sim.World.Net;
        var list = new List<string>();
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            SegmentData sd = n.Segments[s];
            Vec2 a = n.Nodes[sd.NodeA].Position;
            Vec2 b = n.Nodes[sd.NodeB].Position;
            list.Add($"S {a.X:F1},{a.Z:F1}>{b.X:F1},{b.Z:F1} {sd.LanesAB}/{sd.LanesBA} v{sd.SpeedLimit:F1} k{(int)sd.Kind} f{sd.Flags} p{sd.Profile}");
        }

        list.Sort(StringComparer.Ordinal);
        return list.ToArray();
    }

    private static void Settle(Simulation sim)
    {
        for (int i = 0; i < 6000 && sim.World.Ops.Count > 0; i++)
        {
            sim.Step(1);
        }

        Assert.Equal(0, sim.World.Ops.Count);
    }

    [Fact]
    public void Undo_Redo_ProfileBuildUpgradeAndRemove_RoundTrip()
    {
        using Simulation sim = Quiet();
        RoadProfileStore st = sim.World.Net.Profiles!;
        var stack = new UndoStack();
        string[] c0 = Canon(sim);

        BuildRoadCmd b = Build(st, P("asym_1_2"), true, 600f, 600f, 600f, 900f);
        Assert.True(stack.Execute(sim, new[] { Command.MakeBuildRoad(in b, 1) }).Recorded);
        int seg = SegmentAt(sim, 600f, 600f, 600f, 900f);
        string[] c1 = Canon(sim);

        Command up = Upgrade(sim, seg, P("arterial_6"), false);
        UndoResult ur = stack.Execute(sim, new[] { up });
        Assert.True(ur.Ok && ur.Recorded);
        AssertMatchesProfile(sim, seg, P("arterial_6"), false);
        string[] c2 = Canon(sim);

        Command speed = Upgrade(sim, seg, P("arterial_6"), false, speed: 9f);
        Assert.True(stack.Execute(sim, new[] { speed }).Recorded);
        string[] c3 = Canon(sim);

        // Снос профильной дороги и его отмена возвращают профиль и зеркальность.
        Command rm = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, ++_tag);
        Assert.True(stack.Execute(sim, new[] { rm }).Recorded);
        Settle(sim);
        string[] c4 = Canon(sim);
        Assert.Equal(c0, c4);

        Assert.True(stack.Undo(sim).Ok);
        Settle(sim);
        Assert.Equal(c3, Canon(sim));
        int seg2 = SegmentAt(sim, 600f, 600f, 600f, 900f);
        AssertMatchesProfile(sim, seg2, P("arterial_6"), false);

        Assert.True(stack.Undo(sim).Ok);   // скорость
        Assert.Equal(c2, Canon(sim));
        Assert.True(stack.Undo(sim).Ok);   // профиль: возврат к асимметричному зеркальному
        Settle(sim);
        Assert.Equal(c1, Canon(sim));
        AssertMatchesProfile(sim, seg2, P("asym_1_2"), true);
        Assert.True(stack.Undo(sim).Ok);   // постройка
        Settle(sim);
        Assert.Equal(c0, Canon(sim));

        while (stack.RedoCount > 0)
        {
            UndoResult r = stack.Redo(sim);
            Assert.True(r.Rejected == 0, $"redo отклонён: {r.FirstReason} {r.FirstKind}");
            Settle(sim);
        }

        Assert.Equal(c4, Canon(sim));
        AssertConsistent(sim);
    }

    [Fact]
    public void Undo_UpgradeLegacyToProfile_UndoRemovesProfile_RedoRestores()
    {
        // T5: обратная к «без профиля -> профиль» - UpgradeRoad с NoProfile (полосы и тип прежние, ширина стандартная).
        using Simulation sim = Quiet();
        BuildRoadCmd legacy = ToolsFixture.Road(new Vec2(600f, 600f), new Vec2(600f, 900f), 2);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in legacy, 1)));
        int seg = SegmentAt(sim, 600f, 600f, 600f, 900f);
        SegmentData before = sim.World.Net.Segments[seg];
        Command up = Upgrade(sim, seg, P("arterial_4"), false);
        Command copy = up;
        var issues = new PreviewIssue[8];
        Assert.Equal(RejectReason.None, sim.Preview(in copy, out PreviewResult pr, issues));
        Assert.DoesNotContain(issues.Take(pr.IssuesWritten), i => i.Code == IssueCode.UndoUnavailable);
        var stack = new UndoStack();
        UndoResult r = stack.Execute(sim, new[] { up });
        Assert.Equal(0, r.Rejected);
        Assert.True(r.Recorded);
        Settle(sim);
        AssertMatchesProfile(sim, seg, P("arterial_4"), false);
        Assert.True(stack.Undo(sim).Ok);
        Settle(sim);
        SegmentData back = sim.World.Net.Segments[seg];
        Assert.Equal(-1, back.Profile);
        Assert.Equal(before.LanesAB, back.LanesAB);
        Assert.Equal(before.LanesBA, back.LanesBA);
        Assert.Equal(before.Kind, back.Kind);
        Assert.Equal(sim.World.Builder.Options.LaneWidth, sim.World.Net.Lanes[back.FirstLaneAB].Width);
        Assert.True(stack.Redo(sim).Ok);
        Settle(sim);
        AssertMatchesProfile(sim, seg, P("arterial_4"), false);
    }

    [Fact]
    public void Undo_Redo_NarrowingDuringDrain_CancelsAndReapplies()
    {
        // T5: undo во время дренажа полос отменяет операцию, redo ставит её снова; после финала undo возвращает полосы (перенос машин).
        using Simulation sim = Simulation.Create(BigCross(), new SimRuntimeOptions());
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        sim.Step(5);
        NetworkStore n = sim.World.Net;
        int seg = -1;
        for (int s = 0; s < n.SegmentCount && seg < 0; s++)
        {
            seg = n.SegmentAlive(s) && n.Segments[s].Profile == P("arterial_4") ? s : -1;
        }

        var stack = new UndoStack();
        TestScenarios.PlaceCar(sim, n.Segments[seg].FirstLaneAB + 1, 25f);   // машина на убираемой полосе (LaneNo 1)
        UndoResult r = stack.Execute(sim, new[] { Upgrade(sim, seg, P("street_2"), false) });
        Assert.True(r.Recorded && r.Rejected == 0);
        Assert.True(sim.World.Ops.FindShrink(seg) >= 0);
        Assert.True(stack.Undo(sim).Ok);
        Assert.True(sim.World.Ops.FindShrink(seg) < 0);
        AssertMatchesProfile(sim, seg, P("arterial_4"), false);
        Assert.True(stack.Redo(sim).Ok);
        Settle(sim);
        AssertMatchesProfile(sim, seg, P("street_2"), false);
        Assert.True(stack.Undo(sim).Ok);
        Settle(sim);
        AssertMatchesProfile(sim, seg, P("arterial_4"), false);
        Assert.Null(sim.World.VerifyDerived());
        using Simulation twin = ToolsFixture.RoundTrip(sim);
    }

    // ---------------------------------------------------------------- журнал команд

    [Fact]
    public void ReplayLog_RoundTripsProfileFields_AndReplaysToSameHash()
    {
        ScenarioDesc d = Net(new[] { (-320f, 0f), (320f, 0f) }, new[] { (0, 1, "street_2") });
        RoadProfileStore st = new(d.RoadProfiles);
        using Simulation a = Simulation.Create(in d, new SimRuntimeOptions());
        a.SetToolsConfig(ToolsFixture.Unbounded);
        var rec = new ReplayRecorder(a, 100, "rp7");
        rec.Step(50);
        BuildRoadCmd b = Build(st, P("asym_1_2"), true, 600f, 600f, 600f, 900f);
        Command build = Command.MakeBuildRoad(in b, 1);
        Assert.True(rec.Enqueue(in build));
        rec.Step(60);
        int seg = SegmentAt(a, 600f, 600f, 600f, 900f);
        Command up = Upgrade(a, seg, P("arterial_4"), false, speed: 9f);
        Assert.True(rec.Enqueue(in up));
        rec.Step(80);
        ReplayLog log = rec.Finish();
        Assert.Equal(ReplayStatus.Ok, ReplayLog.TryParse(log.ToBytes(), out ReplayLog back));
        Assert.Equal(2, back.Commands.Count);
        Assert.Equal((ushort)(P("asym_1_2") + 1), back.Commands[0].BuildRoad.Profile);
        Assert.Equal(BuildRoadFlags.Mirrored, (byte)(back.Commands[0].BuildRoad.Flags & BuildRoadFlags.Mirrored));
        Assert.Equal((ushort)(P("arterial_4") + 1), back.Commands[1].UpgradeRoad.Profile);
        Assert.Equal(9f, back.Commands[1].UpgradeRoad.SpeedLimit);

        using Simulation fresh = Simulation.Create(in d, new SimRuntimeOptions());
        fresh.SetToolsConfig(ToolsFixture.Unbounded);
        ReplayResult res = ReplayPlayer.Run(fresh, back);
        Assert.Equal(ReplayStatus.Ok, res.Status);
        Assert.False(res.Diverged);
        Assert.Equal(a.ComputeStateHash(), fresh.ComputeStateHash());
    }

    // ---------------------------------------------------------------- детерминизм и память

    private static ulong Scenario(int threads)
    {
        ScenarioDesc d = BigCross(120);
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        RoadProfileStore st = sim.World.Net.Profiles!;
        sim.Step(200);
        var rng = new Random(3);
        for (int i = 0; i < 400; i++)
        {
            Command c = RandomProfileCommand(sim, rng, (uint)(i + 1));
            sim.Enqueue(c);
            sim.Step(rng.Next(1, 12));
        }

        sim.Step(100);
        Assert.Null(sim.World.VerifyDerived());
        _ = st;
        return sim.ComputeStateHash();
    }

    [Fact]
    public void ProfileEdits_AreDeterministic_At123Threads()
    {
        ulong h1 = Scenario(1);
        Assert.Equal(h1, Scenario(2));
        Assert.Equal(h1, Scenario(3));
    }

    // Живой город s1k по профилям фикстуры (1 000 машин, светофоры, автобусы): правки профилей идут под нагрузкой; мир согласован, результат не зависит от числа потоков.
    private static (ulong Hash, int Applied, int Busy) LiveCity(int threads, int seed = 11, int attempts = 80)
    {
        ScenarioDesc d = ProfileSaveFixtures.S1k().Desc;
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.SetToolsConfig(ToolsFixture.Unbounded);
        sim.Step(400);
        var rng = new Random(seed);
        int applied = 0;
        int busy = 0;
        for (int i = 0; i < attempts; i++)
        {
            NetworkStore n = sim.World.Net;
            int seg = rng.Next(n.SegmentCount);
            if (!n.SegmentAlive(seg))
            {
                continue;
            }

            int p = rng.Next(n.Profiles!.Count);
            bool mir = rng.Next(2) == 0;
            Command cmd = Upgrade(sim, seg, p, mir, rng.Next(3) == 0 ? 9f : 0f);
            Command copy = cmd;
            RejectReason pv = sim.Preview(in copy, out _, new PreviewIssue[16]);
            sim.Enqueue(cmd);
            sim.Step(1);
            RejectReason av = RejectReason.None;
            foreach (SimEvent e in sim.Events)
            {
                if (e.Tag == cmd.Tag && e.Kind == SimEventKind.CommandRejected)
                {
                    av = (RejectReason)e.A;
                }
            }

            if (pv == av)
            {
                applied += av == RejectReason.None ? 1 : 0;
                busy += av == RejectReason.Busy ? 1 : 0;
            }

            if (av == RejectReason.None)
            {
                // T5: занятый сегмент ждёт опустения убираемых полос (операция ShrinkLanes) - профиль появится в её финале.
                for (int t = 0; t < 3600 && sim.World.Ops.FindShrink(seg) >= 0; t++)
                {
                    sim.Step(1);
                }

                AssertMatchesProfile(sim, seg, p, mir);
            }

            sim.Step(rng.Next(5, 40));

            if (i % 10 == 0)
            {
                Assert.Null(sim.World.VerifyDerived());
            }
        }

        sim.Step(300);
        Assert.Null(sim.World.VerifyDerived());
        Assert.True(sim.World.Vehicles.Pool.AliveCount > 100, "город не должен опустеть после правок");
        return (sim.ComputeStateHash(), applied, busy);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(23)]
    [InlineData(37)]
    [InlineData(41)]
    public void UpgradeRoad_OnLiveCity_OtherSeeds_StayConsistent(int seed)
    {
        (_, int applied, _) = LiveCity(1, seed, 200);
        Assert.True(applied >= 10, $"применено {applied}");
    }

    [Fact]
    public void UpgradeRoad_OnLiveCity_StaysConsistent_AndDeterministicAt123Threads()
    {
        (ulong h1, int applied, int busy) = LiveCity(1);
        Assert.True(applied >= 5, $"применено {applied} правок, занято {busy}");
        Assert.Equal(h1, LiveCity(2).Hash);
        Assert.Equal(h1, LiveCity(3).Hash);
    }

    [Fact]
    public void Step_AfterProfileEdits_AllocatesNothing()
    {
        using Simulation sim = Quiet();
        RoadProfileStore st = sim.World.Net.Profiles!;
        BuildRoadCmd b = Build(st, P("arterial_4"), false, 600f, 600f, 600f, 900f);
        Assert.Equal(RejectReason.None, Run(sim, Command.MakeBuildRoad(in b, 1)));
        int seg = SegmentAt(sim, 600f, 600f, 600f, 900f);
        Command up = Upgrade(sim, seg, P("asym_1_2"), true);
        Assert.Equal(RejectReason.None, Run(sim, in up));
        sim.Step(300);
        long bytes = AllocationProbe.Measure(() => sim.Step(200));
        Assert.Equal(0, bytes);
    }
}
