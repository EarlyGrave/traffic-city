using System.Collections.Generic;
using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Иконки кита. <see cref="Glyph"/> — пиктограмма без плашки (<c>icons/toolbar/glyph/</c>): плашку рисует стиль кнопки,
/// поэтому она красится по состоянию. <see cref="Plated"/> — иконка с запечённой плашкой (F1), для мест вне кнопок.
/// Текстуры кэшируются: повторный вызов не грузит и не выделяет.
/// </summary>
public static class UiIcons
{
    public static readonly string[] ToolbarNames =
    {
        "road", "intersection", "zones", "bus", "bulldoze", "layers", "stats", "undo", "redo", "pause", "fast", "settings",
    };

    private static readonly Dictionary<string, Texture2D> _cache = new();

    public static Texture2D Glyph(string name) => Load("res://assets/ui/icons/toolbar/glyph/" + name + ".png");

    public static Texture2D Plated(string name) => Load("res://assets/ui/icons/toolbar/" + name + ".png");

    private static Texture2D Load(string path)
    {
        if (!_cache.TryGetValue(path, out Texture2D? tex))
        {
            tex = GD.Load<Texture2D>(path);
            _cache[path] = tex;
        }

        return tex;
    }
}
