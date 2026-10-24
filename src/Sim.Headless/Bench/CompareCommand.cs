using System;
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

        output.WriteLine(slow || alloc ? "FAIL" : "OK");
        return slow || alloc ? ExitRegression : ExitOk;
    }

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
