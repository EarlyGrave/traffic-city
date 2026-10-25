using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Worlds;

/// <summary>Шаг 3, задача J3/J4: правила перекрёстков на s1k (intersections.md §11, T9) - проверки и диагностика.</summary>
internal static class Step3Gates
{
    /// <summary>
    /// I6 на connector'ах (intersections.md §11): для каждой Cross-пары (c, c') тела двух машин не покрывают общую точку
    /// одновременно (<c>S - L &lt;= SMine &lt;= S</c> на обоих). null = чисто.
    /// </summary>
    public static string? CheckConnectorConflicts(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        ConnectorStore cs = w.Connectors;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            ref readonly LaneData ld = ref net.Lanes[v.Lane[i]];
            if (ld.Kind != LaneKind.Connector || ld.ConflictGroup < 0)
            {
                continue;
            }

            int g = ld.ConflictGroup;
            int first = net.Nodes[cs.Node[g]].FirstConnector;
            int end = cs.FirstConflict[g] + cs.ConflictCount[g];
            for (int e = cs.FirstConflict[g]; e < end; e++)
            {
                ConflictEntry ce = cs.Conflicts[e];
                if (ce.Kind != ConflictKind.Cross)
                {
                    continue;
                }

                float sMine = ce.SMineCm * 0.01f;
                if (!(v.S[i] - v.Length[i] <= sMine && sMine <= v.S[i]))
                {
                    continue;
                }

                float sOther = ce.SOtherCm * 0.01f;
                int other = first + ce.OtherLocal;
                for (int x = w.Occupancy.LaneTail[other]; x != -1; x = v.Ahead[x])
                {
                    if (v.S[x] - v.Length[x] <= sOther && sOther <= v.S[x])
                    {
                        return $"tick {w.Tick}: node {cs.Node[g]}: vehicle {i} on connector {v.Lane[i]} (s={v.S[i]:F2}, ticket {v.Ticket[i]}) and {x} on {other} (s={v.S[x]:F2}, ticket {v.Ticket[x]}) cover a common conflict point ({sMine:F2}/{sOther:F2})";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Новые stuck-эпизоды (переход флага 0 -> 1) группируются по узлу / типу управления / причине отказа ворот / размеру очереди.</summary>
    public static void CollectStuckEpisodes(World w, bool[] was, Dictionary<string, int> acc)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        for (int i = 0; i < v.HighWater && i < was.Length; i++)
        {
            bool st = v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.Stuck) != 0;
            if (st && !was[i])
            {
                int lane = v.Lane[i];
                int node = lane < 0 ? -1 : GateRules.GateNode(net, lane);
                string ctl = node < 0 ? "-" : net.Nodes[node].Control.ToString();
                int q = 0;
                for (int j = 0; j < v.HighWater; j++)
                {
                    if (v.Pool.IsAlive(j) && v.Lane[j] == lane)
                    {
                        q++;
                    }
                }

                string key = $"node {node} {ctl} deny={(GateDeny)w.Gate.DenyReason[i]} gate={v.GateState[i]} queue={q} lane={lane}";
                acc[key] = acc.GetValueOrDefault(key) + 1;
            }

            was[i] = st;
        }
    }

    /// <summary>Цепочка "лидер полосы -> ворота -> отказ Exit -> выездная полоса" от первой застрявшей машины до корня + доля маршрутов через узлы цепочки.</summary>
    public static string DescribeChain(World w)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        var sb = new StringBuilder();
        int first = -1;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.Stuck) != 0)
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return "no stuck\n";
        }

        int lane = v.Lane[first];
        var chainNodes = new HashSet<int>();
        var chainLanes = new HashSet<int>();
        sb.Append($"chain start: stuck veh {first} on lane {lane}\n");
        for (int step = 0; step < 40 && lane >= 0 && chainLanes.Add(lane); step++)
        {
            int cnt = 0;
            int lead = -1;
            for (int j = 0; j < v.HighWater; j++)
            {
                if (v.Pool.IsAlive(j) && v.Lane[j] == lane)
                {
                    cnt++;
                    if (lead < 0 || v.S[j] > v.S[lead])
                    {
                        lead = j;
                    }
                }
            }

            int node = GateRules.GateNode(net, lane);
            string ctl = node < 0 ? "-" : net.Nodes[node].Control.ToString();
            if (node >= 0)
            {
                chainNodes.Add(node);
            }

            string head = lead < 0
                ? "no vehicles"
                : $"lead veh {lead} s={v.S[lead]:F1} v={v.Speed[lead]:F2} gate={v.GateState[lead]} deny={(GateDeny)w.Gate.DenyReason[lead]} wait={v.WaitTicks[lead]}t({v.WaitTicks[lead] * 0.05:F1}s) still={v.StillTicks[lead]} nextConn={v.NextConn[lead]}";
            sb.Append($"  [{step}] lane {lane} {net.Lanes[lane].Kind} len={net.Lanes[lane].Length:F1} veh={cnt} node={node} ctl={ctl} | {head}\n");
            if (lead < 0 || w.Gate.DenyReason[lead] != (byte)GateDeny.Exit || v.NextConn[lead] < 0)
            {
                sb.Append($"  ROOT at lane {lane}: " + (lead < 0 ? "empty lane" : $"deny={(GateDeny)w.Gate.DenyReason[lead]} (not Exit or no nextConn)") + "\n");
                if (lead >= 0 && node >= 0 && net.Nodes[node].Signal >= 0)
                {
                    int sl = net.Nodes[node].Signal;
                    int nc = v.NextConn[lead];
                    string cyc = string.Join("/", Enumerable.Range(0, w.Signals.PhaseCount[sl]).Select(p => w.Signals.GreenTicks[(sl * SignalStore.MaxPhases) + p]));
                    sb.Append($"  signal slot {sl}: mode={w.Signals.Mode[sl]} phase={w.SignalStates[sl].Phase} interval={w.SignalStates[sl].Interval} toChange={w.SignalStates[sl].TicksToChange} greenTicks[{cyc}] yellow={w.Signals.YellowTicks[sl]} allRed={w.Signals.AllRedTicks[sl]} lead nextConn={nc} laneSignal={(nc >= 0 ? (LaneSignal)w.LaneSignals[nc] : default)}\n");
                }

                break;
            }

            lane = net.Lanes[v.NextConn[lead]].ToLane;
        }

        int active = 0;
        int viaNodeRemain = 0;
        int viaLaneRemain = 0;
        int viaNodeFull = 0;
        var perNode = new Dictionary<int, int>();
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Route[i] < 0)
            {
                continue;
            }

            int r = v.Route[i];
            active++;
            int f = r * w.Routes.WindowSteps;
            int n = w.Routes.WinCount[r];
            bool anyRemain = false;
            bool anyLaneRemain = false;
            bool anyFull = false;
            var seen = new HashSet<int>();
            for (int k = 0; k < n; k++)
            {
                RouteStep rs = w.Routes.Window[f + k];
                bool inN = rs.Node >= 0 && chainNodes.Contains(rs.Node);
                anyFull |= inN;
                if (k >= v.RouteCursor[i])
                {
                    anyRemain |= inN;
                    anyLaneRemain |= chainLanes.Contains(rs.Lane);
                    if (inN && seen.Add(rs.Node))
                    {
                        perNode[rs.Node] = perNode.GetValueOrDefault(rs.Node) + 1;
                    }
                }
            }

            viaNodeRemain += anyRemain ? 1 : 0;
            viaLaneRemain += anyLaneRemain ? 1 : 0;
            viaNodeFull += anyFull ? 1 : 0;
        }

        sb.Append($"routes: active={active} remaining-path through chain nodes={viaNodeRemain} ({100.0 * viaNodeRemain / Math.Max(1, active):F1}%), through chain lanes={viaLaneRemain} ({100.0 * viaLaneRemain / Math.Max(1, active):F1}%), full route via chain nodes={viaNodeFull} ({100.0 * viaNodeFull / Math.Max(1, active):F1}%)\n");
        sb.Append("per-node remaining share: " + string.Join(", ", perNode.OrderByDescending(k => k.Value).Select(k => $"node {k.Key}: {k.Value} ({100.0 * k.Value / Math.Max(1, active):F1}%)")) + "\n");
        return sb.ToString();
    }

    /// <summary>Описание застрявших машин (stuck) с состоянием ворот - для разбора причин.</summary>
    public static string DescribeStuck(World w, int max = 12)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        var sb = new StringBuilder();
        int n = 0;
        for (int i = 0; i < v.HighWater && n < max; i++)
        {
            if (!v.Pool.IsAlive(i) || (v.Flags[i] & (byte)VehicleFlags.Stuck) == 0)
            {
                continue;
            }

            n++;
            int lane = v.Lane[i];
            string laneInfo = lane < 0 ? "no lane" : $"lane {lane} ({net.Lanes[lane].Kind}, len {net.Lanes[lane].Length:F1}, node {GateRules.GateNode(net, lane)}/{net.Lanes[lane].Node})";
            sb.Append($"  veh {i}: {laneInfo} s={v.S[i]:F2} v={v.Speed[i]:F2} ahead={v.Ahead[i]} gate={v.GateState[i]} gc={v.GrantConnector[i]} wait={v.WaitTicks[i]} deny={(GateDeny)w.Gate.DenyReason[i]} nextConn={v.NextConn[i]} still={v.StillTicks[i]}\n");
            if (w.Gate.DenyReason[i] == (byte)GateDeny.Exit && v.NextConn[i] >= 0)
            {
                int ol = net.Lanes[v.NextConn[i]].ToLane;
                int tl = w.Occupancy.LaneTail[ol];
                sb.Append($"     out lane {ol} len {net.Lanes[ol].Length:F1} reserved={net.OutReserved[ol]:F1} tail={tl}" + (tl < 0 ? "" : $" s={v.S[tl]:F1} v={v.Speed[tl]:F2} len={v.Length[tl]:F1} still={v.StillTicks[tl]} gate={v.GateState[tl]} deny={(GateDeny)w.Gate.DenyReason[tl]}") + "\n");
                int cnt = 0;
                int mov = 0;
                int lead = -1;
                for (int j = 0; j < v.HighWater; j++)
                {
                    if (v.Pool.IsAlive(j) && v.Lane[j] == ol)
                    {
                        cnt++;
                        mov += v.Speed[j] > 0.5f ? 1 : 0;
                        if (lead < 0 || v.S[j] > v.S[lead])
                        {
                            lead = j;
                        }
                    }
                }

                sb.Append($"     out lane vehicles={cnt} moving={mov} lead={lead}" + (lead < 0 ? "" : $" s={v.S[lead]:F1} v={v.Speed[lead]:F2} gate={v.GateState[lead]} deny={(GateDeny)w.Gate.DenyReason[lead]} wait={v.WaitTicks[lead]} still={v.StillTicks[lead]}") + "\n");
                for (int j = 0; j < v.HighWater; j++)
                {
                    if (v.Pool.IsAlive(j) && v.Lane[j] == ol)
                    {
                        sb.Append($"       ov {j} s={v.S[j]:F1} v={v.Speed[j]:F2} still={v.StillTicks[j]} deny={(GateDeny)w.Gate.DenyReason[j]}\n");
                    }
                }

                for (int c = 0; c < net.LaneCount; c++)
                {
                    if (net.Lanes[c].Kind == LaneKind.Connector && net.Lanes[c].ToLane == ol)
                    {
                        int h = w.Occupancy.LaneHead[net.Lanes[c].FromLane];
                        sb.Append($"       feeder conn {c} from {net.Lanes[c].FromLane} head={h}" + (h < 0 ? "" : $" s={v.S[h]:F1} v={v.Speed[h]:F2} wait={v.WaitTicks[h]} deny={(GateDeny)w.Gate.DenyReason[h]} gate={v.GateState[h]}") + "\n");
                    }
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>Один прогон s1k на 10 минут игрового времени с проверками каждый тик, затем 1000 тиков под замером аллокаций.</summary>
public sealed class Step3GateRun
{
    public const int Ticks = 12000;

    public string? GapViolation { get; private set; }
    public string? ConflictViolation { get; private set; }
    public int MaxStuck { get; private set; }
    public string StuckInfo { get; private set; } = string.Empty;
    public long Allocated1000Ticks { get; private set; }
    public SimCounters CountersEnd { get; private set; }
    public SimStats StatsEnd { get; private set; }
    public int ActiveEnd { get; private set; }

    public Step3GateRun()
    {
        ScenarioDesc d = Step2.S1k();
        using Simulation sim = Simulation.Create(in d);
        for (int t = 1; t <= Ticks; t++)
        {
            sim.Step(1);
            GapViolation ??= Step2.CheckGaps(sim.World);
            ConflictViolation ??= Step3Gates.CheckConnectorConflicts(sim.World);
            if (sim.Counters.StuckVehicles > MaxStuck)
            {
                MaxStuck = sim.Counters.StuckVehicles;
                StuckInfo = $"tick {t}:\n" + Step3Gates.DescribeStuck(sim.World);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 1000; t++)
        {
            sim.Step(1);
        }

        Allocated1000Ticks = GC.GetAllocatedBytesForCurrentThread() - before;
        CountersEnd = sim.Counters;
        sim.GetStats(out SimStats st);
        StatsEnd = st;
        ActiveEnd = sim.ActiveVehicleCount;
    }
}

public sealed class Step3GateWorldTests : IClassFixture<Step3GateRun>
{
    private readonly Step3GateRun _run;
    private readonly ITestOutputHelper _out;

    public Step3GateWorldTests(Step3GateRun run, ITestOutputHelper output)
    {
        _run = run;
        _out = output;
    }

    [Fact]
    public void S1k_NoStuck_NoGridlock_10Min()
    {
        SimCounters c = _run.CountersEnd;
        _out.WriteLine($"active={_run.ActiveEnd} completed={c.TripsCompleted} avg_speed_kmh={_run.StatsEnd.AvgSpeedKmh:F2} grants={c.Grants} deniedExit={c.GateDeniedExit} inversions={c.PriorityInversions} boxWait={c.BoxWaitTicks} hardClamps={c.HardClamps}");
        _out.WriteLine($"max stuck={_run.MaxStuck} {_run.StuckInfo}");
        Assert.Equal(0, _run.MaxStuck);
        Assert.Equal(0, c.StuckEventsTotal);
        Assert.Equal(0, c.GridlockFlag);
        Assert.True(c.TripsCompleted > 0);
        Assert.True(c.Grants > 0);
    }

    [Fact]
    public void S1k_NoNegativeGaps_AndNoConflictOverlaps_EveryTick()
    {
        Assert.Null(_run.GapViolation);
        Assert.Null(_run.ConflictViolation);
    }

    [Fact]
    public void S1k_StationaryTick_AllocatesZeroBytes()
    {
        Assert.Equal(0, _run.Allocated1000Ticks);
    }

    [Fact]
    public void S1k_SignalsAreLive()
    {
        ScenarioDesc d = Step2.S1k();
        using Simulation sim = Simulation.Create(in d);
        Assert.Equal(25, sim.SignalStates.Length);
        sim.Step(1);
        SignalState s0 = sim.SignalStates[0];
        sim.Step(1);
        Assert.Equal(s0.TicksToChange - 1, sim.SignalStates[0].TicksToChange);
    }
}

/// <summary>Реальные числа s1k за 30 мин игрового времени с правилами перекрёстков (Release). Только при TC_STEP3_REPORT=1.</summary>
public class Step3GateReportTests
{
    private readonly ITestOutputHelper _out;

    public Step3GateReportTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(36000, false)]
    [InlineData(36000, true)]
    public void Report_S1k(int ticks, bool adaptive)
    {
        if (Environment.GetEnvironmentVariable("TC_STEP3_REPORT") != "1")
        {
            return;
        }

        ScenarioDesc d = Step2.S1k(buses: true);
        d.City.SignalMode = adaptive ? CitySignalMode.Adaptive : CitySignalMode.Fixed;   // 2.5 S2: дефолт сценария - adaptive; Fixed + legacy - эталон «до»
        using Simulation sim = Simulation.Create(in d);
        sim.Step(200);
        long sum = 0;
        long max = 0;
        long over1ms = 0;
        int maxStuck = 0;
        long stuckEventTick = -1;
        string stuckInfo = string.Empty;
        string chainInfo = string.Empty;
        string? gap = null;
        string? conflict = null;
        var episodes = new Dictionary<string, int>();
        var wasStuck = new bool[sim.World.Vehicles.Pool.Capacity];
        var samples = new long[ticks];
        var overByTenth = new int[10];
        int gc0 = GC.CollectionCount(0);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
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
                overByTenth[t * 10 / ticks]++;
            }

            gap ??= Step2.CheckGaps(sim.World);
            conflict ??= Step3Gates.CheckConnectorConflicts(sim.World);
            if (sim.Counters.StuckVehicles > maxStuck)
            {
                maxStuck = sim.Counters.StuckVehicles;
                stuckEventTick = sim.Tick;
                stuckInfo = Step3Gates.DescribeStuck(sim.World);
            }

            if (chainInfo.Length == 0 && sim.Counters.StuckVehicles > 0)
            {
                chainInfo = $"tick {sim.Tick}:\n" + Step3Gates.DescribeChain(sim.World);
            }

            if (sim.Counters.StuckVehicles > 0 || t % 50 == 0)
            {
                Step3Gates.CollectStuckEpisodes(sim.World, wasStuck, episodes);
            }
        }

        Array.Sort(samples);
        double us = 1e6 / Stopwatch.Frequency;
        SimCounters c = sim.Counters;
        sim.GetStats(out SimStats st);
        _out.WriteLine($"=== s1k + intersections ({(adaptive ? "adaptive" : "fixed")}), {(ticks + 200) / 1200.0:F1} min game time ===");
        _out.WriteLine($"active={c.ActiveVehicles} spawned={c.VehiclesSpawned} completed={c.TripsCompleted} pendingTrips={sim.World.Trips.Count}");
        _out.WriteLine($"avg_speed_kmh={st.AvgSpeedKmh:F2} avg_trip_time_s={st.AvgTripTimeS:F1} trips_per_min={st.TripsCompletedPerMin:F2}");
        _out.WriteLine($"reroutes={sim.Counters.Reroutes} timed_reroutes={sim.Counters.TimedReroutes} cost_recalcs={sim.Counters.CostRecalcs} queries={sim.Counters.PathfindQueries} deferred={sim.Counters.RouteDeferred}");
        _out.WriteLine($"routing: work={c.PathfindWork} expanded={c.PathfindExpandedNodes} failed={c.RouteFailed} refills={c.WindowRefills} refill_late={c.RefillLate} unpacks={c.PlanUnpacks} unpack_work={c.UnpackWork} plan_compactions={c.PlanCompactions} timed_checks={c.TimedChecks} timed_triggered={c.TimedTriggered} customize_rows={c.CustomizeRows} customize_work={c.CustomizeWork} lag_ticks={c.CustomizeLagTicks}");
        _out.WriteLine($"stuck_now={c.StuckVehicles} stuck_max={maxStuck} stuck_events_total={c.StuckEventsTotal} stuck_ticks_total={c.StuckTicksTotal} gridlock_flag={c.GridlockFlag} gridlock_ticks_total={c.GridlockTicksTotal}");
        _out.WriteLine($"grants={c.Grants} denied_exit={c.GateDeniedExit} inversions={c.PriorityInversions} box_wait_ticks={c.BoxWaitTicks} gate_wait_avg_s={(c.Grants > 0 ? c.GateWaitTicksTotal * 0.05 / c.Grants : 0):F2}");
        SignalMetrics sm = sim.World.SignalWait;
        string[] mn = { "straight", "right", "left", "uturn" };
        for (int m = 0; m < SignalMetrics.Maneuvers; m++)
        {
            long n = sm.Grants[m];
            long need = ((n * 95) + 99) / 100;
            long acc = 0;
            int p95 = 0;
            for (int b = 0; b < SignalMetrics.HistBuckets && n > 0; b++)
            {
                acc += sm.Hist[(m * SignalMetrics.HistBuckets) + b];
                if (acc >= need)
                {
                    p95 = b;
                    break;
                }
            }

            _out.WriteLine($"signal_wait {mn[m]}: grants={n} mean_s={(n > 0 ? sm.WaitTicks[m] * 0.05 / n : 0):F2} p95_s={p95}");
        }

        _out.WriteLine($"signal: left_wait_max_s={c.SignalLeftWaitMax * 0.05:F1} arterial_grants={c.ArterialSignalGrants} arterial_stop_share={(c.ArterialSignalGrants > 0 ? c.ArterialSignalStops / (double)c.ArterialSignalGrants : 0):F4} starve_denials={c.StarveDenials} arrow_phases={c.ArrowPhases} spill_outs={c.SpillOuts} exit_claims={c.ExitClaims}");
        _out.WriteLine($"bus: dispatched={c.BusDispatched} stop_arrivals={c.BusStopArrivals} boarded={c.BusPassengersBoarded} headway_samples={c.BusHeadwaySamples} bus_headway_deviation_s={st.BusHeadwayDeviationS:F2}");
        _out.WriteLine($"hard_clamps={c.HardClamps} lane_changes={c.LaneChanges} aborted={c.LaneChangesAborted} connector_entries={c.ConnectorEntries} spawn_deferred_ticks={c.SpawnDeferredTicks} events_dropped={c.EventsDropped}");
        _out.WriteLine($"tick: mean={sum / (double)ticks * us:F1} us p50={samples[ticks / 2] * us:F1} p95={samples[(int)(ticks * 0.95)] * us:F1} p99={samples[(int)(ticks * 0.99)] * us:F1} max={max * us:F1} us; >1ms: {over1ms}");
        _out.WriteLine($"gap violation: {gap ?? "none"}; conflict overlap: {conflict ?? "none"}");
        _out.WriteLine($">1ms ticks by tenth of run: {string.Join(" ", overByTenth)}; gen0 GCs during run: {GC.CollectionCount(0) - gc0}; allocated by Step (all ticks, incl. ramp): {GC.GetAllocatedBytesForCurrentThread() - allocBefore} B");
        foreach (KeyValuePair<string, int> kv in episodes.OrderByDescending(k => k.Value).Take(25))
        {
            _out.WriteLine($"stuck-episodes {kv.Value,4}  {kv.Key}");
        }

        if (chainInfo.Length > 0)
        {
            _out.WriteLine($"first stuck chain at {chainInfo}");
        }

        if (maxStuck > 0)
        {
            _out.WriteLine($"first stuck max at tick {stuckEventTick}:\n{stuckInfo}");
        }
    }
}
