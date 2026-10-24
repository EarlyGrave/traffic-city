using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Scenarios;

/// <summary>Результат загрузки: desc плюс информативные поля, которых нет в <see cref="ScenarioDesc"/>.</summary>
public sealed class LoadedScenario
{
    public LoadedScenario(string name, string[] typeNames, ScenarioDesc desc)
    {
        Name = name;
        TypeNames = typeNames;
        Desc = desc;
    }

    public string Name { get; }

    /// <summary>Имена типов ТС; индекс = <c>VehicleType</c>.</summary>
    public string[] TypeNames { get; }

    public ScenarioDesc Desc { get; }
}

/// <summary>
/// Загрузчик сценариев JSON → <see cref="ScenarioDesc"/> по docs/design/scenario-format.md:
/// дефолты, единицы (км/ч → м/с, с → тики при 20 Гц), строгий контроль ключей, format_version = 2 (сценарий) / 1 (типы ТС).
/// </summary>
public static class ScenarioLoader
{
    public const int SupportedFormatVersion = 2;
    public const int VehicleTypesFormatVersion = 1;
    private const int MaxRoutingLevels = 4;       // = Overlay.MaxLevels ядра
    private const int MaxRoutingFanout = 16;      // = Overlay.MaxFanout
    private const int MaxArenaSteps = 1 << 28;    // предел арен шагов/планов (WorldBuilder.MaxRouteStepsCapacity)
    public const int TicksPerSecond = 20;
    private const string DefaultVehicleTypesFile = "vehicle-types.json";

    private static readonly JsonDocumentOptions _docOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static ScenarioDesc Load(string path) => LoadInfo(path).Desc;

    public static LoadedScenario LoadInfo(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string text = ReadFile(path);
        return Parse(text, rel => ReadFile(Path.Combine(dir, rel)), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Разбор из строк (тесты): <paramref name="vehicleTypesJson"/> подставляется вместо файла типов.</summary>
    public static LoadedScenario Parse(string scenarioJson, string vehicleTypesJson) =>
        Parse(scenarioJson, _ => vehicleTypesJson, "scenario");

    public static LoadedScenario Parse(string scenarioJson, Func<string, string> readRelativeFile, string defaultName)
    {
        using JsonDocument doc = OpenDoc(scenarioJson, "scenario");
        var root = new JsonObjectReader(doc.RootElement, "$");
        CheckVersion(root, SupportedFormatVersion);

        string name = root.Str("name", defaultName);
        ulong seed = root.ULong("seed");
        double durationS = root.Num("duration_s", min: 0);
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

        VehicleTypeDesc[] types;
        string[] typeNames;
        try
        {
            (types, typeNames) = ParseVehicleTypes(typesText);
        }
        catch (ScenarioException e)
        {
            throw new ScenarioException($"{typesFile}: {e.Message}", e);
        }

        JsonObjectReader? cityR = root.OptObject("city");
        JsonObjectReader? netR = root.OptObject("network");
        CityGenDesc city = ParseCity(cityR);
        NetworkDesc network = default;
        if (netR != null)
        {
            if (city.Enabled)
            {
                throw new ScenarioException("$.network: explicit network requires $.city.enabled = false");
            }

            network = ParseNetwork(netR);
        }
        else if (!city.Enabled)
        {
            throw new ScenarioException("$.network: required key is missing when $.city.enabled = false");
        }

        JsonObjectReader vehR = root.ReqObject("vehicles");
        int count = vehR.Int("count", min: 0);
        int rampTicks = Ticks(vehR.Num("spawn_ramp_s", 120, min: 0));
        float[] typeMix = ParseTypeMix(vehR, typeNames);
        vehR.Finish();

        DemandDesc demand = ParseDemand(root.OptObject("demand"));
        demand.TargetVehicles = count;
        demand.SpawnRampTicks = rampTicks;
        demand.TypeMix = typeMix;

        TransitDesc transit = ParseTransit(root.OptObject("transit"), typeNames);
        RulesDesc rules = ParseRules(root.OptObject("rules"));
        RoutingDesc routing = ParseRouting(root.OptObject("routing"));
        SimCapacities caps = ParseCapacities(root.ReqObject("capacities"), routing);
        root.Finish();

        var desc = new ScenarioDesc
        {
            Seed = seed,
            DurationTicks = Ticks(durationS),
            Capacities = caps,
            Routing = routing,
            City = city,
            Network = network,
            Demand = demand,
            Transit = transit,
            Rules = rules,
            VehicleTypes = types,
        };
        return new LoadedScenario(name, typeNames, desc);
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

    private static int FindType(string[] names, string name, string path)
    {
        int i = Array.IndexOf(names, name);
        if (i < 0)
        {
            throw new ScenarioException($"{path}: unknown vehicle type '{name}'");
        }

        return i;
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

            r.Finish();
            types[i] = t;
        }

        return (types, names);
    }

    // ---------------------------------------------------------------- city / network

    private static CityGenDesc ParseCity(JsonObjectReader? r)
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
        };
        if (r == null)
        {
            c.IrregularNodes = ScaledIrregularDefault(c.Scale);
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

        r.Finish();
        return c;
    }

    private static int ScaledIrregularDefault(float scale) =>
        (int)Math.Round(12.0 * scale * scale, MidpointRounding.AwayFromZero);

    private static NetworkDesc ParseNetwork(JsonObjectReader r)
    {
        JsonElement[] nodesJ = r.ReqArray("nodes");
        JsonElement[] segsJ = r.ReqArray("segments");
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

        return new NetworkDesc { Nodes = nodes, Segments = segs };
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

    private static float[] ParseTypeMix(JsonObjectReader vehR, string[] typeNames)
    {
        var mix = new float[typeNames.Length];
        if (!vehR.Has("type_mix"))
        {
            mix[FindType(typeNames, "car", vehR.ChildPath("type_mix"))] = 1f;
            return mix;
        }

        JsonObjectReader m = vehR.ReqObject("type_mix");
        double sum = 0;
        foreach (string k in m.Keys)
        {
            string p = m.ChildPath(k);
            double w = JsonObjectReader.NumValue(m.Raw(k), p, min: 0);
            mix[FindType(typeNames, k, p)] = (float)w;
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
            _ => throw new ScenarioException($"{r.ChildPath("model")}: expected \"uniform\" or \"gravity\""),
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

    private static TransitDesc ParseTransit(JsonObjectReader? r, string[] typeNames)
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
        };
        string busName = "bus";
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
            busName = r.Str("bus_type", "bus");
            t.DwellBaseTicks = Ticks(r.Num("dwell_base_s", 8, min: 0));
            t.DwellPerPassengerTicks = (float)(r.Num("dwell_per_passenger_s", 1.5, min: 0) * TicksPerSecond);
            t.PassengerRatePerMin = (float)r.Num("passenger_rate_per_min", 2, min: 0);

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
            t.BusType = FindType(typeNames, busName, "$.transit.bus_type");
        }
        else
        {
            int i = Array.IndexOf(typeNames, busName);
            t.BusType = i >= 0 ? i : 0;
        }

        return t;
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
        d.CustomizeBudgetPerTick = r.Int("customize_budget_per_tick", 10000, min: 1);
        d.CustomizeEps = (float)r.Num("customize_eps", 0.05, min: 0, max: 1);
        d.CostRecalcTicks = Math.Max(1, Ticks(r.Num("cost_recalc_s", 30, min: 0.05)));
        d.TimedDrift = (float)r.Num("timed_drift", 0.15, min: 0);
        d.TimedDriftAbsTicks = Ticks(r.Num("timed_drift_abs_s", 20, min: 0));
        r.Finish();
        return d;
    }

    private static int WorkBudgetDefault(int pathfindBudget) => (int)Math.Min(int.MaxValue, 1500L * pathfindBudget);

    private static SimCapacities ParseCapacities(JsonObjectReader r, in RoutingDesc routing)
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
            PathfindBudgetPerTick = routing.PathfindBudgetPerTick, // переходно: зеркало routing.pathfind_budget_per_tick до H3
            MaxSignalizedNodes = r.Int("max_signalized_nodes", min: 0),
        };
        // max_plan_steps: дефолт max_routes * (12 + 8 * levels) (оценка, уточняется замером H3); предел арены - 1 << 28.
        long planDefault = Math.Min(MaxArenaSteps, (long)c.MaxRoutes * (12 + (8 * Math.Max(1, routing.Levels))));
        c.MaxPlanSteps = r.Int("max_plan_steps", (int)planDefault, min: 1, max: MaxArenaSteps);
        // Переходный ключ: арена шагов кэша маршрутов этапа 1 (RoutePool) живёт до H3, где RouteStore её заменит и ключ уйдёт.
        long legacySteps = Math.Min(MaxArenaSteps, (long)c.MaxRoutes * 64);
        c.MaxRouteSteps = r.Int("max_route_steps", (int)legacySteps, min: 1, max: MaxArenaSteps);
        c.MaxConnectors = r.Int("max_connectors", checked(c.MaxNodes * 16), min: 1);
        c.MaxConflicts = r.Int("max_conflicts", checked(c.MaxConnectors * 6), min: 1);
        r.Finish();
        return c;
    }
}
