using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Host;
using Sim.Scenarios;
using Sim.Scenarios.Mods;
using Sim.Scenarios.Tools;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>replay</c> и <c>bugreport</c> (player-tools.md §9, save-format.md §10). <c>replay --log y [--save x] [--until T] [--check]</c> ре-симулирует журнал
/// (мир - из <c>--save</c>, из сейва внутри журнала-баг-репорта или из сценария на тике 0) и сверяет контрольные хэши; <c>replay --record out --scenario s --ticks N</c>
/// пишет журнал прогона (таймлайн сценария + <c>--synth M</c> синтетических команд <c>SetTargetVehicleCount</c>); <c>bugreport</c> упаковывает сейв и журнал в один файл.
/// Коды выхода: 0 - сошлось, 1 - расхождение (<c>--check</c>), 2 - ошибка аргументов/файла/версии.
/// </summary>
public static class ReplayCommands
{
    public const int ExitOk = 0;
    public const int ExitDiverged = 1;
    public const int ExitUsage = 2;

    public const string ReplayUsage =
        "Usage: Sim.Headless replay --log <file> [--save <file>] [--scenario <name|path>] [--until T] [--check] [--threads N] [--scenarios-dir <dir>] [--packages-dir <dir>]\n" +
        "       Sim.Headless replay --record <out> (--scenario <name|path> | --save <file>) --ticks N [--synth M] [--interval K] [--threads N] ...\n" +
        "  replay: re-simulates the log from the save (or the save embedded by 'bugreport', or the scenario at tick 0) and prints a line per checkpoint;\n" +
        "          --check compares the checkpoint hashes (first mismatch: last good tick, first bad tick, both hashes, commands in between; exit 1)\n" +
        "  --record: runs N ticks, feeding the scenario timeline and M synthetic commands through the recorder, writes the log (checkpoint every K ticks, default 1200)\n";

    public const string BugReportUsage =
        "Usage: Sim.Headless bugreport --save <file> --log <file> --out <file> [--scenario <name|path>] [--scenarios-dir <dir>]\n" +
        "  packs the last autosave, the log from it, the core versions and the source scenario (if given) into one file that 'replay --log' runs on its own\n";

    private static readonly string[] _replayAllowed =
    {
        "--log", "--save", "--scenario", "--until", "--record", "--ticks", "--synth", "--interval", "--threads", "--scenarios-dir", "--packages-dir",
    };

    private static readonly string[] _replayFlags = { "--check" };
    private static readonly string[] _bugAllowed = { "--save", "--log", "--out", "--scenario", "--scenarios-dir" };

    public static int RunReplay(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _replayAllowed, _replayFlags, out CliArgs a)
            || !a.TryGetInt("--until", 0, out int until, -1)
            || !a.TryGetInt("--ticks", 1, out int ticks, 1200)
            || !a.TryGetInt("--synth", 0, out int synth, 0)
            || !a.TryGetInt("--interval", 1, out int interval, ReplayLog.DefaultInterval)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"replay: {a.Error}");
            error.Write(ReplayUsage);
            return ExitUsage;
        }

        try
        {
            return a.Has("--record")
                ? Record(a, ticks, synth, interval, threads, output, error)
                : Verify(a, until, threads, output, error);
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }
    }

    public static int RunBugReport(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _bugAllowed, out CliArgs a))
        {
            error.WriteLine($"bugreport: {a.Error}");
            error.Write(BugReportUsage);
            return ExitUsage;
        }

        string? savePath = a.Get("--save");
        string? logPath = a.Get("--log");
        string? outPath = a.Get("--out");
        if (savePath == null || logPath == null || outPath == null)
        {
            error.WriteLine("bugreport: --save, --log and --out are required");
            error.Write(BugReportUsage);
            return ExitUsage;
        }

        if (!TryLoadLog(logPath, out ReplayLog log, error))
        {
            return ExitUsage;
        }

        byte[] container;
        try
        {
            container = SaveEnvelope.ReadContainer(savePath);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }

        log.EmbeddedSave = container;
        string? scenario = a.Get("--scenario");
        if (scenario != null)
        {
            try
            {
                string path = ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir"));
                log.ScenarioJson = File.ReadAllText(path);
                if (log.Scenario.Length == 0)
                {
                    log.Scenario = scenario;
                }
            }
            catch (Exception e) when (e is ScenarioException or IOException)
            {
                error.WriteLine($"error: {e.Message}");
                return ExitUsage;
            }
        }

        File.WriteAllBytes(outPath, log.ToBytes());
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"bugreport: file={outPath} save_bytes={container.Length} commands={log.Commands.Count} start_tick={log.StartTick} world_version={log.WorldVersion} api_version={log.ApiVersion} scenario={(log.Scenario.Length > 0 ? log.Scenario : "-")}"));
        return ExitOk;
    }

    private static int Verify(CliArgs a, int until, int threads, TextWriter output, TextWriter error)
    {
        string? logPath = a.Get("--log");
        if (logPath == null)
        {
            error.WriteLine("replay: --log (or --record) is required");
            error.Write(ReplayUsage);
            return ExitUsage;
        }

        if (!TryLoadLog(logPath, out ReplayLog log, error))
        {
            return ExitUsage;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"replay: log={logPath} start_tick={log.StartTick} commands={log.Commands.Count} checkpoints={log.Checkpoints.Count} interval={log.Interval} world_version={log.WorldVersion} api_version={log.ApiVersion}"));

        var runtime = new SimRuntimeOptions { WorkerThreads = threads };
        Simulation? sim = null;
        try
        {
            string? savePath = a.Get("--save");
            if (savePath != null || log.EmbeddedSave != null)
            {
                byte[] container;
                try
                {
                    container = savePath != null ? SaveEnvelope.ReadContainer(savePath) : log.EmbeddedSave!;
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                    error.WriteLine($"error: {e.Message}");
                    return ExitUsage;
                }

                var options = new LoadOptions { StrictHash = 1 };
                LoadStatus st = SaveAssets.Load(container, in options, runtime, PackageDirs.For(Directory.GetCurrentDirectory(), a), out sim, out LoadReport rep, out _);
                if (st != LoadStatus.Ok || sim == null)
                {
                    error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"replay: save load failed: {st} (block 0x{rep.FailedBlock:x8})"));
                    return ExitUsage;
                }
            }
            else
            {
                string? scenario = a.Get("--scenario") ?? (log.Scenario.Length > 0 ? log.Scenario : null);
                if (scenario == null || log.StartTick != 0)
                {
                    error.WriteLine("replay: the log starts from a save; pass --save <file> (or build the log with 'bugreport')");
                    return ExitUsage;
                }

                LoadedScenario sc = PackageDirs.Load(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir")), a);
                if (log.ContentFingerprint != 0 && ContentFingerprint.For(sc) != log.ContentFingerprint)
                {
                    error.WriteLine("replay: content fingerprint of the scenario differs from the one in the log (other mods/content); the replay would not be comparable");
                    return ExitUsage;
                }

                sim = CreateFromScenario(sc, runtime);
            }

            using (sim)
            {
                if (sim.Tick < log.StartTick || (sim.Tick > log.StartTick && sim.Tick > (until >= 0 ? until : long.MaxValue)))
                {
                    error.WriteLine($"replay: world is at tick {sim.Tick}, log starts at {log.StartTick}, --until {until}");
                    return ExitUsage;
                }

                bool check = a.Has("--check");
                ReplayResult r = ReplayPlayer.Run(sim, log, until, check, line => output.WriteLine("  " + line));
                if (r.Status != ReplayStatus.Ok)
                {
                    error.WriteLine($"replay: {r.Status}: {r.Error}");
                    return ExitUsage;
                }

                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"replay: tick={r.EndTick} hash={r.EndHash:x16} commands_applied={r.CommandsApplied} checkpoints_checked={r.CheckpointsChecked}"));
                if (!r.Diverged)
                {
                    output.WriteLine(check ? "replay: OK (all checked hashes match)" : "replay: done (no --check: hashes not compared)");
                    return ExitOk;
                }

                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"replay: DIVERGED last_good_tick={r.LastGoodTick} first_bad_tick={r.BadTick} expected={r.Expected:x16} actual={r.Actual:x16}"));
                int shown = 0;
                for (int i = 0; i < log.Commands.Count && shown < 20; i++)
                {
                    long t = log.CommandTicks[i];
                    if (t >= r.LastGoodTick && t < r.BadTick)
                    {
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  command in the diverging window: tick={t} kind={log.Commands[i].Kind} tag={log.Commands[i].Tag}"));
                        shown++;
                    }
                }

                return ExitDiverged;
            }
        }
        finally
        {
            sim?.Dispose();
        }
    }

    private static int Record(CliArgs a, int ticks, int synth, int interval, int threads, TextWriter output, TextWriter error)
    {
        string? outPath = a.Get("--record");
        string? scenario = a.Get("--scenario");
        string? savePath = a.Get("--save");
        if (outPath == null || (scenario == null) == (savePath == null))
        {
            error.WriteLine("replay: --record needs <out> and exactly one of --scenario/--save");
            error.Write(ReplayUsage);
            return ExitUsage;
        }

        var runtime = new SimRuntimeOptions { WorkerThreads = threads };
        Simulation sim;
        IReadOnlyList<TimelineEntry> timeline = [];
        ulong fingerprint = 0;
        string name = string.Empty;
        int target = 1000;
        if (scenario != null)
        {
            LoadedScenario sc = PackageDirs.Load(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir")), a);
            sim = CreateFromScenario(sc, runtime);
            timeline = sc.Timeline;
            fingerprint = ContentFingerprint.For(sc);
            name = scenario;
            target = Math.Max(1, sc.Desc.Demand.TargetVehicles);
        }
        else
        {
            byte[] container;
            try
            {
                container = SaveEnvelope.ReadContainer(savePath!);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                error.WriteLine($"error: {e.Message}");
                return ExitUsage;
            }

            LoadStatus st = SaveAssets.Load(container, new LoadOptions { StrictHash = 1 }, runtime, PackageDirs.For(Directory.GetCurrentDirectory(), a), out Simulation? loaded, out _, out _);
            if (st != LoadStatus.Ok || loaded == null)
            {
                error.WriteLine($"replay: save load failed: {st}");
                return ExitUsage;
            }

            sim = loaded;
        }

        using (sim)
        {
            long start = sim.Tick;
            var plan = new List<(long Tick, int Seq, Command Cmd)>();
            foreach (TimelineEntry e in timeline)
            {
                plan.Add((e.Tick, plan.Count, e.Cmd));
            }

            for (int i = 0; i < synth; i++)
            {
                long t = start + (((long)(i + 1) * ticks) / (synth + 1));
                int count = (int)((long)target * (80 + ((i * 7) % 41)) / 100);
                plan.Add((t, plan.Count, Command.MakeSetTargetVehicleCount(count, (uint)(i + 1))));
            }

            plan.Sort((x, y) => x.Tick != y.Tick ? x.Tick.CompareTo(y.Tick) : x.Seq.CompareTo(y.Seq));   // стабильно: равные тики - в порядке добавления
            var rec = new ReplayRecorder(sim, interval, name, fingerprint);
            long end = start + ticks;
            int dropped = 0;
            foreach ((long tick, _, Command cmd) in plan)
            {
                if (tick >= end)
                {
                    break;
                }

                if (tick > sim.Tick)
                {
                    rec.Step((int)(tick - sim.Tick));
                }

                Command c = cmd;
                if (!rec.Enqueue(in c))
                {
                    dropped++;
                }
            }

            rec.Step((int)(end - sim.Tick));
            File.WriteAllBytes(outPath, rec.Finish().ToBytes());
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"recorded: file={outPath} start_tick={start} end_tick={sim.Tick} commands={rec.Log.Commands.Count} dropped={dropped} checkpoints={rec.Log.Checkpoints.Count} interval={interval} end_hash={sim.ComputeStateHash():x16}"));
        }

        return ExitOk;
    }

    private static bool TryLoadLog(string path, out ReplayLog log, TextWriter error)
    {
        ReplayStatus st = ReplayLog.TryLoad(path, out log, out string msg);
        if (st == ReplayStatus.Ok)
        {
            st = log.CheckCompatible(out msg);
        }

        if (st != ReplayStatus.Ok)
        {
            error.WriteLine($"error: {path}: {st}{(msg.Length > 0 ? ": " + msg : string.Empty)}");
            return false;
        }

        return true;
    }

    public static Simulation CreateFromScenario(LoadedScenario sc, in SimRuntimeOptions runtime)
    {
        Simulation sim = Simulation.Create(sc.Desc, in runtime);
        AssetRef[] assets;
        byte[] assetIds;
        if (sc.Catalog != null && sc.Packages != null)
        {
            AssetManifestBuilder.ForCatalog(sc.Catalog, sc.Packages, out assets, out assetIds);
        }
        else
        {
            AssetManifestBuilder.ForVehicleTypes(sc.TypeNames, sc.Desc.VehicleTypes, out assets, out assetIds);
        }

        sim.SetAssetManifest(assets, assetIds);
        return sim;
    }
}
