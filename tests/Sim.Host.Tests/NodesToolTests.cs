using System;
using System.Diagnostics;
using System.Threading;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Scenarios;
using TrafficCity.Tools;

namespace Sim.Host.Tests;

/// <summary>
/// Инструмент «Узлы» (UI10) на настоящем ядре: сценарий <c>demo-profiles</c>, центральный узел n0 (0, 0) с рукавами на В, Ю, З, С.
/// Мир инструмента читает сеть из кадров хоста, как клиент.
/// </summary>
[Collection("HostThreads")]
public class NodesToolTests
{
    private sealed class FrameWorld : INodesWorld
    {
        private readonly SimThreadHost _host;
        private SimFrame? _frame;

        public FrameWorld(SimThreadHost host) => _host = host;

        public bool TryGetNet(out NetworkView net)
        {
            if (_host.TryAcquireFrame(out SimFrame f) || _frame == null)
            {
                _frame = f;
            }

            if (_frame?.Network == null)
            {
                net = default;
                return false;
            }

            net = _frame.Network.View;
            return true;
        }
    }

    private static (SimThreadHost Host, ToolCommandService Svc, NodesTool Tool, FrameWorld World) Start()
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve("demo-profiles", null));
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        var host = new SimThreadHost(sim, new SimHostOptions());
        var svc = new ToolCommandService(host);
        var world = new FrameWorld(host);
        var tool = new NodesTool(svc, world);
        var sw = Stopwatch.StartNew();
        while (!world.TryGetNet(out _))
        {
            host.SetTargetTick(host.Tick + 1);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "нет кадра");
            Thread.Sleep(1);
        }

        return (host, svc, tool, world);
    }

    private static ToolAck WaitAck(SimThreadHost host, ToolCommandService svc)
    {
        var sw = Stopwatch.StartNew();
        ToolAck? got = null;
        void On(ToolResult r) => got = r.Ack;
        svc.ActionCompleted += On;
        while (got == null)
        {
            host.SetTargetTick(Math.Max(host.TargetTick, host.Tick + 2));
            svc.Pump();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "нет подтверждения");
            Thread.Sleep(1);
        }

        svc.ActionCompleted -= On;
        return got.Value;
    }

    /// <summary>Дождаться кадра с сетью новее <paramref name="version"/> и обновить раскладку инструмента.</summary>
    private static NetworkView WaitNet(SimThreadHost host, FrameWorld world, NodesTool tool, int version)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            host.SetTargetTick(Math.Max(host.TargetTick, host.Tick + 2));
            if (world.TryGetNet(out NetworkView net) && net.Version > version)
            {
                tool.Tick();
                return net;
            }

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "сеть не обновилась");
            Thread.Sleep(1);
        }
    }

    private static int Center(FrameWorld world)
    {
        Assert.True(world.TryGetNet(out NetworkView net));
        int node = NodeLayout.PickNode(net, 0f, 0f, 2f);
        Assert.True(node >= 0);
        return node;
    }

    [Fact]
    public void Layout_ArmsInCoreSlotOrder_EastSouthWestNorth()
    {
        (SimThreadHost host, _, NodesTool tool, FrameWorld world) = Start();
        using (host)
        {
            Assert.True(tool.Select(Center(world)));
            NodeLayout l = tool.Layout;
            Assert.Equal(4, l.Count);
            Assert.True(l.DirX(0) > 0.9f);   // В
            Assert.True(l.DirZ(1) > 0.9f);   // Ю
            Assert.True(l.DirX(2) < -0.9f);  // З
            Assert.True(l.DirZ(3) < -0.9f);  // С
            Assert.Equal(0, l.Maneuver(2, 0)); // с запада на восток - прямо

            // клик по стрелке выбирает именно этот манёвр
            var p = l.Arrow(2, 0);
            tool.SetMode(NodesMode.Bans);
            tool.OnHover(new ToolPointer(p.X, p.Z, true));
            Assert.Equal(2, tool.HoverFrom);
            Assert.Equal(0, tool.HoverTo);
        }
    }

    [Fact]
    public void TurnBan_Toggle_SetsCoreBit_AndUndoClearsIt()
    {
        (SimThreadHost host, ToolCommandService svc, NodesTool tool, FrameWorld world) = Start();
        using (host)
        {
            int node = Center(world);
            Assert.True(tool.Select(node));
            Assert.True(world.TryGetNet(out NetworkView net0));
            int v0 = net0.Version;

            Assert.True(tool.ToggleBan(2, 1) > 0); // с запада на юг
            ToolAck ack = WaitAck(host, svc);
            Assert.True(ack.Ok, ack.FirstReason.ToString());
            Assert.True(ack.Recorded);
            NetworkView net = WaitNet(host, world, tool, v0);
            Assert.NotEqual(0UL, net.Nodes[node].TurnBan & (1UL << ((2 * 8) + 1)));
            Assert.True(tool.Layout.Banned(2, 1));

            svc.Undo();
            ToolAck u = WaitAck(host, svc);
            Assert.True(u.Ok);
            net = WaitNet(host, world, tool, net.Version);
            Assert.Equal(0UL, net.Nodes[node].TurnBan);
            Assert.False(tool.Layout.Banned(2, 1));
        }
    }

    [Fact]
    public void Control_SignalFixed_ThenCycleDrag_IsOneUndoItem()
    {
        (SimThreadHost host, ToolCommandService svc, NodesTool tool, FrameWorld world) = Start();
        using (host)
        {
            int node = Center(world);
            Assert.True(tool.Select(node));
            Assert.True(world.TryGetNet(out NetworkView net0));
            int v0 = net0.Version;

            Assert.True(tool.ApplyControl(NodeControlChoice.SignalFixed) > 0);
            ToolAck a = WaitAck(host, svc);
            Assert.True(a.Ok, a.FirstReason.ToString());
            Assert.Equal(2, a.Applied);
            NetworkView net = WaitNet(host, world, tool, v0);
            Assert.Equal(NodeControl.Signal, net.Nodes[node].Control);
            Assert.Equal(NodeControl.Signal, tool.Layout.Control);
            int depth = a.UndoDepth;

            Assert.True(tool.SetCycle(60, continuing: false) > 0);
            Assert.True(WaitAck(host, svc).Ok);
            Assert.True(tool.SetCycle(72, continuing: true) > 0);
            Assert.True(WaitAck(host, svc).Ok);
            Assert.True(tool.SetCycle(90, continuing: true) > 0);
            ToolAck last = WaitAck(host, svc);
            Assert.True(last.Ok);
            Assert.Equal(depth + 1, last.UndoDepth); // протяжка ползунка - один пункт
            tool.EndCycleDrag();
        }
    }

    [Fact]
    public void LaneFlag_BusOnly_And_ArmSign_AreApplied()
    {
        (SimThreadHost host, ToolCommandService svc, NodesTool tool, FrameWorld world) = Start();
        using (host)
        {
            Assert.True(world.TryGetNet(out NetworkView net0));
            int v0 = net0.Version;
            Assert.True(NodeLayout.PickLane(net0, 80f, 0f, 10f, out LaneHit lane));
            Assert.Equal(0, lane.Flags & LaneFlags.BusOnly);

            tool.SetMode(NodesMode.Lanes);
            Assert.True(tool.ToggleLaneFlag(in lane, LaneFlags.BusOnly) > 0);
            Assert.True(WaitAck(host, svc).Ok);
            NetworkView net = WaitNet(host, world, tool, v0);
            Assert.True(NodeLayout.PickLane(net, lane.Point.X, lane.Point.Z, 0.5f, out LaneHit after));
            Assert.Equal(LaneFlags.BusOnly, after.Flags & LaneFlags.BusOnly);

            Assert.True(tool.Select(Center(world)));
            Assert.True(tool.CycleArm(1) > 0);
            ToolAck arm = WaitAck(host, svc);
            Assert.True(arm.Ok, arm.FirstReason.ToString());
            Assert.Equal(1, tool.ArmControl(1));
        }
    }
}
