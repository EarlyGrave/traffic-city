using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Terrain;
using Sim.Core.Tests.City;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Tools;
using Sim.Core.Tools;
using Sim.Core.Util;

namespace Sim.Core.Tests.Terrain;

/// <summary>
/// TR3 (terrain.md §4): высоты узлов и подгонка под предел уклона, TooSteep/NeedsBridge/TooDeep, мосты (автомат, флаг, длина, просвет), штамп и <c>Lock</c>,
/// земляные работы в цене и вне undo-кредита, производные высоты полос, сейв, детерминизм при 1/2/3 потоках.
/// </summary>
public sealed class RoadTerrainTests
{
    private const int CellMm = 8000;
    private const float MinX = -800f, MinZ = -800f, MaxX = 2600f, MaxZ = 800f;

    internal static readonly ToolsDesc Tools = new()
    {
        HasWorldBounds = true,
        BoundsMinX = -1e6f,
        BoundsMinZ = -1e6f,
        BoundsMaxX = 1e6f,
        BoundsMaxZ = 1e6f,
        Costs = new ToolCostsDesc { RoadPerMLaneStreet = 10, RoadPerMLaneArterial = 20, CurveFactorPct = 100, DemolishRefundPct = 50, EarthworkPerM3 = 2, BridgePerMLane = 60 },
    };

    /// <summary>
    /// Профиль по X (см): город (x &lt; 1000) - 50 м; 1000..1500 - 10%; 1500..1700 - 25%; дальше 150 м. Долина 8 м у x = 2100 (река 1), река 2 у x = 1850 на ровном,
    /// холм 15 м радиусом 100 м у (2400, 500).
    /// </summary>
    internal static float GroundCm(float x, float z)
    {
        float h = x < 1000f ? 5000f : x < 1500f ? 5000f + ((x - 1000f) * 10f) : x < 1700f ? 10000f + ((x - 1500f) * 25f) : 15000f;
        float dv = MathF.Abs(x - 2100f);
        if (dv < 60f)
        {
            h -= 800f * (1f - (dv / 60f));
        }

        float dx = x - 2400f;
        float dz = z - 500f;
        float dh = MathF.Sqrt((dx * dx) + (dz * dz));
        if (dh < 100f)
        {
            h += 1500f * (1f - (dh / 100f));
        }

        return h;
    }

    internal static TerrainDesc SlopeTerrain()
    {
        int nx = (int)((MaxX - MinX) * 1000f / CellMm) + 1;
        int nz = (int)((MaxZ - MinZ) * 1000f / CellMm) + 1;
        var h = new ushort[nx * nz];
        for (int iz = 0; iz < nz; iz++)
        {
            for (int ix = 0; ix < nx; ix++)
            {
                h[(iz * nx) + ix] = (ushort)MathF.Round(GroundCm(MinX + (ix * CellMm / 1000f), MinZ + (iz * CellMm / 1000f)));
            }
        }

        var t = new TerrainDesc
        {
            Enabled = true,
            CellMm = CellMm,
            HasExtent = true,
            ExtentMinX = MinX,
            ExtentMinZ = MinZ,
            ExtentMaxX = MaxX,
            ExtentMaxZ = MaxZ,
            Source = TerrainSource.Heightmap,
            SourceHeights = h,
            SourceSizeX = nx,
            SourceSizeZ = nz,
            SourceCellMm = CellMm,
            SourceOriginX = MinX,
            SourceOriginZ = MinZ,
        };
        t.Water.Rivers = new[]
        {
            new TerrainRiverDesc { Points = new[] { new Vec2(2100f, MinZ), new Vec2(2100f, MaxZ) }, WidthM = 16f, DepthM = 1.5f, FlowM3s = float.NaN },
            new TerrainRiverDesc { Points = new[] { new Vec2(1850f, MinZ), new Vec2(1850f, MaxZ) }, WidthM = 12f, DepthM = 1f, FlowM3s = float.NaN },
        };
        return t;
    }

    private static Simulation Make(int threads = 1, bool noAutoBridge = false)
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Terrain = SlopeTerrain();
        d.Terrain.NoAutoBridge = noAutoBridge;
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.SetToolsConfig(Tools);
        return sim;
    }

    private static Command Road(float ax, float az, float bx, float bz, uint tag, byte flags = 0)
    {
        BuildRoadCmd b = ToolsFixture.Road(new Vec2(ax, az), new Vec2(bx, bz));
        b.Flags = flags;
        return Command.MakeBuildRoad(in b, tag);
    }

    private static RejectReason Preview(Simulation sim, in Command cmd, out PreviewResult r, out PreviewIssue[] issues)
    {
        issues = new PreviewIssue[16];
        return sim.Preview(in cmd, out r, issues);
    }

    private static PreviewIssue? Issue(PreviewIssue[] issues, in PreviewResult r, IssueCode code)
    {
        for (int i = 0; i < r.IssuesWritten; i++)
        {
            if (issues[i].Code == code)
            {
                return issues[i];
            }
        }

        return null;
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

    private static byte LockAt(Simulation sim, float x, float z)
    {
        TerrainStore t = sim.World.Terrain!;
        return t.Lock[t.NearestSample(x, z)];
    }

    // ---------------------------------------------------------------- уклон

    [Fact]
    public void Grade_NewEnd_FittedToLimit_LaneHeightsLinear()
    {
        using Simulation sim = Make();
        Command cmd = Road(1050f, 0f, 1250f, 0f, 1);   // земля 55 -> 75 м: 10% > 8%
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
        NetworkStore n = sim.World.Net;
        ref readonly SegmentData sd = ref n.Segments[LastSegment(sim)];
        float ya = n.Nodes[sd.NodeA].Elevation;
        float yb = n.Nodes[sd.NodeB].Elevation;
        Assert.Equal(sim.World.Terrain!.GroundCmAt(1050f, 0f) / 100f, ya);   // A - по земле, двигается только B
        Assert.True(yb < (GroundCm(1250f, 0f) / 100f) - 1f, $"B {yb} не опущен");   // земля под B уже срезана штампом - сравнение с исходной
        float g = (yb - ya) / sd.Length;
        Assert.InRange(g, 0.075f, 0.0801f);

        float[] grade = n.LaneGrade!;
        float[] ly = n.LanePointY!;
        for (int l = sd.FirstLaneAB; l < sd.FirstLaneAB + sd.LanesAB; l++)
        {
            Assert.Equal(g, grade[l], 4);
            ref readonly LaneData ld = ref n.Lanes[l];
            Assert.Equal(ya, ly[ld.FirstPoint], 3);
            Assert.Equal(yb, ly[ld.FirstPoint + ld.PointCount - 1], 3);
        }

        for (int l = sd.FirstLaneBA; l < sd.FirstLaneBA + sd.LanesBA; l++)
        {
            Assert.Equal(-g, grade[l], 4);
        }

        Assert.Null(sim.World.VerifyDerived());
    }

    [Fact]
    public void Grade_BeyondCutAndFill_TooSteep_PreviewValuePermille()
    {
        using Simulation sim = Make();
        Command cmd = Road(1520f, 0f, 1680f, 0f, 1);   // 25%: подгонка упирается в max_cut/max_fill
        Assert.Equal(RejectReason.TooSteep, Preview(sim, in cmd, out PreviewResult r, out PreviewIssue[] issues));
        PreviewIssue? e = Issue(issues, in r, (IssueCode)(int)RejectReason.TooSteep);
        Assert.NotNull(e);
        Assert.InRange(e.Value.Value, 81, 250);
        Assert.Equal(RejectReason.TooSteep, ToolsFixture.Run(sim, cmd));
    }

    [Fact]
    public void Grade_ProfileMaxGrade_OverridesKindDefault()
    {
        using Simulation sim = Make();
        Assert.Equal(80, RoadTerrain.MaxGradePm(sim.World, -1, RoadKind.Street));
        Assert.Equal(60, RoadTerrain.MaxGradePm(sim.World, -1, RoadKind.Arterial));
    }

    // ---------------------------------------------------------------- мосты

    [Fact]
    public void Bridge_Auto_OverValley_FlagCostLockAndPreview()
    {
        using Simulation sim = Make();
        Command cmd = Road(2000f, 0f, 2200f, 0f, 1);
        Assert.Equal(RejectReason.None, Preview(sim, in cmd, out PreviewResult r, out PreviewIssue[] issues));
        PreviewIssue? auto = Issue(issues, in r, IssueCode.BridgeAuto);
        Assert.NotNull(auto);
        Assert.Equal(200, auto.Value.Value);
        long road = ToolValidator.RoadCost(in Tools.Costs, RoadKind.Street, 200f, 2, curve: false, bridge: true);
        PreviewIssue? ew = Issue(issues, in r, IssueCode.Earthwork);
        Assert.Equal(road + ((ew?.Value ?? 0) * 2L), r.Cost);

        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
        int seg = LastSegment(sim);
        Assert.NotEqual(0, sim.World.Net.Segments[seg].Flags & SegmentFlags.Bridge);
        Assert.Equal(0, LockAt(sim, 2104f, 0f) & TerrainLock.Network);         // под пролётом - свободно
        Assert.Equal(0, LockAt(sim, 2048f, 0f) & TerrainLock.Network);
        Assert.NotEqual(0, LockAt(sim, 2192f, 0f) & (TerrainLock.BridgeEnd | TerrainLock.NodePad));   // устой: 16 м от узла
        Assert.NotEqual(0, LockAt(sim, 2000f, 0f) & TerrainLock.NodePad);
        Assert.Null(sim.World.VerifyDerived());
    }

    [Fact]
    public void Bridge_NoAuto_NeedsBridge_FlagBuilds()
    {
        using Simulation sim = Make(noAutoBridge: true);
        Command cmd = Road(2000f, 0f, 2200f, 0f, 1);
        Assert.Equal(RejectReason.NeedsBridge, Preview(sim, in cmd, out PreviewResult r, out PreviewIssue[] issues));
        Assert.Equal(0, Issue(issues, in r, (IssueCode)(int)RejectReason.NeedsBridge)!.Value.Value);
        Assert.Equal(RejectReason.NeedsBridge, ToolsFixture.Run(sim, cmd));

        Command flagged = Road(2000f, 0f, 2200f, 0f, 2, BuildRoadFlags.Bridge);
        Assert.Equal(RejectReason.None, Preview(sim, in flagged, out r, out issues));
        Assert.Null(Issue(issues, in r, IssueCode.BridgeAuto));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, flagged));
        Assert.NotEqual(0, sim.World.Net.Segments[LastSegment(sim)].Flags & SegmentFlags.Bridge);
    }

    [Fact]
    public void Bridge_TooLong_ValueMinus2_TooLow_ValueMinus1()
    {
        using Simulation sim = Make();
        Command lng = Road(1950f, 200f, 2290f, 200f, 1);   // 340 м > max_bridge_m 300
        Assert.Equal(RejectReason.NeedsBridge, Preview(sim, in lng, out PreviewResult r, out PreviewIssue[] issues));
        Assert.Equal(-2, Issue(issues, in r, (IssueCode)(int)RejectReason.NeedsBridge)!.Value.Value);

        Command low = Road(1800f, -300f, 1900f, -300f, 2);   // река на ровном: палуба на уровне берега < вода + 2 м
        Assert.Equal(RejectReason.NeedsBridge, Preview(sim, in low, out r, out issues));
        Assert.Equal(-1, Issue(issues, in r, (IssueCode)(int)RejectReason.NeedsBridge)!.Value.Value);

        Command wet = Road(2050f, -400f, 2100f, -400f, 3);   // новый узел в реке
        Assert.Equal(RejectReason.NeedsBridge, ToolsFixture.Run(sim, wet));
    }

    [Fact]
    public void Bridge_FlagWithoutTerrain_NotSupported_OnTerrainFlatBuilds()
    {
        using Simulation sim = Make();
        Command cmd = Road(2300f, 0f, 2500f, 0f, 1, BuildRoadFlags.Bridge);
        Assert.Equal(RejectReason.None, Preview(sim, in cmd, out PreviewResult r, out _));
        Assert.Equal(ToolValidator.RoadCost(in Tools.Costs, RoadKind.Street, 200f, 2, curve: false, bridge: true), r.Cost);   // ровно: земляных работ нет
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
    }

    [Fact]
    public void Hill_TooDeep()
    {
        using Simulation sim = Make();
        Command cmd = Road(2300f, 500f, 2500f, 500f, 1);
        Assert.Equal(RejectReason.TooDeep, Preview(sim, in cmd, out _, out _));
        Assert.Equal(RejectReason.TooDeep, ToolsFixture.Run(sim, cmd));
    }

    [Fact]
    public void SplitAndMerge_KeepBridge_MixedMergeRejected()
    {
        using Simulation sim = Make();
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(2000f, 0f, 2200f, 0f, 1)));
        int seg = LastSegment(sim);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeSplitRoad(new SplitRoadCmd { Seg = ToolsFixture.Segment(sim, seg), S = 60f }, 2)));
        NetworkStore n = sim.World.Net;
        int node = -1;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.NodeCreated)
            {
                node = e.A;
            }
        }

        Assert.True(node >= 0);
        int bridges = 0;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (n.SegmentAlive(s) && (n.Segments[s].NodeA == node || n.Segments[s].NodeB == node))
            {
                Assert.NotEqual(0, n.Segments[s].Flags & SegmentFlags.Bridge);
                bridges++;
            }
        }

        Assert.Equal(2, bridges);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeMergeAtNode(new MergeAtNodeCmd { Node = ToolsFixture.Node(sim, node) }, 3)));
        Assert.NotEqual(0, n.Segments[LastSegment(sim)].Flags & SegmentFlags.Bridge);

        // Мост + дорога через узел степени 2: слияние отклоняется.
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(2200f, 0f, 2400f, 0f, 4)));
        int end = n.Segments[LastSegment(sim)].NodeA;
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Command.MakeMergeAtNode(new MergeAtNodeCmd { Node = ToolsFixture.Node(sim, end) }, 5)));
        Assert.Null(sim.World.VerifyDerived());
    }

    // ---------------------------------------------------------------- штамп, Lock, версия

    [Fact]
    public void Stamp_FlattensCorridor_LockFollowsNetwork_VersionBumps()
    {
        using Simulation sim = Make();
        TerrainStore t = sim.World.Terrain!;
        int v0 = t.Version;
        Assert.Equal(0, LockAt(sim, 1150f, 0f));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(1050f, 0f, 1250f, 0f, 1)));
        Assert.True(t.Version > v0);
        int seg = LastSegment(sim);
        NetworkStore n = sim.World.Net;
        Assert.NotEqual(0, LockAt(sim, 1150f, 0f) & TerrainLock.Road);
        Assert.NotEqual(0, LockAt(sim, 1050f, 0f) & TerrainLock.NodePad);

        // Земля в коридоре = профиль оси.
        int axisCm = RoadTerrain.SegmentYCm(sim.World, seg, 100f);
        Assert.InRange(t.GroundCmAt(1150f, 0f) - axisCm, -2, 2);
        int ground = t.GroundCmAt(1150f, 0f);

        // Тик без правок - версия стоит.
        int v1 = t.Version;
        sim.Step(5);
        Assert.Equal(v1, t.Version);

        // Снос: насыпь остаётся, коридор разблокируется.
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 2)));
        for (int i = 0; i < 40 && n.SegmentAlive(seg); i++)
        {
            sim.Step(5);
        }

        Assert.False(n.SegmentAlive(seg));
        Assert.Equal(0, LockAt(sim, 1150f, 0f));
        Assert.Equal(ground, t.GroundCmAt(1150f, 0f));
        Assert.True(t.Version > v1);
    }

    [Fact]
    public void Create_CityStamped_NodesOnGround_RiverCrossingsAreBridges()
    {
        using Simulation sim = Simulation.Create(TerrainFixtures.Demo(), new SimRuntimeOptions { WorkerThreads = 1 });
        World w = sim.World;
        NetworkStore n = w.Net;
        int bridges = 0;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            bridges += n.SegmentAlive(s) && (n.Segments[s].Flags & SegmentFlags.Bridge) != 0 ? 1 : 0;
        }

        Assert.True(bridges > 0, "река по диагонали города - мосты");
        Assert.NotNull(n.LanePointY);
        Assert.Null(w.VerifyDerived());

        // Высота машин в снапшоте - по полосе (не 0).
        sim.Step(400);
        int lifted = 0;
        foreach (VehicleRenderData v in sim.Vehicles)
        {
            lifted += v.Flags != 0 && v.Y > 1f ? 1 : 0;
        }

        Assert.True(lifted > 0);
    }

    // ---------------------------------------------------------------- деньги

    // Плоскость с уклоном 2% по X и по Z, без воды: поперечный уклон под дорогой даёт выемку и насыпь, предел уклона не мешает.
    private static TerrainDesc Tilted()
    {
        const int n = 400;
        var h = new ushort[n * n];
        for (int iz = 0; iz < n; iz++)
        {
            for (int ix = 0; ix < n; ix++)
            {
                h[(iz * n) + ix] = (ushort)(2000 + (64 * ix) + (64 * iz));
            }
        }

        return new TerrainDesc
        {
            Enabled = true,
            CellMm = 8000,
            MarginM = 200f,
            Source = TerrainSource.Heightmap,
            SourceHeights = h,
            SourceSizeX = n,
            SourceSizeZ = n,
            SourceCellMm = 32000,
            SourceOriginX = -6400f,
            SourceOriginZ = -6400f,
        };
    }

    [Fact]
    public void Earthwork_InCost_UndoRefundsRoadOnly()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Economy.CityBudget = true;
        d.Terrain = Tilted();
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        sim.SetToolsConfig(Tools);
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 a = sim.World.Net.Nodes[west].Position;
        Command build = Command.MakeBuildRoad(ToolsFixture.Road(a, new Vec2(a.X - 300f, a.Z + 40f)), 11);
        Assert.Equal(RejectReason.None, Preview(sim, in build, out PreviewResult r, out PreviewIssue[] issues));
        PreviewIssue? ew = Issue(issues, in r, IssueCode.Earthwork);
        Assert.NotNull(ew);
        long earth = ew.Value.Value * 2L;
        Assert.True(earth > 0);

        long start = sim.World.City!.Ledger.City;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, build));
        long cost = start - sim.World.City.Ledger.City;
        Assert.Equal(r.Cost, cost);
        Assert.Contains(sim.World.City.Budget!.UndoCredits, u => u.TargetGeneration != 0 && u.Amount == cost - earth);

        Command inv = default;
        foreach (InverseRecord ir in sim.Inverses)
        {
            if (ir.Tag == 11)
            {
                inv = sim.InverseCommands[ir.First];
            }
        }

        Assert.Equal(CommandKind.RemoveRoad, inv.Kind);
        inv.Tag = 12;
        sim.Step(20);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, inv));
        Assert.Equal(start - earth, sim.World.City.Ledger.City);
    }

    // ---------------------------------------------------------------- сейв, детерминизм

    private static ulong Script(int threads, bool roundTrip)
    {
        using Simulation sim = Make(threads);
        sim.Step(100);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(1050f, 0f, 1250f, 0f, 1)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(2000f, 0f, 2200f, 0f, 2)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(2300f, 0f, 2500f, 0f, 3, BuildRoadFlags.Bridge)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Road(600f, 0f, 1050f, 0f, 4)));
        sim.Step(200);
        Assert.Null(sim.World.VerifyDerived());
        if (roundTrip)
        {
            using Simulation loaded = ToolsFixture.RoundTrip(sim, threads);
            Assert.Equal(sim.World.Terrain!.Version, loaded.World.Terrain!.Version);
            Assert.True(sim.World.Terrain.Lock.AsSpan().SequenceEqual(loaded.World.Terrain.Lock));
            NetworkStore a = sim.World.Net, b = loaded.World.Net;
            Assert.Equal(a.LaneCount, b.LaneCount);
            for (int l = 0; l < a.LaneCount; l++)
            {
                if (a.LaneAlive(l))
                {
                    ref readonly LaneData ld = ref a.Lanes[l];
                    Assert.True(a.LanePointY.AsSpan(ld.FirstPoint, ld.PointCount).SequenceEqual(b.LanePointY.AsSpan(b.Lanes[l].FirstPoint, ld.PointCount)), $"lane {l}");
                    Assert.Equal(a.LaneGrade![l], b.LaneGrade![l]);
                }
            }
            loaded.Step(100);
            sim.Step(100);
            Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        }

        return sim.ComputeStateHash();
    }

    [Fact]
    public void SaveRoundTrip_DerivedRestored_HashEqual()
    {
        Script(1, roundTrip: true);
    }

    [Fact]
    public void Hash_IndependentOfThreads()
    {
        ulong h1 = Script(1, roundTrip: false);
        Assert.Equal(h1, Script(2, roundTrip: false));
        Assert.Equal(h1, Script(3, roundTrip: false));
    }
}
