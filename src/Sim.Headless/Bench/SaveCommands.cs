using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>save</c> и <c>load</c> (save-format.md §7, §11): контейнер ядра в файл <c>.tcsave</c> (конверт + deflate, <see cref="SaveFile"/>; <c>--raw</c> - без конверта) и обратно (формат определяется по магии).
/// <c>save --scenario s --ticks N [--warmup-until-full] --out f</c> считает N тиков, пишет сейв, печатает хэш и размер;
/// <c>load --in f [--ticks N] [--every K]</c> грузит строго (хэш заголовка обязан сойтись) и печатает хэши в формате <c>hash</c>, чтобы сравнить с прогоном без сейва.
/// </summary>
public static class SaveCommands
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitFailed = 1;

    public const string SaveUsage =
        "Usage: Sim.Headless save --scenario <name|path> --ticks N --out <file> [--warmup-until-full] [--warmup-max-ticks M] [--threads N] [--scenarios-dir <dir>] [--raw]\n" +
        "  runs N ticks (after the optional warmup), writes the .tcsave envelope (--raw: the bare container), prints 'tick=<T> hash=<hex> active=<N>' and 'saved bytes=<B> save_ms=<ms>'\n";

    public const string LoadUsage =
        "Usage: Sim.Headless load --in <file> [--ticks N] [--every K] [--threads N]\n" +
        "  loads a save (strict hash check), prints 'loaded tick=<T> hash=<hex> active=<N> load_ms=<ms>' then, with --ticks, a 'hash'-style line every K ticks\n";

    private static readonly string[] _saveAllowed = { "--scenario", "--ticks", "--out", "--warmup-max-ticks", "--scenarios-dir", "--threads" };
    private static readonly string[] _saveFlags = { "--warmup-until-full", "--raw" };
    private static readonly string[] _loadAllowed = { "--in", "--ticks", "--every", "--threads" };

    public static int RunSave(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _saveAllowed, _saveFlags, out CliArgs a)
            || !a.TryGetInt("--ticks", 0, out int ticks, 0)
            || !a.TryGetInt("--warmup-max-ticks", 0, out int warmupMax, BenchCommand.DefaultWarmupMaxTicks)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"save: {a.Error}");
            error.Write(SaveUsage);
            return ExitUsage;
        }

        string? scenario = a.Get("--scenario");
        string? outPath = a.Get("--out");
        if (scenario == null || outPath == null)
        {
            error.WriteLine("save: --scenario and --out are required");
            error.Write(SaveUsage);
            return ExitUsage;
        }

        LoadedScenario sc;
        try
        {
            sc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir")));
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }

        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        AssetManifestBuilder.ForVehicleTypes(sc.TypeNames, sc.Desc.VehicleTypes, out AssetRef[] assets, out byte[] assetIds);
        sim.SetAssetManifest(assets, assetIds);
        if (a.Has("--warmup-until-full"))
        {
            BenchCommand.WarmUpUntilFull(sim, sc.Desc.Demand.TargetVehicles, 0, warmupMax);
        }

        sim.Step(ticks);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"tick={sim.Tick} hash={sim.ComputeStateHash():x16} active={sim.ActiveVehicleCount}"));

        long size = sim.MeasureSave();
        byte[] buf = new byte[size];
        long t0 = Stopwatch.GetTimestamp();
        SaveStatus st = sim.Save(buf, out long written);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (st != SaveStatus.Ok || written != size)
        {
            error.WriteLine($"save: failed with {st}");
            return ExitFailed;
        }

        byte[] file = a.Has("--raw") ? buf : SaveFile.Pack(buf);
        File.WriteAllBytes(outPath, file);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"saved bytes={written} file_bytes={file.Length} save_ms={ms:F2} file={outPath}"));
        return ExitOk;
    }

    public static int RunLoad(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _loadAllowed, out CliArgs a)
            || !a.TryGetInt("--ticks", 0, out int ticks, 0)
            || !a.TryGetInt("--every", 1, out int every, 0)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"load: {a.Error}");
            error.Write(LoadUsage);
            return ExitUsage;
        }

        string? path = a.Get("--in");
        if (path == null)
        {
            error.WriteLine("load: --in is required");
            error.Write(LoadUsage);
            return ExitUsage;
        }

        byte[] bytes;
        try
        {
            bytes = SaveFile.ReadContainer(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }

        var options = new LoadOptions { StrictHash = 1 };
        long t0 = Stopwatch.GetTimestamp();
        LoadStatus st = Simulation.Load(bytes, in options, new SimRuntimeOptions { WorkerThreads = threads }, out Simulation? sim, out LoadReport rep);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (st != LoadStatus.Ok || sim == null)
        {
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"load: {st} (block 0x{rep.FailedBlock:x8} column {rep.FailedColumn} row {rep.FailedRow})"));
            return ExitFailed;
        }

        using (sim)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"loaded tick={sim.Tick} hash={sim.ComputeStateHash():x16} active={sim.ActiveVehicleCount} load_ms={ms:F2} world_version={rep.FileWorldVersion} kept_blocks={rep.KeptBlocks} assets={rep.AssetsInManifest}"));
            int step = every > 0 ? every : Math.Max(1, ticks);
            int done = 0;
            while (done < ticks)
            {
                int n = Math.Min(step, ticks - done);
                sim.Step(n);
                done += n;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"tick={sim.Tick} hash={sim.ComputeStateHash():x16} active={sim.ActiveVehicleCount}"));
            }
        }

        return ExitOk;
    }
}
