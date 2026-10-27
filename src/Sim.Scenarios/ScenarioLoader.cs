using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Scenarios.Mods;
using ModAssetKind = Sim.Scenarios.Mods.AssetKind;

namespace Sim.Scenarios;

/// <summary>Результат загрузки: desc плюс информативные поля, которых нет в <see cref="ScenarioDesc"/>.</summary>
public sealed class LoadedScenario
{
    public LoadedScenario(
        string name, string[] typeNames, ScenarioDesc desc, ContentCatalog? catalog = null, PackageSet? packages = null,
        IReadOnlyList<TimelineEntry>? timeline = null)
    {
        Timeline = timeline ?? [];
        Catalog = catalog;
        Packages = packages;
        Name = name;
        TypeNames = typeNames;
        Desc = desc;
    }

    public string Name { get; }

    /// <summary>Имена типов ТС; индекс = <c>VehicleType</c>.</summary>
    public string[] TypeNames { get; }

    public ScenarioDesc Desc { get; }

    /// <summary>Таймлайн команд (<c>timeline</c>, modding.md §3.5); пустой, если ключа нет. В <see cref="Desc"/> не входит.</summary>
    public IReadOnlyList<TimelineEntry> Timeline { get; }

    /// <summary>Каталог контента набора пакетов (только <c>format_version: 3</c>, иначе <c>null</c>); индекс типа ТС = индекс в <see cref="ContentCatalog.Vehicles"/>.</summary>
    public ContentCatalog? Catalog { get; }

    /// <summary>Набор пакетов в порядке загрузки (только <c>format_version: 3</c>, иначе <c>null</c>).</summary>
    public PackageSet? Packages { get; }
}

/// <summary>
/// Загрузчик сценариев JSON → <see cref="ScenarioDesc"/> по docs/design/scenario-format.md:
/// дефолты, единицы (км/ч → м/с, с → тики при 20 Гц), строгий контроль ключей, format_version = 2 (сценарий) / 1 (типы ТС).
/// </summary>
public static partial class ScenarioLoader
{
    public const int SupportedFormatVersion = 2;
    public const int PackagesFormatVersion = 3;
    public const int VehicleTypesFormatVersion = 1;
    private const int MaxRoutingLevels = 4;       // = Overlay.MaxLevels ядра
    private const int MaxRoutingFanout = 16;
    private const int MaxLodSlots = 4;           // слотов фокуса (0..3) и закреплений (4..7)
    private const int MaxLodFocusMargin = 64;      // = Overlay.MaxFanout
    private const int MaxArenaSteps = 1 << 28;    // предел арены планов (WorldBuilder.MaxPlanStepsCapacity)
    public const int TicksPerSecond = 20;
    private const string DefaultVehicleTypesFile = "vehicle-types.json";

    private static readonly JsonDocumentOptions _docOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static ScenarioDesc Load(string path) => LoadInfo(path).Desc;

    /// <param name="path">Файл сценария.</param>
    /// <param name="searchDirs">Каталоги пакетов для <c>format_version: 3</c> (modding.md §1.1); <c>null</c> - <c>content/</c> игры, найденный от каталога сценария/текущего.</param>
    /// <param name="overrides">Подмена внешних файлов (каталог профилей дорог, golden-тесты); <c>null</c> - как в сценарии.</param>
    public static LoadedScenario LoadInfo(string path, IReadOnlyList<PackageSearchDir>? searchDirs = null, ScenarioOverrides? overrides = null)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string text = ReadFile(path);
        return Parse(text, rel => ReadFile(Path.Combine(dir, rel)), Path.GetFileNameWithoutExtension(path), searchDirs ?? DefaultSearchDirs(dir), overrides);
    }

    /// <summary>Поиск <c>content/</c> игры: вверх от <paramref name="startDir"/> и от текущего каталога (как <see cref="ScenarioLocator"/> ищет <c>scenarios/</c>).</summary>
    public static IReadOnlyList<PackageSearchDir> DefaultSearchDirs(string startDir)
    {
        string? content = ScenarioLocator.FindContentDir(startDir) ?? ScenarioLocator.FindContentDir(Directory.GetCurrentDirectory());
        return content == null ? [] : [new PackageSearchDir(content, true)];
    }

    /// <summary>Разбор из строк (тесты): <paramref name="vehicleTypesJson"/> подставляется вместо файла типов.</summary>
    public static LoadedScenario Parse(string scenarioJson, string vehicleTypesJson) =>
        Parse(scenarioJson, _ => vehicleTypesJson, "scenario");

    public static LoadedScenario Parse(string scenarioJson, Func<string, string> readRelativeFile, string defaultName) =>
        Parse(scenarioJson, readRelativeFile, defaultName, null);

    /// <summary>Разбор с каталогами пакетов для <c>format_version: 3</c> (<c>null</c> - v3 отвергается).</summary>
    public static LoadedScenario Parse(
        string scenarioJson, Func<string, string> readRelativeFile, string defaultName, IReadOnlyList<PackageSearchDir>? searchDirs, ScenarioOverrides? overrides = null)
    {
        using JsonDocument doc = OpenDoc(scenarioJson, "scenario");
        var root = new JsonObjectReader(doc.RootElement, "$");
        bool v3 = CheckScenarioVersion(root);

        string name = root.Str("name", defaultName);
        ulong seed = root.ULong("seed");
        double durationS = root.Num("duration_s", min: 0);
        VehicleTypeDesc[] types;
        string[] typeNames;
        TypeRefs refs;
        ContentCatalog? catalog = null;
        PackageSet? packages = null;
        if (v3)
        {
            (types, typeNames, refs, catalog, packages) = LoadPackageTypes(root, searchDirs);
        }
        else
        {
            string typesFile = root.Str("vehicle_types_file", DefaultVehicleTypesFile);
            string typesText;
            try
            {
                typesText = readRelativeFile(typesFile);
            }
            catch (ScenarioException e)
            {
                throw new ScenarioException($"$.vehicle_types_file: {e.Message}", e);
            }

            try
            {
                (types, typeNames) = ParseVehicleTypes(typesText);
            }
            catch (ScenarioException e)
            {
                throw new ScenarioException($"{typesFile}: {e.Message}", e);
            }

            refs = TypeRefs.ForNames(typeNames);
        }

        JsonObjectReader? cityR = root.OptObject("city");
        JsonObjectReader? netR = root.OptObject("network");
        if (v3 && cityR != null)
        {
            CheckRoadAssets(cityR, catalog!, packages!);
        }

        RoadProfilesDesc roadProfiles = LoadRoadProfiles(root, readRelativeFile, overrides);
        CityGenDesc city = ParseCity(cityR, roadProfiles);
        NetworkDesc network = default;
        if (netR != null)
        {
            if (city.Enabled)
            {
                throw new ScenarioException("$.network: explicit network requires $.city.enabled = false");
            }

            network = ParseNetwork(netR, roadProfiles);
        }
        else if (!city.Enabled)
        {
            throw new ScenarioException("$.network: required key is missing when $.city.enabled = false");
        }

        JsonObjectReader vehR = root.ReqObject("vehicles");
        int count = vehR.Int("count", min: 0);
        int rampTicks = Ticks(vehR.Num("spawn_ramp_s", 120, min: 0));
        float[] typeMix = ParseTypeMix(vehR, refs);
        vehR.Finish();

        DemandDesc demand = ParseDemand(root.OptObject("demand"));
        demand.TargetVehicles = count;
        demand.SpawnRampTicks = rampTicks;
        demand.TypeMix = typeMix;

        PedDesc peds = ParsePeds(root.OptObject("peds"));
        TransitDesc transit = ParseTransit(root.OptObject("transit"), refs, peds.Enabled);
        if (peds.Enabled && peds.StuckStopTicks <= 0)
        {
            peds.StuckStopTicks = Math.Max(3 * transit.HeadwayTicks, Ticks(900));   // stuck_stop_s: max(3 · headway_s, 900)
        }

        RulesDesc rules = ParseRules(root.OptObject("rules"));
        RoutingDesc routing = ParseRouting(root.OptObject("routing"));
        bool residents = demand.Model == DemandModel.Residents;
        JsonObjectReader? lodR = root.OptObject("lod");
        if (residents && (lodR == null || !lodR.Has("cell_m")))
        {
            throw new ScenarioException("$.lod.cell_m: required with demand.model = \"residents\" (the city grid of skims uses the LOD cell size)");
        }

        LodDesc lod = ParseLod(lodR);
        if (peds.Source == PedSource.City && !residents)
        {
            throw new ScenarioException("$.peds.source: \"city\" requires demand.model = \"residents\"");
        }

        CityDesc town = ParseTown(root, residents, catalog);
        ToolsDesc tools = ParseTools(root.OptObject("tools"));
        SimCapacities caps = ParseCapacities(root.ReqObject("capacities"), routing, peds, residents);
        TimelineEntry[] timeline = Mods.Timeline.Parse(root, new Mods.Timeline.Context
        {
            Types = refs,
            Road = catalog != null ? (reference, path) => ResolveRoad(catalog, packages!, reference, path) : null,
        });
        root.Finish();

        var desc = new ScenarioDesc
        {
            Seed = seed,
            DurationTicks = Ticks(durationS),
            Capacities = caps,
            Routing = routing,
            Lod = lod,
            Peds = peds,
            Town = town,
            Tools = tools,
            City = city,
            Network = network,
            Demand = demand,
            Transit = transit,
            Rules = rules,
            VehicleTypes = types,
            RoadProfiles = roadProfiles,
        };
        return new LoadedScenario(name, typeNames, desc, catalog, packages, timeline);
    }

    // ---------------------------------------------------------------- units

    internal static int Ticks(double seconds) =>
        checked((int)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero));

    internal static float Mps(double kmh) => (float)(kmh / 3.6);

    // ---------------------------------------------------------------- helpers

    private static string ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ScenarioException($"cannot read '{path}': {e.Message}", e);
        }
    }

    private static JsonDocument OpenDoc(string text, string what)
    {
        try
        {
            return JsonDocument.Parse(text, _docOptions);
        }
        catch (JsonException e)
        {
            throw new ScenarioException($"{what}: invalid JSON at line {(e.LineNumber ?? 0) + 1}: {e.Message}", e);
        }
    }

    private static void CheckVersion(JsonObjectReader root, int supported)
    {
        if (!root.Has("format_version"))
        {
            throw new ScenarioException("$.format_version: required key is missing");
        }

        int v = root.Int("format_version");
        if (v != supported)
        {
            throw new ScenarioException(
                $"$.format_version: unsupported version {v}, loader supports {supported}");
        }
    }

    private static bool CheckScenarioVersion(JsonObjectReader root)
    {
        if (!root.Has("format_version"))
        {
            throw new ScenarioException("$.format_version: required key is missing");
        }

        int v = root.Int("format_version");
        if (v is not (SupportedFormatVersion or PackagesFormatVersion))
        {
            throw new ScenarioException(
                $"$.format_version: unsupported version {v}, loader supports {SupportedFormatVersion} and {PackagesFormatVersion}");
        }

        return v == PackagesFormatVersion;
    }

    // ---------------------------------------------------------------- packages (format_version 3, modding.md §3.4, §5)

    private static (VehicleTypeDesc[] Types, string[] Names, TypeRefs Refs, ContentCatalog Catalog, PackageSet Packages) LoadPackageTypes(
        JsonObjectReader root, IReadOnlyList<PackageSearchDir>? searchDirs)
    {
        if (root.Has("vehicle_types_file"))
        {
            throw new ScenarioException("$.vehicle_types_file: unknown key in format_version 3 (use $.packages)", ModError.UnknownKey);
        }

        JsonElement[] list = root.ReqArray("packages");
        var ids = new List<string>(list.Length);
        string path = root.ChildPath("packages");
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].ValueKind != JsonValueKind.String)
            {
                throw new ScenarioException($"{JsonObjectReader.Index(path, i)}: expected string", ModError.BadType);
            }

            string id = list[i].GetString()!;
            if (ids.Contains(id))
            {
                throw new ScenarioException($"{JsonObjectReader.Index(path, i)}: duplicate package '{id}'", ModError.BadValue);
            }

            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            throw new ScenarioException($"{path}: at least one package is required", ModError.BadValue);
        }

        if (searchDirs == null || searchDirs.Count == 0)
        {
            throw new ScenarioException($"{path}: no package directories (content/ not found)", ModError.MissingDependency);
        }

        PackageSet set = PackageLoader.Load(searchDirs, ids);
        set.ThrowIfFailed();
        var issues = new List<ModIssue>();
        ContentCatalog cat = ContentCatalog.Build(set, issues);
        if (issues.Count > 0)
        {
            throw new ScenarioException(issues[0].ToString(), issues[0].Code);
        }

        if (cat.Vehicles.Count == 0)
        {
            throw new ScenarioException($"{path}: package set has no vehicle types", ModError.BadValue);
        }

        var types = new VehicleTypeDesc[cat.Vehicles.Count];
        var names = new string[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = cat.Vehicles[i].Desc;
            names[i] = cat.Vehicles[i].Id.Name;
        }

        return (types, names, TypeRefs.ForCatalog(cat, set), cat, set);
    }

    /// <summary>
    /// Ссылка из сценария (modding.md §2): полный id - пакет обязан быть в наборе (<c>packages</c> + транзитивные),
    /// иначе <c>UndeclaredDependency</c>; сокращение <c>vehicle/car</c> - из <c>base</c>.
    /// </summary>
    private static bool ExpandRef(PackageSet set, string reference, List<AssetId> candidates, out ModError error)
    {
        error = default;
        candidates.Clear();
        if (AssetId.TryParse(reference, out AssetId full))
        {
            if (set.Find(full.Package) == null)
            {
                error = ModError.UndeclaredDependency;
                return false;
            }

            candidates.Add(full);
            return true;
        }

        if (AssetId.TryParseShort(reference, out ModAssetKind kind, out string name))
        {
            candidates.Add(new AssetId("base", kind, name));
            return true;
        }

        error = ModError.BadValue;
        return false;
    }

    private static RoadEntry ResolveRoad(ContentCatalog cat, PackageSet set, string reference, string path)
    {
        var cands = new List<AssetId>();
        if (!ExpandRef(set, reference, cands, out ModError err))
        {
            throw new ScenarioException($"{path}: {err} ('{reference}')", err);
        }

        foreach (AssetId c in cands)
        {
            int idx = c.Kind == ModAssetKind.Road ? cat.FindRoad(c) : -1;
            if (idx >= 0)
            {
                return cat.Roads[idx];
            }
        }

        throw new ScenarioException($"{path}: {ModError.UnknownAssetRef} ('{reference}')", ModError.UnknownAssetRef);
    }

    // city.road_assets: визуальная метка сегментов генератора; до M4 только проверяется (modding.md §3.4).
    private static void CheckRoadAssets(JsonObjectReader cityR, ContentCatalog cat, PackageSet set)
    {
        JsonObjectReader? ra = cityR.OptObject("road_assets");
        if (ra == null)
        {
            return;
        }

        foreach (string key in ra.Keys)
        {
            string p = ra.ChildPath(key);
            RoadKind expected = key switch
            {
                "street" => RoadKind.Street,
                "arterial" => RoadKind.Arterial,
                _ => throw new ScenarioException($"{p}: unknown key", ModError.UnknownKey),
            };
            JsonElement el = ra.Raw(key);
            if (el.ValueKind != JsonValueKind.String)
            {
                throw new ScenarioException($"{p}: expected string", ModError.BadType);
            }

            string reference = el.GetString()!;
            var cands = new List<AssetId>();
            if (!ExpandRef(set, reference, cands, out ModError err))
            {
                throw new ScenarioException($"{p}: {err} ('{reference}')", err);
            }

            int idx = -1;
            foreach (AssetId c in cands)
            {
                if (c.Kind == ModAssetKind.Road && (idx = cat.FindRoad(c)) >= 0)
                {
                    break;
                }
            }

            if (idx < 0)
            {
                throw new ScenarioException($"{p}: {ModError.UnknownAssetRef} ('{reference}')", ModError.UnknownAssetRef);
            }

            if (cat.Roads[idx].Kind != expected)
            {
                throw new ScenarioException($"{p}: road '{reference}' is not of kind {key}", ModError.BadValue);
            }
        }
    }

    /// <summary>Разрешение ссылок на типы ТС: v2 - локальные имена файла типов; v3 - id ассетов каталога (modding.md §2).</summary>
    internal sealed class TypeRefs
    {
        private Func<string, string, int> _lookup = null!;

        public int Count { get; private init; }

        /// <summary>Имя автобуса по умолчанию (v2: <c>bus</c>, v3: <c>base:vehicle/bus</c>).</summary>
        public string DefaultBus { get; private init; } = "bus";

        /// <summary>v3: тип по умолчанию - тип 0 набора (категории car, гарантировано каталогом); v2: тип <c>car</c> по имени.</summary>
        public bool CarIsIndexZero { get; private init; }

        public static TypeRefs ForNames(string[] names) => new()
        {
            Count = names.Length,
            _lookup = (n, _) => Array.IndexOf(names, n),
        };

        public static TypeRefs ForCatalog(ContentCatalog cat, PackageSet set) => new()
        {
            Count = cat.Vehicles.Count,
            DefaultBus = FormatUpgrades.BaseVehicleRef("bus"),
            CarIsIndexZero = true,
            _lookup = (reference, path) =>
            {
                var cands = new List<AssetId>();
                if (!ExpandRef(set, reference, cands, out ModError err))
                {
                    throw new ScenarioException($"{path}: {err} ('{reference}')", err);
                }

                foreach (AssetId c in cands)
                {
                    int i = c.Kind == ModAssetKind.Vehicle ? cat.FindVehicle(c) : -1;
                    if (i >= 0)
                    {
                        return i;
                    }
                }

                return -1;
            },
        };

        /// <summary>Индекс типа или -1 (ошибка формы ссылки в v3 бросает).</summary>
        public int TryFind(string name, string path) => _lookup(name, path);

        public int Find(string name, string path)
        {
            int i = _lookup(name, path);
            if (i < 0)
            {
                throw CarIsIndexZero
                    ? new ScenarioException($"{path}: {ModError.UnknownAssetRef} ('{name}')", ModError.UnknownAssetRef)
                    : new ScenarioException($"{path}: unknown vehicle type '{name}'");
            }

            return i;
        }
    }

    // ---------------------------------------------------------------- vehicle types

    private static (VehicleTypeDesc[], string[]) ParseVehicleTypes(string text)
    {
        using JsonDocument doc = OpenDoc(text, "vehicle types");
        var root = new JsonObjectReader(doc.RootElement, "$");
        CheckVersion(root, VehicleTypesFormatVersion);
        JsonElement[] arr = root.ReqArray("types");
        root.Finish();
        if (arr.Length == 0)
        {
            throw new ScenarioException("$.types: at least one vehicle type is required");
        }

        var types = new VehicleTypeDesc[arr.Length];
        var names = new string[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            string p = JsonObjectReader.Index("$.types", i);
            var r = new JsonObjectReader(arr[i], p);
            string name = r.Str("name");
            if (Array.IndexOf(names, name, 0, i) >= 0)
            {
                throw new ScenarioException($"{p}.name: duplicate vehicle type '{name}'");
            }

            names[i] = name;
            VehicleTypeDesc t = ParseVehicleFields(r, p);
            r.Finish();
            types[i] = t;
        }

        return (types, names);
    }

    /// <summary>
    /// Поля типа ТС (категория, габариты, IDM, MOBIL) без <c>name</c> и без <c>Finish</c>: общий разбор для
    /// <c>vehicle-types.json</c> v1 и <c>vehicles.json</c> пакета (modding.md §3.1).
    /// </summary>
    internal static VehicleTypeDesc ParseVehicleFields(JsonObjectReader r, string p)
    {
        string cat = r.Str("category");
        VehicleCategory category = cat switch
        {
            "car" => VehicleCategory.Car,
            "bus" => VehicleCategory.Bus,
            _ => throw new ScenarioException($"{p}.category: expected \"car\" or \"bus\""),
        };
        var t = new VehicleTypeDesc
        {
            Category = category,
            Length = (float)r.Num("length_m", min: 0.1),
            Width = (float)r.Num("width_m", min: 0.1),
            MaxSpeed = Mps(r.Num("max_speed_kmh", min: 0.1)),
            SpeedFactor = (float)r.Num("speed_factor", min: 0.01),
        };

        JsonObjectReader idm = r.ReqObject("idm");
        t.Accel = (float)idm.Num("a", min: 0.01);
        t.ComfortDecel = (float)idm.Num("b", min: 0.01);
        t.HeadwayTime = (float)idm.Num("T", min: 0);
        t.MinGap = (float)idm.Num("s0", min: 0);
        t.Delta = (float)idm.Num("delta", min: 0.01);
        idm.Finish();

        JsonObjectReader mobil = r.ReqObject("mobil");
        t.Politeness = (float)mobil.Num("politeness", min: 0, max: 1);
        t.LaneChangeThreshold = (float)mobil.Num("threshold", min: 0);
        t.SafeDecel = (float)mobil.Num("b_safe", min: 0);
        t.RightBias = (float)mobil.Num("right_bias");
        t.LaneChangeDurationTicks = Ticks(mobil.Num("duration_s", min: 0));
        mobil.Finish();

        return t;
    }

    // ---------------------------------------------------------------- city / network

    private static CityGenDesc ParseCity(JsonObjectReader? r, RoadProfilesDesc profiles)
    {
        var c = new CityGenDesc
        {
            Enabled = true,
            SizeX = 3000,
            SizeZ = 3000,
            Scale = 1.0f,
            BlockSize = 150,
            ArterialEvery = 4,
            ArterialSpeed = Mps(60),
            StreetSpeed = Mps(40),
            LaneWidth = 3.5f,
            IrregularNodes = 12,
            IrregularJitter = 40,
            NodeJitter = 0,
            RemovedStreetShare = 0.15f,
            ConnectorSamples = 8,
            Signals = CitySignals.ArterialXArterial,
            MinorControl = MinorControl.Yield,
            SignalMode = CitySignalMode.Adaptive,                 // 2.5 S2: дефолт спеки (ADR-0018); "fixed" - явно в сценарии
            SignalCoordination = CitySignalCoordination.Legacy,   // 2.5 S3: alternate - опция, дефолт остаётся legacy (замер s10k, ADR-0018)
            ProgressionSpeedFactor = 1f,
        };
        if (r == null)
        {
            c.IrregularNodes = ScaledIrregularDefault(c.Scale);
            if (profiles.Enabled)
            {
                throw new ScenarioException("$.city.profiles: required key is missing when $.road_profiles_file is set");
            }

            return c;
        }

        c.Enabled = r.Bool("enabled", true);
        JsonElement[]? size = r.OptArray("size_m");
        if (size != null)
        {
            string sp = r.ChildPath("size_m");
            if (size.Length != 2)
            {
                throw new ScenarioException($"{sp}: expected array of 2 numbers");
            }

            c.SizeX = (float)JsonObjectReader.NumValue(size[0], JsonObjectReader.Index(sp, 0), min: 1);
            c.SizeZ = (float)JsonObjectReader.NumValue(size[1], JsonObjectReader.Index(sp, 1), min: 1);
        }

        c.Scale = (float)r.Num("scale", 1.0, min: 0.001);
        c.BlockSize = (float)r.Num("block_m", 150, min: 1);
        c.ArterialEvery = r.Int("arterial_every", 4, min: 1);
        c.ArterialSpeed = Mps(r.Num("arterial_speed_kmh", 60, min: 1));
        c.StreetSpeed = Mps(r.Num("street_speed_kmh", 40, min: 1));
        c.LaneWidth = (float)r.Num("lane_width_m", 3.5, min: 0.1);

        // Дефолт «12, масштабируется scale²» (scenario-format.md §2.1): round(12 * scale^2).
        c.IrregularNodes = r.Has("irregular_nodes")
            ? r.Int("irregular_nodes", min: 0)
            : ScaledIrregularDefault(c.Scale);
        c.IrregularJitter = (float)r.Num("irregular_jitter_m", 40, min: 0);
        c.NodeJitter = (float)r.Num("node_jitter_m", 0, min: 0);
        c.RemovedStreetShare = (float)r.Num("removed_street_share", 0.15, min: 0, max: 0.5);
        c.ConnectorSamples = r.Int("connector_samples", 8, min: 2);

        // ADR-0028: 0 в JSON - скругление выключено (фиксированный отступ 7 м); в desc это -1, а 0 там значит «дефолт».
        double corner = r.Num("corner_radius_m", 6, min: 0, max: 50);
        c.CornerRadius = corner == 0 ? -1f : (float)corner;

        string signals = r.Str("signals", "arterial_x_arterial");
        c.Signals = signals switch
        {
            "arterial_x_arterial" => CitySignals.ArterialXArterial,
            "all_arterial" => CitySignals.AllArterial,
            "none" => CitySignals.None,
            _ => throw new ScenarioException(
                $"{r.ChildPath("signals")}: expected \"arterial_x_arterial\", \"all_arterial\" or \"none\""),
        };

        string minor = r.Str("minor_control", "yield");
        c.MinorControl = minor switch
        {
            "yield" => MinorControl.Yield,
            "stop" => MinorControl.Stop,
            "right_hand" => MinorControl.RightHand,
            _ => throw new ScenarioException(
                $"{r.ChildPath("minor_control")}: expected \"yield\", \"stop\" or \"right_hand\""),
        };

        c.SignalMode = ParseSignalMode(r.Str("signal_mode", "adaptive"), r.ChildPath("signal_mode"));
        c.SignalCoordination = ParseSignalCoordination(r.Str("signal_coordination", "legacy"), r.ChildPath("signal_coordination"));
        c.ProgressionSpeedFactor = (float)r.Num("progression_speed_factor", 1.0, min: 0.5, max: 1.5);
        if (c.Enabled)
        {
            ParseCityProfiles(r, profiles, ref c);   // city.profiles: с каталогом обязательна для генератора
        }

        r.Finish();
        return c;
    }

    /// <summary>
    /// <c>city.signal_mode</c> (scenario-format.md §2.1, 2.5, ADR-0018): "adaptive" (дефолт) или "fixed"; остальное - понятная ошибка.
    /// Используется и опцией bench/hash <c>--signal-mode</c>.
    /// </summary>
    public static CitySignalMode ParseSignalMode(string value, string where) => value switch
    {
        "fixed" => CitySignalMode.Fixed,
        "adaptive" => CitySignalMode.Adaptive,
        _ => throw new ScenarioException($"{where}: expected \"adaptive\" or \"fixed\""),
    };

    /// <summary>
    /// <c>city.signal_coordination</c> (2.5 S3): "alternate" (чередующаяся волна, опция), "legacy" (дефолт, формула офсетов этапа 1), "none" (без офсетов).
    /// </summary>
    public static CitySignalCoordination ParseSignalCoordination(string value, string where) => value switch
    {
        "legacy" => CitySignalCoordination.Legacy,
        "alternate" => CitySignalCoordination.Alternate,
        "none" => CitySignalCoordination.None,
        _ => throw new ScenarioException($"{where}: expected \"alternate\", \"legacy\" or \"none\""),
    };

    private static int ScaledIrregularDefault(float scale) =>
        (int)Math.Round(12.0 * scale * scale, MidpointRounding.AwayFromZero);

    private static NetworkDesc ParseNetwork(JsonObjectReader r, RoadProfilesDesc profiles)
    {
        JsonElement[] nodesJ = r.ReqArray("nodes");
        JsonElement[] segsJ = r.ReqArray("segments");
        double corner = r.Num("corner_radius_m", 6, min: 0, max: 50);   // ADR-0028: 0 - выключено (в desc -1)
        r.Finish();

        var nodes = new NodeDesc[nodesJ.Length];
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        string np = r.ChildPath("nodes");
        for (int i = 0; i < nodes.Length; i++)
        {
            string p = JsonObjectReader.Index(np, i);
            var n = new JsonObjectReader(nodesJ[i], p);
            string id = n.Str("id");
            if (!ids.TryAdd(id, i))
            {
                throw new ScenarioException($"{p}.id: duplicate node id '{id}'");
            }

            nodes[i].X = (float)n.Num("x");
            nodes[i].Z = (float)n.Num("z");
            string ctl = n.Str("control");
            nodes[i].Control = ctl switch
            {
                "none" => NodeControl.None,
                "yield" => NodeControl.Yield,
                "stop" => NodeControl.Stop,
                "signal" => NodeControl.Signal,
                "right_hand" => NodeControl.RightHandRule,
                _ => throw new ScenarioException(
                    $"{n.ChildPath("control")}: expected \"none\", \"yield\", \"stop\", \"signal\" or \"right_hand\""),
            };
            n.Finish();
        }

        var segs = new SegmentDesc[segsJ.Length];
        string sp = r.ChildPath("segments");
        for (int i = 0; i < segs.Length; i++)
        {
            string p = JsonObjectReader.Index(sp, i);
            var s = new JsonObjectReader(segsJ[i], p);
            segs[i].A = NodeRef(s, "a", ids);
            segs[i].B = NodeRef(s, "b", ids);
            if (ParseSegmentProfile(s, profiles, ref segs[i]))
            {
                segs[i].SpeedLimit = Mps(s.Num("speed_kmh", min: 1));
                s.Finish();
                continue;
            }

            segs[i].LanesAB = s.Int("lanes_ab", min: 0, max: 16);
            segs[i].LanesBA = s.Int("lanes_ba", min: 0, max: 16);
            if (segs[i].LanesAB + segs[i].LanesBA == 0)
            {
                throw new ScenarioException($"{p}: lanes_ab + lanes_ba must be > 0");
            }

            segs[i].SpeedLimit = Mps(s.Num("speed_kmh", min: 1));
            string kind = s.Str("kind");
            segs[i].Kind = kind switch
            {
                "street" => RoadKind.Street,
                "arterial" => RoadKind.Arterial,
                _ => throw new ScenarioException($"{s.ChildPath("kind")}: expected \"street\" or \"arterial\""),
            };
            s.Finish();
        }

        return new NetworkDesc { Nodes = nodes, Segments = segs, CornerRadius = corner == 0 ? -1f : (float)corner };
    }

    private static int NodeRef(JsonObjectReader s, string key, Dictionary<string, int> ids)
    {
        string id = s.Str(key);
        if (!ids.TryGetValue(id, out int idx))
        {
            throw new ScenarioException($"{s.ChildPath(key)}: unknown node id '{id}'");
        }

        return idx;
    }

    // ---------------------------------------------------------------- vehicles / demand

    private static float[] ParseTypeMix(JsonObjectReader vehR, TypeRefs refs)
    {
        var mix = new float[refs.Count];
        if (!vehR.Has("type_mix"))
        {
            mix[refs.CarIsIndexZero ? 0 : refs.Find("car", vehR.ChildPath("type_mix"))] = 1f;
            return mix;
        }

        JsonObjectReader m = vehR.ReqObject("type_mix");
        double sum = 0;
        var seen = new bool[mix.Length];
        foreach (string k in m.Keys)
        {
            string p = m.ChildPath(k);
            double w = JsonObjectReader.NumValue(m.Raw(k), p, min: 0);
            int idx = refs.Find(k, p);
            if (seen[idx])
            {
                throw new ScenarioException($"{p}: vehicle type given twice");
            }

            seen[idx] = true;
            mix[idx] = (float)w;
            sum += w;
        }

        if (sum <= 0)
        {
            throw new ScenarioException($"{m.Path}: sum of weights must be > 0");
        }

        return mix;
    }

    private static DemandDesc ParseDemand(JsonObjectReader? r)
    {
        var d = new DemandDesc
        {
            ZoneSize = 500,
            Model = DemandModel.Gravity,
            GravityExponent = 1.0f,
            WeightJitter = 0.5f,
            ExternalShare = 0.3f,
            MinTripLength = 600,
        };
        if (r == null)
        {
            return d;
        }

        d.ZoneSize = (float)r.Num("zone_m", 500, min: 1);
        string model = r.Str("model", "gravity");
        d.Model = model switch
        {
            "gravity" => DemandModel.Gravity,
            "uniform" => DemandModel.Uniform,
            "residents" => DemandModel.Residents,
            _ => throw new ScenarioException($"{r.ChildPath("model")}: expected \"uniform\", \"gravity\" or \"residents\""),
        };
        d.GravityExponent = (float)r.Num("gravity_exponent", 1.0, min: 0);
        d.WeightJitter = (float)r.Num("weight_jitter", 0.5, min: 0, max: 1);
        d.ExternalShare = (float)r.Num("external_share", 0.3, min: 0, max: 1);
        d.MinTripLength = (float)r.Num("min_trip_m", 600, min: 0);

        JsonElement[]? od = r.OptArray("od_pairs");
        if (od != null)
        {
            string op = r.ChildPath("od_pairs");
            d.OdPairs = new OdPairDesc[od.Length];
            for (int i = 0; i < od.Length; i++)
            {
                var o = new JsonObjectReader(od[i], JsonObjectReader.Index(op, i));
                d.OdPairs[i].FromZone = o.Int("from", min: 0);
                d.OdPairs[i].ToZone = o.Int("to", min: 0);
                d.OdPairs[i].Weight = (float)o.Num("weight", 1.0, min: 0);
                o.Finish();
            }
        }

        r.Finish();
        return d;
    }

    // ---------------------------------------------------------------- transit

    private static TransitDesc ParseTransit(JsonObjectReader? r, TypeRefs refs, bool pedsEnabled)
    {
        var t = new TransitDesc
        {
            Routes = 0,
            HeadwayTicks = Ticks(300),
            StopsPerRouteMin = 8,
            StopsPerRouteMax = 14,
            StopSpacing = 400,
            StopKind = StopKindMode.Mixed,
            BusType = -1,
            DwellBaseTicks = Ticks(8),
            DwellPerPassengerTicks = (float)(1.5 * TicksPerSecond),
            PassengerRatePerMin = 2,
            HoldAlpha = 0.9f,
            HoldMaxTicks = Ticks(60),
            HoldEveryN = 1,
            BayModel = BayModel.Virtual,
            BayYieldTicks = Ticks(3),
            PassengerModel = pedsEnabled ? PassengerModel.Agents : PassengerModel.Counter,
        };
        string busName = refs.DefaultBus;
        if (r != null)
        {
            t.Routes = r.Int("routes", 0, min: 0);
            t.HeadwayTicks = Ticks(r.Num("headway_s", 300, min: 1));
            JsonElement[]? spr = r.OptArray("stops_per_route");
            if (spr != null)
            {
                string sp = r.ChildPath("stops_per_route");
                if (spr.Length != 2)
                {
                    throw new ScenarioException($"{sp}: expected array of 2 integers [min, max]");
                }

                t.StopsPerRouteMin = JsonObjectReader.IntValue(spr[0], JsonObjectReader.Index(sp, 0), min: 1);
                t.StopsPerRouteMax = JsonObjectReader.IntValue(spr[1], JsonObjectReader.Index(sp, 1), min: 1);
                if (t.StopsPerRouteMin > t.StopsPerRouteMax)
                {
                    throw new ScenarioException($"{sp}: min must be <= max");
                }
            }

            t.StopSpacing = (float)r.Num("stop_spacing_m", 400, min: 1);
            string kind = r.Str("stop_kind", "mixed");
            t.StopKind = kind switch
            {
                "mixed" => StopKindMode.Mixed,
                "curb" => StopKindMode.Curb,
                "bay" => StopKindMode.Bay,
                _ => throw new ScenarioException($"{r.ChildPath("stop_kind")}: expected \"curb\", \"bay\" or \"mixed\""),
            };
            busName = r.Str("bus_type", refs.DefaultBus);
            t.DwellBaseTicks = Ticks(r.Num("dwell_base_s", 8, min: 0));
            t.DwellPerPassengerTicks = (float)(r.Num("dwell_per_passenger_s", 1.5, min: 0) * TicksPerSecond);
            t.PassengerRatePerMin = (float)r.Num("passenger_rate_per_min", 2, min: 0);
            t.HoldAlpha = (float)r.Num("hold_alpha", 0.9, min: 0, max: 1);
            t.HoldMaxTicks = Ticks(r.Num("hold_max_s", 60, min: 0));
            t.HoldEveryN = r.Int("hold_every_n", 1, min: 1);
            t.Bidirectional = r.Bool("bidirectional", false);
            string bm = r.Str("bay_model", "virtual");
            t.BayModel = bm switch
            {
                "virtual" => BayModel.Virtual,
                "curb" => BayModel.Curb,
                "lane" => BayModel.Lane,
                _ => throw new ScenarioException($"{r.ChildPath("bay_model")}: expected \"virtual\", \"curb\" or \"lane\""),
            };
            t.BayYieldTicks = Ticks(r.Num("bay_yield_s", 3, min: 0, max: 60));
            string pm = r.Str("passenger_model", pedsEnabled ? "agents" : "counter");
            t.PassengerModel = pm switch
            {
                "counter" => PassengerModel.Counter,
                "agents" => PassengerModel.Agents,
                _ => throw new ScenarioException($"{r.ChildPath("passenger_model")}: expected \"counter\" or \"agents\""),
            };
            if (t.PassengerModel == PassengerModel.Agents && !pedsEnabled)
            {
                throw new ScenarioException($"{r.ChildPath("passenger_model")}: \"agents\" requires an enabled $.peds section");
            }


            JsonElement[]? ex = r.OptArray("routes_explicit");
            if (ex != null)
            {
                t.RoutesExplicit = ParseExplicitRoutes(ex, r.ChildPath("routes_explicit"));
            }

            r.Finish();
        }

        bool needBus = t.Routes > 0 || t.RoutesExplicit is { Length: > 0 };
        if (needBus)
        {
            t.BusType = refs.Find(busName, "$.transit.bus_type");
        }
        else
        {
            int i = refs.TryFind(busName, "$.transit.bus_type");
            t.BusType = i >= 0 ? i : 0;
        }

        return t;
    }

    // ---------------------------------------------------------------- peds (scenario-format.md §2.4)

    private const int MaxPedCount = 10_000_000;
    private const double MaxPedDistanceM = 100_000;

    /// <summary>Секция <c>peds</c> -> <see cref="PedDesc"/> в целых единицах ядра: мм, мм/тик (км/ч / 3.6 · 50), тики. Нет секции - <c>Enabled = false</c>.</summary>
    private static PedDesc ParsePeds(JsonObjectReader? r)
    {
        if (r == null)
        {
            return default;
        }

        var p = new PedDesc { Enabled = r.Bool("enabled", true) };
        p.Count = r.Int("count", min: 0, max: MaxPedCount);
        p.Residents = r.Int("residents", 2 * p.Count, min: p.Count, max: 2 * MaxPedCount);
        p.SpawnRampTicks = Ticks(r.Num("spawn_ramp_s", 120, min: 0));
        (p.SpeedMinMmPerTick, p.SpeedMaxMmPerTick) = ParseKmhRange(r, "speed_kmh", 4.0, 5.4);
        (p.ActivityMinTicks, p.ActivityMaxTicks) = ParseSecondsRange(r, "activity_s", 600, 1800);
        p.WalkOnlyMm = Mm(r.Num("walk_only_m", 800, min: 0, max: MaxPedDistanceM));
        p.MaxWalkToStopMm = Mm(r.Num("max_walk_to_stop_m", 500, min: 0, max: MaxPedDistanceM));
        p.MaxWalkMm = Mm(r.Num("max_walk_m", 1500, min: 0, max: MaxPedDistanceM));
        p.SidewalkWidthMm = Mm(r.Num("sidewalk_width_m", 3, min: 0.5, max: 20));
        p.CrosswalkWidthMm = Mm(r.Num("crosswalk_width_m", 3, min: 0.5, max: 20));
        string arterial = r.Str("arterial_crossings", "signal_only");
        p.ArterialCrossings = arterial switch
        {
            "signal_only" => ArterialCrossings.SignalOnly,
            "all" => ArterialCrossings.All,
            _ => throw new ScenarioException($"{r.ChildPath("arterial_crossings")}: expected \"signal_only\" or \"all\""),
        };
        p.MidblockAtStops = r.Bool("midblock_at_stops", true);
        p.WalkTicks = Math.Max(1, Ticks(r.Num("walk_s", 5, min: 0.05)));
        p.ClearSpeedMmPerTick = KmhToMmPerTick(r.Num("clear_speed_kmh", 4.3, min: 0.1, max: 20));
        p.MaxCloseTicks = Ticks(r.Num("max_close_s", 10, min: 0));
        p.CarWindowTicks = Ticks(r.Num("car_window_s", 5, min: 0));
        p.PathfindBudgetPerTick = r.Int("pathfind_budget_per_tick", 8, min: 0);
        p.WorkBudgetPerTick = r.Int("work_budget_per_tick", 8000, min: 0);
        p.StuckCrossTicks = Math.Max(1, Ticks(r.Num("stuck_cross_s", 120, min: 0.05)));
        p.StuckStopTicks = r.Has("stuck_stop_s") ? Math.Max(1, Ticks(r.Num("stuck_stop_s", min: 0.05))) : 0;   // 0 -> max(3 · headway_s, 900) после разбора transit
        string source = r.Str("source", "preform");
        p.Source = source switch
        {
            "preform" => PedSource.Preform,
            "city" => PedSource.City,
            _ => throw new ScenarioException($"{r.ChildPath("source")}: expected \"preform\" or \"city\""),
        };
        r.Finish();
        return p;
    }

    private static int Mm(double meters) => (int)Math.Round(meters * 1000.0);

    private static int KmhToMmPerTick(double kmh) => Math.Max(1, (int)Math.Round(kmh / 3.6 * 50.0));

    private static (int Min, int Max) ParseKmhRange(JsonObjectReader r, string key, double defMin, double defMax)
    {
        (double lo, double hi) = ParseRange(r, key, defMin, defMax, 0.1, 30);
        return (KmhToMmPerTick(lo), KmhToMmPerTick(hi));
    }

    private static (int Min, int Max) ParseSecondsRange(JsonObjectReader r, string key, double defMin, double defMax)
    {
        (double lo, double hi) = ParseRange(r, key, defMin, defMax, 0.05, 1e7);
        return (Math.Max(1, Ticks(lo)), Math.Max(1, Ticks(hi)));
    }

    private static (double Min, double Max) ParseRange(JsonObjectReader r, string key, double defMin, double defMax, double lowest, double highest)
    {
        JsonElement[]? arr = r.OptArray(key);
        if (arr == null)
        {
            return (defMin, defMax);
        }

        string path = r.ChildPath(key);
        if (arr.Length != 2)
        {
            throw new ScenarioException($"{path}: expected array of 2 numbers [min, max]");
        }

        double lo = JsonObjectReader.NumValue(arr[0], JsonObjectReader.Index(path, 0), lowest, highest);
        double hi = JsonObjectReader.NumValue(arr[1], JsonObjectReader.Index(path, 1), lowest, highest);
        if (lo > hi)
        {
            throw new ScenarioException($"{path}: min must be <= max");
        }

        return (lo, hi);
    }

    private static BusRouteDesc[] ParseExplicitRoutes(JsonElement[] arr, string path)
    {
        var routes = new BusRouteDesc[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            string p = JsonObjectReader.Index(path, i);
            var rr = new JsonObjectReader(arr[i], p);
            JsonElement[] stops = rr.ReqArray("stops");
            routes[i].HeadwayTicks = Ticks(rr.Num("headway_s", 300, min: 1));
            routes[i].Stops = new BusStopDesc[stops.Length];
            string sp = rr.ChildPath("stops");
            for (int j = 0; j < stops.Length; j++)
            {
                var s = new JsonObjectReader(stops[j], JsonObjectReader.Index(sp, j));
                routes[i].Stops[j].Lane = s.Int("lane", min: 0);
                routes[i].Stops[j].S = (float)s.Num("s", min: 0);
                string kind = s.Str("kind");
                routes[i].Stops[j].Kind = kind switch
                {
                    "curb" => BusStopKind.Curb,
                    "bay" => BusStopKind.Bay,
                    _ => throw new ScenarioException($"{s.ChildPath("kind")}: expected \"curb\" or \"bay\""),
                };
                s.Finish();
            }

            rr.Finish();
        }

        return routes;
    }

    // ---------------------------------------------------------------- rules / capacities

    private static RulesDesc ParseRules(JsonObjectReader? r)
    {
        var d = new RulesDesc
        {
            MinPhysicalGap = 0.5f,
            StuckThresholdTicks = Ticks(60),
            StuckSpeedThreshold = 0.1f,
            GridlockStuckShare = 0.05f,
            GridlockMinStuck = 10,
            GridlockHoldTicks = Ticks(60),
            MobilEveryTicks = 10,
            DefaultYellowTicks = Ticks(3),
            DefaultAllRedTicks = Ticks(1),
            YieldGapTime = 3.0f,
            MaxYieldWaitTicks = Ticks(20),
            SignalMinGreenTicks = Ticks(7),
            SignalMaxGreenTicks = Ticks(24),
            SignalGapTicks = Ticks(2.5),
            SignalArrowTriggerTicks = Ticks(4),
            SignalArrowMinTicks = Ticks(3),
            SignalArrowMaxTicks = Ticks(6),
            SignalArrowGapTicks = Ticks(2),
        };
        if (r == null)
        {
            return d;
        }

        d.MinPhysicalGap = (float)r.Num("min_physical_gap_m", 0.5, min: 0);
        d.StuckThresholdTicks = Ticks(r.Num("stuck_threshold_s", 60, min: 0));
        d.StuckSpeedThreshold = (float)r.Num("stuck_speed_mps", 0.1, min: 0);
        d.GridlockStuckShare = (float)r.Num("gridlock_stuck_share", 0.05, min: 0, max: 1);
        d.GridlockMinStuck = r.Int("gridlock_min_stuck", 10, min: 0);
        d.GridlockHoldTicks = Ticks(r.Num("gridlock_hold_s", 60, min: 0));
        d.MobilEveryTicks = r.Int("mobil_every_ticks", 10, min: 1);
        d.DefaultYellowTicks = Ticks(r.Num("signal_yellow_s", 3, min: 0));
        d.DefaultAllRedTicks = Ticks(r.Num("signal_all_red_s", 1, min: 0));
        d.YieldGapTime = (float)r.Num("yield_gap_s", 3.0, min: 0);
        d.MaxYieldWaitTicks = Ticks(r.Num("max_yield_wait_s", 20, min: 0));
        d.SignalMinGreenTicks = Ticks(r.Num("signal_min_green_s", 7, min: 0.05));
        d.SignalMaxGreenTicks = Ticks(r.Num("signal_max_green_s", 24, min: 0.05));
        d.SignalGapTicks = Ticks(r.Num("signal_gap_s", 2.5, min: 0.05));
        d.SignalArrowTriggerTicks = Ticks(r.Num("signal_arrow_trigger_s", 4, min: 0.05));
        d.SignalArrowMinTicks = Ticks(r.Num("signal_arrow_min_s", 3, min: 0.05));
        d.SignalArrowMaxTicks = Ticks(r.Num("signal_arrow_max_s", 6, min: 0.05));
        d.SignalArrowGapTicks = Ticks(r.Num("signal_arrow_gap_s", 2, min: 0.05));
        r.Finish();
        return d;
    }

    // routing.md 9.9, scenario-format.md 2.1 "routing": дефолты - как в спеке; секция необязательна.
    private static RoutingDesc ParseRouting(JsonObjectReader? r)
    {
        var d = new RoutingDesc
        {
            Levels = 1,
            Fanout = 4,
            CellSize = 1000f,
            WindowSteps = 16,
            PathfindBudgetPerTick = 32,
            CustomizeBudgetPerTick = 10000,
            CustomizeEps = 0.05f,
            CostRecalcTicks = Ticks(30),
            TimedDrift = 0.15f,
            TimedDriftAbsTicks = Ticks(20),
        };
        d.WorkBudgetPerTick = WorkBudgetDefault(d.PathfindBudgetPerTick);
        if (r == null)
        {
            return d;
        }

        d.Levels = r.Int("levels", 1, min: 0, max: MaxRoutingLevels);
        d.CellSize = (float)r.Num("cell_m", 1000, min: 1);
        d.Fanout = r.Int("fanout", 4, min: 2, max: MaxRoutingFanout);
        d.WindowSteps = r.Int("window_steps", 16, min: 8, max: 32);
        d.PathfindBudgetPerTick = r.Int("pathfind_budget_per_tick", 32, min: 1);
        d.WorkBudgetPerTick = r.Int("work_budget_per_tick", WorkBudgetDefault(d.PathfindBudgetPerTick), min: 1);
        d.QueryWorkCap = r.Int("query_work_cap", 0, min: 0); // 0 = 4 * число направленных сегментов (routing.md 9.4)
        d.CustomizeBudgetPerTick = r.Int("customize_budget_per_tick", 10000, min: 1);
        d.CustomizeEps = (float)r.Num("customize_eps", 0.05, min: 0, max: 1);
        d.CostRecalcTicks = Math.Max(1, Ticks(r.Num("cost_recalc_s", 30, min: 0.05)));
        d.TimedDrift = (float)r.Num("timed_drift", 0.15, min: 0);
        d.TimedDriftAbsTicks = Ticks(r.Num("timed_drift_abs_s", 20, min: 0));
        r.Finish();
        return d;
    }

    // player-tools.md §11, scenario-format.md 2.1 "tools": стоимости (заглушки до баланса 4.2) и границы мира; секция необязательна.
    private static ToolsDesc ParseTools(JsonObjectReader? r)
    {
        var d = new ToolsDesc
        {
            Costs = new ToolCostsDesc
            {
                RoadPerMLaneStreet = 10,
                RoadPerMLaneArterial = 20,
                CurveFactorPct = 120,
                NodeSignal = 500,
                ArmSign = 50,
                TurnBan = 100,
                BusStop = 200,
                BusRoute = 1000,
                DemolishRefundPct = 50,
                UpgradeRefundPct = 50,
            },
        };
        if (r == null)
        {
            return d;
        }

        JsonObjectReader? c = r.OptObject("costs");
        if (c != null)
        {
            JsonObjectReader? perM = c.OptObject("road_per_m_lane");
            if (perM != null)
            {
                d.Costs.RoadPerMLaneStreet = (long)perM.Num("street", d.Costs.RoadPerMLaneStreet, min: 0, max: MaxCostValue);
                d.Costs.RoadPerMLaneArterial = (long)perM.Num("arterial", d.Costs.RoadPerMLaneArterial, min: 0, max: MaxCostValue);
                perM.Finish();
            }

            d.Costs.CurveFactorPct = c.Int("curve_factor_pct", d.Costs.CurveFactorPct, min: 1, max: 1000);
            d.Costs.NodeSignal = (long)c.Num("node_signal", d.Costs.NodeSignal, min: 0, max: MaxCostValue);
            d.Costs.ArmSign = (long)c.Num("arm_sign", d.Costs.ArmSign, min: 0, max: MaxCostValue);
            d.Costs.TurnBan = (long)c.Num("turn_ban", d.Costs.TurnBan, min: 0, max: MaxCostValue);
            d.Costs.BusStop = (long)c.Num("bus_stop", d.Costs.BusStop, min: 0, max: MaxCostValue);
            d.Costs.BusRoute = (long)c.Num("bus_route", d.Costs.BusRoute, min: 0, max: MaxCostValue);
            d.Costs.DemolishRefundPct = c.Int("demolish_refund_pct", d.Costs.DemolishRefundPct, min: 0, max: 100);
            d.Costs.UpgradeRefundPct = c.Int("upgrade_refund_pct", d.Costs.UpgradeRefundPct, min: 0, max: 100);
            c.Finish();
        }

        d.InverseCapacity = r.Int("inverse_capacity", 0, min: 0, max: 1 << 20);
        JsonObjectReader? b = r.OptObject("world_bounds");
        if (b != null)
        {
            d.HasWorldBounds = true;
            d.BoundsMinX = (float)b.Num("min_x", min: -1e7, max: 1e7);
            d.BoundsMinZ = (float)b.Num("min_z", min: -1e7, max: 1e7);
            d.BoundsMaxX = (float)b.Num("max_x", min: -1e7, max: 1e7);
            d.BoundsMaxZ = (float)b.Num("max_z", min: -1e7, max: 1e7);
            if (d.BoundsMaxX <= d.BoundsMinX || d.BoundsMaxZ <= d.BoundsMinZ)
            {
                throw new ScenarioException($"{b.Path}: expected min_x < max_x and min_z < max_z");
            }

            b.Finish();
        }

        r.Finish();
        return d;
    }

    private const double MaxCostValue = 1e12;

    // sim-lod.md, scenario-format.md 2.1 "lod": секция необязательна, без неё (и при mode = off) LOD нет.
    private static LodDesc ParseLod(JsonObjectReader? r)
    {
        var d = new LodDesc
        {
            Mode = LodMode.Off,
            CellM = 1000f,
            MicroBudget = 15000,
            EvalTicks = Ticks(5),
            HoldTicks = Ticks(30),
            FocusMarginCells = 1,
            FollowSelected = true,
            FocusCommands = true,
            CongestionShare = 0.1f,
            CongestionMin = 10,
            MesoHeadwayScale = 0.8f,
            MesoAccelScale = 1f,
        };
        if (r == null)
        {
            return d;
        }

        string mode = r.Str("mode", "off");
        d.Mode = mode switch
        {
            "off" => LodMode.Off,
            "auto" => LodMode.Auto,
            "all_meso" => LodMode.AllMeso,
            _ => throw new ScenarioException($"{r.ChildPath("mode")}: expected \"off\", \"auto\" or \"all_meso\""),
        };
        d.CellM = (float)r.Num("cell_m", 1000, min: 1);
        d.MicroBudget = r.Int("micro_budget", 15000, min: 0);
        d.EvalTicks = Math.Max(1, Ticks(r.Num("eval_s", 5, min: 0.05)));
        d.HoldTicks = Ticks(r.Num("hold_s", 30, min: 0));
        d.FocusMarginCells = r.Int("focus_margin_cells", 1, min: 0, max: MaxLodFocusMargin);
        d.FollowSelected = r.Bool("follow_selected", true);
        d.FocusCommands = r.Bool("focus_commands", true);
        d.CongestionShare = (float)r.Num("congestion_share", 0.1, min: 0, max: 1);
        d.CongestionMin = r.Int("congestion_min", 10, min: 0);
        d.MesoHeadwayScale = (float)r.Num("meso_headway_scale", 0.8, min: 0.1, max: 10);
        d.MesoAccelScale = (float)r.Num("meso_accel_scale", 1.0, min: 0.1, max: 10);
        string model = r.Str("meso_model", "kinematic");
        d.MesoModel = model switch
        {
            "kinematic" => MesoModel.Kinematic,
            "queue" => MesoModel.Queue,
            _ => throw new ScenarioException($"{r.ChildPath("meso_model")}: expected \"kinematic\" or \"queue\""),
        };
        if (d.MesoModel == MesoModel.Queue && d.Mode != LodMode.AllMeso)
        {
            throw new ScenarioException($"{r.ChildPath("meso_model")}: \"queue\" requires mode \"all_meso\" (prototype Q0)");
        }

        d.QueueHeadwayScale = (float)r.Num("queue_headway_scale", 0, min: 0, max: 10);
        d.Focus = ParseLodPlaces(r, "focus");
        d.Pinned = ParseLodPlaces(r, "pinned");

        JsonObjectReader? tour = r.OptObject("tour");
        if (tour != null)
        {
            d.Tour.PeriodTicks = Math.Max(1, Ticks(tour.Num("period_s", min: 0.05)));
            d.Tour.RadiusM = (float)tour.Num("radius_m", min: 1);
            string tp = tour.ChildPath("path");
            JsonElement[] path = tour.ReqArray("path");
            if (path.Length == 0)
            {
                throw new ScenarioException($"{tp}: expected at least one point");
            }

            d.Tour.PathX = new float[path.Length];
            d.Tour.PathZ = new float[path.Length];
            for (int i = 0; i < path.Length; i++)
            {
                string ip = JsonObjectReader.Index(tp, i);
                JsonElement[] pt = JsonObjectReader.ToArray(path[i], ip);
                if (pt.Length != 2)
                {
                    throw new ScenarioException($"{ip}: expected [x, z]");
                }

                d.Tour.PathX[i] = (float)JsonObjectReader.NumValue(pt[0], JsonObjectReader.Index(ip, 0));
                d.Tour.PathZ[i] = (float)JsonObjectReader.NumValue(pt[1], JsonObjectReader.Index(ip, 1));
            }

            tour.Finish();
        }

        r.Finish();
        return d;
    }

    private static LodPlaceDesc[]? ParseLodPlaces(JsonObjectReader r, string key)
    {
        JsonElement[]? arr = r.OptArray(key);
        if (arr == null)
        {
            return null;
        }

        string path = r.ChildPath(key);
        if (arr.Length > MaxLodSlots)
        {
            throw new ScenarioException($"{path}: at most {MaxLodSlots} entries");
        }

        var res = new LodPlaceDesc[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var o = new JsonObjectReader(arr[i], JsonObjectReader.Index(path, i));
            res[i].X = (float)o.Num("x");
            res[i].Z = (float)o.Num("z");
            res[i].RadiusM = (float)o.Num("radius_m", min: 0);
            o.Finish();
        }

        return res;
    }

    private static readonly string[] _pedCapacityKeys = { "max_peds", "max_residents", "max_ped_path_steps" };

    private static int WorkBudgetDefault(int pathfindBudget) => (int)Math.Min(int.MaxValue, 1500L * pathfindBudget);

    private static SimCapacities ParseCapacities(JsonObjectReader r, in RoutingDesc routing, in PedDesc peds, bool residents)
    {
        var c = new SimCapacities
        {
            MaxVehicles = r.Int("max_vehicles", min: 1),
            MaxNodes = r.Int("max_nodes", min: 1),
            MaxSegments = r.Int("max_segments", min: 1),
            MaxLanes = r.Int("max_lanes", min: 1),
            MaxLanePoints = r.Int("max_lane_points", min: 1),
            MaxZones = r.Int("max_zones", min: 1),
            MaxBusRoutes = r.Int("max_bus_routes", min: 0),
            MaxBusStops = r.Int("max_bus_stops", min: 0),
            MaxBusStopsPerRoute = r.Int("max_bus_stops_per_route", 32, min: 1),
            MaxRoutes = r.Int("max_routes", min: 1),
            MaxPendingTrips = r.Int("max_pending_trips", min: 1),
            CommandQueueCapacity = r.Int("command_queue", 1024, min: 1),
            EventCapacity = r.Int("events", 4096, min: 1),
            MaxSignalizedNodes = r.Int("max_signalized_nodes", min: 0),
        };
        // max_plan_steps: дефолт max_routes * (12 + 8 * levels) (формула и замер - routing.md 15, H3); предел арены - 1 << 28.
        // Окна маршрутов (max_routes * window_steps шагов) отдельного ключа не имеют.
        long planDefault = Math.Min(MaxArenaSteps, (long)c.MaxRoutes * (12 + (8 * Math.Max(1, routing.Levels))));
        c.MaxPlanSteps = r.Int("max_plan_steps", (int)planDefault, min: 1, max: MaxArenaSteps);
        c.MaxConnectors = r.Int("max_connectors", checked(c.MaxNodes * 16), min: 1);
        c.MaxConflicts = r.Int("max_conflicts", checked(c.MaxConnectors * 6), min: 1);
        if (peds.Enabled)
        {
            // pedestrians.md §12: max_peds >= count · 1.3, max_residents >= residents.
            long minPeds = (((long)peds.Count * 13L) + 9L) / 10L;
            c.MaxPeds = r.Int("max_peds", min: (int)Math.Max(1L, minPeds));
            c.MaxResidents = r.Int("max_residents", min: Math.Max(1, peds.Residents));
            c.MaxPedPathSteps = r.Int("max_ped_path_steps", 64, min: 1, max: 4096);
        }
        else
        {
            foreach (string key in _pedCapacityKeys)
            {
                if (r.Has(key))
                {
                    throw new ScenarioException($"{r.ChildPath(key)}: allowed only with an enabled $.peds section");
                }
            }
        }

        if (residents)
        {
            // zoning-demand.md §16: ключ max_residents занят заготовками peds, жители города - max_city_residents.
            c.MaxLots = r.Int("max_lots", min: 1);
            c.MaxBuildings = r.Int("max_buildings", min: 1);
            c.MaxCityResidents = r.Int("max_city_residents", min: 1);
            c.SkimObsCapacity = r.Int("skim_obs_capacity", 65_536, min: 1);
        }
        else
        {
            foreach (string key in _cityCapacityKeys)
            {
                if (r.Has(key))
                {
                    throw new ScenarioException($"{r.ChildPath(key)}: allowed only with demand.model = \"residents\"");
                }
            }
        }

        r.Finish();
        return c;
    }
}
