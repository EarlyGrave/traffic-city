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
/// 2.6r R1 (снятие допуска на красном, intersections.md 5.4 п. 6): первые отзывы допусков случаются на s1k между тиками 3000 и 4000,
/// поэтому хэши до тика 3000 и s10k до тика 600 не изменились, а тик 4000 s1k - новый (был 546ef7677165a0b6), полная загрузка s10k:
/// tick=6100 930f16b7951f5fd7, tick=6300 abdc420cf70ba05b (при 1, 2 и 3 потоках).
/// 2.5 S2 (ADR-0018): дефолт <c>city.signal_mode</c> стал <c>adaptive</c>, поэтому эти эталоны закреплены на <c>fixed</c> + <c>legacy</c>
/// (<c>--signal-mode fixed --signal-coordination legacy</c> у команды <c>hash</c>) и не изменились; хэши дефолта (adaptive) - в <c>AdaptiveGoldenHashTests</c>.
/// 2.5 (удержание автобусов, ADR-0008): <c>hold_alpha</c> по умолчанию 0.9, хэш остановок включает <c>LastDepartTick</c> при удержании - все эталоны (и в
/// <c>AdaptiveGoldenHashTests</c>) сняты заново; при <c>hold_alpha = 0</c> прежние константы совпадали побитово (проверено до смены дефолта).
/// ADR-0028 (отступ рукава по ширине поперечной дороги + <c>corner_radius</c> 6 м): другая геометрия полос и connector'ов - все эталоны (и в <c>AdaptiveGoldenHashTests</c>)
/// сняты заново (одинаковы при 1/2/3 потоках); с <c>city.corner_radius_m = 0</c> сеть побитово прежняя (<c>CornerRadiusTests.Disabled_ReproducesLegacyNetwork</c>).
/// Интергрин по длине коробки (ADR-0028, all-red + (W - 14 м) / v): светофорные узлы s1k/s10k получают +15 тиков all-red, эталоны сняты ещё раз;
/// с corner_radius_m = 0 хэши до ADR-0028 воспроизводятся (<c>LegacyCornerGoldenTests</c>). Полная загрузка s10k: tick=6100 1c87c60a48e9891e, tick=6300 66f87e2b42445be1 (до ADR-0029).
/// ADR-0029 (виртуальный карман остановок, дефолт загрузчика <c>transit.bay_model = "virtual"</c>): в хэш добавлен раздел «карманы» и меняется поведение автобусов на остановках Bay -
/// эталоны сняты заново отдельным коммитом (при 1/2/3 потоках одинаковы); с <c>bay_model = "curb"</c> эталоны до ADR-0029 воспроизводятся (<c>LegacyCornerGoldenTests</c>).
/// </summary>
public sealed class HotPathGoldenHashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tc-golden-" + Guid.NewGuid().ToString("N"));

    public HotPathGoldenHashTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Hex(ulong h) => h.ToString("x16", CultureInfo.InvariantCulture);

    private static Simulation Create(string name, int threads)
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null));
        ScenarioDesc d = sc.Desc;
        d.City.SignalMode = CitySignalMode.Fixed;                    // 2.5 S2: дефолт сценариев - adaptive; эталон снят на fixed + legacy (4b-bis)
        d.City.SignalCoordination = CitySignalCoordination.Legacy;
        return Simulation.Create(d, new SimRuntimeOptions { WorkerThreads = threads });
    }

    // ADR-0017: хэш не зависит от числа потоков - те же константы при 1, 2 и 3.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S1k_StateHash_MatchesGolden(int threads)
    {
        using Simulation sim = Create("s1k", threads);
        string[] expected = { "2af924566810da2f", "bf55c7d416e70bc8", "293a50c4eaa29f71", "558d3c3ef0b080e0" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(1000);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }

        Assert.Equal(4000, sim.Tick);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S10k_StateHash_MatchesGolden(int threads)
    {
        using Simulation sim = Create("s10k", threads);
        string[] expected = { "968acda58db1f475", "b6de7d495370be2c", "4a215863ab2ef5ae" };
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
        int code = HashCommand.Run(new[] { "hash", "--scenario", "s1k", "--ticks", "2000", "--every", "1000", "--signal-mode", "fixed", "--signal-coordination", "legacy" }, o, e);
        Assert.Equal(0, code);
        string[] lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("tick=1000 hash=2af924566810da2f ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("tick=2000 hash=bf55c7d416e70bc8 ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void HashCommand_ThreadsOption_GivesSameHashes()
    {
        string[] lines = new string[3];
        for (int t = 1; t <= 3; t++)
        {
            var o = new StringWriter();
            Assert.Equal(0, HashCommand.Run(new[] { "hash", "--scenario", "s1k", "--ticks", "1000", "--signal-mode", "fixed", "--signal-coordination", "legacy", "--threads", t.ToString(CultureInfo.InvariantCulture) }, o, new StringWriter()));
            lines[t - 1] = o.ToString();
        }

        Assert.StartsWith("tick=1000 hash=2af924566810da2f ", lines[0], StringComparison.Ordinal);
        Assert.Equal(lines[0], lines[1]);
        Assert.Equal(lines[0], lines[2]);
    }

    [Fact]
    public void Compare_RefusesRunsWithDifferentThreads()
    {
        string a = Path.Combine(_dir, "t1.json");
        string b = Path.Combine(_dir, "t2.json");
        const string body = "\"scenario\":\"s1k\",\"perf\":{\"tick_ms\":{\"p95\":1.0},\"alloc_bytes_per_tick\":0}";
        File.WriteAllText(a, "{\"threads\":1," + body + "}");
        File.WriteAllText(b, "{\"threads\":2," + body + "}");
        var e = new StringWriter();
        Assert.Equal(CompareCommand.ExitUsage, CompareCommand.Run(new[] { "compare", "--result", b, "--baseline", a }, new StringWriter(), e));
        Assert.Contains("threads", e.ToString(), StringComparison.Ordinal);
        File.WriteAllText(a, "{" + body + "}"); // старый файл без поля = 1 поток
        File.WriteAllText(b, "{\"threads\":1," + body + "}");
        Assert.Equal(CompareCommand.ExitOk, CompareCommand.Run(new[] { "compare", "--result", b, "--baseline", a }, new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void Bench_WithThreads_WritesThreadsToJson()
    {
        string path = Path.Combine(_dir, "thr.json");
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "s1k", "--ticks", "20", "--warmup-ticks", "50", "--threads", "2", "--out", path }, new StringWriter(), new StringWriter());
        Assert.Equal(0, code);
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, d.RootElement.GetProperty("threads").GetInt32());
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
