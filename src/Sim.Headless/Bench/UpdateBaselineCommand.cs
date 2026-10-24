using System.IO;
using System.Text.Json;

namespace Sim.Headless.Bench;

/// <summary><c>update-baseline --from result.json [--baseline perf/baseline.json]</c>: единственный легальный способ менять baseline.</summary>
public static class UpdateBaselineCommand
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;

    public const string Usage = "Usage: Sim.Headless update-baseline --from <result.json> [--baseline perf/baseline.json]\n";

    private static readonly string[] _allowed = { "--from", "--baseline" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, out CliArgs a))
        {
            error.WriteLine($"update-baseline: {a.Error}");
            error.Write(Usage);
            return ExitUsage;
        }

        string? from = a.Get("--from");
        string target = a.Get("--baseline") ?? "perf/baseline.json";
        if (from == null)
        {
            error.WriteLine("update-baseline: --from is required");
            error.Write(Usage);
            return ExitUsage;
        }

        if (!File.Exists(from))
        {
            error.WriteLine($"update-baseline: result file not found: {from}");
            return ExitUsage;
        }

        string text = File.ReadAllText(from);
        try
        {
            using JsonDocument d = JsonDocument.Parse(text);
            if (!d.RootElement.TryGetProperty("perf", out JsonElement perf) || !perf.TryGetProperty("tick_ms", out _))
            {
                error.WriteLine($"update-baseline: {from} is not a bench result (no perf.tick_ms)");
                return ExitUsage;
            }
        }
        catch (JsonException e)
        {
            error.WriteLine($"update-baseline: {from}: invalid JSON ({e.Message})");
            return ExitUsage;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(target, text);
        output.WriteLine($"baseline updated: {target} <- {from}");
        return ExitOk;
    }
}
