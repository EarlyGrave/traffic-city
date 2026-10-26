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
        "  prints vehicle and pedestrian counters every K ticks (default: only after the last one)\n" +
        "Usage: Sim.Headless run --scenario <name|path> (--days N | --years N) [--exec micro|aggregate] [--calibrate-days C] [--micro-sample-every-days K] [--save-every-days S] [--out <file.json>] [--threads N] [--seed S]\n" +
        "  city run by game days (residents scenarios): aggregate executes legs without agents after C micro days (default 1), every K days one micro day re-calibrates;\n" +
        "  prints a line per 30 days, writes the 'city' JSON block (series, day_hashes) to --out, saves <out>-day<N>.tcsave every S days\n";

    private static readonly string[] _allowed = { "--scenario", "--ticks", "--every", "--scenarios-dir", "--threads", "--signal-mode", "--signal-coordination", "--seed", "--days", "--years", "--exec", "--calibrate-days", "--micro-sample-every-days", "--save-every-days", "--out" };

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

        if (a.Has("--days") || a.Has("--years"))
        {
            return CityRunCommand.Run(a, output, error, Usage);
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
        long[] prevHist = new long[Simulation.PedWaitBuckets];
        long[] curHist = new long[Simulation.PedWaitBuckets];
        long[] stopTmp = new long[Simulation.PedWaitBuckets];
        SimCounters prev = sim.Counters;
        while (done < ticks)
        {
            int n = System.Math.Min(step, ticks - done);
            sim.Step(n);
            done += n;
            output.WriteLine(Line(sim) + WindowTail(sim, in prev, prevHist, curHist, stopTmp));
            prev = sim.Counters;
            (prevHist, curHist) = (curHist, prevHist);
        }

        return ExitOk;
    }

    /// <summary>Хвост строки: метрики окна с прошлой строки (поездки, ожидание у переходов: среднее, p95 и максимум по корзинам 1 с). Только с пешеходами.</summary>
    private static string WindowTail(Simulation sim, in SimCounters prev, long[] prevHist, long[] curHist, long[] stopTmp)
    {
        SimCounters c = sim.Counters;
        string win = string.Create(CultureInfo.InvariantCulture, $" | win_trips={c.TripsCompleted - prev.TripsCompleted} win_stuck_events={c.StuckEventsTotal - prev.StuckEventsTotal}");
        if (!sim.HasPeds)
        {
            return win;
        }

        sim.CopyPedWaitHistograms(curHist, stopTmp);
        long total = c.PedCrossReleases - prev.PedCrossReleases;
        long need = ((total * 95) + 99) / 100;
        long acc = 0;
        int p95 = -1;
        int max = 0;
        for (int i = 0; i < curHist.Length; i++)
        {
            long d = curHist[i] - prevHist[i];
            acc += d;
            if (p95 < 0 && acc >= need)
            {
                p95 = i;
            }

            if (d > 0)
            {
                max = i;
            }
        }

        double mean = total > 0 ? (c.PedCrossWaitTicksTotal - prev.PedCrossWaitTicksTotal) / 20.0 / total : 0;
        return win + string.Create(CultureInfo.InvariantCulture, $" win_cross_wait_mean_s={mean:F2} win_cross_wait_p95_s={p95} win_cross_wait_max_s={max}");
    }

    private static string Line(Simulation sim)
    {
        SimCounters c = sim.Counters;
        string veh = string.Create(
            CultureInfo.InvariantCulture,
            $"tick={sim.Tick} vehicles={c.ActiveVehicles} stuck={c.StuckVehicles} stuck_events={c.StuckEventsTotal} trips={c.TripsCompleted} gridlock={c.GridlockFlag}");
        if (sim.HasCity)
        {
            veh += CityLine(sim, c);
        }

        if (!sim.HasPeds)
        {
            return veh;
        }

        return veh + string.Create(
            CultureInfo.InvariantCulture,
            $" | peds={c.PedActive} residents={sim.ResidentCount} spawned={c.PedSpawned} ped_trips={c.PedTripsCompleted} no_path={c.PedNoPath} " +
            $"path_queries={c.PedPathQueries} alloc_failed={c.PedAllocFailed} ped_stuck={c.PedStuckEvents} net_resets={c.PedNetResets} " +
            $"cross_releases={c.PedCrossReleases} cross_wait_mean_s={(c.PedCrossReleases > 0 ? c.PedCrossWaitTicksTotal / 20.0 / c.PedCrossReleases : 0):F2} " +
            $"car_cross_delay_s_per_veh_h={(c.VehicleTicksTotal > 0 ? c.CarCrossDelayTicks / (double)c.VehicleTicksTotal * 3600.0 : 0):F1} grants_revoked_ped={c.GrantsRevokedPed} crossing_closed_share={(c.CrossingObservedTicks > 0 ? (double)c.CrossingClosedTicks / c.CrossingObservedTicks : 0):F3} " +
            $"boardings={c.PedStopBoardings} alighted={c.BusPassengersAlighted} mode_fallback={c.PedModeFallback} " +
            $"stop_wait_mean_s={(c.PedStopBoardings > 0 ? c.PedStopWaitTicksTotal * 0.05 / c.PedStopBoardings : 0.0):F1}");
    }

    /// <summary>Сводка города (режим residents): население, занятость, деньги (Σ = 0), туры и дедлайны.</summary>
    private static string CityLine(Simulation sim, SimCounters c)
    {
        if (!sim.GetCityStats(out CityStats s))
        {
            return string.Empty;
        }

        long sum = s.BalanceResidents + s.BalanceFirms + s.BalanceExternal + s.BalanceCity;
        return string.Create(
            CultureInfo.InvariantCulture,
            $" | city day={s.Day} pop={s.Population} labour={s.Labour} employed={s.Employed} seeking={s.Seeking} vacancies={s.Vacancies} free_housing={s.FreeHousing} " +
            $"bld_r={s.BuildingsR} bld_c={s.BuildingsC} bld_i={s.BuildingsI} constructing={s.Constructing} abandoned={s.Abandoned} " +
            $"bal_res={s.BalanceResidents} bal_firms={s.BalanceFirms} bal_ext={s.BalanceExternal} bal_city={s.BalanceCity} bal_sum={sum} " +
            $"tours={c.ResToursPlanned} dropped={c.ResToursDropped} cancelled={c.ResToursCancelled} legs_done={c.ResLegsDone} visits={c.ResVisits} unserved={c.ResVisitsUnserved} " +
            $"imm={c.Immigrants} emig={c.Emigrants} reloc={c.ResRelocations} hired={c.ResHired} fired={c.ResFired} built={c.BuildingsBuilt} res_stuck={c.ResStuck} leg_overdue={c.ResLegOverdue}");
    }
}
