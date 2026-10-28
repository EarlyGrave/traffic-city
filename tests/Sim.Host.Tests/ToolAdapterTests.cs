using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Sim.Core.Api;
using Sim.Scenarios;
using TrafficCity.Tools;

namespace Sim.Host.Tests;

/// <summary>
/// Протокол инструментов игрока через хост (T13): превью (почтовый ящик, троттлинг), группы команд, подтверждения, undo/redo, тексты ошибок.
/// Клиентский адаптер (<c>game/Scripts/Tools</c>) собран в тесты из исходников. Сценарий <c>demo-grid</c>: сеть 3x3 по 200 м, 30 машин, лимит 16 сегментов.
/// </summary>
[Collection("HostThreads")]
public class ToolAdapterTests
{
    private static SimThreadHost Start()
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve("demo-grid", null));
        return new SimThreadHost(Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 }), new SimHostOptions());
    }

    private static Command Road(float x0, float z0, float x1, float z1) => ToolDemo.MakeBuildRoad(new ToolDemoArgs(x0, z0, x1, z1));

    private static ToolResult Complete(SimThreadHost host, ToolCommandService svc, uint id)
    {
        Assert.NotEqual(0u, id);
        ToolResult? got = null;
        void On(ToolResult t)
        {
            if (t.Ack.ActionId == id)
            {
                got = t;
            }
        }

        svc.ActionCompleted += On;
        var sw = Stopwatch.StartNew();
        while (got == null)
        {
            host.SetTargetTick(Math.Max(host.TargetTick, host.Tick + 2));
            svc.Pump();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "нет подтверждения действия");
            Thread.Sleep(1);
        }

        svc.ActionCompleted -= On;
        return got.Value;
    }

    private static void WaitPreview(ToolCommandService svc)
    {
        var sw = Stopwatch.StartNew();
        while (svc.PreviewPending || !svc.HasPreview)
        {
            svc.Pump();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "нет ответа превью");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void Demo_PreviewEqualsApply_UndoRedoRestoreNetwork()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        var log = new List<string>();
        int code = ToolDemo.Run(host, svc, new ToolDemoArgs(400, 200, 600, 200), log.Add);
        Assert.True(code == 0, string.Join('\n', log));
        Assert.Contains(log, l => l.StartsWith("[tool] preview ", StringComparison.Ordinal) && l.Contains("verdict=None", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("undo restored topology: True", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("redo restored built network: True", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejected_PreviewVerdict_MatchesApply_AndNetworkUntouched()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        // 10 м < tools.min_segment_m
        Command bad = Road(500, 500, 510, 500);
        svc.SetPreview(in bad);
        WaitPreview(svc);
        RejectReason verdict = svc.Preview.Verdict;
        Assert.NotEqual(RejectReason.None, verdict);
        ToolDemo.NetState before = ToolDemo.Snapshot(host);

        ToolResult res = Complete(host, svc, svc.Submit(in bad));
        Assert.False(res.Ok);
        Assert.Equal(verdict, res.Ack.FirstReason);
        Assert.Equal(0, res.Ack.Applied);
        Assert.False(res.Ack.Recorded);
        Assert.Equal(RejectMessages.Full(verdict), res.Message);
        Assert.Equal(before.Topo, ToolDemo.Snapshot(host).Topo);
        Assert.Equal(0, host.UndoDepth);
    }

    [Fact]
    public void Preview_IsPure_AndThrottled()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        ToolDemo.NetState before = ToolDemo.Snapshot(host);
        Command c = Road(400, 200, 600, 200);
        for (int i = 0; i < 300; i++)
        {
            svc.SetPreview(in c);
            svc.Pump();
            Thread.Sleep(0);
        }

        WaitPreview(svc);
        Assert.Equal(1, svc.PreviewRequests); // тик не менялся, команда та же: один запрос
        Assert.Equal(RejectReason.None, svc.Preview.Verdict);
        Assert.True(svc.Preview.Result.Cost >= 0);
        Assert.True(svc.Preview.AxisCount >= 2);

        Command c2 = Road(400, 200, 650, 200);
        svc.SetPreview(in c2);
        WaitPreview(svc);
        Assert.Equal(2, svc.PreviewRequests);
        Assert.True(svc.Preview.Result.LengthM > 240f);

        ToolDemo.NetState after = ToolDemo.Snapshot(host);
        Assert.Equal(before.Hash, after.Hash);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public void Preview_SteadyState_AllocatesNothingOnMainThread()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        Command c = Road(400, 200, 600, 200);
        svc.SetPreview(in c);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 300)
        {
            host.SetTargetTick(host.Tick + 1); // по тику за миллисекунду: как идущая игра
            svc.Pump();
            Thread.Sleep(1);
        }

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        long r0 = svc.PreviewRequests;
        sw.Restart();
        while (sw.ElapsedMilliseconds < 300)
        {
            host.SetTargetTick(host.Tick + 1);
            svc.SetPreview(in c);
            svc.Pump();
            Thread.Sleep(1);
        }

        long a1 = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(svc.PreviewRequests > r0, "тики шли, превью должно обновляться раз за тик");
        Assert.Equal(0, a1 - a0);
    }

    [Fact]
    public void Group_IsOneAction_OneUndo_AndJournaled()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        ToolDemo.NetState start = ToolDemo.Snapshot(host);

        Span<Command> group = stackalloc Command[2];
        group[0] = Road(400, 200, 600, 200);
        group[1] = Road(600, 200, 600, 400);
        ToolResult r = Complete(host, svc, svc.Submit(group));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.Ack.Applied);
        Assert.True(r.Ack.Recorded);
        Assert.Equal(1, r.Ack.UndoDepth);
        Assert.Equal(0, r.Ack.RedoDepth);
        Assert.Equal(start.Segments + 2, ToolDemo.Snapshot(host).Segments);

        JournalEntry[] j = host.CopyJournal();
        int tagged = 0;
        foreach (JournalEntry e in j)
        {
            if ((e.Command.Tag & SimThreadHost.ToolTagBase) != 0)
            {
                tagged++;
                Assert.Equal(r.Ack.Tick, e.Tick);
            }
        }

        Assert.Equal(2, tagged);

        ToolResult u = Complete(host, svc, svc.Undo());
        Assert.True(u.Ok, u.Message);
        Assert.Equal(0, u.Ack.UndoDepth);
        Assert.Equal(1, u.Ack.RedoDepth);
        Assert.Equal(start.Topo, ToolDemo.Snapshot(host).Topo);
        Assert.False(svc.CanUndo);
        Assert.True(svc.CanRedo);

        // новое действие сбрасывает redo
        ToolResult n = Complete(host, svc, svc.Submit(Road(400, 200, 600, 200)));
        Assert.True(n.Ok, n.Message);
        Assert.Equal(0, n.Ack.RedoDepth);
    }

    [Fact]
    public void Undo_OnEmptyStack_ReportsNothing()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        ToolResult u = Complete(host, svc, svc.Undo());
        Assert.True(u.Ack.Nothing);
        Assert.False(u.Ok);
        Assert.Equal("Нечего отменять", u.Message);
        ToolResult r = Complete(host, svc, svc.Redo());
        Assert.Equal("Нечего повторять", r.Message);
    }

    [Fact]
    public void UndoRightAfterDo_WaitsForResult()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        ToolDemo.NetState start = ToolDemo.Snapshot(host);
        uint a = svc.Submit(Road(400, 200, 600, 200));
        uint u = svc.Undo(); // подан до того, как действие применено: undo должен дождаться записи в стек
        ToolResult ra = Complete(host, svc, a);
        ToolResult ru = Complete(host, svc, u);
        Assert.True(ra.Ok);
        Assert.False(ru.Ack.Nothing);
        Assert.True(ru.Ok, ru.Message);
        Assert.Equal(start.Topo, ToolDemo.Snapshot(host).Topo);
    }

    private static EntityRef FirstSegment(SimThreadHost host)
    {
        using ExclusiveAccess ex = host.AcquireExclusive();
        NetworkView v = ex.Simulation!.Network;
        return v.SegmentRef(0);
    }

    [Fact]
    public void RemoveRoad_Undo_RecreatesSegment()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        ToolDemo.NetState start = ToolDemo.Snapshot(host);
        EntityRef seg = FirstSegment(host);

        var rm = new RemoveRoadCmd { Seg = seg };
        ToolResult r = Complete(host, svc, svc.Submit(Command.MakeRemoveRoad(in rm)));
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Ack.Recorded);

        // дренаж идёт тиками; обратная переоткрывает/пересоздаёт сегмент
        host.SetTargetTick(host.Tick + 600);
        var sw = Stopwatch.StartNew();
        while (host.Tick < host.TargetTick && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(5);
        }

        ToolResult u = Complete(host, svc, svc.Undo());
        Assert.True(u.Ok, u.Message);
        host.SetTargetTick(host.Tick + 20);
        sw.Restart();
        while (host.Tick < host.TargetTick && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(5);
        }

        ToolDemo.NetState end = ToolDemo.Snapshot(host);
        Assert.Equal(start.Segments, end.Segments);
        Assert.Equal(start.Topo, end.Topo);
    }

    [Fact]
    public void RejectMessages_CoverAllCodes()
    {
        foreach (RejectReason r in Enum.GetValues<RejectReason>())
        {
            if (r == RejectReason.None)
            {
                Assert.Equal(string.Empty, RejectMessages.Label(r));
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(RejectMessages.Label(r)), r.ToString());
            Assert.NotEqual("Не получилось", RejectMessages.Label(r)); // у каждого известного кода свой текст
            Assert.StartsWith(RejectMessages.Label(r), RejectMessages.Full(r), StringComparison.Ordinal);
        }

        foreach (IssueCode c in Enum.GetValues<IssueCode>())
        {
            Assert.False(string.IsNullOrWhiteSpace(RejectMessages.Issue(c, 3)), c.ToString());
            if ((int)c >= 100)
            {
                Assert.NotEqual("Есть замечание", RejectMessages.Issue(c, 3));
            }
        }

        Assert.Equal("Слишком короткий участок. Тяните дальше: от 20 м", RejectMessages.Full(RejectReason.TooShort));
        Assert.Equal("За границей карты", RejectMessages.Full(RejectReason.OutOfBounds));
        Assert.Equal("Участков под застройку потеряется: 7", RejectMessages.Issue(IssueCode.ZoneLotsLost, 7));
        Assert.Equal("Не хватает ₽ 2 400", RejectMessages.Shortfall(2400));
        Assert.True(RejectMessages.IsSilent(RejectReason.InvalidHandle));
        Assert.False(RejectMessages.IsSilent(RejectReason.Busy));
        Assert.Equal(RejectMessages.Label(RejectReason.Overlap), RejectMessages.Issue((IssueCode)(int)RejectReason.Overlap, 0)); // коды 1..99 - те же ошибки
    }

    [Fact]
    public void ToolBase_CancelGesture_ReportsDrag()
    {
        using SimThreadHost host = Start();
        var svc = new ToolCommandService(host);
        var tool = new ProbeTool(svc);
        Assert.False(tool.CancelGesture());
        tool.OnPress(new ToolPointer(1, 2, true));
        Assert.True(tool.CancelGesture());
        Assert.Equal(1, tool.Cancels - 1); // первый вызов CancelGesture тоже зовёт OnCancel
        Assert.False(tool.CancelGesture());
    }

    private sealed class ProbeTool : ToolBase
    {
        public ProbeTool(ToolCommandService service)
            : base(service)
        {
        }

        public override string Id => "probe";

        public override string DisplayName => "Проба";

        public int Cancels { get; private set; }

        public override void OnCancel()
        {
            Cancels++;
            base.OnCancel();
        }
    }
}
