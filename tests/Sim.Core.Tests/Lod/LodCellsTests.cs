using System;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.LodTests;

/// <summary>L1 шага 2.6 (ADR-0013, sim-lod.md §1, §9): каркас LOD - ячейки, привязка узлов/полос, состояние уровней, режим off.</summary>
public class LodCellsTests
{
    private static LodDesc Cfg(LodMode mode, float cellM = 1000f) => new()
    {
        Mode = mode,
        CellM = cellM,
        MicroBudget = 15000,
        FollowSelected = true,
        FocusCommands = true,
    };

    private static ScenarioDesc City(string scenario, LodMode mode, float cellM = 1000f)
    {
        ScenarioDesc d = CityWorldTests.Desc(scenario);
        d.Lod = Cfg(mode, cellM);
        return d;
    }

    private static Simulation Create(in ScenarioDesc d)
    {
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? sim));
        return sim!;
    }

    // ---- главный инвариант: узел, его входные полосы и connector'ы - в одной ячейке ----

    [Theory]
    [InlineData("s1k", 1000f)]
    [InlineData("s1k", 300f)]
    [InlineData("s10k", 1000f)]
    [InlineData("s100k", 1000f)]
    public void NodeAndItsInputLanesAndConnectors_AreInOneCell(string scenario, float cellM)
    {
        using Simulation sim = Create(City(scenario, LodMode.Auto, cellM));
        World w = sim.World;
        LodCells lod = w.Lod!;
        NetworkStore net = w.Net;
        int normalChecked = 0;
        int normalTotal = 0;
        for (int l = 0; l < net.LaneCount; l++)
        {
            if (net.Lanes[l].Kind != LaneKind.Connector)
            {
                normalTotal++;
            }
        }

        for (int n = 0; n < net.NodeCount; n++)
        {
            ref readonly NodeData nd = ref net.Nodes[n];
            int cell = lod.NodeCell[n];

            // Ячейка узла - по его координатам (независимая проверка отображения, зажим к краю).
            int cx = Math.Clamp((int)MathF.Floor((nd.Position.X - lod.OriginX) / lod.CellM), 0, lod.CellsX - 1);
            int cz = Math.Clamp((int)MathF.Floor((nd.Position.Z - lod.OriginZ) / lod.CellM), 0, lod.CellsZ - 1);
            Assert.Equal((cz * lod.CellsX) + cx, cell);

            // Входные полосы по сегментам рукавов: A->B входит в B, B->A входит в A.
            for (int a = 0; a < nd.ArmCount; a++)
            {
                ref readonly SegmentData sd = ref net.Segments[net.NodeArms[nd.FirstArm + a]];
                if (sd.NodeB == n)
                {
                    for (int k = 0; k < sd.LanesAB; k++)
                    {
                        Assert.Equal(cell, lod.LaneCell[sd.FirstLaneAB + k]);
                        normalChecked++;
                    }
                }

                if (sd.NodeA == n)
                {
                    for (int k = 0; k < sd.LanesBA; k++)
                    {
                        Assert.Equal(cell, lod.LaneCell[sd.FirstLaneBA + k]);
                        normalChecked++;
                    }
                }
            }

            // Connector'ы узла и их входные полосы.
            for (int c = 0; c < nd.ConnectorCount; c++)
            {
                int cl = nd.FirstConnector + c;
                Assert.Equal(LaneKind.Connector, net.Lanes[cl].Kind);
                Assert.Equal(cell, lod.LaneCell[cl]);
                Assert.Equal(cell, lod.LaneCell[net.Lanes[cl].FromLane]);
            }
        }

        // Каждая обычная полоса входит ровно в один узел: проверка покрыла все.
        Assert.Equal(normalTotal, normalChecked);
    }

    [Fact]
    public void LaneLists_AreCsrOfAllLanesAscendingPerCell()
    {
        using Simulation sim = Create(City("s1k", LodMode.Auto));
        LodCells lod = sim.World.Lod!;
        int lanes = sim.World.Net.LaneCount;
        Assert.Equal(0, lod.CellLaneStart[0]);
        Assert.Equal(lanes, lod.CellLaneStart[lod.Cells]);
        var seen = new bool[lanes];
        for (int c = 0; c < lod.Cells; c++)
        {
            Assert.True(lod.CellLaneStart[c] <= lod.CellLaneStart[c + 1]);
            int prev = -1;
            for (int i = lod.CellLaneStart[c]; i < lod.CellLaneStart[c + 1]; i++)
            {
                int l = lod.CellLaneList[i];
                Assert.True(l > prev);
                prev = l;
                Assert.Equal(c, lod.LaneCell[l]);
                Assert.False(seen[l]);
                seen[l] = true;
            }
        }

        Assert.DoesNotContain(false, seen);
    }

    // ---- явная сеть: точные ячейки, цвет, зажим, слоты фокуса ----

    [Fact]
    public void ExplicitLine_CellsColorsAndSlots()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = Cfg(LodMode.Auto, 100f);
        d.Lod.Focus = new[] { new LodPlaceDesc { X = 200, Z = 0, RadiusM = 150 } };
        d.Lod.Pinned = new[] { new LodPlaceDesc { X = 399, Z = 0, RadiusM = 0 } };
        using Simulation sim = Create(in d);
        LodCells lod = sim.World.Lod!;

        // bbox 0..400 по X, 0 по Z -> 4 x 1 ячейки по 100 м; узел на x = 400 зажимается в последнюю ячейку.
        Assert.Equal(4, lod.CellsX);
        Assert.Equal(1, lod.CellsZ);
        Assert.Equal(0, lod.NodeCell[0]);
        Assert.Equal(2, lod.NodeCell[1]);
        Assert.Equal(3, lod.NodeCell[2]);

        // Обычная полоса - в ячейке КОНЕЧНОГО узла: сегмент 0: полоса A->B заканчивается в узле 1, B->A - в узле 0.
        ref readonly SegmentData s0 = ref sim.World.Net.Segments[0];
        Assert.Equal(lod.NodeCell[1], lod.LaneCell[s0.FirstLaneAB]);
        Assert.Equal(lod.NodeCell[0], lod.LaneCell[s0.FirstLaneBA]);

        // Цвет = (cx & 1) | (cz & 1) << 1.
        Assert.Equal(0, lod.ColorOf(0));
        Assert.Equal(1, lod.ColorOf(1));
        Assert.Equal(0, lod.ColorOf(2));
        Assert.Equal(1, lod.ColorOf(3));

        // Слоты: 0 - фокус, 4 - закрепление; ячейки квантуются зажимом.
        LodFocusSlot f = lod.Focus[0];
        Assert.Equal((byte)LodFocusKind.Focus, f.Kind);
        Assert.Equal(0, f.Cx0);
        Assert.Equal(3, f.Cx1);
        Assert.Equal(2, f.CenterCx);
        LodFocusSlot p = lod.Focus[4];
        Assert.Equal((byte)LodFocusKind.Pin, p.Kind);
        Assert.Equal(3, p.Cx0);
        Assert.Equal(3, p.Cx1);
        Assert.Equal((byte)LodFocusKind.Clear, lod.Focus[1].Kind);
        Assert.Equal(LodCells.LevelMicro, lod.CellLevel[0]);
    }

    [Fact]
    public void BuildRoad_MapsNewNodesAndLanes_ClampedToGrid()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = Cfg(LodMode.Auto, 100f);
        using Simulation sim = Create(in d);
        LodCells lod = sim.World.Lod!;
        int lanesBefore = sim.World.Net.LaneCount;

        // Новый изолированный сегмент вне bbox по Z (500 м): сетка не растёт, ячейки зажимаются к краю (cz = 0).
        var r = new BuildRoadCmd { Ax = 0, Az = 500, Bx = 150, Bz = 500, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        sim.Enqueue(Command.MakeBuildRoad(in r));
        sim.Step(1);

        NetworkStore net = sim.World.Net;
        Assert.True(net.LaneCount > lanesBefore);
        Assert.Equal(0, lod.NodeCell[3]);
        Assert.Equal(1, lod.NodeCell[4]);
        Assert.Equal(lod.CellsX * lod.CellsZ, lod.Cells);
        for (int l = lanesBefore; l < net.LaneCount; l++)
        {
            Assert.InRange(lod.LaneCell[l], 0, lod.Cells - 1);
        }

        // CSR покрывает все полосы, включая новые, и согласован с LaneCell.
        Assert.Equal(net.LaneCount, lod.CellLaneStart[lod.Cells]);
        for (int c = 0; c < lod.Cells; c++)
        {
            for (int i = lod.CellLaneStart[c]; i < lod.CellLaneStart[c + 1]; i++)
            {
                Assert.Equal(c, lod.LaneCell[lod.CellLaneList[i]]);
            }
        }

        // Инвариант узла и его входных полос на новом узле: connector'ы построены BuildRoad'ом.
        ref readonly NodeData nd = ref net.Nodes[4];
        Assert.True(nd.ConnectorCount > 0);
        for (int c = 0; c < nd.ConnectorCount; c++)
        {
            Assert.Equal(lod.NodeCell[4], lod.LaneCell[nd.FirstConnector + c]);
            Assert.Equal(lod.NodeCell[4], lod.LaneCell[net.Lanes[nd.FirstConnector + c].FromLane]);
        }
    }

    // ---- режимы и состояние ----

    [Fact]
    public void ModeOff_AllocatesNoLodState()
    {
        ScenarioDesc d = TestScenarios.Line();
        using Simulation off = Create(in d);
        Assert.Null(off.World.Lod);
        Assert.False(off.World.LodEnabled);
        Assert.Empty(off.World.Vehicles.Lod);
        Assert.Empty(off.World.Vehicles.MesoAllow);

        d.Lod = Cfg(LodMode.Off, 50f); // секция есть, режим off - то же самое
        using Simulation off2 = Create(in d);
        Assert.Null(off2.World.Lod);
        Assert.Empty(off2.World.Vehicles.Lod);
    }

    [Fact]
    public void ModeAuto_AllocatesState_AllMesoStartsMeso()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = Cfg(LodMode.Auto, 100f);
        using Simulation auto = Create(in d);
        World w = auto.World;
        Assert.True(w.LodEnabled);
        Assert.Equal(w.Caps.MaxVehicles, w.Vehicles.Lod.Length);
        Assert.Equal(w.Caps.MaxVehicles, w.Vehicles.MesoAllow.Length);
        Assert.All(w.Lod!.CellLevel, v => Assert.Equal(LodCells.LevelMicro, v));

        d.Lod.Mode = LodMode.AllMeso;
        using Simulation meso = Create(in d);
        Assert.All(meso.World.Lod!.CellLevel, v => Assert.Equal(LodCells.LevelMeso, v));
    }

    [Fact]
    public void Defaults_AreAppliedToZeroedFields()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = new LodDesc { Mode = LodMode.Auto };
        using Simulation sim = Create(in d);
        LodDesc c = sim.World.LodCfg;
        Assert.Equal(1000f, c.CellM);
        Assert.Equal(15000, c.MicroBudget);
        Assert.Equal(100, c.EvalTicks);
        Assert.Equal(600, c.HoldTicks);
        Assert.Equal(10, c.CongestionMin);
        Assert.Equal(0.8f, c.MesoHeadwayScale);
    }

    [Fact]
    public void InvalidLod_IsRejectedByCreate()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = Cfg(LodMode.Auto, float.NaN);
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out _));

        d.Lod = Cfg(LodMode.Auto, -5f);
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out _));

        d.Lod = Cfg(LodMode.Auto, 0.01f); // 40 000 x 1 ячеек... сеть 400 м: ceil(400 / 0.01) = 40 000 < MaxCells, допустимо
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? ok));
        ok!.Dispose();

        d.Lod = Cfg(LodMode.Auto, 100f);
        d.Lod.Focus = new LodPlaceDesc[LodCells.FocusSlots + 1];
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out _));

        d.Lod = Cfg(LodMode.Auto, 100f);
        d.Lod.Pinned = new[] { new LodPlaceDesc { X = float.NaN } };
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out _));

        d.Lod = Cfg((LodMode)7, 100f);
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in d, out _));

        // Слишком много ячеек: 6 км x 6 км сети s10k при cell_m = 1 даёт > 2^20.
        ScenarioDesc big = City("s10k", LodMode.Auto, 1f);
        Assert.Equal(CreateStatus.InvalidCapacity, Simulation.TryCreate(in big, out _));
    }

    // ---- хэш ----

    private static ulong HashAfter(ScenarioDesc d, int ticks)
    {
        using Simulation sim = Create(in d);
        sim.Step(ticks);
        return sim.ComputeStateHash();
    }

    [Fact]
    public void Hash_ModeOffIgnoresLodSection_AutoIsDeterministicAndDiffers()
    {
        ScenarioDesc plain = CityWorldTests.Desc("s1k");
        ScenarioDesc offSection = plain;
        offSection.Lod = Cfg(LodMode.Off, 250f);
        Assert.Equal(HashAfter(plain, 150), HashAfter(offSection, 150));

        ScenarioDesc auto = City("s1k", LodMode.Auto);
        ulong a1 = HashAfter(auto, 150);
        Assert.Equal(a1, HashAfter(auto, 150));
        Assert.NotEqual(HashAfter(plain, 150), a1); // состояние уровней входит в хэш при mode != off

        // Фокус - часть состояния, поэтому входит в хэш.
        ScenarioDesc focused = auto;
        focused.Lod.Focus = new[] { new LodPlaceDesc { X = 0, Z = 0, RadiusM = 1500 } };
        Assert.NotEqual(a1, HashAfter(focused, 150));
    }

    // ---- 0 B/тик ----

    [Theory]
    [InlineData(LodMode.Auto)]
    [InlineData(LodMode.AllMeso)]
    public void Step_WithLodState_AllocatesZeroBytes(LodMode mode)
    {
        using Simulation sim = Create(City("s1k", mode));
        sim.Step(300); // прогрев JIT и спавн
        Assert.Equal(0, AllocationProbe.Measure(() => sim.Step(200)));
    }
}
