using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Host;
using Sim.Scenarios;
using TrafficCity.Saves;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Хост симуляции: шаг ядра 20 Гц аккумулятором в _Process, интерполяция Previous -> Current по доле тика,
/// машины (MultiMesh на тип ТС), дороги, оверлеи F1/F2, выбор машины ЛКМ и маршрут, HUD F3.
/// Работает только через публичное API Sim.Core.Api.
/// </summary>
public partial class SimHost : Node3D
{
    private const double TickSeconds = 1.0 / Simulation.TicksPerSecond;
    private const int MaxStepsPerFrame = 5;
    private const float RoutePickMinRadius = 4f;
    private const float MarkerNearDistance = 450f;   // маркеры Walk/Clear и очереди на остановках только вблизи
    private const float MaxViewRange = 6000f;

    private enum PedMode
    {
        Auto,
        Heat,
        Agents,
    }

    private static readonly Color _signalRed = new(1f, 0.2f, 0.2f);
    private static readonly Color _signalYellow = new(1f, 0.86f, 0.15f);
    private static readonly Color _signalGreen = Palette.SignalWalk;

    // ADR-0024: после старта ядром владеет поток симуляции (_host); главный поток читает только кадр (_frame) и ходит в ядро через запросы/команды.
    // _bootSim живёт лишь в TryStart (сборка сцены и прогрев до создания хоста), потом null.
    private Simulation? _bootSim;
    private SimThreadHost? _host;
    private SimFrame? _frame;
    private long _targetTick;          // тик, до которого просим поток симуляции шагать (аккумулятор × скорость)
    private bool _lockstep;            // кадр ждёт тик симуляции: детерминированные замеры/скриншоты (--render-stats, --write-movie, --lockstep)
    private bool _lockstepArg;
    private bool _noViewMargin;        // --no-view-margin: вид ядра без запаса на 2 тика (отладка)
    private int _threadsArg = -1;      // --threads N: потоки симуляции (переопределяет ADR-0024 п. 7)
    private long _lastSimAlloc = -1;
    private long _lastSeq;
    private long _lastSnapTick = -1;
    private float _prevVx0, _prevVz0, _prevVx1, _prevVz1;
    private bool _hasPrevView;
    private double _lastDelta = 1.0 / 60.0;
    private HostOverlays _overlays;
    private enum PickStage
    {
        None,
        Ped,
        Vehicle,
    }

    private PickStage _pickStage;
    private long _pickId;
    private Vector3 _pickHit;
    private float _pickDist;
    private float _pickVehicleRadius;
    private VehicleTypeDesc[] _types = Array.Empty<VehicleTypeDesc>();
    private VehicleLayer? _vehicles;
    private LodConfig _lod = LodConfig.Load(string.Empty, "high");
    private string _renderPreset = TrafficCity.Ui.UiSettings.Current.RenderPreset;
    private string _contentDir = string.Empty;
    private bool _logStepAlloc;
    private RoadChunks? _roads;
    private HeatTexture? _roadHeat;
    private readonly InfoMode _info = new();
    private bool _infoArg;
    private int _infoArgLayer;
    private PedChunks? _pedStatic;
    private LandUseChunks? _landUse;
    private BuildingLayer? _buildings;
    private WorldGrid? _grid;
    private bool _gridArg;           // --grid: сетка 8 м показана сразу (скриншоты)
    private bool _noSnapArg;         // --no-snap: снап выключен
    private bool _hasSnapDemo;       // --snap-demo x z: превью снапа в фиксированной точке
    private Vector2 _snapDemo;
    private bool _profRes;
    private bool _viaSave;           // --prof-res: сценарий *-res в режиме профилей (подмена JSON в памяти, как ProfileSaveFixtures.Res)
    private BoxBatch? _signals;
    private int[] _signalLanes = Array.Empty<int>();
    private MeshInstance3D? _routeNode;
    private ImmediateMesh? _routeMesh;
    private RtsCamera? _camera;
    private PerfHud? _hud;
    private UiRoot? _ui;
    private bool _statusError;
    private RenderStats? _renderStats;
    private string _pose = string.Empty;
    private bool _timeAuto;        // --time-of-day auto: час суток из игрового времени (только при demand.model = residents)
    private int _dayTicks;         // calendar.day_s * 20; 0 - нет календаря (фиксированный час)
    private Label? _status;
    private PedMesh? _peds;
    private CrosswalkMesh? _cross;
    private PedMode _pedMode = PedMode.Auto;
    private PedHandle _selPed;
    private bool _pedHeatShown;
    private int _warmupTicks;
    private int _demolishCount;      // --demolish-test N: снести N сегментов после прогрева (проверка мёртвых слотов сети в слоях клиента)
    private int _demolishTicks = 1200; // --demolish-ticks M: сколько тиков прогнать после команд (дренаж, затем снос)
    private int _stopWaitCache;
    private bool _hasCamArgs;
    private Vector3 _camCenter;
    private float _camDistance;
    private float _camPitch = 55f;
    private float _camYaw;
    private bool _showHud;
    private bool _hudPending;
    private bool _selectFirstPed;
    private bool _frameTiming;       // --frame-timing: время кадра главного потока (мс) в журнал при выходе
    private int _maxFpsArg;          // --max-fps N: потолок FPS окна (замеры без vsync)
    private float _timeScaleArg = 1f; // --time-scale X: стартовая скорость игры
    private bool _pausedArg;         // --paused: старт на паузе (после прогрева)
    private readonly System.Collections.Generic.List<float> _ftProcess = new(4096);
    private readonly System.Collections.Generic.List<float> _ftEngine = new(4096);
    private readonly System.Collections.Generic.List<float> _ftWall = new(4096);
    private int _ftSkip = 60;
    private const int SelectCentralSignal = -2;
    private int _selectNodeArg = -1;
    private NodeArms.NodePreset? _presetArg;
    private TrafficCity.Tools.NodesMode? _nodesToolArg;
    private NodePicker? _nodePicker;
    private float _viewX0, _viewZ0, _viewX1, _viewZ1;
    private float _mapX0, _mapZ0, _mapX1, _mapZ1;

    private double _accumulator;
    private double _statsTimer;
    private bool _paused;
    private float _timeScale = 1f;
    private bool _hasCity;           // сценарий в режиме жителей: у города есть население (и, возможно, казна)
    private double _cityHudTimer;
    private long _cityHudTick = -1;
    private CityHudData _cityHud;
    private bool _signalsOn;
    private bool _routeDirty;
    private long _routeTick = -1;
    private SimStats _stats;

    /// <summary>Имя сценария из scenarios/ (без .json) или путь к файлу; аргумент запуска <c>-- --scenario имя</c> имеет приоритет.</summary>
    [Export]
    public string Scenario { get; set; } = "s1k";

    public override void _Ready()
    {
        TrafficCity.Ui.UiSettingsPage.TimeOfDayRequested += SetTimeOfDay;
        _camera = GetNodeOrNull<RtsCamera>("../CameraRig");
        _hud = GetNodeOrNull<PerfHud>("../Hud/PerfHud");
        _status = GetNodeOrNull<Label>("../Hud/Status");
        _ui = GetNodeOrNull<UiRoot>("../Ui");
        if (_ui != null)
        {
            _ui.Router.InfoChanged += OnUiInfoChanged;
            _ui.Router.InfoLayerChanged += OnUiInfoLayer;
            _ui.Router.InspectorCloseRequested += ClearSelection;
        }
        _renderStats = GetNodeOrNull<RenderStats>("../RenderStats");

        InfoMode.Init();
        WorldGrid.Init();
        string name = Scenario;
        string[] args = OS.GetCmdlineUserArgs();
        ParseArgs(args, ref name);
        InitSave(args.Length);
        StartKind start = DecideStart(args.Length, ref name);
        if (start == StartKind.Menu)
        {
            ShowMainMenu();
            return;
        }

        if (start == StartKind.New && _loadPath.Length == 0 && !_profRes && BeginReadyCity(name, args))
        {
            return; // готовый город готовится в фоне; FinishReady вызовет PollReadyCity
        }

        FinishReady(name, args, start);
    }

    /// <summary>Вторая половина старта: создать мир (TryStart), хост, камеру, режимы; для «готового города» вызывается после фоновой подготовки.</summary>
    private void FinishReady(string name, string[] args, StartKind start)
    {
        if (!TryStart(name, out string error))
        {
            GD.PushError(error);
            if (start == StartKind.Load)
            {
                GameFlow.Notice = "Не удалось загрузить: " + error;
                ShowMainMenu();
                return;
            }

            SetStatus("ERROR: " + error);
            return;
        }

        AfterStart();

        if (_pose.Length > 0)
        {
            ApplyNamedPose(_pose);
        }
        else if (_hasCamArgs)
        {
            _camera?.SetPose(_camCenter, _camDistance, _camPitch, _camYaw);
        }
        else
        {
            RestoreLoadedCamera();
        }

        if (_renderStats != null)
        {
            _renderStats.Scenario = name;
            _renderStats.Pose = _pose.Length > 0 ? _pose : (_hasCamArgs ? "cam" : "default");
            _renderStats.WarmupTicks = _warmupTicks;
            _renderStats.ExtraArgs = ArgsWithoutPaths(args);
        }

        if (!_infoArg && _ui?.StartMode == UiMode.Info)
        {
            _infoArg = true; // --ui-state info = --info traffic
        }

        if (_infoArg)
        {
            _info.Snap(true, _infoArgLayer);
            _ui?.Router.MirrorInfo(true, _infoArgLayer);
        }

        if (_showHud)
        {
            _hudPending = true; // PerfHud._Ready ещё не отработал (он ниже в сцене) и сбросит Visible
        }
    }

    /// <summary>Аргументы запуска для JSON замера без путей вывода (JSON не зависит от места checkout).</summary>
    private static string ArgsWithoutPaths(string[] args)
    {
        var parts = new System.Collections.Generic.List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--render-stats" && i + 1 < args.Length)
            {
                i++;
            }
            else
            {
                parts.Add(args[i]);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Аргументы после <c>--</c>: <c>--scenario имя</c>; для скриншотов и замеров: <c>--cam x z расстояние [наклон° [поворот°]]</c>,
    /// <c>--select-node id|signal [--node-preset adaptive|fixed|flash] [--nodes-tool control|arms|bans|lanes]</c> (панель узла, инструмент «Узлы»), <c>--warmup-ticks N</c> (прогнать симуляцию до первого кадра), <c>--hud</c> (показать HUD), <c>--ped-mode auto|heat|agents</c>, <c>--info traffic|peds|zones</c> (сразу в информационном режиме),
    /// <c>--time-of-day HH:MM|ч|auto</c> (свет суток, VS10; auto - по calendar.day_s при residents, тик 0 = 00:00; выбор в настройках), <c>--pose overview|mid|close|lod0</c> (фиксированная поза замера), <c>--render-stats файл.json [--stats-frame K]</c>, <c>--demolish-test N [--demolish-ticks M]</c> (снести N сегментов после прогрева, проверка мёртвых слотов),
    /// RP10: <c>--grid</c> (сетка 8 м показана сразу), <c>--no-snap</c>, <c>--snap-demo x z</c> (превью снапа в точке + строка <c>[snap]</c> в журнале), <c>--prof-res</c> (сценарий <c>*-res</c> с профилями фикстуры; Shift+G - сетка, G - снап).
    /// S10 (ADR-0024): <c>--threads N</c> (потоки симуляции; по умолчанию min(4, max(1, ядер/2 - 2))), <c>--lockstep</c> (кадр ждёт тик симуляции; включается сам при <c>--render-stats</c> и <c>--write-movie</c>),
    /// <c>--frame-timing</c> (перцентили времени кадра главного потока в журнал при выходе), <c>--max-fps N</c>, <c>--time-scale X</c> (скорость игры со старта), <c>--no-view-margin</c> (вид ядра без запаса на 2 тика).
    /// </summary>
    /// <summary>Час суток из <c>HH:MM</c> или десятичных часов (<c>19</c>, <c>19.5</c>).</summary>
    private static bool TryParseHour(string v, out float hour)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        int c = v.IndexOf(':', StringComparison.Ordinal);
        if (c > 0 && int.TryParse(v.AsSpan(0, c), NumberStyles.Integer, inv, out int h) && int.TryParse(v.AsSpan(c + 1), NumberStyles.Integer, inv, out int m))
        {
            hour = h + (m / 60f);
            return true;
        }

        return float.TryParse(v, NumberStyles.Float, inv, out hour);
    }

    private void ParseArgs(string[] args, ref string name)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--hud")
            {
                _showHud = true;
            }
            else if (a == "--grid")
            {
                _gridArg = true;
            }
            else if (a == "--no-view-margin")
            {
                _noViewMargin = true;
            }
            else if (a == "--lockstep")
            {
                _lockstepArg = true;
            }
            else if (a == "--threads" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], NumberStyles.Integer, inv, out _threadsArg);
            }
            else if (a == "--frame-timing")
            {
                _frameTiming = true;
            }
            else if (a == "--max-fps" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], NumberStyles.Integer, inv, out _maxFpsArg);
            }
            else if (a == "--time-scale" && i + 1 < args.Length)
            {
                float.TryParse(args[++i], NumberStyles.Float, inv, out _timeScaleArg);
            }
            else if (a == "--paused")
            {
                _pausedArg = true;
            }
            else if (a == "--no-snap")
            {
                _noSnapArg = true;
            }
            else if (a == "--prof-res")
            {
                _profRes = true;
            }
            else if (a == "--via-save")
            {
                _viaSave = true;
            }
            else if (a == "--snap-demo" && i + 2 < args.Length
                && float.TryParse(args[i + 1], NumberStyles.Float, inv, out float sdx)
                && float.TryParse(args[i + 2], NumberStyles.Float, inv, out float sdz))
            {
                _hasSnapDemo = true;
                _snapDemo = new Vector2(sdx, sdz);
                i += 2;
            }
            else if (a == "--render-preset" && i + 1 < args.Length)
            {
                _renderPreset = args[++i] == "low" ? "low" : "high";
            }
            else if (a == "--log-step-alloc")
            {
                _logStepAlloc = true;
            }
            else if (a == "--time-of-day" && i + 1 < args.Length)
            {
                string v = args[++i];
                if (v == "auto")
                {
                    _timeAuto = true;
                }
                else if (TryParseHour(v, out float hh))
                {
                    RenderPreset.SetStartHour(hh);
                }
            }
            else if (a == "--pose" && i + 1 < args.Length)
            {
                _pose = args[++i];
            }
            else if (_renderStats != null && _renderStats.TryParseArg(args, i, out int used))
            {
                i += used;
            }
            else if (a == "--select-node" && i + 1 < args.Length)
            {
                string v = args[++i];
                if (v == "signal")
                {
                    _selectNodeArg = SelectCentralSignal;
                }
                else
                {
                    int.TryParse(v, NumberStyles.Integer, inv, out _selectNodeArg);
                }
            }
            else if (a == "--nodes-tool" && i + 1 < args.Length)
            {
                _nodesToolArg = args[++i] switch
                {
                    "arms" => TrafficCity.Tools.NodesMode.Arms,
                    "bans" => TrafficCity.Tools.NodesMode.Bans,
                    "lanes" => TrafficCity.Tools.NodesMode.Lanes,
                    _ => TrafficCity.Tools.NodesMode.Control,
                };
            }
            else if (a == "--bus-tool" && i + 1 < args.Length)
            {
                _busToolArg = args[++i];
            }
            else if (a == "--zones-tool" && i + 1 < args.Length)
            {
                _zonesToolArg = args[++i];
            }
            else if (a == "--zones-rect" && i + 1 < args.Length)
            {
                string[] zr = args[++i].Split(',');
                if (zr.Length == 4)
                {
                    _zonesRectArg = new float[4];
                    for (int zi = 0; zi < 4; zi++)
                    {
                        float.TryParse(zr[zi], NumberStyles.Float, inv, out _zonesRectArg[zi]);
                    }
                }
            }
            else if (a == "--node-preset" && i + 1 < args.Length)
            {
                _presetArg = args[++i] switch
                {
                    "fixed" => NodeArms.NodePreset.Fixed,
                    "flash" => NodeArms.NodePreset.Flash,
                    _ => NodeArms.NodePreset.Adaptive,
                };
            }
            else if (a == "--select-ped")
            {
                _selectFirstPed = true;
            }
            else if (a == "--scenario" && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else if (ParseSaveArg(args, ref i))
            {
                // --main-menu, --load-file, --saves-dir, --autosave-hours, --save-test, --save-quit (SimHost.Save.cs)
            }
            else if (a == "--warmup-ticks" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], NumberStyles.Integer, inv, out _warmupTicks);
            }
            else if (a == "--demolish-test" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], NumberStyles.Integer, inv, out _demolishCount);
            }
            else if (a == "--demolish-ticks" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], NumberStyles.Integer, inv, out _demolishTicks);
            }
            else if (a == "--info" && i + 1 < args.Length)
            {
                _infoArg = true;
                _infoArgLayer = args[++i] switch
                {
                    "peds" => InfoMode.LayerPeds,
                    "zones" => InfoMode.LayerZones,
                    _ => InfoMode.LayerTraffic,
                };
            }
            else if (a == "--ped-mode" && i + 1 < args.Length)
            {
                _pedMode = args[++i] switch
                {
                    "heat" => PedMode.Heat,
                    "agents" => PedMode.Agents,
                    _ => PedMode.Auto,
                };
            }
            else if (a == "--cam" && i + 3 < args.Length
                && float.TryParse(args[i + 1], NumberStyles.Float, inv, out float cx)
                && float.TryParse(args[i + 2], NumberStyles.Float, inv, out float cz)
                && float.TryParse(args[i + 3], NumberStyles.Float, inv, out float cd))
            {
                _hasCamArgs = true;
                _camCenter = new Vector3(cx, 0f, cz);
                _camDistance = cd;
                i += 3;
                if (i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, inv, out float pitch))
                {
                    _camPitch = pitch;
                    i++;
                    if (i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, inv, out float yaw))
                    {
                        _camYaw = yaw;
                        i++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Фиксированные позы замера (docs/design/render.md §8.2), от размера карты <c>E = max(w, h)</c>, центр карты, наклон 55°, поворот 0:
    /// overview - как стартовый <c>Focus</c> (1.1·E); mid - 0.12·E в [300, 1 500] м; close - 0.02·E в [60, 120] м.
    /// </summary>
    private void ApplyNamedPose(string pose)
    {
        float extent = MathF.Max(_mapX1 - _mapX0, _mapZ1 - _mapZ0);
        var center = new Vector3((_mapX0 + _mapX1) * 0.5f, 0f, (_mapZ0 + _mapZ1) * 0.5f);
        float dist = pose switch
        {
            "mid" => Math.Clamp(extent * 0.12f, 300f, 1500f),
            "close" => Math.Clamp(extent * 0.02f, 60f, 120f),
            "lod0" => 30f, // внутри LOD0-границы обоих пресетов (42/60 м): единственная поза, где работают тени ТС (V6)
            _ => extent * 1.1f,
        };
        if (_camera != null)
        {
            _camera.SetPose(center, dist, 55f, 0f);
            _camera.Locked = true;
        }

        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[pose] {pose}: center ({center.X:0.0}, {center.Z:0.0}) dist {dist:0.0} extent {extent:0.0}"));
    }

    private static string Percentiles(System.Collections.Generic.List<float> v)
    {
        if (v.Count == 0)
        {
            return "n=0";
        }

        float[] a = v.ToArray();
        Array.Sort(a);
        float mean = 0f;
        foreach (float x in a)
        {
            mean += x;
        }

        mean /= a.Length;
        float At(double q) => a[Math.Min(a.Length - 1, (int)(a.Length * q))];
        return string.Create(CultureInfo.InvariantCulture, $"n={a.Length} mean {mean:0.000} p50 {At(0.5):0.000} p95 {At(0.95):0.000} p99 {At(0.99):0.000} max {a[^1]:0.000}");
    }

    /// <summary>Замер времени кадра главного потока (мс): SimHost._Process, Performance TimeProcess, wall delta.</summary>
    private void PrintFrameTiming()
    {
        GD.Print("[frame-timing] SimHost._Process ms: " + Percentiles(_ftProcess));
        GD.Print("[frame-timing] engine TimeProcess ms: " + Percentiles(_ftEngine));
        GD.Print("[frame-timing] wall frame ms: " + Percentiles(_ftWall));
    }

    public override void _ExitTree()
    {
        if (_frameTiming)
        {
            PrintFrameTiming();
        }

        _host?.Dispose();
        _host = null;
        _bootSim?.Dispose();
        _bootSim = null;
        _preparedSim?.Dispose();
        _preparedSim = null;
        Task<ReadyResult>? pending = _prepTask;
        if (pending != null && !pending.IsCompleted)
        {
            pending.ContinueWith(t => t.Result.Sim?.Dispose(), TaskScheduler.Default); // сцену сносят посреди подготовки: мир никому не нужен
        }
    }

    public override void _Process(double delta)
    {
        PollReadyCity();
        if (_frameTiming)
        {
            if (_ftSkip > 0)
            {
                _ftSkip--;
            }
            else
            {
                _ftEngine.Add((float)(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0));
                _ftWall.Add((float)(delta * 1000.0));
            }
        }

        long ft0 = Stopwatch.GetTimestamp();
        ProcessFrame(delta);
        if (_frameTiming && _ftSkip == 0)
        {
            _ftProcess.Add((float)((Stopwatch.GetTimestamp() - ft0) * 1000.0 / Stopwatch.Frequency));
        }
    }

    private void ProcessFrame(double delta)
    {
        if (_host == null || _frame == null)
        {
            return;
        }

        _lastDelta = delta;
        if (_hudPending && _hud != null)
        {
            SetDebugOverlay(true);
            _hudPending = false;
        }

        HandleResponses();
        SyncUiInspector();

        long allocA = GC.GetAllocatedBytesForCurrentThread();
        UpdateRenderView(_host, delta);
        long allocView = GC.GetAllocatedBytesForCurrentThread() - allocA;

        // Темп (ADR-0024 п. 2): главный поток только копит время и называет целевой тик; шагает поток симуляции.
        if (!_paused)
        {
            _accumulator += delta * _timeScale;
            int steps = 0;
            while (_accumulator >= TickSeconds && steps < MaxStepsPerFrame)
            {
                _targetTick++;
                _accumulator -= TickSeconds;
                steps++;
            }

            if (_accumulator >= TickSeconds)
            {
                // не успеваем: отбрасываем накопленное, чтобы не уйти в спираль
                _accumulator = 0;
            }
        }

        UpdateSubscriptions();
        _host.SetTargetTick(_targetTick);
        bool newFrame = AcquireFrame();
        SimFrame f = _frame;
        if (newFrame)
        {
            OnNewFrame(f);
        }

        RefreshCityHud(f, delta);
        RefreshCityPanel(f, delta);
        UpdateSaveFlow(f);
        if (_timeAuto && _dayTicks > 0)
        {
            // тик 0 = 00:00; обновляем раз в ~секунду игрового времени (20 тиков), не каждый кадр
            RenderPreset.SetHour(GetParent(), 24f * (f.SnapshotTick % _dayTicks) / _dayTicks);
        }

        // Рисуем кадр на 1 тик позади цели (как и раньше: Previous -> Current по доле тика). Кадр опоздал на тик - держим его конец (alpha <= 1).
        float alpha = _paused ? 1f : Math.Clamp((float)(_targetTick - f.SnapshotTick) + (float)(_accumulator / TickSeconds), 0f, 1f);
        float dist = _camera?.Distance ?? 0f;
        long allocB = GC.GetAllocatedBytesForCurrentThread();
        if (_vehicles != null && _camera != null)
        {
            _vehicles.Update(f.Vehicles, f.PreviousVehicles, alpha, _camera.Camera.GlobalPosition);
        }

        long allocLayer = GC.GetAllocatedBytesForCurrentThread() - allocB;
        _renderStats?.RecordFrame(_vehicles, allocView, allocLayer);
        if (_logStepAlloc && allocView + allocLayer > 0)
        {
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[frame-alloc] tick {f.SnapshotTick} SetRenderView {allocView} B, vehicle layer {allocLayer} B"));
        }

        if (_roads != null && _roads.Version != f.NetworkVersion)
        {
            RebuildNetworkLayers(f.Network.View);
        }

        if (_peds != null && _cross != null)
        {
            UpdatePeds(f, newFrame, alpha, dist, delta);
        }

        UpdateInspect(f, alpha, delta);

        if (_signalsOn)
        {
            UpdateSignals(f);
        }

        if (_landUse != null && _landUse.IsStale(f.LandUseVersion, f.LotsVersion))
        {
            // земля/участки читаются из ядра: пересборка под эксклюзивным доступом (ADR-0024 п. 5, редкое событие)
            using ExclusiveAccess ex = _host.AcquireExclusive();
            if (ex.Simulation != null)
            {
                _landUse.Rebuild(ex.Simulation);
            }
        }

        if (_buildings != null && _camera != null)
        {
            _buildings.Update(f.BuildingsVersion, _camera.Camera.GlobalPosition, _host);
        }

        _info.Update(delta, _camera?.Distance ?? 1000f);
        UpdateLegend(f);
        _grid?.Update(delta, _camera?.Camera, GetViewport().GetMousePosition(), Input.IsActionPressed(UiActions.SnapOverride));
        ProcessTools();
        if (newFrame && f.SegmentLoadsFresh && _roadHeat != null && _info.Visible)
        {
            // heatmap дорог в шейдере: раз в 10 тиков (2 Гц) приходит копия нагрузок, обновляется только текстура данных, мешей не трогаем (render.md §3.5)
            _roadHeat.UpdateRoads(f.SegmentLoads);
        }

        if (_routeNode != null && (_routeDirty || _routeTick != f.SnapshotTick))
        {
            UpdateRoute(f);
        }

        if (_hud != null && _hud.Visible)
        {
            _hud.Agents = f.ActiveVehicleCount;
            _stats = f.Stats;
            _renderStats?.RefreshHud();
            long lag = _host.TickLag;
            string state = _host.State == HostState.Faulted ? "\nSIM FAULT: " + _host.Fault?.Message : string.Empty;
            _hud.Extra = string.Create(
                CultureInfo.InvariantCulture,
                $"sim tick {f.SnapshotTick}  x{_timeScale:0.##}{(_paused ? " PAUSED" : string.Empty)}{(lag > 1 ? " lag " + lag : string.Empty)}\navg {_stats.AvgSpeedKmh:0.0} km/h  stuck {_stats.StuckVehicles}")
                + (_vehicles != null ? "\n" + _vehicles.HudText() : string.Empty)
                + PedHudText(f)
                + (_renderStats != null && _renderStats.HudText.Length > 0 ? "\n" + _renderStats.HudText : string.Empty)
                + state;
        }
    }

    /// <summary>
    /// Забрать кадр симуляции. В режиме lockstep (скриншоты и замеры рендера, где кадр = 1 тик) ждёт, пока поток симуляции дойдёт до цели:
    /// число тиков на кадр и состояние на кадре те же, что при шаге на главном потоке. Возвращает true, если кадр новый.
    /// </summary>
    private bool AcquireFrame()
    {
        SimThreadHost host = _host!;
        bool got = host.TryAcquireFrame(out SimFrame f);
        if (_lockstep && host.State == HostState.Running && f.SnapshotTick < _targetTick)
        {
            long t0 = Stopwatch.GetTimestamp();
            while (f.SnapshotTick < _targetTick && host.State == HostState.Running
                && (Stopwatch.GetTimestamp() - t0) < 20L * Stopwatch.Frequency)
            {
                System.Threading.Thread.Yield();
                got |= host.TryAcquireFrame(out f);
            }
        }

        _frame = f;
        return got && f.Sequence != _lastSeq;
    }

    /// <summary>Новый опубликованный кадр: статистика шага потока симуляции (HUD, JSON замера).</summary>
    private void OnNewFrame(SimFrame f)
    {
        _lastSeq = f.Sequence;
        if (f.LastStepTicks <= 0)
        {
            return; // републикация после эксклюзивного доступа или нулевой кадр
        }

        if (_lastSimAlloc >= 0)
        {
            long dAlloc = f.SimThreadAllocatedBytes - _lastSimAlloc;
            int dTicks = (int)Math.Max(1, f.SnapshotTick - _lastSnapTick);
            _hud?.RecordTick(f.LastStepMicros / 1000.0 / f.LastStepTicks, dAlloc, dTicks);
            _renderStats?.RecordStep(dAlloc / dTicks);
            if (_logStepAlloc && dAlloc != 0)
            {
                GD.Print(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[step-alloc] tick {f.SnapshotTick} bytes {dAlloc} over {dTicks} ticks view ({_viewX0:0.0},{_viewZ0:0.0})-({_viewX1:0.0},{_viewZ1:0.0})"));
            }
        }

        _lastSimAlloc = f.SimThreadAllocatedBytes;
        _lastSnapTick = f.SnapshotTick;
    }

    /// <summary>Подписки на оверлеи кадра по тому, что сейчас видно/открыто (ADR-0024 п. 3); смена - в хост.</summary>
    private HostOverlays ComputeOverlays(bool hasPeds)
    {
        HostOverlays o = HostOverlays.None;
        if (hasPeds)
        {
            o |= HostOverlays.CrossingStates | HostOverlays.BusStopWaiting | HostOverlays.PedEdgeCounts;
        }

        if (_signalsOn)
        {
            o |= HostOverlays.LaneSignals;
        }

        o |= InspectOverlays();

        if (_info.Visible)
        {
            o |= HostOverlays.SegmentLoads;
        }

        return o;
    }

    private void UpdateSubscriptions()
    {
        HostOverlays o = ComputeOverlays(_frame?.HasPeds ?? false);
        if (o != _overlays)
        {
            _overlays = o;
            _host?.SetOverlays(o);
        }
    }

    private void SendCommand(in Command cmd)
    {
        if (_host != null)
        {
            _host.TryEnqueue(in cmd);
        }
        else
        {
            _bootSim?.Enqueue(in cmd);
        }
    }

    private void SetSelectedPed(PedHandle h)
    {
        _selPed = h;
        _host?.SetSelectedPed(h);
    }

    // ---- время и данные для верхней полосы (UI2) ----

    /// <summary>Игра на паузе: аккумулятор не копится, целевой тик стоит, поток симуляции не шагает.</summary>
    public bool TimePaused => _paused;

    /// <summary>Множитель скорости игры (1 = реальное время; кнопки ×1/×2/×4, клавиши +/- - 0.125-16).</summary>
    public float TimeScale => _timeScale;

    /// <summary>Симуляция запущена (хост создан, первый кадр получен): свойства сценария ниже достоверны.</summary>
    public bool IsReady => _host != null && _frame != null;

    /// <summary>Тик последнего кадра симуляции (0 до старта).</summary>
    public long SimTick => _frame?.SnapshotTick ?? 0;

    /// <summary>Тиков в игровых сутках (calendar.day_s * 20); 0 - у сценария нет календаря.</summary>
    public int DayTicks => _dayTicks;

    /// <summary>Сценарий в режиме жителей: есть население, зонирование, возможно казна.</summary>
    public bool HasCity => _hasCity;

    /// <summary>Население и казна, обновляются раз в секунду (под эксклюзивным доступом), пока идёт время.</summary>
    public CityHudData CityHud => _cityHud;

    /// <summary>Изменились пауза или скорость.</summary>
    public event Action? TimeChanged;

    public void TogglePause() => SetPaused(!_paused);

    public void SetPaused(bool paused)
    {
        if (_paused != paused)
        {
            _paused = paused;
            TimeChanged?.Invoke();
        }
    }

    /// <summary>Час суток из настроек: null - по игровому времени (при календаре), иначе фиксированный час.</summary>
    private void SetTimeOfDay(float? hour)
    {
        _timeAuto = hour == null;
        if (hour is float h)
        {
            RenderPreset.SetHour(GetParent(), h);
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[time] {RenderPreset.Hour:0.0} h"));
        }
    }

    /// <summary>Пресет рендера (F4, настройки): только render-часть; LOD-пороги и лимиты слоёв читаются при старте.</summary>
    public void SetRenderPreset(string preset)
    {
        if (!IsReady || _lod.Preset == preset)
        {
            return;
        }

        _lod = LodConfig.Load(_contentDir, preset);
        RenderPreset.Apply(GetParent(), _lod);
        GD.Print(RenderPreset.Describe(_lod) + " (LOD/лимиты слоёв - после перезапуска)");
    }

    /// <summary>Задать скорость (снимает паузу): кнопки ×1/×2/×4 и клавиши 1/2/3 вне инфо-режима.</summary>
    public void SetSpeed(float scale)
    {
        _timeScale = Math.Clamp(scale, 0.125f, 16f);
        _paused = false;
        TimeChanged?.Invoke();
    }

    /// <summary>Клавиши +/-: ×2 или ÷2 в отладочном диапазоне 0.125-16 (ui-spec.md §9); паузу не снимает.</summary>
    private void StepSpeed(float factor)
    {
        _timeScale = Math.Clamp(_timeScale * factor, 0.125f, 16f);
        TimeChanged?.Invoke();
    }

    private void RefreshCityHud(SimFrame f, double delta)
    {
        _cityHudTimer -= delta;
        if (!_hasCity || _host == null || _cityHudTimer > 0 || f.SnapshotTick == _cityHudTick)
        {
            return; // на паузе тик не меняется - данные те же, поток симуляции не трогаем
        }

        _cityHudTimer = 1.0;
        _cityHudTick = f.SnapshotTick;
        using ExclusiveAccess ex = _host.AcquireExclusive();
        Simulation? sim = ex.Simulation;
        if (sim == null)
        {
            return;
        }

        var d = new CityHudData { HasPopulation = true, Population = sim.CityPopulation };
        if (sim.GetCityBudget(out CityBudgetInfo b))
        {
            d.HasBudget = true;
            d.Balance = b.Balance;
            d.FlowPerDay = b.IncomeEwma - b.ExpenseEwma;
            d.Insolvent = b.Insolvent != 0;
        }

        _cityHud = d;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_host == null)
        {
            return;
        }

        if (_ui != null && _ui.WorldInputBlocked)
        {
            return; // меню игры или подтверждение открыто: мир и время молчат
        }

        if (HandleToolInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false })
        {
            // F1, слои 1-3 и Esc обрабатывает UiRoot (роутер состояния интерфейса); тут - остальные действия мира
            if (@event.IsActionPressed(UiActions.QuickSave))
            {
                SaveGame(SaveKind.Quick);
            }
            else if (@event.IsActionPressed(UiActions.QuickLoad))
            {
                QuickLoad();
            }
            else if (UiActions.Pressed(@event, UiActions.GridShowToggle) && _grid != null)
            {
                _grid.ToggleShown();
                GD.Print(_grid.StatusText());
            }
            else if (UiActions.Pressed(@event, UiActions.GridSnapToggle) && _grid != null)
            {
                _grid.ToggleSnap();
                GD.Print(_grid.StatusText());
            }
            else if (@event.IsActionPressed(UiActions.SignalsOverlay))
            {
                _signalsOn = !_signalsOn;
                if (_signals != null)
                {
                    _signals.Node.Visible = _signalsOn;
                }
            }
            else if (@event.IsActionPressed(UiActions.RenderPresetToggle))
            {
                // Только render-часть пресета (тени, scaling_3d, MSAA, SSAO); LOD-пороги и лимиты слоёв читаются при старте -
                // для них перезапуск с --render-preset.
                SetRenderPreset(_lod.Preset == "low" ? "high" : "low");
                TrafficCity.Ui.UiSettings.Current.RenderPreset = _lod.Preset;
                TrafficCity.Ui.UiSettings.Current.Save();
            }
            else if (@event.IsActionPressed(UiActions.PerfHud))
            {
                if (_hud != null)
                {
                    SetDebugOverlay(!_hud.Visible);
                }
            }
            else if (@event.IsActionPressed(UiActions.PedModeCycle))
            {
                _pedMode = _pedMode switch
                {
                    PedMode.Auto => PedMode.Heat,
                    PedMode.Heat => PedMode.Agents,
                    _ => PedMode.Auto,
                };
            }
            else if (@event.IsActionPressed(UiActions.TimePause))
            {
                TogglePause();
            }
            else if (@event.IsActionPressed(UiActions.TimeFaster))
            {
                StepSpeed(2f);
            }
            else if (@event.IsActionPressed(UiActions.TimeSlower))
            {
                StepSpeed(0.5f);
            }
        }
        else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            PickAtMouse(mb.Position);
        }
    }

    /// <summary>
    /// Сценарий в режиме профилей без отдельного файла (<c>--prof-res</c>): тот же набор подмен, что у <c>ProfileSaveFixtures.Res</c> -
    /// <c>road_profiles_file</c>, профили генератора, запас участков (на сетке 8 м их заметно больше, чем вдоль дороги).
    /// </summary>
    private static ScenarioDesc LoadProfileRes(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        System.Text.Json.Nodes.JsonNode n = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))!;
        n["road_profiles_file"] = "road-profiles.json";
        n["city"]!["profiles"] = new System.Text.Json.Nodes.JsonObject { ["street"] = "street_2", ["arterial"] = "arterial_4" };
        System.Text.Json.Nodes.JsonNode caps = n["capacities"]!;
        caps["max_lots"] = 12_000;
        caps["max_buildings"] = 9_000;
        return ScenarioLoader.Parse(
            n.ToJsonString(),
            rel => System.IO.File.ReadAllText(System.IO.Path.Combine(dir, rel)),
            System.IO.Path.GetFileNameWithoutExtension(path) + "-prof",
            ScenarioLoader.DefaultSearchDirs(dir)).Desc;
    }

    /// <summary>
    /// Проверка RP10b без UI загрузки: мир сохраняется в память и грузится заново без дескриптора, дальше клиент читает каталог профилей и сетку только из вью/снапшотов.
    /// </summary>
    private static Simulation? ReloadViaSave(Simulation src, in SimRuntimeOptions rt, out string error)
    {
        error = string.Empty;
        var bytes = new byte[src.MeasureSave()];
        SaveStatus ss = src.Save(bytes, out long written);
        src.Dispose();
        if (ss != SaveStatus.Ok)
        {
            error = "Save failed: " + ss;
            return null;
        }

        var o = new LoadOptions { StrictHash = 1 };
        LoadStatus ls = Simulation.Load(bytes.AsSpan(0, (int)written), in o, in rt, out Simulation? loaded, out _);
        if (ls != LoadStatus.Ok || loaded == null)
        {
            error = "Load failed: " + ls;
            return null;
        }

        GD.Print("[via-save] мир сохранён и загружен без дескриптора");
        return loaded;
    }

    private bool TryStart(string nameOrPath, out string error)
    {
        error = string.Empty;
        // ADR-0024 п. 7: потоки симуляции (включая поток хоста) = min(4, max(1, логических ядер / 2 - 2)); --threads N переопределяет. На хэш не влияет (ADR-0017).
        int workers = _threadsArg >= 0 ? _threadsArg : Math.Min(4, Math.Max(1, (System.Environment.ProcessorCount / 2) - 2));
        var rt = new SimRuntimeOptions { WorkerThreads = workers };
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[threads] sim workers {workers} (logical cores {System.Environment.ProcessorCount})"));

        // загрузка сейва: мир приходит из файла, сценарий (имя из meta сейва) нужен клиенту только для типов ТС и сетки привязки
        Simulation? sim = _preparedSim;
        _preparedSim = null;
        if (_loadPath.Length > 0)
        {
            sim = LoadWorld(in rt, out nameOrPath, out error);
            if (sim == null)
            {
                return false;
            }
        }

        string path;
        try
        {
            string scenariosDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "scenarios"));
            path = ScenarioLocator.Resolve(nameOrPath, scenariosDir);
        }
        catch (ScenarioException e)
        {
            sim?.Dispose();
            error = e.Message;
            return false;
        }

        ScenarioDesc desc;
        try
        {
            desc = _profRes ? LoadProfileRes(path) : ScenarioLoader.LoadInfo(path).Desc;
        }
        catch (ScenarioException e)
        {
            sim?.Dispose();
            error = e.Message;
            return false;
        }

        _gameScenario = System.IO.Path.GetFileNameWithoutExtension(path);
        if (sim == null)
        {
            CreateStatus st = Simulation.TryCreate(in desc, in rt, out sim);
            if (st != CreateStatus.Ok || sim == null)
            {
                error = "Simulation.Create failed: " + st;
                return false;
            }
        }

        if (_viaSave)
        {
            sim = ReloadViaSave(sim, in rt, out error);
            if (sim == null)
            {
                return false;
            }
        }

        _bootSim = sim;
        _timeScale = _timeScaleArg;
        _paused = _pausedArg;
        if (_maxFpsArg > 0)
        {
            Engine.MaxFps = _maxFpsArg;
        }

        _grid = new WorldGrid(this);
        _grid.Configure(sim.Network, in desc);
        _grid.SetSnap(!_noSnapArg);
        if (_gridArg)
        {
            _grid.SetShown(true, true);
        }

        if (_hasSnapDemo)
        {
            _grid.SetFixedPoint(_snapDemo.X, _snapDemo.Y);
            GD.Print(_grid.DescribeSnap(_snapDemo.X, _snapDemo.Y));
        }

        GD.Print(_grid.StatusText());
        RoadProfileLayouts.Set(sim.Network); // сечения дорог для RoadChunks/PedChunks (RP6, RP10b: из вью ядра, не из desc); без каталога - legacy
        _types = desc.VehicleTypes;
        _dayTicks = desc.Demand.Model == DemandModel.Residents ? desc.Town.Calendar.DayTicks : 0;
        _hasCity = desc.Demand.Model == DemandModel.Residents;
        _zonesMaxPaintM2 = desc.Town.Zoning.MaxPaintM2;
        if (_loadPath.Length > 0)
        {
            _dayTicks = sim.CityDayTicks; // мир из сейва сам знает, есть ли в нём город; сценарий мог быть подобран по имени файла
            _hasCity = _dayTicks > 0;
        }

        string contentDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "content"));
        _contentDir = contentDir;
        _lod = LodConfig.Load(contentDir, _renderPreset);
        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[lod] preset {_lod.Preset}: bounds {_lod.Bounds[0]:0}/{_lod.Bounds[1]:0}/{_lod.Bounds[2]:0} m, points to {_lod.Bounds[3]:0} m, caps {_lod.Caps[0]}/{_lod.Caps[1]}/{_lod.Caps[2]}/{_lod.Caps[3]}, min point {_lod.PointMinPx:0.#} px ({_lod.Source})"));
        RenderPreset.Apply(GetParent(), _lod);
        GD.Print(RenderPreset.Describe(_lod));
        BuildScene(sim);
        if (_loadPath.Length == 0)
        {
            sim.Step(Math.Max(1, _warmupTicks)); // первый снапшот; Previous == пусто до второго тика
        }
        if (_demolishCount > 0)
        {
            DemolishTest(sim);
        }

        if (_showHud)
        {
            LogInfo(sim);
        }

        if (_selectFirstPed)
        {
            SelectFirstWalkingPed(sim);
        }

        if (_selectNodeArg == SelectCentralSignal)
        {
            _selectNodeArg = CentralSignalNode(sim);
        }

        if (_selectNodeArg >= 0)
        {
            SelectNode(sim.Network, _selectNodeArg, !_hasCamArgs && _pose.Length == 0);
            if (_presetArg.HasValue)
            {
                OnNodePreset(_selectNodeArg, _presetArg.Value);
                sim.Step(2);
            }
        }

        // Дальше ядром владеет поток симуляции (ADR-0024); главный поток видит только кадры.
        string[] engineArgs = OS.GetCmdlineArgs();
        _lockstep = _lockstepArg || (_renderStats?.Recording ?? false) || Array.IndexOf(engineArgs, "--write-movie") >= 0;
        bool hasPeds = sim.HasPeds;
        _overlays = ComputeOverlays(hasPeds);
        _host = new SimThreadHost(sim, new SimHostOptions { InitialOverlays = _overlays });
        _bootSim = null;
        _host.TryAcquireFrame(out SimFrame first);
        _frame = first;
        _lastSeq = first.Sequence;
        _targetTick = _host.Tick;
        _host.SetSelectedPed(_selPed);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[host] sim thread started at tick {_targetTick}{(_lockstep ? ", lockstep" : string.Empty)}"));
        SetStatus(string.Empty);
        return true;
    }

    /// <summary>
    /// Сеть изменилась (4.3, T2-T4: снос, перенос, закрытие): всё, что построено по массивам сети, пересобирается целиком
    /// (инкрементальный оверлей - T6, бэклог): дороги и heatmap, тротуары/переходы, маркеры переходов и очереди остановок, полосы сигналов.
    /// Слои сами пропускают мёртвые слоты сети (<c>Generation</c> чётное).
    /// </summary>
    private void RebuildNetworkLayers(NetworkView net)
    {
        _roads!.Root.QueueFree();
        _roadHeat = new HeatTexture(net.Segments.Length);
        _roads = new RoadChunks(net, _roadHeat, _lod);
        AddChild(_roads.Root);
        if (_pedStatic != null)
        {
            _pedStatic.Root.QueueFree();
            _pedStatic = new PedChunks(net, _lod);
            AddChild(_pedStatic.Root);
        }

        // heatmap заполнится свежей копией нагрузок/пешеходов из ближайшего кадра, а не через 10 тиков
        _host?.RequestFreshHeavyOverlays();
        _cross?.Refresh(net);
        if (_signals != null)
        {
            CollectSignalLanes(net);
        }

        _routeDirty = true;
        _routeTick = -1;
    }

    /// <summary>
    /// Отладка (<c>--demolish-test N [--demolish-ticks M]</c>): ставит <c>RemoveRoad</c> на N живых сегментов (сначала несущие остановки, затем равномерно по сети)
    /// и прогоняет M тиков. Слои клиента пересобираются в первом кадре по смене версии сети; в лог пишутся счётчики до/после.
    /// </summary>
    private void DemolishTest(Simulation sim)
    {
        NetworkView net = sim.Network;
        // выбор детерминирован и кучный (ближе к центру карты), чтобы дыра была видна на скриншоте
        float cx = (_mapX0 + _mapX1) * 0.5f;
        float cz = (_mapZ0 + _mapZ1) * 0.5f;
        var stopSegs = new System.Collections.Generic.List<(float D, int Seg)>();
        for (int i = 0; i < net.BusStops.Length; i++)
        {
            if (net.IsBusStopAlive(i) && net.IsLaneAlive(net.BusStops[i].Lane))
            {
                int seg = net.Lanes[net.BusStops[i].Lane].Segment;
                if (net.IsSegmentAlive(seg))
                {
                    stopSegs.Add((SegDist(net, seg, cx, cz), seg));
                }
            }
        }

        stopSegs.Sort((x, y) => x.D != y.D ? x.D.CompareTo(y.D) : x.Seg.CompareTo(y.Seg));
        var picked = new System.Collections.Generic.List<int>();
        for (int i = 0; i < stopSegs.Count && picked.Count < _demolishCount / 2; i++)
        {
            if (!picked.Contains(stopSegs[i].Seg))
            {
                picked.Add(stopSegs[i].Seg);
            }
        }

        var alive = new System.Collections.Generic.List<(float D, int Seg)>();
        for (int i = 0; i < net.Segments.Length; i++)
        {
            if (net.IsSegmentAlive(i) && !picked.Contains(i))
            {
                alive.Add((SegDist(net, i, cx, cz), i));
            }
        }

        alive.Sort((x, y) => x.D != y.D ? x.D.CompareTo(y.D) : x.Seg.CompareTo(y.Seg));
        int rest = _demolishCount - picked.Count;
        int pool = Math.Min(alive.Count, rest * 3);
        for (int k = 0; k < rest && pool > 0; k++)
        {
            picked.Add(alive[(int)((long)k * pool / rest)].Seg);
        }

        int ver0 = net.Version;
        int segs0 = CountAliveSegments(net);
        int cross0 = net.Crossings.Length;
        foreach (int seg in picked)
        {
            sim.Enqueue(Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = net.SegmentRef(seg), Flags = 0 }));
        }

        sim.Step(Math.Max(1, _demolishTicks));
        net = sim.Network;
        int closing = 0;
        for (int i = 0; i < net.Segments.Length; i++)
        {
            if (net.IsSegmentAlive(i) && (net.Segments[i].Flags & SegmentFlags.Closing) != 0)
            {
                closing++;
            }
        }

        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[demolish-test] requested {picked.Count} (incl. {Math.Min(picked.Count, _demolishCount / 2)} with stops); segments alive {segs0} -> {CountAliveSegments(net)} of {net.Segments.Length} slots, closing {closing}; crossings {cross0} -> {net.Crossings.Length}; version {ver0} -> {net.Version}"));
    }

    private static float SegDist(NetworkView net, int seg, float cx, float cz)
    {
        Vec2 a = net.Nodes[net.Segments[seg].NodeA].Position;
        Vec2 b = net.Nodes[net.Segments[seg].NodeB].Position;
        float dx = ((a.X + b.X) * 0.5f) - cx;
        float dz = ((a.Z + b.Z) * 0.5f) - cz;
        return (dx * dx) + (dz * dz);
    }

    private static int CountAliveSegments(NetworkView net)
    {
        int n = 0;
        for (int i = 0; i < net.Segments.Length; i++)
        {
            if (net.IsSegmentAlive(i))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Живые не-connector полосы с геометрией: по ним рисуются сигналы (пересобирается при смене версии сети).</summary>
    private void CollectSignalLanes(NetworkView net)
    {
        int lanes = net.Lanes.Length;
        int[] tmp = new int[lanes];
        int n = 0;
        for (int i = 0; i < lanes; i++)
        {
            if (net.IsLaneAlive(i) && net.Lanes[i].Kind != LaneKind.Connector && net.Lanes[i].PointCount >= 2)
            {
                tmp[n++] = i;
            }
        }

        _signalLanes = new int[n];
        Array.Copy(tmp, _signalLanes, n);
    }

    private static ShaderMaterial GroundMaterial()
    {
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/ground_flat.gdshader") };
        mat.SetShaderParameter("ground_color", Palette.Ground);
        return mat;
    }

    private void BuildScene(Simulation sim)
    {
        NetworkView net = sim.Network;
        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxZ = float.MinValue;
        ReadOnlySpan<Vec2> pts = net.LanePoints;
        for (int i = 0; i < pts.Length; i++)
        {
            minX = MathF.Min(minX, pts[i].X);
            maxX = MathF.Max(maxX, pts[i].X);
            minZ = MathF.Min(minZ, pts[i].Z);
            maxZ = MathF.Max(maxZ, pts[i].Z);
        }

        if (pts.Length == 0)
        {
            minX = minZ = -100f;
            maxX = maxZ = 100f;
        }

        float w = maxX - minX;
        float h = maxZ - minZ;
        _mapX0 = minX;
        _mapX1 = maxX;
        _mapZ0 = minZ;
        _mapZ1 = maxZ;
        _viewX0 = minX;
        _viewX1 = maxX;
        _viewZ0 = minZ;
        _viewZ1 = maxZ;
        var center = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);

        AddChild(new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(w + 400f, h + 400f) },
            Position = center + new Vector3(0f, -0.15f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = GroundMaterial(),
        });

        if (LandUseChunks.Needed(sim))
        {
            // слой земли VS9: парки/вода/берег и тон кварталов, один меш на дальний чанк
            _landUse = new LandUseChunks(new Vec2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f));
            _landUse.Rebuild(sim);
            AddChild(_landUse.Root);
        }

        _roadHeat = new HeatTexture(sim.Network.Segments.Length);
        _roadHeat.UpdateRoads(sim.SegmentLoads);
        _roads = new RoadChunks(sim.Network, _roadHeat, _lod);
        AddChild(_roads.Root);

        _vehicles = new VehicleLayer(this, _types, _lod) { Info = _info };
        _buildings = new BuildingLayer(this, _lod); // здания V8 + VS5

        if (sim.HasPeds)
        {
            _cross = new CrosswalkMesh(sim.Network);
            _peds = new PedMesh(this, _lod);
            _pedStatic = new PedChunks(sim.Network, _lod);
            _pedStatic.RefreshHeat(sim.PedEdgeCounts);
            AddChild(_pedStatic.Root);
            AddChild(_cross.Markers);
            AddChild(_cross.Queues);
            if (_lod.Preset != "low" && VisualPreset.PedestrianShadowsHigh)
            {
                _peds.SetLod0Shadows(true);
            }

        }

        _signals = new BoxBatch("Signals", new Vector3(0.7f, 0.5f, 3.0f), true);
        _signals.Node.Visible = false;
        AddChild(_signals.Node);
        CollectSignalLanes(net);

        _routeMesh = new ImmediateMesh();
        _routeNode = new MeshInstance3D
        {
            Name = "Route",
            Mesh = _routeMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Palette.RouteLine,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = true,
            },
        };
        AddChild(_routeNode);

        _camera?.Focus(center, MathF.Max(w, h));

        _nodePicker = new NodePicker();
        AddChild(_nodePicker);
        AttachInspector();
    }

    private void OnNodePreset(int node, NodeArms.NodePreset preset)
    {
        // до создания хоста (--node-preset в TryStart) - напрямую в ядро, потом - в кольцо команд хоста (+1 тик лага)
        if (_host != null && _frame != null)
        {
            NetworkView net = _frame.Network.View;
            if (net.IsNodeAlive(node))
            {
                SendCommand(NodeArms.MakePreset(node, net.Nodes[node].Generation, preset));
            }
        }
        else if (_bootSim != null)
        {
            NetworkView net = _bootSim.Network;
            if (net.IsNodeAlive(node))
            {
                SendCommand(NodeArms.MakePreset(node, net.Nodes[node].Generation, preset));
            }
        }
    }

    /// <summary>Для скриншотов (<c>--select-node signal</c>): светофорный узел ближе всего к центру карты, предпочтительно с 4 рукавами.</summary>
    private int CentralSignalNode(Simulation sim)
    {
        NetworkView net = sim.Network;
        float cx = (_mapX0 + _mapX1) * 0.5f;
        float cz = (_mapZ0 + _mapZ1) * 0.5f;
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < net.Nodes.Length; i++)
        {
            ref readonly NodeData n = ref net.Nodes[i];
            if (!net.IsNodeAlive(i) || n.Control != NodeControl.Signal || n.Signal < 0 || n.ArmCount < 4)
            {
                continue;
            }

            float d = ((n.Position.X - cx) * (n.Position.X - cx)) + ((n.Position.Z - cz) * (n.Position.Z - cz));
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }

    private void LogInfo(Simulation sim)
    {
        NetworkView net = sim.Network;
        int active = 0;
        ReadOnlySpan<ushort> counts = sim.PedEdgeCounts;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                active++;
            }
        }

        string firstSig = "none";
        for (int j = 0; j < net.Crossings.Length; j++)
        {
            if ((net.Crossings[j].Flags & 1) != 0)
            {
                Vec2 p = net.PedEdgePoints[net.Crossings[j].FirstPoint];
                firstSig = string.Create(CultureInfo.InvariantCulture, $"({p.X:0.0}, {p.Z:0.0})");
                break;
            }
        }

        string pedAt = "none";
        ReadOnlySpan<PedestrianRenderData> snap = sim.Pedestrians;
        for (int i = 0; i < snap.Length; i++)
        {
            if ((snap[i].Flags & PedRenderFlags.Active) != 0 && (PedState)snap[i].State == PedState.Walking)
            {
                pedAt = string.Create(CultureInfo.InvariantCulture, $"({snap[i].X:0.0}, {snap[i].Z:0.0})");
                break;
            }
        }

        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[info] first walking ped {pedAt}; map x {_mapX0:0}..{_mapX1:0} z {_mapZ0:0}..{_mapZ1:0}; vehicles {sim.ActiveVehicleCount}; peds {sim.ActivePedCount}; snapshot slots {sim.Pedestrians.Length}; sidewalks {net.Sidewalks.Length} crossings {net.Crossings.Length}; edges with peds {active}/{counts.Length}; first signal crossing {firstSig}"));
    }

    /// <summary>Для скриншотов (<c>--select-ped</c>): выбрать первого идущего пешехода, как кликом, и поставить камеру на него, если нет <c>--cam</c>.</summary>
    private void SelectFirstWalkingPed(Simulation sim)
    {
        ReadOnlySpan<PedestrianRenderData> snap = sim.Pedestrians;
        for (int i = 0; i < snap.Length; i++)
        {
            if ((snap[i].Flags & PedRenderFlags.Active) != 0 && (PedState)snap[i].State == PedState.Walking
                && sim.TryPickPedestrian(snap[i].X, snap[i].Z, 0.5f, out PedHandle h))
            {
                _selPed = h;
                _selKind = InspectKind.Pedestrian;
                if (!_hasCamArgs)
                {
                    _hasCamArgs = true;
                    _camCenter = new Vector3(snap[i].X, 0f, snap[i].Z);
                    _camDistance = 40f;
                }

                return;
            }
        }
    }


    /// <summary>
    /// Вид снапшота по камере (общий для машин и пешеходов): ограничивающий прямоугольник проекций углов окна на землю,
    /// с запасом. Почти вся карта в кадре - вид «всё» (дешевле, чем собирать вид).
    /// </summary>
    private void UpdateRenderView(SimThreadHost host, double delta)
    {
        if (_camera == null)
        {
            return;
        }

        Camera3D cam = _camera.Camera;
        Vector2 size = GetViewport().GetVisibleRect().Size;
        float x0 = float.MaxValue;
        float z0 = float.MaxValue;
        float x1 = float.MinValue;
        float z1 = float.MinValue;
        for (int k = 0; k < 4; k++)
        {
            var corner = new Vector2((k & 1) * size.X, (k >> 1) * size.Y);
            Vector3 o = cam.ProjectRayOrigin(corner);
            Vector3 d = cam.ProjectRayNormal(corner);
            float t = d.Y < -1e-3f ? MathF.Min(-o.Y / d.Y, MaxViewRange) : MaxViewRange;
            x0 = MathF.Min(x0, o.X + (d.X * t));
            x1 = MathF.Max(x1, o.X + (d.X * t));
            z0 = MathF.Min(z0, o.Z + (d.Z * t));
            z1 = MathF.Max(z1, o.Z + (d.Z * t));
        }

        float padX = ((x1 - x0) * 0.05f) + 20f;
        float padZ = ((z1 - z0) * 0.05f) + 20f;
        x0 -= padX;
        x1 += padX;
        z0 -= padZ;
        z1 += padZ;

        // render.md §3.4: след фрустума на земле, обрезанный дистанцией точек (круг вокруг проекции камеры; радиус на земле
        // из дистанции по прямой). Машин за ним ядро не пишет, клиент не рисует.
        // --ped-mode agents: пешеходы-точки на всём виде (render.md §10 V5), поэтому обрезка дистанцией точек ТС отключена;
        // ТС дальше своей дистанции слой всё равно не рисует.
        Vector3 cp = cam.GlobalPosition;
        float range = _pedMode == PedMode.Agents && _peds != null ? 1e6f : _lod.PointRange;
        float ground2 = (range * range) - (cp.Y * cp.Y);
        float rg = ground2 > 0f ? MathF.Sqrt(ground2) + 20f : 0f;
        x0 = MathF.Max(x0, cp.X - rg);
        x1 = MathF.Min(x1, cp.X + rg);
        z0 = MathF.Max(z0, cp.Z - rg);
        z1 = MathF.Min(z1, cp.Z + rg);
        if (rg <= 0f || x0 > x1 || z0 > z1)
        {
            // в радиусе точек земли нет: пустой вид (нулевой прямоугольник под камерой), не «всё»
            x0 = x1 = cp.X;
            z0 = z1 = cp.Z;
        }

        _viewX0 = x0;
        _viewZ0 = z0;
        _viewX1 = x1;
        _viewZ1 = z1;

        // ADR-0024 п. 4: вид доходит до ядра и применяется на 2 тика (0.1 с) позже, чем кадр, который его запросил, поэтому края, ползущие наружу
        // (камера едет/зумится), выносим на путь за 2 тика; сам вид для клиента (_viewX0..) остаётся точным. Ядро пишет только слоты в прямоугольнике.
        float mx0 = x0;
        float mz0 = z0;
        float mx1 = x1;
        float mz1 = z1;
        if (_hasPrevView && delta > 1e-4 && !_noViewMargin)
        {
            float k = (float)(2.0 * TickSeconds / delta);
            const float maxMargin = 600f;
            mx0 -= MathF.Min(maxMargin, MathF.Max(0f, _prevVx0 - x0) * k);
            mz0 -= MathF.Min(maxMargin, MathF.Max(0f, _prevVz0 - z0) * k);
            mx1 += MathF.Min(maxMargin, MathF.Max(0f, x1 - _prevVx1) * k);
            mz1 += MathF.Min(maxMargin, MathF.Max(0f, z1 - _prevVz1) * k);
        }

        _prevVx0 = x0;
        _prevVz0 = z0;
        _prevVx1 = x1;
        _prevVz1 = z1;
        _hasPrevView = true;
        bool coversMap = mx0 <= _mapX0 && mx1 >= _mapX1 && mz0 <= _mapZ0 && mz1 >= _mapZ1;
        if (coversMap)
        {
            host.SetRenderViewAll();
        }
        else
        {
            host.SetRenderView(mx0, mz0, mx1, mz1);
        }
    }

    private bool PedsAsAgents(float dist) => _pedMode == PedMode.Agents || (_pedMode == PedMode.Auto && dist <= _lod.PedPointRange);

    private void UpdatePeds(SimFrame f, bool newFrame, float alpha, float dist, double delta)
    {
        if (_peds == null || _cross == null)
        {
            return;
        }

        bool agents = PedsAsAgents(dist);
        _peds.Visible = agents;
        // тротуары/зебры - чанки с visibility_range; режим пешеходов - только uniform шейдера (heatmap в шейдере, меши не трогаем)
        _pedStatic?.SetForce(_pedMode switch { PedMode.Heat => 1f, PedMode.Agents => -1f, _ => 0f });
        bool near = agents && dist <= MarkerNearDistance;
        _cross.Markers.Visible = near;
        _cross.Queues.Visible = agents;

        if (agents)
        {
            _peds.Update(f.Pedestrians, f.PreviousPedestrians, alpha, _camera!.Camera.GlobalPosition, _pedMode == PedMode.Agents, _selPed.IsNull ? -1 : _selPed.Index, (ushort)_selPed.Generation);
            _cross.UpdateQueues(f.BusStopWaiting);
            if (near)
            {
                _cross.UpdateMarkers(f.CrossingStates, _viewX0, _viewZ0, _viewX1, _viewZ1);
            }
        }

        if (newFrame && f.PedEdgeCountsFresh)
        {
            // heatmap пешеходов: копия раз в 10 тиков (2 Гц), обновляется только текстура данных
            _pedStatic?.RefreshHeat(f.PedEdgeCounts);
        }

        _pedHeatShown = !agents;
    }

    private string PedHudText(SimFrame f)
    {
        if (_peds == null || _cross == null)
        {
            return string.Empty;
        }

        _stopWaitCache = _cross.StopWaitingTotal;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\npeds {f.ActivePedCount}/{f.PedTarget}  mode {_pedMode}{(_pedHeatShown ? " (heat)" : string.Empty)}\nin view: walk {_peds.CountOf(PedState.Walking)}  at crossing {_peds.CountOf(PedState.AtCrossing)}  crossing {_peds.CountOf(PedState.Crossing)}  at stop {_peds.CountOf(PedState.AtStop)}\nbus stops waiting {_stopWaitCache}");
    }

    private void UpdateSignals(SimFrame f)
    {
        if (_signals == null)
        {
            return;
        }

        ReadOnlySpan<byte> ls = f.LaneSignals;
        NetworkView net = f.Network.View;
        _signals.Begin(_signalLanes.Length);
        for (int k = 0; k < _signalLanes.Length; k++)
        {
            int li = _signalLanes[k];
            if (li >= ls.Length || ls[li] == (byte)LaneSignal.None || !net.IsLaneAlive(li))
            {
                continue;
            }

            LaneData lane = net.Lanes[li];
            Vec2 end = net.LanePoints[lane.FirstPoint + lane.PointCount - 1];
            Vec2 before = net.LanePoints[lane.FirstPoint + lane.PointCount - 2];
            float dx = end.X - before.X;
            float dz = end.Z - before.Z;
            float len = MathF.Sqrt((dx * dx) + (dz * dz));
            if (len < 1e-4f)
            {
                continue;
            }

            dx /= len;
            dz /= len;
            Color col = ls[li] switch
            {
                (byte)LaneSignal.Green => _signalGreen,
                (byte)LaneSignal.Yellow => _signalYellow,
                _ => _signalRed,
            };
            _signals.Add(end.X - (dx * 0.6f), 0.35f, end.Z - (dz * 0.6f), MathF.Atan2(dz, dx), col);
        }

        _signals.End();
    }

    private void UpdateRoute(SimFrame f)
    {
        if (_routeMesh == null)
        {
            return;
        }

        _routeDirty = false;
        _routeTick = f.SnapshotTick;
        _routeMesh.ClearSurfaces();
        // Ядро отдаёт окно маршрута (routing.md 9.5): ≤ 8 сегментов вперёд; хвост плана наружу не выходит, лента короче полного пути.
        // Окно дозаполняется ядром и сдвигает Cursor - лента перерисовывается каждый снапшот, поэтому конец ленты «растёт» вместе с машиной.
        // Окно маршрута приходит копией в кадре (ADR-0024 п. 3).
        ReadOnlySpan<RouteStep> steps = f.RouteSteps;
        if (!_routeShown || steps.IsEmpty || !f.RouteVehicle.Equals(_selVeh))
        {
            return;
        }

        NetworkView net = f.Network.View;
        bool havePrev = false;
        float px = 0f;
        float pz = 0f;
        const float half = 1.0f;
        const float y = 0.7f;
        bool open = false;
        for (int s = Math.Max(f.RouteCursor, 0); s < steps.Length; s++)
        {
            int li = steps[s].Lane;
            if (!net.IsLaneAlive(li))
            {
                havePrev = false; // снесённая полоса: ленту через дыру не тянем
                continue;
            }

            LaneData lane = net.Lanes[li];
            for (int k = 0; k < lane.PointCount; k++)
            {
                Vec2 p = net.LanePoints[lane.FirstPoint + k];
                if (havePrev)
                {
                    float dx = p.X - px;
                    float dz = p.Z - pz;
                    float len = MathF.Sqrt((dx * dx) + (dz * dz));
                    if (len < 0.05f)
                    {
                        continue;
                    }

                    if (!open)
                    {
                        _routeMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
                        open = true;
                    }

                    float nx = -dz / len * half;
                    float nz = dx / len * half;
                    var a0 = new Vector3(px + nx, y, pz + nz);
                    var a1 = new Vector3(px - nx, y, pz - nz);
                    var b0 = new Vector3(p.X + nx, y, p.Z + nz);
                    var b1 = new Vector3(p.X - nx, y, p.Z - nz);
                    _routeMesh.SurfaceAddVertex(a0);
                    _routeMesh.SurfaceAddVertex(a1);
                    _routeMesh.SurfaceAddVertex(b0);
                    _routeMesh.SurfaceAddVertex(a1);
                    _routeMesh.SurfaceAddVertex(b1);
                    _routeMesh.SurfaceAddVertex(b0);
                }

                px = p.X;
                pz = p.Z;
                havePrev = true;
            }
        }

        if (open)
        {
            _routeMesh.SurfaceEnd();
        }
    }

    private void SetStatus(string text)
    {
        if (_status != null)
        {
            _status.Text = text;
            _statusError = text.StartsWith("ERROR", StringComparison.Ordinal);
            _status.Visible = _statusError || (_hud?.Visible ?? false);
        }
    }

    /// <summary>Отладочный оверлей (F3): перф-HUD и строка подсказок; ошибка запуска показывается всегда.</summary>
    private void SetDebugOverlay(bool on)
    {
        if (_hud != null)
        {
            _hud.Visible = on;
        }

        if (_status != null)
        {
            _status.Visible = on || _statusError;
        }
    }

    /// <summary>Числа легенды инфо-слоя (UI7): строки легенда пересобирает только при смене значений, так что в кадре 0 аллокаций.</summary>
    private void UpdateLegend(SimFrame f)
    {
        if (_ui == null || !_info.Active)
        {
            return;
        }

        switch (_info.Layer)
        {
            case InfoMode.LayerTraffic when _roadHeat != null:
                _ui.Legend.SetTraffic(_roadHeat.RoadAvgLoad, _roadHeat.RoadP95, f.Stats.StuckVehicles);
                break;
            case InfoMode.LayerPeds when _pedStatic != null:
                _ui.Legend.SetPeds(_pedStatic.Heat.PedTop, _pedStatic.Heat.PedTotal, _pedStatic.Heat.PedEdges);
                break;
            case InfoMode.LayerZones:
                _ui.Legend.SetZones(LotChunks.ZoneCounts[1], LotChunks.ZoneCounts[2], LotChunks.ZoneCounts[3]);
                break;
        }
    }

    private void OnUiInfoChanged(bool on)
    {
        if (_info.Active != on)
        {
            _info.Toggle();
        }

        _host?.RequestFreshHeavyOverlays();
    }

    private void OnUiInfoLayer(int layer)
    {
        _info.SetLayer(layer);
        if (layer == InfoMode.LayerPeds)
        {
            _host?.RequestFreshHeavyOverlays();
        }
    }
}
