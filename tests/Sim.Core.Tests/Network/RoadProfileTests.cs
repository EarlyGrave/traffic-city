using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Worlds;
using Sim.Scenarios;

namespace Sim.Core.Tests.Network;

/// <summary>
/// RP1 (ADR-0030, road-profiles.md): каталог профилей, валидация, эмиссия полос по профилю, Mirrored, <c>h</c> от кромки, хэш только в режиме профилей.
/// Ожидаемые значения берутся из самого каталога (метров в тестах нет), кроме двух-трёх фиксированных негативных правок.
/// </summary>
public class RoadProfileTests
{
    private const ulong GoldenS1kNetworkHash = 0xADA92B804C9F5496UL;   // как в CityGeneratorTests: legacy-сеть s1k побитово прежняя

    private static string FixturePath => Path.Combine(GenFixture.RepoRoot(), "tests", "fixtures", "road-profiles.fixture.json");

    private static string GamePath => Path.Combine(GenFixture.RepoRoot(), "scenarios", "road-profiles.json");

    private static RoadProfilesDesc Load(string path) => ScenarioLoader.ParseRoadProfiles(File.ReadAllText(path));

    public static TheoryData<string> Catalogs => new() { "fixture", "game" };

    private static RoadProfilesDesc CatalogByName(string name) => Load(name == "fixture" ? FixturePath : GamePath);

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

    // ---- каталог: загрузка и инварианты ----

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void Catalog_LoadsAndValidates(string name)
    {
        RoadProfilesDesc d = CatalogByName(name);
        Assert.True(d.Enabled);
        Assert.Equal(5, d.Profiles.Length);
        Assert.Equal(RoadProfileError.None, RoadProfileValidator.Validate(in d, out _));
        foreach (string id in new[] { "street_2", "oneway_2", "arterial_4", "arterial_6", "asym_1_2" })
        {
            Assert.True(Index(in d, id) >= 0);
        }
    }

    [Fact]
    public void Fixture_IsFrozenCopyOfStartSet()
    {
        // Фикстура заморожена: те же профили, что в игровом каталоге на момент RP1. Правка игровых ширин фикстуру не трогает (тест её не сравнивает с game).
        RoadProfilesDesc f = Load(FixturePath);
        Assert.Equal(8000, f.ModuleMm);
        Assert.Equal(5, f.Profiles.Length);
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void EveryProfile_Invariants(string name)
    {
        RoadProfilesDesc d = CatalogByName(name);
        var store = new RoadProfileStore(in d);
        for (int p = 0; p < d.Profiles.Length; p++)
        {
            RoadProfileDesc pd = d.Profiles[p];
            long sum = 0;
            int fwd = 0;
            int bwd = 0;
            foreach (RoadElemDesc e in pd.Elements)
            {
                sum += e.WidthMm;
                if (e.Kind == RoadElemKind.Lane)
                {
                    if (e.Dir == RoadLaneDir.Forward)
                    {
                        fwd++;
                    }
                    else
                    {
                        bwd++;
                    }
                }
            }

            // Σ = ширина = k * модуль, k в 1..8.
            Assert.Equal(pd.WidthMm, sum);
            Assert.Equal(0, pd.WidthMm % d.ModuleMm);
            Assert.InRange(pd.WidthMm / d.ModuleMm, 1, 8);
            Assert.Equal(fwd, store.LanesFwd[p]);
            Assert.Equal(bwd, store.LanesBwd[p]);
            Assert.Equal(pd.WidthMm / 2, store.EdgeMm[p]);

            // Элементы идут встык от левого края.
            int x = -pd.WidthMm / 2;
            for (int e = 0; e < pd.Elements.Length; e++)
            {
                Assert.Equal(x, store.ElemOffsetMm[store.FirstElem[p] + e]);
                x += pd.Elements[e].WidthMm;
            }

            Assert.Equal(pd.WidthMm / 2, x);

            // Смещение каждой полосы = вычисленное из элементов (независимый обход слева направо).
            int fSeen = 0;
            int bSeen = 0;
            x = -pd.WidthMm / 2;
            foreach (RoadElemDesc e in pd.Elements)
            {
                if (e.Kind == RoadElemKind.Lane)
                {
                    float center = (x + (e.WidthMm * 0.5f)) * 0.001f;
                    int dir = e.Dir == RoadLaneDir.Forward ? 0 : 1;
                    int laneNo = dir == 0 ? fwd - 1 - fSeen++ : bSeen++;
                    Assert.Equal(center, store.LaneCenterM(p, false, dir, laneNo));
                    Assert.Equal(e.WidthMm * 0.001f, store.LaneWidthM(p, false, dir, laneNo));
                    if (dir == 0 && laneNo + 1 < fwd)
                    {
                        Assert.True(store.LaneCenterM(p, false, 0, laneNo) > store.LaneCenterM(p, false, 0, laneNo + 1));   // LaneNo 0 = правая
                    }
                }

                x += e.WidthMm;
            }

            // Полосы направлений не перекрываются: все forward правее всех backward.
            if (fwd > 0 && bwd > 0)
            {
                Assert.True(store.LaneCenterM(p, false, 0, fwd - 1) > store.LaneCenterM(p, false, 1, bwd - 1));
            }

            // Кромка - внешний край крайних полос; h не зависит от Mirrored.
            Assert.True(store.CurbLMm[p] > 0 && store.CurbRMm[p] > 0);
            Assert.Equal(store.HalfRoadM(p), Math.Max(store.CurbMm(p, true, true), store.CurbMm(p, true, false)) * 0.001f);
        }
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void Mirrored_SwapsDirectionsAndNegatesOffsets(string name)
    {
        RoadProfilesDesc d = CatalogByName(name);
        var store = new RoadProfileStore(in d);
        for (int p = 0; p < d.Profiles.Length; p++)
        {
            Assert.Equal(store.LanesFwd[p], store.LanesBA(p, true));
            Assert.Equal(store.LanesBwd[p], store.LanesAB(p, true));
            for (int dir = 0; dir < 2; dir++)
            {
                int n = dir == 0 ? store.LanesFwd[p] : store.LanesBwd[p];
                for (int k = 0; k < n; k++)
                {
                    // Та же физическая полоса: у зеркального сегмента направление обратное, смещение с другим знаком.
                    Assert.Equal(-store.LaneCenterM(p, false, dir, k), store.LaneCenterM(p, true, 1 - dir, k));
                    Assert.Equal(store.LaneWidthM(p, false, dir, k), store.LaneWidthM(p, true, 1 - dir, k));
                }
            }

            Assert.Equal(store.CurbMm(p, false, true), store.CurbMm(p, true, false));
            Assert.Equal(store.WalkMm(p, false, true), store.WalkMm(p, true, false));
        }
    }

    [Fact]
    public void AsymmetricProfile_HasUnequalLaneCounts_AndShiftedAxis()
    {
        RoadProfilesDesc d = Load(FixturePath);
        var store = new RoadProfileStore(in d);
        int p = Index(in d, "asym_1_2");
        Assert.NotEqual(store.LanesFwd[p], store.LanesBwd[p]);
        int one = Index(in d, "oneway_2");
        Assert.Equal(0, store.LanesBwd[one]);
    }

    // ---- валидация ----

    private static RoadProfilesDesc Mutate(Action<RoadProfileDesc[]> edit)
    {
        RoadProfilesDesc d = Load(FixturePath);
        var copy = new RoadProfileDesc[d.Profiles.Length];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = d.Profiles[i];
            copy[i].Elements = (RoadElemDesc[])d.Profiles[i].Elements.Clone();
        }

        edit(copy);
        d.Profiles = copy;
        return d;
    }

    [Fact]
    public void Validation_RejectsBrokenCatalogs()
    {
        // Сумма не кратна модулю: газон +0.5 м при той же заявленной ширине.
        Assert.Equal(RoadProfileError.BadWidth, RoadProfileValidator.Validate(Mutate(p => p[0].Elements[1].WidthMm += 500), out _));

        // Сумма кратна, но не равна заявленной ширине.
        Assert.Equal(RoadProfileError.BadWidth, RoadProfileValidator.Validate(Mutate(p => p[0].WidthMm += 8000), out _));

        // Полоса 4.5 м.
        Assert.Equal(RoadProfileError.ElementWidth, RoadProfileValidator.Validate(Mutate(p => p[0].Elements[3].WidthMm = 4500), out _));

        // Нет curb между полосой и газоном.
        Assert.Equal(RoadProfileError.NoCurb, RoadProfileValidator.Validate(Mutate(p =>
        {
            p[0].Elements[2].Kind = RoadElemKind.Verge;
        }), out _));

        // Два curb подряд.
        Assert.Equal(RoadProfileError.DoubleCurb, RoadProfileValidator.Validate(Mutate(p =>
        {
            p[0].Elements[1].Kind = RoadElemKind.Curb;
            p[0].Elements[1].WidthMm = 250;
        }), out _));

        // Нет полос.
        Assert.Equal(RoadProfileError.NoLane, RoadProfileValidator.Validate(Mutate(p =>
        {
            p[0].Elements[3].Kind = RoadElemKind.Median;
            p[0].Elements[4].Kind = RoadElemKind.Median;
        }), out _));

        // forward левее backward.
        Assert.Equal(RoadProfileError.LaneOrder, RoadProfileValidator.Validate(Mutate(p =>
        {
            p[0].Elements[3].Dir = RoadLaneDir.Forward;
            p[0].Elements[4].Dir = RoadLaneDir.Backward;
        }), out _));

        // Полосы одного направления не подряд (между ними разделитель).
        Assert.Equal(RoadProfileError.LaneOrder, RoadProfileValidator.Validate(Mutate(p =>
        {
            int i = Index(Load(FixturePath), "arterial_4");
            p[i].Elements[4].Kind = RoadElemKind.Median;
            p[i].Elements[4].WidthMm = 3500;
            p[i].Elements[5].Kind = RoadElemKind.Lane;
            p[i].Elements[5].Dir = RoadLaneDir.Backward;
            p[i].Elements[5].WidthMm = 3500;
        }), out _));

        // Дубль id.
        Assert.Equal(RoadProfileError.DuplicateId, RoadProfileValidator.Validate(Mutate(p => p[1].Id = p[0].Id), out int bad));
        Assert.Equal(1, bad);

        // Пустой и слишком большой каталог.
        Assert.Equal(RoadProfileError.NoProfiles, RoadProfileValidator.Validate(Mutate(p => { }) with { Profiles = [] }, out _));
        Assert.Equal(RoadProfileError.BadModule, RoadProfileValidator.Validate(Mutate(p => { }) with { ModuleMm = 0 }, out _));
        Assert.Equal(RoadProfileError.TooManyProfiles, RoadProfileValidator.Validate(Mutate(p => { }) with { Profiles = new RoadProfileDesc[65] }, out _));
    }

    [Fact]
    public void Validation_RejectsTooManyElementsAndLanes()
    {
        RoadProfilesDesc d = Load(FixturePath);
        RoadProfileDesc big = d.Profiles[0];
        big.Elements = new RoadElemDesc[17];
        d.Profiles = [big];
        Assert.Equal(RoadProfileError.TooManyElements, RoadProfileValidator.Validate(in d, out _));

        // 9 полос forward: 9 * 3.5 + 2 * 0.25 = 32 м -> влезает в 4 модуля, но > 8 полос на направление.
        big.Id = "wide";
        big.WidthMm = 40000;
        var els = new RoadElemDesc[12];
        els[0] = new RoadElemDesc { Kind = RoadElemKind.Curb, WidthMm = 250 };
        for (int i = 0; i < 9; i++)
        {
            els[1 + i] = new RoadElemDesc { Kind = RoadElemKind.Lane, Dir = RoadLaneDir.Forward, WidthMm = 3500 };
        }

        els[10] = new RoadElemDesc { Kind = RoadElemKind.Curb, WidthMm = 250 };
        els[11] = new RoadElemDesc { Kind = RoadElemKind.Verge, WidthMm = 40000 - 500 - (9 * 3500) };
        big.Elements = els;
        d.Profiles = [big];
        Assert.Equal(RoadProfileError.TooManyLanes, RoadProfileValidator.Validate(in d, out _));
    }

    [Fact]
    public void Loader_RejectsFractionalMillimetresAndUnknownKeys()
    {
        string text = File.ReadAllText(FixturePath);
        Assert.Throws<ScenarioException>(() => ScenarioLoader.ParseRoadProfiles(text.Replace("\"w\": 1.25", "\"w\": 1.2501")));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.ParseRoadProfiles(text.Replace("\"module_m\": 8", "\"module_m\": 8, \"zzz\": 1")));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.ParseRoadProfiles(text.Replace("\"width_m\": 16", "\"width_m\": 12")));
    }

    [Fact]
    public void Loader_MaxGradePct_ToPermille_DefaultZero()
    {
        string text = File.ReadAllText(FixturePath);
        RoadProfilesDesc d = ScenarioLoader.ParseRoadProfiles(text.Replace("\"width_m\": 16", "\"width_m\": 16, \"max_grade_pct\": 7.5"));
        Assert.Equal(75, d.Profiles[0].MaxGradePm);   // TR3, terrain.md §4.3
        Assert.Equal(0, ScenarioLoader.ParseRoadProfiles(text).Profiles[0].MaxGradePm);
        Assert.Throws<ScenarioException>(() => ScenarioLoader.ParseRoadProfiles(text.Replace("\"width_m\": 16", "\"width_m\": 16, \"max_grade_pct\": 101")));
    }

    // ---- сеть по профилям ----

    private static ScenarioDesc Cross(RoadProfilesDesc cat, string main, string side, bool mirroredSide = false)
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
                new NodeDesc { X = 0, Z = 0, Control = NodeControl.None },       // центр
                new NodeDesc { X = 200, Z = 0, Control = NodeControl.None },     // +X
                new NodeDesc { X = -200, Z = 0, Control = NodeControl.None },    // -X
                new NodeDesc { X = 0, Z = 200, Control = NodeControl.None },     // +Z
                new NodeDesc { X = 0, Z = -200, Control = NodeControl.None },    // -Z
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
        return d;
    }

    private static Simulation Make(in ScenarioDesc d)
    {
        CreateStatus st = Simulation.TryCreate(in d, out Simulation? sim);
        Assert.Equal(CreateStatus.Ok, st);
        return sim!;
    }

    [Fact]
    public void Emission_LanesFollowProfile_OffsetsWidthsAndCounts()
    {
        RoadProfilesDesc cat = Load(FixturePath);
        ScenarioDesc d = Cross(cat, "arterial_4", "street_2");
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        Assert.NotNull(net.Profiles);
        var store = new RoadProfileStore(in cat);

        for (int s = 0; s < net.SegmentCount; s++)
        {
            ref readonly SegmentData sd = ref net.Segments[s];
            int p = sd.Profile;
            Assert.InRange(p, 0, cat.Profiles.Length - 1);
            Assert.Equal(store.LanesFwd[p], sd.LanesAB);
            Assert.Equal(store.LanesBwd[p], sd.LanesBA);
            Assert.Equal(store.Kind[p], sd.Kind);
            Assert.Equal(0, sd.Flags & SegmentFlags.Mirrored);

            Vec2Dir(net, in sd, out float dx, out float dz);
            for (int dir = 0; dir < 2; dir++)
            {
                int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
                int count = dir == 0 ? sd.LanesAB : sd.LanesBA;
                for (int k = 0; k < count; k++)
                {
                    ref readonly LaneData ld = ref net.Lanes[first + k];
                    Assert.Equal(store.LaneWidthM(p, false, dir, k), ld.Width);

                    // Боковое смещение первой точки полосы от оси: (точка - ось) · правая нормаль A→B.
                    Core.Util.Vec2 axis = net.SegmentPoints[sd.FirstPoint];
                    Core.Util.Vec2 pt = net.LanePoints[ld.FirstPoint + (dir == 0 ? 0 : ld.PointCount - 1)];
                    float lateral = ((pt.X - axis.X) * -dz) + ((pt.Z - axis.Z) * dx);
                    Assert.Equal(store.LaneCenterM(p, false, dir, k), lateral, 3);
                }
            }
        }
    }

    private static void Vec2Dir(NetworkStore net, in SegmentData sd, out float dx, out float dz)
    {
        Core.Util.Vec2 a = net.SegmentPoints[sd.FirstPoint];
        Core.Util.Vec2 b = net.SegmentPoints[sd.FirstPoint + sd.PointCount - 1];
        float len = MathF.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));
        dx = (b.X - a.X) / len;
        dz = (b.Z - a.Z) / len;
    }

    [Fact]
    public void Setback_UsesProfileCurbNotLaneCount()
    {
        RoadProfilesDesc cat = Load(FixturePath);
        ScenarioDesc d = Cross(cat, "arterial_4", "street_2");
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        var store = new RoadProfileStore(in cat);
        float r = NetworkOptions.Default.CornerRadius;
        float hMain = store.HalfRoadM(Index(in cat, "arterial_4"));
        float hSide = store.HalfRoadM(Index(in cat, "street_2"));

        // Рукава поперечной дороги отступают на h(главной) + R, рукава главной - на h(второстепенной) + R (не меньше NodeSetback).
        float expectSide = MathF.Max(NetworkOptions.Default.NodeSetback, hMain + r);
        float expectMain = MathF.Max(NetworkOptions.Default.NodeSetback, hSide + r);
        float axisLen = 200f;
        Assert.Equal(axisLen - expectMain - NetworkOptions.Default.NodeSetback, net.Segments[0].Length, 3);   // дальний конец - тупик
        Assert.Equal(axisLen - expectSide - NetworkOptions.Default.NodeSetback, net.Segments[2].Length, 3);
        Assert.Equal(MathF.Max(expectMain, expectSide), net.Nodes[0].Radius, 3);
        Assert.True(hMain > hSide);   // у артерии кромка дальше, чем у улицы
    }

    [Fact]
    public void Mirrored_SamePhysicalLanes_ForAsymmetricProfile()
    {
        RoadProfilesDesc cat = Load(FixturePath);
        int p = Index(in cat, "asym_1_2");
        var store = new RoadProfileStore(in cat);
        ScenarioDesc d = TestScenarios.Line();
        d.RoadProfiles = cat;
        d.Network = new NetworkDesc
        {
            Nodes =
            [
                new NodeDesc { X = 0, Z = 0 }, new NodeDesc { X = 200, Z = 0 },       // S1: рисуется вдоль профиля (+X)
                new NodeDesc { X = 200, Z = 100 }, new NodeDesc { X = 0, Z = 100 },   // S2: против (-X), Mirrored
            ],
            Segments =
            [
                new SegmentDesc { A = 0, B = 1, Profile = p + 1, Mirrored = false, LanesAB = store.LanesAB(p, false), LanesBA = store.LanesBA(p, false), SpeedLimit = 13.9f, Kind = store.Kind[p] },
                new SegmentDesc { A = 2, B = 3, Profile = p + 1, Mirrored = true, LanesAB = store.LanesAB(p, true), LanesBA = store.LanesBA(p, true), SpeedLimit = 13.9f, Kind = store.Kind[p] },
            ],
        };
        d.Demand.Zones = [new ZoneDesc { CenterX = 100, CenterZ = 0, HalfSize = 50, Weight = 1 }, new ZoneDesc { CenterX = 100, CenterZ = 100, HalfSize = 50, Weight = 1 }];
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        ref readonly SegmentData s1 = ref net.Segments[0];
        ref readonly SegmentData s2 = ref net.Segments[1];
        Assert.Equal(0, s1.Flags & SegmentFlags.Mirrored);
        Assert.Equal(SegmentFlags.Mirrored, s2.Flags & SegmentFlags.Mirrored);
        Assert.Equal(s1.LanesAB, s2.LanesBA);
        Assert.Equal(s1.LanesBA, s2.LanesAB);
        Assert.NotEqual(s1.LanesAB, s1.LanesBA);

        for (int dir = 0; dir < 2; dir++)
        {
            int n = dir == 0 ? s1.LanesAB : s1.LanesBA;
            for (int k = 0; k < n; k++)
            {
                LaneData l1 = net.Lanes[(dir == 0 ? s1.FirstLaneAB : s1.FirstLaneBA) + k];
                LaneData l2 = net.Lanes[(dir == 0 ? s2.FirstLaneBA : s2.FirstLaneAB) + k];
                Assert.Equal(l1.Width, l2.Width);
                Core.Util.Vec2 a1 = net.LanePoints[l1.FirstPoint];
                Core.Util.Vec2 b1 = net.LanePoints[l1.FirstPoint + l1.PointCount - 1];
                Core.Util.Vec2 a2 = net.LanePoints[l2.FirstPoint];
                Core.Util.Vec2 b2 = net.LanePoints[l2.FirstPoint + l2.PointCount - 1];
                Assert.Equal(a1.Z, a2.Z - 100f, 3);                      // тот же поперечный сдвиг
                Assert.True((b1.X - a1.X) * (b2.X - a2.X) > 0f);          // и то же направление движения
            }
        }
    }

    [Fact]
    public void Create_RejectsInconsistentProfileReferences()
    {
        RoadProfilesDesc cat = Load(FixturePath);
        ScenarioDesc ok = Cross(cat, "arterial_4", "street_2");
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in ok, out Simulation? s0));
        s0!.Dispose();

        ScenarioDesc badLanes = Cross(cat, "arterial_4", "street_2");
        badLanes.Network.Segments[0].LanesAB++;
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in badLanes, out _));

        ScenarioDesc noProfile = Cross(cat, "arterial_4", "street_2");
        noProfile.Network.Segments[1].Profile = 0;
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in noProfile, out _));

        ScenarioDesc outOfRange = Cross(cat, "arterial_4", "street_2");
        outOfRange.Network.Segments[1].Profile = 99;
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in outOfRange, out _));

        ScenarioDesc brokenCatalog = Cross(cat, "arterial_4", "street_2");
        brokenCatalog.RoadProfiles.Profiles = (RoadProfileDesc[])cat.Profiles.Clone();
        brokenCatalog.RoadProfiles.Profiles[0].WidthMm += 8000;
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in brokenCatalog, out _));

        // Ссылка на профиль без каталога - ошибка, а не молчаливый legacy.
        ScenarioDesc noCatalog = Cross(cat, "arterial_4", "street_2");
        noCatalog.RoadProfiles = default;
        Assert.Equal(CreateStatus.InvalidNetwork, Simulation.TryCreate(in noCatalog, out _));
    }

    // ---- хэш: расширяется только в режиме профилей ----

    [Fact]
    public void Legacy_ReproducesNetworkHash()
    {
        GenRun r = GenFixture.Run("s1k");
        Assert.Equal(CreateStatus.Ok, r.CityStatus);
        Assert.Null(r.Net.Profiles);
        Assert.Equal(GoldenS1kNetworkHash, r.Net.Hash());
        for (int s = 0; s < r.Net.SegmentCount; s++)
        {
            Assert.Equal(-1, r.Net.Segments[s].Profile);
        }

        for (int l = 0; l < r.Net.LaneCount; l++)
        {
            if (r.Net.Lanes[l].Kind == LaneKind.Normal)
            {
                Assert.Equal(r.Builder.Options.LaneWidth, r.Net.Lanes[l].Width);
            }
        }
    }

    [Fact]
    public void ProfileMode_HashIsDeterministic_AndSeesCatalogAndWidths()
    {
        RoadProfilesDesc cat = Load(FixturePath);
        ScenarioDesc a = Cross(cat, "arterial_4", "street_2");
        using Simulation s1 = Make(in a);
        using Simulation s2 = Make(in a);
        Assert.Equal(s1.World.Net.Hash(), s2.World.Net.Hash());
        Assert.Equal(s1.ComputeNetworkHash(), s2.ComputeNetworkHash());

        // Та же сеть без профилей (legacy): хэш другой (ширины и разделы профилей).
        ScenarioDesc legacy = TestScenarios.Line();
        using Simulation s3 = Make(in legacy);
        Assert.NotEqual(s1.World.Net.Hash(), s3.World.Net.Hash());

        // Другая ширина полосы в каталоге (сумма та же, газон компенсирует) - хэш сети другой.
        ScenarioDesc b = Cross(cat, "arterial_4", "street_2");
        b.RoadProfiles.Profiles = (RoadProfileDesc[])cat.Profiles.Clone();
        int p = Index(in cat, "arterial_4");
        b.RoadProfiles.Profiles[p].Elements = (RoadElemDesc[])cat.Profiles[p].Elements.Clone();
        b.RoadProfiles.Profiles[p].Elements[3].WidthMm -= 250;     // первая полоса уже
        b.RoadProfiles.Profiles[p].Elements[1].WidthMm += 250;     // газон шире
        using Simulation s4 = Make(in b);
        Assert.NotEqual(s1.World.Net.Hash(), s4.World.Net.Hash());
        Assert.NotEqual(s1.ComputeNetworkHash(), s4.ComputeNetworkHash());
    }

    // ---- город по профилям: детерминизм при 1/2/3 потоках, 0 B/тик, ScenarioOverrides ----

    private static ScenarioDesc S1kWithProfiles(string? overridePath)
    {
        string root = GenFixture.RepoRoot();
        JsonNode n = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", "s1k.json")))!;
        n["road_profiles_file"] = "road-profiles.json";
        n["city"]!["profiles"] = new JsonObject { ["street"] = "street_2", ["arterial"] = "arterial_4" };
        return ScenarioLoader.Parse(
            n.ToJsonString(),
            rel => File.ReadAllText(Path.IsPathRooted(rel) ? rel : Path.Combine(root, "scenarios", rel)),
            "s1k-profiles",
            null,
            overridePath == null ? null : new ScenarioOverrides { Profiles = overridePath }).Desc;
    }

    [Fact]
    public void ScenarioOverrides_ReplacesCatalog_OnlyWhenScenarioDeclaresOne()
    {
        ScenarioDesc game = S1kWithProfiles(null);
        ScenarioDesc fixture = S1kWithProfiles(FixturePath);
        Assert.True(game.RoadProfiles.Enabled && fixture.RoadProfiles.Enabled);
        Assert.Equal(Load(GamePath).Profiles.Length, game.RoadProfiles.Profiles.Length);
        Assert.Equal(Load(FixturePath).Profiles.Length, fixture.RoadProfiles.Profiles.Length);
        Assert.True(fixture.City.StreetProfile > 0 && fixture.City.ArterialProfile > 0);

        // Legacy-сценарий подмену игнорирует: остаётся legacy.
        ScenarioDesc legacy = ScenarioLoader.LoadInfo(
            Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k.json"), null, new ScenarioOverrides { Profiles = FixturePath }).Desc;
        Assert.False(legacy.RoadProfiles.Enabled);
        Assert.Equal(0, legacy.City.StreetProfile);
    }

    [Fact]
    public void Loader_RequiresProfilesForGeneratorAndForbidsThemWithoutCatalog()
    {
        string root = GenFixture.RepoRoot();
        string Read(string rel) => File.ReadAllText(Path.IsPathRooted(rel) ? rel : Path.Combine(root, "scenarios", rel));
        JsonNode n = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", "s1k.json")))!;
        n["road_profiles_file"] = "road-profiles.json";
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(n.ToJsonString(), Read, "x"));        // city.profiles нет

        JsonNode m = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", "s1k.json")))!;
        m["city"]!["profiles"] = new JsonObject { ["street"] = "street_2", ["arterial"] = "arterial_4" };
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(m.ToJsonString(), Read, "x"));        // каталога нет

        JsonNode k = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scenarios", "s1k.json")))!;
        k["road_profiles_file"] = "road-profiles.json";
        k["city"]!["profiles"] = new JsonObject { ["street"] = "arterial_4", ["arterial"] = "arterial_4" };
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(k.ToJsonString(), Read, "x"));        // вид профиля не совпал
    }

    [Fact]
    public void ProfileCity_Generates_WithProfileGeometry()
    {
        ScenarioDesc d = S1kWithProfiles(FixturePath);
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        RoadProfileStore store = net.Profiles!;
        int street = d.City.StreetProfile - 1;
        int arterial = d.City.ArterialProfile - 1;
        int streets = 0;
        int arterials = 0;
        for (int s = 0; s < net.SegmentCount; s++)
        {
            ref readonly SegmentData sd = ref net.Segments[s];
            if (!NetworkAlive.IsAlive(sd.Generation))
            {
                continue;
            }

            Assert.Equal(sd.Kind == RoadKind.Arterial ? arterial : street, sd.Profile);
            Assert.Equal(store.LanesFwd[sd.Profile], sd.LanesAB);
            Assert.Equal(store.LanesBwd[sd.Profile], sd.LanesBA);
            if (sd.Kind == RoadKind.Arterial)
            {
                arterials++;
            }
            else
            {
                streets++;
            }
        }

        Assert.True(streets > 0 && arterials > 0);
    }

    [Fact]
    public void ProfileCity_StateHash_IsSame_For1To3Threads_AndStepAllocatesNothing()
    {
        ScenarioDesc d = S1kWithProfiles(FixturePath);
        ulong h1 = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
            sim.Step(400);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                h1 = h;
                Assert.NotEqual(0UL, h1);
                Assert.True(sim.World.Vehicles.AliveCount > 0);
            }
            else
            {
                Assert.Equal(h1.ToString("x16", CultureInfo.InvariantCulture), h.ToString("x16", CultureInfo.InvariantCulture));
            }
        }

        using Simulation one = Simulation.Create(in d);
        one.Step(600);
        Assert.Equal(0, AllocationProbe.Measure(() => one.Step(200)));
    }
}
