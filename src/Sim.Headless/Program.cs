using System;
using Sim.Headless.Bench;
using Sim.Headless.Golden;
using Sim.Headless.Render;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

namespace Sim.Headless;

public static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;

    private const string Usage =
        "Usage: Sim.Headless <command> [options]\n" +
        "\n" +
        "Commands:\n" +
        "  run              run a scenario and print counters (--scenario <name|path> --ticks N [--every K]; see run --help)\n" +
        "  ready-city       prepare a new-game city (progression.md 7.3) and run it by days (see ready-city --help)\n" +
        "  bench            run a scenario and write perf JSON\n" +
        "  hash             print the state hash every K ticks (--scenario <name|path> --ticks N [--every K]; see hash --help)\n" +
        "  save             run a scenario and write a save container (--scenario <name|path> --ticks N --out <file>; see save --help)\n" +
        "  load             load a save and continue (--in <file> [--ticks N] [--every K]; see load --help)\n" +
        "  replay           re-simulate a command log and check its hashes, or record one (--log <file> [--save <file>] [--check]; see replay --help)\n" +
        "  bugreport        pack a save and its command log into one file (--save <file> --log <file> --out <file>)\n" +
        "  golden           write cross-language golden files for the Rust pilot (--out <dir>; see golden --help)\n" +
        "  compare          compare a result with the baseline\n" +
        "  update-baseline  replace perf/baseline.json with a result\n" +
        "  render-compare   gate client render counters (godot-shot.sh stats) against budgets and the client baseline\n" +
        "  render-update-baseline  replace the client render baseline with fresh stats\n" +
        "  render           render a scenario to mp4 (--scenario <name|path> --seconds <s> --out <file.mp4>; see render --help)\n" +
        "  validate         load a scenario and print a summary (--scenario <name|path> [--scenarios-dir <dir>])\n" +
        "  validate-package check a mod package: loader, content, glTF/GLB and LODs (<package-dir> [--budgets <file>] [--game])\n" +
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
            case "validate-package":
                return HasHelp(args) ? Help(ValidatePackageCommand.Usage) : ValidatePackageCommand.Run(args, Console.Out, Console.Error);
            case "bench":
                return HasHelp(args) ? Help(BenchCommand.Usage) : BenchCommand.Run(args, Console.Out, Console.Error);
            case "hash":
                return HasHelp(args) ? Help(HashCommand.Usage) : HashCommand.Run(args, Console.Out, Console.Error);
            case "save":
                return HasHelp(args) ? Help(SaveCommands.SaveUsage) : SaveCommands.RunSave(args, Console.Out, Console.Error);
            case "load":
                return HasHelp(args) ? Help(SaveCommands.LoadUsage) : SaveCommands.RunLoad(args, Console.Out, Console.Error);
            case "replay":
                return HasHelp(args) ? Help(ReplayCommands.ReplayUsage) : ReplayCommands.RunReplay(args, Console.Out, Console.Error);
            case "bugreport":
                return HasHelp(args) ? Help(ReplayCommands.BugReportUsage) : ReplayCommands.RunBugReport(args, Console.Out, Console.Error);
            case "compare":
                return HasHelp(args) ? Help(CompareCommand.Usage) : CompareCommand.Run(args, Console.Out, Console.Error);
            case "update-baseline":
                return HasHelp(args)
                    ? Help(UpdateBaselineCommand.Usage)
                    : UpdateBaselineCommand.Run(args, Console.Out, Console.Error);
            case "golden":
                return HasHelp(args) ? Help(GoldenCommand.Usage) : GoldenCommand.Run(args, Console.Out, Console.Error);
            case "render-compare":
                return HasHelp(args) ? Help(RenderCompare.Usage) : RenderCompare.RunCompare(args, Console.Out, Console.Error);
            case "render-update-baseline":
                return HasHelp(args) ? Help(RenderCompare.UpdateUsage) : RenderCompare.RunUpdate(args, Console.Out, Console.Error);
            case "ready-city":
                return HasHelp(args) ? Help(ReadyCityCommand.Usage) : ReadyCityCommand.Run(args, Console.Out, Console.Error);
            case "run":
                return HasHelp(args) ? Help(RunCommand.Usage) : RunCommand.Run(args, Console.Out, Console.Error);
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
