using Godot;

namespace TrafficCity;

/// <summary>
/// ВСЕ визуальные параметры света, неба и постобработки - в одном месте (docs/design/render.md §6, §11 В3-В5).
/// Значения - нейтральный плейсхолдер (дневной направленный свет, простое процедурное небо); вид за владельцем:
/// менять только числа ниже, остальной код (<see cref="RenderPreset"/>) их не дублирует.
/// Технические параметры пресетов (сплиты, дальность и атлас теней, scaling_3d) берутся из <c>content/render-budgets.json</c>.
/// </summary>
public static class VisualPreset
{
    // --- Солнце (В3) ---

    /// <summary>Высота солнца над горизонтом, градусы.</summary>
    public const float SunElevationDeg = 55f;

    /// <summary>Азимут солнца (поворот вокруг Y), градусы.</summary>
    public const float SunAzimuthDeg = 30f;

    /// <summary>Подобрано так, чтобы горизонтальная поверхность с альбедо 1 в сумме с ambient давала ~1 (цвета heatmap не темнеют).</summary>
    public const float SunEnergy = 0.30f;

    // --- Небо и ambient (В3) ---
    public const float AmbientEnergy = 0.18f;

    // --- Туман на краю вида (В4): выключен до решения владельца ---
    public const bool FogEnabled = false;
    public const float FogDensity = 0.0001f;

    // --- Постобработка (В3): SSAO только на high (Forward+), остальное выключено ---
    public const bool SsaoHigh = true;
    public const bool GlowEnabled = false;

    // --- Тени ---

    /// <summary>Тени отбрасывают только объекты с высотой AABB не ниже этой (render.md §6).</summary>
    public const float ShadowMinHeightM = 1f;

    /// <summary>Тени пешеходов на high (В5). Открытый вопрос владельцу: по умолчанию выключены.</summary>
    public const bool PedestrianShadowsHigh = false;

    /// <summary>Мягкость фильтра теней: low - минимальная, high - средняя.</summary>
    public const RenderingServer.ShadowQuality ShadowFilterLow = RenderingServer.ShadowQuality.Hard;

    public const RenderingServer.ShadowQuality ShadowFilterHigh = RenderingServer.ShadowQuality.SoftMedium;

    /// <summary>Смещения теней (плейсхолдер; подбор по виду - владельцу).</summary>
    public const float ShadowBias = 0.05f;

    public const float ShadowNormalBias = 1.0f;

    // цвета неба и света - в Palette.cs (visual-style.md §2); здесь только ссылки, VS10 будет менять их по времени суток
    public static readonly Color SunColor = Palette.Sun;
    public static readonly Color SkyTop = Palette.SkyTop;
    public static readonly Color SkyHorizon = Palette.SkyHorizon;
    public static readonly Color GroundHorizon = Palette.SkyGroundHorizon;
    public static readonly Color GroundBottom = Palette.SkyGroundBottom;
    public static readonly Color AmbientColor = Palette.Ambient;
    public static readonly Color FogColor = Palette.Fog;

    /// <summary>Строит окружение (небо, ambient, туман, SSAO/glow) для пресета.</summary>
    public static Godot.Environment BuildEnvironment(bool high)
    {
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = SkyTop,
                SkyHorizonColor = SkyHorizon,
                GroundHorizonColor = GroundHorizon,
                GroundBottomColor = GroundBottom,
                SunAngleMax = 30f,
            },
            RadianceSize = Sky.RadianceSizeEnum.Size64, // отражения не нужны: освещение ambient задаётся цветом
            ProcessMode = Sky.ProcessModeEnum.Quality,
        };
        return new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = AmbientColor,
            AmbientLightEnergy = AmbientEnergy,
            FogEnabled = FogEnabled,
            FogLightColor = FogColor,
            FogDensity = FogDensity,
            SsaoEnabled = high && SsaoHigh,
            GlowEnabled = GlowEnabled,
            SsrEnabled = false,
        };
    }
}
