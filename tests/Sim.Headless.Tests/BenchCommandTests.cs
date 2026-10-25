#pragma warning disable CA1861 // тест: литералы-массивы аргументов допустимы
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sim.Headless.Bench;

namespace Sim.Headless.Tests;

public sealed class BenchCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tc-bench-" + Guid.NewGuid().ToString("N"));

    public BenchCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, double p95, double alloc, string scenario = "s1k")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(
            path,
            "{\"scenario\":\"" + scenario + "\",\"perf\":{\"tick_ms\":{\"p95\":" +
            p95.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "},\"alloc_bytes_per_tick\":" + alloc.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}");
        return path;
    }

    private static int Compare(string result, string baseline, out string stdout, string[]? extra = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var args = new List<string> { "compare", "--result", result, "--baseline", baseline };
        if (extra != null)
        {
            args.AddRange(extra);
        }

        int code = CompareCommand.Run(args.ToArray(), o, e);
        stdout = o.ToString() + e.ToString();
        return code;
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("focus", true)]
    [InlineData("0,0,100,100", true)]
    [InlineData("1,2,3", false)]
    [InlineData("a,b,c,d", false)]
    [InlineData("whole", false)]
    public void ViewSpec_Parses(string text, bool ok) => Assert.Equal(ok, ViewSpec.TryParse(text, out _));

    [Fact]
    public void Compare_RefusesDifferentViews_AndTreatsMissingViewAsAll()
    {
        string all = Write("all.json", 1.0, 0);
        string focus = Path.Combine(_dir, "focus.json");
        File.WriteAllText(focus, File.ReadAllText(all).Replace("{\"scenario\"", "{\"view\":\"focus\",\"scenario\""));
        string allExplicit = Path.Combine(_dir, "all2.json");
        File.WriteAllText(allExplicit, File.ReadAllText(all).Replace("{\"scenario\"", "{\"view\":\"all\",\"scenario\""));
        Assert.Equal(CompareCommand.ExitUsage, Compare(focus, all, out string text));
        Assert.Contains("snapshot views", text);
        Assert.Equal(CompareCommand.ExitOk, Compare(allExplicit, all, out _));
    }

    [Fact]
    public void ArgsParse_RejectsUnknownAndMissingValue()
    {
        string[] allowed = { "--a", "--b" };
        Assert.True(CliArgs.TryParse(new[] { "x", "--a", "1", "--b", "2" }, allowed, out CliArgs ok));
        Assert.Equal("1", ok.Get("--a"));
        Assert.False(CliArgs.TryParse(new[] { "x", "--c", "1" }, allowed, out CliArgs bad));
        Assert.Contains("unknown", bad.Error);
        Assert.False(CliArgs.TryParse(new[] { "x", "--a", "--b", "2" }, allowed, out CliArgs missing));
        Assert.Contains("requires a value", missing.Error);
    }

    [Fact]
    public void ArgsParse_Numbers()
    {
        Assert.True(CliArgs.TryParse(new[] { "x", "--n", "5", "--t", "0.2" }, new[] { "--n", "--t" }, out CliArgs a));
        Assert.True(a.TryGetInt("--n", 1, out int n, 9));
        Assert.Equal(5, n);
        Assert.True(a.TryGetDouble("--t", 0, 1, out double t, 0.1));
        Assert.Equal(0.2, t);
        Assert.False(a.TryGetInt("--t", 1, out _, 9));
    }

    private static int Budget(string result, string budget, out string stdout)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CompareCommand.Run(new[] { "compare", "--result", result, "--budget-p95-ms", budget }, o, e);
        stdout = o.ToString() + e.ToString();
        return code;
    }

    [Fact]
    public void Compare_Budget_UnderBudgetNoAlloc_Ok()
    {
        Assert.Equal(0, Budget(Write("r.json", 0.9, 0), "1.0", out string o));
        Assert.Contains("OK", o);
    }

    [Fact]
    public void Compare_Budget_OverBudget_Fails()
    {
        Assert.Equal(1, Budget(Write("r.json", 1.1, 0), "1.0", out string o));
        Assert.Contains("OVER BUDGET", o);
    }

    [Fact]
    public void Compare_Budget_Alloc_Fails()
    {
        Assert.Equal(1, Budget(Write("r.json", 0.5, 24), "1.0", out string o));
        Assert.Contains("ALLOCATIONS", o);
    }

    [Fact]
    public void Compare_NeitherBaselineNorBudget_Usage()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(2, CompareCommand.Run(new[] { "compare", "--result", Write("r.json", 1, 0) }, o, e));
    }

    [Fact]
    public void Median_OddAndEven()
    {
        Assert.Equal(2, BenchCommand.Median(new[] { 3.0, 1.0, 2.0 }));
        Assert.Equal(2.5, BenchCommand.Median(new[] { 4.0, 1.0, 2.0, 3.0 }));
    }

    [Fact]
    public void Bench_Repeat3_WritesThreeRuns()
    {
        string scenarios = FindScenariosDir();
        string outPath = Path.Combine(_dir, "rep.json");
        var o = new StringWriter();
        var e = new StringWriter();
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "demo-grid", "--ticks", "20", "--warmup-ticks", "5", "--repeat", "3", "--out", outPath, "--scenarios-dir", scenarios }, o, e);
        Assert.True(code == 0, e.ToString());
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(outPath));
        JsonElement perf = d.RootElement.GetProperty("perf");
        Assert.Equal(3, perf.GetProperty("tick_ms_runs").GetArrayLength());
        double[] p95s = perf.GetProperty("tick_ms_runs").EnumerateArray().Select(x => x.GetProperty("p95").GetDouble()).OrderBy(x => x).ToArray();
        Assert.Equal(p95s[1], perf.GetProperty("tick_ms").GetProperty("p95").GetDouble());
    }

    [Fact]
    public void Bench_WritesSignalMetrics_AndSeedOverride()
    {
        string scenarios = FindScenariosDir();
        string outPath = Path.Combine(_dir, "sig.json");
        var o = new StringWriter();
        var e = new StringWriter();
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "demo-grid", "--ticks", "400", "--warmup-ticks", "200", "--seed", "7", "--signal-mode", "fixed", "--signal-coordination", "legacy", "--out", outPath, "--scenarios-dir", scenarios }, o, e);
        Assert.True(code == 0, e.ToString());
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(outPath));
        Assert.Equal(7UL, d.RootElement.GetProperty("seed").GetUInt64());
        Assert.Equal("fixed", d.RootElement.GetProperty("signal_mode").GetString());
        Assert.Equal("legacy", d.RootElement.GetProperty("signal_coordination").GetString());
        JsonElement t = d.RootElement.GetProperty("traffic");
        foreach (string k in new[] { "signal_wait_s", "signal_wait_p95_s", "signal_grants" })
        {
            foreach (string m in new[] { "straight", "right", "left", "uturn" })
            {
                Assert.True(t.GetProperty(k).TryGetProperty(m, out _), k + "." + m);
            }
        }

        foreach (string k in new[] { "left_wait_max_s", "arterial_stop_share", "stuck_episodes", "starve_denials", "arrow_phases", "spill_outs", "exit_claims", "gate_denied_exit" })
        {
            Assert.True(t.TryGetProperty(k, out _), k);
        }

        Assert.Equal(0, t.GetProperty("arrow_phases").GetInt64());
        Assert.Equal(0, t.GetProperty("exit_claims").GetInt64());
    }

    [Theory]
    [InlineData("--signal-mode", "adaptive", "not implemented")]
    [InlineData("--signal-mode", "bogus", "expected")]
    [InlineData("--signal-coordination", "alternate", "not implemented")]
    [InlineData("--seed", "-1", "unsigned")]
    public void Bench_UnsupportedSignalOptions_AreClearErrors(string opt, string value, string text)
    {
        string scenarios = FindScenariosDir();
        var o = new StringWriter();
        var e = new StringWriter();
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "demo-grid", "--ticks", "5", "--warmup-ticks", "0", opt, value, "--out", Path.Combine(_dir, "x.json"), "--scenarios-dir", scenarios }, o, e);
        Assert.Equal(BenchCommand.ExitUsage, code);
        Assert.Contains(text, e.ToString());
    }

    private string WriteRamp(string name, double p95, bool? complete, int active = 1000, int target = 1000)
    {
        string path = Path.Combine(_dir, name);
        string ramp = complete is null
            ? string.Empty
            : ",\"ramp_complete\":" + (complete.Value ? "true" : "false") + ",\"active_vehicles\":" + active + ",\"active_vehicles_start\":" + active + ",\"target_vehicles\":" + target;
        File.WriteAllText(
            path,
            "{\"scenario\":\"s1k\",\"perf\":{\"tick_ms\":{\"p95\":" + p95.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "},\"alloc_bytes_per_tick\":0" + ramp + "}}");
        return path;
    }

    [Fact]
    public void Compare_RampMismatch_Fails()
    {
        Assert.Equal(1, Compare(WriteRamp("r.json", 1.0, false, 500), WriteRamp("b.json", 1.0, true), out string o));
        Assert.Contains("RAMP MISMATCH", o);
    }

    [Fact]
    public void Compare_RampBothComplete_Ok()
    {
        Assert.Equal(0, Compare(WriteRamp("r.json", 1.0, true), WriteRamp("b.json", 1.0, true), out string o));
        Assert.DoesNotContain("warning", o);
    }

    [Fact]
    public void Compare_LegacyBaselineWithoutRampInfo_ReadsAndWarns()
    {
        Assert.Equal(0, Compare(WriteRamp("r.json", 1.0, true), Write("b.json", 1.0, 0), out string o));
        Assert.Contains("ramp info is missing", o);
        Assert.Equal(0, Compare(Write("r2.json", 1.0, 0), Write("b2.json", 1.0, 0), out string o2));
        Assert.DoesNotContain("warning", o2);
    }

    [Fact]
    public void Compare_IncompleteResultAgainstLegacyBaseline_Warns()
    {
        Assert.Equal(0, Compare(WriteRamp("r.json", 1.0, false, 500), Write("b.json", 1.0, 0), out string o));
        Assert.Contains("ramp_complete=false", o);
    }

    [Fact]
    public void Compare_ActiveVehiclesDiffer_WarnsButDoesNotFail()
    {
        Assert.Equal(0, Compare(WriteRamp("r.json", 1.0, true, 1300), WriteRamp("b.json", 1.0, true, 1000), out string o));
        Assert.Contains("active vehicles differ", o);
    }

    [Theory]
    [InlineData(1000, 1000, true)]
    [InlineData(980, 1000, true)]
    [InlineData(979, 1000, false)]
    [InlineData(0, 0, true)]
    [InlineData(4991, 10000, false)]
    public void IsFull_UsesShareOfTarget(int active, int target, bool expected) =>
        Assert.Equal(expected, BenchCommand.IsFull(active, target));

    [Fact]
    public void CliArgs_Flag_NeedsNoValue()
    {
        Assert.True(CliArgs.TryParse(new[] { "x", "--f", "--a", "1" }, new[] { "--a" }, new[] { "--f" }, out CliArgs a));
        Assert.True(a.Has("--f"));
        Assert.Equal("1", a.Get("--a"));
        Assert.False(a.Has("--g"));
    }

    [Fact]
    public void Bench_WarmupUntilFull_ReportsRampFields()
    {
        string scenarios = FindScenariosDir();
        string full = Path.Combine(_dir, "full.json");
        string partial = Path.Combine(_dir, "partial.json");
        var e = new StringWriter();
        Assert.Equal(
            0,
            BenchCommand.Run(
                new[] { "bench", "--scenario", "s1k", "--ticks", "20", "--warmup-ticks", "5", "--warmup-until-full", "--warmup-max-ticks", "6000", "--out", full, "--scenarios-dir", scenarios },
                new StringWriter(),
                e));
        Assert.Equal(
            0,
            BenchCommand.Run(
                new[] { "bench", "--scenario", "s1k", "--ticks", "20", "--warmup-ticks", "5", "--out", partial, "--scenarios-dir", scenarios },
                new StringWriter(),
                e));
        using JsonDocument f = JsonDocument.Parse(File.ReadAllText(full));
        using JsonDocument p = JsonDocument.Parse(File.ReadAllText(partial));
        JsonElement fp = f.RootElement.GetProperty("perf");
        JsonElement pp = p.RootElement.GetProperty("perf");
        Assert.True(fp.GetProperty("ramp_complete").GetBoolean());
        Assert.Equal(1000, fp.GetProperty("target_vehicles").GetInt32());
        Assert.True(fp.GetProperty("active_vehicles_start").GetInt32() >= 980);
        Assert.True(fp.GetProperty("warmup_ticks_actual").GetInt32() > 5);
        Assert.False(pp.GetProperty("ramp_complete").GetBoolean());
        Assert.Equal(5, pp.GetProperty("warmup_ticks_actual").GetInt32());
    }

    [Fact]
    public void Compare_WithinTolerance_Ok()
    {
        string b = Write("b.json", 1.0, 0);
        string r = Write("r.json", 1.09, 0);
        Assert.Equal(0, Compare(r, b, out string o));
        Assert.Contains("OK", o);
    }

    [Fact]
    public void Compare_RegressionOverTolerance_Fails()
    {
        string b = Write("b.json", 1.0, 0);
        string r = Write("r.json", 1.2, 0);
        Assert.Equal(1, Compare(r, b, out string o));
        Assert.Contains("REGRESSION", o);
    }

    [Fact]
    public void Compare_CustomTolerance()
    {
        string b = Write("b.json", 1.0, 0);
        string r = Write("r.json", 1.2, 0);
        Assert.Equal(0, Compare(r, b, out _, new[] { "--tolerance", "0.25" }));
    }

    [Fact]
    public void Compare_Allocations_Fail()
    {
        string b = Write("b.json", 1.0, 0);
        string r = Write("r.json", 0.5, 24);
        Assert.Equal(1, Compare(r, b, out string o));
        Assert.Contains("ALLOCATIONS", o);
    }

    [Fact]
    public void Compare_MissingBaseline_Exit2()
    {
        string r = Write("r.json", 1.0, 0);
        Assert.Equal(2, Compare(r, Path.Combine(_dir, "nope.json"), out string o));
        Assert.Contains("baseline not found", o);
    }

    [Fact]
    public void Compare_BadJson_Exit2()
    {
        string r = Write("r.json", 1.0, 0);
        string b = Path.Combine(_dir, "bad.json");
        File.WriteAllText(b, "{ not json");
        Assert.Equal(2, Compare(r, b, out _));
    }

    [Fact]
    public void UpdateBaseline_CopiesResult()
    {
        string r = Write("r.json", 1.0, 0);
        string b = Path.Combine(_dir, "sub", "baseline.json");
        int code = UpdateBaselineCommand.Run(new[] { "update-baseline", "--from", r, "--baseline", b }, new StringWriter(), new StringWriter());
        Assert.Equal(0, code);
        Assert.Equal(File.ReadAllText(r), File.ReadAllText(b));
        Assert.Equal(0, Compare(r, b, out _));
    }

    [Fact]
    public void UpdateBaseline_RejectsNonResult()
    {
        string r = Path.Combine(_dir, "x.json");
        File.WriteAllText(r, "{}");
        int code = UpdateBaselineCommand.Run(new[] { "update-baseline", "--from", r, "--baseline", Path.Combine(_dir, "b.json") }, new StringWriter(), new StringWriter());
        Assert.Equal(2, code);
        Assert.False(File.Exists(Path.Combine(_dir, "b.json")));
    }

    [Fact]
    public void Bench_DemoGrid_WritesAllKeys()
    {
        string scenarios = FindScenariosDir();
        string outFile = Path.Combine(_dir, "demo.json");
        var o = new StringWriter();
        var e = new StringWriter();
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "demo-grid", "--ticks", "200", "--warmup-ticks", "20", "--out", outFile, "--scenarios-dir", scenarios },
            o,
            e);
        Assert.True(code == 0, e.ToString());

        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(outFile));
        JsonElement root = d.RootElement;
        foreach (string k in new[] { "scenario", "ticks", "commit", "environment", "perf", "traffic" })
        {
            Assert.True(root.TryGetProperty(k, out _), k);
        }

        JsonElement perf = root.GetProperty("perf");
        foreach (string k in new[] { "ticks_per_sec", "vehicle_updates_per_sec", "alloc_bytes_per_tick", "gc_gen0", "gc_gen1", "gc_gen2", "peak_memory_mb", "bytes_per_vehicle", "pathfind_queries", "pathfind_ms_p95" })
        {
            Assert.True(perf.TryGetProperty(k, out _), k);
        }

        foreach (string k in new[] { "p50", "p95", "p99", "max" })
        {
            Assert.True(perf.GetProperty("tick_ms").TryGetProperty(k, out _), k);
        }

        JsonElement traffic = root.GetProperty("traffic");
        foreach (string k in new[] { "avg_speed_kmh", "avg_trip_time_s", "trips_completed_per_min", "stuck_vehicles", "gridlock_flag", "bus_headway_deviation_s", "top10_intersections" })
        {
            Assert.True(traffic.TryGetProperty(k, out _), k);
        }

        Assert.Equal(200, root.GetProperty("ticks").GetInt32());
        Assert.True(perf.GetProperty("tick_ms").GetProperty("p95").GetDouble() >= perf.GetProperty("tick_ms").GetProperty("p50").GetDouble());
    }

    [Fact]
    public void Bench_MissingArgs_Exit2()
    {
        Assert.Equal(2, BenchCommand.Run(new[] { "bench", "--ticks", "10" }, new StringWriter(), new StringWriter()));
        Assert.Equal(2, BenchCommand.Run(new[] { "bench", "--ticks", "abc", "--scenario", "x", "--out", "y" }, new StringWriter(), new StringWriter()));
    }

    private static string FindScenariosDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "scenarios");
            if (File.Exists(Path.Combine(candidate, "demo-grid.json")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("scenarios dir not found");
    }
}
