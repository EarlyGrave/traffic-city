using System;
using System.Globalization;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>
/// Привязка к мировой сетке <c>module_m</c> (RP10, road-profiles.md §6): чистая функция без Godot-состояния. Концы, узлы и шаг инструментов кратны
/// <see cref="ModuleM"/> от начала сетки (<see cref="OriginX"/>, <see cref="OriginZ"/>). Ядру снап не нужен - команды несут уже округлённые координаты.
/// </summary>
public readonly struct GridSnap
{
    /// <summary><c>module_m</c> без каталога профилей (ядро: <c>CityGenerator.DefaultModuleM</c>, ADR-0030).</summary>
    public const float DefaultModuleM = 8f;

    /// <summary>Линия-блок по умолчанию, клеток: <c>block_m</c> 152 / 8 (RP9).</summary>
    public const int DefaultBlockCells = 19;

    public GridSnap(float moduleM, float originX, float originZ, int blockCells)
    {
        ModuleM = moduleM > 0f ? moduleM : DefaultModuleM;
        OriginX = originX;
        OriginZ = originZ;
        BlockCells = Math.Max(1, blockCells);
    }

    public float ModuleM { get; }

    public float OriginX { get; }

    public float OriginZ { get; }

    /// <summary>Жирная линия сетки каждые столько клеток (<c>block_m / module_m</c>).</summary>
    public int BlockCells { get; }

    /// <summary>
    /// Сетка по <c>NetworkView</c> (RP10b): <c>ModuleMm</c> и начало сетки отдаёт ядро, верно и после Load без дескриптора; legacy - модуль 8 м, начало (0, 0).
    /// Размер блока (<paramref name="blockCells"/>) во вью не входит: это параметр генератора города (<c>City.BlockSize / module</c>), берётся из сценария, если он есть.
    /// </summary>
    public static GridSnap FromView(in NetworkView v, int blockCells = DefaultBlockCells) =>
        new(v.ModuleMm > 0 ? v.ModuleMm / 1000f : DefaultModuleM, v.GridOriginX, v.GridOriginZ, blockCells);

    /// <summary>Клеток в блоке по размеру блока города в метрах (<c>City.BlockSize</c>); без города - <see cref="DefaultBlockCells"/>.</summary>
    public static int BlockCellsOf(in ScenarioDesc d, float moduleM) =>
        d.City.Enabled && d.City.BlockSize > 0f && moduleM > 0f ? (int)MathF.Round(d.City.BlockSize / moduleM) : DefaultBlockCells;

    /// <summary>Ближайший узел сетки (половина клетки - в сторону +).</summary>
    public Vector2 Snap(Vector2 p) => new(
        OriginX + (MathF.Floor(((p.X - OriginX) / ModuleM) + 0.5f) * ModuleM),
        OriginZ + (MathF.Floor(((p.Y - OriginZ) / ModuleM) + 0.5f) * ModuleM));

    /// <summary>Точка для инструмента: снап включён и <c>Shift</c> не удерживается - привязанная, иначе сырая (ui-spec §5.1).</summary>
    public Vector2 Resolve(Vector2 p, bool snapOn, bool shiftHeld) => snapOn && !shiftHeld ? Snap(p) : p;

    /// <summary>Длина шага (протяжка прямой, разбиение), кратная модулю; не меньше одного модуля.</summary>
    public float SnapLength(float lengthM) => MathF.Max(1f, MathF.Floor((lengthM / ModuleM) + 0.5f)) * ModuleM;

    /// <summary>Кратна ли координата модулю от начала сетки (допуск 1 мм).</summary>
    public bool OnGrid(Vector2 p)
    {
        Vector2 s = Snap(p);
        return MathF.Abs(s.X - p.X) < 1e-3f && MathF.Abs(s.Y - p.Y) < 1e-3f;
    }
}

/// <summary>
/// Показ мировой сетки и превью снапа (RP10). Линии рисует шейдер земли (<c>world_grid</c> в world.gdshaderinc) по глобальным uniform'ам
/// <c>grid_on / grid_params / grid_color</c>: 0 новых draw calls. Клавиши (ui-spec §9): <c>G</c> - снап, <c>Shift+G</c> - показ сетки; <c>Shift</c> удержан - снап выключен.
/// Интерактивного инструмента «Дороги» в клиенте пока нет (UI4), поэтому превью - две метки на земле под курсором (сырая и привязанная точка),
/// видимые, пока сетка показана при включённом снапе или задан <c>--snap-demo</c> (при выключенном снапе меток нет, сетка стоит 0 draw calls).
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены
public sealed class WorldGrid
{
    public const float FadeSeconds = 0.25f;

    private readonly Node3D _markers = new() { Name = "SnapPreview", Visible = false };
    private readonly MeshInstance3D _raw;
    private readonly MeshInstance3D _snapped;
    private float _t;
    private float _sentT = -1f;
    private bool _paramsSent;
    private Vector2? _fixedPoint;

    public WorldGrid(Node parent)
    {
        _raw = Marker("SnapRaw", Palette.SnapRaw, 1.6f);
        _snapped = Marker("SnapSnapped", Palette.SnapSnapped, 2.4f);
        _markers.AddChild(_raw);
        _markers.AddChild(_snapped);
        parent.AddChild(_markers);
    }

    public GridSnap Grid { get; private set; } = new(GridSnap.DefaultModuleM, 0f, 0f, GridSnap.DefaultBlockCells);

    /// <summary>Показ сетки (Shift+G).</summary>
    public bool Shown { get; private set; }

    /// <summary>Снап инструментов (G); включён по умолчанию.</summary>
    public bool SnapOn { get; private set; } = true;

    /// <summary>Текст для HUD/журнала.</summary>
    public string StatusText() => string.Create(
        CultureInfo.InvariantCulture,
        $"grid {(Shown ? "on" : "off")}  snap {(SnapOn ? "on" : "off")}  module {Grid.ModuleM:0.##} m  origin ({Grid.OriginX:0.##}, {Grid.OriginZ:0.##})  block {Grid.BlockCells} cells");

    public static void Init()
    {
        RenderingServer.GlobalShaderParameterSet("grid_on", 0f);
        RenderingServer.GlobalShaderParameterSet("grid_color", new Vector3(Palette.ToWorld(Palette.GridLine).R, Palette.ToWorld(Palette.GridLine).G, Palette.ToWorld(Palette.GridLine).B));
    }

    public void Configure(in NetworkView v, in ScenarioDesc d)
    {
        float module = v.ModuleMm > 0 ? v.ModuleMm / 1000f : GridSnap.DefaultModuleM;
        Grid = GridSnap.FromView(in v, GridSnap.BlockCellsOf(in d, module));
        _paramsSent = false;
    }

    public void ToggleShown() => Shown = !Shown;

    public void ToggleSnap() => SnapOn = !SnapOn;

    /// <summary>Мгновенно, без перехода (скриншоты: <c>--grid</c>).</summary>
    public void SetShown(bool shown, bool instant)
    {
        Shown = shown;
        if (instant)
        {
            _t = shown ? 1f : 0f;
        }
    }

    public void SetSnap(bool on) => SnapOn = on;

    /// <summary>Фиксированная точка превью вместо курсора (<c>--snap-demo x z</c>).</summary>
    public void SetFixedPoint(float x, float z) => _fixedPoint = new Vector2(x, z);

    public void Update(double delta, Camera3D? cam, Vector2 mouse, bool shiftHeld)
    {
        float target = Shown ? 1f : 0f;
        float step = (float)delta / FadeSeconds;
        _t = _t < target ? MathF.Min(target, _t + step) : MathF.Max(target, _t - step);
        if (_t != _sentT)
        {
            _sentT = _t;
            RenderingServer.GlobalShaderParameterSet("grid_on", _t * _t * (3f - (2f * _t)));
        }

        if (!_paramsSent)
        {
            _paramsSent = true;
            RenderingServer.GlobalShaderParameterSet("grid_params", new Vector4(Grid.ModuleM, Grid.OriginX, Grid.OriginZ, Grid.BlockCells));
        }

        bool preview = (Shown && SnapOn) || _fixedPoint.HasValue;
        _markers.Visible = preview;
        if (!preview || !TryGround(cam, mouse, out Vector2 p))
        {
            return;
        }

        Vector2 s = Grid.Resolve(p, SnapOn, shiftHeld);
        _raw.Position = new Vector3(p.X, 0.35f, p.Y);
        _snapped.Position = new Vector3(s.X, 0.4f, s.Y);
        _raw.Visible = s != p;
    }

    /// <summary>Журнал для проверки без UI (<c>--snap-demo</c>): сырая точка, привязка, привязка при удержанном Shift, кратность модулю.</summary>
    public string DescribeSnap(float x, float z)
    {
        var p = new Vector2(x, z);
        Vector2 s = Grid.Resolve(p, SnapOn, false);
        Vector2 sh = Grid.Resolve(p, SnapOn, true);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[snap] raw ({p.X:0.###}, {p.Y:0.###}) -> snapped ({s.X:0.###}, {s.Y:0.###}) on_grid {Grid.OnGrid(s)}; shift held -> ({sh.X:0.###}, {sh.Y:0.###}); module {Grid.ModuleM:0.##} origin ({Grid.OriginX:0.##}, {Grid.OriginZ:0.##}); length 61.3 -> {Grid.SnapLength(61.3f):0.##}");
    }

    private static MeshInstance3D Marker(string name, Color color, float size) => new()
    {
        Name = name,
        Mesh = new BoxMesh { Size = new Vector3(size, 0.3f, size) },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = color, NoDepthTest = true },
    };

    private bool TryGround(Camera3D? cam, Vector2 mouse, out Vector2 p)
    {
        if (_fixedPoint.HasValue)
        {
            p = _fixedPoint.Value;
            return true;
        }

        p = default;
        if (cam == null)
        {
            return false;
        }

        Vector3 o = cam.ProjectRayOrigin(mouse);
        Vector3 d = cam.ProjectRayNormal(mouse);
        if (d.Y > -1e-4f)
        {
            return false;
        }

        float t = -o.Y / d.Y;
        p = new Vector2(o.X + (d.X * t), o.Z + (d.Z * t));
        return true;
    }
}
