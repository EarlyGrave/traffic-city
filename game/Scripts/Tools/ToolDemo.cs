using System;
using System.Globalization;
using System.Threading;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Host;

namespace TrafficCity.Tools;

/// <summary>Аргументы <c>--tool-demo build-road x0 z0 x1 z1</c>.</summary>
public readonly record struct ToolDemoArgs(float X0, float Z0, float X1, float Z1);

/// <summary>
/// Отладочная проверка пути «инструмент -> ядро» без виджетов (T13): превью -> вердикт и цена -> применение -> сверка вердиктов -> undo -> redo.
/// Журнал строками <c>[tool] ...</c>. Чистый C# (без Godot): гоняется и из Godot-раннера (<c>ToolDemoRunner</c>), и из юнит-теста. Блокирует вызывающий поток
/// (это отладка, не кадр игры): подгоняет тики хоста и крутит <see cref="ToolCommandService.Pump"/>.
/// </summary>
public static class ToolDemo
{
    private const int TimeoutMs = 20_000;

    /// <summary>Разбор <c>--tool-demo build-road x0 z0 x1 z1</c> из аргументов запуска (после <c>--</c>). false - флага нет или он неполон.</summary>
    public static bool TryParse(string[] args, out ToolDemoArgs demo)
    {
        demo = default;
        CultureInfo inv = CultureInfo.InvariantCulture;
        for (int i = 0; i + 5 < args.Length; i++)
        {
            if (args[i] == "--tool-demo" && args[i + 1] == "build-road"
                && float.TryParse(args[i + 2], NumberStyles.Float, inv, out float x0)
                && float.TryParse(args[i + 3], NumberStyles.Float, inv, out float z0)
                && float.TryParse(args[i + 4], NumberStyles.Float, inv, out float x1)
                && float.TryParse(args[i + 5], NumberStyles.Float, inv, out float z1))
            {
                demo = new ToolDemoArgs(x0, z0, x1, z1);
                return true;
            }
        }

        return false;
    }

    /// <summary>Команда демо: двусторонняя улица 50 км/ч.</summary>
    public static Command MakeBuildRoad(in ToolDemoArgs a)
    {
        var p = new BuildRoadCmd { Ax = a.X0, Az = a.Z0, Bx = a.X1, Bz = a.Z1, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        return Command.MakeBuildRoad(in p);
    }

    /// <summary>Прогон демо. Возвращает 0, если вердикт превью совпал с применением и undo вернул топологию сети, иначе 1. Хост должен идти (не пауза) - цель тика двигает демо.</summary>
    public static int Run(SimThreadHost host, ToolCommandService svc, in ToolDemoArgs a, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(log);
        CultureInfo inv = CultureInfo.InvariantCulture;
        int fails = 0;

        NetState before = Snapshot(host);
        log(string.Create(inv, $"[tool] start tick={host.Tick} net_version={before.Version} segments={before.Segments} nodes={before.Nodes} topo={before.Topo:X16} hash={before.Hash:X16}"));

        Command cmd = MakeBuildRoad(in a);
        svc.SetPreview(in cmd, PreviewOptions.Detail);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!svc.HasPreview)
        {
            svc.Pump();
            if (sw.ElapsedMilliseconds > TimeoutMs)
            {
                log("[tool] FAIL preview timeout");
                return 1;
            }

            Thread.Sleep(1);
        }

        PreviewReply pr = svc.Preview;
        PreviewResult r = pr.Result;
        log(string.Create(inv, $"[tool] preview id={pr.Id} tick={pr.Tick} verdict={r.Verdict} cost={r.Cost} len={r.LengthM:F1}m snapA={r.SnapKindA} snapB={r.SnapKindB} newNodes={r.NewNodes} newSegs={r.NewSegments} issues={r.IssueCount} axisPts={pr.AxisCount}"));
        if (r.Verdict != RejectReason.None)
        {
            log("[tool] preview msg: " + RejectMessages.Full(r.Verdict));
        }

        for (int i = 0; i < pr.IssueCount; i++)
        {
            log(string.Create(inv, $"[tool]   issue {pr.Issues[i].Code} sev={pr.Issues[i].Severity} value={pr.Issues[i].Value}: {RejectMessages.Issue(pr.Issues[i].Code, pr.Issues[i].Value)}"));
        }

        // превью не меняет состояние
        NetState afterPreview = Snapshot(host);
        bool previewPure = afterPreview.Hash == before.Hash && afterPreview.Version == before.Version;
        log(string.Create(inv, $"[tool] preview pure (hash+version unchanged): {previewPure}"));
        fails += previewPure ? 0 : 1;

        uint id = svc.Submit(in cmd);
        ToolResult? done = WaitAck(host, svc, id, log);
        if (done == null)
        {
            return 1;
        }

        ToolAck ack = done.Value.Ack;
        NetState applied = Snapshot(host);
        log(string.Create(inv, $"[tool] apply  id={ack.ActionId} tick={ack.Tick} applied={ack.Applied} rejected={ack.Rejected} reason={ack.FirstReason} recorded={ack.Recorded} undo={ack.UndoDepth} redo={ack.RedoDepth} net_version={applied.Version} segments={applied.Segments} nodes={applied.Nodes} topo={applied.Topo:X16}"));
        bool same = r.Verdict == RejectReason.None ? ack.Ok : ack.FirstReason == r.Verdict;
        log(string.Create(inv, $"[tool] preview verdict == apply verdict: {same} (preview={r.Verdict}, apply={(ack.Rejected == 0 ? RejectReason.None : ack.FirstReason)})"));
        fails += same ? 0 : 1;
        if (!ack.Ok)
        {
            log("[tool] apply msg: " + done.Value.Message);
            return fails == 0 ? 0 : 1;
        }

        id = svc.Undo();
        done = WaitAck(host, svc, id, log);
        if (done == null)
        {
            return 1;
        }

        ack = done.Value.Ack;
        NetState undone = Snapshot(host);
        bool restored = undone.Topo == before.Topo && undone.Segments == before.Segments && undone.Nodes == before.Nodes;
        log(string.Create(inv, $"[tool] undo   id={ack.ActionId} tick={ack.Tick} applied={ack.Applied} rejected={ack.Rejected} undo={ack.UndoDepth} redo={ack.RedoDepth} net_version={undone.Version} segments={undone.Segments} nodes={undone.Nodes} topo={undone.Topo:X16}"));
        log(string.Create(inv, $"[tool] undo restored topology: {restored} (net_version {before.Version} -> {applied.Version} -> {undone.Version}: версия растёт при любой правке, undo - тоже правка)"));
        fails += restored && ack.Ok ? 0 : 1;

        id = svc.Redo();
        done = WaitAck(host, svc, id, log);
        if (done == null)
        {
            return 1;
        }

        ack = done.Value.Ack;
        NetState redone = Snapshot(host);
        bool again = redone.Topo == applied.Topo && redone.Segments == applied.Segments;
        log(string.Create(inv, $"[tool] redo   id={ack.ActionId} applied={ack.Applied} rejected={ack.Rejected} undo={ack.UndoDepth} redo={ack.RedoDepth} net_version={redone.Version} segments={redone.Segments} nodes={redone.Nodes} topo={redone.Topo:X16}"));
        log(string.Create(inv, $"[tool] redo restored built network: {again}"));
        fails += again && ack.Ok ? 0 : 1;

        log(string.Create(inv, $"[tool] preview requests sent={svc.PreviewRequests} served={host.PreviewsServed}; result={(fails == 0 ? "OK" : "FAIL")}"));
        return fails == 0 ? 0 : 1;
    }

    private static ToolResult? WaitAck(SimThreadHost host, ToolCommandService svc, uint id, Action<string> log)
    {
        if (id == 0)
        {
            log("[tool] FAIL action queue refused");
            return null;
        }

        ToolResult? got = null;
        void On(ToolResult t)
        {
            if (t.Ack.ActionId == id)
            {
                got = t;
            }
        }

        svc.ActionCompleted += On;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (got == null)
            {
                host.SetTargetTick(Math.Max(host.TargetTick, host.Tick + 2)); // команда применяется на границе ближайшего Step
                svc.Pump();
                if (sw.ElapsedMilliseconds > TimeoutMs)
                {
                    log("[tool] FAIL ack timeout");
                    return null;
                }

                Thread.Sleep(1);
            }
        }
        finally
        {
            svc.ActionCompleted -= On;
        }

        return got;
    }

    internal readonly record struct NetState(int Version, int Segments, int Nodes, ulong Topo, ulong Hash);

    // Живые узлы/сегменты + геометрия (FNV-1a) под эксклюзивным доступом; ComputeStateHash включает тик, поэтому для сравнения «до/после undo» годится только топология.
    internal static NetState Snapshot(SimThreadHost host)
    {
        using ExclusiveAccess ex = host.AcquireExclusive();
        Simulation sim = ex.Simulation!;
        NetworkView v = sim.Network;
        ulong h = 0; // сумма по элементам: не зависит от того, в каких слотах пересозданы узлы и сегменты
        int segs = 0, nodes = 0;
        for (int i = 0; i < v.Nodes.Length; i++)
        {
            if (v.IsNodeAlive(i))
            {
                nodes++;
                h += Mix(Bits(v.Nodes[i].Position.X), Bits(v.Nodes[i].Position.Z), 1);
            }
        }

        for (int i = 0; i < v.Segments.Length; i++)
        {
            if (!v.IsSegmentAlive(i))
            {
                continue;
            }

            segs++;
            SegmentData s = v.Segments[i];
            ulong a = Mix(Bits(v.Nodes[s.NodeA].Position.X), Bits(v.Nodes[s.NodeA].Position.Z), 2);
            ulong b = Mix(Bits(v.Nodes[s.NodeB].Position.X), Bits(v.Nodes[s.NodeB].Position.Z), 2);
            h += Mix(a ^ b, (ulong)s.LanesAB | ((ulong)s.LanesBA << 8) | ((ulong)(byte)s.Kind << 16), 3);
        }

        return new NetState(v.Version, segs, nodes, h, sim.ComputeStateHash());
    }

    private static ulong Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static ulong Mix(ulong x, ulong y, ulong salt)
    {
        ulong h = 14695981039346656037UL ^ salt;
        h = (h ^ x) * 1099511628211UL;
        h = (h ^ y) * 1099511628211UL;
        return h;
    }
}
