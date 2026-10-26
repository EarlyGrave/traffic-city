using System;
using System.Globalization;
using System.IO;
using Sim.Headless.Render;
using Xunit;

namespace Sim.Headless.Tests.Render;

public sealed class RenderCompareTests : IDisposable
{
    private const string Budgets = """
        {
          "format_version": 2,
          "kinds": {},
          "presets": {
            "low": { "lod_scale": 0.7, "point_min_px": 2, "scaling_3d": 0.75,
                     "shadow": { "distance_m": 300, "splits": 2, "atlas": 2048 },
                     "frame": { "draw_calls": 1000, "shadow_draw_calls": 250, "primitives": 2000000, "shadow_primitives": 500000, "objects": 1500, "texture_mb": 512, "video_mb": 1536 } }
          }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rendercmp-" + Guid.NewGuid().ToString("N"));

    public RenderCompareTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Stats(string scenario, string pose, string preset, long draw = 100, long prims = 100000, long shadowDraw = 20, long video = 100L << 20, string driver = "opengl3")
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            { "format": 1, "scenario": "{{scenario}}", "pose": "{{pose}}", "driver": "{{driver}}", "method": "gl_compatibility", "resolution": "1280x720",
              "deterministic": {
                "main": { "draw_calls": {{draw}}, "primitives": {{prims}}, "objects": 80 },
                "shadow": { "draw_calls": {{shadowDraw}}, "primitives": 5000, "objects": 10 },
                "memory_bytes": { "video": {{video}}, "texture": 1048576, "buffer": 2097152 },
                "vehicle_layer": { "preset": "{{preset}}" },
                "instances": { "total": 500 } },
              "timing": { "fps": 12.5, "render_gpu_ms": 0 } }
            """);
    }

    private string Write(string sub, string name, string text)
    {
        string d = Path.Combine(_dir, sub);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, name), text);
        return d;
    }

    private string BudgetFile()
    {
        string p = Path.Combine(_dir, "render-budgets.json");
        File.WriteAllText(p, Budgets);
        return p;
    }

    private static (int Code, string Out, string Err) Compare(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = RenderCompare.RunCompare(["render-compare", .. args], o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static int Update(params string[] args) => RenderCompare.RunUpdate(["render-update-baseline", .. args], new StringWriter(), new StringWriter());

    [Fact]
    public void Budget_WithinLimits_Passes()
    {
        string res = Write("r", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low"));
        (int code, string output, _) = Compare("--result", res, "--budget", BudgetFile());
        Assert.Equal(RenderCompare.ExitOk, code);
        Assert.Contains("budget 'low': ok", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_DrawCallsOver_Fails()
    {
        // 990 основной + 20 теней = 1010 > 1000
        string res = Write("r", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low", draw: 990));
        (int code, string output, _) = Compare("--result", res, "--budget", BudgetFile());
        Assert.Equal(RenderCompare.ExitRegression, code);
        Assert.Contains("OVER BUDGET", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_VideoMemoryOver_Fails()
    {
        string res = Write("r", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low", video: 2000L << 20));
        (int code, _, _) = Compare("--result", res, "--budget", BudgetFile());
        Assert.Equal(RenderCompare.ExitRegression, code);
    }

    [Fact]
    public void Baseline_RoundTrip_SameResult_Passes()
    {
        string res = Write("r", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low"));
        string baseFile = Path.Combine(_dir, "base.json");
        Assert.Equal(RenderCompare.ExitOk, Update("--from", res, "--baseline", baseFile));
        (int code, _, _) = Compare("--result", res, "--baseline", baseFile, "--budget", BudgetFile());
        Assert.Equal(RenderCompare.ExitOk, code);
    }

    [Fact]
    public void Baseline_GrowthBeyondTolerance_Fails_AndWithinPasses()
    {
        string good = Write("good", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low"));
        string baseFile = Path.Combine(_dir, "base.json");
        Update("--from", good, "--baseline", baseFile);

        string within = Write("within", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low", draw: 109));
        Assert.Equal(RenderCompare.ExitOk, Compare("--result", within, "--baseline", baseFile).Code);

        string worse = Write("worse", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low", draw: 112));
        (int code, string output, _) = Compare("--result", worse, "--baseline", baseFile);
        Assert.Equal(RenderCompare.ExitRegression, code);
        Assert.Contains("REGRESSION", output, StringComparison.Ordinal);
        Assert.Contains("main.draw_calls", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_ImprovementAndTimingChange_DoNotFail()
    {
        string good = Write("good", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low"));
        string baseFile = Path.Combine(_dir, "base.json");
        Update("--from", good, "--baseline", baseFile);
        string better = Write("better", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low", draw: 50, prims: 10));
        Assert.Equal(RenderCompare.ExitOk, Compare("--result", better, "--baseline", baseFile).Code);
    }

    [Fact]
    public void Baseline_DifferentEnvironment_IsUsageError()
    {
        string a = Write("a", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low"));
        string baseFile = Path.Combine(_dir, "base.json");
        Update("--from", a, "--baseline", baseFile);
        string b = Write("b", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low", driver: "vulkan"));
        (int code, _, string err) = Compare("--result", b, "--baseline", baseFile);
        Assert.Equal(RenderCompare.ExitUsage, code);
        Assert.Contains("environments", err, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBaseline_NoResults_AndBadArgs_AreUsageErrors()
    {
        string res = Write("r", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low"));
        Assert.Equal(RenderCompare.ExitUsage, Compare("--result", res, "--baseline", Path.Combine(_dir, "nope.json")).Code);
        Assert.Equal(RenderCompare.ExitUsage, Compare("--result", Path.Combine(_dir, "empty"), "--budget", BudgetFile()).Code);
        Assert.Equal(RenderCompare.ExitUsage, Compare("--result", res).Code);
    }

    [Fact]
    public void Update_Merge_KeepsOtherKeys()
    {
        string a = Write("a", "stats-s1k-overview-low.json", Stats("s1k", "overview", "low"));
        string baseFile = Path.Combine(_dir, "base.json");
        Update("--from", a, "--baseline", baseFile);
        string b = Write("b", "stats-s1k-mid-low.json", Stats("s1k", "mid", "low"));
        Assert.Equal(RenderCompare.ExitOk, Update("--from", b, "--baseline", baseFile, "--merge"));
        string text = File.ReadAllText(baseFile);
        Assert.Contains("s1k/overview/low", text, StringComparison.Ordinal);
        Assert.Contains("s1k/mid/low", text, StringComparison.Ordinal);
    }
}
