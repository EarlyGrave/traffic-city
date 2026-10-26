#pragma warning disable CA1861 // тест: литералы-массивы аргументов допустимы
using System;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Bench;
using Sim.Scenarios;
using Sim.Scenarios.Tools;

namespace Sim.Headless.Tests;

/// <summary>T10 (player-tools.md §9, §14): журнал команд = реплей; <c>Replay_Golden</c>, порча файла, поиск расхождения.</summary>
public sealed class ReplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tc-replay-" + Guid.NewGuid().ToString("N"));

    public ReplayTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static int Replay(out string stdout, out string stderr, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = ReplayCommands.RunReplay(args, o, e);
        stdout = o.ToString();
        stderr = e.ToString();
        return code;
    }

    private static Simulation NewSim(out LoadedScenario sc)
    {
        sc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve("s1k", null));
        return ReplayCommands.CreateFromScenario(sc, default);
    }

    private static ReplayLog Record(int ticks, int interval, int commands)
    {
        using Simulation sim = NewSim(out LoadedScenario sc);
        var rec = new ReplayRecorder(sim, interval, "s1k");
        for (int i = 0; i < commands; i++)
        {
            rec.Step(ticks / (commands + 1));
            var c = Command.MakeSetTargetVehicleCount(sc.Desc.Demand.TargetVehicles * (60 + (i * 9 % 50)) / 100, (uint)i);
            Assert.True(rec.Enqueue(in c));
        }

        rec.Step(ticks - (int)sim.Tick);
        return rec.Finish();
    }

    [Fact]
    public void Replay_Golden()
    {
        string log = Path.Combine(_dir, "run.tcrp");
        Assert.Equal(0, Replay(out string o, out string e, "replay", "--record", log, "--scenario", "s1k", "--ticks", "1000", "--synth", "20", "--interval", "250"));
        Assert.Contains("commands=20", o, StringComparison.Ordinal);
        Assert.Equal(0, Replay(out o, out e, "replay", "--log", log, "--check"));
        Assert.Contains("replay: OK", o, StringComparison.Ordinal);
        Assert.Contains("checkpoints_checked=5", o, StringComparison.Ordinal);   // тик 0 + 250/500/750/1000

        // С сейва посреди записи: журнал начинается на тике 600, команды и хэши те же, что при непрерывном прогоне.
        string save = Path.Combine(_dir, "s.tcsave");
        Assert.Equal(0, SaveCommands.RunSave(new[] { "save", "--scenario", "s1k", "--ticks", "600", "--out", save }, new StringWriter(), new StringWriter()));
        string log2 = Path.Combine(_dir, "from-save.tcrp");
        Assert.Equal(0, Replay(out o, out e, "replay", "--record", log2, "--save", save, "--ticks", "700", "--synth", "10", "--interval", "300"));
        Assert.Equal(0, Replay(out o, out e, "replay", "--log", log2, "--save", save, "--check"));
        Assert.Contains("replay: OK", o, StringComparison.Ordinal);

        // Баг-репорт: сейв + журнал в одном файле, replay без --save.
        string bug = Path.Combine(_dir, "bug.tcrp");
        Assert.Equal(0, ReplayCommands.RunBugReport(new[] { "bugreport", "--save", save, "--log", log2, "--out", bug, "--scenario", "s1k" }, new StringWriter(), new StringWriter()));
        Assert.Equal(0, Replay(out o, out e, "replay", "--log", bug, "--check"));
        Assert.Contains("replay: OK", o, StringComparison.Ordinal);

        // --until: останов раньше конца.
        Assert.Equal(0, Replay(out o, out e, "replay", "--log", log, "--check", "--until", "500"));
        Assert.Contains("replay: tick=500 ", o, StringComparison.Ordinal);
    }

    [Fact]
    public void Roundtrip_PreservesEverything()
    {
        ReplayLog log = Record(400, 100, 7);
        log.EmbeddedSave = new byte[] { 1, 2, 3 };
        log.ScenarioJson = "{\"x\":1}";
        Assert.Equal(ReplayStatus.Ok, ReplayLog.TryParse(log.ToBytes(), out ReplayLog back));
        Assert.Equal(log.BaseHash, back.BaseHash);
        Assert.Equal(log.EndTick, back.EndTick);
        Assert.Equal(log.Checkpoints, back.Checkpoints);
        Assert.Equal(log.CommandTicks, back.CommandTicks);
        Assert.Equal(7, back.Commands.Count);
        Assert.Equal(log.Commands[3].SetTargetVehicleCount.Count, back.Commands[3].SetTargetVehicleCount.Count);
        Assert.Equal(log.EmbeddedSave, back.EmbeddedSave);
        Assert.Equal("{\"x\":1}", back.ScenarioJson);
        Assert.Equal("s1k", back.Scenario);
        Assert.Equal(4, back.Checkpoints.Count);   // 100/200/300/400
    }

    [Fact]
    public void CorruptFile_GivesStatusNotException()
    {
        byte[] good = Record(300, 100, 5).ToBytes();
        Assert.Equal(ReplayStatus.BadMagic, ReplayLog.TryParse(ReadOnlySpan<byte>.Empty, out _));
        Assert.Equal(ReplayStatus.BadMagic, ReplayLog.TryParse(new byte[100], out _));
        for (int cut = 8; cut < good.Length; cut += 37)
        {
            Assert.NotEqual(ReplayStatus.Ok, ReplayLog.TryParse(good.AsSpan(0, cut), out _));
        }

        for (int i = 8; i < good.Length; i += 29)
        {
            byte[] bad = (byte[])good.Clone();
            bad[i] ^= 0x40;
            Assert.NotEqual(ReplayStatus.Ok, ReplayLog.TryParse(bad, out _));
        }

        byte[] sum = (byte[])good.Clone();
        sum[^1] ^= 1;
        Assert.Equal(ReplayStatus.BadChecksum, ReplayLog.TryParse(sum, out _));

        // Через CLI: код 2 и сообщение, без исключения.
        string path = Path.Combine(_dir, "bad.tcrp");
        File.WriteAllBytes(path, good.AsSpan(0, good.Length / 2).ToArray());
        Assert.Equal(2, Replay(out _, out string err, "replay", "--log", path, "--check"));
        Assert.Contains("error:", err, StringComparison.Ordinal);
        Assert.Equal(2, Replay(out _, out err, "replay", "--log", Path.Combine(_dir, "none.tcrp"), "--check"));
    }

    [Fact]
    public void Unsorted_Commands_AreRejected()
    {
        ReplayLog log = Record(200, 100, 3);
        (log.CommandTicks[0], log.CommandTicks[2]) = (log.CommandTicks[2], log.CommandTicks[0]);
        Assert.Equal(ReplayStatus.BadCommandOrder, ReplayLog.TryParse(log.ToBytes(), out _));
    }

    [Fact]
    public void VersionMismatch_IsAClearError()
    {
        ReplayLog log = Record(100, 50, 1);
        log.WorldVersion++;
        string path = Path.Combine(_dir, "ver.tcrp");
        File.WriteAllBytes(path, log.ToBytes());
        Assert.Equal(ReplayStatus.VersionMismatch, log.CheckCompatible(out string msg));
        Assert.Contains("world_version", msg, StringComparison.Ordinal);
        Assert.Equal(2, Replay(out _, out string err, "replay", "--log", path, "--check"));
        Assert.Contains("VersionMismatch", err, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenCheckpoint_IsFoundAtItsTick()
    {
        ReplayLog log = Record(600, 100, 5);
        ReplayCheckpoint good = log.Checkpoints[2];
        ReplayCheckpoint bad = log.Checkpoints[3];
        log.Checkpoints[3] = bad with { Hash = bad.Hash ^ 1 };
        using Simulation sim = NewSim(out _);
        ReplayResult r = ReplayPlayer.Run(sim, log);
        Assert.Equal(ReplayStatus.Ok, r.Status);
        Assert.True(r.Diverged);
        Assert.Equal(bad.Tick, r.BadTick);
        Assert.Equal(good.Tick, r.LastGoodTick);
        Assert.Equal(bad.Hash, r.Actual);
        Assert.Equal(bad.Hash ^ 1, r.Expected);
        Assert.Equal(bad.Tick, r.EndTick);
    }

    [Fact]
    public void ChangedCommand_DivergesAfterItsTick_WithPerTickCheckpoints()
    {
        ReplayLog log = Record(400, 1, 4);
        int k = 2;
        long tick = log.CommandTicks[k];
        var c = log.Commands[k];
        c.SetTargetVehicleCount.Count = 5;
        log.Commands[k] = c;
        using Simulation sim = NewSim(out _);
        ReplayResult r = ReplayPlayer.Run(sim, log);
        Assert.True(r.Diverged);
        Assert.True(r.LastGoodTick >= tick, $"last good {r.LastGoodTick} < command tick {tick}");
        Assert.Equal(r.LastGoodTick + 1, r.BadTick);   // интервал 1: точный тик
        Assert.True(r.BadTick > tick);
    }

    [Fact]
    public void BaseHashMismatch_IsReportedAtStart()
    {
        ReplayLog log = Record(100, 50, 1);
        log.BaseHash ^= 1;
        using Simulation sim = NewSim(out _);
        ReplayResult r = ReplayPlayer.Run(sim, log);
        Assert.True(r.Diverged);
        Assert.Equal(0, r.BadTick);
        Assert.Equal(-1, r.LastGoodTick);
    }

    [Fact]
    public void BugReport_NeedsExistingFiles()
    {
        var e = new StringWriter();
        Assert.Equal(2, ReplayCommands.RunBugReport(new[] { "bugreport", "--save", Path.Combine(_dir, "a"), "--log", Path.Combine(_dir, "b"), "--out", Path.Combine(_dir, "c") }, new StringWriter(), e));
        Assert.Equal(2, ReplayCommands.RunBugReport(new[] { "bugreport" }, new StringWriter(), new StringWriter()));
    }
}
