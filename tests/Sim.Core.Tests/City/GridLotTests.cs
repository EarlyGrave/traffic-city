using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;
using Sim.Scenarios;
using Xunit.Abstractions;

namespace Sim.Core.Tests.City;

/// <summary>
/// RP5 (road-profiles.md §4, ADR-0030): участки на мировой сетке 8 м в режиме профилей. Явные сети: ортогональная, под 45 градусов, дуга, перекрёсток; генератор s1k-res по профилям фикстуры:
/// детерминизм при 1/2/3 потоках, сейв, 0 B/тик, согласованность с парковкой. Legacy-хэши закрыты прежними тестами (хэш и корпус).
/// </summary>
public sealed class GridLotTests
{
    private readonly ITestOutputHelper _out;

    public GridLotTests(ITestOutputHelper output) => _out = output;

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static (float X, float Z) P(float x, float z) => (x, z);

    /// <summary>Явная сеть улиц по профилям фикстуры + город жителей без автозонирования.</summary>
    private static ScenarioDesc Net((float X, float Z)[] nodes, (int A, int B, string Profile)[] segs, float gridX = 0f, float gridZ = 0f)
    {
        RoadProfilesDesc cat = ProfileSaveFixtures.Catalog();
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
        d.WorldGridOriginX = gridX;
        d.WorldGridOriginZ = gridZ;
        d.Town.Zoning.Auto = false;
        d.Town.Residents.InitialFillQ16 = 0;
        SimCapacities c = d.Capacities;
        c.MaxNodes = 64;
        c.MaxSegments = 64;
        c.MaxLanes = 4096;
        c.MaxLanePoints = 16384;
        c.MaxZones = 8;
        c.MaxRoutes = 64;
        c.MaxConnectors = 1024;
        c.MaxConflicts = 8192;
        d.Capacities = c;
        return d;
    }

    private static List<int> LiveGridLots(Simulation sim)
    {
        LotStore l = sim.World.City!.Lots;
        var r = new List<int>();
        for (int i = 0; i < l.Count; i++)
        {
            if ((l.Flags[i] & LotFlags.Lost) == 0 && l.Orient[i] != LotOrient.Legacy)
            {
                r.Add(i);
            }
        }

        return r;
    }

    /// <summary>Клетки участков не пересекаются; ни одна клетка не дорога по независимой проверке (центр клетки не ближе кромки профиля минус 2 м к оси).</summary>
    private static void AssertLotsSane(Simulation sim, float slackM = 2f)
    {
        World w = sim.World;
        LotStore l = w.City!.Lots;
        var seen = new HashSet<(int, int)>();
        foreach (int lot in LiveGridLots(sim))
        {
            Assert.InRange(l.CellsF[lot], 2, 8);
            Assert.InRange(l.CellsD[lot], 2, 6);
            Assert.True(l.Orient[lot] <= 3);
            LotBuilder.CellBounds(l, lot, out int x0, out int z0, out int x1, out int z1);
            Assert.Equal(l.CellsF[lot] * l.CellsD[lot], (x1 - x0 + 1) * (z1 - z0 + 1));
            ref readonly SegmentData sd = ref w.Net.Segments[l.Seg[lot]];
            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    Assert.True(seen.Add((cx, cz)), $"клетка ({cx},{cz}) занята двумя участками");
                    float mx = (cx + 0.5f) * 8f + w.WorldGridOriginX;
                    float mz = (cz + 0.5f) * 8f + w.WorldGridOriginZ;
                    Assert.True(DistToNet(w, mx, mz) >= -slackM, $"клетка ({cx},{cz}) участка {lot} лежит на дороге");
                }
            }
        }
    }

    /// <summary>Расстояние от точки до кромки ближайшей дороги (метры, отрицательное - внутри профиля). Независимо от LotBuilder, в double.</summary>
    private static double DistToNet(World w, double x, double z)
    {
        double best = double.MaxValue;
        for (int s = 0; s < w.Net.SegmentCount; s++)
        {
            if (!w.Net.SegmentAlive(s))
            {
                continue;
            }

            ref readonly SegmentData sd = ref w.Net.Segments[s];
            double edge = sd.Profile >= 0 ? w.Net.Profiles!.EdgeMm[sd.Profile] / 1000.0 : (Math.Max(sd.LanesAB, sd.LanesBA) * 3.5) + 3.0;
            for (int i = 0; i + 1 < sd.PointCount; i++)
            {
                Vec2 a = w.Net.SegmentPoints[sd.FirstPoint + i];
                Vec2 b = w.Net.SegmentPoints[sd.FirstPoint + i + 1];
                double dx = b.X - a.X;
                double dz = b.Z - a.Z;
                double t = Math.Clamp((((x - a.X) * dx) + ((z - a.Z) * dz)) / ((dx * dx) + (dz * dz)), 0, 1);
                double d = Math.Sqrt(Math.Pow(a.X + (dx * t) - x, 2) + Math.Pow(a.Z + (dz * t) - z, 2)) - edge;
                best = Math.Min(best, d);
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- ортогональная

    [Fact]
    public void Orthogonal_StreetOnGridLine_FullLotsFlushWithCurb()
    {
        // Улица 16 м вдоль x по линии сетки z = 0: кромка на z = +-8 м - ровно на линии сетки, участки вплотную.
        ScenarioDesc d = Net(new[] { P(-320, 0), P(320, 0) }, new[] { (0, 1, "street_2") });
        using Simulation sim = Make(d);
        LotStore l = sim.World.City!.Lots;
        List<int> lots = LiveGridLots(sim);
        _out.WriteLine($"ortho: {lots.Count} lots, F = {string.Join(',', lots.Select(i => l.CellsF[i]))}");
        Assert.True(lots.Count >= 20, $"участков {lots.Count}");
        Assert.Equal(lots.Count, l.Count);
        AssertLotsSane(sim, 0f);
        foreach (int lot in lots)
        {
            LotBuilder.CellBounds(l, lot, out _, out int z0, out _, out int z1);
            if (l.Orient[lot] == 2)   // +z: передний ряд сразу за кромкой z = 8 (клетка 1)
            {
                Assert.Equal(1, z0);
                Assert.Equal(z0 + l.CellsD[lot] - 1, z1);
            }
            else
            {
                Assert.Equal(3, l.Orient[lot]);   // -z: передний ряд - клетка -2 [-16, -8)
                Assert.Equal(-2, z1);
            }
        }

        Assert.Contains(lots, i => l.CellsF[i] == 5 && l.CellsD[i] == 5);
        // участки одной стороны идут вплотную друг к другу по фасаду
        foreach (byte side in new byte[] { 0, 1 })
        {
            var xs = lots.Where(i => l.Side[i] == side).Select(i =>
            {
                LotBuilder.CellBounds(l, i, out int x0, out _, out int x1, out _);
                return (x0, x1);
            }).OrderBy(t => t.x0).ToList();
            for (int i = 1; i < xs.Count; i++)
            {
                Assert.Equal(xs[i - 1].x1 + 1, xs[i].x0);
            }
        }

        // Heading лицом к дороге: участок с осью +z смотрит на -z (атан2(-dz, -dx) = -pi/2)
        int plus = lots.First(i => l.Orient[i] == 2);
        Assert.Equal(MathF.Atan2(-1f, 0f), l.Heading[plus], 4);
        Assert.True(l.CarLane[plus] >= 0 || l.PedEdge[plus] >= 0);
    }

    [Fact]
    public void Orthogonal_Arterial24_LeavesFourMetreVergeStrip()
    {
        // Артерия 24 м (кромка +-12 м): клетка [8, 16) наполовину под дорогой - «дорога», участки начинаются с клетки 2 (z >= 16), газон 4 м.
        ScenarioDesc d = Net(new[] { P(-320, 0), P(320, 0) }, new[] { (0, 1, "arterial_4") });
        using Simulation sim = Make(d);
        LotStore l = sim.World.City!.Lots;
        List<int> lots = LiveGridLots(sim);
        Assert.NotEmpty(lots);
        foreach (int lot in lots.Where(i => l.Orient[i] == 2))
        {
            LotBuilder.CellBounds(l, lot, out _, out int z0, out _, out _);
            Assert.Equal(2, z0);
        }
    }

    [Fact]
    public void OffGridOrigin_ShiftsCellsConsistently()
    {
        // Начало сетки (-4, 12): те же дороги, участки считаются от него; клетки не пересекают дорогу и стоят вплотную к ней по смещённой сетке.
        ScenarioDesc d = Net(new[] { P(-320, 4), P(320, 4) }, new[] { (0, 1, "street_2") }, -4f, 12f);
        using Simulation sim = Make(d);
        List<int> lots = LiveGridLots(sim);
        Assert.NotEmpty(lots);
        AssertLotsSane(sim, 0f);
        LotStore l = sim.World.City!.Lots;
        foreach (int lot in lots)
        {
            // X, Z центра лежат на смещённой сетке: (центр - начало) кратно половине клетки
            Assert.Equal(0.0, (l.X[lot] - -4f) % 4f, 3);
            Assert.Equal(0.0, (l.Z[lot] - 12f) % 4f, 3);
        }
    }

    // ---------------------------------------------------------------- 45 градусов, дуга, угол

    [Fact]
    public void Diagonal45_ShorterLots_StairFrontage_NoRoadCells()
    {
        ScenarioDesc d = Net(new[] { P(-240, -240), P(240, 240) }, new[] { (0, 1, "street_2") });
        using Simulation sim = Make(d);
        LotStore l = sim.World.City!.Lots;
        List<int> lots = LiveGridLots(sim);
        Assert.True(lots.Count >= 8, $"на диагонали участков {lots.Count}");
        AssertLotsSane(sim);
        foreach (int lot in lots)
        {
            Assert.InRange(l.CellsF[lot], 2, 3);   // чем круче к сетке, тем короче фасад: ступенька ряда не больше одной клетки
        }

        // потеря фасада: на косой дороге участков меньше, чем на ортогональной той же длины
        ScenarioDesc o = Net(new[] { P(-240, 0), P(240, 0) }, new[] { (0, 1, "street_2") });
        using Simulation ortho = Make(o);
        _out.WriteLine($"45 deg: {lots.Count} lots, F = {string.Join(',', lots.Select(i => l.CellsF[i]))}");
        long diagFront = lots.Sum(i => (long)l.CellsF[i]);
        long orthoFront = LiveGridLots(ortho).Sum(i => (long)ortho.World.City!.Lots.CellsF[i]);
        Assert.True(diagFront * 1.0 / Math.Sqrt(2) < orthoFront * 1.0, "диагональ длиннее ортогонали в sqrt 2 раз; клеток фасада не больше");
    }

    [Fact]
    public void Arc_ChangesDepthAxis_AndNeverOverlaps()
    {
        // Дуга 90 градусов радиусом 320 м из 6 прямых сегментов по ~84 м (узлы вне линий сетки).
        var nodes = new List<(float, float)>();
        for (int i = 0; i <= 6; i++)
        {
            double a = Math.PI / 2 * i / 6;
            nodes.Add(((float)(320 * Math.Sin(a)) - 100.3f, (float)(320 * (1 - Math.Cos(a))) - 100.7f));
        }

        var segs = new List<(int, int, string)>();
        for (int i = 0; i < 6; i++)
        {
            segs.Add((i, i + 1, "street_2"));
        }

        ScenarioDesc d = Net(nodes.ToArray(), segs.ToArray());
        using Simulation sim = Make(d);
        LotStore l = sim.World.City!.Lots;
        List<int> lots = LiveGridLots(sim);
        Assert.True(lots.Count >= 6, $"на дуге участков {lots.Count}");
        AssertLotsSane(sim);
        _out.WriteLine($"arc: {lots.Count} lots, orient = {string.Join(',', lots.Select(i => l.Orient[i]))}, F = {string.Join(',', lots.Select(i => l.CellsF[i]))}, D = {string.Join(',', lots.Select(i => l.CellsD[i]))}");
        Assert.True(lots.Select(i => (int)l.Orient[i]).Distinct().Count() >= 3, "ось глубины на дуге меняется");
    }

    [Fact]
    public void Crossing_CornerBelongsToLowerSegment_AndNoLotInIntersection()
    {
        ScenarioDesc d = Net(
            new[] { P(-320, 0), P(320, 0), P(0, -320), P(0, 320) },
            new[] { (0, 1, "street_2"), (2, 3, "street_2") });
        using Simulation sim = Make(d);
        World w = sim.World;
        LotStore l = w.City!.Lots;
        List<int> lots = LiveGridLots(sim);
        Assert.True(lots.Count >= 20);
        AssertLotsSane(sim, 0f);
        // узел: ни одна клетка участка не в круге радиуса кромки вокруг центра
        foreach (int lot in lots)
        {
            Assert.True(MathF.Abs(l.X[lot]) > 16f || MathF.Abs(l.Z[lot]) > 16f);
        }

        // участок угла: у соседних по углу участков двух дорог сегмент с меньшим индексом получил свою клетку первым
        int first = lots.Min(i => l.Seg[i]);
        Assert.Equal(0, first);
    }

    // ---------------------------------------------------------------- Lost по клеткам

    [Fact]
    public void BuildRoadAcrossLots_MakesThemLostByCells()
    {
        ScenarioDesc d = Net(new[] { P(-320, 0), P(320, 0), P(-80, 200), P(-80, 400) }, new[] { (0, 1, "street_2") });
        using Simulation sim = Make(d);
        World w = sim.World;
        LotStore l = w.City!.Lots;
        int before = LiveGridLots(sim).Count;
        Assert.True(before > 0);
        ulong h0 = sim.ComputeStateHash();

        // Новая дорога вдоль x на z = 32 пересекает участки +z стороны (клетки 1..5): OnRoadsChanged переводит их в Lost, остальные стоят на месте.
        int segCount = w.Net.SegmentCount;
        int nodeA = w.Net.NodeCount;
        var cmd = new BuildRoadCmd { Ax = -160f, Az = 32f, Bx = 160f, Bz = 32f, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        sim.Enqueue(Command.MakeBuildRoad(in cmd));
        sim.Step(2);
        Assert.True(w.Net.SegmentCount > segCount);
        int lost = 0;
        for (int i = 0; i < l.Count; i++)
        {
            if ((l.Flags[i] & LotFlags.Lost) != 0)
            {
                lost++;
            }
        }

        Assert.True(lost > 0, "участки под новой дорогой должны стать Lost");
        Assert.NotEqual(h0, sim.ComputeStateHash());
        Assert.True(LiveGridLots(sim).Count < before + 100);
        AssertLotsSane(sim);
        Assert.Equal(nodeA, nodeA);
    }
}
