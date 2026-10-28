using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Sim.Core.Api;
using Sim.Scenarios;
using TrafficCity.Tools;

namespace Sim.Host.Tests;

/// <summary>
/// Инструмент «Зоны» (UI9) на настоящем ядре: сценарий <c>s1k-res</c> (режим жителей, участки). Мир инструмента читает участки под эксклюзивным доступом, как клиент.
/// </summary>
[Collection("HostThreads")]
public class ZonesToolTests
{
    private sealed class HostZonesWorld : IZonesWorld
    {
        private readonly SimThreadHost _host;
        private LotRenderData[] _lots = Array.Empty<LotRenderData>();
        private int _count;
        private int _version = -1;

        public HostZonesWorld(SimThreadHost host, long maxPaintM2)
        {
            _host = host;
            MaxPaintM2 = maxPaintM2;
        }

        public bool HasCity => true;

        public float CellM => 8f;

        public float GridOriginX => 0f;

        public float GridOriginZ => 0f;

        public long MaxPaintM2 { get; }

        public bool Paused => false;

        public LotRenderData[] Snapshot()
        {
            Refresh();
            return _lots[.._count];
        }

        public void Refresh()
        {
            using ExclusiveAccess ex = _host.AcquireExclusive();
            Simulation sim = ex.Simulation!;
            if (sim.LotsVersion == _version)
            {
                return;
            }

            _lots = new LotRenderData[sim.LotCount];
            _count = sim.GetLots(_lots);
            _version = sim.LotsVersion;
        }

        public bool TryGetLots(out ReadOnlySpan<LotRenderData> lots, out int lotsVersion)
        {
            Refresh();
            lots = _lots.AsSpan(0, _count);
            lotsVersion = _version;
            return true;
        }
    }

    private static (SimThreadHost Host, ToolCommandService Svc, ZonesTool Tool, HostZonesWorld World) Start(long maxPaintM2 = 250_000)
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve("s1k-res", null));
        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        var host = new SimThreadHost(sim, new SimHostOptions());
        var svc = new ToolCommandService(host);
        var world = new HostZonesWorld(host, maxPaintM2);
        return (host, svc, new ZonesTool(svc, world), world);
    }

    private static List<ToolAck> Acks(SimThreadHost host, ToolCommandService svc, int ticks)
    {
        var got = new List<ToolAck>();
        void On(ToolResult r) => got.Add(r.Ack);
        svc.ActionCompleted += On;
        long until = host.Tick + ticks;
        var sw = Stopwatch.StartNew();
        while (host.Tick < until || got.Count == 0)
        {
            host.SetTargetTick(Math.Max(host.TargetTick, host.Tick + 2));
            svc.Pump();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "нет подтверждения");
            Thread.Sleep(1);
            if (sw.Elapsed > TimeSpan.FromSeconds(5) && got.Count == 0)
            {
                break;
            }
        }

        svc.ActionCompleted -= On;
        return got;
    }

    /// <summary>Первый живой участок с зданием или без: берём любой не потерянный и строим клеточный прямоугольник вокруг его центра.</summary>
    private static LotRenderData PickLot(LotRenderData[] lots)
    {
        foreach (LotRenderData l in lots)
        {
            if ((l.Flags & LotRenderFlags.Lost) == 0)
            {
                return l;
            }
        }

        throw new InvalidOperationException("в сценарии нет участков");
    }

    private static ToolPointer At(float x, float z) => new(x, z, true);

    [Fact]
    public void Stroke_IsOneAction_OneCommand_AndPaintsLots()
    {
        (SimThreadHost host, ToolCommandService svc, ZonesTool tool, HostZonesWorld world) = Start();
        using (host)
        {
            LotRenderData[] before = world.Snapshot();
            LotRenderData lot = PickLot(before);
            ZoneChoice target = lot.Zone == (byte)ZoneChoice.Industrial ? ZoneChoice.Commercial : ZoneChoice.Industrial;
            tool.SetKind(target);
            tool.OnEnter();

            // протяжка в несколько шагов мыши = одна команда
            tool.OnPress(At(lot.X - 20f, lot.Z - 20f));
            tool.OnHover(At(lot.X, lot.Z));
            tool.OnHover(At(lot.X + 20f, lot.Z + 20f));
            Assert.True(tool.IsDragging);
            Assert.True(tool.Paint + tool.Abandon >= 1, "оценка видит участок в прямоугольнике");
            int expected = tool.Paint + tool.Abandon;
            long requestsBefore = svc.PreviewRequests;
            tool.OnRelease(At(lot.X + 20f, lot.Z + 20f));
            Assert.False(tool.IsDragging);

            List<ToolAck> acks = Acks(host, svc, 4);
            Assert.Single(acks);
            Assert.Equal(1, acks[0].Applied);
            Assert.Equal(0, acks[0].Rejected);
            Assert.Equal(CommandKind.PaintZone, acks[0].Subject);
            Assert.Equal(1, acks[0].Pieces);
            Assert.Equal(requestsBefore, svc.PreviewRequests); // у PaintZone нет превью ядра: оценка клиентская

            LotRenderData[] after = world.Snapshot();
            int changed = 0;
            for (int i = 0; i < after.Length; i++)
            {
                if ((after[i].Flags & LotRenderFlags.Lost) == 0 && (after[i].Zone != before[i].Zone || after[i].Density != before[i].Density))
                {
                    changed++;
                    Assert.Equal((byte)target, after[i].Zone);
                }
            }

            Assert.Equal(expected, changed); // клиентская оценка совпала с тем, что сделало ядро
            Assert.True(changed >= 1);
        }
    }

    [Fact]
    public void OverLimit_SendsNothing_AndToasts()
    {
        (SimThreadHost host, ToolCommandService svc, ZonesTool tool, HostZonesWorld world) = Start(maxPaintM2: 100);
        using (host)
        {
            LotRenderData lot = PickLot(world.Snapshot());
            string? toast = null;
            bool error = false;
            tool.Message += (t, e) =>
            {
                toast = t;
                error = e;
            };
            tool.OnEnter();
            tool.OnPress(At(lot.X - 20f, lot.Z - 20f));
            tool.OnHover(At(lot.X + 20f, lot.Z + 20f));
            Assert.True(tool.OverLimit);
            tool.OnRelease(At(lot.X + 20f, lot.Z + 20f));

            Assert.NotNull(toast);
            Assert.Contains("Не больше", toast);
            Assert.True(error);
            Assert.Empty(Acks(host, svc, 4)); // ничего не ушло в ядро
            Assert.Equal(0, svc.UndoDepth);
        }
    }

    [Fact]
    public void Cancel_And_RightClick_DiscardStroke()
    {
        (SimThreadHost host, ToolCommandService svc, ZonesTool tool, HostZonesWorld world) = Start();
        using (host)
        {
            LotRenderData lot = PickLot(world.Snapshot());
            tool.OnEnter();
            tool.OnPress(At(lot.X - 20f, lot.Z - 20f));
            tool.OnHover(At(lot.X + 20f, lot.Z + 20f));
            Assert.True(tool.CancelGesture()); // Esc: было что отменять
            Assert.False(tool.IsDragging);
            tool.OnRelease(At(lot.X, lot.Z)); // отпускание после отмены ничего не шлёт

            tool.OnPress(At(lot.X - 20f, lot.Z - 20f));
            tool.OnRelease(new ToolPointer(lot.X, lot.Z, true, ToolMods.None, ToolButton.Secondary)); // ПКМ-клик
            Assert.False(tool.IsDragging);
            Assert.False(tool.CancelGesture()); // второй Esc уже не расходуется инструментом
            Assert.Empty(Acks(host, svc, 4));
        }
    }

    [Fact]
    public void Enter_And_Exit_RaiseLayerEvents()
    {
        (SimThreadHost host, _, ZonesTool tool, _) = Start();
        using (host)
        {
            int entered = 0;
            int exited = 0;
            tool.Entered += () => entered++;
            tool.Exited += () => exited++;
            tool.OnEnter();
            tool.OnExit();
            Assert.Equal(1, entered);
            Assert.Equal(1, exited);
        }
    }
}
