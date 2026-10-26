using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>run --scenario &lt;имя|путь&gt; --ticks N [--every K] [--threads N] [--seed S] [--scenarios-dir d]</c>: гонит сценарий и печатает счётчики каждые K тиков
/// (по умолчанию только после последнего): машины, остановки (stuck), пешеходы, поездки, переходы. Для быстрой проверки «ходят и не застревают»;
/// замеров времени не делает (это <c>bench</c>).
/// </summary>
public static class RunCommand
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;

    public const string Usage =
        "Usage: Sim.Headless run --scenario <name|path> --ticks N [--every K] [--threads N] [--seed S] [--scenarios-dir <dir>]\n" +
        "  prints vehicle and pedestrian counters every K ticks (default: only after the last one)\n";

    private static readonly string[] _allowed = { "--scenario", "--ticks", "--every", "--scenarios-dir", "--threads", "--signal-mode", "--signal-coordination", "--seed" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, out CliArgs a)
            || !a.TryGetInt("--ticks", 1, out int ticks, 1000)
            || !a.TryGetInt("--every", 1, out int every, 0)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"run: {a.Error}");
            error.Write(Usage);
            return ExitUsage;
        }

        string? scenario = a.Get("--scenario");
        if (scenario == null)
        {
            error.WriteLine("run: --scenario is required");
            error.Write(Usage);
            return ExitUsage;
        }

        LoadedScenario sc;
        try
        {
            sc = BenchCommand.ApplyOverrides(ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir"))), a);
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }

        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        int step = every > 0 ? every : ticks;
        int done = 0;
        while (done < ticks)
        {
            int n = System.Math.Min(step, ticks - done);
            sim.Step(n);
            done += n;
            output.WriteLine(Line(sim));
        }

        return ExitOk;
    }

    private static string Line(Simulation sim)
    {
        SimCounters c = sim.Counters;
        string veh = string.Create(
            CultureInfo.InvariantCulture,
            $"tick={sim.Tick} vehicles={c.ActiveVehicles} stuck={c.StuckVehicles} trips={c.TripsCompleted} gridlock={c.GridlockFlag}");
        if (!sim.HasPeds)
        {
            return veh;
        }

        return veh + string.Create(
            CultureInfo.InvariantCulture,
            $" | peds={c.PedActive} residents={sim.ResidentCount} spawned={c.PedSpawned} ped_trips={c.PedTripsCompleted} no_path={c.PedNoPath} " +
            $"path_queries={c.PedPathQueries} alloc_failed={c.PedAllocFailed} ped_stuck={c.PedStuckEvents} net_resets={c.PedNetResets} " +
            $"cross_releases={c.PedCrossReleases} cross_wait_mean_s={(c.PedCrossReleases > 0 ? c.PedCrossWaitTicksTotal / 20.0 / c.PedCrossReleases : 0):F2} " +
            $"car_cross_delay_s={c.CarCrossDelayTicks / 20.0:F1} grants_revoked_ped={c.GrantsRevokedPed} crossing_closed_share={(c.CrossingObservedTicks > 0 ? (double)c.CrossingClosedTicks / c.CrossingObservedTicks : 0):F3}");
    }
}
