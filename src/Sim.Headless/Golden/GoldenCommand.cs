using System;
using System.IO;
using System.Text;

namespace Sim.Headless.Golden;

/// <summary><c>Sim.Headless golden --out &lt;каталог&gt; [--root &lt;корень репозитория&gt;]</c>: пишет <c>util.json</c> и файл на каждый сценарий (rust-pilot.md §5).</summary>
public static class GoldenCommand
{
    public const string Usage =
        "Usage: Sim.Headless golden --out <dir> [--root <repo root>]\n" +
        "  Writes util.json and one <scenario>.json per scenario (scenarios/demo-grid.json and <dir>/scenarios/*.json).\n" +
        "  Fixtures in <dir>/scenarios are inputs and are never rewritten. Output is byte-deterministic.";

    private const int ExitOk = 0;
    private const int ExitError = 2;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? outDir = null;
        string? root = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--root" when i + 1 < args.Length:
                    root = args[++i];
                    break;
                default:
                    error.WriteLine($"golden: unknown or incomplete option '{args[i]}'");
                    return ExitError;
            }
        }

        if (outDir == null)
        {
            error.WriteLine("golden: --out <dir> is required");
            return ExitError;
        }

        outDir = Path.GetFullPath(outDir);
        root ??= FindRoot(outDir) ?? FindRoot(Directory.GetCurrentDirectory());
        if (root == null)
        {
            error.WriteLine("golden: repository root (traffic-city.sln) not found; pass --root");
            return ExitError;
        }

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(outDir);
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(outDir, "util.json"), RustPilotGolden.UtilJson(), utf8);
        output.WriteLine("util.json");
        foreach (string path in RustPilotGolden.ScenarioPaths(root, outDir))
        {
            string file = RustPilotGolden.FileNameFor(path);
            File.WriteAllText(Path.Combine(outDir, file), RustPilotGolden.ScenarioJson(root, path), utf8);
            output.WriteLine(file);
        }

        return ExitOk;
    }

    private static string? FindRoot(string start)
    {
        for (DirectoryInfo? d = new(start); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "traffic-city.sln")))
            {
                return d.FullName;
            }
        }

        return null;
    }
}
