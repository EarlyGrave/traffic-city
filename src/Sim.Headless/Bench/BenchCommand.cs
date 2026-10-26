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
using Sim.Core.Network;
using Sim.Headless.Scenarios;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

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
        "Usage: Sim.Headless bench --scenario <name|path> --ticks N --out <file.json> [--warmup-ticks M] [--warmup-until-full] [--warmup-max-ticks K] [--repeat R] [--phases] [--threads N] [--view all|focus|x0,z0,x1,z1] [--seed S] [--signal-mode adaptive|fixed] [--signal-coordination alternate|legacy|none] [--scenarios-dir <dir>] [--packages-dir <dir>] [--warmup-days D] [--window am_peak]\n" +
        "  --warmup-days D      residents scenarios: warm up D whole game days (then up to the window start, if any) instead of --warmup-ticks; JSON gets warmup_days\n" +
        "  --window am_peak     residents scenarios: measure the morning peak, 6000 ticks (the default of --ticks here) from 6 slots before the earliest shift start; JSON gets window,\n" +
        "                       compare refuses to mix values\n" +
        "  --phases             time each of the 10 tick phases (core-api 7) via Simulation.OnPhaseEnd; JSON gets perf.phase_ms\n" +
        "                       (the probe adds ~10 timer reads per tick, so tick_ms of a --phases run is not for the gate)\n" +
        "  --threads N          worker threads of the core (SimRuntimeOptions.WorkerThreads, default 1); JSON gets threads, compare refuses to mix values\n" +
        "  --view V             what the snapshot phase writes (SetRenderView): all (default), focus (3x3 km around the network centre) or a rectangle\n" +
        "                       x0,z0,x1,z1 in metres; JSON gets view, compare refuses to mix values\n" +
        "  --seed S             override the scenario seed (unsigned 64-bit); JSON gets seed\n" +
        "  --signal-mode M      override city.signal_mode (adaptive|fixed; the core supports only fixed until step 2.5 S2); JSON gets signal_mode\n" +
        "  --signal-coordination K  override city.signal_coordination (alternate|legacy|none); JSON gets signal_coordination\n" +
        "  --warmup-until-full  after --warmup-ticks keep warming up (in chunks of 20 ticks) until active vehicles >= 98% of the scenario target,\n" +
        "                       but not beyond --warmup-max-ticks in total (default 30000); JSON gets ramp_complete/active_*/target_vehicles.\n";

    /// <summary>Доля цели сценария, с которой считаем рампу завершённой (машины в стационаре колеблются у цели).</summary>
    public const double FullShare = 0.98;
    public const int WarmupChunkTicks = 20;
    public const int DefaultWarmupMaxTicks = 30000;

    /// <summary>Окно утреннего пика residents-сценариев (zoning-demand.md §11): 6 000 тиков.</summary>
    public const string AmPeak = "am_peak";
    public const int AmPeakWindowTicks = 6000;

    /// <summary>Прогрев городом: целые сутки и окно (<see cref="AmPeak"/> или null). Без жителей игнорируется.</summary>
    internal readonly record struct CityWindow(int WarmupDays, string? Window);

    private static readonly string[] _allowed = { "--scenario", "--ticks", "--out", "--warmup-ticks", "--repeat", "--scenarios-dir", "--warmup-max-ticks", "--threads", "--view", "--seed", "--signal-mode", "--signal-coordination", "--warmup-days", "--window", "--packages-dir" };
    private static readonly string[] _flags = { "--warmup-until-full", "--phases" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, _flags, out CliArgs a)
            || !a.TryGetInt("--ticks", 1, out int ticks, a.Has("--window") ? AmPeakWindowTicks : 2000)
            || !a.TryGetInt("--warmup-days", 0, out int warmupDays, 0)
            || !a.TryGetInt("--warmup-ticks", 0, out int warmup, 200)
            || !a.TryGetInt("--warmup-max-ticks", 0, out int warmupMax, DefaultWarmupMaxTicks)
            || !a.TryGetInt("--repeat", 1, out int repeat, 1)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"bench: {a.Error}");
            error.Write(Usage);
            return ExitUsage;
        }

        string? window = a.Get("--window");
        if (window != null && window != AmPeak)
        {
            error.WriteLine($"bench: --window: expected {AmPeak}, got '{window}'");
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
            LoadedScenario sc = PackageDirs.Load(path, a);
            sc = ApplyOverrides(sc, a);
            string view = a.Get("--view") ?? "all";
            if (!ViewSpec.TryParse(view, out ViewSpec viewSpec))
            {
                error.WriteLine($"bench: --view: expected all, focus or x0,z0,x1,z1, got '{view}'");
                error.Write(Usage);
                return ExitUsage;
            }

            r = MeasureRepeated(sc, ticks, warmup, repeat, a.Has("--warmup-until-full") ? Math.Max(warmup, warmupMax) : 0, a.Has("--phases"), threads, viewSpec, new CityWindow(warmupDays, window));
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

    /// <summary>Опции <c>--seed</c>, <c>--signal-mode</c>, <c>--signal-coordination</c> поверх сценария; неверное значение - <see cref="ScenarioException"/>.</summary>
    internal static LoadedScenario ApplyOverrides(LoadedScenario sc, CliArgs a)
    {
        string? seedText = a.Get("--seed");
        string? mode = a.Get("--signal-mode");
        string? coord = a.Get("--signal-coordination");
        if (seedText == null && mode == null && coord == null)
        {
            return sc;
        }

        ScenarioDesc d = sc.Desc;
        if (seedText != null)
        {
            if (!ulong.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
            {
                throw new ScenarioException($"--seed: expected an unsigned 64-bit integer, got '{seedText}'");
            }

            d.Seed = seed;
        }

        if (mode != null)
        {
            d.City.SignalMode = ScenarioLoader.ParseSignalMode(mode, "--signal-mode");
        }

        if (coord != null)
        {
            d.City.SignalCoordination = ScenarioLoader.ParseSignalCoordination(coord, "--signal-coordination");
        }

        return new LoadedScenario(sc.Name, sc.TypeNames, d, sc.Catalog, sc.Packages, sc.Timeline);
    }

    /// <summary>
    /// <paramref name="repeat"/> независимых прогонов; в итог берётся прогон с медианным p95, p50 — медиана p50,
    /// аллокации — максимум (любая аллокация в любом прогоне видна). Все значения — в <c>tick_ms_runs</c>.
    /// </summary>
    internal static BenchResult MeasureRepeated(LoadedScenario sc, int ticks, int warmup, int repeat, int warmupUntilFullMax = 0, bool phases = false, int threads = 1, ViewSpec view = default, CityWindow cityWindow = default)
    {
        var runs = new List<BenchResult>(repeat);
        for (int i = 0; i < repeat; i++)
        {
            runs.Add(Measure(sc, ticks, warmup, warmupUntilFullMax, phases, threads, view, cityWindow));
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

    internal static BenchResult Measure(LoadedScenario sc, int ticks, int warmup, int warmupUntilFullMax = 0, bool phases = false, int threads = 1, ViewSpec view = default, CityWindow cityWindow = default)
    {
        var r = new BenchResult
        {
            Scenario = sc.Name,
            ContentFingerprint = Sim.Scenarios.Mods.ContentFingerprint.Hex(Sim.Scenarios.Mods.ContentFingerprint.For(sc)),
            Ticks = ticks,
            WarmupTicks = warmup,
            WarmupDays = cityWindow.WarmupDays,
            Window = cityWindow.Window,
            Threads = threads,
            View = view.ToString(),
            Seed = sc.Desc.Seed,
            SignalMode = sc.Desc.City.SignalMode == CitySignalMode.Adaptive ? "adaptive" : "fixed",
            SignalCoordination = sc.Desc.City.SignalCoordination switch
            {
                CitySignalCoordination.Alternate => "alternate",
                CitySignalCoordination.None => "none",
                _ => "legacy",
            },
        };
        double[] samples = new double[ticks];
        PhaseTimer? timer = phases ? new PhaseTimer(ticks) : null;

        long memBefore = GC.GetTotalMemory(true);
        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        view.Apply(sim);
        var timeline = new TimelineRunner(sc.Timeline);   // команды сценария в абсолютных тиках симуляции, в том числе в прогреве
        int target = sc.Desc.Demand.TargetVehicles;
        int warmedUp;
        if (sim.HasCity && (cityWindow.WarmupDays > 0 || cityWindow.Window != null))
        {
            // Город: целые сутки прогрева и выход на начало окна утреннего пика (тик внутри суток); рампы OD-генератора нет.
            long start = ((long)cityWindow.WarmupDays * sim.CityDayTicks) + (cityWindow.Window == AmPeak ? sim.CityAmPeakStartTickOfDay : 0);
            while (sim.Tick < start)
            {
                timeline.Step(sim, (int)Math.Min(sim.CityDayTicks, start - sim.Tick));
            }

            warmedUp = (int)Math.Min(int.MaxValue, sim.Tick);
        }
        else
        {
            if (warmup > 0)
            {
                timeline.Step(sim, warmup);
            }

            warmedUp = WarmUpUntilFull(sim, target, warmup, warmupUntilFullMax, timeline);
        }

        r.WarmupTicksActual = warmedUp;
        r.TargetVehicles = target;
        r.ActiveAtStart = sim.ActiveVehicleCount;

        long vtBefore = sim.Counters.VehicleTicksTotal;
        SimCounters cBefore = sim.Counters;
        var sigBefore = new SignalMetrics();
        sim.CopySignalMetrics(sigBefore);
        sim.ResetSignalWindow();
        SimWork workBefore = sim.Work;
        PedWindow? pedBefore = sim.HasPeds ? PedWindow.Snapshot(sim) : null;
        if (timer != null)
        {
            sim.OnPhaseEnd = timer.OnPhaseEnd;
        }

        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        // воркеры аллоцируют в своих потоках: при threads > 1 меряем весь процесс (в окне больше никто не работает)
        long allocBefore = threads > 1 ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
        long windowStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < ticks; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            timer?.BeginTick(t0);
            timeline.Feed(sim);
            sim.Step(1);
            samples[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        long windowEnd = Stopwatch.GetTimestamp();
        long allocAfter = threads > 1 ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
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

        r.Signal = SignalWindow.Compute(sim, in cBefore, sigBefore);
        r.Peds = pedBefore != null ? PedWindow.Compute(sim, pedBefore, in cBefore, in workBefore, ticks) : null;
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
    internal static int WarmUpUntilFull(Simulation sim, int target, int warmup, int warmupMax, TimelineRunner? timeline = null)
    {
        int warmedUp = warmup;
        while (warmupMax > 0 && warmedUp < warmupMax && !IsFull(sim.ActiveVehicleCount, target))
        {
            int chunk = Math.Min(WarmupChunkTicks, warmupMax - warmedUp);
            if (timeline != null)
            {
                timeline.Step(sim, chunk);
            }
            else
            {
                sim.Step(chunk);
            }

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
            w.WriteString("content_fingerprint", r.ContentFingerprint);
            w.WriteNumber("ticks", r.Ticks);
            w.WriteNumber("warmup_ticks", r.WarmupTicks);
            if (r.WarmupDays > 0)
            {
                w.WriteNumber("warmup_days", r.WarmupDays);
            }

            if (r.Window != null)
            {
                w.WriteString("window", r.Window);
            }

            w.WriteNumber("threads", r.Threads);
            w.WriteString("view", r.View);
            w.WriteNumber("seed", r.Seed);
            w.WriteString("signal_mode", r.SignalMode);
            w.WriteString("signal_coordination", r.SignalCoordination);
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
                int phaseCount = r.Peds != null ? PhaseTimer.TotalCount : PhaseTimer.PhaseCount;
                for (int ph = 0; ph < phaseCount; ph++)
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
            WriteNullable(w, "max_still_s", r.Signal.MaxStillS);
            w.WriteNumber("still_episodes_60s", r.Signal.StillEpisodes60);
            w.WriteNumber("still_episodes_120s", r.Signal.StillEpisodes120);
            w.WriteNumber("still_concurrent_60s_max", r.Signal.StillConcurrent60Max);
            WriteNullable(w, "still_share_60s", r.Signal.StillShare60);
            WriteNullable(w, "lane_share_max_mean", r.Signal.LaneShareMaxMean);
            WriteNullable(w, "lane_share_max_p95", r.Signal.LaneShareMaxP95);
            w.WriteNumber("lane_share_samples", r.Signal.LaneShareSamples);
            WriteNullable(w, "lane_miss_share", r.Signal.LaneMissShare);
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
            w.WriteString("note", "traffic counters above are cumulative since simulation start (include warmup); the signal_* / *_window fields below are deltas over the measured window");
            WriteSignal(w, r.Signal);
            w.WriteEndObject();

            if (r.Peds != null)
            {
                WritePeds(w, r.Peds);
            }

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

    private static readonly string[] _maneuverNames = { "straight", "right", "left", "uturn" };

    /// <summary>Метрики пешеходов и пассажиров за окно замера (pedestrians.md §9); <c>compare</c> их не гейтит, нужны отчёту и верификатору.</summary>
    private static void WritePeds(Utf8JsonWriter w, PedWindow p)
    {
        w.WriteStartObject("peds");
        w.WriteNumber("ped_active", p.ActiveNow);
        w.WriteNumber("ped_target", p.Target);
        w.WriteNumber("ped_active_mean", p.ActiveMean);
        w.WriteNumber("ped_trips_completed", p.Trips);
        w.WriteNumber("ped_trips_per_min", p.TripsPerMin);
        WriteNullable(w, "ped_cross_wait_mean_s", p.CrossWaitMeanS);
        WriteNullable(w, "ped_cross_wait_p95_s", p.CrossWaitP95S);
        WriteNullable(w, "ped_stop_wait_mean_s", p.StopWaitMeanS);
        WriteNullable(w, "ped_stop_wait_p95_s", p.StopWaitP95S);
        w.WriteNumber("ped_stuck_events", p.StuckEvents);
        WriteNullable(w, "car_cross_delay_s_per_veh_h", p.CarCrossDelaySPerVehH);
        WriteNullable(w, "crossing_closed_share", p.CrossingClosedShare);
        w.WriteNumber("bus_passengers_boarded", p.Boarded);
        w.WriteNumber("bus_passengers_alighted", p.Alighted);
        w.WriteNumber("bus_pax_on_board_mean", p.PaxOnBoardMean);
        w.WriteNumber("ped_path_queries", p.PathQueries);
        w.WriteNumber("ped_path_work", p.PathWork);
        w.WriteNumber("ped_no_path", p.NoPath);
        w.WriteNumber("ped_mode_fallback", p.ModeFallback);
        w.WriteNumber("ped_events_per_tick", p.EventsPerTick);
        w.WriteNumber("ped_alloc_failed", p.AllocFailed);
        w.WriteNumber("ped_net_resets", p.NetResets);
        w.WriteEndObject();
    }

    /// <summary>Метрики светофоров за окно замера (intersections.md §10, 2.5); поля только добавляются, <c>compare</c> их не гейтит.</summary>
    private static void WriteSignal(Utf8JsonWriter w, SignalWindow s)
    {
        w.WriteStartObject("signal_wait_s");
        for (int m = 0; m < SignalMetrics.Maneuvers; m++)
        {
            WriteNullable(w, _maneuverNames[m], s.MeanWaitS[m]);
        }

        w.WriteEndObject();
        w.WriteStartObject("signal_wait_p95_s");
        for (int m = 0; m < SignalMetrics.Maneuvers; m++)
        {
            WriteNullable(w, _maneuverNames[m], s.P95WaitS[m]);
        }

        w.WriteEndObject();
        w.WriteStartObject("signal_grants");
        for (int m = 0; m < SignalMetrics.Maneuvers; m++)
        {
            w.WriteNumber(_maneuverNames[m], s.Grants[m]);
        }

        w.WriteEndObject();
        w.WriteNumber("left_wait_max_s", s.LeftWaitMaxS);
        w.WriteNumber("arterial_signal_grants", s.ArterialGrants);
        WriteNullable(w, "arterial_stop_share", s.ArterialStopShare);
        w.WriteNumber("stuck_episodes", s.StuckEpisodes);
        w.WriteNumber("starve_denials", s.StarveDenials);
        w.WriteNumber("grants_revoked", s.GrantsRevoked);
        w.WriteNumber("arrow_phases", s.ArrowPhases);
        w.WriteNumber("spill_outs", s.SpillOuts);
        w.WriteNumber("exit_claims", s.ExitClaims);
        w.WriteNumber("exit_claim_denials", s.ExitClaimDenials);
        w.WriteNumber("gate_denied_exit", s.GateDeniedExit);
        w.WriteNumber("trips_completed_window", s.TripsCompleted);
        WriteNullable(w, "avg_speed_kmh_window", s.AvgSpeedKmh);
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
    public string ContentFingerprint = "";
    public int Ticks;
    public int WarmupTicks;
    public int WarmupDays;
    public string? Window;
    public int Threads = 1;
    public string View = "all";
    public ulong Seed;
    public string SignalMode = "fixed";
    public string SignalCoordination = "legacy";
    public SignalWindow Signal = new();
    public PedWindow? Peds;
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

/// <summary>Разность счётчиков светофоров за окно замера (после - до) и производные: средние, p95 по гистограмме, доля остановок.</summary>
internal sealed class SignalWindow
{
    public double?[] MeanWaitS = new double?[SignalMetrics.Maneuvers];
    public double?[] P95WaitS = new double?[SignalMetrics.Maneuvers];
    public long[] Grants = new long[SignalMetrics.Maneuvers];
    public double LeftWaitMaxS;
    public long ArterialGrants;
    public double? ArterialStopShare;
    public long StuckEpisodes, StarveDenials, GrantsRevoked, ArrowPhases, SpillOuts, ExitClaims, ExitClaimDenials, GateDeniedExit, TripsCompleted;
    public double? AvgSpeedKmh;
    public double MaxStillS;
    public long StillEpisodes60, StillEpisodes120;
    public int StillConcurrent60Max;
    public double? StillShare60;
    public double? LaneShareMaxMean, LaneShareMaxP95, LaneMissShare;
    public long LaneShareSamples;

    public static SignalWindow Compute(Simulation sim, in SimCounters before, SignalMetrics sigBefore)
    {
        var after = new SignalMetrics();
        sim.CopySignalMetrics(after);
        SimCounters c = sim.Counters;
        const double tickS = 1.0 / 20.0;
        var s = new SignalWindow();
        for (int m = 0; m < SignalMetrics.Maneuvers; m++)
        {
            long n = after.Grants[m] - sigBefore.Grants[m];
            s.Grants[m] = n;
            if (n <= 0)
            {
                continue;
            }

            s.MeanWaitS[m] = (after.WaitTicks[m] - sigBefore.WaitTicks[m]) * tickS / n;

            // p95: наименьшая корзина (1 с), в которой накопленная доля допусков >= 95%; последняя корзина - «>= 120 с».
            long need = ((n * 95) + 99) / 100;
            long acc = 0;
            int bucket = SignalMetrics.HistBuckets - 1;
            for (int b = 0; b < SignalMetrics.HistBuckets; b++)
            {
                int idx = (m * SignalMetrics.HistBuckets) + b;
                acc += after.Hist[idx] - sigBefore.Hist[idx];
                if (acc >= need)
                {
                    bucket = b;
                    break;
                }
            }

            s.P95WaitS[m] = bucket;
        }

        s.LeftWaitMaxS = c.SignalLeftWaitMax * tickS;
        s.ArterialGrants = c.ArterialSignalGrants - before.ArterialSignalGrants;
        if (s.ArterialGrants > 0)
        {
            s.ArterialStopShare = (c.ArterialSignalStops - before.ArterialSignalStops) / (double)s.ArterialGrants;
        }

        s.StuckEpisodes = c.StuckEventsTotal - before.StuckEventsTotal;
        s.StarveDenials = c.StarveDenials - before.StarveDenials;
        s.GrantsRevoked = c.GrantsRevoked - before.GrantsRevoked;
        s.ArrowPhases = c.ArrowPhases - before.ArrowPhases;
        s.SpillOuts = c.SpillOuts - before.SpillOuts;
        s.ExitClaims = c.ExitClaims - before.ExitClaims;
        s.ExitClaimDenials = c.ExitClaimDenials - before.ExitClaimDenials;
        s.GateDeniedExit = c.GateDeniedExit - before.GateDeniedExit;
        s.TripsCompleted = c.TripsCompleted - before.TripsCompleted;
        s.MaxStillS = c.StillMaxTicks * tickS;
        s.StillEpisodes60 = c.StillEpisodes60 - before.StillEpisodes60;
        s.StillEpisodes120 = c.StillEpisodes120 - before.StillEpisodes120;
        s.StillConcurrent60Max = c.StillConcurrent60Max;
        long denom = s.TripsCompleted + c.ActiveVehicles;
        if (denom > 0)
        {
            s.StillShare60 = s.StillEpisodes60 / (double)denom;
        }

        if (s.TripsCompleted > 0)
        {
            s.LaneMissShare = (c.Reroutes - before.Reroutes) / (double)s.TripsCompleted;
        }

        s.LaneShareSamples = c.LaneShareSamples - before.LaneShareSamples;
        if (s.LaneShareSamples > 0)
        {
            s.LaneShareMaxMean = (c.LaneShareMaxPermilleSum - before.LaneShareMaxPermilleSum) / 1000.0 / s.LaneShareSamples;
            long[] h = { c.LaneShareH0 - before.LaneShareH0, c.LaneShareH1 - before.LaneShareH1, c.LaneShareH2 - before.LaneShareH2, c.LaneShareH3 - before.LaneShareH3, c.LaneShareH4 - before.LaneShareH4, c.LaneShareH5 - before.LaneShareH5, c.LaneShareH6 - before.LaneShareH6, c.LaneShareH7 - before.LaneShareH7, c.LaneShareH8 - before.LaneShareH8, c.LaneShareH9 - before.LaneShareH9 };
            long need = ((s.LaneShareSamples * 95) + 99) / 100;
            long acc = 0;
            for (int b = 0; b < h.Length; b++)
            {
                acc += h[b];
                if (acc >= need)
                {
                    s.LaneShareMaxP95 = (b + 1) / (double)h.Length;   // верхняя граница корзины (шаг 10%)
                    break;
                }
            }
        }

        long vt = c.VehicleTicksTotal - before.VehicleTicksTotal;
        if (vt > 0)
        {
            s.AvgSpeedKmh = (c.DistanceTotalMm - before.DistanceTotalMm) / 1000.0 / (vt * tickS) * 3.6;
        }

        return s;
    }
}

/// <summary>Вид снапшота для <c>bench --view</c> (эмуляция клиента): <c>all</c>, <c>focus</c> (3x3 км вокруг центра сети) или прямоугольник.</summary>
public readonly struct ViewSpec
{
    /// <summary>Сторона вида <c>focus</c>, м.</summary>
    public const float FocusSizeM = 3000f;

    private enum Kind : byte { All, Focus, Rect }

    private readonly Kind _kind;
    private readonly float _x0, _z0, _x1, _z1;

    private ViewSpec(Kind kind, float x0, float z0, float x1, float z1)
    {
        _kind = kind;
        _x0 = x0;
        _z0 = z0;
        _x1 = x1;
        _z1 = z1;
    }

    public static bool TryParse(string text, out ViewSpec spec)
    {
        spec = default;
        if (text == "all")
        {
            return true;
        }

        if (text == "focus")
        {
            spec = new ViewSpec(Kind.Focus, 0, 0, 0, 0);
            return true;
        }

        string[] p = text.Split(',');
        if (p.Length != 4)
        {
            return false;
        }

        var v = new float[4];
        for (int i = 0; i < 4; i++)
        {
            if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
            {
                return false;
            }
        }

        spec = new ViewSpec(Kind.Rect, v[0], v[1], v[2], v[3]);
        return true;
    }

    public void Apply(Simulation sim)
    {
        switch (_kind)
        {
            case Kind.Focus:
                {
                    NetworkView net = sim.Network;
                    if (net.Nodes.Length == 0)
                    {
                        return;
                    }

                    float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                    foreach (NodeData n in net.Nodes)
                    {
                        minX = Math.Min(minX, n.Position.X);
                        maxX = Math.Max(maxX, n.Position.X);
                        minZ = Math.Min(minZ, n.Position.Z);
                        maxZ = Math.Max(maxZ, n.Position.Z);
                    }

                    float cx = (minX + maxX) * 0.5f;
                    float cz = (minZ + maxZ) * 0.5f;
                    float h = FocusSizeM * 0.5f;
                    sim.SetRenderView(cx - h, cz - h, cx + h, cz + h);
                    break;
                }

            case Kind.Rect:
                sim.SetRenderView(_x0, _z0, _x1, _z1);
                break;
            default:
                sim.SetRenderViewAll();
                break;
        }
    }

    public override string ToString() => _kind switch
    {
        Kind.Focus => "focus",
        Kind.Rect => string.Create(CultureInfo.InvariantCulture, $"{_x0:0.###},{_z0:0.###},{_x1:0.###},{_z1:0.###}"),
        _ => "all",
    };
}
