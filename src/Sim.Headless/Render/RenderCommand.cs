using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Headless.Scenarios;

namespace Sim.Headless.Render;

/// <summary>
/// <c>Sim.Headless render --scenario X --seconds N --out file.mp4 [...]</c>: видео прогресса симуляции без Godot.
/// Использует только публичное API ядра (Simulation, снапшоты, NetworkView).
/// </summary>
public static class RenderCommand
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "Usage: Sim.Headless render --scenario <name|path> --seconds <game seconds> (--out <file.mp4> | --frames-dir <dir>)\n" +
        "  [--fps 20] [--speed 1] [--width 1280] [--height 1280] [--view x0,z0,x1,z1] [--warmup-s N] [--scenarios-dir <dir>]\n";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!RenderOptions.Parse(args, out RenderOptions opt, out string perr))
        {
            error.WriteLine($"render: {perr}");
            error.Write(Usage);
            return ExitUsage;
        }

        try
        {
            if (opt.Out != null && !FfmpegSink.IsAvailable())
            {
                error.WriteLine("render: ffmpeg not found in PATH (install ffmpeg, or use --frames-dir for PPM frames)");
                return ExitUsage;
            }

            string path = ScenarioLocator.Resolve(opt.Scenario, opt.ScenariosDir);
            LoadedScenario sc = ScenarioLoader.LoadInfo(path);
            if (opt.Out != null)
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(opt.Out));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }

            Render(opt, sc, output);
            return ExitOk;
        }
        catch (ScenarioException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }
        catch (ArgumentException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitUsage;
        }
        catch (RenderException e)
        {
            error.WriteLine($"error: {e.Message}");
            return ExitFailure;
        }
    }

    private static void Render(RenderOptions opt, LoadedScenario sc, TextWriter output)
    {
        using Simulation sim = Simulation.Create(sc.Desc);
        var renderer = new FrameRenderer(sim, sc.Desc.VehicleTypes, opt.Width, opt.Height, opt.View);

        int warmupTicks = (int)Math.Round(opt.WarmupSeconds * Simulation.TicksPerSecond);
        if (warmupTicks > 0)
        {
            sim.Step(warmupTicks);
        }

        long baseTick = sim.Tick;
        int frames = opt.FrameCount;
        double ticksPerFrame = opt.Speed * Simulation.TicksPerSecond / opt.Fps;

        using IFrameSink sink = opt.Out != null
            ? new FfmpegSink(opt.Out, opt.Width, opt.Height, opt.Fps)
            : new PpmSink(opt.FramesDir!, opt.Width, opt.Height);

        var sw = Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            double tickF = f * ticksPerFrame;
            long n = (long)Math.Floor(tickF);
            float alpha = (float)(tickF - n);

            // Нужны снапшоты n и n+1 (относительно baseTick): Previous = n, Current = n+1.
            long need = baseTick + n + 1 - sim.Tick;
            if (need > 0)
            {
                sim.Step((int)need);
            }

            double simSeconds = (baseTick + tickF) / Simulation.TicksPerSecond;
            string hud = FrameRenderer.FormatHud(sim, simSeconds);
            sink.Write(renderer.Render(sim, alpha, hud));
        }

        sink.Finish();
        sw.Stop();
        double secs = Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
        string target = opt.Out ?? opt.FramesDir!;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"rendered {frames} frames ({opt.Width}x{opt.Height}, {opt.Fps} fps, {opt.Seconds:0.##} game s, speed x{opt.Speed:0.##}) -> {target}"));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  wall time {secs:0.00} s, {frames / secs:0.0} frames/s, vehicles at end {sim.ActiveVehicleCount}, tick {sim.Tick}"));
    }
}
