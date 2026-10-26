#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Save;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Transit;

namespace Sim.Core.Tests.Peds;

/// <summary>Пассажиры-агенты (pedestrians.md §7, §15 «Пассажиры (P4)»), парные обратные маршруты и выбор автобусных ног жителей (§3).</summary>
public sealed class PassengerTests
{
    private const int Headway = 400;

    /// <summary>Малый город с тремя автобусными маршрутами (+3 обратных) и жителями с короткими циклами «дом - работа».</summary>
    private static ScenarioDesc Desc(int count = 300, int residents = 600, bool bidirectional = true, float holdAlpha = 0.9f)
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Transit.Bidirectional = bidirectional;
        d.Transit.HeadwayTicks = Headway;
        d.Transit.HoldAlpha = holdAlpha;
        d.Transit.HoldMaxTicks = 1200;
        d.Transit.HoldEveryN = 1;
        d.Transit.PassengerModel = PassengerModel.Agents;
        d.Peds = new PedDesc
        {
            Enabled = true,
            Count = count,
            Residents = residents,
            SpawnRampTicks = 200,
            ActivityMinTicks = 100,
            ActivityMaxTicks = 400,
            WalkOnlyMm = 150_000,        // город 1200 м: автобус нужен для всего, что дальше 150 м
            MaxWalkToStopMm = 400_000,
            MidblockAtStops = true,
        };
        d.Capacities.MaxPeds = (count * 13 / 10) + 8;
        d.Capacities.MaxResidents = residents;
        d.Capacities.MaxPedPathSteps = 64;
        d.Capacities.MaxBusRoutes = 16;
        d.Capacities.MaxBusStops = 256;
        return d;
    }

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    // ------------------------------------------------------------ transit.bidirectional

    [Fact]
    public void Bidirectional_AddsMirroredReverseRoutes_AndKeepsForwardOnes()
    {
        ScenarioDesc one = Desc(bidirectional: false);
        ScenarioDesc two = Desc(bidirectional: true);
        using Simulation a = Make(in one);
        using Simulation b = Make(in two);
        NetworkStore na = a.World.Net;
        NetworkStore nb = b.World.Net;
        int fwd = na.BusRouteCount;
        Assert.True(fwd > 0);
        Assert.Equal(2 * fwd, nb.BusRouteCount);
        Assert.Equal(2 * na.BusStopCount, nb.BusStopCount);
        for (int r = 0; r < fwd; r++)
        {
            Assert.Equal(na.BusRoutes[r].FirstStop, nb.BusRoutes[r].FirstStop);
            Assert.Equal(na.BusRoutes[r].StopCount, nb.BusRoutes[r].StopCount);
            for (int k = 0; k < na.BusRoutes[r].StopCount; k++)
            {
                int s = na.BusRoutes[r].FirstStop + k;
                Assert.Equal(na.BusStops[s].Lane, nb.BusStops[s].Lane);
                Assert.Equal(na.BusStops[s].S, nb.BusStops[s].S);
            }

            // обратный маршрут: те же остановки в обратном порядке, встречная полоса того же сегмента, S' = Len - S, остановка принадлежит маршруту
            ref readonly BusRouteData rev = ref nb.BusRoutes[fwd + r];
            Assert.Equal(na.BusRoutes[r].StopCount, rev.StopCount);
            Assert.Equal(na.BusRoutes[r].HeadwayTicks, rev.HeadwayTicks);
            for (int k = 0; k < rev.StopCount; k++)
            {
                BusStopData f = nb.BusStops[na.BusRoutes[r].FirstStop + rev.StopCount - 1 - k];
                BusStopData s = nb.BusStops[rev.FirstStop + k];
                LaneData lf = nb.Lanes[f.Lane];
                LaneData ls = nb.Lanes[s.Lane];
                Assert.Equal(lf.Segment, ls.Segment);
                Assert.NotEqual(lf.Direction, ls.Direction);
                Assert.Equal(fwd + r, s.Route);
                Assert.Equal(f.Kind, s.Kind);
                Assert.InRange(f.S + s.S - nb.Segments[lf.Segment].Length, -0.01f, 0.01f);
            }
        }
    }

    // ------------------------------------------------------------ жители: способ и пара остановок

    [Fact]
    public void Residents_ChooseBusLegs_WithValidStopPairs_AndRoutesWithoutBusStayWalking()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        World w = sim.World;
        ResidentStore rs = w.Peds!.R;
        int bus = 0;
        for (int r = 0; r < rs.Count; r++)
        {
            Check(w, rs.ModeOut[r], rs.RouteOut[r], rs.StopAOut[r], rs.StopBOut[r], ref bus);
            Check(w, rs.ModeBack[r], rs.RouteBack[r], rs.StopABack[r], rs.StopBBack[r], ref bus);
        }

        Assert.True(bus > rs.Count / 4, $"автобусных ног {bus} из {2 * rs.Count}");

        ScenarioDesc none = Desc(bidirectional: false);
        none.Transit.Routes = 0;
        using Simulation plain = Make(in none);
        ResidentStore walk = plain.World.Peds!.R;
        for (int r = 0; r < walk.Count; r++)
        {
            Assert.Equal(0, walk.ModeOut[r]);
            Assert.Equal(0, walk.ModeBack[r]);
            Assert.Equal(-1, walk.StopAOut[r]);
        }
    }

    private static void Check(World w, byte mode, int route, int a, int b, ref int bus)
    {
        if (mode == 0)
        {
            Assert.Equal(-1, route);
            Assert.Equal(-1, a);
            Assert.Equal(-1, b);
            return;
        }

        bus++;
        NetworkStore net = w.Net;
        ref readonly BusRouteData br = ref net.BusRoutes[route];
        Assert.InRange(a, br.FirstStop, br.FirstStop + br.StopCount - 1);
        Assert.InRange(b, br.FirstStop, br.FirstStop + br.StopCount - 1);
        Assert.True(a < b, "посадка раньше высадки по маршруту");
        Assert.True(Stops.AccessPoint(w, a, out _, out _));
        Assert.True(Stops.AccessPoint(w, b, out _, out _));
    }

    // ------------------------------------------------------------ поток пассажиров

    [Fact]
    public void Passengers_BoardRideAndAlight_WithConsistentBookkeeping()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        bool sawQueue = false;
        bool sawRider = false;
        for (int t = 0; t < 8000; t += 50)
        {
            sim.Step(50);
            int atStop = 0;
            int onBus = 0;
            for (int p = 0; p < pw.P.HighWater; p++)
            {
                if (!pw.P.Pool.IsAlive(p))
                {
                    continue;
                }

                atStop += pw.P.State[p] == (byte)PedState.AtStop ? 1 : 0;
                onBus += pw.P.State[p] == (byte)PedState.OnBus ? 1 : 0;
            }

            int queued = 0;
            for (int s = 0; s < w.Net.BusStopCount; s++)
            {
                queued += pw.StopQCount[s];
                Assert.Equal(pw.StopQCount[s], w.Net.BusStops[s].Waiting);
                Assert.Equal(pw.StopQCount[s], sim.BusStopWaiting[s]);
            }

            Assert.Equal(atStop, queued);

            int listed = 0;
            VehicleStore v = w.Vehicles;
            for (int i = 0; i < v.HighWater; i++)
            {
                int len = 0;
                int last = -1;
                for (int p = pw.PaxHead[i]; p >= 0; p = pw.P.Next[p])
                {
                    Assert.Equal(i, pw.P.Bus[p]);
                    Assert.True(p > last, "список пассажиров по возрастанию индекса");
                    last = p;
                    len++;
                }

                if (len > 0)
                {
                    Assert.True(v.Pool.IsAlive(i));
                    Assert.True((v.Flags[i] & (byte)VehicleFlags.IsBus) != 0);
                    Assert.InRange(len, 1, BusStopPhase.BusCapacity);
                }

                if ((v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 && v.Pool.IsAlive(i))
                {
                    Assert.Equal(len, v.Passengers[i]);
                }

                listed += len;
            }

            Assert.Equal(onBus, listed);
            sawQueue |= queued > 0;
            sawRider |= onBus > 0;
        }

        SimCounters c = sim.Counters;
        Assert.True(sawQueue && sawRider);
        Assert.True(c.PedStopBoardings > 50, $"посадок {c.PedStopBoardings}");
        Assert.Equal(c.PedStopBoardings, c.BusPassengersBoarded);
        Assert.True(c.BusPassengersAlighted > 30, $"высадок {c.BusPassengersAlighted}");
        Assert.True(c.BusPassengersAlighted <= c.BusPassengersBoarded);
        Assert.True(c.PedTripsCompleted > 50);
        Assert.Equal(0, c.PedNoPath);
        Assert.Equal(0, c.PedStuckEvents);
        Assert.True(c.PedStopWaitTicksTotal > 0);
    }

    // ------------------------------------------------------------ посадка FIFO и вместимость, стоянка по числам

    /// <summary>Ставит на остановку <paramref name="stop"/> <paramref name="n"/> готовых жителей (по одному пешеходу на жителя, в порядке индексов) через <c>Stops.OnArrive</c>.</summary>
    private static int[] Queue(World w, int stop, int n, int stopB, int firstResident)
    {
        PedWorld pw = w.Peds!;
        var peds = new int[n];
        for (int k = 0; k < n; k++)
        {
            int r = firstResident + k;
            Assert.True(pw.P.TryAllocate(out int p));
            pw.P.Resident[p] = r;
            pw.P.GoalEdge[p] = -1;
            pw.R.Where[r] = ResidentStore.ToWork;
            pw.R.Ped[r] = p;
            pw.R.StopAOut[r] = stop;
            pw.R.StopBOut[r] = stopB;
            pw.R.ModeOut[r] = 1;
            pw.P.State[p] = (byte)PedState.Walking;
            Stops.OnArrive(w, p);
            peds[k] = p;
        }

        return peds;
    }

    [Fact]
    public void Boarding_IsFifoAndLimitedByCapacity_DwellUsesActualNumbers()
    {
        ScenarioDesc d = Desc(holdAlpha: 0f);
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        w.TargetPeds = 0;
        BusRouteData r0 = w.Net.BusRoutes[0];
        int stopA = r0.FirstStop;
        int stopB = r0.FirstStop + r0.StopCount - 1;
        int[] peds = Queue(w, stopA, 100, stopB, 0);
        Assert.Equal(100, pw.StopQCount[stopA]);
        Assert.Equal(100, sim.BusStopWaiting[stopA]);

        // первый автобус маршрута 0 прибывает на первую остановку: садятся первые 80 в порядке постановки в очередь
        long before = sim.Counters.BusPassengersBoarded;
        int bus = -1;
        for (int t = 0; t < 3000 && bus < 0; t++)
        {
            sim.Step(1);
            VehicleStore v = w.Vehicles;
            for (int i = 0; i < v.HighWater && bus < 0; i++)
            {
                if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 && v.BusRoute[i] == 0 && (v.Flags[i] & (byte)VehicleFlags.AtStop) != 0 && v.BusNextStop[i] == 0)
                {
                    bus = i;
                }
            }
        }

        Assert.True(bus >= 0, "автобус не прибыл на первую остановку");
        Assert.Equal(before + 80, sim.Counters.BusPassengersBoarded);
        Assert.Equal(80, w.Vehicles.Passengers[bus]);
        Assert.Equal(20, pw.StopQCount[stopA]);
        for (int k = 0; k < 100; k++)
        {
            Assert.Equal(k < 80 ? (byte)PedState.OnBus : (byte)PedState.AtStop, pw.P.State[peds[k]]);
        }

        Assert.Equal(peds[80], pw.StopQHead[stopA]);
        Assert.Equal(peds[99], pw.StopQTail[stopA]);

        // стоянка = DwellBase + DwellPerPassenger * (вышло + село), без удержания (alpha = 0), не больше 45 с
        TransitDesc cfg = w.TransitCfg;
        int expected = cfg.DwellBaseTicks + (int)(cfg.DwellPerPassengerTicks * 80);
        expected = Math.Clamp(expected, 1, BusStopPhase.MaxDwellTicks);
        Assert.Equal(expected, w.Vehicles.DwellTicks[bus]);
    }

    [Fact]
    public void Alight_AtOwnOrNextStop_SkippedStopLosesNobody_AndTerminalEmptiesBus()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        w.TargetPeds = 0;
        int bus = -1;
        for (int t = 0; t < 3000 && bus < 0; t++)
        {
            sim.Step(1);
            VehicleStore v = w.Vehicles;
            for (int i = 0; i < v.HighWater && bus < 0; i++)
            {
                if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
                {
                    bus = i;
                }
            }
        }

        Assert.True(bus >= 0);
        int r = w.Vehicles.BusRoute[bus];
        BusRouteData br = w.Net.BusRoutes[r];
        Assert.True(br.StopCount >= 5);
        int s1 = br.FirstStop + 1;
        int s2 = br.FirstStop + 2;
        int s3 = br.FirstStop + 3;
        int[] peds = Queue(w, br.FirstStop, 3, s1, 10);
        // пассажиры сходят на s1, s2 (пропущена автобусом) и s3; индексы пешеходов растут вместе со списком
        int[] stops = { s2, s1, s3 };
        Assert.Equal(3, Stops.Board(w, bus, br.FirstStop, 80));
        for (int k = 0; k < 3; k++)
        {
            pw.P.AlightStop[peds[k]] = stops[k];
        }

        w.Vehicles.Passengers[bus] = 3;
        Assert.Equal(peds[0], pw.PaxHead[bus]);

        // автобус прибыл на s2: выходят те, кому s1 и s2 (пропущенная s1 не теряет человека)
        Assert.Equal(2, Stops.Alight(w, bus, s2, false));
        Assert.Equal(1, CountPax(pw, bus));
        Assert.Equal(peds[2], pw.PaxHead[bus]);
        Assert.Equal((byte)PedState.PendingPath, pw.P.State[peds[0]]);
        Assert.Equal((byte)PedState.PendingPath, pw.P.State[peds[1]]);
        Assert.Equal((byte)PedLeg.FromStop, pw.P.Leg[peds[0]]);
        Assert.True(Stops.AccessPoint(w, s2, out int edge, out int sMm));
        Assert.Equal(edge, pw.P.Edge[peds[0]]);
        Assert.Equal(sMm, pw.P.S0Mm[peds[0]]);
        Assert.Equal(pw.R.WorkEdge[10], pw.P.GoalEdge[peds[0]]);
        Assert.Equal(-1, pw.P.Bus[peds[0]]);

        // конечная: выходят все
        Assert.Equal(1, Stops.Alight(w, bus, s3 + 1, true));
        Assert.Equal(-1, pw.PaxHead[bus]);
        Assert.Equal(3, sim.Counters.BusPassengersAlighted);
    }

    private static int CountPax(PedWorld pw, int bus)
    {
        int n = 0;
        for (int p = pw.PaxHead[bus]; p >= 0; p = pw.P.Next[p])
        {
            n++;
        }

        return n;
    }

    [Fact]
    public void FullStop_FallsBackToWalking_WhenWalkIsShort()
    {
        ScenarioDesc d = Desc(count: 1, residents: 600);
        d.Peds.MaxWalkMm = 5_000_000;   // любой путь в городе 1200 м - «пешком допустимо»
        d.Capacities.MaxPeds = 200;     // 80 в очереди остановки и запас
        d.Peds.SpawnRampTicks = 1;
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        ResidentStore rs = pw.R;
        int r = -1;
        for (int i = 100; i < rs.Count && r < 0; i++)
        {
            if (rs.ModeOut[i] == 1)
            {
                r = i;
            }
        }

        Assert.True(r >= 100);
        int stop = rs.StopAOut[r];
        BusRouteData br = w.Net.BusRoutes[w.Net.BusStops[stop].Route];
        Queue(w, stop, BusStopPhase.BusCapacity, br.FirstStop + br.StopCount - 1, 0);
        w.TargetPeds = BusStopPhase.BusCapacity + 1;
        // остальные жители не стартуют: далёкое время готовности и пересборка колеса; запускается только r
        for (int i = 0; i < rs.Count; i++)
        {
            if (i != r && rs.Ped[i] < 0)
            {
                rs.ReadyTick[i] = 1_000_000;
            }
        }

        rs.ReadyTick[r] = w.Tick + 1;
        pw.RestoreDerived(w);
        sim.Step(3);
        Assert.Equal(1, sim.Counters.PedModeFallback);
        int p = rs.Ped[r];
        Assert.True(p >= 0);
        Assert.Equal((byte)PedLeg.Direct, pw.P.Leg[p]);
        Assert.Equal(80, pw.StopQCount[stop]);
    }

    // ------------------------------------------------------------ удержание, счётчик, сейв, потоки

    [Fact]
    public void Holding_KeepsDeparturesApart_InAgentsMode()
    {
        foreach (float alpha in new[] { 0f, 0.8f })
        {
            ScenarioDesc d = Desc(holdAlpha: alpha);
            using Simulation sim = Make(in d);
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

            Assert.True(sim.Counters.PedStopBoardings > 0);
            if (alpha > 0f)
            {
                Assert.NotEqual(long.MaxValue, minGap);
                Assert.True(minGap >= (long)(alpha * Headway) - 1, $"min departure gap {minGap} < {alpha * Headway}");
            }
        }
    }

    [Fact]
    public void CounterMode_IgnoresAgents_AndWaitingGrowsByRate()
    {
        ScenarioDesc d = Desc();
        d.Transit.PassengerModel = PassengerModel.Counter;
        using Simulation sim = Make(in d);
        sim.Step(3000);
        Assert.Equal(0, sim.Counters.PedStopBoardings);
        Assert.Equal(0, sim.Counters.BusPassengersAlighted);
        Assert.True(sim.Counters.BusPassengersBoarded > 0);
        foreach (ushort wt in sim.BusStopWaiting)
        {
            Assert.Equal(0, wt);
        }
    }

    [Fact]
    public void SaveLoad_WithQueuesAndRiders_ContinuesWithSameHash()
    {
        ScenarioDesc d = Desc();
        using Simulation a = Make(in d);
        PedWorld pw = a.World.Peds!;
        int tries = 0;
        while (tries++ < 100)
        {
            a.Step(100);
            int atStop = 0;
            int onBus = 0;
            for (int p = 0; p < pw.P.HighWater; p++)
            {
                if (pw.P.Pool.IsAlive(p))
                {
                    atStop += pw.P.State[p] == (byte)PedState.AtStop ? 1 : 0;
                    onBus += pw.P.State[p] == (byte)PedState.OnBus ? 1 : 0;
                }
            }

            if (atStop > 3 && onBus > 3)
            {
                break;
            }
        }

        Assert.True(tries < 100, "не нашли момент с очередями и пассажирами");
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(bytes);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.True(bytes.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(b)), "повторный сейв загруженного мира должен совпасть побайтно");
        for (int i = 0; i < 6; i++)
        {
            a.Step(97);
            b.Step(97);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }
    }

    [Fact]
    public void Hash_DoesNotDependOnThreads_WithPassengers()
    {
        ScenarioDesc d = Desc();
        ulong[]? reference = null;
        foreach (int threads in new[] { 1, 2, 3 })
        {
            using Simulation sim = Make(in d, threads);
            var hashes = new ulong[8];
            for (int i = 0; i < hashes.Length; i++)
            {
                sim.Step(500);
                hashes[i] = sim.ComputeStateHash();
            }

            Assert.True(sim.Counters.BusPassengersAlighted > 0);
            if (reference == null)
            {
                reference = hashes;
            }
            else
            {
                Assert.Equal(reference, hashes);
            }
        }
    }

    [Fact]
    public void Step_AllocatesZeroBytes_WithPassengers()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        sim.Step(3000);
        Assert.True(sim.Counters.PedStopBoardings > 0);
        long bytes = AllocationProbe.Measure(() => sim.Step(200));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void NoStuckResidents_LongRun()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        long thr = pw.Cfg.StuckStopTicks;
        long maxWait = 0;
        for (int t = 0; t < 24000; t += 200)
        {
            sim.Step(200);
            for (int p = 0; p < pw.P.HighWater; p++)
            {
                if (pw.P.Pool.IsAlive(p) && pw.P.State[p] == (byte)PedState.AtStop)
                {
                    maxWait = Math.Max(maxWait, w.Tick - pw.P.T0[p]);
                }
            }
        }

        Assert.True(maxWait < thr, $"max stop wait {maxWait} >= {thr}");
        Assert.Equal(0, sim.Counters.PedStuckEvents);
        Assert.Equal(0, sim.Counters.PedNoPath);
        Assert.Equal(0, sim.Counters.StuckVehicles);
        Assert.Equal(pw.R.IdleCount() + pw.P.AliveCount, pw.R.Count);
        Assert.True(sim.Counters.PedStopBoardings > 200);
    }
}
