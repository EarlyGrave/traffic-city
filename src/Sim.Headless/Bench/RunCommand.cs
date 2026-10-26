using System.Globalization;
using System.IO;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Scenarios;
using Sim.Scenarios.Mods;

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
        "Usage: Sim.Headless run --scenario <name|path> --ticks N [--every K] [--threads N] [--seed S] [--scenarios-dir <dir>] [--packages-dir <dir>] [--json]\n" +
        "  prints vehicle and pedestrian counters every K ticks (default: only after the last one)\n";

    private static readonly string[] _allowed = { "--scenario", "--ticks", "--every", "--scenarios-dir", "--threads", "--signal-mode", "--signal-coordination", "--seed", "--packages-dir" };
    private static readonly string[] _flags = { "--json" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, _flags, out CliArgs a)
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
            sc = BenchCommand.ApplyOverrides(PackageDirs.Load(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir")), a), a);
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }

        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        bool json = a.Has("--json");
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
            if (!json)
            {
                output.WriteLine(Line(sim) + WindowTail(sim, in prev, prevHist, curHist, stopTmp));
            }

            prev = sim.Counters;
            (prevHist, curHist) = (curHist, prevHist);
        }

        if (json)
        {
            output.WriteLine(ToJson(sc, sim, ticks));
        }

        return ExitOk;
    }

    /// <summary>Итог прогона одним JSON-объектом (<c>--json</c>): сценарий, тик, хэш состояния, отпечаток контента (modding.md §5.3) и счётчики.</summary>
    private static string ToJson(LoadedScenario sc, Simulation sim, int ticks)
    {
        SimCounters c = sim.Counters;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("scenario", sc.Name);
            w.WriteNumber("ticks", ticks);
            w.WriteNumber("tick", sim.Tick);
            w.WriteString("state_hash", sim.ComputeStateHash().ToString("x16", CultureInfo.InvariantCulture));
            w.WriteString("content_fingerprint", ContentFingerprint.Hex(ContentFingerprint.For(sc)));
            w.WriteNumber("vehicles", c.ActiveVehicles);
            w.WriteNumber("stuck", c.StuckVehicles);
            w.WriteNumber("stuck_events", c.StuckEventsTotal);
            w.WriteNumber("trips", c.TripsCompleted);
            w.WriteBoolean("gridlock", c.GridlockFlag != 0);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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
