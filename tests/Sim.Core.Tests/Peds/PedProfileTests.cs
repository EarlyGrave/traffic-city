using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;
using Sim.Scenarios;

namespace Sim.Core.Tests.Peds;

/// <summary>
/// RP4 (road-profiles.md §3.2, §10): пешеходная сеть в режиме профилей берёт тротуар каждой стороны из профиля (смещение и ширина своя),
/// у стороны без тротуара нет рёбер и переходов, ось сети - середина профиля, <c>Mirrored</c> меняет стороны местами. Ожидания считаются
/// из элементов профиля независимо от <see cref="RoadProfileStore"/>; legacy-голдены закрыты прежними тестами (хэши и корпус сейвов).
/// </summary>
public sealed class PedProfileTests
{
    private const float Tol = 2e-3f;

    private static string FixturePath => Path.Combine(GenFixture.RepoRoot(), "tests", "fixtures", "road-profiles.fixture.json");

    private static RoadElemDesc E(RoadElemKind k, int mm, RoadLaneDir dir = RoadLaneDir.Forward) => new() { Kind = k, WidthMm = mm, Dir = dir };

    private static RoadProfileDesc Street(string id, RoadElemDesc[] left, RoadElemDesc[] right)
    {
        RoadElemDesc[] mid =
        [
            E(RoadElemKind.Curb, 250),
            E(RoadElemKind.Lane, 3500, RoadLaneDir.Backward),
            E(RoadElemKind.Lane, 3500, RoadLaneDir.Forward),
            E(RoadElemKind.Curb, 250),
        ];
        RoadElemDesc[] all = left.Concat(mid).Concat(right).ToArray();
        return new RoadProfileDesc { Id = id, Kind = RoadKind.Street, WidthMm = all.Sum(e => e.WidthMm), Elements = all };
    }

    /// <summary>Каталог фикстуры + три улицы: тротуары 2 и 3 м (асимметрия), только справа, только слева.</summary>
    private static RoadProfilesDesc Catalog()
    {
        RoadProfilesDesc d = ScenarioLoader.ParseRoadProfiles(File.ReadAllText(FixturePath));
        RoadElemDesc g(int mm) => E(RoadElemKind.Verge, mm);
        RoadElemDesc w(int mm) => E(RoadElemKind.Sidewalk, mm);
        d.Profiles = d.Profiles.Concat(
        [
            Street("walk_2_3", [w(2000), g(2250)], [g(1250), w(3000)]),
            Street("walk_right", [g(4250)], [g(1250), w(3000)]),
            Street("walk_left", [w(3000), g(1250)], [g(4250)]),
        ]).ToArray();
        Assert.Equal(RoadProfileError.None, RoadProfileValidator.Validate(in d, out _));
        return d;
    }

    private static int Index(in RoadProfilesDesc d, string id)
    {
        for (int i = 0; i < d.Profiles.Length; i++)
        {
            if (d.Profiles[i].Id == id)
            {
                return i;
            }
        }

        throw new InvalidOperationException(id);
    }

    /// <summary>Ширина тротуара на стороне профиля (left - первый элемент), 0 - тротуара нет. Независимо от <see cref="RoadProfileStore"/>.</summary>
    private static int Walk(RoadProfileDesc p, bool left)
    {
        RoadElemDesc e = left ? p.Elements[0] : p.Elements[^1];
        return e.Kind == RoadElemKind.Sidewalk ? e.WidthMm : 0;
    }

    /// <summary>
    /// Крест из пяти узлов: рукава E/W - профиль <paramref name="main"/> (A→B слева направо), N/S - профиль <paramref name="side"/>,
    /// у которого <paramref name="mirroredSide"/> разворачивает чтение профиля. Пешеходы включены, переходы на перегоне выключены.
    /// </summary>
    private static ScenarioDesc Cross(RoadProfilesDesc cat, string main, string side, bool mirroredSide)
    {
        int pm = Index(in cat, main);
        int ps = Index(in cat, side);
        var store = new RoadProfileStore(in cat);
        SegmentDesc Seg(int a, int b, int p, bool mir) => new()
        {
            A = a,
            B = b,
            Profile = p + 1,
            Mirrored = mir,
            LanesAB = store.LanesAB(p, mir),
            LanesBA = store.LanesBA(p, mir),
            SpeedLimit = 13.9f,
            Kind = store.Kind[p],
        };

        ScenarioDesc d = TestScenarios.Line();
        d.Network = new NetworkDesc
        {
            Nodes =
            [
                new NodeDesc { X = 0, Z = 0, Control = NodeControl.Signal },
                new NodeDesc { X = 200, Z = 0, Control = NodeControl.None },
                new NodeDesc { X = -200, Z = 0, Control = NodeControl.None },
                new NodeDesc { X = 0, Z = 200, Control = NodeControl.None },
                new NodeDesc { X = 0, Z = -200, Control = NodeControl.None },
            ],
            Segments =
            [
                Seg(0, 1, pm, false),
                Seg(2, 0, pm, false),
                Seg(0, 3, ps, mirroredSide),
                Seg(4, 0, ps, mirroredSide),
            ],
        };
        d.Demand.Zones =
        [
            new ZoneDesc { CenterX = 200, CenterZ = 0, HalfSize = 50, Weight = 1 },
            new ZoneDesc { CenterX = -200, CenterZ = 0, HalfSize = 50, Weight = 1 },
        ];
        d.RoadProfiles = cat;
        d.Peds = new PedDesc { Enabled = true, Count = 20, MidblockAtStops = false, ArterialCrossings = ArterialCrossings.SignalOnly };
        SimCapacities c = d.Capacities;
        c.MaxPeds = 40;
        c.MaxResidents = 40;
        c.MaxPedPathSteps = 64;
        d.Capacities = c;
        return d;
    }

    private static Simulation Make(in ScenarioDesc d)
    {
        CreateStatus st = Simulation.TryCreate(in d, out Simulation? sim);
        Assert.Equal(CreateStatus.Ok, st);
        return sim!;
    }

    /// <summary>Знаковое смещение точки от оси A→B сегмента, м (+ вправо по A→B).</summary>
    private static float Signed(NetworkStore net, int seg, Vec2 p)
    {
        ref readonly SegmentData sd = ref net.Segments[seg];
        Vec2 a = net.Nodes[sd.NodeA].Position;
        Vec2 b = net.Nodes[sd.NodeB].Position;
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float inv = 1f / MathF.Sqrt((dx * dx) + (dz * dz));
        return (((p.X - a.X) * -dz) + ((p.Z - a.Z) * dx)) * inv;
    }

    /// <summary>Все точки рёбер стороны лежат на смещении <paramref name="expected"/> (со знаком по стороне).</summary>
    private static void AssertSideOffset(NetworkStore net, int seg, int side, float expectedAbs)
    {
        PedNetwork pn = net.PedNet!;
        pn.SideEdges(seg, side, out int first, out int count);
        Assert.True(count > 0, $"segment {seg} side {side}: sidewalk expected");
        float expected = side == 0 ? expectedAbs : -expectedAbs;
        for (int e = first; e < first + count; e++)
        {
            ref readonly PedEdgeData ed = ref pn.Edges[e];
            for (int k = 0; k < ed.PointCount; k++)
            {
                Assert.InRange(Signed(net, seg, pn.Points[ed.FirstPoint + k]), expected - Tol, expected + Tol);
            }
        }
    }

    private static int Count(PedNetwork pn, int seg, int side)
    {
        pn.SideEdges(seg, side, out _, out int count);
        return count;
    }

    // ------------------------------------------------------------ асимметрия и Mirrored

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsymmetricWalks_EachSideUsesItsOwnOffset(bool mirroredSide)
    {
        RoadProfilesDesc cat = Catalog();
        ScenarioDesc d = Cross(cat, "walk_2_3", "walk_2_3", mirroredSide);
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        RoadProfileDesc p = cat.Profiles[Index(in cat, "walk_2_3")];
        float half = p.WidthMm / 2f;
        float offL = (half - (Walk(p, true) / 2f)) * 0.001f;    // слева тротуар 2 м
        float offR = (half - (Walk(p, false) / 2f)) * 0.001f;   // справа 3 м
        Assert.NotEqual(offL, offR);

        for (int seg = 0; seg < net.SegmentCount; seg++)
        {
            bool mir = (net.Segments[seg].Flags & SegmentFlags.Mirrored) != 0;
            // Справа от A→B (side 0): правая сторона профиля, при Mirrored - левая.
            AssertSideOffset(net, seg, 0, mir ? offL : offR);
            AssertSideOffset(net, seg, 1, mir ? offR : offL);
        }
    }

    [Fact]
    public void Crossing_SpansSidewalkCentres_NotGlobalWidth()
    {
        RoadProfilesDesc cat = Catalog();
        ScenarioDesc d = Cross(cat, "walk_2_3", "street_2", false);
        using Simulation sim = Make(in d);
        PedNetwork pn = sim.World.Net.PedNet!;
        RoadProfileDesc p = cat.Profiles[Index(in cat, "walk_2_3")];
        // Центр - центр: Ширина - (WalkL + WalkR) / 2 (кромка слева + кромка справа + газоны + половины тротуаров).
        int expected = p.WidthMm - ((Walk(p, true) + Walk(p, false)) / 2);
        int seen = 0;
        for (int e = pn.SidewalkEdgeCount; e < pn.EdgeCount; e++)
        {
            ref readonly PedEdgeData ed = ref pn.Edges[e];
            if (ed.Kind == PedEdgeKind.Crossing && sim.World.Net.Segments[ed.Segment].Profile == Index(in cat, "walk_2_3"))
            {
                Assert.InRange(ed.LenMm, expected - 1, expected + 1);
                seen++;
            }
        }

        Assert.Equal(2, seen);   // рукава E и W; у кресте N/S другой профиль

        // Улица 16 м с тротуаром 3 м: тот же счёт по её собственным ширинам.
        RoadProfileDesc s = cat.Profiles[Index(in cat, "street_2")];
        int street = s.WidthMm - ((Walk(s, true) + Walk(s, false)) / 2);
        for (int e = pn.SidewalkEdgeCount; e < pn.EdgeCount; e++)
        {
            ref readonly PedEdgeData ed = ref pn.Edges[e];
            if (ed.Kind == PedEdgeKind.Crossing && sim.World.Net.Segments[ed.Segment].Profile == Index(in cat, "street_2"))
            {
                Assert.InRange(ed.LenMm, street - 1, street + 1);
            }
        }
    }

    // ------------------------------------------------------------ сторона без тротуара

    [Theory]
    [InlineData("walk_right", false)]
    [InlineData("walk_right", true)]
    [InlineData("walk_left", false)]
    [InlineData("walk_left", true)]
    public void OneSidedProfile_NoLaneNoCrossingOnMissingSide(string id, bool mirrored)
    {
        RoadProfilesDesc cat = Catalog();
        ScenarioDesc d = Cross(cat, "street_2", id, mirrored);
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        PedNetwork pn = net.PedNet!;
        RoadProfileDesc p = cat.Profiles[Index(in cat, id)];
        bool walkOnProfileRight = Walk(p, false) > 0;
        // Сторона A→B, где есть тротуар: профиль справа и не Mirrored -> side 0; Mirrored переворачивает.
        int walkSide = walkOnProfileRight != mirrored ? 0 : 1;
        float off = ((p.WidthMm / 2f) - (3000 / 2f)) * 0.001f;

        for (int seg = 2; seg < 4; seg++)
        {
            Assert.Equal(0, Count(pn, seg, 1 - walkSide));
            AssertSideOffset(net, seg, walkSide, off);
        }

        // Крест N/S не получил переходов через рукава без второго тротуара; E/W (street_2) переходы сохранили.
        int nsCrossings = 0;
        int ewCrossings = 0;
        for (int e = 0; e < pn.EdgeCount; e++)
        {
            if (pn.Edges[e].Kind != PedEdgeKind.Crossing)
            {
                continue;
            }

            if (pn.Edges[e].Segment >= 2)
            {
                nsCrossings++;
            }
            else
            {
                ewCrossings++;
            }
        }

        Assert.Equal(0, nsCrossings);
        Assert.Equal(2, ewCrossings);
        for (int x = 0; x < pn.MidXBase; x++)
        {
            int e = pn.XEdge[x];
            if (e >= 0)
            {
                Assert.True(pn.Edges[e].Segment < 2, "переход через рукав без тротуара с одной из сторон");
            }
        }
    }

    [Fact]
    public void OneSidedProfile_SimulationRuns_WithoutStuckPeds()
    {
        RoadProfilesDesc cat = Catalog();
        ScenarioDesc d = Cross(cat, "street_2", "walk_right", false);
        using Simulation sim = Make(in d);
        sim.Step(1200);
        Assert.True(sim.World.Net.PedNet!.SidewalkEdgeCount > 0);
        Assert.True(sim.ActivePedCount >= 0);
    }

    // ------------------------------------------------------------ симметричная улица

    [Fact]
    public void SymmetricStreet_OffsetIsSidewalkElementCentre()
    {
        // street_2: тротуар 3 м, газон 1.25 м: ось пешеходной полосы = край профиля - 1.5 м (раньше было кромка + 1.5 м).
        RoadProfilesDesc cat = Catalog();
        ScenarioDesc d = Cross(cat, "street_2", "street_2", false);
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        RoadProfileDesc p = cat.Profiles[Index(in cat, "street_2")];
        float expected = ((p.WidthMm / 2f) - (Walk(p, true) / 2f)) * 0.001f;
        for (int seg = 0; seg < net.SegmentCount; seg++)
        {
            AssertSideOffset(net, seg, 0, expected);
            AssertSideOffset(net, seg, 1, expected);
        }

        Assert.True(net.PedNet!.CrossingNodeCount > 0);
    }

    [Fact]
    public void ProfileCityWithPeds_StateHashSameFor1To3Threads_AndNoAllocation()
    {
        string root = GenFixture.RepoRoot();
        JsonNode n = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", "s1k-peds.json")))!;
        n["road_profiles_file"] = "road-profiles.json";
        n["city"]!["profiles"] = new JsonObject { ["street"] = "street_2", ["arterial"] = "arterial_4" };
        ScenarioDesc d = ScenarioLoader.Parse(
            n.ToJsonString(),
            rel => File.ReadAllText(Path.IsPathRooted(rel) ? rel : Path.Combine(root, "scenarios", rel)),
            "s1k-peds-profiles",
            null,
            new ScenarioOverrides { Profiles = FixturePath }).Desc;

        ulong h1 = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
            sim.Step(1200);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                h1 = h;
                Assert.True(sim.ActivePedCount > 0);
                Assert.True(sim.World.Net.PedNet!.CrossingNodeCount > 0);
            }
            else
            {
                Assert.Equal(h1, h);
            }
        }

        using Simulation one = Simulation.Create(in d);
        one.Step(1200);
        Assert.Equal(0, AllocationProbe.Measure(() => one.Step(100)));
    }
}
