using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Headless.Scenarios;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>bench --scenario &lt;имя|путь&gt; --ticks N --out file.json [--warmup-ticks M] [--scenarios-dir d]</c>.
/// Формат результата — docs/plan/brief-stage1.md, «Метрики и бюджеты».
/// </summary>
public static class BenchCommand
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;

    public const string Usage =
        "Usage: Sim.Headless bench --scenario <name|path> --ticks N --out <file.json> [--warmup-ticks M] [--scenarios-dir <dir>]\n";

    private static readonly string[] _allowed = { "--scenario", "--ticks", "--out", "--warmup-ticks", "--scenarios-dir" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, out CliArgs a)
            || !a.TryGetInt("--ticks", 1, out int ticks, 2000)
            || !a.TryGetInt("--warmup-ticks", 0, out int warmup, 200))
        {
            error.WriteLine($"bench: {a.Error}");
            error.Write(Usage);
            return ExitUsage;
        }

        string? scenario = a.Get("--scenario");
        string? outPath = a.Get("--out");
        if (scenario == null || outPath == null)
        {
            error.WriteLine("bench: --scenario and --out are required");
            error.Write(Usage);
            return ExitUsage;
        }

        BenchResult r;
        try
        {
            string path = ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir"));
            LoadedScenario sc = ScenarioLoader.LoadInfo(path);
            r = Measure(sc, ticks, warmup);
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outPath, ToJson(r));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"bench {r.Scenario}: {r.Ticks} ticks, tick p50/p95/p99/max = {r.P50:0.####}/{r.P95:0.####}/{r.P99:0.####}/{r.Max:0.####} ms, alloc {r.AllocBytesPerTick:0.###} B/tick, stuck {r.StuckVehicles} -> {outPath}"));
        return ExitOk;
    }

    internal static BenchResult Measure(LoadedScenario sc, int ticks, int warmup)
    {
        var r = new BenchResult { Scenario = sc.Name, Ticks = ticks, WarmupTicks = warmup };
        double[] samples = new double[ticks];

        long memBefore = GC.GetTotalMemory(true);
        using Simulation sim = Simulation.Create(sc.Desc);
        if (warmup > 0)
        {
            sim.Step(warmup);
        }

        long vtBefore = sim.Counters.VehicleTicksTotal;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long windowStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < ticks; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            sim.Step(1);
            samples[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        long windowEnd = Stopwatch.GetTimestamp();
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        r.Gen0 = GC.CollectionCount(0) - g0;
        r.Gen1 = GC.CollectionCount(1) - g1;
        r.Gen2 = GC.CollectionCount(2) - g2;

        double windowSec = (windowEnd - windowStart) / (double)Stopwatch.Frequency;
        r.AllocBytesPerTick = (allocAfter - allocBefore) / (double)ticks;
        r.TicksPerSec = ticks / windowSec;
        r.VehicleUpdatesPerSec = (sim.Counters.VehicleTicksTotal - vtBefore) / windowSec;

        Array.Sort(samples);
        r.P50 = Percentile(samples, 0.50);
        r.P95 = Percentile(samples, 0.95);
        r.P99 = Percentile(samples, 0.99);
        r.Max = samples[^1];

        using (Process self = Process.GetCurrentProcess())
        {
            r.PeakMemoryMb = self.PeakWorkingSet64 / (1024.0 * 1024.0);
        }

        r.ActiveVehicles = sim.ActiveVehicleCount;
        long memAfter = GC.GetTotalMemory(true);
        r.BytesPerVehicle = r.ActiveVehicles > 0 ? (memAfter - memBefore) / (double)r.ActiveVehicles : null;

        SimCounters c = sim.Counters;
        sim.GetStats(out SimStats s);
        r.PathfindQueries = c.PathfindQueries;
        r.AvgSpeedKmh = s.AvgSpeedKmh;
        r.AvgTripTimeS = s.AvgTripTimeS;
        r.TripsCompletedPerMin = s.TripsCompletedPerMin;
        r.StuckVehicles = s.StuckVehicles;
        r.Gridlock = s.GridlockFlag != 0;
        r.BusHeadwayDeviationS = c.BusStopArrivals > 0 ? s.BusHeadwayDeviationS : null;

        // Топ-10 узлов по пропускной способности: счётчик за всю симуляцию / игровые минуты.
        double minutes = c.Ticks * (double)Simulation.Dt / 60.0;
        ReadOnlySpan<uint> thr = sim.NodeThroughput;
        var all = new List<(int Node, uint Count)>();
        for (int i = 0; i < thr.Length; i++)
        {
            if (thr[i] > 0)
            {
                all.Add((i, thr[i]));
            }
        }

        all.Sort((x, y) => y.Count != x.Count ? y.Count.CompareTo(x.Count) : x.Node.CompareTo(y.Node));
        for (int i = 0; i < all.Count && i < 10; i++)
        {
            r.Top10.Add((all[i].Node, all[i].Count, minutes > 0 ? all[i].Count / minutes : 0));
        }

        r.Commit = GitCommit();
        r.Runtime = RuntimeInformation.FrameworkDescription;
        r.Os = RuntimeInformation.OSDescription;
        r.Cpu = CpuName();
        r.CpuCount = Environment.ProcessorCount;
        return r;
    }

    private static double Percentile(double[] sorted, double p)
    {
        int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    private static string? GitCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p == null)
            {
                return null;
            }

            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && o.Length > 0 ? o : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? CpuName()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        int i = line.IndexOf(':', StringComparison.Ordinal);
                        return i >= 0 ? line[(i + 1)..].Trim() : null;
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    internal static string ToJson(BenchResult r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("scenario", r.Scenario);
            w.WriteNumber("ticks", r.Ticks);
            w.WriteNumber("warmup_ticks", r.WarmupTicks);
            WriteNullable(w, "commit", r.Commit);
            w.WriteStartObject("environment");
            w.WriteString("runtime", r.Runtime);
            w.WriteString("os", r.Os);
            WriteNullable(w, "cpu", r.Cpu);
            w.WriteNumber("cpu_count", r.CpuCount);
            w.WriteEndObject();

            w.WriteStartObject("perf");
            w.WriteStartObject("tick_ms");
            w.WriteNumber("p50", r.P50);
            w.WriteNumber("p95", r.P95);
            w.WriteNumber("p99", r.P99);
            w.WriteNumber("max", r.Max);
            w.WriteEndObject();
            w.WriteNumber("ticks_per_sec", r.TicksPerSec);
            w.WriteNumber("vehicle_updates_per_sec", r.VehicleUpdatesPerSec);
            w.WriteNumber("alloc_bytes_per_tick", r.AllocBytesPerTick);
            w.WriteNumber("gc_gen0", r.Gen0);
            w.WriteNumber("gc_gen1", r.Gen1);
            w.WriteNumber("gc_gen2", r.Gen2);
            w.WriteNumber("peak_memory_mb", r.PeakMemoryMb);
            WriteNullable(w, "bytes_per_vehicle", r.BytesPerVehicle);
            w.WriteString(
                "bytes_per_vehicle_note",
                "(GC.GetTotalMemory(true) after run - GC.GetTotalMemory(true) before Simulation.Create) / active vehicles at end");
            w.WriteNumber("active_vehicles", r.ActiveVehicles);
            w.WriteNumber("pathfind_queries", r.PathfindQueries);
            w.WriteNull("pathfind_ms_p95");
            w.WriteString("pathfind_ms_p95_note", "core public API does not expose per-query search time");
            w.WriteEndObject();

            w.WriteStartObject("traffic");
            w.WriteNumber("avg_speed_kmh", r.AvgSpeedKmh);
            w.WriteNumber("avg_trip_time_s", r.AvgTripTimeS);
            w.WriteNumber("trips_completed_per_min", r.TripsCompletedPerMin);
            w.WriteNumber("stuck_vehicles", r.StuckVehicles);
            w.WriteBoolean("gridlock_flag", r.Gridlock);
            WriteNullable(w, "bus_headway_deviation_s", r.BusHeadwayDeviationS);
            w.WriteStartArray("top10_intersections");
            foreach ((int node, uint count, double perMin) in r.Top10)
            {
                w.WriteStartObject();
                w.WriteNumber("node", node);
                w.WriteNumber("total", count);
                w.WriteNumber("vehicles_per_min", perMin);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("note", "traffic counters are cumulative since simulation start (include warmup)");
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? v)
    {
        if (v == null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, v);
        }
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, double? v)
    {
        if (v == null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteNumber(name, v.Value);
        }
    }
}

internal sealed class BenchResult
{
    public string Scenario = "";
    public int Ticks;
    public int WarmupTicks;
    public string? Commit;
    public string? Cpu;
    public string Runtime = "";
    public string Os = "";
    public int CpuCount;
    public double P50;
    public double P95;
    public double P99;
    public double Max;
    public double TicksPerSec;
    public double VehicleUpdatesPerSec;
    public double AllocBytesPerTick;
    public double PeakMemoryMb;
    public int Gen0;
    public int Gen1;
    public int Gen2;
    public int ActiveVehicles;
    public int StuckVehicles;
    public double? BytesPerVehicle;
    public double? BusHeadwayDeviationS;
    public long PathfindQueries;
    public double AvgSpeedKmh;
    public double AvgTripTimeS;
    public double TripsCompletedPerMin;
    public bool Gridlock;
    public List<(int Node, uint Total, double PerMin)> Top10 { get; } = new();
}
