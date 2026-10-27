using System;
using Godot;

namespace TrafficCity;

/// <summary>
/// Информационный режим (visual-style.md §7): глобальные uniform шейдеров <c>info_mode</c> (0..1, твин 0.25 с), <c>info_layer</c>,
/// <c>light_tint</c>, <c>cam_focus_dist</c> (объявлены в project.godot [shader_globals], общая функция - Shaders/world.gdshaderinc).
/// Ни проходов, ни draw calls: тонирование в беж и оверлей слоя считают сами шейдеры мира.
/// </summary>
public sealed class InfoMode
{
    public const int LayerTraffic = 1;
    public const int LayerPeds = 2;
    public const int LayerZones = 3;

    /// <summary>Длительность перехода, с.</summary>
    public const float FadeSeconds = 0.25f;

    private float _t;
    private float _sentT = -1f;
    private int _sentLayer = -1;
    private float _sentFocus = -1f;

    public bool Active { get; private set; }

    public int Layer { get; private set; } = LayerTraffic;

    /// <summary>Нужны ли данные heatmap (текстуры обновляются, пока режим виден).</summary>
    public bool Visible => Active || _t > 0f;

    /// <summary>stuck-красный у ТС - только в слое «Трафик» (visual-style.md §2).</summary>
    public bool ShowStuck => Active && Layer == LayerTraffic;

    /// <summary>Разовая запись цветов тонирования и нейтрального <c>light_tint</c> (VS10 будет писать его по времени суток).</summary>
    public static void Init()
    {
        RenderingServer.GlobalShaderParameterSet("info_bg_dark", ToVec(Palette.ToWorld(Palette.InfoBgDark)));
        RenderingServer.GlobalShaderParameterSet("info_bg_light", ToVec(Palette.ToWorld(Palette.InfoBgLight)));
        RenderingServer.GlobalShaderParameterSet("light_tint", Vector3.One);
    }

    public void Toggle() => Active = !Active;

    public void SetLayer(int layer)
    {
        Layer = layer;
        Active = true;
    }

    /// <summary>Мгновенно, без перехода (скриншоты: <c>--info</c>).</summary>
    public void Snap(bool active, int layer)
    {
        Active = active;
        Layer = layer;
        _t = active ? 1f : 0f;
    }

    public void Update(double delta, float focusDist)
    {
        float target = Active ? 1f : 0f;
        float step = (float)delta / FadeSeconds;
        _t = _t < target ? MathF.Min(target, _t + step) : MathF.Max(target, _t - step);
        if (_t != _sentT)
        {
            _sentT = _t;
            RenderingServer.GlobalShaderParameterSet("info_mode", _t * _t * (3f - (2f * _t)));
        }

        if (Layer != _sentLayer)
        {
            _sentLayer = Layer;
            RenderingServer.GlobalShaderParameterSet("info_layer", Layer);
        }

        if (MathF.Abs(focusDist - _sentFocus) > 0.01f)
        {
            _sentFocus = focusDist;
            RenderingServer.GlobalShaderParameterSet("cam_focus_dist", focusDist);
        }
    }

    private static Vector3 ToVec(Color c) => new(c.R, c.G, c.B);
}
