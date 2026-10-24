using System;
using System.Diagnostics;
using System.Text;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Worlds;

/// <summary>Шаг 3, задача J3/J4: правила перекрёстков на s1k (intersections.md §11, T9) - проверки и диагностика.</summary>
internal static class Step3
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
        }

        return sb.ToString();
    }
}

/// <summary>Один прогон s1k на 10 минут игрового времени с проверками каждый тик, затем 1000 тиков под замером аллокаций.</summary>
public sealed class Step3Run
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

    public Step3Run()
    {
        ScenarioDesc d = Step2.S1k();
        using Simulation sim = Simulation.Create(in d);
        for (int t = 1; t <= Ticks; t++)
        {
            sim.Step(1);
            GapViolation ??= Step2.CheckGaps(sim.World);
            ConflictViolation ??= Step3.CheckConnectorConflicts(sim.World);
            if (sim.Counters.StuckVehicles > MaxStuck)
            {
                MaxStuck = sim.Counters.StuckVehicles;
                StuckInfo = $"tick {t}:\n" + Step3.DescribeStuck(sim.World);
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

public sealed class Step3WorldTests : IClassFixture<Step3Run>
{
    private readonly Step3Run _run;
    private readonly ITestOutputHelper _out;

    public Step3WorldTests(Step3Run run, ITestOutputHelper output)
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
public class Step3ReportTests
{
    private readonly ITestOutputHelper _out;

    public Step3ReportTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(36000)]
    public void Report_S1k(int ticks)
    {
        if (Environment.GetEnvironmentVariable("TC_STEP3_REPORT") != "1")
        {
            return;
        }

        ScenarioDesc d = Step2.S1k();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(200);
        long sum = 0;
        long max = 0;
        long over1ms = 0;
        int maxStuck = 0;
        long stuckEventTick = -1;
        string stuckInfo = string.Empty;
        string? gap = null;
        string? conflict = null;
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
            conflict ??= Step3.CheckConnectorConflicts(sim.World);
            if (sim.Counters.StuckVehicles > maxStuck)
            {
                maxStuck = sim.Counters.StuckVehicles;
                stuckEventTick = sim.Tick;
                stuckInfo = Step3.DescribeStuck(sim.World);
            }
        }

        Array.Sort(samples);
        double us = 1e6 / Stopwatch.Frequency;
        SimCounters c = sim.Counters;
        sim.GetStats(out SimStats st);
        _out.WriteLine($"=== s1k + intersections, {(ticks + 200) / 1200.0:F1} min game time ===");
        _out.WriteLine($"active={c.ActiveVehicles} spawned={c.VehiclesSpawned} completed={c.TripsCompleted} pendingTrips={sim.World.Trips.Count}");
        _out.WriteLine($"avg_speed_kmh={st.AvgSpeedKmh:F2} avg_trip_time_s={st.AvgTripTimeS:F1} trips_per_min={st.TripsCompletedPerMin:F2}");
        _out.WriteLine($"stuck_now={c.StuckVehicles} stuck_max={maxStuck} stuck_events_total={c.StuckEventsTotal} stuck_ticks_total={c.StuckTicksTotal} gridlock_flag={c.GridlockFlag} gridlock_ticks_total={c.GridlockTicksTotal}");
        _out.WriteLine($"grants={c.Grants} denied_exit={c.GateDeniedExit} inversions={c.PriorityInversions} box_wait_ticks={c.BoxWaitTicks} gate_wait_avg_s={(c.Grants > 0 ? c.GateWaitTicksTotal * 0.05 / c.Grants : 0):F2}");
        _out.WriteLine($"hard_clamps={c.HardClamps} lane_changes={c.LaneChanges} aborted={c.LaneChangesAborted} connector_entries={c.ConnectorEntries} spawn_deferred_ticks={c.SpawnDeferredTicks} events_dropped={c.EventsDropped}");
        _out.WriteLine($"tick: mean={sum / (double)ticks * us:F1} us p50={samples[ticks / 2] * us:F1} p95={samples[(int)(ticks * 0.95)] * us:F1} p99={samples[(int)(ticks * 0.99)] * us:F1} max={max * us:F1} us; >1ms: {over1ms}");
        _out.WriteLine($"gap violation: {gap ?? "none"}; conflict overlap: {conflict ?? "none"}");
        _out.WriteLine($">1ms ticks by tenth of run: {string.Join(" ", overByTenth)}; gen0 GCs during run: {GC.CollectionCount(0) - gc0}; allocated by Step (all ticks, incl. ramp): {GC.GetAllocatedBytesForCurrentThread() - allocBefore} B");
        if (maxStuck > 0)
        {
            _out.WriteLine($"first stuck max at tick {stuckEventTick}:\n{stuckInfo}");
        }
    }
}
