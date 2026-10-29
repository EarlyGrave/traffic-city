using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Scenarios;
using Sim.Scenarios;
using Sim.Scenarios.Game;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>ready-city --scenario game-city [--seed S] [--threads N] [--days D] [--exec micro|aggregate] [--scenarios-dir d]</c> (progression.md §7.3, §7.4):
/// готовит «готовый город» (<see cref="ReadyCity: микро до 06:00 суток 1), печатает состояние на старте, затем идёт ещё <c>D</c> суток
/// (<c>--exec aggregate</c> - с границы суток 1 агрегатом) и печатает строку на конец каждых суток: население, казна, тренд, банкротство, веха, показатели ворот,
/// был ли <c>GridlockFlag</c> (опрос каждые 20 тиков), <c>res_stuck</c>. В конце - тики взятия вех от старта игры.
/// </summary>
public static class ReadyCityCommand
{
    public const string Usage =
        "Usage: Sim.Headless ready-city --scenario <name|path> [--days D] [--exec micro|aggregate] [--threads N] [--seed S] [--scenarios-dir <dir>]\n" +
        "  prepares a new-game city (micro until 06:00 of day 1), prints its state, then runs D more game days (default 0)\n" +
        "  and prints a line per day end: population, treasury, trend, insolvency, milestone, KPI, gridlock seen, res_stuck; --exec aggregate switches at the next day boundary\n";

    private static readonly string[] _allowed = { "--scenario", "--days", "--exec", "--threads", "--seed", "--scenarios-dir" };

    private const int PollTicks = 20;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (!CliArgs.TryParse(args, _allowed, out CliArgs a)
            || !a.TryGetInt("--days", 0, out int days, 0)
            || !a.TryGetInt("--threads", 1, out int threads, 1))
        {
            error.WriteLine($"ready-city: {a.Error}");
            error.Write(Usage);
            return RunCommand.ExitUsage;
        }

        string exec = a.Get("--exec") ?? "micro";
        string? scenario = a.Get("--scenario");
        if (scenario == null || exec is not ("micro" or "aggregate"))
        {
            error.WriteLine("ready-city: --scenario is required, --exec must be micro or aggregate");
            error.Write(Usage);
            return RunCommand.ExitUsage;
        }

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

        var wall = Stopwatch.StartNew();
        CreateStatus st;
        Simulation? sim;
        try
        {
            st = ReadyCity.TryPrepare(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads }, ReadyCityOptions.Default, out sim);
        }
        catch (ArgumentException e)
        {
            error.WriteLine($"ready-city: {e.Message}");
            return RunCommand.ExitUsage;
        }

        if (st != CreateStatus.Ok || sim == null)
        {
            error.WriteLine($"ready-city: create failed: {st}");
            return RunCommand.ExitUsage;
        }

        using (sim)
        {
            double prepS = wall.Elapsed.TotalSeconds;
            long start = sim.Tick;
            int dayTicks = sim.CityDayTicks;
            output.WriteLine(Fmt($"ready {sc.Name} seed={sc.Desc.Seed}: tick={start} prepare_wall_s={prepS:F1}"));
            Line(output, sim, start, 0, false);
            bool gridlockSeen = false;
            bool aggregateOn = false;
            for (int d = 1; d <= days; d++)
            {
                long dayEnd = ((sim.Tick / dayTicks) + 1) * dayTicks;
                while (sim.Tick < dayEnd)
                {
                    int chunk = (int)Math.Min(PollTicks, dayEnd - sim.Tick);
                    if (exec == "aggregate" && !aggregateOn && sim.Tick + chunk == dayEnd)
                    {
                        // Переключение на последнем тике суток (как в `run --days`): следующие сутки идут агрегатом.
                        if (chunk > 1)
                        {
                            sim.Step(chunk - 1);
                            gridlockSeen |= sim.Counters.GridlockFlag != 0;
                        }

                        sim.Enqueue(Command.MakeSetTripExecution(TripExecution.Aggregate));
                        sim.Step(1);
                        aggregateOn = true;
                    }
                    else
                    {
                        sim.Step(chunk);
                    }

                    gridlockSeen |= sim.Counters.GridlockFlag != 0;
                }

                Line(output, sim, start, d, gridlockSeen);
            }

            if (sim.HasProgression && sim.GetProgression(out ProgressionInfo p))
            {
                var sb = new System.Text.StringBuilder("milestones (ticks since start):");
                for (int i = 0; i < p.MilestoneCount; i++)
                {
                    sim.GetMilestone(i, out MilestoneInfo mi);
                    sb.Append(Fmt($" m{i + 1}={(mi.ReachedTick < 0 ? "-" : (mi.ReachedTick - start).ToString())}"));
                }

                output.WriteLine(sb.ToString());
            }

            output.WriteLine(Fmt($"done: wall_s={wall.Elapsed.TotalSeconds:F1} tick={sim.Tick} gridlock_seen={(gridlockSeen ? 1 : 0)} res_stuck={sim.Counters.ResStuck} stuck_events={sim.Counters.StuckEventsTotal}"));
        }

        return RunCommand.ExitOk;
    }

    private static void Line(TextWriter o, Simulation sim, long start, int day, bool gridlockSeen)
    {
        sim.GetCityStats(out CityStats s);
        sim.GetCityBudget(out CityBudgetInfo b);
        sim.GetCityKpi(out CityKpiInfo k);
        sim.GetProgression(out ProgressionInfo p);
        o.WriteLine(Fmt(
            $"+{day}d t={sim.Tick - start} pop={s.Population} seeking={s.Seeking} vac={s.Vacancies} balance={b.Balance} income_ewma={b.IncomeEwma} expense_ewma={b.ExpenseEwma} maint_road={b.MaintRoadDay} maint_bus={b.MaintBusDay} trend={b.IncomeEwma - b.ExpenseEwma} " +
            $"insolvent={b.Insolvent} reached={p.Reached}/{p.MilestoneCount} next_pop={p.NextPopulation} kpi[legs={k.LegsAll} travel_min={k.TravelMinX10 / 10.0:F1} idle%={k.IdlePermille / 10.0:F1} bus%={k.BusSharePermille / 10.0:F1} sat%={k.SatisfactionPermille / 10.0:F1}] " +
            $"gridlock_seen={(gridlockSeen ? 1 : 0)} res_stuck={sim.Counters.ResStuck} active={sim.Counters.ActiveVehicles} stuck_now={sim.Counters.StuckVehicles}"));
    }

    private static string Fmt(string s) => s;   // культура задаётся в Run (Invariant)
}
