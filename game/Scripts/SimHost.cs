using System;
using System.Diagnostics;
using System.Globalization;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Scenarios;

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
    private static readonly Color _signalGreen = new(0.24f, 0.9f, 0.35f);

    private Simulation? _sim;
    private VehicleTypeDesc[] _types = Array.Empty<VehicleTypeDesc>();
    private VehicleLayer? _vehicles;
    private LodConfig _lod = LodConfig.Load(string.Empty, "high");
    private string _renderPreset = "high";
    private string _contentDir = string.Empty;
    private bool _logStepAlloc;
    private RoadChunks? _roads;
    private HeatTexture? _roadHeat;
    private PedChunks? _pedStatic;
    private BoxBatch? _signals;
    private int[] _signalLanes = Array.Empty<int>();
    private MeshInstance3D? _routeNode;
    private ImmediateMesh? _routeMesh;
    private RtsCamera? _camera;
    private PerfHud? _hud;
    private RenderStats? _renderStats;
    private string _pose = string.Empty;
    private Label? _status;
    private Label? _inspector;
    private PedMesh? _peds;
    private CrosswalkMesh? _cross;
    private PedMode _pedMode = PedMode.Auto;
    private PedHandle _selPed;
    private double _pedHeatTimer;
    private double _inspectTimer;
    private bool _pedHeatShown;
    private int _warmupTicks;
    private int _stopWaitCache;
    private bool _hasCamArgs;
    private Vector3 _camCenter;
    private float _camDistance;
    private float _camPitch = 55f;
    private float _camYaw;
    private bool _showHud;
    private bool _hudPending;
    private bool _selectFirstPed;
    private float _viewX0, _viewZ0, _viewX1, _viewZ1;
    private float _mapX0, _mapZ0, _mapX1, _mapZ1;

    private double _accumulator;
    private double _heatTimer;
    private double _statsTimer;
    private bool _paused;
    private float _timeScale = 1f;
    private bool _signalsOn;
    private bool _routeDirty;
    private long _routeTick = -1;
    private SimStats _stats;

    /// <summary>Имя сценария из scenarios/ (без .json) или путь к файлу; аргумент запуска <c>-- --scenario имя</c> имеет приоритет.</summary>
    [Export]
    public string Scenario { get; set; } = "s1k";

    public override void _Ready()
    {
        _camera = GetNodeOrNull<RtsCamera>("../CameraRig");
        _hud = GetNodeOrNull<PerfHud>("../Hud/PerfHud");
        _status = GetNodeOrNull<Label>("../Hud/Status");
        _renderStats = GetNodeOrNull<RenderStats>("../RenderStats");

        string name = Scenario;
        string[] args = OS.GetCmdlineUserArgs();
        ParseArgs(args, ref name);

        if (!TryStart(name, out string error))
        {
            GD.PushError(error);
            SetStatus("ERROR: " + error);
            return;
        }

        if (_pose.Length > 0)
        {
            ApplyNamedPose(_pose);
        }
        else if (_hasCamArgs)
        {
            _camera?.SetPose(_camCenter, _camDistance, _camPitch, _camYaw);
        }

        if (_renderStats != null)
        {
            _renderStats.Scenario = name;
            _renderStats.Pose = _pose.Length > 0 ? _pose : (_hasCamArgs ? "cam" : "default");
            _renderStats.WarmupTicks = _warmupTicks;
            _renderStats.ExtraArgs = ArgsWithoutPaths(args);
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
    /// <c>--warmup-ticks N</c> (прогнать симуляцию до первого кадра), <c>--hud</c> (показать HUD), <c>--ped-mode auto|heat|agents</c>,
    /// <c>--pose overview|mid|close|lod0</c> (фиксированная поза замера), <c>--render-stats файл.json [--stats-frame K]</c>.
    /// </summary>
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
            else if (a == "--render-preset" && i + 1 < args.Length)
            {
                _renderPreset = args[++i] == "low" ? "low" : "high";
            }
            else if (a == "--log-step-alloc")
            {
                _logStepAlloc = true;
            }
            else if (a == "--pose" && i + 1 < args.Length)
            {
                _pose = args[++i];
            }
            else if (_renderStats != null && _renderStats.TryParseArg(args, i, out int used))
            {
                i += used;
            }
            else if (a == "--select-ped")
            {
                _selectFirstPed = true;
            }
            else if (a == "--scenario" && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else if (a == "--warmup-ticks" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], NumberStyles.Integer, inv, out _warmupTicks);
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

    public override void _ExitTree()
    {
        _sim?.Dispose();
        _sim = null;
    }

    public override void _Process(double delta)
    {
        if (_sim == null)
        {
            return;
        }

        if (_hudPending && _hud != null)
        {
            _hud.Visible = true;
            _hudPending = false;
        }

        long allocA = GC.GetAllocatedBytesForCurrentThread();
        UpdateRenderView(_sim);
        long allocView = GC.GetAllocatedBytesForCurrentThread() - allocA;
        if (!_paused)
        {
            _accumulator += delta * _timeScale;
            int steps = 0;
            while (_accumulator >= TickSeconds && steps < MaxStepsPerFrame)
            {
                StepOnce();
                _accumulator -= TickSeconds;
                steps++;
            }

            if (_accumulator >= TickSeconds)
            {
                // не успеваем: отбрасываем накопленное, чтобы не уйти в спираль
                _accumulator = 0;
            }
        }

        float alpha = _paused ? 1f : (float)(_accumulator / TickSeconds);
        float dist = _camera?.Distance ?? 0f;
        long allocB = GC.GetAllocatedBytesForCurrentThread();
        if (_vehicles != null && _camera != null)
        {
            _vehicles.Update(_sim, alpha, _camera.Camera.GlobalPosition);
        }

        long allocLayer = GC.GetAllocatedBytesForCurrentThread() - allocB;
        _renderStats?.RecordFrame(_vehicles, allocView, allocLayer);
        if (_logStepAlloc && allocView + allocLayer > 0)
        {
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[frame-alloc] tick {_sim.Tick} SetRenderView {allocView} B, vehicle layer {allocLayer} B"));
        }
        if (_peds != null && _cross != null)
        {
            UpdatePeds(_sim, alpha, dist, delta);
        }

        if (_signalsOn)
        {
            UpdateSignals(_sim);
        }

        if (_roadHeat != null)
        {
            // heatmap дорог в шейдере: раз в 0.5 с обновляется только текстура данных, мешей не трогаем (render.md §3.5)
            _heatTimer -= delta;
            if (_heatTimer <= 0)
            {
                _heatTimer = HeatTexture.UpdateIntervalS;
                _roadHeat.UpdateRoads(_sim.SegmentLoads);
            }
        }

        if (_routeNode != null && (_routeDirty || _routeTick != _sim.SnapshotTick))
        {
            UpdateRoute(_sim);
        }

        if (_hud != null && _hud.Visible)
        {
            _hud.Agents = _sim.ActiveVehicleCount;
            _statsTimer -= delta;
            if (_statsTimer <= 0)
            {
                _statsTimer = 0.5;
                _sim.GetStats(out _stats);
            }

            _renderStats?.RefreshHud();
            _hud.Extra = string.Create(
                CultureInfo.InvariantCulture,
                $"sim tick {_sim.Tick}  x{_timeScale:0.##}{(_paused ? " PAUSED" : string.Empty)}\navg {_stats.AvgSpeedKmh:0.0} km/h  stuck {_stats.StuckVehicles}")
                + (_vehicles != null ? "\n" + _vehicles.HudText() : string.Empty)
                + PedHudText(_sim)
                + (_renderStats != null && _renderStats.HudText.Length > 0 ? "\n" + _renderStats.HudText : string.Empty);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_sim == null)
        {
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.Keycode)
            {
                case Key.F1:
                    if (_roads != null)
                    {
                        _roads.SetHeat(!_roads.Heat);
                        _heatTimer = 0;
                    }

                    break;
                case Key.F2:
                    _signalsOn = !_signalsOn;
                    if (_signals != null)
                    {
                        _signals.Node.Visible = _signalsOn;
                    }

                    break;
                case Key.F4:
                    // Только render-часть пресета (тени, scaling_3d, MSAA, SSAO); LOD-пороги и лимиты слоёв читаются при старте -
                    // для них перезапуск с --render-preset.
                    _lod = LodConfig.Load(_contentDir, _lod.Preset == "low" ? "high" : "low");
                    RenderPreset.Apply(GetParent(), _lod);
                    GD.Print(RenderPreset.Describe(_lod) + " (F4: LOD/лимиты слоёв - после перезапуска)");
                    break;
                case Key.F3:
                    if (_hud != null)
                    {
                        _hud.Visible = !_hud.Visible;
                    }

                    break;
                case Key.F:
                    _pedMode = _pedMode switch
                    {
                        PedMode.Auto => PedMode.Heat,
                        PedMode.Heat => PedMode.Agents,
                        _ => PedMode.Auto,
                    };
                    _pedHeatTimer = 0;
                    break;
                case Key.Space:
                    _paused = !_paused;
                    break;
                case Key.Equal:
                case Key.Plus:
                case Key.KpAdd:
                    _timeScale = Math.Min(_timeScale * 2f, 16f);
                    break;
                case Key.Minus:
                case Key.KpSubtract:
                    _timeScale = Math.Max(_timeScale / 2f, 0.125f);
                    break;
                case Key.Escape:
                    ClearSelection();
                    break;
                default:
                    break;
            }
        }
        else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            PickVehicleAtMouse();
        }
    }

    private bool TryStart(string nameOrPath, out string error)
    {
        error = string.Empty;
        string path;
        try
        {
            string scenariosDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "scenarios"));
            path = ScenarioLocator.Resolve(nameOrPath, scenariosDir);
        }
        catch (ScenarioException e)
        {
            error = e.Message;
            return false;
        }

        ScenarioDesc desc;
        try
        {
            desc = ScenarioLoader.LoadInfo(path).Desc;
        }
        catch (ScenarioException e)
        {
            error = e.Message;
            return false;
        }

        CreateStatus st = Simulation.TryCreate(in desc, out Simulation? sim);
        if (st != CreateStatus.Ok || sim == null)
        {
            error = "Simulation.Create failed: " + st;
            return false;
        }

        _sim = sim;
        _types = desc.VehicleTypes;
        string contentDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "content"));
        _contentDir = contentDir;
        _lod = LodConfig.Load(contentDir, _renderPreset);
        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[lod] preset {_lod.Preset}: bounds {_lod.Bounds[0]:0}/{_lod.Bounds[1]:0}/{_lod.Bounds[2]:0} m, points to {_lod.Bounds[3]:0} m, caps {_lod.Caps[0]}/{_lod.Caps[1]}/{_lod.Caps[2]}/{_lod.Caps[3]}, min point {_lod.PointMinPx:0.#} px ({_lod.Source})"));
        RenderPreset.Apply(GetParent(), _lod);
        GD.Print(RenderPreset.Describe(_lod));
        BuildScene(sim);
        sim.Step(Math.Max(1, _warmupTicks)); // первый снапшот; Previous == пусто до второго тика
        if (_showHud)
        {
            LogInfo(sim);
        }

        if (_selectFirstPed)
        {
            SelectFirstWalkingPed(sim);
        }
        SetStatus(string.Empty);
        return true;
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
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.10f, 0.13f, 0.11f),
                Roughness = 1f,
                MetallicSpecular = 0f,
            },
        });

        _roadHeat = new HeatTexture(sim.Network.Segments.Length);
        _roadHeat.UpdateRoads(sim.SegmentLoads);
        _roads = new RoadChunks(sim, _roadHeat, _lod);
        AddChild(_roads.Root);

        _vehicles = new VehicleLayer(this, _types, _lod);

        if (sim.HasPeds)
        {
            _cross = new CrosswalkMesh(sim);
            _peds = new PedMesh(this, _lod);
            _pedStatic = new PedChunks(sim, _lod);
            _pedStatic.RefreshHeat(sim);
            AddChild(_pedStatic.Root);
            AddChild(_cross.Markers);
            AddChild(_cross.Queues);
            if (_lod.Preset != "low" && VisualPreset.PedestrianShadowsHigh)
            {
                _peds.SetLod0Shadows(true);
            }

            _inspector = new Label
            {
                Name = "PedInspector",
                Visible = false,
                Position = new Vector2(8f, 290f),
            };
            _inspector.AddThemeColorOverride("font_color", new Color(1f, 1f, 0.85f));
            _inspector.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
            GetNodeOrNull<CanvasLayer>("../Hud")?.AddChild(_inspector);
        }

        _signals = new BoxBatch("Signals", new Vector3(0.7f, 0.5f, 3.0f), true);
        _signals.Node.Visible = false;
        AddChild(_signals.Node);
        int lanes = net.Lanes.Length;
        int[] tmp = new int[lanes];
        int n = 0;
        for (int i = 0; i < lanes; i++)
        {
            if (net.Lanes[i].Kind != LaneKind.Connector && net.Lanes[i].PointCount >= 2)
            {
                tmp[n++] = i;
            }
        }

        _signalLanes = new int[n];
        Array.Copy(tmp, _signalLanes, n);

        _routeMesh = new ImmediateMesh();
        _routeNode = new MeshInstance3D
        {
            Name = "Route",
            Mesh = _routeMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.95f, 0.2f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = true,
            },
        };
        AddChild(_routeNode);

        _camera?.Focus(center, MathF.Max(w, h));
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

    private void StepOnce()
    {
        if (_sim == null)
        {
            return;
        }

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        _sim.Step(1);
        long t1 = Stopwatch.GetTimestamp();
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        _hud?.RecordTick((t1 - t0) * 1000.0 / Stopwatch.Frequency, a1 - a0, 1);
        _renderStats?.RecordStep(a1 - a0);
        if (_logStepAlloc && a1 != a0)
        {
            GD.Print(string.Create(
                CultureInfo.InvariantCulture,
                $"[step-alloc] tick {_sim.Tick} bytes {a1 - a0} view ({_viewX0:0.0},{_viewZ0:0.0})-({_viewX1:0.0},{_viewZ1:0.0})"));
        }
    }

    /// <summary>
    /// Вид снапшота по камере (общий для машин и пешеходов): ограничивающий прямоугольник проекций углов окна на землю,
    /// с запасом. Почти вся карта в кадре - вид «всё» (дешевле, чем собирать вид).
    /// </summary>
    private void UpdateRenderView(Simulation sim)
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
        bool coversMap = x0 <= _mapX0 && x1 >= _mapX1 && z0 <= _mapZ0 && z1 >= _mapZ1;
        if (coversMap)
        {
            sim.SetRenderViewAll();
        }
        else
        {
            sim.SetRenderView(x0, z0, x1, z1);
        }
    }

    private bool PedsAsAgents(float dist) => _pedMode == PedMode.Agents || (_pedMode == PedMode.Auto && dist <= _lod.PedPointRange);

    private void UpdatePeds(Simulation sim, float alpha, float dist, double delta)
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
            _peds.Update(sim, alpha, _camera!.Camera.GlobalPosition, _pedMode == PedMode.Agents, _selPed.IsNull ? -1 : _selPed.Index, (ushort)_selPed.Generation);
            _cross.UpdateQueues(sim);
            if (near)
            {
                _cross.UpdateMarkers(sim, _viewX0, _viewZ0, _viewX1, _viewZ1);
            }
        }

        _pedHeatTimer -= delta;
        if (_pedHeatTimer <= 0)
        {
            _pedHeatTimer = HeatTexture.UpdateIntervalS;
            _pedStatic?.RefreshHeat(sim);
        }

        _pedHeatShown = !agents;
        UpdateInspector(sim, delta);
    }

    private void UpdateInspector(Simulation sim, double delta)
    {
        if (_inspector == null)
        {
            return;
        }

        if (_selPed.IsNull)
        {
            _inspector.Visible = false;
            return;
        }

        _inspectTimer -= delta;
        if (_inspectTimer > 0)
        {
            return;
        }

        _inspectTimer = 0.25;
        if (!sim.TryGetPedInfo(_selPed, out PedInfo info))
        {
            _selPed = PedHandle.Null;
            _inspector.Visible = false;
            return;
        }

        const double dt = 1.0 / Simulation.TicksPerSecond;
        _inspector.Visible = true;
        _inspector.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Pedestrian #{_selPed.Index} (gen {_selPed.Generation})\nstate {info.State}  leg {info.Leg}\nresident {info.Resident}  edge {info.Edge}\nwaiting {info.WaitTicks * dt:0.0} s  trip {info.TripTicks * dt:0.0} s\nsteps left {info.StepsLeft}  at ({info.X:0.0}, {info.Z:0.0})");
    }

    private string PedHudText(Simulation sim)
    {
        if (_peds == null || _cross == null)
        {
            return string.Empty;
        }

        _stopWaitCache = _cross.StopWaitingTotal;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\npeds {sim.ActivePedCount}/{sim.PedTarget}  mode {_pedMode}{(_pedHeatShown ? " (heat)" : string.Empty)}\nin view: walk {_peds.CountOf(PedState.Walking)}  at crossing {_peds.CountOf(PedState.AtCrossing)}  crossing {_peds.CountOf(PedState.Crossing)}  at stop {_peds.CountOf(PedState.AtStop)}\nbus stops waiting {_stopWaitCache}");
    }

    private void UpdateSignals(Simulation sim)
    {
        if (_signals == null)
        {
            return;
        }

        ReadOnlySpan<byte> ls = sim.LaneSignals;
        NetworkView net = sim.Network;
        _signals.Begin(_signalLanes.Length);
        for (int k = 0; k < _signalLanes.Length; k++)
        {
            int li = _signalLanes[k];
            if (li >= ls.Length || ls[li] == (byte)LaneSignal.None)
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

    private void PickVehicleAtMouse()
    {
        if (_sim == null || _camera == null)
        {
            return;
        }

        Camera3D cam = _camera.Camera;
        Vector2 mp = GetViewport().GetMousePosition();
        Vector3 origin = cam.ProjectRayOrigin(mp);
        Vector3 dir = cam.ProjectRayNormal(mp);
        if (dir.Y > -1e-4f)
        {
            return;
        }

        float t = -origin.Y / dir.Y;
        Vector3 hit = origin + (dir * t);
        float radius = MathF.Max(RoutePickMinRadius, origin.DistanceTo(hit) * 0.012f);
        float dist = _camera.Distance;
        if (_peds != null && PedsAsAgents(dist)
            && _sim.TryPickPedestrian(hit.X, hit.Z, MathF.Max(1.5f, origin.DistanceTo(hit) * 0.01f), out PedHandle ph))
        {
            _selPed = ph;
            _inspectTimer = 0;
            _sim.Enqueue(Command.MakeClearSelection());
            _routeDirty = true;
            _routeTick = -1;
            return;
        }

        _selPed = PedHandle.Null;
        if (_sim.TryPickVehicle(hit.X, hit.Z, radius, out VehicleHandle h))
        {
            _sim.Enqueue(Command.MakeSelectVehicle(h));
        }
        else
        {
            _sim.Enqueue(Command.MakeClearSelection());
        }

        _routeDirty = true;
        _routeTick = -1;
    }

    private void ClearSelection()
    {
        _sim?.Enqueue(Command.MakeClearSelection());
        _selPed = PedHandle.Null;
        _routeDirty = true;
    }

    private void UpdateRoute(Simulation sim)
    {
        if (_routeMesh == null)
        {
            return;
        }

        _routeDirty = false;
        _routeTick = sim.SnapshotTick;
        _routeMesh.ClearSurfaces();
        // Ядро отдаёт окно маршрута (routing.md 9.5): ≤ 8 сегментов вперёд; хвост плана наружу не выходит, лента короче полного пути.
        // Окно дозаполняется ядром и сдвигает Cursor - лента перерисовывается каждый снапшот, поэтому конец ленты «растёт» вместе с машиной.
        RouteView route = sim.SelectedRoute;
        if (route.IsEmpty)
        {
            return;
        }

        NetworkView net = sim.Network;
        bool havePrev = false;
        float px = 0f;
        float pz = 0f;
        const float half = 1.0f;
        const float y = 0.7f;
        bool open = false;
        for (int s = Math.Max(route.Cursor, 0); s < route.Steps.Length; s++)
        {
            int li = route.Steps[s].Lane;
            if (li < 0 || li >= net.Lanes.Length)
            {
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
        }
    }
}
