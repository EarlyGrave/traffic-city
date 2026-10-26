using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Save;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Network;

/// <summary>
/// Сеть с дырами (4.3, T2; player-tools.md §3, §10, TL1/TL4/TL5): пулы с поколениями и LIFO, блоки арен, канон мёртвых слотов, хэш только после первой правки,
/// сейв и загрузка, handle'ы команд, <c>NetworkView</c>.
/// </summary>
public sealed class ToolStateTests
{
    // ------------------------------------------------------------ TL1: без правок ничего не меняется

    [Fact]
    public void NoEdits_ToolStateIsEmpty_AndHashSectionAbsent()
    {
        ScenarioDesc d = ToolStateFixture.Desc();
        using Simulation sim = Simulation.Create(d);
        sim.Step(200);
        World w = sim.World;
        NetworkStore n = w.Net;
        Assert.Equal(0, w.ToolsTouched);
        Assert.Equal(n.NodeCount, n.AliveNodes);
        Assert.Equal(n.SegmentCount, n.AliveSegments);
        Assert.Equal(n.BusStopCount, n.AliveBusStops);
        Assert.Equal(n.BusRouteCount, n.AliveBusRoutes);
        Assert.Equal(0, n.NodePool.FreeCount + n.SegmentPool.FreeCount + n.BusStopPool.FreeCount + n.BusRoutePool.FreeCount);
        foreach (BlockArena a in new[] { n.LaneArena, n.LanePointArena, n.SegmentPointArena, n.ArmArena, n.LaneNextArena, n.ZoneLaneArena, n.BusRouteStopArena })
        {
            Assert.Equal(0, a.FreeBlockCount);
            Assert.Equal(0, a.LeakedElements);
        }

        for (int i = 0; i < n.NodeCount; i++)
        {
            Assert.Equal(1, n.Nodes[i].Generation);
            Assert.Equal(0u, n.Nodes[i].Flags);
            Assert.Equal(0UL, n.Nodes[i].TurnBan);
        }

        Assert.Null(w.VerifyDerived());
    }

    [Fact]
    public void BuildRoadsOnly_DoesNotTouchTools_AndKeepsTightLayout()
    {
        // BuildRoad (рост сети) - не правка: блоки подряд без запаса, раскладка как раньше, хэш без раздела инструментов.
        ScenarioDesc d = ToolStateFixture.Desc();
        using Simulation sim = Simulation.Create(d);
        World w = sim.World;
        int lanesBefore = w.Net.LaneCount;
        int pointsBefore = w.Net.LanePointCount;
        sim.Step(50);
        ToolStateFixture.BuildDetached(sim);
        Assert.Equal(0, w.ToolsTouched);
        Assert.Equal(lanesBefore + 2 + 4, w.Net.LaneCount);   // 1+1 и 2+2 полосы подряд в хвосте
        Assert.True(w.Net.LanePointCount > pointsBefore);
        Assert.Null(w.VerifyDerived());
    }

    // ------------------------------------------------------------ пулы, LIFO, поколения

    [Fact]
    public void Release_FreesSlotsWithEvenGenerationAndCanonicalValues_AndTouchesTools()
    {
        ScenarioDesc d = ToolStateFixture.Desc();
        using Simulation sim = Simulation.Create(d);
        sim.Step(100);
        World w = sim.World;
        int nodes0 = w.Net.NodeCount;
        int segs0 = w.Net.SegmentCount;
        ulong h0 = sim.ComputeStateHash();
        (int s1, int s2) = ToolStateFixture.BuildDetached(sim);
        Assert.Equal(segs0, s1);
        Assert.Equal(segs0 + 1, s2);
        Assert.Equal(nodes0 + 3, w.Net.NodeCount);
        Assert.Equal(1, w.Net.Segments[s1].Generation);

        SegmentData a = w.Net.Segments[s1];
        ToolStateFixture.ReleaseDetached(sim, (s1, s2));

        Assert.Equal(1, w.ToolsTouched);
        NetworkStore n = w.Net;
        Assert.Equal(2, n.Segments[s1].Generation);
        Assert.False(n.SegmentAlive(s1));
        Assert.False(n.NodeAlive(a.NodeA));
        Assert.True(SaveValidator.DeadSegmentIsCanonical(in n.Segments[s1]));
        Assert.True(SaveValidator.DeadNodeIsCanonical(in n.Nodes[a.NodeA]));
        Assert.Equal(segs0 + 2, n.SegmentCount);          // HighWater не убывает
        Assert.Equal(segs0, n.AliveSegments);
        Assert.Equal(nodes0, n.AliveNodes);
        Assert.Equal(2, n.SegmentPool.FreeCount);
        Assert.Equal(3, n.NodePool.FreeCount);
        Assert.True(n.LaneArena.FreeBlockCount > 0 && n.LanePointArena.FreeBlockCount > 0 && n.SegmentPointArena.FreeBlockCount > 0);
        Assert.Null(w.VerifyDerived());

        for (int l = a.FirstLaneAB; l < a.FirstLaneAB + a.LanesAB + a.LanesBA; l++)
        {
            Assert.False(n.LaneAlive(l));
            Assert.True(NetworkStore.IsDeadLane(in n.Lanes[l]));
        }

        // LIFO: следующий BuildRoad занимает слоты освобождённого последним (сегмент s1 - освобождён последним среди сегментов) с поколением +2.
        (int r1, int r2) = ToolStateFixture.BuildDetached(sim, x: 4000f);
        Assert.Equal(s1, r1);
        Assert.Equal(s2, r2);
        Assert.Equal(3, n.Segments[r1].Generation);
        Assert.Equal(nodes0 + 3, n.NodeCount);   // узлы тоже в дыры
        Assert.Equal(0, n.SegmentPool.FreeCount);
        Assert.Equal(0, n.NodePool.FreeCount);
        Assert.NotEqual(h0, sim.ComputeStateHash());
        Assert.Null(w.VerifyDerived());
        sim.Step(100);
        Assert.Null(w.VerifyDerived());
    }

    [Fact]
    public void Release_RejectsBusySegmentAndBuiltNode()
    {
        ScenarioDesc d = ToolStateFixture.Desc();
        using Simulation sim = Simulation.Create(d);
        sim.Step(300);
        World w = sim.World;
        // Сегмент города с connector'ами у концов и узел с рукавами освободить нельзя (перестройка узла - T3).
        int builtSeg = 0;
        Assert.False(NetworkEdit.ReleaseSegment(w, builtSeg));
        int builtNode = w.Net.Segments[builtSeg].NodeA;
        Assert.False(NetworkEdit.ReleaseNode(w, builtNode));
        Assert.False(NetworkEdit.ReleaseSegment(w, -1));
        Assert.False(NetworkEdit.ReleaseSegment(w, w.Net.SegmentCount));
        Assert.Equal(0, w.ToolsTouched);   // отказ ничего не трогает
        Assert.Null(w.VerifyDerived());
    }

    // ------------------------------------------------------------ сейв (TL4) и хэш (TL5)

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EditedWorld_SaveLoadSave_ByteEqual_AndContinuesWithSameHashes(int threads)
    {
        using Simulation sim = ToolStateFixture.EditedWorld(threads: threads);
        Assert.Equal(1, sim.World.ToolsTouched);
        byte[] a = WorldSaveFixtures.SaveBytes(sim);
        ulong hash = sim.ComputeStateHash();
        using Simulation loaded = WorldSaveFixtures.Load(a, threads);
        Assert.Equal(hash, loaded.ComputeStateHash());
        Assert.Equal(1, loaded.World.ToolsTouched);
        Assert.Null(loaded.World.VerifyDerived());
        byte[] b = WorldSaveFixtures.SaveBytes(loaded);
        Assert.True(a.AsSpan().SequenceEqual(b), "Save -> Load -> Save не побайтно равен: " + FirstDiff(a, b));

        for (int i = 0; i < 4; i++)
        {
            sim.Step(50);
            loaded.Step(50);
            Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        }

        // Состояние арен и пулов восстановлено точно: новая стройка после загрузки занимает те же слоты и блоки (LIFO, лестница), хэши остаются равными.
        (int a1, int a2) = ToolStateFixture.BuildDetached(sim, x: 6000f);
        (int b1, int b2) = ToolStateFixture.BuildDetached(loaded, x: 6000f);
        Assert.Equal((a1, a2), (b1, b2));
        sim.Step(40);
        loaded.Step(40);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Null(sim.World.VerifyDerived());
        Assert.Null(loaded.World.VerifyDerived());
        Assert.True(WorldSaveFixtures.SaveBytes(sim).AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(loaded)));
    }

    [Fact]
    public void EditedWorld_StateHash_DoesNotDependOnThreads()
    {
        ulong h1, h2, h3;
        using (Simulation s = ToolStateFixture.EditedWorld(threads: 1))
        {
            s.Step(150);
            h1 = s.ComputeStateHash();
        }

        using (Simulation s = ToolStateFixture.EditedWorld(threads: 2))
        {
            s.Step(150);
            h2 = s.ComputeStateHash();
        }

        using (Simulation s = ToolStateFixture.EditedWorld(threads: 3))
        {
            s.Step(150);
            h3 = s.ComputeStateHash();
        }

        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3);
    }

    [Fact]
    public void EditedWorld_WithPeds_RebuildsPedNetworkAndRoundTrips()
    {
        using Simulation sim = ToolStateFixture.EditedWorld(peds: true);
        Assert.NotNull(sim.World.Net.PedNet);
        sim.Step(100);
        byte[] a = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(a);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        sim.Step(100);
        loaded.Step(100);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.True(a.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(WorldSaveFixtures.Load(a))));
    }

    [Fact]
    public void EditedWorld_WithCity_SavesLoadsAndContinues()
    {
        // Город строится от сети (участки по сегментам, дорожные компоненты): дыры в сети его производные не ломают.
        ScenarioDesc d = Sim.Core.Tests.City.CityFixtures.S1kRes();
        d.Routing.Levels = 0;
        using Simulation sim = Simulation.Create(d);
        Assert.True(sim.HasCity);
        sim.Step(200);
        (int First, int Second) segs = ToolStateFixture.BuildDetached(sim);
        sim.Step(10);
        ToolStateFixture.ReleaseDetached(sim, segs);
        sim.Step(60);
        Assert.Equal(1, sim.World.ToolsTouched);
        byte[] a = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(a);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Null(loaded.World.VerifyDerived());
        sim.Step(100);
        loaded.Step(100);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.True(a.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(WorldSaveFixtures.Load(a))));
    }

    [Fact]
    public void Load_RejectsNonCanonicalDeadSlot_AndHolesInUntouchedNetwork()
    {
        using Simulation sim = ToolStateFixture.EditedWorld();
        World w = sim.World;
        int dead = -1;
        for (int i = 0; i < w.Net.NodeCount && dead < 0; i++)
        {
            if (!w.Net.NodeAlive(i))
            {
                dead = i;
            }
        }

        Assert.True(dead >= 0);

        // Мусор в мёртвом слоте узла.
        w.Net.Nodes[dead].Elevation = 5f;
        byte[] bad = WorldSaveFixtures.SaveBytes(sim);
        Assert.NotEqual(LoadStatus.Ok, TryLoad(bad));
        w.Net.Nodes[dead].Elevation = 0f;

        // Дыры при ToolsTouched = 0 невозможны.
        w.Net.ToolsTouched = 0;
        byte[] untouched = WorldSaveFixtures.SaveBytes(sim);
        Assert.NotEqual(LoadStatus.Ok, TryLoad(untouched));
        w.Net.ToolsTouched = 1;
        Assert.Equal(LoadStatus.Ok, TryLoad(WorldSaveFixtures.SaveBytes(sim)));
    }

    private static LoadStatus TryLoad(byte[] bytes)
    {
        var o = new LoadOptions { StrictHash = 0 };
        LoadStatus st = Simulation.Load(bytes, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? loaded, out _);
        loaded?.Dispose();
        return st;
    }

    private static string FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return WorldSaveFixtures.Locate(a, i);
            }
        }

        return $"длины {a.Length} и {b.Length}";
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EditedWorld_Step_AllocatesZeroBytes(int threads)
    {
        // TL9 (часть T2): мир с дырами, раскладка по лестнице и раздел хэша инструментов не добавляют аллокаций на тик.
        using Simulation sim = ToolStateFixture.EditedWorld(threads: threads);
        sim.Step(300);
        long bytes = threads > 1
            ? Sim.Core.Tests.Fixtures.AllocationProbe.MeasureSimThreads(sim, () => sim.Step(100))
            : Sim.Core.Tests.Fixtures.AllocationProbe.Measure(() => sim.Step(100));
        Assert.Equal(0, bytes);
        ulong h = sim.ComputeStateHash();
        Assert.NotEqual(0UL, h);
    }

    // ------------------------------------------------------------ handle'ы команд, NetworkView

    [Fact]
    public void Commands_WithStaleOrDeadNodeHandle_AreRejectedAsInvalidHandle()
    {
        using Simulation sim = ToolStateFixture.EditedWorld();
        World w = sim.World;
        int dead = -1;
        for (int i = 0; i < w.Net.NodeCount && dead < 0; i++)
        {
            if (!w.Net.NodeAlive(i))
            {
                dead = i;
            }
        }

        // Живой сигнальный узел.
        int signal = -1;
        for (int i = 0; i < w.Net.NodeCount && signal < 0; i++)
        {
            if (w.Net.NodeAlive(i) && w.Net.Nodes[i].Control == NodeControl.Signal)
            {
                signal = i;
            }
        }

        Assert.True(dead >= 0 && signal >= 0);
        long invalid0 = sim.Counters.InvalidHandleOps;
        long rejected0 = sim.Counters.CommandsRejected;
        long applied0 = sim.Counters.CommandsApplied;

        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(new SetNodeControlCmd { Node = dead, Control = NodeControl.Stop })));
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(new SetNodeControlCmd { Node = signal, Control = NodeControl.Signal, NodeGen = w.Net.Nodes[signal].Generation + 2 })));
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(new SetNodeControlCmd { Node = signal, Control = NodeControl.Signal, NodeGen = w.Net.Nodes[signal].Generation })));
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(new SetNodeControlCmd { Node = signal, Control = NodeControl.Signal })));   // 0 - без проверки поколения
        sim.Step(1);

        Assert.Equal(invalid0 + 2, sim.Counters.InvalidHandleOps);
        Assert.Equal(rejected0 + 2, sim.Counters.CommandsRejected);
        Assert.Equal(applied0 + 2, sim.Counters.CommandsApplied);
    }

    [Fact]
    public void NetworkView_ReportsAliveAndHandles()
    {
        using Simulation sim = ToolStateFixture.EditedWorld();
        World w = sim.World;
        NetworkView v = sim.Network;
        int aliveNodes = 0;
        int aliveSegs = 0;
        for (int i = 0; i < v.Nodes.Length; i++)
        {
            Assert.Equal(w.Net.NodeAlive(i), v.IsNodeAlive(i));
            aliveNodes += v.IsNodeAlive(i) ? 1 : 0;
        }

        for (int i = 0; i < v.Segments.Length; i++)
        {
            aliveSegs += v.IsSegmentAlive(i) ? 1 : 0;
        }

        Assert.Equal(w.Net.AliveNodes, aliveNodes);
        Assert.Equal(w.Net.AliveSegments, aliveSegs);
        Assert.True(aliveNodes < v.Nodes.Length);
        Assert.False(v.IsNodeAlive(-1));
        Assert.False(v.IsNodeAlive(v.Nodes.Length));
        for (int l = 0; l < v.Lanes.Length; l++)
        {
            Assert.Equal(w.Net.LaneAlive(l), v.IsLaneAlive(l));
        }

        int s = 0;
        EntityRef r = v.SegmentRef(s);
        Assert.Equal(s, r.Index);
        Assert.Equal(v.Segments[s].Generation, r.Generation);
        Assert.Equal(EntityRef.None.Index, v.NodeRef(-5).Index);
    }
}
