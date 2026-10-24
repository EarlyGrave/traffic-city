using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Sim.Headless.Render;

/// <summary>Ошибка рендера с понятным текстом для пользователя.</summary>
public sealed class RenderException : Exception
{
    public RenderException(string message)
        : base(message)
    {
    }
}

/// <summary>Приёмник готовых RGB24-кадров.</summary>
public interface IFrameSink : IDisposable
{
    void Write(byte[] rgb);

    /// <summary>Завершает вывод (закрывает пайп, ждёт ffmpeg). Бросает <see cref="RenderException"/> при сбое.</summary>
    void Finish();
}

/// <summary>Кадры - в <c>ffmpeg</c> через stdin (rawvideo rgb24 -> libx264 yuv420p).</summary>
public sealed class FfmpegSink : IFrameSink
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly StringBuilder _stderr = new();
    private bool _finished;

    public FfmpegSink(string outPath, int width, int height, int fps, string ffmpeg = "ffmpeg")
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string a in new[]
        {
            "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
            "-s", $"{width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}",
            "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "-",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-crf", "23", "-movflags", "+faststart", outPath,
        })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            _proc = Process.Start(psi) ?? throw new RenderException("failed to start ffmpeg");
        }
        catch (Win32Exception e)
        {
            throw new RenderException($"ffmpeg not found or not executable ('{ffmpeg}'): {e.Message}. Install ffmpeg or use --frames-dir.");
        }

        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(e.Data);
                }
            }
        };
        _proc.BeginErrorReadLine();
        _proc.OutputDataReceived += (_, _) => { };
        _proc.BeginOutputReadLine();
        _stdin = _proc.StandardInput.BaseStream;
    }

    public static bool IsAvailable(string ffmpeg = "ffmpeg")
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p == null)
            {
                return false;
            }

            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public void Write(byte[] rgb)
    {
        try
        {
            _stdin.Write(rgb, 0, rgb.Length);
        }
        catch (IOException)
        {
            throw new RenderException("ffmpeg closed the pipe: " + Stderr());
        }
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            _stdin.Flush();
            _stdin.Dispose();
        }
        catch (IOException)
        {
            // ffmpeg уже упал - сообщим по коду выхода
        }

        _proc.WaitForExit();
        if (_proc.ExitCode != 0)
        {
            throw new RenderException($"ffmpeg exited with code {_proc.ExitCode}: {Stderr()}");
        }
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            try
            {
                _stdin.Dispose();
                if (!_proc.WaitForExit(5000))
                {
                    _proc.Kill();
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }

        _proc.Dispose();
    }

    private string Stderr()
    {
        _proc.WaitForExit(2000);
        lock (_stderr)
        {
            return _stderr.ToString().Trim();
        }
    }
}

/// <summary>Кадры - в файлы <c>frame_000000.ppm</c> (P6) в каталоге.</summary>
public sealed class PpmSink : IFrameSink
{
    private readonly string _dir;
    private readonly byte[] _header;
    private int _index;

    public PpmSink(string dir, int width, int height)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _header = Encoding.ASCII.GetBytes($"P6\n{width.ToString(CultureInfo.InvariantCulture)} {height.ToString(CultureInfo.InvariantCulture)}\n255\n");
    }

    public void Write(byte[] rgb)
    {
        string path = Path.Combine(_dir, $"frame_{_index.ToString("D6", CultureInfo.InvariantCulture)}.ppm");
        _index++;
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        f.Write(_header, 0, _header.Length);
        f.Write(rgb, 0, rgb.Length);
    }

    public void Finish()
    {
    }

    public void Dispose()
    {
    }
}
