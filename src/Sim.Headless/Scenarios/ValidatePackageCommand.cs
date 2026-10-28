using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sim.Scenarios.Mods;
using Sim.Scenarios.Mods.Gltf;

namespace Sim.Headless.Scenarios;

/// <summary><c>Sim.Headless validate-package &lt;каталог-пакета&gt;</c>: загрузчик, каталог контента, glTF и LOD (modding.md §4, §7).</summary>
public static class ValidatePackageCommand
{
    public const int ExitOk = 0;
    public const int ExitErrors = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "Usage: Sim.Headless validate-package <package-dir> [--budgets <file>] [--game]\n" +
        "\n" +
        "  <package-dir>     package directory (its name is the package id); dependencies are searched next to it\n" +
        "  --budgets <file>  render budgets (default: render-budgets.json next to <package-dir>, else the built-in stub)\n" +
        "  --game            the package ships with the game (ids 'base' and 'tc_*' are allowed)\n" +
        "\n" +
        "Prints every issue as 'package:file jsonpath: Code (detail)'. Exit code: 0 ok, 1 errors, 2 usage.";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? dir = null;
        string? budgetsPath = null;
        bool game = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--budgets":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("validate-package: option '--budgets' requires a value");
                        return ExitUsage;
                    }

                    budgetsPath = args[++i];
                    break;
                case "--game":
                    game = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || dir != null)
                    {
                        error.WriteLine($"validate-package: unexpected argument '{args[i]}'");
                        return ExitUsage;
                    }

                    dir = args[i];
                    break;
            }
        }

        if (dir == null)
        {
            error.WriteLine("validate-package: <package-dir> is required");
            return ExitUsage;
        }

        string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(full);
        if (!Directory.Exists(full) || parent == null)
        {
            error.WriteLine($"validate-package: directory '{dir}' not found");
            return ExitUsage;
        }

        var errors = new List<ModIssue>();
        var warnings = new List<ModIssue>();
        string id = Path.GetFileName(full);

        RenderBudgets budgets = RenderBudgets.Default;
        string defaultBudgets = Path.Combine(parent, RenderBudgets.FileName);
        string? path = budgetsPath ?? (File.Exists(defaultBudgets) ? defaultBudgets : null);
        if (path != null)
        {
            budgets = RenderBudgets.Load(path, errors) ?? budgets;
        }

        PackageSet set = PackageLoader.Load([new PackageSearchDir(parent, game)], [id]);
        errors.AddRange(set.Errors);
        warnings.AddRange(set.Warnings);
        int vehicles = 0;
        int roads = 0;
        if (set.Ok)
        {
            ContentCatalog catalog = ContentCatalog.Build(set, errors);
            if (errors.Count == 0)
            {
                VisualValidator.Validate(set, catalog, budgets, errors, warnings);
            }

            vehicles = catalog.Vehicles.Count;
            roads = catalog.Roads.Count;
        }

        foreach (ModIssue w in warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        foreach (ModIssue e in errors)
        {
            error.WriteLine($"error: {e}");
        }

        if (errors.Count > 0)
        {
            error.WriteLine($"validate-package: {errors.Count.ToString(CultureInfo.InvariantCulture)} error(s) in '{id}'");
            return ExitErrors;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ok: {id} ({set.Packages.Count} package(s), {vehicles} vehicle type(s), {roads} road type(s), {warnings.Count} warning(s))"));
        return ExitOk;
    }
}
