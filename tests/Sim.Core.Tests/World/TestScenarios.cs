using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Tests.Worlds;

/// <summary>Фикстуры для тестов фасада: малая явная сеть и ручная расстановка машин через внутренние хранилища.</summary>
internal static class TestScenarios
{
    public const int MaxVehicles = 64;

    public static VehicleTypeDesc Car() => new()
    {
        Category = VehicleCategory.Car,
        Length = 4.5f,
        Width = 1.8f,
        MaxSpeed = 33f,
        SpeedFactor = 1f,
        Accel = 1.5f,
        ComfortDecel = 2f,
        HeadwayTime = 1.5f,
        MinGap = 2f,
        Delta = 4f,
        Politeness = 0.3f,
        LaneChangeThreshold = 0.2f,
        SafeDecel = 4f,
        RightBias = 0.2f,
        LaneChangeDurationTicks = 40,
    };

    public static SimCapacities Caps() => new()
    {
        MaxVehicles = MaxVehicles,
        MaxNodes = 32,
        MaxSegments = 32,
        MaxLanes = 512,
        MaxLanePoints = 4096,
        MaxZones = 4,
        MaxBusRoutes = 2,
        MaxBusStops = 4,
        MaxBusStopsPerRoute = 4,
        MaxRoutes = 8,
        MaxRouteSteps = 64,
        MaxPendingTrips = 16,
        CommandQueueCapacity = 8,
        EventCapacity = 64,
        PathfindBudgetPerTick = 4,
        MaxSignalizedNodes = 2,
    };

    /// <summary>Три узла на прямой (0, 200, 400 м), два сегмента 1+1 полос, две зоны. Полоса 0 - A→B первого сегмента.</summary>
    public static ScenarioDesc Line(ulong seed = 1)
    {
        return new ScenarioDesc
        {
            Seed = seed,
            DurationTicks = 1000,
            Capacities = Caps(),
            Network = new NetworkDesc
            {
                Nodes = new[]
                {
                    new NodeDesc { X = 0, Z = 0, Control = NodeControl.None },
                    new NodeDesc { X = 200, Z = 0, Control = NodeControl.Yield },
                    new NodeDesc { X = 400, Z = 0, Control = NodeControl.None },
                },
                Segments = new[]
                {
                    new SegmentDesc { A = 0, B = 1, LanesAB = 1, LanesBA = 1, SpeedLimit = 13.9f, Kind = RoadKind.Street },
                    new SegmentDesc { A = 1, B = 2, LanesAB = 1, LanesBA = 1, SpeedLimit = 13.9f, Kind = RoadKind.Street },
                },
            },
            Demand = new DemandDesc
            {
                TargetVehicles = 0,
                Zones = new[]
                {
                    new ZoneDesc { CenterX = 0, CenterZ = 0, HalfSize = 50, Weight = 1 },
                    new ZoneDesc { CenterX = 400, CenterZ = 0, HalfSize = 50, Weight = 1 },
                },
            },
            VehicleTypes = new[] { Car() },
        };
    }

    /// <summary>Ставит машину на полосу (минуя спавнер). Возвращает handle.</summary>
    internal static VehicleHandle PlaceCar(Simulation sim, int lane, float s, float speed = 0f)
    {
        World w = sim.World;
        Assert.True(w.Vehicles.TrySpawn(out VehicleHandle h));
        int i = h.Index;
        w.Vehicles.S[i] = s;
        w.Vehicles.Speed[i] = speed;
        w.Vehicles.Length[i] = w.Types[0].Length;
        w.Vehicles.Type[i] = 0;
        w.Occupancy.InsertSorted(lane, i);
        return h;
    }
}
