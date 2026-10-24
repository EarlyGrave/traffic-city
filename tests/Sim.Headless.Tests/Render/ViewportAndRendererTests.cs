using System;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Render;
using Sim.Headless.Scenarios;

namespace Sim.Headless.Tests.Render;

public class ViewportAndRendererTests
{
    private static readonly string[] _noScenario = { "render", "--seconds", "5" };
    private static readonly float[] _view400 = { 0f, 0f, 400f, 400f };

    [Fact]
    public void Viewport_SquareView_MapsCornersAndCenter()
    {
        var v = new Viewport(0f, 0f, 100f, 100f, 200, 200);
        Assert.Equal(2f, v.Scale);
        Assert.Equal(0f, v.ToScreenX(0f));
        Assert.Equal(200f, v.ToScreenX(100f));
        Assert.Equal(100f, v.ToScreenY(50f));
    }

    [Fact]
    public void Viewport_NonSquareView_KeepsAspectAndCenters()
    {
        var v = new Viewport(-50f, 10f, 50f, 30f, 200, 200); // 100 x 20 m -> scale 2, letterboxed vertically
        Assert.Equal(2f, v.Scale);
        Assert.Equal(100f, v.ToScreenX(0f));
        Assert.Equal(100f, v.ToScreenY(20f)); // центр вида - центр кадра
        Assert.Equal(80f, v.ToScreenY(10f));
        Assert.Equal(120f, v.ToScreenY(30f));
    }

    [Fact]
    public void LerpAngle_TakesShortestWay()
    {
        float mid = FrameRenderer.LerpAngle(3.0f, -3.0f, 0.5f); // через ±pi, а не через 0
        Assert.True(MathF.Abs(MathF.Abs(mid) - MathF.PI) < 0.2f, $"mid={mid}");
        Assert.Equal(1.5f, FrameRenderer.LerpAngle(1f, 2f, 0.5f), 4);
    }

    private static Simulation DemoSim(out LoadedScenario sc)
    {
        string path = ScenarioLocator.Resolve("demo-grid", null);
        sc = ScenarioLoader.LoadInfo(path);
        return Simulation.Create(sc.Desc);
    }

    private static VehicleRenderData Car(int index, ushort gen, float x, float z, float heading, byte flags = (byte)VehicleRenderFlags.Active) =>
        new() { Index = index, Generation = gen, X = x, Z = z, Heading = heading, Type = 0, Flags = flags };

    [Fact]
    public void DrawVehicles_InterpolatesByIndexAndGeneration()
    {
        using Simulation sim = DemoSim(out LoadedScenario sc);
        var r = new FrameRenderer(sim, sc.Desc.VehicleTypes, 400, 400, _view400);
        float s = r.View.Scale;
        Assert.Equal(1f, s);

        // Машина 0: поколение совпадает - позиция интерполируется 100 -> 200 (alpha 0.5 -> 150).
        // Машина 1: поколение другое (слот переиспользован) - рисуется только Current.
        VehicleRenderData[] prev = { Car(0, 1, 100f, 330f, 0f), Car(1, 1, 50f, 360f, 0f) };
        VehicleRenderData[] cur = { Car(0, 1, 200f, 330f, 0f), Car(1, 2, 300f, 360f, 0f) };
        r.Canvas.Clear(new Rgb(0, 0, 0));
        r.DrawVehicles(cur, prev, 0.5f);

        Rgb car = FrameRenderer.CarColor;
        Assert.Equal(car, r.Canvas.GetPixel(150, 330));
        Assert.Equal(new Rgb(0, 0, 0), r.Canvas.GetPixel(100, 330));
        Assert.Equal(new Rgb(0, 0, 0), r.Canvas.GetPixel(200, 330));
        Assert.Equal(car, r.Canvas.GetPixel(300, 360));
        Assert.Equal(new Rgb(0, 0, 0), r.Canvas.GetPixel(50, 360));
    }

    [Fact]
    public void DrawVehicles_StuckIsRed_BusIsOrange_InactiveSkipped()
    {
        using Simulation sim = DemoSim(out LoadedScenario sc);
        var r = new FrameRenderer(sim, sc.Desc.VehicleTypes, 400, 400, _view400);
        VehicleRenderData bus = Car(1, 1, 200f, 340f, 0f);
        bus.Type = 1;
        VehicleRenderData[] cur =
        {
            Car(0, 1, 100f, 340f, 0f, (byte)(VehicleRenderFlags.Active | VehicleRenderFlags.Stuck)),
            bus,
            Car(2, 1, 300f, 340f, 0f, 0),
        };
        r.Canvas.Clear(new Rgb(0, 0, 0));
        r.DrawVehicles(cur, default, 0f);
        Assert.Equal(FrameRenderer.StuckColor, r.Canvas.GetPixel(100, 340));
        Assert.Equal(FrameRenderer.BusColor, r.Canvas.GetPixel(200, 340));
        Assert.Equal(new Rgb(0, 0, 0), r.Canvas.GetPixel(300, 340));
    }

    [Fact]
    public void Render_ProducesFullFrame_AndNetworkIsDrawn()
    {
        using Simulation sim = DemoSim(out LoadedScenario sc);
        var r = new FrameRenderer(sim, sc.Desc.VehicleTypes, 320, 320, null);
        sim.Step(1);
        byte[] rgb = r.Render(sim, 0f, "TEST");
        Assert.Equal(320 * 320 * 3, rgb.Length);
        int lit = 0;
        for (int i = 0; i < rgb.Length; i += 3)
        {
            if (rgb[i] != FrameRenderer.Background.R || rgb[i + 1] != FrameRenderer.Background.G || rgb[i + 2] != FrameRenderer.Background.B)
            {
                lit++;
            }
        }

        Assert.True(lit > 500, $"only {lit} non-background pixels");
    }

    [Fact]
    public void Command_FramesDir_WritesValidPpmFrames()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tc-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int code = RenderCommand.Run(
                new[] { "render", "--scenario", "demo-grid", "--seconds", "0.5", "--fps", "10", "--width", "64", "--height", "48", "--frames-dir", dir },
                o,
                e);
            Assert.True(code == 0, e.ToString());
            string[] files = Directory.GetFiles(dir, "*.ppm");
            Assert.Equal(5, files.Length);
            byte[] data = File.ReadAllBytes(Path.Combine(dir, "frame_000000.ppm"));
            string header = "P6\n64 48\n255\n";
            Assert.Equal(header.Length + (64 * 48 * 3), data.Length);
            Assert.Equal(header, System.Text.Encoding.ASCII.GetString(data, 0, header.Length));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void Command_BadArgs_ReturnsUsageExitCode()
    {
        var e = new StringWriter();
        Assert.Equal(RenderCommand.ExitUsage, RenderCommand.Run(_noScenario, new StringWriter(), e));
        Assert.Contains("--scenario", e.ToString());
    }

    [Fact]
    public void Command_UnknownScenario_ReturnsUsageExitCode()
    {
        var e = new StringWriter();
        int code = RenderCommand.Run(new[] { "render", "--scenario", "no-such-scenario", "--seconds", "1", "--frames-dir", Path.GetTempPath() + "tc-never" }, new StringWriter(), e);
        Assert.Equal(RenderCommand.ExitUsage, code);
    }
}
