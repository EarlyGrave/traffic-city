using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

namespace Sim.Headless.Bench;

/// <summary>
/// Долгий прогон города (zoning-demand.md §9.3): <c>run --scenario s --days N | --years N [--exec micro|aggregate] [--calibrate-days C] [--micro-sample-every-days K]
/// [--save-every-days S] [--out file.json]</c>. Идёт по суткам. В режиме <c>aggregate</c> первые <c>C</c> суток (по умолчанию 1) исполняются агентами (калибровка <c>K</c>), затем команда
/// <c>SetTripExecution</c> переводит ноги на агрегат; каждые <c>K</c> суток одни сутки снова идут агентами (контрольная выборка, при возврате <c>K</c> калибруется заново).
/// В JSON - итоги и ряд по 30 суток (население, занятость, здания, туры, способы, стоимость, спрос R/C/I, потоки денег на жителя, Σ счетов, <c>res_stuck</c>) и хэш на конец каждых суток (<c>day_hashes</c>).
/// </summary>
public static class CityRunCommand
{
    public const int SeriesEveryDays = 30;
    public const int DaysPerYear = 365;

    /// <summary>Режим исполнения ног на сутки <paramref name="day"/>: micro на калибровку и контрольные сутки, иначе aggregate (<paramref name="aggregate"/> = false - всегда micro).</summary>
    public static TripExecution ModeFor(int day, bool aggregate, int calibrateDays, int sampleEvery)
    {
        if (!aggregate || day < calibrateDays)
        {
            return TripExecution.Agents;
        }

        return sampleEvery > 0 && day % sampleEvery == 0 ? TripExecution.Agents : TripExecution.Aggregate;
    }

    public static int Run(CliArgs a, TextWriter output, TextWriter error, string usage)
    {
        if (!a.TryGetInt("--days", 1, out int days, 0) || !a.TryGetInt("--years", 1, out int years, 0)
            || !a.TryGetInt("--calibrate-days", 1, out int calibrateDays, 1)
            || !a.TryGetInt("--micro-sample-every-days", 0, out int sampleEvery, 0)
            || !a.TryGetInt("--save-every-days", 0, out int saveEvery, 0)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"run: {a.Error}");
            error.Write(usage);
            return RunCommand.ExitUsage;
        }

        if (days > 0 && years > 0)
        {
            error.WriteLine("run: --days and --years are mutually exclusive");
            return RunCommand.ExitUsage;
        }

        string exec = a.Get("--exec") ?? "micro";
        if (exec is not ("micro" or "aggregate"))
        {
            error.WriteLine($"run: --exec must be micro or aggregate, got '{exec}'");
            return RunCommand.ExitUsage;
        }

        string? scenario = a.Get("--scenario");
        if (scenario == null)
        {
            error.WriteLine("run: --scenario is required");
            error.Write(usage);
            return RunCommand.ExitUsage;
        }

        int totalDays = years > 0 ? years * DaysPerYear : days;
        bool aggregate = exec == "aggregate";
        LoadedScenario sc;
        try
        {
            sc = BenchCommand.ApplyOverrides(ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir"))), a);
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return RunCommand.ExitUsage;
        }

        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
        if (!sim.HasCity)
        {
            error.WriteLine("run: --days/--years need a scenario with demand.model = residents");
            return RunCommand.ExitUsage;
        }

        string? outPath = a.Get("--out");
        int dayTicks = sim.CityDayTicks;
        var hashes = new List<ulong>(totalDays);
        var series = new List<Point>();
        var wall = Stopwatch.StartNew();
        TripExecution current = TripExecution.Agents;
        long microDays = 0;
        long aggregateDays = 0;
        CityStats first;
        sim.GetCityStats(out first);
        series.Add(Capture(sim, in first, 0));
        for (int day = 0; day < totalDays; day++)
        {
            TripExecution next = ModeFor(day + 1, aggregate, calibrateDays, sampleEvery);
            sim.Step(dayTicks - 1);
            if (next != current)
            {
                if (!sim.Enqueue(Command.MakeSetTripExecution(next)))
                {
                    error.WriteLine("run: command queue is full");
                    return RunCommand.ExitUsage;
                }
            }

            sim.Step(1);
            if (current == TripExecution.Agents)
            {
                microDays++;
            }
            else
            {
                aggregateDays++;
            }

            current = next;
            hashes.Add(sim.ComputeStateHash());
            int done = day + 1;
            if (done % SeriesEveryDays == 0 || done == totalDays || (totalDays <= 10 && series.Count < 11))
            {
                sim.GetCityStats(out CityStats s);
                Point p = Capture(sim, in s, wall.Elapsed.TotalSeconds);
                series.Add(p);
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"day={done} pop={p.Population} employed={p.Employed} seeking={p.Seeking} vacancies={p.Vacancies} bld={p.BuildingsR}/{p.BuildingsC}/{p.BuildingsI} money_sum={p.MoneySum} res_stuck={p.ResStuck} legs_done={sim.Counters.ResLegsDone} tours={sim.Counters.ResToursPlanned} visits={sim.Counters.ResVisits} exec={(current == TripExecution.Agents ? "micro" : "aggregate")} wall_s={p.WallS:F1}"));
            }

            if (saveEvery > 0 && done % saveEvery == 0 && outPath != null)
            {
                WriteSave(sim, outPath, done);
            }
        }

        wall.Stop();
        sim.GetCityStats(out CityStats last);
        Point end = Capture(sim, in last, wall.Elapsed.TotalSeconds);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"run {sc.Name}: {totalDays} days, exec={exec}, wall_s={wall.Elapsed.TotalSeconds:F1}, micro_days={microDays}, aggregate_days={aggregateDays}, jumped_ticks={sim.CityJumpedTicks}/{sim.Tick}, " +
            $"pop={end.Population}, money_sum={end.MoneySum}, res_stuck={end.ResStuck}, legs_done={sim.Counters.ResLegsDone}, tours={sim.Counters.ResToursPlanned}, visits={sim.Counters.ResVisits}, dropped={sim.Counters.ResToursDropped}"));
        if (outPath != null)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(outPath, ToJson(sc.Name, exec, totalDays, calibrateDays, sampleEvery, threads, wall.Elapsed.TotalSeconds, microDays, aggregateDays, sim, series, hashes, dayTicks));
        }

        return RunCommand.ExitOk;
    }

    private static void WriteSave(Simulation sim, string outPath, int day)
    {
        long size = sim.MeasureSave();
        byte[] buf = new byte[size];
        if (sim.Save(buf, out long written) != SaveStatus.Ok || written != size)
        {
            throw new InvalidOperationException("save failed");
        }

        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(outPath)}-day{day}.tcsave"), SaveFile.Pack(buf));
    }

    /// <summary>Точка ряда: срез города на конец суток.</summary>
    private struct Point
    {
        public long Day;
        public double WallS;
        public int Population, Labour, Employed, Seeking, Vacancies, FreeHousing;
        public int BuildingsR, BuildingsC, BuildingsI, Constructing, Active, Abandoned;
        public int DemandR, DemandC, DemandI;
        public int Tours, LegsCar, LegsBus, LegsWalk, CostCar, CostBus, CostWalk;
        public long CarCrossings;
        public double CarKm;
        public long MoneySum, ResStuck, Immigrants, Emigrants;
        public int Exec;
        public long[] Flows;
    }

    private static Point Capture(Simulation sim, in CityStats s, double wallS)
    {
        var p = new Point
        {
            Day = s.Day,
            WallS = wallS,
            Population = s.Population,
            Labour = s.Labour,
            Employed = s.Employed,
            Seeking = s.Seeking,
            Vacancies = s.Vacancies,
            FreeHousing = s.FreeHousing,
            BuildingsR = s.BuildingsR,
            BuildingsC = s.BuildingsC,
            BuildingsI = s.BuildingsI,
            Constructing = s.Constructing,
            Active = s.Active,
            Abandoned = s.Abandoned,
            DemandR = s.DemandR,
            DemandC = s.DemandC,
            DemandI = s.DemandI,
            Tours = s.ToursYesterday,
            LegsCar = s.LegsYesterdayCar,
            LegsBus = s.LegsYesterdayBus,
            LegsWalk = s.LegsYesterdayWalk,
            CostCar = s.AvgCostCarDs,
            CostBus = s.AvgCostBusDs,
            CostWalk = s.AvgCostWalkDs,
            CarCrossings = sim.CityCarCellCrossingsYesterday,
            MoneySum = s.BalanceResidents + s.BalanceFirms + s.BalanceExternal + s.BalanceCity,
            ResStuck = s.ResStuck,
            Immigrants = sim.Counters.Immigrants,
            Emigrants = sim.Counters.Emigrants,
            Exec = s.ExecMode,
            Flows = new long[Simulation.FlowKindCount],
        };
        p.CarKm = p.CarCrossings * sim.CityCellM / 1000.0;
        sim.CopyFlowTotals(p.Flows, FlowPeriod.Yesterday);
        return p;
    }

    private static string ToJson(string scenario, string exec, int days, int calibrateDays, int sampleEvery, int threads, double wallS, long microDays, long aggregateDays, Simulation sim, List<Point> series, List<ulong> hashes, int dayTicks)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("scenario", scenario);
            w.WriteString("exec", exec);
            w.WriteNumber("days", days);
            w.WriteNumber("day_ticks", dayTicks);
            w.WriteNumber("calibrate_days", calibrateDays);
            w.WriteNumber("micro_sample_every_days", sampleEvery);
            w.WriteNumber("threads", threads);
            w.WriteNumber("wall_s", Math.Round(wallS, 2));
            w.WriteNumber("ticks", sim.Tick);
            w.WriteNumber("jumped_ticks", sim.CityJumpedTicks);
            w.WriteNumber("micro_days", microDays);
            w.WriteNumber("aggregate_days", aggregateDays);
            w.WriteStartObject("city");
            Point last = series[^1];
            w.WriteNumber("population", last.Population);
            w.WriteNumber("employed", last.Employed);
            w.WriteNumber("seeking", last.Seeking);
            w.WriteNumber("vacancies", last.Vacancies);
            w.WriteNumber("money_sum", last.MoneySum);
            w.WriteNumber("res_stuck", last.ResStuck);
            w.WriteNumber("immigrants", last.Immigrants);
            w.WriteNumber("emigrants", last.Emigrants);
            w.WriteStartArray("series");
            foreach (Point p in series)
            {
                WritePoint(w, in p);
            }

            w.WriteEndArray();
            w.WriteStartArray("day_hashes");
            foreach (ulong h in hashes)
            {
                w.WriteStringValue(h.ToString("x16", CultureInfo.InvariantCulture));
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WritePoint(Utf8JsonWriter w, in Point p)
    {
        double pop = Math.Max(1, p.Population);
        long legs = Math.Max(1, (long)p.LegsCar + p.LegsBus + p.LegsWalk);
        w.WriteStartObject();
        w.WriteNumber("day", p.Day);
        w.WriteNumber("wall_s", Math.Round(p.WallS, 2));
        w.WriteString("exec", p.Exec == (int)TripExecution.Agents ? "micro" : "aggregate");
        w.WriteNumber("population", p.Population);
        w.WriteNumber("labour", p.Labour);
        w.WriteNumber("employed", p.Employed);
        w.WriteNumber("seeking", p.Seeking);
        w.WriteNumber("vacancies", p.Vacancies);
        w.WriteNumber("free_housing", p.FreeHousing);
        w.WriteStartObject("buildings");
        w.WriteNumber("residential", p.BuildingsR);
        w.WriteNumber("commercial", p.BuildingsC);
        w.WriteNumber("industrial", p.BuildingsI);
        w.WriteNumber("constructing", p.Constructing);
        w.WriteNumber("active", p.Active);
        w.WriteNumber("abandoned", p.Abandoned);
        w.WriteEndObject();
        w.WriteStartObject("demand");
        w.WriteNumber("r", p.DemandR);
        w.WriteNumber("c", p.DemandC);
        w.WriteNumber("i", p.DemandI);
        w.WriteEndObject();
        w.WriteNumber("tours_per_resident", Math.Round(p.Tours / pop, 4));
        w.WriteStartObject("mode_share");
        w.WriteNumber("car", Math.Round((double)p.LegsCar / legs, 4));
        w.WriteNumber("bus", Math.Round((double)p.LegsBus / legs, 4));
        w.WriteNumber("walk", Math.Round((double)p.LegsWalk / legs, 4));
        w.WriteEndObject();
        w.WriteNumber("legs_per_day", legs);
        w.WriteNumber("car_km_per_day", Math.Round(p.CarKm, 1));
        w.WriteStartObject("avg_cost_ds");
        w.WriteNumber("car", p.CostCar);
        w.WriteNumber("bus", p.CostBus);
        w.WriteNumber("walk", p.CostWalk);
        w.WriteEndObject();
        w.WriteStartArray("flows_per_resident_day");
        for (int i = 0; i < 13; i++)
        {
            w.WriteNumberValue(Math.Round(p.Flows[i] / pop, 4));
        }

        w.WriteEndArray();
        w.WriteNumber("money_sum", p.MoneySum);
        w.WriteNumber("res_stuck", p.ResStuck);
        w.WriteNumber("immigrants_total", p.Immigrants);
        w.WriteNumber("emigrants_total", p.Emigrants);
        w.WriteEndObject();
    }
}
