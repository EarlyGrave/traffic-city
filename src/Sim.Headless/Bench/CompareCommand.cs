using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>compare --result f --baseline perf/baseline.json [--tolerance 0.10]</c>: exit 1 при регрессии tick p95
/// сверх допуска или любых аллокациях в результате; 2 — ошибка ввода (в т.ч. нет baseline).
/// Без baseline: <c>compare --result f --budget-p95-ms 1.0</c> — exit 1 при alloc &gt; 0 или p95 &gt; бюджета.
/// </summary>
public static class CompareCommand
{
    public const int ExitOk = 0;
    public const int ExitRegression = 1;
    public const int ExitUsage = 2;

    public const string Usage = "Usage: Sim.Headless compare --result <file.json> --baseline <perf/baseline.json> [--tolerance 0.10]\n       Sim.Headless compare --result <file.json> --budget-p95-ms <ms>\n";

    private static readonly string[] _allowed = { "--result", "--baseline", "--tolerance", "--budget-p95-ms" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, out CliArgs a)
            || !a.TryGetDouble("--tolerance", 0, 10, out double tol, 0.10)
            || !a.TryGetDouble("--budget-p95-ms", 0, 1e6, out double budget, -1))
        {
            error.WriteLine($"compare: {a.Error}");
            error.Write(Usage);
            return ExitUsage;
        }

        string? resultPath = a.Get("--result");
        string? basePath = a.Get("--baseline");
        if (resultPath == null || (basePath == null && budget < 0))
        {
            error.WriteLine("compare: --result and (--baseline or --budget-p95-ms) are required");
            error.Write(Usage);
            return ExitUsage;
        }

        if (!File.Exists(resultPath))
        {
            error.WriteLine($"compare: result file not found: {resultPath}");
            return ExitUsage;
        }

        if (basePath == null)
        {
            return RunBudget(resultPath, budget, output, error);
        }

        if (!File.Exists(basePath))
        {
            error.WriteLine(
                $"compare: baseline not found: {basePath}. Create it with: Sim.Headless update-baseline --from {resultPath}");
            return ExitUsage;
        }

        if (!TryRead(resultPath, out double rP95, out double rAlloc, out string rScenario, out string rErr))
        {
            error.WriteLine($"compare: {rErr}");
            return ExitUsage;
        }

        if (!TryRead(basePath, out double bP95, out double bAlloc, out string bScenario, out string bErr))
        {
            error.WriteLine($"compare: {bErr}");
            return ExitUsage;
        }

        int rThreads = ReadThreads(resultPath);
        int bThreads = ReadThreads(basePath);
        if (rThreads != bThreads)
        {
            error.WriteLine($"compare: refusing to compare runs with different threads (baseline {bThreads}, result {rThreads})");
            return ExitUsage;
        }

        string rView = ReadView(resultPath);
        string bView = ReadView(basePath);
        if (rView != bView)
        {
            error.WriteLine($"compare: refusing to compare runs with different snapshot views (baseline '{bView}', result '{rView}')");
            return ExitUsage;
        }

        RampInfo rRamp = ReadRamp(resultPath);
        RampInfo bRamp = ReadRamp(basePath);
        bool rampMismatch = false;
        var notes = new List<string>();
        EvaluateRamp(rRamp, bRamp, notes, out rampMismatch);

        double limit = bP95 * (1 + tol);
        bool slow = rP95 > limit;
        bool alloc = rAlloc > 0;
        double delta = bP95 > 0 ? ((rP95 / bP95) - 1) * 100 : 0;

        CultureInfo ci = CultureInfo.InvariantCulture;
        output.WriteLine("metric                    baseline       result       delta");
        output.WriteLine(string.Create(
            ci,
            $"tick_ms.p95          {bP95,12:0.#####} {rP95,12:0.#####} {delta,+9:0.0;-0.0}%   (limit {limit:0.#####}, tolerance {tol * 100:0.#}%){(slow ? "  REGRESSION" : string.Empty)}"));
        output.WriteLine(string.Create(
            ci,
            $"alloc_bytes_per_tick {bAlloc,12:0.###} {rAlloc,12:0.###}{(alloc ? "   ALLOCATIONS" : string.Empty)}"));
        if (bScenario != rScenario)
        {
            output.WriteLine($"warning: scenario differs (baseline '{bScenario}', result '{rScenario}')");
        }

        PrintInfo(resultPath, basePath, output);
        foreach (string n in notes)
        {
            output.WriteLine(n);
        }

        bool fail = slow || alloc || rampMismatch;
        output.WriteLine(fail ? "FAIL" : "OK");
        return fail ? ExitRegression : ExitOk;
    }

    /// <summary>Вид снапшота из JSON (поле <c>view</c>; старые файлы и нечитаемый JSON - <c>all</c>).</summary>
    internal static string ReadView(string path)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            return d.RootElement.TryGetProperty("view", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "all" : "all";
        }
        catch (JsonException)
        {
            return "all";
        }
    }

    /// <summary>Число потоков ядра из JSON (поле <c>threads</c>; старые файлы и нечитаемый JSON - 1).</summary>
    internal static int ReadThreads(string path)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            return d.RootElement.TryGetProperty("threads", out JsonElement t) && t.TryGetInt32(out int n) ? n : 1;
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    /// <summary>Сведения о рампе из JSON (поля необязательны: старые файлы их не содержат).</summary>
    internal readonly record struct RampInfo(bool? Complete, int? ActiveStart, int? ActiveEnd, int? Target);

    /// <summary>Максимальная относительная разница активных машин, выше которой compare предупреждает.</summary>
    internal const double ActiveWarnShare = 0.10;

    /// <summary>
    /// Разный <c>ramp_complete</c> у результата и baseline — FAIL: p95 при разгоне и в стационаре несопоставимы
    /// (ложная регрессия или ложный «ок»). Сильно разное число активных и отсутствие сведений — только предупреждение.
    /// </summary>
    internal static void EvaluateRamp(RampInfo result, RampInfo baseline, List<string> notes, out bool mismatch)
    {
        mismatch = false;
        if (result.Complete is null || baseline.Complete is null)
        {
            if (result.Complete == false || baseline.Complete == false)
            {
                notes.Add("warning: " + (result.Complete == false ? "result" : "baseline")
                    + " ramp_complete=false, the other side has no ramp info (old format): p95 may be incomparable");
            }
            else if (result.Complete is null != baseline.Complete is null)
            {
                notes.Add("warning: ramp info is missing on one side (old format baseline?): cannot verify that both runs are past the spawn ramp");
            }

            return;
        }

        if (result.Complete != baseline.Complete)
        {
            mismatch = true;
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"ramp_complete differs (baseline {baseline.Complete.ToString()!.ToLowerInvariant()}, result {result.Complete.ToString()!.ToLowerInvariant()}): p95 of a partially loaded and a fully loaded city are incomparable  RAMP MISMATCH"));
            return;
        }

        if (result.ActiveEnd is int ra && baseline.ActiveEnd is int ba && ba > 0
            && Math.Abs(ra - ba) > ActiveWarnShare * ba)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"warning: active vehicles differ by more than {ActiveWarnShare * 100:0}% (baseline {ba}, result {ra})"));
        }
    }

    private static RampInfo ReadRamp(string path)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            if (!d.RootElement.TryGetProperty("perf", out JsonElement perf))
            {
                return default;
            }

            bool? complete = perf.TryGetProperty("ramp_complete", out JsonElement c)
                && c.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? c.GetBoolean()
                : null;
            return new RampInfo(complete, Int(perf, "active_vehicles_start"), Int(perf, "active_vehicles"), Int(perf, "target_vehicles"));
        }
        catch (JsonException)
        {
            return default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>
    /// Справочно (не влияет на exit code - работа меняется намеренно при оптимизациях): <c>work_per_tick.*</c> и
    /// <c>perf.phase_ms.*.p50/p95</c>, которые есть в результате; baseline - если в нём тоже есть.
    /// </summary>
    internal static void PrintInfo(string resultPath, string basePath, TextWriter output)
    {
        Dictionary<string, double> res = ReadInfo(resultPath);
        if (res.Count == 0)
        {
            return;
        }

        Dictionary<string, double> bas = ReadInfo(basePath);
        CultureInfo ci = CultureInfo.InvariantCulture;
        output.WriteLine("info (not gated)          baseline       result       delta");
        foreach (KeyValuePair<string, double> kv in res)
        {
            if (bas.TryGetValue(kv.Key, out double b))
            {
                string d = b != 0 ? string.Create(ci, $"{((kv.Value / b) - 1) * 100,+8:0.0;-0.0}%") : "       -";
                output.WriteLine(string.Create(ci, $"{kv.Key,-34} {b,10:0.####} {kv.Value,12:0.####} {d}"));
            }
            else
            {
                output.WriteLine(string.Create(ci, $"{kv.Key,-34} {"-",10} {kv.Value,12:0.####}"));
            }
        }
    }

    private static Dictionary<string, double> ReadInfo(string path)
    {
        var map = new Dictionary<string, double>();
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            if (d.RootElement.TryGetProperty("work_per_tick", out JsonElement w) && w.ValueKind == JsonValueKind.Object)
            {
                AddNumbers(map, "work.", w);
            }

            if (d.RootElement.TryGetProperty("perf", out JsonElement perf))
            {
                if (perf.TryGetProperty("work_per_tick", out JsonElement w2) && w2.ValueKind == JsonValueKind.Object)
                {
                    AddNumbers(map, "work.", w2);
                }

                if (perf.TryGetProperty("phase_ms", out JsonElement ph) && ph.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in ph.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Object)
                        {
                            AddNumbers(map, "phase_ms." + p.Name + ".", p.Value, "p50", "p95");
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return map;
    }

    private static void AddNumbers(Dictionary<string, double> map, string prefix, JsonElement obj, params string[] only)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double v)
                && (only.Length == 0 || Array.IndexOf(only, p.Name) >= 0))
            {
                map[prefix + p.Name] = v;
            }
        }
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : null;

    private static int RunBudget(string resultPath, double budget, TextWriter output, TextWriter error)
    {
        if (!TryRead(resultPath, out double p95, out double alloc, out _, out string err))
        {
            error.WriteLine($"compare: {err}");
            return ExitUsage;
        }

        bool slow = p95 > budget;
        bool hasAlloc = alloc > 0;
        CultureInfo ci = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(ci, $"tick_ms.p95 {p95:0.#####} ms (budget {budget:0.#####} ms){(slow ? "  OVER BUDGET" : string.Empty)}"));
        output.WriteLine(string.Create(ci, $"alloc_bytes_per_tick {alloc:0.###}{(hasAlloc ? "   ALLOCATIONS" : string.Empty)}"));
        output.WriteLine(slow || hasAlloc ? "FAIL" : "OK");
        return slow || hasAlloc ? ExitRegression : ExitOk;
    }

    private static bool TryRead(string path, out double p95, out double alloc, out string scenario, out string err)
    {
        p95 = 0;
        alloc = 0;
        err = string.Empty;
        scenario = string.Empty;
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = d.RootElement;
            if (root.TryGetProperty("scenario", out JsonElement s) && s.ValueKind == JsonValueKind.String)
            {
                scenario = s.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("perf", out JsonElement perf)
                && perf.TryGetProperty("tick_ms", out JsonElement t)
                && t.TryGetProperty("p95", out JsonElement p)
                && p.TryGetDouble(out p95)
                && perf.TryGetProperty("alloc_bytes_per_tick", out JsonElement al)
                && al.TryGetDouble(out alloc))
            {
                return true;
            }

            err = $"{path}: missing perf.tick_ms.p95 or perf.alloc_bytes_per_tick";
        }
        catch (JsonException e)
        {
            err = $"{path}: invalid JSON ({e.Message})";
        }
        catch (InvalidOperationException e)
        {
            err = $"{path}: unexpected JSON shape ({e.Message})";
        }

        return false;
    }
}
