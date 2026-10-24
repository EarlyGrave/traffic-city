using System;
using System.Globalization;

namespace Sim.Headless.Render;

/// <summary>Параметры <c>render</c> (см. <see cref="Parse"/>).</summary>
public sealed class RenderOptions
{
    public string Scenario { get; set; } = "";

    public string? ScenariosDir { get; set; }

    /// <summary>Длительность в игровых секундах.</summary>
    public double Seconds { get; set; }

    public int Fps { get; set; } = 20;

    /// <summary>Игровых секунд на секунду видео.</summary>
    public double Speed { get; set; } = 1.0;

    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 1280;

    /// <summary>x0, z0, x1, z1 в метрах; null = вся сеть.</summary>
    public float[]? View { get; set; }

    /// <summary>Игровых секунд прогона до первого кадра.</summary>
    public double WarmupSeconds { get; set; }

    public string? Out { get; set; }

    public string? FramesDir { get; set; }

    /// <summary>Число кадров видео: <c>floor(seconds * fps / speed)</c>, не менее 1.</summary>
    public int FrameCount => Math.Max(1, (int)Math.Floor((Seconds * Fps / Speed) + 1e-9));

    /// <summary>
    /// Разбор <c>render --scenario X --seconds N [...]</c>; <c>args[0]</c> - имя команды. false + <paramref name="error"/> при ошибке.
    /// </summary>
    public static bool Parse(string[] args, out RenderOptions options, out string error)
    {
        options = new RenderOptions();
        error = "";
        bool haveSeconds = false;
        for (int i = 1; i < args.Length; i++)
        {
            string name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unexpected argument '{name}'";
                return false;
            }

            if (i + 1 >= args.Length || (args[i + 1].StartsWith("--", StringComparison.Ordinal) && !IsNumber(args[i + 1])))
            {
                error = $"option '{name}' requires a value";
                return false;
            }

            string v = args[++i];
            switch (name)
            {
                case "--scenario":
                    options.Scenario = v;
                    break;
                case "--scenarios-dir":
                    options.ScenariosDir = v;
                    break;
                case "--out":
                    options.Out = v;
                    break;
                case "--frames-dir":
                    options.FramesDir = v;
                    break;
                case "--seconds":
                    if (!TryDouble(name, v, out double sec, out error))
                    {
                        return false;
                    }

                    options.Seconds = sec;
                    haveSeconds = true;
                    break;
                case "--speed":
                    if (!TryDouble(name, v, out double spd, out error))
                    {
                        return false;
                    }

                    options.Speed = spd;
                    break;
                case "--warmup-s":
                    if (!TryDouble(name, v, out double warm, out error))
                    {
                        return false;
                    }

                    options.WarmupSeconds = warm;
                    break;
                case "--fps":
                    if (!TryInt(name, v, out int fps, out error))
                    {
                        return false;
                    }

                    options.Fps = fps;
                    break;
                case "--width":
                    if (!TryInt(name, v, out int w, out error))
                    {
                        return false;
                    }

                    options.Width = w;
                    break;
                case "--height":
                    if (!TryInt(name, v, out int h, out error))
                    {
                        return false;
                    }

                    options.Height = h;
                    break;
                case "--view":
                    if (!TryView(v, out float[]? view, out error))
                    {
                        return false;
                    }

                    options.View = view;
                    break;
                default:
                    error = $"unknown option '{name}'";
                    return false;
            }
        }

        if (options.Scenario.Length == 0)
        {
            error = "--scenario <name|path> is required";
            return false;
        }

        if (!haveSeconds)
        {
            error = "--seconds <game seconds> is required";
            return false;
        }

        if (options.Out == null && options.FramesDir == null)
        {
            error = "--out <file.mp4> (or --frames-dir <dir>) is required";
            return false;
        }

        if (!(options.Seconds > 0.0))
        {
            error = "--seconds must be > 0";
            return false;
        }

        if (!(options.Speed > 0.0))
        {
            error = "--speed must be > 0";
            return false;
        }

        if (options.WarmupSeconds < 0.0)
        {
            error = "--warmup-s must be >= 0";
            return false;
        }

        if (options.Fps < 1 || options.Fps > 240)
        {
            error = "--fps must be in 1..240";
            return false;
        }

        if (options.Width < 16 || options.Height < 16 || options.Width > 8192 || options.Height > 8192)
        {
            error = "--width/--height must be in 16..8192";
            return false;
        }

        if ((options.Width & 1) != 0 || (options.Height & 1) != 0)
        {
            error = "--width and --height must be even (yuv420p)";
            return false;
        }

        return true;
    }

    private static bool IsNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static bool TryDouble(string name, string v, out double value, out string error)
    {
        error = "";
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value))
        {
            return true;
        }

        error = $"option '{name}': '{v}' is not a number";
        return false;
    }

    private static bool TryInt(string name, string v, out int value, out string error)
    {
        error = "";
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        error = $"option '{name}': '{v}' is not an integer";
        return false;
    }

    private static bool TryView(string v, out float[]? view, out string error)
    {
        view = null;
        error = "";
        string[] parts = v.Split(',');
        var r = new float[4];
        if (parts.Length != 4)
        {
            error = "--view expects x0,z0,x1,z1 (meters)";
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]) || !float.IsFinite(r[i]))
            {
                error = $"--view: '{parts[i]}' is not a number";
                return false;
            }
        }

        if (!(r[2] > r[0]) || !(r[3] > r[1]))
        {
            error = "--view: need x1 > x0 and z1 > z0";
            return false;
        }

        view = r;
        return true;
    }
}
