using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Routing;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Network;

/// <summary>
/// Канонический порядок раскладки сети (save-format.md §13 п. 5): connector'ы и конфликты (<see cref="ConnectorStore"/>) лежат по возрастанию индекса узла, входы/выходы
/// оверлея - по возрастанию DirSeg с запасом <see cref="Overlay.CapFor"/>, то есть раскладка живого мира после любой цепочки <c>BuildRoad</c> совпадает с раскладкой,
/// построенной заново по тем же узлам и сегментам.
/// </summary>
public class CanonicalLayoutTests
{
    // Три узла на прямой + два пустых узла в углах (bbox сети охватывает все будущие дороги: сетка оверлея при пересборке та же, что у живого).
    private static ScenarioDesc Desc()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Capacities.MaxNodes = 256;
        d.Capacities.MaxSegments = 256;
        d.Capacities.MaxLanes = 4096;
        d.Capacities.MaxLanePoints = 32768;
        d.Capacities.CommandQueueCapacity = 64;
        d.Capacities.EventCapacity = 512;
        d.Network.Nodes = new[]
        {
            d.Network.Nodes[0], d.Network.Nodes[1], d.Network.Nodes[2],
            new NodeDesc { X = 0, Z = 300, Control = NodeControl.None },
            new NodeDesc { X = 400, Z = 300, Control = NodeControl.None },
        };
        d.Routing = OverlayFixture.Desc(2, 100f, fanout: 2);
        return d;
    }

    private static BuildRoadCmd Road(float ax, float az, float bx, float bz, int lanes = 1, RoadKind kind = RoadKind.Street, byte flags = 0) => new()
    {
        Ax = ax,
        Az = az,
        Bx = bx,
        Bz = bz,
        SpeedLimit = 13.9f,
        LanesAB = (byte)lanes,
        LanesBA = (byte)lanes,
        Kind = kind,
        Flags = flags,
    };

    private static void Apply(Simulation sim, in BuildRoadCmd r)
    {
        Assert.True(sim.Enqueue(Command.MakeBuildRoad(in r)));
        sim.Step(1);
        for (int i = 0; i < 3000 && sim.World.Overlay != null && sim.World.Overlay.WaveActive; i++)
        {
            sim.Step(1);
        }
    }

    private static long Rejected(Simulation sim) => sim.Counters.CommandsRejected;

    [Fact]
    public void LiveLayout_EqualsLayoutBuiltFromScratch_AfterBuildRoads()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;

        // Новые дороги: одиночные; перекрёсток с несколькими рукавами (узел создан дорогой без connector'ов, достроен следующей); магистрали; дорога без connector'ов.
        Apply(sim, Road(10, 30, 150, 30));
        Apply(sim, Road(250, 30, 380, 30, lanes: 2, kind: RoadKind.Arterial));
        Apply(sim, Road(10, 100, 150, 100, flags: 1));
        Apply(sim, Road(150, 100, 150, 160, lanes: 2, kind: RoadKind.Arterial));
        Apply(sim, Road(300, 200, 300, 260, lanes: 3, kind: RoadKind.Arterial));
        Apply(sim, Road(250, 140, 380, 140, flags: 1));
        Apply(sim, Road(10, 200, 150, 200));
        Assert.Equal(0, Rejected(sim));

        // ConnectorStore: сброс идентификаторов групп и повторное построение по узлам в порядке индекса даёт те же массивы.
        ConnectorStore live = w.Connectors;
        ulong liveHash = live.Hash();
        int[] groups = new int[w.Net.LaneCount];
        for (int l = 0; l < groups.Length; l++)
        {
            groups[l] = w.Net.Lanes[l].ConflictGroup;
            w.Net.Lanes[l].ConflictGroup = -1;
        }

        var fresh = new ConnectorStore(live.MaxConnectors, live.MaxConflicts);
        Assert.Equal(NetworkStatus.Ok, fresh.BuildAll(w.Net));
        Assert.Equal(live.Count, fresh.Count);
        Assert.Equal(live.ConflictsUsed, fresh.ConflictsUsed);
        Assert.Equal(liveHash, fresh.Hash());
        for (int l = 0; l < groups.Length; l++)
        {
            Assert.Equal(groups[l], w.Net.Lanes[l].ConflictGroup);
        }

        // Узлы с connector'ами идут по возрастанию индекса (id группы растёт вместе с узлом).
        int prevNode = -1;
        for (int g = 0; g < live.Count; g++)
        {
            Assert.True(live.Node[g] >= prevNode);
            prevNode = live.Node[g];
        }

        // Оверлей: раскладка входов/выходов и запасы - функция сети.
        Overlay ov = w.Overlay!;
        Assert.False(ov.Disabled);
        CreateStatus st = Overlay.TryCreate(w.Net, w.Graph, in w.Routing, out Overlay? scratch);
        Assert.Equal(CreateStatus.Ok, st);
        Assert.Equal(scratch!.CellCap, ov.CellCap);
        for (int c = 0; c < ov.CellCap; c++)
        {
            Assert.Equal(scratch.EntryCount[c], ov.EntryCount[c]);
            Assert.Equal(scratch.ExitCount[c], ov.ExitCount[c]);
            Assert.Equal(scratch.EntryCap[c], ov.EntryCap[c]);
            Assert.Equal(scratch.ExitCap[c], ov.ExitCap[c]);
            Assert.Equal(scratch.EntryFirst[c], ov.EntryFirst[c]);
            Assert.Equal(scratch.ExitFirst[c], ov.ExitFirst[c]);
            Assert.Equal(scratch.MatrixFirst[c], ov.MatrixFirst[c]);
            for (int k = 0; k < ov.EntryCount[c]; k++)
            {
                Assert.Equal(scratch.Entries[scratch.EntryFirst[c] + k], ov.Entries[ov.EntryFirst[c] + k]);
            }

            for (int k = 0; k < ov.ExitCount[c]; k++)
            {
                Assert.Equal(scratch.Exits[scratch.ExitFirst[c] + k], ov.Exits[ov.ExitFirst[c] + k]);
            }
        }

        Assert.True(OverlayFixture.AssertMatricesMatchReference(ov, w.Graph) > 0);
    }

    [Fact]
    public void CapFor_IsLadderWithMinimumSlack()
    {
        int prev = 0;
        for (int c = 0; c < 5000; c++)
        {
            int cap = Overlay.CapFor(c);
            Assert.True(cap >= c + Overlay.MinSlack);
            Assert.True(cap >= prev); // монотонна
            Assert.True(cap - c <= Overlay.MinSlack + Math.Max(4, (c + Overlay.MinSlack) / 4)); // запас не раздувается
            Assert.Equal(cap, Overlay.CapFor(Math.Max(0, cap - Overlay.MinSlack))); // ступенька - неподвижная точка: растить в её пределах можно без пересчёта
            prev = cap;
        }

        Assert.Equal(4, Overlay.CapFor(0));
        Assert.Equal(8, Overlay.CapFor(1));
        Assert.Equal(8, Overlay.CapFor(4));
        Assert.Equal(12, Overlay.CapFor(5));
        Assert.Equal(16, Overlay.CapFor(12));
        Assert.Equal(20, Overlay.CapFor(13));
        Assert.Equal(40, Overlay.CapFor(33));
    }

    [Fact]
    public void BuildRoad_ToUnbuiltNodeBelowBuiltOnes_IsRejectedWithoutChanges()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        Apply(sim, Road(10, 30, 150, 30, flags: 1));            // узлы без connector'ов (индексы 5, 6)
        Apply(sim, Road(250, 30, 380, 30));                      // построенные узлы 7, 8 - выше
        int unbuilt = w.Builder.FindNode(new Sim.Core.Util.Vec2(10, 30));
        Assert.True(unbuilt >= 0 && w.Net.Nodes[unbuilt].FirstArm < 0);
        Assert.True(unbuilt < w.Net.HighestBuiltNode());

        int nodes = w.Net.NodeCount;
        int segs = w.Net.SegmentCount;
        long rejected = Rejected(sim);
        Apply(sim, Road(10, 30, 10, 90));                        // присоединение к «низкому» неотстроенному узлу
        Assert.Equal(rejected + 1, Rejected(sim));
        Assert.Equal(nodes, w.Net.NodeCount);
        Assert.Equal(segs, w.Net.SegmentCount);
    }

    [Fact]
    public void BuildRoad_BuildsLowerNodeFirst()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        Apply(sim, Road(10, 30, 150, 30, flags: 1));            // неотстроенные узлы 5 (10,30) и 6 (150,30), оба выше построенных
        int low = w.Builder.FindNode(new Sim.Core.Util.Vec2(150, 30));
        Assert.True(low >= 0 && w.Net.Nodes[low].FirstArm < 0);

        // Рукав: от нового узла (A) к существующему неотстроенному (B с меньшим индексом): строить надо сначала B.
        Apply(sim, Road(150, 90, 150, 30));
        Assert.Equal(0, Rejected(sim));
        int created = w.Builder.FindNode(new Sim.Core.Util.Vec2(150, 90));
        Assert.True(created > low);
        Assert.True(w.Net.Nodes[low].FirstArm >= 0 && w.Net.Nodes[created].FirstArm >= 0);
        Assert.True(w.Net.Nodes[low].FirstConnector < w.Net.Nodes[created].FirstConnector);
        Assert.True(w.Net.Lanes[w.Net.Nodes[low].FirstConnector].ConflictGroup < w.Net.Lanes[w.Net.Nodes[created].FirstConnector].ConflictGroup);
    }
}
