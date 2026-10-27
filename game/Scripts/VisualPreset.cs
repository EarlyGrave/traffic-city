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

    // --- День/ночь только светом (VS10, visual-style.md §2): ключевые точки интерполируются по часу суток ---

    /// <summary>Свет и небо на момент суток. Все цвета - sRGB (свойства Light3D/Environment); <see cref="Tint"/> - множитель unshaded-слоёв.</summary>
    public readonly record struct TimeLight(
        float ElevationDeg,
        float AzimuthDeg,
        Color SunColor,
        float SunEnergy,
        Color AmbientColor,
        float AmbientEnergy,
        Color SkyTop,
        Color SkyHorizon,
        Color GroundHorizon,
        Color GroundBottom,
        Vector3 Tint);

    /// <summary>Ниже этой энергии направленный свет теней не отбрасывает (ночь).</summary>
    public const float MinShadowEnergy = 0.12f;

    /// <summary>Час суток по умолчанию (без <c>--time-of-day</c>): полдень - вид как до VS10.</summary>
    public const float DefaultHour = 12f;

    // Ночь (солнце выключено, "луна" - слабый холодный направленный свет), общий для обеих ночных ключей.
    private static TimeLight Night() => new(
        38f, 200f, Palette.Srgb("#9DB4E8"), 0.05f,
        Palette.Srgb("#5A6B8C"), 0.35f,
        Palette.Srgb("#0B1228"), Palette.Srgb("#1B2640"), Palette.Srgb("#10161F"), Palette.Srgb("#080B10"),
        new Vector3(0.27f, 0.30f, 0.40f));

    // Ключи (часы): ночь-конец, рассвет, полдень, закат, ночь-начало. Полдень = значения констант выше (палитра).
    private static readonly float[] _keyHours = { 4.5f, 6.5f, 12f, 19f, 21.5f };

    private static readonly TimeLight[] _keys =
    {
        Night(),
        new(7f, 110f, Palette.Srgb("#FFC9A0"), 0.40f, Palette.Srgb("#B8C2E0"), 0.20f,
            Palette.Srgb("#6E8FC4"), Palette.Srgb("#F2C9A8"), Palette.Srgb("#8C8584"), Palette.Srgb("#3C3C42"),
            new Vector3(0.80f, 0.72f, 0.70f)),
        new(SunElevationDeg, SunAzimuthDeg, Palette.Sun, SunEnergy, Palette.Ambient, AmbientEnergy,
            Palette.SkyTop, Palette.SkyHorizon, Palette.SkyGroundHorizon, Palette.SkyGroundBottom,
            Vector3.One),
        new(12f, -50f, Palette.Srgb("#FFB070"), 0.55f, Palette.Srgb("#C4B4D8"), 0.20f,
            Palette.Srgb("#5C7FB8"), Palette.Srgb("#F6B58A"), Palette.Srgb("#8C8280"), Palette.Srgb("#3C3A40"),
            new Vector3(0.85f, 0.65f, 0.55f)),
        Night(),
    };

    /// <summary>Свет и небо на час суток <paramref name="hour"/> (0..24, циклично): линейная интерполяция между ключами.</summary>
    public static TimeLight Light(float hour)
    {
        float h = hour % 24f;
        if (h < 0f)
        {
            h += 24f;
        }

        if (h < _keyHours[0])
        {
            h += 24f; // ночь до первого ключа: тот же отрезок "закат-ночь-рассвет", ночные ключи совпадают
        }

        if (h >= _keyHours[^1])
        {
            return _keys[^1]; // плато ночи: 21.5 -> 4.5 следующих суток
        }

        int i = 0;
        while (h >= _keyHours[i + 1])
        {
            i++;
        }

        float t = (h - _keyHours[i]) / (_keyHours[i + 1] - _keyHours[i]);
        TimeLight a = _keys[i];
        TimeLight b = _keys[i + 1];
        return new TimeLight(
            Mathf.Lerp(a.ElevationDeg, b.ElevationDeg, t),
            Mathf.Lerp(a.AzimuthDeg, b.AzimuthDeg, t),
            a.SunColor.Lerp(b.SunColor, t),
            Mathf.Lerp(a.SunEnergy, b.SunEnergy, t),
            a.AmbientColor.Lerp(b.AmbientColor, t),
            Mathf.Lerp(a.AmbientEnergy, b.AmbientEnergy, t),
            a.SkyTop.Lerp(b.SkyTop, t),
            a.SkyHorizon.Lerp(b.SkyHorizon, t),
            a.GroundHorizon.Lerp(b.GroundHorizon, t),
            a.GroundBottom.Lerp(b.GroundBottom, t),
            a.Tint.Lerp(b.Tint, t));
    }

    /// <summary>Применить свет суток: окружение (небо, ambient), солнце/луна и глобальный <c>light_tint</c>. Только меняет значения, объекты не создаёт.</summary>
    public static void ApplyTime(Godot.Environment? env, DirectionalLight3D? sun, float hour)
    {
        TimeLight l = Light(hour);
        if (env != null)
        {
            env.AmbientLightColor = l.AmbientColor;
            env.AmbientLightEnergy = l.AmbientEnergy;
            env.FogLightColor = l.SkyHorizon;
            if (env.Sky?.SkyMaterial is ProceduralSkyMaterial m)
            {
                m.SkyTopColor = l.SkyTop;
                m.SkyHorizonColor = l.SkyHorizon;
                m.GroundHorizonColor = l.GroundHorizon;
                m.GroundBottomColor = l.GroundBottom;
            }
        }

        if (sun != null)
        {
            sun.RotationDegrees = new Vector3(-l.ElevationDeg, l.AzimuthDeg, 0f);
            sun.LightColor = l.SunColor;
            sun.LightEnergy = l.SunEnergy;
            sun.ShadowEnabled = l.SunEnergy >= MinShadowEnergy; // ночью "луна" без теней
        }

        RenderingServer.GlobalShaderParameterSet("light_tint", l.Tint);
    }

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
