using System.Globalization;
using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// <c>--ui-shot файл.png [--ui-shot-frame K]</c>: сохранить кадр окна в его настоящем разрешении на кадре K (по умолчанию 30) и выйти
/// (<c>--write-movie</c> пишет только 1152x648).
/// </summary>
internal sealed class UiShot
{
    private readonly Node _owner;
    private readonly string _path;
    private readonly int _frame;
    private int _frames;

    private UiShot(Node owner, string path, int frame)
    {
        _owner = owner;
        _path = path;
        _frame = frame;
    }

    /// <summary>Снимок по аргументам запуска; null - не просили.</summary>
    public static UiShot? FromArgs(Node owner, string[] args)
    {
        string path = string.Empty;
        int frame = 30;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--ui-shot")
            {
                path = args[++i];
            }
            else if (args[i] == "--ui-shot-frame" && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f))
            {
                frame = f;
                i++;
            }
        }

        return path.Length > 0 ? new UiShot(owner, path, frame) : null;
    }

    /// <summary>Каждый кадр: на кадре K подписаться на конец отрисовки.</summary>
    public void Tick()
    {
        if (++_frames == _frame)
        {
            RenderingServer.FramePostDraw += OnDraw;
        }
    }

    private void OnDraw()
    {
        RenderingServer.FramePostDraw -= OnDraw;
        Image img = _owner.GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(_path);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui-shot] {_path} {img.GetWidth()}x{img.GetHeight()} {err}"));
        _owner.GetTree().Quit();
    }
}

internal static class UiControlExtensions
{
    public static T WithFullRect<T>(this T c)
        where T : Control
    {
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return c;
    }
}
