using System;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core;

/// <summary>
/// Создание <see cref="World"/> (core-api 3, 10): валидация <see cref="ScenarioDesc"/>, выделение всех массивов,
/// построение сети. Единственное место ядра, где допустимы аллокации; исключений нет - только <see cref="CreateStatus"/>.
/// </summary>
internal static class WorldBuilder
{
    /// <summary>Верхний предел любой ёмкости (защита от случайных значений вроде int.MaxValue).</summary>
    public const int MaxCapacity = 1 << 24;

    public const int MaxVehicleTypes = 256; // VehicleStore.Type - byte

    public static CreateStatus Create(in ScenarioDesc desc, out World? world)
    {
        world = null;
        CreateStatus st = ValidateCapacities(in desc.Capacities);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateVehicleTypes(desc.VehicleTypes);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateDemand(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = ValidateTransit(in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        var w = new World(in desc, WithDefaults(desc.Rules), (VehicleTypeDesc[])desc.VehicleTypes.Clone());
        st = BuildNetwork(w, in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        st = BuildZones(w, in desc);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        w.TargetVehicles = desc.Demand.TargetVehicles;
        SyncStructuralCounters(w);
        world = w;
        return CreateStatus.Ok;
    }

    /// <summary>
    /// Строит сеть. Явная сеть (<c>desc.Network</c>) - узлы без прилипания друг к другу, сегменты, connector'ы всех узлов.
    /// </summary>
    public static CreateStatus BuildNetwork(World w, in ScenarioDesc desc)
    {
        if (desc.City.Enabled)
        {
            return BuildCity(w, in desc);
        }

        NodeDesc[]? nodes = desc.Network.Nodes;
        SegmentDesc[]? segments = desc.Network.Segments;
        int nodeCount = nodes?.Length ?? 0;
        int segmentCount = segments?.Length ?? 0;

        for (int i = 0; i < nodeCount; i++)
        {
            NodeDesc n = nodes![i];
            NetworkStatus st = w.Builder.AddNode(new Vec2(n.X, n.Z), n.Control, out int idx);
            if (st != NetworkStatus.Ok)
            {
                return st.ToCreateStatus();
            }

            // Прилипание сдвинуло бы индексы узлов относительно desc: явная сеть обязана быть разреженной (> SnapRadius).
            if (idx != i)
            {
                return CreateStatus.InvalidNetwork;
            }
        }

        for (int i = 0; i < segmentCount; i++)
        {
            SegmentDesc s = segments![i];
            NetworkStatus st = w.Builder.AddSegment(s.A, s.B, s.LanesAB, s.LanesBA, s.SpeedLimit, s.Kind, out _);
            if (st != NetworkStatus.Ok)
            {
                return st.ToCreateStatus();
            }
        }

        NetworkStatus built = w.Builder.BuildAllConnectorsEx();
        return built.ToCreateStatus();
    }

    /// <summary>
    /// Генератор города (city-generator 7): сеть, затем зоны (ZoneGenerator -> <c>Net.Zones</c>/<c>Net.ZoneLanes</c>)
    /// и автобусные маршруты (TransitGenerator -> <c>Net.Bus*</c> + <c>w.Transit</c> с путями); планы светофоров - в <c>w.SignalPlanSeeds</c>.
    /// </summary>
    private static CreateStatus BuildCity(World w, in ScenarioDesc desc)
    {
        CreateStatus st = CityGenerator.Generate(in desc.City, in w.Rules, in w.Caps, w.Builder, ref w.RngCityGen, out CityGenResult city);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        w.CityInfo = city;
        w.SignalPlanSeeds = city.SignalPlans ?? System.Array.Empty<SignalPlanSeed>();
        w.SignalCount = w.SignalPlanSeeds.Length;
        if (w.SignalCount > w.SignalStates.Length)
        {
            return CreateStatus.NetworkTooLarge;
        }

        NetworkStore net = w.Net;
        var zones = new GenZones(w.Caps.MaxZones, w.Caps.MaxLanes);
        st = ZoneGenerator.Generate(in desc.Demand, city.Width, city.Height, net, zones, ref w.RngZones, out _);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        Array.Copy(zones.Zones, net.Zones, zones.ZoneCount);
        Array.Copy(zones.ZoneLanes, net.ZoneLanes, zones.ZoneLaneCount);
        net.ZoneCount = zones.ZoneCount;
        net.ZoneLaneCount = zones.ZoneLaneCount;

        var transit = new GenTransit(w.Caps.MaxBusRoutes, w.Caps.MaxBusStops, w.Caps.MaxBusStopsPerRoute);
        st = TransitGenerator.Generate(in desc.Transit, city.Width, city.Height, net, transit, ref w.RngTransit, out _);
        if (st != CreateStatus.Ok)
        {
            return st;
        }

        Array.Copy(transit.Routes, net.BusRoutes, transit.RouteCount);
        Array.Copy(transit.Stops, net.BusStops, transit.StopCount);
        Array.Copy(transit.RouteStops, net.BusRouteStops, transit.StopCount);
        net.BusRouteCount = transit.RouteCount;
        net.BusStopCount = transit.StopCount;
        net.BusRouteStopCount = transit.StopCount;
        w.Transit = transit;
        return CreateStatus.Ok;
    }

    private static CreateStatus BuildZones(World w, in ScenarioDesc desc)
    {
        if (desc.City.Enabled)
        {
            return CreateStatus.Ok; // зоны уже построены ZoneGenerator в BuildCity
        }

        ZoneDesc[]? zones = desc.Demand.Zones;
        int n = zones?.Length ?? 0;
        if (n > w.Net.Zones.Length)
        {
            return CreateStatus.NetworkTooLarge;
        }

        for (int i = 0; i < n; i++)
        {
            ZoneDesc z = zones![i];
            w.Net.Zones[i] = new ZoneData
            {
                Center = new Vec2(z.CenterX, z.CenterZ),
                HalfSize = z.HalfSize,
                Weight = z.Weight,
                FirstSpawnLane = 0, // TODO(gen): полосы появления назначает ZoneGenerator
                SpawnLaneCount = 0,
                IsExternal = z.IsExternal ? (byte)1 : (byte)0,
            };
        }

        w.Net.ZoneCount = n;
        return CreateStatus.Ok;
    }

    public static void SyncStructuralCounters(World w)
    {
        w.Counters.NodeCount = w.Net.NodeCount;
        w.Counters.SegmentCount = w.Net.SegmentCount;
        w.Counters.LaneCount = w.Net.LaneCount;
        w.Counters.ZoneCount = w.Net.ZoneCount;
        w.Counters.SignalizedNodes = w.SignalCount;
        w.Counters.ActiveVehicles = w.Vehicles.AliveCount;
    }

    private static CreateStatus ValidateCapacities(in SimCapacities c)
    {
        // Обязательные (> 0).
        if (!InRange(c.MaxVehicles, 1) || !InRange(c.MaxNodes, 1) || !InRange(c.MaxSegments, 1) || !InRange(c.MaxLanes, 1) ||
            !InRange(c.MaxLanePoints, 1) || !InRange(c.MaxPendingTrips, 1) || !InRange(c.CommandQueueCapacity, 1) ||
            !InRange(c.EventCapacity, 1))
        {
            return CreateStatus.InvalidCapacity;
        }

        // Необязательные (>= 0).
        if (!InRange(c.MaxZones, 0) || !InRange(c.MaxBusRoutes, 0) || !InRange(c.MaxBusStops, 0) ||
            !InRange(c.MaxBusStopsPerRoute, 0) || !InRange(c.MaxRoutes, 0) || !InRange(c.MaxRouteSteps, 0) ||
            !InRange(c.PathfindBudgetPerTick, 0) || !InRange(c.MaxSignalizedNodes, 0))
        {
            return CreateStatus.InvalidCapacity;
        }

        // Произведение BusRoutes * StopsPerRoute выделяется одним массивом.
        if ((long)c.MaxBusRoutes * c.MaxBusStopsPerRoute > MaxCapacity)
        {
            return CreateStatus.InvalidCapacity;
        }

        return CreateStatus.Ok;
    }

    private static bool InRange(int v, int min) => v >= min && v <= MaxCapacity;

    private static CreateStatus ValidateVehicleTypes(VehicleTypeDesc[]? types)
    {
        if (types == null || types.Length == 0 || types.Length > MaxVehicleTypes)
        {
            return CreateStatus.InvalidVehicleType;
        }

        for (int i = 0; i < types.Length; i++)
        {
            ref readonly VehicleTypeDesc t = ref types[i];
            if (!Positive(t.Length) || !Positive(t.Width) || !Positive(t.MaxSpeed) || !Positive(t.SpeedFactor) ||
                !Positive(t.Accel) || !Positive(t.ComfortDecel) || !NonNegative(t.HeadwayTime) || !NonNegative(t.MinGap) ||
                !Positive(t.Delta) || t.LaneChangeDurationTicks < 0 ||
                (t.Category != VehicleCategory.Car && t.Category != VehicleCategory.Bus))
            {
                return CreateStatus.InvalidVehicleType;
            }
        }

        return CreateStatus.Ok;
    }

    private static CreateStatus ValidateDemand(in ScenarioDesc desc)
    {
        DemandDesc d = desc.Demand;
        if (d.TargetVehicles < 0 || d.SpawnRampTicks < 0)
        {
            return CreateStatus.InvalidDemand;
        }

        if (d.TypeMix != null)
        {
            if (d.TypeMix.Length > desc.VehicleTypes.Length)
            {
                return CreateStatus.InvalidDemand;
            }

            for (int i = 0; i < d.TypeMix.Length; i++)
            {
                if (!NonNegative(d.TypeMix[i]))
                {
                    return CreateStatus.InvalidDemand;
                }
            }
        }

        int zones = d.Zones?.Length ?? 0;
        for (int i = 0; i < zones; i++)
        {
            ZoneDesc z = d.Zones![i];
            if (!IsFinite(z.CenterX) || !IsFinite(z.CenterZ) || !Positive(z.HalfSize) || !NonNegative(z.Weight))
            {
                return CreateStatus.InvalidDemand;
            }
        }

        int od = d.OdPairs?.Length ?? 0;
        for (int i = 0; i < od; i++)
        {
            OdPairDesc p = d.OdPairs![i];
            if ((uint)p.FromZone >= (uint)zones || (uint)p.ToZone >= (uint)zones || !NonNegative(p.Weight))
            {
                return CreateStatus.InvalidDemand;
            }
        }

        return CreateStatus.Ok;
    }

    private static CreateStatus ValidateTransit(in ScenarioDesc desc)
    {
        TransitDesc t = desc.Transit;
        int explicitRoutes = t.RoutesExplicit?.Length ?? 0;
        if (t.Routes < 0 || t.HeadwayTicks < 0)
        {
            return CreateStatus.InvalidTransit;
        }

        if ((t.Routes > 0 || explicitRoutes > 0) && (uint)t.BusType >= (uint)desc.VehicleTypes.Length)
        {
            return CreateStatus.InvalidTransit;
        }

        // TODO(step 4): сами автобусные маршруты и остановки строит Transit; пока они только валидируются.
        return CreateStatus.Ok;
    }

    /// <summary>Нулевые поля правил (desc, собранный вручную в тестах) заменяются значениями по умолчанию из спеки.</summary>
    private static RulesDesc WithDefaults(RulesDesc r)
    {
        if (!(r.MinPhysicalGap > 0f))
        {
            r.MinPhysicalGap = 0.5f;
        }

        if (r.StuckThresholdTicks <= 0)
        {
            r.StuckThresholdTicks = 1200;
        }

        if (!(r.StuckSpeedThreshold > 0f))
        {
            r.StuckSpeedThreshold = 0.1f;
        }

        if (!(r.GridlockStuckShare > 0f))
        {
            r.GridlockStuckShare = 0.05f;
        }

        if (r.GridlockMinStuck <= 0)
        {
            r.GridlockMinStuck = 10;
        }

        if (r.GridlockHoldTicks <= 0)
        {
            r.GridlockHoldTicks = 1200;
        }

        if (r.MobilEveryTicks <= 0)
        {
            r.MobilEveryTicks = 10;
        }

        return r;
    }

    private static bool IsFinite(float v) => float.IsFinite(v);

    private static bool Positive(float v) => float.IsFinite(v) && v > 0f;

    private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0f;
}
