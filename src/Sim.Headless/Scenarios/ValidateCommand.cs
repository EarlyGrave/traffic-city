using System;
using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Scenarios;

/// <summary><c>Sim.Headless validate --scenario &lt;имя|путь&gt; [--scenarios-dir &lt;каталог&gt;]</c>.</summary>
public static class ValidateCommand
{
    public const int ExitOk = 0;
    public const int ExitError = 2;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? scenario = null;
        string? dir = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario":
                    scenario = NextValue(args, ref i);
                    break;
                case "--scenarios-dir":
                    dir = NextValue(args, ref i);
                    break;
                default:
                    error.WriteLine($"validate: unknown option '{args[i]}'");
                    return ExitError;
            }

            if (scenario == "" || dir == "")
            {
                error.WriteLine($"validate: option '{args[i]}' requires a value");
                return ExitError;
            }
        }

        if (scenario == null)
        {
            error.WriteLine("validate: --scenario <name|path> is required");
            return ExitError;
        }

        try
        {
            string path = ScenarioLocator.Resolve(scenario, dir);
            LoadedScenario s = ScenarioLoader.LoadInfo(path);
            WriteSummary(output, path, s);
            return ExitOk;
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitError;
        }
    }

    private static string NextValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return "";
        }

        i++;
        return args[i];
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static void WriteSummary(TextWriter w, string path, LoadedScenario s)
    {
        ScenarioDesc d = s.Desc;
        w.WriteLine($"scenario: {s.Name} ({path})");
        w.WriteLine($"  seed: {d.Seed}");
        w.WriteLine($"  duration: {N(d.DurationTicks)} ticks ({F(d.DurationTicks / (float)ScenarioLoader.TicksPerSecond)} s)");
        if (d.City.Enabled)
        {
            CityGenDesc c = d.City;
            w.WriteLine(
                $"  city: generated, {F(c.SizeX * c.Scale)} x {F(c.SizeZ * c.Scale)} m (size {F(c.SizeX)} x {F(c.SizeZ)}, scale {F(c.Scale)}), block {F(c.BlockSize)} m");
            w.WriteLine(
                $"        arterial every {c.ArterialEvery}, speeds {F(c.ArterialSpeed)}/{F(c.StreetSpeed)} m/s, irregular nodes {N(c.IrregularNodes)}, signals {c.Signals}, minor {c.MinorControl}");
        }
        else
        {
            w.WriteLine($"  network: explicit, {N(d.Network.Nodes.Length)} nodes, {N(d.Network.Segments.Length)} segments");
        }

        DemandDesc m = d.Demand;
        w.WriteLine($"  vehicles: {N(m.TargetVehicles)}, ramp {N(m.SpawnRampTicks)} ticks");
        for (int i = 0; i < s.TypeNames.Length; i++)
        {
            float wgt = m.TypeMix != null && i < m.TypeMix.Length ? m.TypeMix[i] : 0f;
            VehicleTypeDesc t = d.VehicleTypes[i];
            w.WriteLine(
                $"    type[{i}] {s.TypeNames[i]}: mix {F(wgt)}, {t.Category}, {F(t.Length)} m, max {F(t.MaxSpeed)} m/s");
        }

        w.WriteLine($"  demand: {m.Model}, zone {F(m.ZoneSize)} m, external {F(m.ExternalShare)}, min trip {F(m.MinTripLength)} m, od pairs {N(m.OdPairs?.Length ?? 0)}");
        TransitDesc t2 = d.Transit;
        w.WriteLine($"  transit: {N(t2.Routes)} routes (+{N(t2.RoutesExplicit?.Length ?? 0)} explicit), headway {N(t2.HeadwayTicks)} ticks, bus type {t2.BusType}");
        SimCapacities c2 = d.Capacities;
        w.WriteLine(
            $"  capacities: vehicles {N(c2.MaxVehicles)}, nodes {N(c2.MaxNodes)}, segments {N(c2.MaxSegments)}, lanes {N(c2.MaxLanes)}, lane points {N(c2.MaxLanePoints)}");
        w.WriteLine(
            $"              zones {N(c2.MaxZones)}, routes {N(c2.MaxRoutes)}, route steps {N(c2.MaxRouteSteps)}, plan steps {N(c2.MaxPlanSteps)}, pending trips {N(c2.MaxPendingTrips)}");
        RoutingDesc rt = d.Routing;
        w.WriteLine(
            $"  routing: levels {N(rt.Levels)}, cell {F(rt.CellSize)} m, fanout {N(rt.Fanout)}, window {N(rt.WindowSteps)}, pathfind/tick {N(rt.PathfindBudgetPerTick)}, work/tick {N(rt.WorkBudgetPerTick)}, customize/tick {N(rt.CustomizeBudgetPerTick)}, recalc {N(rt.CostRecalcTicks)} ticks");
        w.WriteLine("ok");
    }
}
