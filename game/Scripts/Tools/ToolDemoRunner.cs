using System;
using System.Globalization;
using Godot;
using Sim.Core.Api;
using Sim.Host;
using Sim.Scenarios;

namespace TrafficCity.Tools;

/// <summary>
/// Автономный запуск отладочной проверки инструментов (T13): своя симуляция и свой <see cref="SimThreadHost"/>, без мира и UI.
/// <c>Godot --headless --path game res://Scripts/Tools/ToolDemo.tscn -- --scenario demo-grid --tool-demo build-road 400 200 600 200</c>.
/// Код выхода 0 - превью совпало с применением и undo/redo вернули сеть. В основной игре то же делает <see cref="ToolDemo"/> из <c>SimHost</c> (см. описание подключения в отчёте T13).
/// </summary>
public partial class ToolDemoRunner : Node
{
    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs();
        string scenario = "demo-grid";
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--scenario")
            {
                scenario = args[i + 1];
            }
        }

        if (!ToolDemo.TryParse(args, out ToolDemoArgs demo))
        {
            GD.PushError("[tool] usage: --tool-demo build-road x0 z0 x1 z1 [--scenario name]");
            GetTree().Quit(2);
            return;
        }

        int code;
        try
        {
            ScenarioDesc desc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(scenario, null)).Desc;
            Simulation sim = Simulation.Create(in desc, new SimRuntimeOptions { WorkerThreads = 1 });
            using var host = new SimThreadHost(sim, new SimHostOptions());
            var svc = new ToolCommandService(host);
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[tool] scenario={scenario} demo=build-road ({demo.X0}, {demo.Z0}) -> ({demo.X1}, {demo.Z1})"));
            code = ToolDemo.Run(host, svc, in demo, s => GD.Print(s));
        }
        catch (Exception ex)
        {
            GD.PushError("[tool] " + ex);
            code = 3;
        }

        GetTree().Quit(code);
    }
}
