using Godot;

namespace TrafficCity;

/// <summary>
/// ЕДИНСТВЕННОЕ место цветов мира (docs/design/visual-style.md §2). Значения стартовые: подбор по скриншотам - только числами здесь.
/// Два вида полей: <c>Srgb</c>-цвета идут в <c>AlbedoColor</c>/<c>source_color</c>-uniform/окружение (Godot сам переводит в линейное);
/// остальные - цвета вершин и инстансов (<c>COLOR</c> в шейдере, <c>VertexColorUseAsAlbedo</c>), там Godot не конвертирует,
/// поэтому они переведены в линейное пространство при создании (<see cref="Lin"/>).
/// </summary>
public static class Palette
{
    /// <summary>
    /// Compatibility (gl_compatibility) считает освещение прямо в sRGB-пространстве: линейные числа вершин и инстансов там выходили тёмными
    /// (асфальт #8A8884 давал ~#2D2B26, lessons.md RP6). В Forward+/Mobile (рабочий метод проекта) пространство линейное.
    /// Поле объявлено первым: статические инициализаторы цветов ниже зовут <see cref="Lin"/>.
    /// </summary>
    private static readonly bool _srgbWorkspace = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";

    // ---- sRGB: материалы, uniform'ы с source_color, окружение ----
    public static readonly Color Ground = Srgb("#EEEAE2");
    public static readonly Color SkyTop = Srgb("#8EC5F2");
    public static readonly Color SkyHorizon = Srgb("#DCEBF7");
    public static readonly Color SkyGroundHorizon = Srgb("#8C9494");
    public static readonly Color SkyGroundBottom = Srgb("#404545");
    public static readonly Color Fog = Srgb("#DCEBF7");
    public static readonly Color Sun = Srgb("#FFF7E6");
    public static readonly Color Ambient = Srgb("#D9E6FF");

    /// <summary>Линии полос и направляющие коннекторов (VS3a): смешиваются с асфальтом долей <see cref="LaneLineAlpha"/>, то есть читаются слабым тёмным оттенком.</summary>
    /// <summary>Асфальт: единственный цвет дорог на всех дистанциях и LOD (класс дороги цветом не различается; heatmap инфо-режима - поверх).</summary>
    public static readonly Color Asphalt = Srgb("#8A8884");
    public static readonly Color AsphaltVertex = Lin("#8A8884"); // тот же цвет в пространстве вершин (линейном; в Compatibility sRGB, см. _srgbWorkspace) - для вершин дорог

    public static readonly Color LaneLine = Srgb("#E2DED6");
    public const float LaneLineAlpha = 0.30f;

    /// <summary>Осевая (граница направлений) заметнее разделителей полос того же направления.</summary>
    public const float LaneLineCenterAlpha = 0.45f;

    /// <summary>Heatmap дорог: свободно - средне - затор (HeatPalette.cs берёт отсюда).</summary>
    public static readonly Color HeatRoadLow = Srgb("#1AD933");
    public static readonly Color HeatRoadMid = Srgb("#FFE61A");
    public static readonly Color HeatRoadHigh = Srgb("#F21A1A");

    /// <summary>Heatmap пешеходов: мало - средне - много.</summary>
    public static readonly Color HeatPedLow = Srgb("#33B3FF");
    public static readonly Color HeatPedMid = Srgb("#FFE61A");
    public static readonly Color HeatPedHigh = Srgb("#F21A1A");

    // Heatmap для дальтоников (UI12, ui-spec.md §11): без пары красный-зелёный (дейтеранопия/протанопия) и без пары синий-жёлтый (тританопия).
    private static readonly Color HeatCbRoadLow = Srgb("#1B6FD6");
    private static readonly Color HeatCbHigh = Srgb("#7A1F7A");
    private static readonly Color HeatCbPedLow = Srgb("#33B3FF");
    private static readonly Color HeatTriRoadLow = Srgb("#1AD933");
    private static readonly Color HeatTriMid = Srgb("#FFD0D8");
    private static readonly Color HeatTriHigh = Srgb("#B3001B");
    private static readonly Color HeatTriPedLow = Srgb("#33D9B3");

    /// <summary>Тройка heatmap дорог для режима палитры (0 - обычная, 1 - дейтеранопия/протанопия, 2 - тританопия).</summary>
    public static (Color Low, Color Mid, Color High) HeatRoad(TrafficCity.Ui.ColorblindMode mode) => mode switch
    {
        TrafficCity.Ui.ColorblindMode.DeuteranopiaProtanopia => (HeatCbRoadLow, HeatRoadMid, HeatCbHigh),
        TrafficCity.Ui.ColorblindMode.Tritanopia => (HeatTriRoadLow, HeatTriMid, HeatTriHigh),
        _ => (HeatRoadLow, HeatRoadMid, HeatRoadHigh),
    };

    /// <summary>Тройка heatmap пешеходов для режима палитры.</summary>
    public static (Color Low, Color Mid, Color High) HeatPed(TrafficCity.Ui.ColorblindMode mode) => mode switch
    {
        TrafficCity.Ui.ColorblindMode.DeuteranopiaProtanopia => (HeatCbPedLow, HeatRoadMid, HeatCbHigh),
        TrafficCity.Ui.ColorblindMode.Tritanopia => (HeatTriPedLow, HeatTriMid, HeatTriHigh),
        _ => (HeatPedLow, HeatPedMid, HeatPedHigh),
    };

    /// <summary>Тонирование инфо-режима: беж «тёмный» и «светлый» (по яркости исходного цвета), visual-style.md §7.</summary>
    public static readonly Color InfoBgDark = Srgb("#D8CCB4");
    public static readonly Color InfoBgLight = Srgb("#F6F1E6");

    /// <summary>Линии мировой сетки 8 м (RP10): шейдер земли смешивает его с землёй долей alpha (world.gdshaderinc, world_grid).</summary>
    public static readonly Color GridLine = Srgb("#334052");

    /// <summary>Метка превью снапа (RP10): сырая точка курсора / точка после привязки к сетке.</summary>
    public static readonly Color SnapRaw = Srgb("#F26B3A");
    public static readonly Color SnapSnapped = Srgb("#1FB86B");

    // служебные цвета отладочных материалов и UI
    public static readonly Color NodePick = Srgb("#4DCCFF");
    public static readonly Color RouteLine = Srgb("#FFF233");
    public static readonly Color HudText = Srgb("#E6FFE6");
    public static readonly Color InspectorText = Srgb("#FFFFD9");
    public static readonly Color TextShadow = new(0f, 0f, 0f, 0.9f);

    // ---- линейные: цвета вершин и инстансов ----
    public static readonly Color Closing = Lin("#B5766B"); // тон «закрыто» поверх серого асфальта
    public static readonly Color Sidewalk = Lin("#E4E1DA");
    public static readonly Color Zebra = Lin("#FFFFFF");

    /// <summary>Слой земли (VS9, visual-style.md §8): основной вид - спокойные тона, вершинные цвета, линейные.</summary>
    public static readonly Color ParkTone = Lin("#C7D8A6");
    public static readonly Color WaterTone = Lin("#A9CDE8");
    public static readonly Color ShoreTone = Srgb("#EAF3F8"); // светлая кромка пруда и берега (uniform шейдера земли, sRGB)

    /// <summary>Тон квартала по зоне участков (почти незаметный, чтобы не спорить с землёй): жилая, коммерческая, промзона.</summary>
    public static readonly Color LotResidentialTone = Lin("#EFE4CC");
    public static readonly Color LotCommercialTone = Lin("#E4E6EE");
    public static readonly Color LotIndustrialTone = Lin("#E3DDE6");

    /// <summary>Инфо-слой «Зоны» (3): насыщенные цвета поверх беж.</summary>
    public static readonly Color ZoneResidential = Lin("#F2C04A");
    public static readonly Color ZoneCommercial = Lin("#4A8CF2");
    public static readonly Color ZoneIndustrial = Lin("#9A63D6");
    public static readonly Color ZonePark = Lin("#52BD5A");
    public static readonly Color ZoneWater = Lin("#3F9BE6");

    /// <summary>Те же цвета слоя «Зоны» в sRGB - для плашек легенды UI (UI7); держать в паре с <c>Zone*</c> выше.</summary>
    public static readonly Color ZoneResidentialUi = Srgb("#F2C04A");
    public static readonly Color ZoneCommercialUi = Srgb("#4A8CF2");
    public static readonly Color ZoneIndustrialUi = Srgb("#9A63D6");

    // элементы профиля дороги (RP6, road-profiles.md §7); асфальт и тротуар - выше. Вершинные цвета, линейные.
    public static readonly Color Lawn = Lin("#C9D4B2");
    public static readonly Color Curb = Lin("#BDB9B0");
    public static readonly Color Median = Lin("#AEBB98");
    public static readonly Color Reserve = Lin("#7E8A88"); // bike, parking, tram, bus_bay: резерв, пока только ширина

    /// <summary>Стоп-линия (VS3): доля <see cref="LaneLine"/> в смеси с асфальтом, плотнее осевой.</summary>
    public const float StopLineAlpha = 0.60f;

    /// <summary>Легковая: набор по <c>slot % 5</c> (решение владельца 2026-10-27).</summary>
    public static readonly Color[] CarSlots = { Lin("#E5484D"), Lin("#3E7BFA"), Lin("#F4F4F2"), Lin("#3A3F47"), Lin("#F2B33D") };
    public static readonly Color Bus = Lin("#F28C28");
    public static readonly Color CarPoint = Lin("#2F6FE0");
    public static readonly Color BusPoint = Lin("#E07A1F");
    /// <summary>Объём и рёбра ТС (VS2, visual-style.md §3, §5; <c>vehicle.gdshader</c>): ширина ребра, px; дистанции гашения, м; множители заливки.</summary>
    public const float VehicleEdgePx = 1.0f;
    public const float VehicleEdgeFadeBegin = 120f;
    public const float VehicleEdgeFadeEnd = 200f;
    public const float VehicleEdgeK = 0.5f;   // цвет ребра = заливка x K
    public const float VehicleWallShade = 0.9f; // стены x mix(0.9, 1, n.y), крыша - цвет палитры
    public const float VehicleAoMin = 0.8f;    // AO-градиент у земли: mix(0.8, 1, smoothstep(0, VehicleAoHeight, y))
    public const float VehicleAoHeight = 0.8f;

    /// <summary>Здания (VS5, visual-style.md §2): цвет крыши по зоне, стены - светом и face shading; строится / заброшено - без прозрачности.</summary>
    public static readonly Color BuildingResidential = Lin("#EDE3D1");
    public static readonly Color BuildingCommercial = Lin("#E2E5EB");
    public static readonly Color BuildingIndustrial = Lin("#DAD4CA");
    public static readonly Color BuildingConstructing = Lin("#F4F1EA");
    public static readonly Color BuildingAbandoned = Lin("#C8C2B7");

    /// <summary>Разброс яркости зданий по хэшу участка (доля): единая палитра, но ряд одинаковых коробок не сливается в полосу.</summary>
    public const float BuildingToneJitter = 0.04f;

    /// <summary>Рёбра и объём зданий (<c>building.gdshader</c>): ширина ребра, px; гашение 1 000 -> 1 500 м (x <c>lod_scale</c> пресета); цвет ребра = заливка x K.</summary>
    public const float BuildingEdgePx = 1.25f;
    public const float BuildingEdgeFadeBegin = 1000f;
    public const float BuildingEdgeFadeEnd = 1500f;
    public const float BuildingEdgeK = 0.62f;
    public const float BuildingWallShade = 0.9f; // стены x mix(0.9, 1, n.y), крыша - цвет палитры
    public const float BuildingAoMin = 0.72f;    // AO у земли: mix(0.72, 1, smoothstep(0, BuildingAoHeight, y_world))
    public const float BuildingAoHeight = 6f;

    /// <summary>X-ray ближних высоток (VS6): здания от 15 м, чья верхушка ближе min(1.5 x фокус, 200 м), выбрасываются дизерингом (доля 0.75 дистанции - полностью, до 1.0 - плавно).</summary>
    public const float BuildingXrayMinHeight = 15f;
    public const float BuildingXrayMaxDist = 200f;
    public const float BuildingXrayFocusK = 1.5f;
    public const float BuildingXrayBegin = 0.75f;

    public static readonly Color Selected = Lin("#FFFFFF");
    public static readonly Color Stuck = Lin("#FF2E2E"); // только в инфо-слое «Трафик»

    /// <summary>Оверлеи без тонирования в беж: a = 0 (шейдер ТС умножает долю тонирования на a).</summary>
    public static readonly Color StuckOverlay = new(Stuck, 0f);
    public static readonly Color SelectedOverlay = new(Selected, 0f);

    // пешеходы: на светлой земле читаются тёмным (плейсхолдер; прежние значения, кроме «идёт»)
    public static readonly Color PedWalking = Lin("#5B6573");
    public static readonly Color PedAtCrossing = Lin("#FFD933");
    public static readonly Color PedCrossing = Lin("#FF801A");
    public static readonly Color PedAtStop = Lin("#CC66FF");
    public static readonly Color PedPassenger = Lin("#73CCFF");

    // фазы переходов/маркеры
    public static readonly Color SignalDontWalk = Lin("#FF3333");
    public static readonly Color SignalWalk = Lin("#3DE659");
    public static readonly Color SignalClear = Lin("#FFDB26");
    public static readonly Color StopQueue = Lin("#CC66FF");

    // BBCode-теги RichTextLabel панели узла (строка-литерал, поэтому не Color)
    public const string BbWalk = "[color=#3ee65a]";
    public const string BbClear = "[color=#ffdb26]";
    public const string BbStop = "[color=#ff4040]";

    public static Color Srgb(string hex) => Color.FromHtml(hex);

    public static Color Lin(string hex) => ToWorld(Color.FromHtml(hex));

    /// <summary>sRGB-цвет в число для <c>COLOR</c> вершин/инстансов и глобальных uniform: линейное в Forward+/Mobile, исходное в Compatibility.</summary>
    public static Color ToWorld(Color srgb) => _srgbWorkspace ? srgb : srgb.SrgbToLinear();
}
