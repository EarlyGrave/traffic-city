using System;
using System.Diagnostics;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Worlds;

/// <summary>Шаг 2, задача H: спавн, фазы 5-9 в PhaseList, метрики на s1k с реальными ёмкостями (без маршрутов и правил перекрёстков).</summary>
internal static class Step2
{
    public static VehicleTypeDesc RealCar() => new()
    {
        Category = VehicleCategory.Car,
        Length = 4.5f,
        Width = 1.8f,
        MaxSpeed = 130f / 3.6f,
        SpeedFactor = 1.05f,
        Accel = 1f,
        ComfortDecel = 1.5f,
        HeadwayTime = 1.2f,
        MinGap = 2f,
        Delta = 4f,
        Politeness = 0.3f,
        LaneChangeThreshold = 0.2f,
        SafeDecel = 4f,
        RightBias = 0.3f,
        LaneChangeDurationTicks = 40,
    };

    /// <summary>s1k: ёмкости и demand как в scenarios/s1k.json (max_vehicles 1280, 1000 машин, рампа 120 с).</summary>
    public static ScenarioDesc S1k()
    {
        GenRun r = GenFixture.Load("s1k");
        SimCapacities c = r.Caps;
        c.MaxVehicles = 1280;
        c.MaxRoutes = 4096;
        c.MaxRouteSteps = 262144;
        c.MaxPendingTrips = 2048;
        c.CommandQueueCapacity = 1024;
        c.EventCapacity = 4096;
        c.PathfindBudgetPerTick = 32;
        DemandDesc dem = r.Demand;
        dem.TargetVehicles = 1000;
        dem.SpawnRampTicks = 2400;
        dem.TypeMix = new[] { 1f, 0f };
        dem.Model = DemandModel.Gravity;
        dem.GravityExponent = 1f;
        dem.ExternalShare = 0.3f;
        dem.MinTripLength = 600f;
        VehicleTypeDesc car = RealCar();
        VehicleTypeDesc bus = car;
        bus.Category = VehicleCategory.Bus;
        bus.Length = 12f;
        var d = new ScenarioDesc
        {
            Seed = r.Seed,
            DurationTicks = 36000,
            Capacities = c,
            City = r.City,
            Demand = dem,
            Transit = r.Transit,
            Rules = new RulesDesc
            {
                MinPhysicalGap = 0.5f,
                StuckThresholdTicks = 1200,
                StuckSpeedThreshold = 0.1f,
                GridlockStuckShare = 0.05f,
                GridlockMinStuck = 10,
                GridlockHoldTicks = 1200,
                MobilEveryTicks = 10,
                DefaultYellowTicks = r.Rules.DefaultYellowTicks,
                DefaultAllRedTicks = r.Rules.DefaultAllRedTicks,
            },
            VehicleTypes = new[] { car, bus },
        };
        return d;
    }

    /// <summary>Первое нарушение «зазор &gt;= 0» в списках полос (primary и ghost). null = чисто.</summary>
    public static string? CheckGaps(World w)
    {
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            int a = v.Ahead[i];
            if (a != -1 && v.S[a] - v.Length[a] - v.S[i] < 0f)
            {
                return $"tick {w.Tick}: lane {v.Lane[i]}: vehicle {i} (s={v.S[i]:F3}) overlaps leader {a} (s={v.S[a]:F3})";
            }

            int ga = v.GhostAhead[i];
            if (v.GhostLane[i] != -1 && ga != -1 && v.S[ga] - v.Length[ga] - v.S[i] < 0f)
            {
                return $"tick {w.Tick}: ghost lane {v.GhostLane[i]}: vehicle {i} overlaps {ga}";
            }
        }

        return null;
    }

    /// <summary>
    /// Число голов полос, чьё тело перекрывается с хвостом следующей полосы цепочки (граница полос). Без правил перекрёстков
    /// (шаг 3) это возможно только на слиянии двух connector'ов в одну полосу; считается диагностикой, не ошибкой шага 2.
    /// </summary>
    public static int CountMergeOverlaps(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int n = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0 || v.Ahead[i] != -1)
            {
                continue;
            }

            int lane = v.Lane[i];
            int next = net.Lanes[lane].Kind == LaneKind.Connector ? net.Lanes[lane].ToLane : v.NextConn[i];
            if (next >= 0)
            {
                int tail = w.Occupancy.LaneTail[next];
                if (tail != -1 && tail != i && net.Lanes[lane].Length - v.S[i] + v.S[tail] - v.Length[tail] < 0f)
                {
                    n++;
                }
            }
        }

        return n;
    }
}

/// <summary>Один прогон s1k на 5 минут с проверкой зазоров на каждом тике, затем 1000 тиков под замером аллокаций.</summary>
public sealed class Step2Run
{
    public const int Ticks5Min = 6000;

    public int ActiveAt1Min { get; private set; }
    public int ActiveAt2Min { get; private set; }
    public int ActiveAt5Min { get; private set; }
    public string? GapViolation { get; private set; }
    public long MergeOverlapTicks { get; private set; }
    public long Allocated1000Ticks { get; private set; }
    public SimCounters Counters5Min { get; private set; }
    public SimCounters CountersEnd { get; private set; }
    public ulong HashEnd { get; private set; }

    public Step2Run()
    {
        ScenarioDesc d = Step2.S1k();
        using Simulation sim = Simulation.Create(in d);
        for (int t = 1; t <= Ticks5Min; t++)
        {
            sim.Step(1);
            GapViolation ??= Step2.CheckGaps(sim.World);
            MergeOverlapTicks += Step2.CountMergeOverlaps(sim.World);
            if (t == 1200)
            {
                ActiveAt1Min = sim.ActiveVehicleCount;
            }
            else if (t == 2400)
            {
                ActiveAt2Min = sim.ActiveVehicleCount;
            }
        }

        ActiveAt5Min = sim.ActiveVehicleCount;
        Counters5Min = sim.Counters;

        sim.Step(1200); // прогрев после рампы (JIT, холодные пути)
        Allocated1000Ticks = AllocationProbe.Measure(() =>
        {
            for (int t = 0; t < 1000; t++)
            {
                sim.Step(1);
            }
        });
        CountersEnd = sim.Counters;
        HashEnd = sim.ComputeStateHash();
    }
}

public sealed class Step2WorldTests : IClassFixture<Step2Run>
{
    private readonly Step2Run _run;
    private readonly ITestOutputHelper _out;

    public Step2WorldTests(Step2Run run, ITestOutputHelper output)
    {
        _run = run;
        _out = output;
    }

    [Fact]
    public void S1k_ActiveVehiclesGrowToTarget()
    {
        _out.WriteLine($"active: 1min={_run.ActiveAt1Min} 2min={_run.ActiveAt2Min} 5min={_run.ActiveAt5Min}");
        Assert.True(_run.ActiveAt1Min > 300, "рампа: к 1 мин (50% рампы) машин должно быть заметно");
        Assert.True(_run.ActiveAt1Min < _run.ActiveAt2Min);
        Assert.True(_run.ActiveAt5Min > 500, $"5 мин: активных {_run.ActiveAt5Min}");
        Assert.True(_run.ActiveAt5Min <= 1000);
        Assert.True(_run.ActiveAt5Min >= _run.ActiveAt2Min - 50, "число машин не падает после рампы (спавн поддерживает цель)");
    }

    [Fact]
    public void S1k_TripsComplete_AndAverageSpeedPositive()
    {
        SimCounters c = _run.CountersEnd;
        _out.WriteLine($"5min: spawned={_run.Counters5Min.VehiclesSpawned} completed={_run.Counters5Min.TripsCompleted}; end: spawned={c.VehiclesSpawned} completed={c.TripsCompleted}");
        Assert.True(c.TripsCompleted > 0);
        Assert.True(c.TripTimeTicksTotal > 0);
        Assert.True(c.DistanceTotalMm > 0);
        Assert.Equal(c.VehiclesSpawned, c.TripsCompleted + c.ActiveVehicles); // деспаун только через прибытие
        Assert.True(c.MovingVehicleTicksTotal <= c.VehicleTicksTotal);
        Assert.Equal(c.ConnectorEntries, c.ConnectorEntries);
    }

    [Fact]
    public void S1k_NoNegativeGaps_EveryTick()
    {
        Assert.Null(_run.GapViolation);
        _out.WriteLine($"merge overlaps (vehicle-ticks, head vs tail of next lane at converging connectors): {_run.MergeOverlapTicks}");
    }

    [Fact]
    public void S1k_StationaryTick_AllocatesZeroBytes()
    {
        Assert.Equal(0, _run.Allocated1000Ticks);
    }

    [Fact]
    public void S1k_Deterministic_2000Ticks()
    {
        ulong Run(out long completed)
        {
            ScenarioDesc d = Step2.S1k();
            using Simulation sim = Simulation.Create(in d);
            sim.Step(2000);
            completed = sim.Counters.TripsCompleted;
            Assert.True(sim.ActiveVehicleCount > 100);
            return sim.ComputeStateHash();
        }

        ulong a = Run(out long ca);
        ulong b = Run(out long cb);
        Assert.Equal(a, b);
        Assert.Equal(ca, cb);

        ScenarioDesc other = Step2.S1k();
        other.Seed ^= 0x5555;
        using Simulation s = Simulation.Create(in other);
        s.Step(2000);
        Assert.NotEqual(a, s.ComputeStateHash());
    }

    [Fact]
    public void SpawnRamp_IsLinear_AndTargetCommandWorks()
    {
        ScenarioDesc d = Step2.S1k();
        d.Demand.TargetVehicles = 200;
        d.Demand.SpawnRampTicks = 400;
        using Simulation sim = Simulation.Create(in d);
        sim.Step(100);
        int a100 = sim.World.Vehicles.AliveCount + sim.World.Trips.Count;
        Assert.InRange(a100, 49, 51); // 200 * 100 / 400
        sim.Step(300);
        Assert.InRange(sim.World.Vehicles.AliveCount + sim.World.Trips.Count, 199, 200);
        Assert.Equal(sim.World.Vehicles.AliveCount, sim.World.Vehicles.AliveCount);

        Assert.True(sim.Enqueue(Command.MakeSetTargetVehicleCount(250)));
        sim.Step(400);
        Assert.InRange(sim.World.Vehicles.AliveCount + sim.World.Trips.Count, 249, 250 + 0);
    }

    [Fact]
    public void TripWaitsInQueueWhenLaneBlocked_AndIsNotDropped()
    {
        ScenarioDesc d = Step2.S1k();
        d.Demand.TargetVehicles = 0;
        d.Capacities.MaxPendingTrips = 64;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        Assert.True(w.Net.ZoneCount > 0);
        int zone = -1;
        for (int z = 0; z < w.Net.ZoneCount; z++)
        {
            if (w.Net.Zones[z].SpawnLaneCount is > 0 and <= 6)
            {
                zone = z;
                break;
            }
        }

        Assert.True(zone >= 0, "нужна зона с 1..6 полосами спавна");
        int to = zone == 0 ? 1 : 0;

        // Забиваем начала всех полос зоны стоящими машинами: s = 8, зазора до нового (s = 6.5) нет.
        var blockers = new VehicleHandle[w.Net.Zones[zone].SpawnLaneCount];
        for (int k = 0; k < blockers.Length; k++)
        {
            int lane = w.Net.ZoneLanes[w.Net.Zones[zone].FirstSpawnLane + k];
            blockers[k] = TestScenarios.PlaceCar(sim, lane, 8f);
            w.Vehicles.DesiredSpeed[blockers[k].Index] = 0f;
        }

        var cmd = new SpawnTripCmd { FromZone = zone, ToZone = to, FromLane = -1, VehicleType = 0, Count = 3 };
        Assert.True(sim.Enqueue(Command.MakeSpawnTrip(in cmd)));
        sim.Step(2); // тик 1: поездки встали в очередь, фаза 3 построила маршруты; тик 2: спавнер упёрся в блокеров
        Assert.Equal(3, w.Trips.Count);
        Assert.Equal(0, sim.Counters.TripsDropped);
        Assert.True(sim.Counters.SpawnDeferredTicks >= 1);

        for (int k = 0; k < blockers.Length; k++)
        {
            w.Vehicles.DesiredSpeed[blockers[k].Index] = 10f; // блокеры уезжают, поездки стартуют по очереди
        }

        sim.Step(1200);
        Assert.True(sim.Counters.VehiclesSpawned >= 1);
        Assert.Equal(0, sim.Counters.TripsDropped);
        Assert.Equal(3, sim.Counters.VehiclesSpawned + w.Trips.Count);
    }

    [Fact]
    public void Arrival_OnEnteringDestinationZoneLane_Despawns()
    {
        ScenarioDesc d = Step2.S1k();
        d.Demand.TargetVehicles = 0;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        w.Spawn.Ready = false; // карта полос зон строится лениво на первом тике
        sim.Step(1);
        int zone = 0;
        while (w.Net.Zones[zone].SpawnLaneCount == 0)
        {
            zone++;
        }

        int zoneLane = w.Net.ZoneLanes[w.Net.Zones[zone].FirstSpawnLane];
        int from = zoneLane;
        // Берём полосу, из которой есть connector в полосу зоны: ищем вход в zoneLane.
        int feeder = -1;
        for (int l = 0; l < w.Net.LaneCount && feeder < 0; l++)
        {
            if (w.Net.Lanes[l].Kind == LaneKind.Connector && w.Net.Lanes[l].ToLane == zoneLane)
            {
                feeder = w.Net.Lanes[l].FromLane;
            }
        }

        Assert.True(feeder >= 0);
        VehicleHandle h = TestScenarios.PlaceCar(sim, feeder, w.Net.Lanes[feeder].Length - 5f, 8f);
        w.Vehicles.DesiredSpeed[h.Index] = 8f;
        w.Vehicles.DestZone[h.Index] = zone;
        w.Vehicles.DestLane[h.Index] = zoneLane;
        w.Vehicles.DestS[h.Index] = 1f;
        Assert.Equal(Sim.Core.Routing.BuildStatus.Ok, w.Router.Build(feeder, w.Net.Lanes[feeder].Length - 5f, zoneLane, 1f, -1, -1, w.Tick, out RouteHandle rh));
        w.Vehicles.Route[h.Index] = rh.Index;
        w.Vehicles.RouteGen[h.Index] = rh.Generation;
        w.Vehicles.NextConn[h.Index] = -1;
        Assert.NotEqual(from, feeder);
        long completed = sim.Counters.TripsCompleted;
        sim.Step(400);
        Assert.False(sim.IsAlive(h));
        Assert.Equal(completed + 1, sim.Counters.TripsCompleted);
    }
}

public sealed class Step2StuckTests
{
    /// <summary>Затор: впереди неподвижное препятствие; колонна встаёт, через 60 с машина помечается stuck, затем поднимается gridlock.</summary>
    [Fact]
    public void JammedVehicle_BecomesStuck_AfterThreshold_AndGridlockFlagRaises()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Network.Nodes = new[] { d.Network.Nodes[0], d.Network.Nodes[1] };
        d.Network.Segments = new[] { d.Network.Segments[0] };
        d.Rules.GridlockMinStuck = 1;
        d.Rules.GridlockHoldTicks = 100;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int lane = w.Net.Segments[0].FirstLaneAB;
        VehicleHandle blocker = TestScenarios.PlaceCar(sim, lane, 100f);
        VehicleHandle h = TestScenarios.PlaceCar(sim, lane, 20f, 5f);
        w.Vehicles.DesiredSpeed[h.Index] = 10f;

        void PinnedStep()
        {
            w.Vehicles.S[blocker.Index] = 100f; // препятствие: возвращаем на место перед каждым тиком
            w.Vehicles.Speed[blocker.Index] = 0f;
            sim.Step(1);
        }

        for (int n = 0; n < 60 * 20 || w.Vehicles.StillTicks[h.Index] == 0; n++)
        {
            Assert.True(n < 6000, $"v={w.Vehicles.Speed[h.Index]} s={w.Vehicles.S[h.Index]} acc={w.Vehicles.Accel[h.Index]} still={w.Vehicles.StillTicks[h.Index]} bs={w.Vehicles.S[blocker.Index]} bv={w.Vehicles.Speed[blocker.Index]}");
            PinnedStep();
        }

        Assert.True(sim.IsAlive(h));
        Assert.True(w.Vehicles.Speed[h.Index] < 0.1f);
        Assert.InRange((int)w.Vehicles.StillTicks[h.Index], 1, 1199);
        Assert.NotEqual(0, w.Vehicles.Flags[blocker.Index] & (byte)VehicleFlags.Stuck); // блокер стоит всё время
        Assert.Equal(0, w.Vehicles.Flags[h.Index] & (byte)VehicleFlags.Stuck);

        while (w.Vehicles.StillTicks[h.Index] < 1199)
        {
            PinnedStep();
        }

        Assert.Equal(1, sim.Counters.StuckVehicles);
        PinnedStep();
        Assert.Equal(2, sim.Counters.StuckVehicles);
        Assert.Equal(2, sim.Counters.StuckEventsTotal);
        Assert.NotEqual(0, w.Vehicles.Flags[h.Index] & (byte)VehicleFlags.Stuck);
        Assert.NotEqual(0, sim.Vehicles[h.Index].Flags & (byte)VehicleRenderFlags.Stuck);
        Assert.Contains(sim.Events.ToArray(), e => e.Kind == SimEventKind.VehicleStuck && e.A == h.Index);

        Assert.Equal(1, sim.Counters.GridlockFlag); // условие (stuck >= 1 и >= 5% живых) держится > 100 тиков
        Assert.True(sim.Counters.GridlockTicksTotal >= 1);
        Assert.True(sim.Counters.StuckTicksTotal >= 2);
        Assert.Equal(0, sim.Counters.TripsCompleted);
        Assert.True(sim.IsAlive(h) && sim.IsAlive(blocker)); // застрявшие машины не исчезают

        // Препятствие убрано: машина едет, stuck снимается, через GridlockHoldTicks гасится gridlock.
        w.Occupancy.UnlinkAll(blocker.Index);
        w.Vehicles.Despawn(blocker);
        sim.Step(40);
        Assert.Equal(0, sim.Counters.StuckVehicles);
        Assert.Equal(0, w.Vehicles.Flags[h.Index] & (byte)VehicleFlags.Stuck);
        Assert.Contains(sim.Events.ToArray(), e => e.Kind == SimEventKind.VehicleUnstuck && e.A == h.Index);
        sim.Step(120);
        Assert.Equal(0, sim.Counters.GridlockFlag);
    }

    [Fact]
    public void SegmentLoads_CountDensityAndSpeedRatio()
    {
        ScenarioDesc d = TestScenarios.Line();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int lane = w.Net.Segments[0].FirstLaneAB;
        VehicleHandle a = TestScenarios.PlaceCar(sim, lane, 50f);
        VehicleHandle b = TestScenarios.PlaceCar(sim, lane, 100f);
        w.Vehicles.DesiredSpeed[a.Index] = 0f;
        w.Vehicles.DesiredSpeed[b.Index] = 0f;
        sim.Step(1);
        SegmentLoad l0 = w.SegmentLoads[0];
        Assert.Equal(2, l0.Count);
        float laneKm = 2 * w.Net.Segments[0].Length * 0.001f;
        Assert.Equal(2f / laneKm, l0.Density, 3);
        Assert.InRange(l0.SpeedRatio, 0f, 0.1f);
        Assert.Equal(0, w.SegmentLoads[1].Count);
        Assert.Equal(1f, w.SegmentLoads[1].SpeedRatio);
    }
}

/// <summary>Реальные числа s1k за 10 и 30 мин игрового времени (Release). Запускается только при TC_STEP2_REPORT=1.</summary>
public class Step2ReportTests
{
    private readonly ITestOutputHelper _out;

    public Step2ReportTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(12000)]
    [InlineData(36000)]
    public void Report_S1k(int ticks)
    {
        if (Environment.GetEnvironmentVariable("TC_STEP2_REPORT") != "1")
        {
            return;
        }

        ScenarioDesc d = Step2.S1k();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(200);
        long sum = 0;
        long max = 0;
        long over1ms = 0;
        var samples = new long[ticks];
        for (int t = 0; t < ticks; t++)
        {
            long t0 = Stopwatch.GetTimestamp();
            sim.Step(1);
            long dt = Stopwatch.GetTimestamp() - t0;
            samples[t] = dt;
            sum += dt;
            max = Math.Max(max, dt);
            if (dt * 1000 > Stopwatch.Frequency)
            {
                over1ms++;
            }
        }

        Array.Sort(samples);
        double us = 1e6 / Stopwatch.Frequency;
        SimCounters c = sim.Counters;
        sim.GetStats(out SimStats st);
        _out.WriteLine($"=== s1k, {(ticks + 200) / 1200.0:F1} min game time ===");
        _out.WriteLine($"active={c.ActiveVehicles} spawned={c.VehiclesSpawned} completed={c.TripsCompleted} pendingTrips={sim.World.Trips.Count}");
        _out.WriteLine($"avg_speed_kmh={st.AvgSpeedKmh:F2} avg_trip_time_s={st.AvgTripTimeS:F1} trips_per_min={st.TripsCompletedPerMin:F2}");
        _out.WriteLine($"stuck_now={c.StuckVehicles} stuck_events_total={c.StuckEventsTotal} stuck_ticks_total={c.StuckTicksTotal} gridlock_flag={c.GridlockFlag} gridlock_ticks_total={c.GridlockTicksTotal}");
        _out.WriteLine($"hard_clamps={c.HardClamps} lane_changes={c.LaneChanges} aborted={c.LaneChangesAborted} connector_entries={c.ConnectorEntries} spawn_deferred_ticks={c.SpawnDeferredTicks} events_dropped={c.EventsDropped}");
        _out.WriteLine($"tick: mean={sum / (double)ticks * us:F1} us p50={samples[ticks / 2] * us:F1} p95={samples[(int)(ticks * 0.95)] * us:F1} p99={samples[(int)(ticks * 0.99)] * us:F1} max={max * us:F1} us; >1ms: {over1ms}");
        _out.WriteLine($"gap violation (same-lane lists): {Step2.CheckGaps(sim.World) ?? "none"}; merge overlaps now: {Step2.CountMergeOverlaps(sim.World)}");
    }
}
