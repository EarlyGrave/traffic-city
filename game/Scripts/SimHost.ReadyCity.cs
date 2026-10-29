using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Sim.Core.Api;
using Sim.Scenarios;
using Sim.Scenarios.Game;
using TrafficCity.Saves;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// «Новая игра -> готовый город» (progression.md §7.3, §10): <c>ReadyCity.TryPrepare</c> гонит 1.25 суток симуляции (~45 с на 3 потоках) в фоновой задаче,
/// главный поток рисует экран загрузки с полосой прогресса и не блокируется; готовый мир подставляется в <c>TryStart</c> как загруженный.
/// </summary>
public partial class SimHost
{
    private const int ReadyCityWorkers = 3;

    private readonly record struct ReadyResult(CreateStatus Status, Simulation? Sim, string Error);

    private Task<ReadyResult>? _prepTask;
    private Simulation? _preparedSim;
    private long _prepDone;
    private long _prepTotal = 1;
    private string _prepName = string.Empty;
    private string[] _prepArgs = Array.Empty<string>();
    private CanvasLayer? _loadLayer;
    private ProgressBar? _loadBar;
    private Label? _loadLabel;

    /// <summary>Запустить подготовку в фоне; false - сценарий не найден (пусть обычный путь сообщит ошибку).</summary>
    private bool BeginReadyCity(string name, string[] args)
    {
        ScenarioDesc desc;
        try
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "scenarios"));
            desc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, dir)).Desc;
        }
        catch (ScenarioException)
        {
            return false;
        }

        int workers = _threadsArg >= 0 ? _threadsArg : ReadyCityWorkers;
        var rt = new SimRuntimeOptions { WorkerThreads = workers };
        var ready = new ReadyCityOptions
        {
            StopHour = 6,
            Progress = (done, total) =>
            {
                Volatile.Write(ref _prepTotal, Math.Max(1, total));
                Volatile.Write(ref _prepDone, done);
            },
        };
        _prepName = name;
        _prepArgs = args;
        BuildLoadingScreen();
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ready-city] prepare started, workers {workers}"));
        ScenarioDesc d = desc;
        _prepTask = Task.Run(() =>
        {
            try
            {
                CreateStatus st = ReadyCity.TryPrepare(in d, in rt, ready, out Simulation? sim);
                return new ReadyResult(st, sim, string.Empty);
            }
            catch (Exception e)
            {
                return new ReadyResult(CreateStatus.Ok, null, e.Message);
            }
        });
        return true;
    }

    /// <summary>Каждый кадр: полоса прогресса; по готовности - достроить старт.</summary>
    private void PollReadyCity()
    {
        Task<ReadyResult>? t = _prepTask;
        if (t == null)
        {
            return;
        }

        if (!t.IsCompleted)
        {
            long done = Volatile.Read(ref _prepDone);
            long total = Volatile.Read(ref _prepTotal);
            double frac = Math.Clamp((double)done / total, 0.0, 1.0);
            if (_loadBar != null)
            {
                _loadBar.Value = frac * 100.0;
            }

            if (_loadLabel != null)
            {
                _loadLabel.Text = string.Create(CultureInfo.InvariantCulture, $"Готовим город… {(int)(frac * 100)}%");
            }

            return;
        }

        _prepTask = null;
        ReadyResult r = t.Result;
        _loadLayer?.QueueFree();
        _loadLayer = null;
        if (r.Sim == null)
        {
            string error = r.Error.Length > 0 ? r.Error : "Simulation.Create failed: " + r.Status;
            GD.PushError(error);
            GameFlow.Notice = "Не удалось создать город: " + error;
            ShowMainMenu();
            return;
        }

        _preparedSim = r.Sim;
        GD.Print("[ready-city] prepared at tick " + r.Sim.Tick.ToString(CultureInfo.InvariantCulture));
        FinishReady(_prepName, _prepArgs, StartKind.New);
    }

    private void BuildLoadingScreen()
    {
        _loadLayer = new CanvasLayer { Layer = 100 };
        var bg = new ColorRect { Color = new Color(0.09f, 0.10f, 0.12f) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadLayer.AddChild(bg);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(360f, 0f) };
        box.SetAnchorsPreset(Control.LayoutPreset.Center);
        box.GrowHorizontal = Control.GrowDirection.Both;
        box.GrowVertical = Control.GrowDirection.Both;
        box.AddThemeConstantOverride("separation", 12);
        _loadLabel = new Label { Text = "Готовим город…", HorizontalAlignment = HorizontalAlignment.Center };
        _loadBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(360f, 12f) };
        box.AddChild(_loadLabel);
        box.AddChild(_loadBar);
        bg.AddChild(box);
        AddChild(_loadLayer);
    }
}
