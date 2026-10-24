#pragma warning disable CA1861 // тест: литералы-массивы аргументов допустимы
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Headless.Bench;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Эталон приёмки оптимизаций горячего пути «побитово эквивалентно» (ADR-0016, hotpath.md D1): хэш состояния s1k и s10k на
/// фиксированных тиках, снятый на коммите ДО первой правки (<c>274dc1b</c>) и совпавший с кодом после H1. Задачи H2-H5 не имеют
/// права его менять. Меняется только осознанно (правка правил движения/перекрёстков, сценария, хэша состояния): отдельный коммит
/// с объяснением, как baseline. Полную загрузку s10k проверяют командой
/// <c>hash --scenario s10k --warmup-until-full --ticks 400 --every 200</c> (на 274dc1b и после H1:
/// tick=6100 861672e99fa95a39, tick=6300 c73832373fb67bd3).
/// </summary>
public sealed class HotPathGoldenHashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tc-golden-" + Guid.NewGuid().ToString("N"));

    public HotPathGoldenHashTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Hex(ulong h) => h.ToString("x16", CultureInfo.InvariantCulture);

    private static Simulation Create(string name)
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null));
        return Simulation.Create(sc.Desc);
    }

    [Fact]
    public void S1k_StateHash_MatchesGolden()
    {
        using Simulation sim = Create("s1k");
        string[] expected = { "b938c1c516383c49", "47392c93d47b0a68", "ebb7ba91cca224be" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(1000);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }

        Assert.Equal(3000, sim.Tick);
    }

    [Fact]
    public void S10k_StateHash_MatchesGolden()
    {
        using Simulation sim = Create("s10k");
        string[] expected = { "8a9f75d816942e0d", "eeabc3cb88296832", "5bf20a479ea7dcdf" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(200);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }

        Assert.Equal(600, sim.Tick);
    }

    [Fact]
    public void HashCommand_PrintsSameHashesAsGolden()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = HashCommand.Run(new[] { "hash", "--scenario", "s1k", "--ticks", "2000", "--every", "1000" }, o, e);
        Assert.Equal(0, code);
        string[] lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("tick=1000 hash=b938c1c516383c49 ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("tick=2000 hash=47392c93d47b0a68 ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void HashCommand_RequiresScenario()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(HashCommand.ExitUsage, HashCommand.Run(new[] { "hash", "--ticks", "10" }, o, e));
    }

    [Fact]
    public void BenchPhases_WritesPhaseMsAndWorkPerTick()
    {
        string path = Path.Combine(_dir, "phases.json");
        var o = new StringWriter();
        var e = new StringWriter();
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "s1k", "--ticks", "100", "--warmup-ticks", "300", "--phases", "--out", path }, o, e);
        Assert.Equal(0, code);
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement perf = d.RootElement.GetProperty("perf");
        JsonElement ph = perf.GetProperty("phase_ms");
        string[] names = { "commands", "spawn", "route", "gate", "behaviour", "integrate", "transition", "arrival", "metrics", "snapshot" };
        double sumMean = 0;
        foreach (string n in names)
        {
            JsonElement x = ph.GetProperty(n);
            Assert.True(x.GetProperty("p95").GetDouble() >= x.GetProperty("p50").GetDouble());
            sumMean += x.GetProperty("mean").GetDouble();
        }

        Assert.True(sumMean > 0);
        JsonElement work = perf.GetProperty("work_per_tick");
        Assert.True(work.GetProperty("behaviour_vehicles").GetDouble() > 0);
        Assert.True(work.GetProperty("obstacle_gap_calls").GetDouble() > 0);
        Assert.True(work.GetProperty("snapshot_slots").GetDouble() > 0);
    }

    [Fact]
    public void Bench_WithoutPhases_HasWorkButNoPhaseMs()
    {
        string path = Path.Combine(_dir, "nophases.json");
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "s1k", "--ticks", "20", "--warmup-ticks", "50", "--out", path }, new StringWriter(), new StringWriter());
        Assert.Equal(0, code);
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement perf = d.RootElement.GetProperty("perf");
        Assert.False(perf.TryGetProperty("phase_ms", out _));
        Assert.True(perf.TryGetProperty("work_per_tick", out _));
    }

    [Fact]
    public void WorkPerTick_IsIdenticalInTwoRuns()
    {
        string a = Path.Combine(_dir, "a.json");
        string b = Path.Combine(_dir, "b.json");
        string[] common = { "bench", "--scenario", "s1k", "--ticks", "100", "--warmup-ticks", "200", "--out" };
        Assert.Equal(0, BenchCommand.Run(Append(common, a), new StringWriter(), new StringWriter()));
        Assert.Equal(0, BenchCommand.Run(Append(common, b), new StringWriter(), new StringWriter()));
        string wa = ReadWork(a);
        Assert.Equal(wa, ReadWork(b));
        Assert.Contains("conflict_entries_scanned", wa, StringComparison.Ordinal);
    }

    private static string[] Append(string[] head, string tail)
    {
        string[] r = new string[head.Length + 1];
        head.CopyTo(r, 0);
        r[^1] = tail;
        return r;
    }

    private static string ReadWork(string path)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.GetProperty("perf").GetProperty("work_per_tick").GetRawText();
    }

    [Fact]
    public void Compare_PrintsInfoForWorkAndPhases_WithoutAffectingVerdict()
    {
        string res = Path.Combine(_dir, "res.json");
        string bas = Path.Combine(_dir, "base.json");
        File.WriteAllText(
            res,
            "{\"scenario\":\"s1k\",\"perf\":{\"tick_ms\":{\"p95\":1.0},\"alloc_bytes_per_tick\":0,\"phase_ms\":{\"gate\":{\"p50\":0.1,\"p95\":0.2,\"mean\":0.1}},\"work_per_tick\":{\"obstacle_gap_calls\":50}}}");
        File.WriteAllText(bas, "{\"scenario\":\"s1k\",\"perf\":{\"tick_ms\":{\"p95\":1.0},\"alloc_bytes_per_tick\":0,\"work_per_tick\":{\"obstacle_gap_calls\":100}}}");
        var o = new StringWriter();
        int code = CompareCommand.Run(new[] { "compare", "--result", res, "--baseline", bas }, o, new StringWriter());
        Assert.Equal(0, code);
        string text = o.ToString();
        Assert.Contains("work.obstacle_gap_calls", text, StringComparison.Ordinal);
        Assert.Contains("-50.0%", text, StringComparison.Ordinal);
        Assert.Contains("phase_ms.gate.p95", text, StringComparison.Ordinal);
    }
}
