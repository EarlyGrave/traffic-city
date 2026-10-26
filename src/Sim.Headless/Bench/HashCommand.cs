using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>hash --scenario &lt;имя|путь&gt; --ticks N [--every K] [--warmup-until-full] [--warmup-max-ticks M] [--scenarios-dir d]</c>:
/// печатает <c>ComputeStateHash()</c> каждые K тиков (по умолчанию - только после последнего). Эталон приёмки оптимизаций
/// «побитово эквивалентно» (hotpath.md D1): до и после правки хэши на всех строках должны совпасть. С <c>--warmup-until-full</c>
/// сначала догоняет рампу (как bench), затем считает N тиков; строки печатаются с номером тика симуляции.
/// </summary>
public static class HashCommand
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;

    public const string Usage =
        "Usage: Sim.Headless hash --scenario <name|path> --ticks N [--every K] [--warmup-until-full] [--warmup-max-ticks M] [--threads N] [--signal-mode adaptive|fixed] [--signal-coordination alternate|legacy|none] [--seed S] [--scenarios-dir <dir>] [--packages-dir <dir>]\n" +
        "  prints 'tick=<T> hash=<16 hex digits> active=<N>' every K ticks (default: only after the last one); --ticks counts after the optional warmup\n";

    private static readonly string[] _allowed = { "--scenario", "--ticks", "--every", "--warmup-max-ticks", "--scenarios-dir", "--threads", "--signal-mode", "--signal-coordination", "--seed", "--packages-dir" };
    private static readonly string[] _flags = { "--warmup-until-full" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, _flags, out CliArgs a)
            || !a.TryGetInt("--ticks", 1, out int ticks, 1000)
            || !a.TryGetInt("--every", 1, out int every, 0)
            || !a.TryGetInt("--warmup-max-ticks", 0, out int warmupMax, BenchCommand.DefaultWarmupMaxTicks)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"hash: {a.Error}");
            error.Write(Usage);
            return ExitUsage;
        }

        string? scenario = a.Get("--scenario");
        if (scenario == null)
        {
            error.WriteLine("hash: --scenario is required");
            error.Write(Usage);
            return ExitUsage;
        }

        LoadedScenario sc;
        try
        {
            sc = BenchCommand.ApplyOverrides(PackageDirs.Load(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir")), a), a);
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }

        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        var timeline = new TimelineRunner(sc.Timeline);
        if (a.Has("--warmup-until-full"))
        {
            BenchCommand.WarmUpUntilFull(sim, sc.Desc.Demand.TargetVehicles, 0, warmupMax, timeline);
        }

        int step = every > 0 ? every : ticks;
        int done = 0;
        while (done < ticks)
        {
            int n = System.Math.Min(step, ticks - done);
            timeline.Step(sim, n);
            done += n;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"tick={sim.Tick} hash={sim.ComputeStateHash():x16} active={sim.ActiveVehicleCount}"));
        }

        timeline.WriteSummary(output);
        return ExitOk;
    }
}
