using System;
using System.Diagnostics;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Worlds;

/// <summary>Шаг 3, 3-R4/R5: маршрутизация подключена к симуляции (фаза 3, спавн с маршрутом, движение по маршруту, прибытие по DestLane).</summary>
internal static class Step3
{
    /// <summary>s1k как в scenarios/s1k.json (ёмкости маршрутов тоже).</summary>
    public static ScenarioDesc S1k()
    {
        ScenarioDesc d = Step2.S1k();
        d.Capacities.MaxRoutes = 8192;
        d.Capacities.MaxRouteSteps = 262144;
        return d;
    }

    /// <summary>
    /// Первое нарушение инварианта «машина едет по своему маршруту»: направленный сегмент (для connector'а - откуда и куда) обязан
    /// совпадать с шагами маршрута вокруг курсора. Машины без маршрута допустимы только в очереди перемаршрутизации. null = чисто.
    /// </summary>
    public static string? CheckRoutes(World w, out int routeless)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        RouteStep[] steps = w.Routes.Steps;
        routeless = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            if (v.DestLane[i] < 0)
            {
                return $"tick {w.Tick}: vehicle {i} has no destination lane";
            }

            int r = v.Route[i];
            if (r < 0)
            {
                routeless++;
                continue;
            }

            if (!w.Routes.Pool.IsAlive(r, v.RouteGen[i]))
            {
                return $"tick {w.Tick}: vehicle {i} holds a dead route {r}/{v.RouteGen[i]}";
            }

            int first = w.Routes.First[r];
            int count = w.Routes.Count[r];
            int c = v.RouteCursor[i];
            int lane = v.Lane[i];
            ref readonly LaneData ld = ref net.Lanes[lane];
            if (ld.Kind == LaneKind.Normal)
            {
                if (c < 0 || c >= count || (c & 1) != 0 || Dir(net, steps[first + c].Lane) != Dir(net, lane))
                {
                    return $"tick {w.Tick}: vehicle {i} on lane {lane} (dirseg {Dir(net, lane)}) cursor {c}/{count} off its route";
                }
            }
            else
            {
                bool okFrom = c < 1 || Dir(net, steps[first + c - 1].Lane) == Dir(net, ld.FromLane);
                bool okTo = c + 1 >= count || Dir(net, steps[first + c + 1].Lane) == Dir(net, ld.ToLane);
                if (c >= count || !okFrom || !okTo)
                {
                    return $"tick {w.Tick}: vehicle {i} on connector {lane} cursor {c}/{count} off its route";
                }
            }
        }

        return null;
    }

    private static int Dir(NetworkStore net, int lane) => (net.Lanes[lane].Segment * 2) + net.Lanes[lane].Direction;
}

/// <summary>Один прогон s1k на 10 мин с проверкой зазоров, маршрутов и бюджета на каждом тике, затем 1000 тиков под замером аллокаций.</summary>
public sealed class Step3Run
{
    public const int Ticks10Min = 12000;
    public const int Budget = 32;

    public string? GapViolation { get; private set; }
    public string? RouteViolation { get; private set; }
    public string? BudgetViolation { get; private set; }
    public int MaxRouteless { get; private set; }
    public int MaxRerouteQueue { get; private set; }
    public int PeakRoutesAlive { get; private set; }
    public int PeakStepsUsed { get; private set; }
    public long Allocated1000Ticks { get; private set; }
    public SimCounters Counters10Min { get; private set; }
    public SimCounters CountersEnd { get; private set; }
    public ulong HashEnd { get; private set; }

    public Step3Run()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        long prevQueries = 0;
        for (int t = 1; t <= Ticks10Min; t++)
        {
            sim.Step(1);
            GapViolation ??= Step2.CheckGaps(w);
            string? rv = Step3.CheckRoutes(w, out int routeless);
            RouteViolation ??= rv;
            MaxRouteless = Math.Max(MaxRouteless, routeless);
            MaxRerouteQueue = Math.Max(MaxRerouteQueue, w.Rerouting.Count);
            if (routeless > w.Rerouting.Count)
            {
                RouteViolation ??= $"tick {w.Tick}: {routeless} routeless vehicles, reroute queue {w.Rerouting.Count}";
            }

            long q = sim.Counters.PathfindQueries;
            if (q - prevQueries > Budget)
            {
                BudgetViolation ??= $"tick {w.Tick}: {q - prevQueries} queries > budget {Budget}";
            }

            prevQueries = q;
            PeakRoutesAlive = Math.Max(PeakRoutesAlive, w.Routes.AliveCount);
            PeakStepsUsed = Math.Max(PeakStepsUsed, w.Routes.StepsUsed);
        }

        Counters10Min = sim.Counters;
        sim.Step(1200); // прогрев (JIT, холодные пути)
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

public sealed class Step3RoutingTests : IClassFixture<Step3Run>
{
    private readonly Step3Run _run;
    private readonly ITestOutputHelper _out;

    public Step3RoutingTests(Step3Run run, ITestOutputHelper output)
    {
        _run = run;
        _out = output;
    }

    [Fact]
    public void S1k_VehiclesFollowRoutes_EveryTick()
    {
        Assert.Null(_run.RouteViolation);
        _out.WriteLine($"max routeless vehicles at once: {_run.MaxRouteless}, max reroute queue: {_run.MaxRerouteQueue}, reroutes: {_run.Counters10Min.Reroutes}");
    }

    [Fact]
    public void S1k_TripsComplete_FarMoreThanRandomWalk()
    {
        SimCounters c = _run.Counters10Min;
        _out.WriteLine($"10 min: spawned={c.VehiclesSpawned} completed={c.TripsCompleted} dropped={c.TripsDropped} routeFailed={c.RouteFailed} active={c.ActiveVehicles}");
        Assert.True(c.TripsCompleted > 3 * 144, $"completed {c.TripsCompleted}; случайное блуждание шага 2 давало 144");
        Assert.Equal(c.VehiclesSpawned, c.TripsCompleted + c.ActiveVehicles); // деспаун только через прибытие
        Assert.Equal(0, c.TripsDropped); // на s1k все пары достижимы
    }

    [Fact]
    public void S1k_NoNegativeGaps_EveryTick() => Assert.Null(_run.GapViolation);

    [Fact]
    public void S1k_PathfindBudgetRespected_AndCacheWorks()
    {
        Assert.Null(_run.BudgetViolation);
        SimCounters c = _run.Counters10Min;
        _out.WriteLine($"queries={c.PathfindQueries} cacheHits={c.PathfindCacheHits} expanded={c.PathfindExpandedNodes} failed={c.RouteFailed} deferred={c.RouteDeferred} compactions={c.RouteCompactions} cacheSkips={c.RouteCacheSkips}");
        _out.WriteLine($"peak routes alive={_run.PeakRoutesAlive}/8192, peak arena steps={_run.PeakStepsUsed}/262144");
        Assert.True(c.PathfindQueries >= c.VehiclesSpawned);
        Assert.True(c.PathfindCacheHits > 0);
    }

    [Fact]
    public void S1k_StationaryTick_AllocatesZeroBytes() => Assert.Equal(0, _run.Allocated1000Ticks);

    [Fact]
    public void S1k_Deterministic_2000Ticks()
    {
        ulong Run(out long completed)
        {
            ScenarioDesc d = Step3.S1k();
            using Simulation sim = Simulation.Create(in d);
            sim.Step(2000);
            completed = sim.Counters.TripsCompleted;
            return sim.ComputeStateHash();
        }

        ulong a = Run(out long ca);
        ulong b = Run(out long cb);
        Assert.Equal(a, b);
        Assert.Equal(ca, cb);
    }

    [Fact]
    public void Trips_AreRoutedBeforeSpawn_AndUnreachableTripsNeverReachTheRoad()
    {
        ScenarioDesc d = Step3.S1k();
        d.Demand.TargetVehicles = 0;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int zone = FirstZoneWithLanes(w, 0);
        int to = FirstZoneWithLanes(w, zone + 1);

        var cmd = new SpawnTripCmd { FromZone = zone, ToZone = to, FromLane = -1, VehicleType = 0, Count = 5 };
        Assert.True(sim.Enqueue(Command.MakeSpawnTrip(in cmd)));
        sim.Step(1);
        Assert.Equal(5, w.Trips.Count);
        for (int k = 0; k < w.Trips.Count; k++)
        {
            Assert.True(w.Trips.At(k).HasRoute, "фаза 3 строит маршрут в том же тике (бюджет 32)");
            Assert.True(w.Trips.At(k).DestLane >= 0);
        }

        Assert.Equal(0, w.Vehicles.AliveCount);
        sim.Step(40);
        Assert.True(w.Vehicles.AliveCount >= 1);
        for (int i = 0; i < w.Vehicles.HighWater; i++)
        {
            if (w.Vehicles.Pool.IsAlive(i))
            {
                Assert.True(w.Vehicles.Route[i] >= 0 && w.Vehicles.DestLane[i] >= 0);
            }
        }
    }

    [Fact]
    public void BudgetLimitsQueries_PerTick_AndTripsWaitWithoutRoute()
    {
        ScenarioDesc d = Step3.S1k();
        d.Demand.TargetVehicles = 0;
        d.Capacities.PathfindBudgetPerTick = 8;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int zone = FirstZoneWithLanes(w, 0);
        int to = FirstZoneWithLanes(w, zone + 1);
        var cmd = new SpawnTripCmd { FromZone = zone, ToZone = to, FromLane = -1, VehicleType = 0, Count = 100 };
        Assert.True(sim.Enqueue(Command.MakeSpawnTrip(in cmd)));
        sim.Step(1);
        Assert.Equal(8, sim.Counters.PathfindQueries);
        Assert.Equal(92, w.Trips.Unrouted);
        Assert.True(sim.Counters.RouteDeferred > 0);
        long prev = sim.Counters.PathfindQueries;
        for (int t = 0; t < 20; t++)
        {
            sim.Step(1);
            Assert.True(sim.Counters.PathfindQueries - prev <= 8);
            prev = sim.Counters.PathfindQueries;
        }

        Assert.Equal(0, w.Trips.Unrouted);
        Assert.Equal(100, w.Trips.Count + w.Vehicles.AliveCount);
        Assert.Equal(0, sim.Counters.TripsDropped);
    }

    [Fact]
    public void UnreachableDestination_TripIsDropped_NotPlacedOnRoad()
    {
        ScenarioDesc d = Step3.S1k();
        d.Demand.TargetVehicles = 0;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int zone = FirstZoneWithLanes(w, 0);
        // Недостижимая цель по контракту Build: старт и цель на одной полосе, StartS > DestS (объезд круга не строится).
        int src = w.Net.ZoneLanes[w.Net.Zones[zone].FirstSpawnLane];
        PendingTrip trip = new()
        {
            FromZone = -1,
            ToZone = zone,
            FromLane = src,
            FromS = 50f,
            Type = 0,
            DestLane = src,
            DestS = 10f,
            Route = -1,
        };
        Assert.True(w.Trips.TryEnqueue(in trip));
        sim.Step(3);
        Assert.Equal(0, w.Trips.Count);
        Assert.Equal(0, w.Vehicles.AliveCount);
        Assert.Equal(1, sim.Counters.TripsDropped);
        Assert.Equal(1, sim.Counters.RouteFailed);
        Assert.Equal(0, sim.Counters.VehiclesSpawned);
    }

    [Fact]
    public void LaneMiss_VehicleKeepsMoving_AndIsRerouted()
    {
        ScenarioDesc d = Step3.S1k();
        d.Demand.TargetVehicles = 0;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        int zone = FirstZoneWithLanes(w, 0);
        int to = FirstZoneWithLanes(w, zone + 1);
        var cmd = new SpawnTripCmd { FromZone = zone, ToZone = to, FromLane = -1, VehicleType = 0, Count = 1 };
        Assert.True(sim.Enqueue(Command.MakeSpawnTrip(in cmd)));
        sim.Step(60);
        Assert.Equal(1, w.Vehicles.AliveCount);
        int i = 0;
        while (!w.Vehicles.Pool.IsAlive(i))
        {
            i++;
        }

        // Ломаем маршрут: ближайший connector - чужой, а следующая полоса маршрута - в направленном сегменте, куда с этой полосы не попасть.
        int r = w.Vehicles.Route[i];
        Assert.True(r >= 0);
        int cur = w.Vehicles.Lane[i];
        Assert.Equal(LaneKind.Normal, w.Net.Lanes[cur].Kind);
        int foreignConnector = -1;
        int foreignLane = -1;
        for (int l = 0; l < w.Net.LaneCount; l++)
        {
            ref readonly LaneData ld = ref w.Net.Lanes[l];
            if (foreignConnector < 0 && ld.Kind == LaneKind.Connector && ld.FromLane != cur)
            {
                foreignConnector = l;
            }

            if (foreignLane < 0 && ld.Kind == LaneKind.Normal && ld.Segment >= 0 && !ReachableFrom(w.Net, cur, ld))
            {
                foreignLane = l;
            }
        }

        Assert.True(foreignConnector >= 0 && foreignLane >= 0);
        int at = w.Routes.First[r] + w.Vehicles.RouteCursor[i];
        w.Routes.Steps[at + 1] = new RouteStep { Lane = foreignConnector, Node = -1 };
        w.Routes.Steps[at + 2] = new RouteStep { Lane = foreignLane, Node = -1 };
        long completed = sim.Counters.TripsCompleted;
        for (int t = 0; t < 12000 && sim.Counters.Reroutes == 0; t++)
        {
            sim.Step(1);
        }

        Assert.Equal(1, sim.Counters.Reroutes);
        sim.Step(1);
        Assert.True(!w.Vehicles.Pool.IsAlive(i) || w.Vehicles.Route[i] >= 0 || w.Rerouting.Count > 0);
        for (int t = 0; t < 12000 && sim.Counters.TripsCompleted == completed; t++)
        {
            sim.Step(1);
        }

        Assert.Equal(completed + 1, sim.Counters.TripsCompleted); // перемаршрутизированная машина всё равно приезжает
    }

    private static bool ReachableFrom(NetworkStore net, int lane, in LaneData other)
    {
        ref readonly LaneData l = ref net.Lanes[lane];
        for (int n = 0; n < l.NextCount; n++)
        {
            ref readonly LaneData to = ref net.Lanes[net.Lanes[net.LaneNext[l.FirstNext + n]].ToLane];
            if (to.Segment == other.Segment && to.Direction == other.Direction)
            {
                return true;
            }
        }

        return other.Segment == l.Segment && other.Direction == l.Direction;
    }

    private static int FirstZoneWithLanes(World w, int from)
    {
        for (int z = from; z < w.Net.ZoneCount; z++)
        {
            if (w.Net.Zones[z].SpawnLaneCount > 0 && w.Net.Zones[z].IsExternal == 0)
            {
                return z;
            }
        }

        throw new InvalidOperationException("no zone with spawn lanes");
    }
}

/// <summary>Реальные числа s1k за 10 и 30 мин игрового времени (Release). Запускается только при TC_STEP3_REPORT=1.</summary>
public class Step3ReportTests
{
    private readonly ITestOutputHelper _out;

    public Step3ReportTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(12000)]
    [InlineData(36000)]
    public void Report_S1k(int ticks)
    {
        if (Environment.GetEnvironmentVariable("TC_STEP3_REPORT") != "1")
        {
            return;
        }

        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        sim.Step(200);
        long sum = 0;
        long max = 0;
        long over1ms = 0;
        var samples = new long[ticks];
        int peakRoutes = 0;
        int peakSteps = 0;
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

            peakRoutes = Math.Max(peakRoutes, w.Routes.AliveCount);
            peakSteps = Math.Max(peakSteps, w.Routes.StepsUsed);
        }

        Array.Sort(samples);
        double us = 1e6 / Stopwatch.Frequency;
        SimCounters c = sim.Counters;
        sim.GetStats(out SimStats st);
        _out.WriteLine($"=== s1k + routing, {(ticks + 200) / 1200.0:F1} min game time ===");
        _out.WriteLine($"active={c.ActiveVehicles} spawned={c.VehiclesSpawned} completed={c.TripsCompleted} dropped={c.TripsDropped} pendingTrips={w.Trips.Count}");
        _out.WriteLine($"avg_speed_kmh={st.AvgSpeedKmh:F2} avg_trip_time_s={st.AvgTripTimeS:F1} trips_per_min={st.TripsCompletedPerMin:F2}");
        _out.WriteLine($"stuck_now={c.StuckVehicles} stuck_events_total={c.StuckEventsTotal} stuck_ticks_total={c.StuckTicksTotal} gridlock_flag={c.GridlockFlag} gridlock_ticks_total={c.GridlockTicksTotal}");
        _out.WriteLine($"pathfind: queries={c.PathfindQueries} cache_hits={c.PathfindCacheHits} hit_rate={st.PathfindCacheHitRate:F3} expanded={c.PathfindExpandedNodes} failed={c.RouteFailed} deferred={c.RouteDeferred} reroutes={c.Reroutes} compactions={c.RouteCompactions} cache_skips={c.RouteCacheSkips}");
        _out.WriteLine($"routes: peak alive={peakRoutes} (cap {w.Routes.Pool.Capacity}), peak arena={peakSteps} (cap {w.Routes.Steps.Length})");
        long vehSteps = 0;
        long trunkSteps = 0;
        int trunks = 0;
        for (int r = 0; r < w.Routes.Pool.HighWater; r++)
        {
            if (!w.Routes.Pool.IsAlive(r))
            {
                continue;
            }

            if (w.Routes.FromZone[r] >= 0)
            {
                trunks++;
                trunkSteps += w.Routes.Count[r];
            }
            else
            {
                vehSteps += w.Routes.Count[r];
            }
        }

        _out.WriteLine($"live now: vehicle routes steps={vehSteps} (avg {vehSteps / Math.Max(1.0, w.Routes.AliveCount - trunks):F1}/route), trunks={trunks} steps={trunkSteps}");
        _out.WriteLine($"hard_clamps={c.HardClamps} lane_changes={c.LaneChanges} aborted={c.LaneChangesAborted} connector_entries={c.ConnectorEntries} spawn_deferred_ticks={c.SpawnDeferredTicks} events_dropped={c.EventsDropped}");
        _out.WriteLine($"tick: mean={sum / (double)ticks * us:F1} us p50={samples[ticks / 2] * us:F1} p95={samples[(int)(ticks * 0.95)] * us:F1} p99={samples[(int)(ticks * 0.99)] * us:F1} max={max * us:F1} us; >1ms: {over1ms}");
        _out.WriteLine($"gap violation (same-lane lists): {Step2.CheckGaps(w) ?? "none"}; route violation: {Step3.CheckRoutes(w, out int rl) ?? "none"}; routeless now: {rl}");
    }
}
