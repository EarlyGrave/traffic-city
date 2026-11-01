using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Intersections;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Tools;

namespace Sim.Core.Tests.Api;

/// <summary>N2: знаки рукавов и план светофора узла, флаг «заброшено» участка читаются из ядра (в том числе после Load без desc); чтение без аллокаций.</summary>
public sealed class ViewApiSignsTests
{
    [Fact]
    public void ArmFlagBits_MatchInternalBits()
    {
        Assert.Equal(NetworkBuilder.ArmMinorBit, ArmFlagBits.Minor);
        Assert.Equal(NetworkBuilder.ArmForceMajorBit, ArmFlagBits.ForceMajor);
        Assert.Equal(NetworkBuilder.ArmForceMinorBit, ArmFlagBits.ForceMinor);
        Assert.Equal(NetworkBuilder.ArmStopBit, ArmFlagBits.Stop);
    }

    [Fact]
    public void ArmSigns_AreReported_AndSurviveSaveLoad()
    {
        using Simulation sim = ToolsFixture.Small();
        int node = ToolsFixture.FindNode(sim, 4, NodeControl.RightHandRule);
        Assert.True(node >= 0);
        Assert.Equal(0, sim.Network.ArmSign(node, 1));
        var cmd = Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 1, Control = 3 }, 3);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in cmd));
        cmd = Command.MakeSetArmControl(new SetArmControlCmd { Node = ToolsFixture.Node(sim, node), ArmSlot = 2, Control = 1 }, 4);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, in cmd));

        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim), 1);
        foreach (Simulation s in new[] { sim, loaded })
        {
            NetworkView v = s.Network;
            Assert.Equal(3, v.ArmSign(node, 1));
            Assert.Equal(1, v.ArmSign(node, 2));
            Assert.Equal(0, v.ArmSign(node, 0));
            Assert.Equal(0, v.ArmSign(node, 99));
            int first = v.Nodes[node].FirstArm;
            Assert.Equal(sim.World.Net.NodeArmCount, v.NodeArms.Length);
            Assert.Equal(v.NodeArms.Length, v.ArmFlags.Length);
            Assert.NotEqual(0, v.ArmFlags[first + 1] & ArmFlagBits.Stop);
            Assert.True(v.NodeArms[first + 1] >= 0);
        }
    }

    [Fact]
    public void SignalPlan_IsReported_AndSurvivesSaveLoad()
    {
        ScenarioDesc d = SignalFixture.Crosses(2);
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        int c0 = SignalFixture.Center(0);
        int c1 = SignalFixture.Center(1);
        Assert.False(sim.TryGetSignalPlan(c0, out _));
        Assert.False(sim.TryGetSignalPlan(-1, out _));
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(c0, NodeControl.Signal, 1)));
        SetSignalPlanCmd p = SignalFixture.Plan(300, 200, 50, 20, 40);
        p.Node = c0;
        Assert.True(sim.Enqueue(Command.MakeSetSignalPlan(in p, 2)));
        sim.Step(10);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(c1, NodeControl.Signal, 3)));
        sim.Step(10);

        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim), 1);
        foreach (Simulation s in new[] { sim, loaded })
        {
            Assert.True(s.TryGetSignalPlan(c0, out SetSignalPlanCmd r));
            Assert.Equal(c0, r.Node);
            Assert.Equal(SignalMode.Fixed, r.Mode);
            Assert.Equal(2, r.PhaseCount);
            Assert.Equal(300, r.Green0);
            Assert.Equal(200, r.Green1);
            Assert.Equal(50, r.YellowTicks);
            Assert.Equal(20, r.AllRedTicks);
            Assert.Equal(40, r.OffsetTicks);
            Assert.True(s.TryGetSignalPlan(c1, out SetSignalPlanCmd r1));
            Assert.Equal(SignalMode.Adaptive, r1.Mode);   // план по умолчанию после SetNodeControl(Signal)
            Assert.False(s.TryGetSignalPlan(c0 + 1, out _));
        }
    }

    [Fact]
    public void LotFlags_Abandoned_IsSet_AndSurvivesSaveLoad()
    {
        using Simulation sim = Simulation.Create(CityFixtures.S1kRes(), new SimRuntimeOptions { WorkerThreads = 1 });
        CityWorld c = sim.World.City!;
        int lot = -1;
        for (int i = 0; i < c.Lots.Count && lot < 0; i++)
        {
            if (c.Lots.Building[i] >= 0 && c.Buildings.State[c.Lots.Building[i]] != (byte)BuildingState.Abandoned)
            {
                lot = i;
            }
        }

        Assert.True(lot >= 0);
        var lots = new LotRenderData[sim.LotCount];
        Assert.True(sim.GetLots(lots) > 0);
        Assert.Equal(0, lots[lot].Flags & LotRenderFlags.Abandoned);
        Growth.Abandon(sim.World, c, c.Lots.Building[lot]);
        Assert.True(sim.GetLots(lots) > 0);
        Assert.NotEqual(0, lots[lot].Flags & LotRenderFlags.Abandoned);
        Assert.NotEqual(0, lots[lot].Flags & LotRenderFlags.HasBuilding);

        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim), 1);
        var lots2 = new LotRenderData[loaded.LotCount];
        Assert.True(loaded.GetLots(lots2) > 0);
        Assert.NotEqual(0, lots2[lot].Flags & LotRenderFlags.Abandoned);
    }

    [Fact]
    public void Reads_DoNotAllocate()
    {
        using Simulation sim = ToolsFixture.Small();
        var lots = Array.Empty<LotRenderData>();
        sim.TryGetSignalPlan(0, out _);
        _ = sim.Network.ArmSign(0, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            sim.TryGetSignalPlan(i % 8, out _);
            _ = sim.Network.ArmSign(i % 8, 0);
            sim.GetLots(lots);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
