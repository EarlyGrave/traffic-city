using System;
using Sim.Headless.Bench;
using Sim.Headless.Render;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

namespace Sim.Headless;

public static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitNotImplemented = 3;

    private const string Usage =
        "Usage: Sim.Headless <command> [options]\n" +
        "\n" +
        "Commands:\n" +
        "  run              run a scenario\n" +
        "  bench            run a scenario and write perf JSON\n" +
        "  compare          compare a result with the baseline\n" +
        "  update-baseline  replace perf/baseline.json with a result\n" +
        "  render           render a scenario to mp4 (--scenario <name|path> --seconds <s> --out <file.mp4>; see render --help)\n" +
        "  validate         load a scenario and print a summary (--scenario <name|path> [--scenarios-dir <dir>])\n" +
        "\n" +
        "Options:\n" +
        "  --help, -h       show this help\n";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }

        string command = args[0];
        switch (command)
        {
            case "--help":
            case "-h":
            case "help":
                Console.Out.WriteLine(Usage);
                return ExitOk;
            case "render":
                if (HasHelp(args))
                {
                    Console.Out.WriteLine(RenderCommand.Usage);
                    return ExitOk;
                }

                return RenderCommand.Run(args, Console.Out, Console.Error);
            case "validate":
                if (HasHelp(args))
                {
                    Console.Out.WriteLine(Usage);
                    return ExitOk;
                }

                return ValidateCommand.Run(args, Console.Out, Console.Error);
            case "bench":
                return HasHelp(args) ? Help(BenchCommand.Usage) : BenchCommand.Run(args, Console.Out, Console.Error);
            case "compare":
                return HasHelp(args) ? Help(CompareCommand.Usage) : CompareCommand.Run(args, Console.Out, Console.Error);
            case "update-baseline":
                return HasHelp(args)
                    ? Help(UpdateBaselineCommand.Usage)
                    : UpdateBaselineCommand.Run(args, Console.Out, Console.Error);
            case "run":
                if (HasHelp(args))
                {
                    Console.Out.WriteLine(Usage);
                    return ExitOk;
                }

                Console.Error.WriteLine($"{command}: not implemented");
                return ExitNotImplemented;
            default:
                Console.Error.WriteLine($"Unknown command '{command}'.");
                Console.Error.WriteLine(Usage);
                return ExitUsage;
        }
    }

    private static int Help(string text)
    {
        Console.Out.WriteLine(text);
        return ExitOk;
    }

    private static bool HasHelp(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                return true;
            }
        }

        return false;
    }
}
