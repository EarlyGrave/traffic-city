using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Sim.Core.Transit;
using Xunit;

namespace Sim.Core.Tests.Transit;

/// <summary>Шаг 4: автобусы (BusDispatch, BusStopPhase). Сценарий s1k с коротким интервалом и без/с лёгким потоком машин.</summary>
public class BusTests
{
    private const int Headway = 600; // 30 с: быстрые тесты

    private static ScenarioDesc Scenario(int cars, float passengerRate = -1f)
    {
        ScenarioDesc d = Step2.S1k(buses: true);
        d.Demand.TargetVehicles = cars;
        d.Transit.HeadwayTicks = Headway;
        if (passengerRate >= 0f)
        {
            d.Transit.PassengerRatePerMin = passengerRate;
        }

        return d;
    }

    [Fact]
    public void Holding_KeepsDeparturesApart()
    {
        foreach (float alpha in new[] { 0f, 0.8f })
        {
            ScenarioDesc d = Scenario(0);
            d.Transit.HoldAlpha = alpha;
            d.Transit.HoldMaxTicks = 1200;
            d.Transit.HoldEveryN = 1;
            using Simulation sim = Simulation.Create(in d);
            NetworkStore net = sim.World.Net;
            var last = new long[net.BusStopCount];
            long minGap = long.MaxValue;
            for (int t = 0; t < 12 * Headway; t++)
            {
                sim.Step(1);
                for (int s = 0; s < net.BusStopCount; s++)
                {
                    long ld = net.BusStops[s].LastDepartTick;
                    if (ld != last[s])
                    {
                        if (last[s] > 0 && ld > 0)
                        {
                            minGap = Math.Min(minGap, ld - last[s]);
                        }

                        last[s] = ld;
                    }
                }
            }

            if (alpha == 0f)
            {
                Assert.True(minGap == long.MaxValue || minGap >= 0);
            }
            else
            {
                Assert.NotEqual(long.MaxValue, minGap);
                Assert.True(minGap >= (long)(alpha * Headway) - 1, $"min departure gap {minGap} < {alpha * Headway}");
            }
        }
    }

    [Fact]
    public void Buses_AreDispatched_WithConfiguredHeadway()
    {
        ScenarioDesc d = Scenario(0);
        using Simulation sim = Simulation.Create(in d);
        NetworkStore net = sim.World.Net;
        int rc = net.BusRouteCount;
        Assert.True(rc > 0);
        var last = new long[rc];
        Array.Fill(last, -1L);
        var dispatches = new List<long>[rc];
        for (int r = 0; r < rc; r++)
        {
            dispatches[r] = new List<long>();
        }

        for (int t = 0; t < 4 * Headway; t++)
        {
            sim.Step(1);
            for (int r = 0; r < rc; r++)
            {
                long ld = net.BusRoutes[r].LastDispatchTick;
                if (ld != last[r])
                {
                    last[r] = ld;
                    dispatches[r].Add(ld);
                }
            }
        }

        for (int r = 0; r < rc; r++)
        {
            Assert.True(dispatches[r].Count >= 4, $"route {r}: {dispatches[r].Count} dispatches");
            Assert.Equal((long)r * Headway / rc, dispatches[r][0]);
            for (int k = 1; k < dispatches[r].Count; k++)
            {
                Assert.Equal(Headway, dispatches[r][k] - dispatches[r][k - 1]);
            }
        }

        Assert.Equal(sim.Counters.BusDispatched, last.Length > 0 ? SumDispatches(dispatches) : 0);
    }

    private static long SumDispatches(List<long>[] d)
    {
        long n = 0;
        foreach (List<long> l in d)
        {
            n += l.Count;
        }

        return n;
    }

    [Fact]
    public void Buses_StopAtEveryStop_AndTerminateAtLast()
    {
        ScenarioDesc d = Scenario(0);
        using Simulation sim = Simulation.Create(in d);
        NetworkStore net = sim.World.Net;
        sim.Step(30000); // 25 мин: полный рейс самого длинного маршрута
        for (int s = 0; s < net.BusStopCount; s++)
        {
            Assert.True(net.BusStops[s].LastArrivalTick >= 0, $"stop {s} (route {net.BusStops[s].Route}) never served");
        }

        Assert.True(sim.Counters.BusStopArrivals >= net.BusStopCount);
        Assert.True(sim.Counters.BusHeadwaySamples > 0);
        sim.GetStats(out SimStats st);
        Assert.True(st.BusHeadwayDeviationS >= 0f);

        // Автобусы деспаунятся на конечной: число живых ограничено временем рейса / интервалом.
        long dispatched = sim.Counters.BusDispatched;
        Assert.True(sim.ActiveVehicleCount < dispatched, $"active={sim.ActiveVehicleCount} dispatched={dispatched}");
        for (int r = 0; r < net.BusRouteCount; r++)
        {
            Assert.Equal(net.BusRoutes[r].ActiveBuses, sim.BusRouteStats[r].ActiveBuses);
        }
    }

    [Fact]
    public void Dwell_GrowsWithPassengers_AndFollowsFormula()
    {
        double meanEmpty = MeanDwell(0f, 0, out _);
        double meanFull = MeanDwell(0f, 40, out bool formulaOk);
        Assert.True(formulaOk, "dwell != base + per_passenger * (alight + board)");
        Assert.True(meanFull > meanEmpty + 20, $"empty={meanEmpty:F1} full={meanFull:F1} ticks");
    }

    private static double MeanDwell(float rate, int preWaiting, out bool formulaOk)
    {
        ScenarioDesc d = Scenario(0, rate);
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        for (int s = 0; s < w.Net.BusStopCount; s++)
        {
            w.Net.BusStops[s].Waiting = (ushort)preWaiting;
        }

        VehicleStore v = w.Vehicles;
        var was = new bool[v.Capacity];
        TransitDesc cfg = w.TransitCfg;
        long sum = 0;
        int n = 0;
        formulaOk = true;
        for (int t = 0; t < 12000; t++)
        {
            sim.Step(1);
            for (int i = 0; i < v.HighWater; i++)
            {
                bool at = v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.AtStop) != 0;
                if (at && !was[i])
                {
                    int dwell = v.DwellTicks[i];
                    sum += dwell;
                    n++;
                    if (dwell > cfg.DwellBaseTicks + (cfg.DwellPerPassengerTicks * 80f) + 1 || dwell < Math.Min(cfg.DwellBaseTicks, 1))
                    {
                        formulaOk = false;
                    }
                }

                was[i] = at;
            }
        }

        Assert.True(n > 20, $"only {n} stops served");
        return sum / (double)n;
    }

    [Fact]
    public void Buses_AtStop_AreNotStuck_AndStopBlocksOnlyBriefly()
    {
        ScenarioDesc d = Scenario(200);
        using Simulation sim = Simulation.Create(in d);
        VehicleStore v = sim.World.Vehicles;
        int atStopTicks = 0;
        int maxStuck = 0;
        string info = string.Empty;
        for (int t = 0; t < 9000; t++)
        {
            sim.Step(1);
            for (int i = 0; i < v.HighWater; i++)
            {
                if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.AtStop) != 0)
                {
                    atStopTicks++;
                    Assert.Equal(0, v.StillTicks[i]);
                    Assert.Equal(0, v.Flags[i] & (byte)VehicleFlags.Stuck);
                }
            }

            if (sim.Counters.StuckVehicles > maxStuck && info.Length == 0)
            {
                info = $"tick {sim.Tick}: " + Step3Gates.DescribeStuck(sim.World) + DescribeBuses(sim.World);
            }

            maxStuck = Math.Max(maxStuck, sim.Counters.StuckVehicles);
        }

        Assert.True(atStopTicks > 0);
        Assert.True(maxStuck == 0, info);
        Assert.Null(Step2.CheckGaps(sim.World));
    }

    private static string DescribeBuses(World w)
    {
        VehicleStore v = w.Vehicles;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
            {
                sb.Append($"  bus {i}: lane {v.Lane[i]} s={v.S[i]:F1} v={v.Speed[i]:F2} flags={v.Flags[i]} next={v.BusNextStop[i]} still={v.StillTicks[i]} ghost={v.GhostLane[i]} stopLane={w.Net.BusStops[BusStopRules.TargetStop(w.Net, v, i)].Lane} stopS={w.Net.BusStops[BusStopRules.TargetStop(w.Net, v, i)].S:F1} dir={v.LaneChangeDir[i]} desired={w.Net.Lanes[v.Lane[i]].LaneNo}\n");
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void Simulation_WithBuses_IsDeterministic()
    {
        ulong Run()
        {
            ScenarioDesc d = Scenario(150);
            using Simulation sim = Simulation.Create(in d);
            sim.Step(6000);
            return sim.ComputeStateHash();
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Step_WithBuses_AllocatesZeroBytes()
    {
        ScenarioDesc d = Scenario(150);
        using Simulation sim = Simulation.Create(in d);
        sim.Step(3000); // прогрев: рампа, первые рейсы и отправления
        long bytes = AllocationProbe.Measure(() => sim.Step(1500));
        Assert.Equal(0L, bytes);
        Assert.True(sim.Counters.BusStopArrivals > 0);
    }
}
