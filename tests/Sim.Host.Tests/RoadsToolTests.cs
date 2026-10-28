using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Scenarios;
using TrafficCity.Tools;

namespace Sim.Host.Tests;

/// <summary>
/// Инструмент «Дороги» (UI4) на настоящем ядре: сценарий <c>demo-profiles</c> (9 узлов, 9 сегментов с профилями, лимит 16). Мир инструмента подставной: сеть берётся из вью до старта хоста.
/// </summary>
[Collection("HostThreads")]
public class RoadsToolTests
{
    private sealed class FakeWorld : IRoadsWorld
    {
        private readonly Vec2[] _nodes;
        private readonly SegmentHit[] _segs;

        public FakeWorld(Simulation sim)
        {
            NetworkView v = sim.Network;
            Catalog = RoadCatalog.FromView(in v);
            var nodes = new List<Vec2>();
            for (int i = 0; i < v.Nodes.Length; i++)
            {
                if (v.IsNodeAlive(i))
                {
                    nodes.Add(v.Nodes[i].Position);
                }
            }

            _nodes = nodes.ToArray();
            var segs = new List<SegmentHit>();
            for (int i = 0; i < v.Segments.Length; i++)
            {
                if (v.IsSegmentAlive(i))
                {
                    SegmentData s = v.Segments[i];
                    segs.Add(new SegmentHit(v.SegmentRef(i), v.Nodes[s.NodeA].Position, v.Nodes[s.NodeB].Position, s.Profile, (s.Flags & SegmentFlags.Mirrored) != 0, s.Kind, s.LanesAB, s.LanesBA, 16f));
                }
            }

            _segs = segs.ToArray();
        }

        public RoadCatalog Catalog { get; }

        public bool SnapOn { get; set; } = true;

        public bool HasBudget => false;

        public long Balance => 0;

        public bool Paused => false;

        public Vec2 SnapToGrid(Vec2 p) => new(MathF.Round(p.X / 8f) * 8f, MathF.Round(p.Z / 8f) * 8f);

        public bool TryNode(float x, float z, float radius, out Vec2 position)
        {
            foreach (Vec2 n in _nodes)
            {
                if (((n.X - x) * (n.X - x)) + ((n.Z - z) * (n.Z - z)) <= radius * radius)
                {
                    position = n;
                    return true;
                }
            }

            position = default;
            return false;
        }

        public bool TrySegment(float x, float z, float radius, out SegmentHit hit)
        {
            foreach (SegmentHit s in _segs)
            {
                float abx = s.B.X - s.A.X;
                float abz = s.B.Z - s.A.Z;
                float t = Math.Clamp((((x - s.A.X) * abx) + ((z - s.A.Z) * abz)) / ((abx * abx) + (abz * abz)), 0f, 1f);
                float dx = s.A.X + (abx * t) - x;
                float dz = s.A.Z + (abz * t) - z;
                if ((dx * dx) + (dz * dz) <= radius * radius)
                {
                    hit = s;
                    return true;
                }
            }

            hit = default;
            return false;
        }
    }

    private static (SimThreadHost Host, ToolCommandService Svc, RoadsTool Tool, FakeWorld World) Start()
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve("demo-profiles", null));
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        var world = new FakeWorld(sim);
        var host = new SimThreadHost(sim, new SimHostOptions());
        var svc = new ToolCommandService(host);
        return (host, svc, new RoadsTool(svc, world), world);
    }

    private static ToolPointer At(float x, float z, ToolButton b = ToolButton.Primary, ToolMods m = ToolMods.None) => new(x, z, true, m, b);

    private static void Click(RoadsTool t, float x, float z)
    {
        t.OnHover(At(x, z));
        t.OnPress(At(x, z));
        t.OnRelease(At(x, z));
    }

    private static void WaitPreview(ToolCommandService svc, RoadsTool tool)
    {
        var sw = Stopwatch.StartNew();
        while (!svc.PreviewMatches(in tool.WantedCommand))
        {
            svc.Pump();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "нет ответа превью");
            Thread.Sleep(1);
        }
    }

    private static ToolAck WaitAck(SimThreadHost host, ToolCommandService svc)
    {
        var sw = Stopwatch.StartNew();
        ToolAck? got = null;
        svc.ActionCompleted += r => got = r.Ack;
        while (got == null)
        {
            host.SetTargetTick(Math.Max(host.TargetTick, host.Tick + 2));
            svc.Pump();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "нет подтверждения");
            Thread.Sleep(1);
        }

        return got.Value;
    }

    [Fact]
    public void Build_ChainOfTwo_AnchorMovesToEnd_AndGroupsAreUndoable()
    {
        (SimThreadHost host, ToolCommandService svc, RoadsTool tool, _) = Start();
        using (host)
        {
            Assert.True(tool.Catalog.FromProfiles);
            tool.OnEnter();
            Click(tool, 240, -160); // начало: кратно 8 (снап), вне узлов
            Assert.True(tool.HasAnchor);
            tool.OnHover(At(240, -240));
            WaitPreview(svc, tool);
            Assert.Equal(RejectReason.None, svc.Preview.Verdict);
            Assert.True(svc.Preview.AxisCount >= 2);
            Assert.True(tool.WantedCommand.BuildRoad.Profile > 0, "команда несёт профиль каталога");

            tool.OnRelease(At(240, -240));
            ToolAck ack = WaitAck(host, svc);
            Assert.True(ack.Ok);
            Assert.True(ack.Recorded);
            Assert.Equal(1, ack.UndoDepth);
            Assert.True(tool.HasAnchor);
            Assert.Equal(-240f, tool.Anchor.Z); // цепочка: конец стал началом
        }
    }

    [Fact]
    public void Build_CrossingMiddleOfSegment_ProfileCommandIsRejectedAsOverlap_LegacyCommandIsNot()
    {
        (SimThreadHost host, ToolCommandService svc, RoadsTool tool, _) = Start();
        using (host)
        {
            tool.OnEnter();
            Click(tool, 80, -64);
            tool.OnHover(At(80, 64)); // пересекает ось n0-nE (z = 0) в её середине
            WaitPreview(svc, tool);
            Assert.Equal(RejectReason.Overlap, svc.Preview.Verdict);
            LabelContent label = RoadsLabel.Compose(tool, svc);
            Assert.Equal(LabelSeverity.Error, label.Severity);
            Assert.Contains("Пересекает другую дорогу", label.Text, StringComparison.Ordinal);

            // клик по красному призраку ничего не ставит
            string? said = null;
            tool.Message += (t, _) => said = t;
            tool.OnRelease(At(80, 64));
            Assert.NotNull(said);

            // та же геометрия прежней командой без профиля (как в --tool-demo T13): ядро профильный Overlap не проверяет
            var legacy = new BuildRoadCmd { Ax = 80, Az = -64, Bx = 80, Bz = 64, SpeedLimit = 11f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
            Command c = Command.MakeBuildRoad(in legacy);
            svc.SetPreview(in c);
            var sw = Stopwatch.StartNew();
            while (!svc.PreviewMatches(in c))
            {
                svc.Pump();
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
                Thread.Sleep(1);
            }

            Assert.NotEqual(RejectReason.Overlap, svc.Preview.Verdict);
        }
    }

    [Fact]
    public void Upgrade_AndRemove_FormCommandsFromHoveredSegment()
    {
        (SimThreadHost host, ToolCommandService svc, RoadsTool tool, _) = Start();
        using (host)
        {
            tool.OnEnter();
            tool.SetMode(RoadsMode.Upgrade);
            tool.SetProfile(2); // arterial_4 каталога RP1
            tool.OnHover(At(0, -80)); // сегмент nN-n0: улица 1+1
            Assert.True(tool.HoverValid);
            Assert.Equal(CommandKind.UpgradeRoad, tool.WantedCommand.Kind);
            Assert.Equal((ushort)(tool.Choice.Profile + 1), tool.WantedCommand.UpgradeRoad.Profile);

            tool.SetMode(RoadsMode.Remove);
            Assert.Equal("bulldoze", tool.Id);
            Assert.Equal(CommandKind.RemoveRoad, tool.WantedCommand.Kind);
            Assert.Equal(tool.Hover.Ref.Index, tool.WantedCommand.RemoveRoad.Seg.Index);
        }
    }

    [Fact]
    public void Esc_FirstDropsChain_SecondReportsNothingToCancel()
    {
        (SimThreadHost host, ToolCommandService svc, RoadsTool tool, _) = Start();
        using (host)
        {
            tool.OnEnter();
            Click(tool, 240, -160);
            Assert.True(tool.CancelGesture());
            Assert.False(tool.HasAnchor);
            Assert.False(tool.CancelGesture());
            Click(tool, 240, -160);
            tool.OnRelease(At(240, -160, ToolButton.Secondary)); // ПКМ-клик тоже сбрасывает
            Assert.False(tool.HasAnchor);
            Assert.NotNull(svc);
        }
    }

    [Fact]
    public void Shift_DisablesGridSnap_NodeSnapWins()
    {
        (SimThreadHost host, ToolCommandService svc, RoadsTool tool, _) = Start();
        using (host)
        {
            tool.OnEnter();
            tool.OnHover(At(243.3f, -161.9f));
            Assert.Equal(PointSnap.Grid, tool.CursorSnap);
            Assert.Equal(240f, tool.Cursor.X);
            tool.OnHover(At(243.3f, -161.9f, ToolButton.Primary, ToolMods.Shift));
            Assert.Equal(PointSnap.Free, tool.CursorSnap);
            Assert.Equal(243.3f, tool.Cursor.X, 3);
            tool.OnHover(At(162f, 3f)); // рядом с узлом nE (160, 0)
            Assert.Equal(PointSnap.Node, tool.CursorSnap);
            Assert.Equal(160f, tool.Cursor.X);
            Assert.NotNull(svc);
        }
    }

    [Fact]
    public void Label_MoneyAndHints()
    {
        Assert.Equal("Улица 1+1 · 16 м", RoadCatalog.NameOf(RoadKind.Street, 1, 1, 16f));
        Assert.Equal("Магистраль, 2 в одну сторону · 24 м", RoadCatalog.NameOf(RoadKind.Arterial, 2, 0, 24f));
    }

    [Fact]
    public async System.Threading.Tasks.Task BackToBackExclusiveAccess_DoesNotDeadlock()
    {
        // клиент берёт эксклюзивный доступ подряд в одном кадре (пересборка участков, затем зданий): сброс _resume не должен потерять пробуждение потока симуляции
        (SimThreadHost host, _, _, _) = Start();
        using (host)
        {
            var done = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 600; i++)
                {
                    using (host.AcquireExclusive())
                    {
                    }

                    using (host.AcquireExclusive())
                    {
                    }
                }
            });
            Assert.Same(done, await System.Threading.Tasks.Task.WhenAny(done, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30))));
        }
    }
}
