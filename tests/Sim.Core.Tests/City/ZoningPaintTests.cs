using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.Tools;
using Sim.Core.Util;

namespace Sim.Core.Tests.City;

/// <summary>Зонирование игроком Z6 (zoning-demand.md §14): <c>PaintZone</c>, участки на <c>BuildRoad</c>, <c>Lost</c> на финале <c>RemoveRoad</c>; TL1/CZ1.</summary>
public sealed class ZoningPaintTests
{
    private static Simulation Make(int threads = 1) => Simulation.Create(CityFixtures.S1kRes(), new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    private static Command Paint(float x0, float z0, float x1, float z1, byte zone, byte density = 0, uint tag = 1) =>
        Command.MakePaintZone(new PaintZoneCmd { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, ZoneKind = zone, Density = density }, tag);

    private static Command PaintAround(LotStore l, int lot, byte zone, byte density = 0, uint tag = 1) =>
        Paint(l.X[lot] - 0.5f, l.Z[lot] - 0.5f, l.X[lot] + 0.5f, l.Z[lot] + 0.5f, zone, density, tag);

    private static int LotWithBuilding(CityWorld c, int skip = 0)
    {
        for (int i = 0; i < c.Lots.Count; i++)
        {
            if (c.Lots.Building[i] >= 0 && skip-- == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static byte Other(LotZone kind) => kind == LotZone.Industrial ? (byte)LotZone.Residential : (byte)((byte)kind + 1);

    private static LotZone KindOf(CityWorld c, int lot) => c.Types.Rows[c.Buildings.Type[c.Lots.Building[lot]]].Kind;

    // ------------------------------------------------------------ PaintZone

    [Fact]
    public void PaintZone_PaintsLotsByCenter_AndSetsPainted()
    {
        using Simulation sim = Make();
        CityWorld c = City(sim);
        LotStore l = c.Lots;
        int lot = -1;
        for (int i = 0; i < l.Count && lot < 0; i++)
        {
            if (l.Building[i] < 0)
            {
                lot = i;
            }
        }

        Assert.True(lot >= 0);
        byte[] zones = l.Zone.AsSpan(0, l.Count).ToArray();
        int version = l.Version;
        long applied = sim.Counters.CommandsApplied;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, PaintAround(l, lot, (byte)LotZone.Commercial, 1)));
        Assert.Equal((byte)LotZone.Commercial, l.Zone[lot]);
        Assert.Equal(1, l.Density[lot]);
        Assert.NotEqual(0, l.Flags[lot] & LotFlags.Painted);
        Assert.True(l.Version > version);
        Assert.Equal(1, sim.Counters.PaintCommands);
        Assert.Equal(applied + 1, sim.Counters.CommandsApplied);
        for (int i = 0; i < l.Count; i++)
        {
            bool inside = MathF.Abs(l.X[i] - l.X[lot]) <= 0.5f && MathF.Abs(l.Z[i] - l.Z[lot]) <= 0.5f;
            if (!inside)
            {
                Assert.Equal(zones[i], l.Zone[i]);
                Assert.Equal(0, l.Flags[i] & LotFlags.Painted);
            }
        }

        // ZoneKind 0 снимает зону (флаг Painted остаётся: зону задал игрок), углы прямоугольника можно давать в любом порядке.
        var swapped = Command.MakePaintZone(new PaintZoneCmd { X0 = l.X[lot] + 0.5f, Z0 = l.Z[lot] + 0.5f, X1 = l.X[lot] - 0.5f, Z1 = l.Z[lot] - 0.5f, ZoneKind = 0 }, 2);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, swapped));
        Assert.Equal(0, l.Zone[lot]);
        Assert.NotEqual(0, l.Flags[lot] & LotFlags.Painted);
    }

    [Fact]
    public void PaintZone_RuleAndArgumentRejections()
    {
        using Simulation sim = Make();
        CityWorld c = City(sim);
        Assert.Equal(250_000, c.Cfg.Zoning.MaxPaintM2);
        long rejected = sim.Counters.CommandsRejected;
        Assert.Equal(RejectReason.ZoneRule, ToolsFixture.Run(sim, Paint(0, 0, 600, 600, 1, 0, 1)));   // 360 000 м2 > max_paint_m2
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Paint(0, 0, 500, 500, 1, 0, 2)));       // ровно предел
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Paint(0, 0, 10, 10, 4, 0, 3)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Paint(0, 0, 10, 10, 1, 2, 4)));
        Assert.Equal(RejectReason.InvalidArgument, ToolsFixture.Run(sim, Paint(float.NaN, 0, 10, 10, 1, 0, 5)));
        Assert.Equal(rejected + 4, sim.Counters.CommandsRejected);
        Assert.Equal(1, sim.Counters.PaintCommands);
    }

    [Fact]
    public void PaintZone_OverBuilding_OfAnotherKind_AbandonsIt()
    {
        using Simulation sim = Make();
        CityWorld c = City(sim);
        int lot = LotWithBuilding(c);
        Assert.True(lot >= 0, "в начальном городе нет зданий");
        int bi = c.Lots.Building[lot];
        LotZone kind = KindOf(c, lot);
        Assert.NotEqual((byte)BuildingState.Abandoned, c.Buildings.State[bi]);
        long abandoned = sim.Counters.BuildingsAbandoned;

        // Тот же вид: здание остаётся, участок получает Painted.
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, PaintAround(c.Lots, lot, (byte)kind, 1, 1)));
        Assert.NotEqual((byte)BuildingState.Abandoned, c.Buildings.State[bi]);
        Assert.Equal(abandoned, sim.Counters.BuildingsAbandoned);

        // Другой вид: Abandoned (жители - Evicted, работники - ищущие), участок получает новую зону.
        byte other = Other(kind);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, PaintAround(c.Lots, lot, other, 0, 2)));
        Assert.Equal((byte)BuildingState.Abandoned, c.Buildings.State[bi]);
        Assert.Equal(abandoned + 1, sim.Counters.BuildingsAbandoned);
        Assert.Equal(other, c.Lots.Zone[lot]);
        Assert.Equal(bi, c.Lots.Building[lot]);   // снос и освобождение участка делает суточный шаг
        if (kind == LotZone.Residential)
        {
            for (int r = c.Buildings.HeadRes[bi]; r >= 0; r = c.Residents.NextHome[r])
            {
                Assert.NotEqual(0, c.Residents.Flags[r] & ResidentFlags.Evicted);
            }
        }

        Assert.Null(sim.World.VerifyDerived());
        ToolsFixture.RoundTrip(sim);
    }

    [Fact]
    public void PaintZone_IsDeterministic_AcrossThreadCounts()
    {
        ulong reference = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Make(threads);
            CityWorld c = City(sim);
            int lot = LotWithBuilding(c);
            LotZone kind = KindOf(c, lot);
            sim.Enqueue(PaintAround(c.Lots, lot, Other(kind), 1, 1));
            sim.Enqueue(Paint(c.Lots.X[5] - 80f, c.Lots.Z[5] - 80f, c.Lots.X[5] + 80f, c.Lots.Z[5] + 80f, 2, 1, 2));
            sim.Step(200);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                reference = h;
            }
            else
            {
                Assert.Equal(reference, h);
            }
        }
    }

    [Fact]
    public void PaintZone_WithoutResidents_IsNotSupported()
    {
        ScenarioDesc d = Worlds.Step2.S1k(buses: false);
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(sim, Paint(0, 0, 100, 100, 1)));
    }

    // ------------------------------------------------------------ BuildRoad -> участки

    [Fact]
    public void BuildRoad_AppendsLotsAlongTheNewSegment_InOrder_AndSurvivesSave()
    {
        using Simulation sim = Make();
        CityWorld c = City(sim);
        LotStore l = c.Lots;
        World w = sim.World;
        int before = l.Count;
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 a = w.Net.Nodes[west].Position;
        var b = new Vec2(a.X - 300f, a.Z);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakeBuildRoad(ToolsFixture.Road(a, b), 1)));
        int seg = -1;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.SegmentCreated)
            {
                seg = e.A;
            }
        }

        Assert.True(seg >= 0);
        int added = l.Count - before;
        Assert.True(added >= 2, $"вдоль нового сегмента участков мало: {added}");
        int prevS1 = -1;
        int prevSide = -1;
        for (int i = before; i < l.Count; i++)
        {
            Assert.Equal(seg, l.Seg[i]);
            Assert.Equal(w.Net.Segments[seg].Generation, l.SegGen[i]);
            Assert.Equal(0, l.Flags[i]);
            Assert.True(l.CarLane[i] >= 0);
            if (l.Side[i] == prevSide)
            {
                Assert.True(l.S0Mm[i] >= prevS1);
            }
            else
            {
                Assert.True(l.Side[i] > prevSide);
            }

            prevSide = l.Side[i];
            prevS1 = l.S1Mm[i];
        }

        Assert.Equal(added, l.Count - before);
        Assert.Null(w.VerifyDerived());
        Simulation loaded = ToolsFixture.RoundTrip(sim);
        LotStore ll = City(loaded).Lots;
        Assert.Equal(l.Count, ll.Count);
        Assert.True(l.X.AsSpan(0, l.Count).SequenceEqual(ll.X.AsSpan(0, ll.Count)));
        Assert.True(l.CarLane.AsSpan(0, l.Count).SequenceEqual(ll.CarLane.AsSpan(0, ll.Count)));
        sim.Step(40);
        loaded.Step(40);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        loaded.Dispose();
    }

    // ------------------------------------------------------------ RemoveRoad -> Lost

    [Fact]
    public void RemoveRoad_Final_MarksLotsLost_AbandonsBuildings_AndSurvivesSave()
    {
        using Simulation sim = Make();
        CityWorld c = City(sim);
        LotStore l = c.Lots;
        World w = sim.World;
        int lot = LotWithBuilding(c);
        int seg = -1;
        RejectReason r = RejectReason.Busy;
        for (int skip = 0; skip < 40 && r != RejectReason.None; skip++)
        {
            lot = LotWithBuilding(c, skip);
            seg = l.Seg[lot];
            r = ToolsFixture.Run(sim, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 1));
        }

        Assert.Equal(RejectReason.None, r);
        int gen = w.Net.Segments[seg].Generation;
        var mine = new System.Collections.Generic.List<int>();
        for (int i = 0; i < l.Count; i++)
        {
            if (l.Seg[i] == seg && l.SegGen[i] == gen)
            {
                mine.Add(i);
            }
        }

        Assert.NotEmpty(mine);
        var buildings = new System.Collections.Generic.List<int>();
        foreach (int i in mine)
        {
            Assert.Equal(0, l.Flags[i] & LotFlags.Lost);   // пока дренаж идёт, участки живы
            if (l.Building[i] >= 0)
            {
                buildings.Add(l.Building[i]);
            }
        }

        int lostEvent = 0;
        bool removed = false;
        for (int t = 0; t < 6000 && !removed; t++)
        {
            sim.Step(1);
            foreach (SimEvent e in sim.Events)
            {
                removed |= e.Kind == SimEventKind.SegmentRemoved && e.A == seg;
                if (e.Kind == SimEventKind.ZoneLotsLost && e.A == seg)
                {
                    lostEvent += e.B;
                }
            }
        }

        Assert.True(removed, "сегмент не удалён");
        Assert.Equal(mine.Count, lostEvent);
        foreach (int i in mine)
        {
            Assert.NotEqual(0, l.Flags[i] & LotFlags.Lost);
            Assert.Equal(-1, l.CarLane[i]);
            Assert.Equal(-1, l.RoadComp[i]);
        }

        foreach (int bi in buildings)
        {
            // суточный шаг мог уже снести здание; тогда слот мёртв или переиспользован - проверяем живые
            if (c.Buildings.IsAlive(bi) && c.Lots.Building[c.Buildings.Lot[bi]] == bi)
            {
                Assert.Equal((byte)BuildingState.Abandoned, c.Buildings.State[bi]);
            }
        }

        Assert.True(sim.GetCityStats(out CityStats stats));
        Assert.Equal(mine.Count, stats.LotsLost);
        Assert.Null(w.VerifyDerived());

        // Lost-участок не красится и не растёт; сейв-загрузка даёт тот же мир и тот же ход.
        long painted = sim.Counters.PaintCommands;
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Paint(-5000, -5000, -4900, -4900, 1, 0, 9)));
        Assert.Equal(painted + 1, sim.Counters.PaintCommands);
        foreach (int i in mine)
        {
            Assert.Equal(0, l.Flags[i] & LotFlags.Painted);
        }

        Simulation loaded = ToolsFixture.RoundTrip(sim);
        LotStore ll = City(loaded).Lots;
        for (int i = 0; i < l.Count; i++)
        {
            Assert.True(l.Cell[i] == ll.Cell[i] && l.CarLane[i] == ll.CarLane[i] && l.RoadComp[i] == ll.RoadComp[i] && l.PedEdge[i] == ll.PedEdge[i] && l.X[i] == ll.X[i] && l.CarS[i] == ll.CarS[i], $"участок {i}: производные после загрузки другие (lost={l.Flags[i]})");
        }

        sim.Step(60);
        loaded.Step(60);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        loaded.Dispose();
    }
}
