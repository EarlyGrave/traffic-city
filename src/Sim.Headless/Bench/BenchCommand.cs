using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

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
        "Usage: Sim.Headless bench --scenario <name|path> --ticks N --out <file.json> [--warmup-ticks M] [--warmup-until-full] [--warmup-max-ticks K] [--repeat R] [--phases] [--scenarios-dir <dir>]\n" +
        "  --phases             time each of the 10 tick phases (core-api 7) via Simulation.OnPhaseEnd; JSON gets perf.phase_ms\n" +
        "                       (the probe adds ~10 timer reads per tick, so tick_ms of a --phases run is not for the gate)\n" +
        "  --warmup-until-full  after --warmup-ticks keep warming up (in chunks of 20 ticks) until active vehicles >= 98% of the scenario target,\n" +
        "                       but not beyond --warmup-max-ticks in total (default 30000); JSON gets ramp_complete/active_*/target_vehicles.\n";

    /// <summary>Доля цели сценария, с которой считаем рампу завершённой (машины в стационаре колеблются у цели).</summary>
    public const double FullShare = 0.98;
    public const int WarmupChunkTicks = 20;
    public const int DefaultWarmupMaxTicks = 30000;

    private static readonly string[] _allowed = { "--scenario", "--ticks", "--out", "--warmup-ticks", "--repeat", "--scenarios-dir", "--warmup-max-ticks" };
    private static readonly string[] _flags = { "--warmup-until-full", "--phases" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, _flags, out CliArgs a)
            || !a.TryGetInt("--ticks", 1, out int ticks, 2000)
            || !a.TryGetInt("--warmup-ticks", 0, out int warmup, 200)
            || !a.TryGetInt("--warmup-max-ticks", 0, out int warmupMax, DefaultWarmupMaxTicks)
            || !a.TryGetInt("--repeat", 1, out int repeat, 1))
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
            r = MeasureRepeated(sc, ticks, warmup, repeat, a.Has("--warmup-until-full") ? Math.Max(warmup, warmupMax) : 0, a.Has("--phases"));
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

    /// <summary>
    /// <paramref name="repeat"/> независимых прогонов; в итог берётся прогон с медианным p95, p50 — медиана p50,
    /// аллокации — максимум (любая аллокация в любом прогоне видна). Все значения — в <c>tick_ms_runs</c>.
    /// </summary>
    internal static BenchResult MeasureRepeated(LoadedScenario sc, int ticks, int warmup, int repeat, int warmupUntilFullMax = 0, bool phases = false)
    {
        var runs = new List<BenchResult>(repeat);
        for (int i = 0; i < repeat; i++)
        {
            runs.Add(Measure(sc, ticks, warmup, warmupUntilFullMax, phases));
        }

        List<BenchResult> byP95 = runs.OrderBy(x => x.P95).ToList();
        BenchResult med = byP95[(byP95.Count - 1) / 2];
        med.P50 = Median(runs.Select(x => x.P50));
        med.AllocBytesPerTick = runs.Max(x => x.AllocBytesPerTick);
        foreach (BenchResult x in runs)
        {
            med.Runs.Add((x.P50, x.P95));
        }

        return med;
    }

    public static double Median(IEnumerable<double> values)
    {
        double[] a = values.OrderBy(x => x).ToArray();
        int n = a.Length;
        return n == 0 ? 0 : n % 2 == 1 ? a[n / 2] : (a[(n / 2) - 1] + a[n / 2]) / 2;
    }

    /// <summary>Цель сценария достигнута: активных не меньше <see cref="FullShare"/> от цели (цель 0 — всегда «полно»).</summary>
    public static bool IsFull(int active, int target) => active >= (int)Math.Floor(target * FullShare);

    internal static BenchResult Measure(LoadedScenario sc, int ticks, int warmup, int warmupUntilFullMax = 0, bool phases = false)
    {
        var r = new BenchResult { Scenario = sc.Name, Ticks = ticks, WarmupTicks = warmup };
        double[] samples = new double[ticks];
        PhaseTimer? timer = phases ? new PhaseTimer(ticks) : null;

        long memBefore = GC.GetTotalMemory(true);
        using Simulation sim = Simulation.Create(sc.Desc);
        if (warmup > 0)
        {
            sim.Step(warmup);
        }

        int target = sc.Desc.Demand.TargetVehicles;
        int warmedUp = WarmUpUntilFull(sim, target, warmup, warmupUntilFullMax);

        r.WarmupTicksActual = warmedUp;
        r.TargetVehicles = target;
        r.ActiveAtStart = sim.ActiveVehicleCount;

        long vtBefore = sim.Counters.VehicleTicksTotal;
        SimWork workBefore = sim.Work;
        if (timer != null)
        {
            sim.OnPhaseEnd = timer.OnPhaseEnd;
        }

        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long windowStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < ticks; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            timer?.BeginTick(t0);
            sim.Step(1);
            samples[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        long windowEnd = Stopwatch.GetTimestamp();
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        sim.OnPhaseEnd = null;
        r.Work = Delta(sim.Work, workBefore);
        r.PhaseMs = timer?.Summarize();
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
        r.RampComplete = IsFull(r.ActiveAtStart, target) && IsFull(r.ActiveVehicles, target);
        long memAfter = GC.GetTotalMemory(true);
        r.BytesPerVehicle = r.ActiveVehicles > 0 ? (memAfter - memBefore) / (double)r.ActiveVehicles : null;

        SimCounters c = sim.Counters;
        sim.GetStats(out SimStats s);
        r.PathfindQueries = c.PathfindQueries;
        r.Counters = c;
        r.AvgSpeedKmh = s.AvgSpeedKmh;
        r.AvgTripTimeS = s.AvgTripTimeS;
        r.TripsCompletedPerMin = s.TripsCompletedPerMin;
        r.StuckVehicles = s.StuckVehicles;
        r.Gridlock = s.GridlockFlag != 0;
        r.BusHeadwayDeviationS = c.BusHeadwaySamples > 0 ? s.BusHeadwayDeviationS : null;

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

    /// <summary>
    /// После <paramref name="warmup"/> тиков (уже выполненных) догоняет рампу чанками по <see cref="WarmupChunkTicks"/> до
    /// <see cref="IsFull"/>, но не дальше <paramref name="warmupMax"/> всего (0 = не догонять). Возвращает число тиков прогрева.
    /// </summary>
    internal static int WarmUpUntilFull(Simulation sim, int target, int warmup, int warmupMax)
    {
        int warmedUp = warmup;
        while (warmupMax > 0 && warmedUp < warmupMax && !IsFull(sim.ActiveVehicleCount, target))
        {
            int chunk = Math.Min(WarmupChunkTicks, warmupMax - warmedUp);
            sim.Step(chunk);
            warmedUp += chunk;
        }

        return warmedUp;
    }

    /// <summary>Разность монотонных счётчиков работы (<paramref name="after"/> - <paramref name="before"/>).</summary>
    internal static SimWork Delta(in SimWork after, in SimWork before) => new()
    {
        BehaviourVehicles = after.BehaviourVehicles - before.BehaviourVehicles,
        LeaderLookaheadHops = after.LeaderLookaheadHops - before.LeaderLookaheadHops,
        ObstacleGapCalls = after.ObstacleGapCalls - before.ObstacleGapCalls,
        ConflictGapCallsBehaviour = after.ConflictGapCallsBehaviour - before.ConflictGapCallsBehaviour,
        ConflictGapCallsIntegrate = after.ConflictGapCallsIntegrate - before.ConflictGapCallsIntegrate,
        ConflictGapMaskRejects = after.ConflictGapMaskRejects - before.ConflictGapMaskRejects,
        ConflictEntriesScanned = after.ConflictEntriesScanned - before.ConflictEntriesScanned,
        ConflictOccupantsScanned = after.ConflictOccupantsScanned - before.ConflictOccupantsScanned,
        GateCandidates = after.GateCandidates - before.GateCandidates,
        GateTryGrantCalls = after.GateTryGrantCalls - before.GateTryGrantCalls,
        GateTryGrantEntries = after.GateTryGrantEntries - before.GateTryGrantEntries,
        RouteRefillChecks = after.RouteRefillChecks - before.RouteRefillChecks,
        NextConnRecomputes = after.NextConnRecomputes - before.NextConnRecomputes,
        SnapshotSlots = after.SnapshotSlots - before.SnapshotSlots,
    };

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
            if (r.PhaseMs is { } phaseMs)
            {
                w.WriteStartObject("phase_ms");
                for (int ph = 0; ph < PhaseTimer.PhaseCount; ph++)
                {
                    w.WriteStartObject(PhaseTimer.Names[ph]);
                    w.WriteNumber("p50", phaseMs[ph].P50);
                    w.WriteNumber("p95", phaseMs[ph].P95);
                    w.WriteNumber("mean", phaseMs[ph].Mean);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteStartObject("work_per_tick");
            WriteWork(w, in r.Work, r.Ticks);
            w.WriteEndObject();
            w.WriteStartArray("tick_ms_runs");
            foreach ((double p50, double p95) in r.Runs)
            {
                w.WriteStartObject();
                w.WriteNumber("p50", p50);
                w.WriteNumber("p95", p95);
                w.WriteEndObject();
            }

            w.WriteEndArray();
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
            w.WriteNumber("active_vehicles_start", r.ActiveAtStart);
            w.WriteNumber("target_vehicles", r.TargetVehicles);
            w.WriteBoolean("ramp_complete", r.RampComplete);
            w.WriteNumber("warmup_ticks_actual", r.WarmupTicksActual);
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

            // Счётчики маршрутизации и оверлея (routing.md 9.10), накопительные с начала симуляции, кроме overlay_disabled (текущее).
            SimCounters k = r.Counters;
            w.WriteStartObject("routing");
            w.WriteNumber("pathfind_queries", k.PathfindQueries);
            w.WriteNumber("pathfind_work", k.PathfindWork);
            w.WriteNumber("pathfind_expanded_nodes", k.PathfindExpandedNodes);
            w.WriteNumber("route_failed", k.RouteFailed);
            w.WriteNumber("route_deferred", k.RouteDeferred);
            w.WriteNumber("reroutes", k.Reroutes);
            w.WriteNumber("timed_checks", k.TimedChecks);
            w.WriteNumber("timed_triggered", k.TimedTriggered);
            w.WriteNumber("timed_reroutes", k.TimedReroutes);
            w.WriteNumber("plan_unpacks", k.PlanUnpacks);
            w.WriteNumber("unpack_work", k.UnpackWork);
            w.WriteNumber("window_refills", k.WindowRefills);
            w.WriteNumber("refill_late", k.RefillLate);
            w.WriteNumber("plan_compactions", k.PlanCompactions);
            w.WriteNumber("cost_recalcs", k.CostRecalcs);
            w.WriteNumber("customize_rows", k.CustomizeRows);
            w.WriteNumber("customize_work", k.CustomizeWork);
            w.WriteNumber("customize_lag_ticks", k.CustomizeLagTicks);
            w.WriteNumber("overlay_rebuilds", k.OverlayRebuilds);
            w.WriteNumber("overlay_disabled", k.OverlayDisabled);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>Средние за тик окна замера (<paramref name="work"/> - разность счётчиков за <paramref name="ticks"/> тиков).</summary>
    private static void WriteWork(Utf8JsonWriter w, in SimWork work, int ticks)
    {
        double n = Math.Max(1, ticks);
        w.WriteNumber("behaviour_vehicles", work.BehaviourVehicles / n);
        w.WriteNumber("leader_lookahead_hops", work.LeaderLookaheadHops / n);
        w.WriteNumber("obstacle_gap_calls", work.ObstacleGapCalls / n);
        w.WriteNumber("conflict_gap_calls_behaviour", work.ConflictGapCallsBehaviour / n);
        w.WriteNumber("conflict_gap_calls_integrate", work.ConflictGapCallsIntegrate / n);
        w.WriteNumber("conflict_gap_mask_rejects", work.ConflictGapMaskRejects / n);
        w.WriteNumber("conflict_entries_scanned", work.ConflictEntriesScanned / n);
        w.WriteNumber("conflict_occupants_scanned", work.ConflictOccupantsScanned / n);
        w.WriteNumber("gate_candidates", work.GateCandidates / n);
        w.WriteNumber("gate_trygrant_calls", work.GateTryGrantCalls / n);
        w.WriteNumber("gate_trygrant_entries", work.GateTryGrantEntries / n);
        w.WriteNumber("route_refill_checks", work.RouteRefillChecks / n);
        w.WriteNumber("next_conn_recomputes", work.NextConnRecomputes / n);
        w.WriteNumber("snapshot_slots", work.SnapshotSlots / n);
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
    public int ActiveAtStart;
    public int TargetVehicles;
    public bool RampComplete;
    public int WarmupTicksActual;
    public int StuckVehicles;
    public double? BytesPerVehicle;
    public double? BusHeadwayDeviationS;
    public long PathfindQueries;
    public SimCounters Counters;
    public SimWork Work;
    public PhaseStat[]? PhaseMs;
    public double AvgSpeedKmh;
    public double AvgTripTimeS;
    public double TripsCompletedPerMin;
    public bool Gridlock;
    public List<(int Node, uint Total, double PerMin)> Top10 { get; } = new();
    public List<(double P50, double P95)> Runs { get; } = new();
}
