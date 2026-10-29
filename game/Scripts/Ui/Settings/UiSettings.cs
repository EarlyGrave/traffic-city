using System;
using Godot;

namespace TrafficCity.Ui;

/// <summary>Режим палитры для дальтоников (ui-spec.md §11).</summary>
public enum ColorblindMode
{
    Normal = 0,
    DeuteranopiaProtanopia = 1,
    Tritanopia = 2,
}

/// <summary>
/// Настройки клиента (ui-spec.md §11, UI12): масштаб UI, палитра для дальтоников, непрозрачность панелей, пресет рендера (F4).
/// Хранятся в <c>user://settings.cfg</c> (<see cref="ConfigFile"/>); читаются один раз лениво, пишутся при каждом изменении (редко, не в кадре).
/// Применяет их <see cref="UiRoot.ApplySettings"/>; здесь только данные и диск.
/// </summary>
public sealed class UiSettings
{
    public const string FilePath = "user://settings.cfg";
    public const int AlphaMinPercent = 70;
    public const int AlphaMaxPercent = 100;
    public const int AlphaStepPercent = 5;

    private const string Section = "ui";
    private static UiSettings? _current;

    /// <summary>Масштаб UI, % (50-150, шаг 5).</summary>
    public int ScalePercent { get; set; } = 100;

    public ColorblindMode Colorblind { get; set; } = ColorblindMode.Normal;

    /// <summary>Непрозрачность панелей, % (70-100, шаг 5; по умолчанию 95).</summary>
    public int PanelAlphaPercent { get; set; } = (int)MathF.Round(UiPalette.DefaultPanelAlpha * 100f);

    /// <summary>Пресет рендера: <c>high</c> или <c>low</c> (F4 переключает и сохраняет).</summary>
    public string RenderPreset { get; set; } = "high";

    public float PanelAlpha => PanelAlphaPercent / 100f;

    public static UiSettings Current => _current ??= Load();

    public static int SnapAlpha(int percent)
    {
        int p = Math.Clamp(percent, AlphaMinPercent, AlphaMaxPercent);
        return (int)(MathF.Round(p / (float)AlphaStepPercent) * AlphaStepPercent);
    }

    public static UiSettings Load(string path = FilePath)
    {
        var s = new UiSettings();
        var cfg = new ConfigFile();
        if (cfg.Load(path) == Error.Ok)
        {
            s.ScalePercent = UiScale.Snap((int)cfg.GetValue(Section, "scale_percent", 100));
            int cb = (int)cfg.GetValue(Section, "colorblind", 0);
            s.Colorblind = Enum.IsDefined(typeof(ColorblindMode), cb) ? (ColorblindMode)cb : ColorblindMode.Normal;
            s.PanelAlphaPercent = SnapAlpha((int)cfg.GetValue(Section, "panel_alpha_percent", s.PanelAlphaPercent));
            string preset = (string)cfg.GetValue(Section, "render_preset", "high");
            s.RenderPreset = preset == "low" ? "low" : "high";
        }

        return s;
    }

    public Error Save(string path = FilePath)
    {
        var cfg = new ConfigFile();
        cfg.SetValue(Section, "scale_percent", ScalePercent);
        cfg.SetValue(Section, "colorblind", (int)Colorblind);
        cfg.SetValue(Section, "panel_alpha_percent", PanelAlphaPercent);
        cfg.SetValue(Section, "render_preset", RenderPreset);
        return cfg.Save(path);
    }
}
