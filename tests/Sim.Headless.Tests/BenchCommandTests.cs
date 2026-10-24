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
