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

    private static readonly Color _carColor = new(0.2f, 0.85f, 1f);
    private static readonly Color _busColor = new(1f, 0.63f, 0.12f);
    private static readonly Color _stuckColor = new(1f, 0.18f, 0.18f);
    private static readonly Color _selectedColor = new(1f, 1f, 1f);
    private static readonly Color _signalRed = new(1f, 0.2f, 0.2f);
    private static readonly Color _signalYellow = new(1f, 0.86f, 0.15f);
    private static readonly Color _signalGreen = new(0.24f, 0.9f, 0.35f);

    private Simulation? _sim;
    private VehicleTypeDesc[] _types = Array.Empty<VehicleTypeDesc>();
    private BoxBatch[] _vehicleBatches = Array.Empty<BoxBatch>();
    private int[] _typeCounts = Array.Empty<int>();
    private float[] _typeHeight = Array.Empty<float>();
    private RoadMesh? _roads;
    private BoxBatch? _signals;
    private int[] _signalLanes = Array.Empty<int>();
    private MeshInstance3D? _routeNode;
    private ImmediateMesh? _routeMesh;
    private RtsCamera? _camera;
    private PerfHud? _hud;
    private Label? _status;

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

        string name = Scenario;
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--scenario")
            {
                name = args[i + 1];
            }
        }

        if (!TryStart(name, out string error))
        {
            GD.PushError(error);
            SetStatus("ERROR: " + error);
        }
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
        UpdateVehicles(_sim, alpha);

        if (_signalsOn)
        {
            UpdateSignals(_sim);
        }

        if (_roads != null && _roads.Heat)
        {
            _heatTimer -= delta;
            if (_heatTimer <= 0)
            {
                _heatTimer = 0.5;
                _roads.Refresh(_sim);
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

            _hud.Extra = string.Create(
                CultureInfo.InvariantCulture,
                $"sim tick {_sim.Tick}  x{_timeScale:0.##}{(_paused ? " PAUSED" : string.Empty)}\navg {_stats.AvgSpeedKmh:0.0} km/h  stuck {_stats.StuckVehicles}");
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
                        _roads.SetHeat(!_roads.Heat, _sim);
                    }

                    break;
                case Key.F2:
                    _signalsOn = !_signalsOn;
                    if (_signals != null)
                    {
                        _signals.Node.Visible = _signalsOn;
                    }

                    break;
                case Key.F3:
                    if (_hud != null)
                    {
                        _hud.Visible = !_hud.Visible;
                    }

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
        BuildScene(sim);
        sim.Step(1); // первый снапшот; Previous == пусто до второго тика
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
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });

        _roads = new RoadMesh(sim);
        AddChild(_roads.Instance);

        _vehicleBatches = new BoxBatch[_types.Length];
        _typeHeight = new float[_types.Length];
        _typeCounts = new int[_types.Length];
        for (int t = 0; t < _types.Length; t++)
        {
            VehicleTypeDesc td = _types[t];
            float len = td.Length > 0f ? td.Length : 4.5f;
            float wid = td.Width > 0f ? td.Width : 1.8f;
            float hgt = td.Category == VehicleCategory.Bus ? 3.0f : 1.5f;
            _typeHeight[t] = hgt;
            _vehicleBatches[t] = new BoxBatch("Vehicles_" + t.ToString(CultureInfo.InvariantCulture), new Vector3(len, hgt, wid), false);
            AddChild(_vehicleBatches[t].Node);
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
    }

    private void UpdateVehicles(Simulation sim, float alpha)
    {
        ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
        ReadOnlySpan<VehicleRenderData> prev = sim.PreviousVehicles;
        int types = _vehicleBatches.Length;
        if (types == 0)
        {
            return;
        }

        Array.Clear(_typeCounts);
        for (int i = 0; i < cur.Length; i++)
        {
            if ((cur[i].Flags & (byte)VehicleRenderFlags.Active) != 0)
            {
                _typeCounts[Math.Min((int)cur[i].Type, types - 1)]++;
            }
        }

        for (int t = 0; t < types; t++)
        {
            _vehicleBatches[t].Begin(_typeCounts[t]);
        }

        float a = Math.Clamp(alpha, 0f, 1f);
        for (int i = 0; i < cur.Length; i++)
        {
            ref readonly VehicleRenderData c = ref cur[i];
            if ((c.Flags & (byte)VehicleRenderFlags.Active) == 0)
            {
                continue;
            }

            float x = c.X;
            float z = c.Z;
            float heading = c.Heading;
            if (i < prev.Length)
            {
                ref readonly VehicleRenderData p = ref prev[i];
                if ((p.Flags & (byte)VehicleRenderFlags.Active) != 0 && p.Index == c.Index && p.Generation == c.Generation)
                {
                    x = p.X + ((c.X - p.X) * a);
                    z = p.Z + ((c.Z - p.Z) * a);
                    heading = LerpAngle(p.Heading, c.Heading, a);
                }
            }

            int type = Math.Min((int)c.Type, types - 1);
            _vehicleBatches[type].Add(x, c.Y + (_typeHeight[type] * 0.5f) + 0.05f, z, heading, VehicleColor(c.Flags, _types[type].Category));
        }

        for (int t = 0; t < types; t++)
        {
            _vehicleBatches[t].End();
        }
    }

    private static Color VehicleColor(byte flags, VehicleCategory category)
    {
        if ((flags & (byte)VehicleRenderFlags.Selected) != 0)
        {
            return _selectedColor;
        }

        if ((flags & (byte)VehicleRenderFlags.Stuck) != 0)
        {
            return _stuckColor;
        }

        return category == VehicleCategory.Bus ? _busColor : _carColor;
    }

    private static float LerpAngle(float from, float to, float t)
    {
        float d = to - from;
        while (d > MathF.PI)
        {
            d -= 2f * MathF.PI;
        }

        while (d < -MathF.PI)
        {
            d += 2f * MathF.PI;
        }

        return from + (d * t);
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
