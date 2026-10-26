using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;

namespace TrafficCity;

/// <summary>
/// Замеры рендера (docs/design/render.md §8): счётчики основного прохода и теней (<c>Viewport.GetRenderInfo</c>),
/// память (<c>Performance</c>), свои счётчики инстансов по <c>MultiMeshInstance3D</c> сцены, время (справочно).
/// Режим <c>--render-stats файл.json [--stats-frame K]</c>: на кадре K записать JSON и выйти.
/// Детерминированные поля лежат в <c>deterministic</c>, нестабильные (время, FPS) - в <c>timing</c>.
/// </summary>
public partial class RenderStats : Node
{
    private const int FormatVersion = 1;
    private const int DefaultStatsFrame = 10;
    private const int WarmupFrames = 8; // первые кадры - JIT/ленивая инициализация, не стационар

    private string _outPath = string.Empty;
    private int _statsFrame = DefaultStatsFrame;
    private bool _capturing;
    private bool _done;
    private Rid _viewportRid;

    /// <summary>Описание запуска для JSON (заполняет SimHost).</summary>
    public string Scenario { get; set; } = string.Empty;

    public string Pose { get; set; } = string.Empty;

    public int WarmupTicks { get; set; }

    public string ExtraArgs { get; set; } = string.Empty;

    /// <summary>Режим записи JSON включён (<c>--render-stats</c>).</summary>
    public bool Recording => _outPath.Length > 0;

    // Управляемые аллокации главного потока (GC.GetAllocatedBytesForCurrentThread), V3: за кадр, без Step; Step - отдельно.
    private long _viewAllocLast;
    private long _layerAllocLast;
    private long _frameAllocMax;
    private long _stepAllocMax;
    private long _stepAllocTotal;
    private long _stepCount;
    private int _frames;
    private int[] _placed = new int[LodConfig.Levels];
    private int[] _desired = new int[LodConfig.Levels];
    private int[] _caps = new int[LodConfig.Levels];
    private float _maxDrawnDist;
    private float _pointRange;
    private int _dropped;
    private int _buildsAtFirstFrame;
    private string _preset = string.Empty;

    /// <summary>Записать итоги кадра слоя ТС: аллокации <c>SetRenderView</c>/обновления слоя и счётчики по уровням.</summary>
    public void RecordFrame(VehicleLayer? layer, long viewAlloc, long layerAlloc)
    {
        _frames++;
        if (_frames == 1)
        {
            _buildsAtFirstFrame = ChunkStats.SurfaceBuilds;
        }

        _viewAllocLast = viewAlloc;
        _layerAllocLast = layerAlloc;
        if (_frames > WarmupFrames)
        {
            _frameAllocMax = Math.Max(_frameAllocMax, viewAlloc + layerAlloc);
        }

        if (layer != null)
        {
            layer.Placed.CopyTo(_placed);
            layer.Desired.CopyTo(_desired);
            layer.Config.Caps.CopyTo(_caps, 0);
            _maxDrawnDist = layer.MaxDrawnDistance;
            _pointRange = layer.Config.PointRange;
            _dropped = layer.DroppedByCap;
            _preset = layer.Config.Preset;
        }
    }

    /// <summary>Записать аллокации одного <c>Simulation.Step</c> (байты на главном потоке).</summary>
    public void RecordStep(long bytes)
    {
        _stepCount++;
        _stepAllocTotal += bytes;
        if (_frames > WarmupFrames)
        {
            _stepAllocMax = Math.Max(_stepAllocMax, bytes);
        }
    }

    /// <summary>Строки для HUD F3: draw calls / примитивы / объекты (основной проход / тени) и память.</summary>
    public string HudText { get; private set; } = string.Empty;

    /// <summary>Разобрать <c>--render-stats</c> и <c>--stats-frame</c>; true, если аргумент обработан (потреблено <paramref name="consumed"/> следующих).</summary>
    public bool TryParseArg(string[] args, int i, out int consumed)
    {
        consumed = 0;
        if (args[i] == "--render-stats" && i + 1 < args.Length)
        {
            _outPath = args[i + 1];
            consumed = 1;
            return true;
        }

        if (args[i] == "--stats-frame" && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && k >= 2)
            {
                _statsFrame = k;
            }

            consumed = 1;
            return true;
        }

        return false;
    }

    public override void _Ready()
    {
        _viewportRid = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true); // GPU-время: осмысленно только на реальном GPU
    }

    public override void _Process(double delta)
    {
        if (_done || !Recording || _capturing || (long)Engine.GetProcessFrames() < _statsFrame)
        {
            return;
        }

        // GetRenderInfo отдаёт кадр, дорисованный к FramePostDraw: ждём конца отрисовки текущего кадра.
        _capturing = true;
        RenderingServer.FramePostDraw += OnFramePostDraw;
    }

    /// <summary>Обновить строки HUD (дёшево: чтение счётчиков прошлого кадра).</summary>
    public void RefreshHud()
    {
        Viewport vp = GetViewport();
        long dc = Info(vp, Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame);
        long pr = Info(vp, Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame);
        long ob = Info(vp, Viewport.RenderInfoType.Visible, Viewport.RenderInfo.ObjectsInFrame);
        long sdc = Info(vp, Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame);
        long spr = Info(vp, Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame);
        long sob = Info(vp, Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame);
        double mb = 1.0 / 1048576.0;
        HudText = string.Create(
            CultureInfo.InvariantCulture,
            $"draw {dc}  prims {pr}  objs {ob}  (main)\ndraw {sdc}  prims {spr}  objs {sob}  (shadow)\nmem MB: video {Mon(Performance.Monitor.RenderVideoMemUsed) * mb:0.0}  tex {Mon(Performance.Monitor.RenderTextureMemUsed) * mb:0.0}  buf {Mon(Performance.Monitor.RenderBufferMemUsed) * mb:0.0}");
    }

    private static long Info(Viewport vp, Viewport.RenderInfoType type, Viewport.RenderInfo what) => (long)vp.GetRenderInfo(type, what);

    private static long Mon(Performance.Monitor m) => (long)Performance.GetMonitor(m);

    private void OnFramePostDraw()
    {
        RenderingServer.FramePostDraw -= OnFramePostDraw;
        if (_done)
        {
            return;
        }

        _done = true;
        try
        {
            byte[] json = BuildJson(GetViewport());
            string dir = Path.GetDirectoryName(Path.GetFullPath(_outPath)) ?? ".";
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(_outPath, json);
            GD.Print("render-stats: " + _outPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushError("render-stats: " + e.Message);
        }

        GetTree().Quit();
    }

    private byte[] BuildJson(Viewport vp)
    {
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            j.WriteStartObject();
            j.WriteNumber("format", FormatVersion);
            j.WriteString("scenario", Scenario);
            j.WriteString("pose", Pose);
            j.WriteNumber("warmup_ticks", WarmupTicks);
            j.WriteNumber("stats_frame", _statsFrame);
            j.WriteNumber("process_frame", (long)Engine.GetProcessFrames());
            j.WriteString("args", ExtraArgs);
            j.WriteString("driver", RenderingServer.GetCurrentRenderingDriverName());
            j.WriteString("method", RenderingServer.GetCurrentRenderingMethod());
            Vector2 size = vp.GetVisibleRect().Size;
            j.WriteString("resolution", string.Create(CultureInfo.InvariantCulture, $"{(int)size.X}x{(int)size.Y}"));

            j.WriteStartObject("deterministic");
            WritePass(j, "main", "Viewport.GetRenderInfo(Visible)", vp, Viewport.RenderInfoType.Visible);
            WritePass(j, "shadow", "Viewport.GetRenderInfo(Shadow)", vp, Viewport.RenderInfoType.Shadow);
            j.WriteStartObject("total");
            j.WriteString("source", "Performance.RenderTotal*InFrame");
            j.WriteNumber("draw_calls", Mon(Performance.Monitor.RenderTotalDrawCallsInFrame));
            j.WriteNumber("primitives", Mon(Performance.Monitor.RenderTotalPrimitivesInFrame));
            j.WriteNumber("objects", Mon(Performance.Monitor.RenderTotalObjectsInFrame));
            j.WriteEndObject();
            j.WriteStartObject("memory_bytes");
            j.WriteString("source", "Performance.Render*MemUsed");
            j.WriteNumber("video", Mon(Performance.Monitor.RenderVideoMemUsed));
            j.WriteNumber("texture", Mon(Performance.Monitor.RenderTextureMemUsed));
            j.WriteNumber("buffer", Mon(Performance.Monitor.RenderBufferMemUsed));
            j.WriteEndObject();
            j.WriteStartObject("vehicle_layer");
            j.WriteString("preset", _preset);
            j.WriteNumber("point_range_m", _pointRange);
            j.WriteNumber("max_drawn_distance_m", Math.Round(_maxDrawnDist, 1));
            j.WriteNumber("dropped_by_cap", _dropped);
            string[] names = ["lod0", "lod1", "lod2", "points"];
            for (int k = 0; k < LodConfig.Levels; k++)
            {
                j.WriteStartObject(names[k]);
                j.WriteNumber("placed", _placed[k]);
                j.WriteNumber("wanted", _desired[k]);
                j.WriteNumber("cap", _caps[k]);
                j.WriteEndObject();
            }

            j.WriteEndObject();
            j.WriteStartObject("instances");
            long total = 0;
            WalkInstances(GetParent(), j, ref total);
            j.WriteNumber("total", total);
            j.WriteEndObject();
            j.WriteEndObject();

            j.WriteStartObject("static_chunks");
            j.WriteString("note", "V4: меши чанков собираются при старте; surface_builds_after_first_frame должен быть 0 без изменений сети");
            j.WriteNumber("chunk_nodes", ChunkStats.Chunks);
            j.WriteNumber("surface_builds_total", ChunkStats.SurfaceBuilds);
            j.WriteNumber("surface_builds_after_first_frame", ChunkStats.SurfaceBuilds - _buildsAtFirstFrame);
            j.WriteEndObject();

            j.WriteStartObject("managed_alloc");
            j.WriteString("note", "GC.GetAllocatedBytesForCurrentThread, главный поток; max - по кадрам после прогрева (8 кадров: JIT, первое выделение буферов)");
            j.WriteNumber("set_render_view_last_frame_bytes", _viewAllocLast);
            j.WriteNumber("vehicle_layer_last_frame_bytes", _layerAllocLast);
            j.WriteNumber("view_plus_layer_max_bytes", _frameAllocMax);
            j.WriteNumber("step_calls", _stepCount);
            j.WriteNumber("step_max_bytes", _stepAllocMax);
            j.WriteNumber("step_total_bytes", _stepAllocTotal);
            j.WriteEndObject();

            j.WriteStartObject("timing");
            j.WriteString("note", "не детерминировано: llvmpipe, шум облака; GPU-время осмысленно только на реальном GPU");
            j.WriteNumber("fps", Engine.GetFramesPerSecond());
            j.WriteNumber("time_process_s", Performance.GetMonitor(Performance.Monitor.TimeProcess));
            j.WriteNumber("render_cpu_ms", RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewportRid));
            j.WriteNumber("render_gpu_ms", RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid));
            j.WriteEndObject();
            j.WriteEndObject();
        }

        return ms.ToArray();
    }

    private static void WritePass(Utf8JsonWriter j, string name, string source, Viewport vp, Viewport.RenderInfoType type)
    {
        j.WriteStartObject(name);
        j.WriteString("source", source);
        j.WriteNumber("draw_calls", Info(vp, type, Viewport.RenderInfo.DrawCallsInFrame));
        j.WriteNumber("primitives", Info(vp, type, Viewport.RenderInfo.PrimitivesInFrame));
        j.WriteNumber("objects", Info(vp, type, Viewport.RenderInfo.ObjectsInFrame));
        j.WriteEndObject();
    }

    /// <summary>Свои счётчики: видимые <c>MultiMeshInstance3D</c> сцены, имя батча -> число инстансов и треугольников на инстанс.</summary>
    private static void WalkInstances(Node node, Utf8JsonWriter j, ref long total)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MultiMeshInstance3D { Multimesh: { } mm } mmi && mmi.IsVisibleInTree())
            {
                int n = mm.VisibleInstanceCount < 0 ? mm.InstanceCount : mm.VisibleInstanceCount;
                j.WriteStartObject(mmi.Name.ToString());
                j.WriteNumber("instances", n);
                j.WriteNumber("triangles_per_instance", mm.Mesh == null ? 0 : MeshTriangles(mm.Mesh));
                j.WriteNumber("surfaces", mm.Mesh?.GetSurfaceCount() ?? 0);
                j.WriteEndObject();
                total += n;
            }

            WalkInstances(child, j, ref total);
        }
    }

    private static int MeshTriangles(Mesh mesh)
    {
        int tris = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(s);
            Variant idx = arrays[(int)Mesh.ArrayType.Index];
            tris += idx.VariantType != Variant.Type.Nil
                ? idx.AsInt32Array().Length / 3
                : arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;
        }

        return tris;
    }
}
