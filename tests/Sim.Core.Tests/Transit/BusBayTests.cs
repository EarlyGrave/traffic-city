using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Worlds;
using Sim.Core.Transit;
using Xunit;

namespace Sim.Core.Tests.Transit;

/// <summary>
/// Виртуальный карман остановок <c>Bay</c> (ADR-0029, docs/design/bus-bays.md §5, BB1). Сцена: прямая из двух сегментов по 600 м, один маршрут из двух остановок
/// (остановка 0 на первом сегменте, остановка 1 - конечная на втором), автобус выпускается в тике 0; трафик ставят сами тесты. Уступка (bay_yield_s) - BB2.
/// </summary>
public class BusBayTests
{
    private const int Dwell = 600;   // 30 с стоянки

    internal sealed class Scene : IDisposable
    {
        public Simulation Sim = null!;
        public int Lane0, Lane1;
        public float Stop0S;

        public World W => Sim.World;

        public void Dispose() => Sim.Dispose();

        /// <summary>Индекс первого живого автобуса или -1.</summary>
        public int Bus(int nth = 0)
        {
            VehicleStore v = W.Vehicles;
            int k = 0;
            for (int i = 0; i < v.HighWater; i++)
            {
                if (v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0 && k++ == nth)
                {
                    return i;
                }
            }

            return -1;
        }

        public bool Flag(int i, VehicleFlags f) => (W.Vehicles.Flags[i] & (byte)f) != 0;

        /// <summary>Шагает до условия (не больше <paramref name="max"/> тиков); false - не дождались.</summary>
        public bool StepUntil(Func<bool> cond, int max)
        {
            for (int t = 0; t < max; t++)
            {
                if (cond())
                {
                    return true;
                }

                Sim.Step(1);
            }

            return cond();
        }

        public int PlaceCar(int lane, float s, float speed, int destLane, float destS)
        {
            World w = W;
            Assert.Equal(BuildStatus.Ok, w.Router.Build(lane, s, destLane, destS, w.Tick, out RouteHandle h));
            var trip = new PendingTrip { FromZone = -1, ToZone = -1, FromLane = lane, FromS = s, Type = 0, DestLane = destLane, DestS = destS, Route = h.Index, RouteGen = h.Generation };
            Assert.True(Spawner.TryPlace(w, in trip, in w.Types[0], lane, s, true, out int i));
            w.Vehicles.Speed[i] = speed;
            return i;
        }
    }

    internal static Scene Make(BayModel model, int lanes = 1, int headway = 100000, float stopFromEnd = 0f, BusStopKind kind = BusStopKind.Bay, int dwell = Dwell, int threads = 1, int yieldTicks = 60)
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Network = new NetworkDesc
        {
            Nodes = new[]
            {
                new NodeDesc { X = 0, Z = 0, Control = NodeControl.None },
                new NodeDesc { X = 600, Z = 0, Control = NodeControl.None },
                new NodeDesc { X = 1200, Z = 0, Control = NodeControl.None },
            },
            Segments = new[]
            {
                new SegmentDesc { A = 0, B = 1, LanesAB = lanes, LanesBA = lanes, SpeedLimit = 13.9f, Kind = RoadKind.Street },
                new SegmentDesc { A = 1, B = 2, LanesAB = lanes, LanesBA = lanes, SpeedLimit = 13.9f, Kind = RoadKind.Street },
            },
        };
        d.Capacities.MaxRoutes = 128;
        d.Capacities.MaxVehicles = 128;
        VehicleTypeDesc bus = TestScenarios.Car();
        bus.Category = VehicleCategory.Bus;
        bus.Length = 12f;
        bus.MaxSpeed = 20f;
        d.VehicleTypes = new[] { TestScenarios.Car(), bus };
        d.Transit = new TransitDesc
        {
            BusType = 1,
            HeadwayTicks = headway,
            DwellBaseTicks = dwell,
            DwellPerPassengerTicks = 0f,
            PassengerRatePerMin = 0f,
            HoldAlpha = 0f,
            HoldEveryN = 1,
            BayModel = model,
            BayYieldTicks = yieldTicks,
        };
        var s = new Scene { Sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads }) };
        NetworkStore n = s.W.Net;
        s.Lane0 = n.Segments[0].FirstLaneAB;
        s.Lane1 = n.Segments[1].FirstLaneAB;
        s.Stop0S = stopFromEnd > 0f ? n.Lanes[s.Lane0].Length - stopFromEnd : 300f;
        n.BusStops[0] = new BusStopData { Lane = s.Lane0, S = s.Stop0S, Kind = kind, Route = 0, LastArrivalTick = -1, LastDepartTick = -1, BayExitFrom = -1 };
        n.BusStops[1] = new BusStopData { Lane = s.Lane1, S = 300f, Kind = kind, Route = 0, LastArrivalTick = -1, LastDepartTick = -1, BayExitFrom = -1 };
        n.BusRoutes[0] = new BusRouteData { FirstStop = 0, StopCount = 2, HeadwayTicks = headway, VehicleType = 1, LastDispatchTick = -1 };
        n.BusRouteStops[0] = 0;
        n.BusRouteStops[1] = 1;
        n.AdoptBusRoutes(1);
        n.AdoptBusStops(2);
        n.BusRouteStopCount = 2;
        return s;
    }

    [Fact]
    public void Bay_CarPassesBusInBay_CurbWaitsForDeparture()
    {
        foreach (BayModel model in new[] { BayModel.Virtual, BayModel.Curb })
        {
            using Scene sc = Make(model);
            Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.AtStop), 2000), $"{model}: автобус не дошёл до остановки");
            int bus = sc.Bus();
            VehicleStore v = sc.W.Vehicles;
            if (model == BayModel.Virtual)
            {
                Assert.True(sc.Flag(bus, VehicleFlags.InBay));
                Assert.Equal(-1, v.Lane[bus]);
                Assert.Equal(0, sc.W.Occupancy.LaneCount[sc.Lane0]);
                Assert.Equal(1, sc.W.Net.BusStops[0].BayUsed);
                Assert.Equal(bus, sc.W.BayBus[0]);
                Assert.Equal(1, sc.Sim.Counters.BayEntries);
                Assert.Null(sc.W.VerifyDerived());
            }
            else
            {
                Assert.False(sc.Flag(bus, VehicleFlags.InBay));
                Assert.Equal(sc.Lane0, v.Lane[bus]);
            }

            int car = sc.PlaceCar(sc.Lane0, 250f, 12f, sc.Lane1, 500f);
            int gen = v.Pool.GenerationOf(car);
            int maxStill = 0;
            bool passed = false;
            while (sc.Flag(bus, VehicleFlags.AtStop) && v.Pool.IsAlive(car) && v.Pool.GenerationOf(car) == gen)
            {
                maxStill = Math.Max(maxStill, v.StillTicks[car]);
                passed |= v.Lane[car] == sc.Lane0 && v.S[car] > sc.Stop0S + 5f;
                sc.Sim.Step(1);
            }

            if (model == BayModel.Virtual)
            {
                Assert.True(passed, "машина должна проехать мимо автобуса в кармане");
                Assert.True(maxStill < 400, $"StillTicks {maxStill} >= 20 с");
            }
            else
            {
                Assert.False(passed);
                Assert.True(maxStill >= 400, $"Curb: машина должна ждать отправления, StillTicks {maxStill}");
            }
        }
    }

    [Fact]
    public void Bay_ExitInsertsBusIntoLane_AndTripFinishesAtTerminal()
    {
        using Scene sc = Make(BayModel.Virtual);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int bus = sc.Bus();
        VehicleStore v = sc.W.Vehicles;
        Assert.True(sc.StepUntil(() => !sc.Flag(bus, VehicleFlags.InBay), 2000));
        Assert.Equal(sc.Lane0, v.Lane[bus]);
        Assert.Equal(1, sc.W.Occupancy.LaneCount[sc.Lane0]);
        Assert.False(sc.Flag(bus, VehicleFlags.AtStop));
        Assert.Equal(1, v.BusNextStop[bus]);
        Assert.Equal(0, sc.W.Net.BusStops[0].BayUsed);
        Assert.Equal(-1, sc.W.BayBus[0]);
        Assert.Equal(0, sc.Sim.Counters.BayExitWaitTicksTotal);   // трафика нет - выезд сразу после стоянки
        Assert.Equal(sc.Stop0S, v.S[bus], 0.01f);
        Assert.Null(sc.W.VerifyDerived());

        // Конечная: заезд в карман второй остановки, стоянка, автобус исчезает (карман освобождён).
        Assert.True(sc.StepUntil(() => sc.Bus() < 0, 6000), "автобус должен закончить рейс");
        Assert.Equal(2, sc.Sim.Counters.BayEntries);
        Assert.Equal(0, sc.Sim.Counters.BayFallbacks);
        Assert.Equal(0, sc.W.Net.BusStops[0].BayUsed + sc.W.Net.BusStops[1].BayUsed);
        Assert.Equal(-1, sc.W.BayBus[1]);
        Assert.Equal(0, sc.W.Net.BusRoutes[0].ActiveBuses);
        Assert.Null(sc.W.VerifyDerived());
    }

    [Fact]
    public void Bay_ExitWaitsForFastFollower_ThenInsertsWithoutOverlap()
    {
        using Scene sc = Make(BayModel.Virtual);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int bus = sc.Bus();
        VehicleStore v = sc.W.Vehicles;
        Assert.True(sc.StepUntil(() => v.DwellTicks[bus] <= 3, Dwell + 5));
        // Быстрая машина в 38 м позади точки выезда: зазор >= s0 + v*T, но торможение до остановки 2.7 м/с² жёстче b_comf = 2 - выезд отклоняется.
        int car = sc.PlaceCar(sc.Lane0, 250f, 13.9f, sc.Lane1, 500f);
        sc.Sim.Step(10);
        Assert.True(sc.Flag(bus, VehicleFlags.InBay), "автобус не должен выезжать перед быстрой машиной");
        Assert.True(sc.W.Net.BusStops[0].BayExitFrom >= 0);
        string? gaps = null;
        Assert.True(sc.StepUntil(
            () =>
            {
                gaps ??= Step2.CheckGaps(sc.W);
                return !sc.Flag(bus, VehicleFlags.InBay);
            },
            600));
        Assert.Null(gaps);
        Assert.True(sc.Sim.Counters.BayExitWaitTicksTotal >= 10);
        Assert.Equal(sc.Lane0, v.Lane[bus]);
        Assert.Null(Step2.CheckGaps(sc.W));
        Assert.Null(sc.W.VerifyDerived());
        _ = car;
    }

    [Fact]
    public void Bay_Occupied_SecondBusServedInLane()
    {
        using Scene sc = Make(BayModel.Virtual);
        sc.Sim.Step(300);
        sc.W.Net.BusRoutes[0].LastDispatchTick = sc.W.Tick - 100000;   // второй автобус выпускается сразу, третий - нет
        Assert.True(sc.StepUntil(() => sc.Sim.Counters.BayFallbacks > 0, 4000), "второй автобус должен обслуживаться в полосе");
        Assert.Equal(1, sc.Sim.Counters.BayEntries);
        Assert.Equal(1, sc.Sim.Counters.BayFallbacks);
        int inBay = -1;
        int onLane = -1;
        VehicleStore v = sc.W.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || (v.Flags[i] & (byte)VehicleFlags.AtStop) == 0)
            {
                continue;
            }

            if (sc.Flag(i, VehicleFlags.InBay))
            {
                inBay = i;
            }
            else
            {
                onLane = i;
            }
        }

        Assert.True(inBay >= 0 && onLane >= 0);
        Assert.Equal(sc.Lane0, v.Lane[onLane]);
        Assert.Equal(-1, v.Lane[inBay]);
        Assert.Null(sc.W.VerifyDerived());

        // Выезд первого автобуса блокирует второй, стоящий в полосе на точке остановки; после его отправления карман освобождается.
        Assert.True(sc.StepUntil(() => !sc.Flag(inBay, VehicleFlags.InBay), 6000));
        Assert.Equal(0, sc.W.Net.BusStops[0].BayUsed);
        Assert.True(sc.Sim.Counters.BayExitWaitTicksTotal > 0);
        Assert.Null(sc.W.VerifyDerived());
    }

    [Fact]
    public void Bay_WithinGateHorizon_BehavesAsCurb()
    {
        using Scene sc = Make(BayModel.Virtual, stopFromEnd: 10f);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.AtStop), 2000));
        int bus = sc.Bus();
        Assert.False(sc.Flag(bus, VehicleFlags.InBay));
        Assert.Equal(sc.Lane0, sc.W.Vehicles.Lane[bus]);
        Assert.Equal(0, sc.Sim.Counters.BayEntries);
        Assert.Equal(1, sc.Sim.Counters.BayFallbacks);
        Assert.Equal(0, sc.W.Net.BusStops[0].BayUsed);
    }

    [Fact]
    public void BayModelCurbAndLane_AndCurbKindStops_KeepPreviousHash()
    {
        ulong Run(BayModel model, BusStopKind kind)
        {
            using Scene sc = Make(model, headway: 700, kind: kind);
            sc.Sim.Step(3000);
            return sc.Sim.ComputeStateHash();
        }

        ulong curb = Run(BayModel.Curb, BusStopKind.Bay);
        Assert.Equal(curb, Run(BayModel.Lane, BusStopKind.Bay));                // "lane" зарезервирован под настоящий карман: пока как Curb
        Assert.Equal(Run(BayModel.Curb, BusStopKind.Curb), Run(BayModel.Virtual, BusStopKind.Curb));   // остановки Curb виртуальной модели хэш не меняют
        Assert.NotEqual(curb, Run(BayModel.Virtual, BusStopKind.Bay));          // а Bay меняют (раздел хэша «карманы» и поведение)
    }

    [Fact]
    public void Bay_BusInBay_IsDrawnAtStopAndKeepsRenderSlot()
    {
        using Scene sc = Make(BayModel.Virtual);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int bus = sc.Bus();
        sc.Sim.Step(5);
        ReadOnlySpan<VehicleRenderData> r = sc.Sim.Vehicles;
        Assert.True(bus < r.Length);
        Assert.NotEqual(0, r[bus].Flags & (byte)VehicleRenderFlags.Active);
        Assert.NotEqual(0, r[bus].Flags & (byte)VehicleRenderFlags.AtStop);
        sc.Sim.Step(40);   // сдвиг кузова в карман занимает 1.5 с
        r = sc.Sim.Vehicles;
        NetworkStore net = sc.W.Net;
        float laneZ = net.LanePoints[net.Lanes[sc.Lane0].FirstPoint].Z;
        Assert.Equal(sc.W.Builder.Options.LaneWidth, MathF.Abs(r[bus].Z - laneZ), 0.01f);   // сдвинут на ширину полосы из сети
        Assert.True(MathF.Abs(r[bus].Heading) < 0.01f, $"кузов в кармане параллелен полосе, курс {r[bus].Heading}");   // обе оси сдвинуты, перекоса нет
        // Вид (ADR-0019): автобус в кармане попадает в список слотов вида по полосе остановки.
        sc.Sim.SetRenderView(-10000f, -10000f, 10000f, 10000f);
        sc.Sim.Step(2);
        r = sc.Sim.Vehicles;
        Assert.NotEqual(0, r[bus].Flags & (byte)VehicleRenderFlags.Active);
        sc.Sim.SetRenderView(5000f, 5000f, 6000f, 6000f);
        sc.Sim.Step(2);
        r = sc.Sim.Vehicles;
        Assert.Equal(0, r[bus].Flags);
    }

    [Fact]
    public void Bay_StopRemovedWhileBusInBay_RemovesBus()
    {
        using Scene sc = Make(BayModel.Virtual);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        NetworkStore n = sc.W.Net;
        n.BusStops[0] = NetworkStore.DeadBusStop(n.BusStopPool.GenerationOf(0));   // как после RemoveBusStop; полный разбор правок сети с карманом - BB2
        Assert.True(sc.StepUntil(() => sc.Bus() < 0, Dwell + 50));
        Assert.Equal(0, n.BusRoutes[0].ActiveBuses);
    }

    [Fact]
    public void Bay_SaveLoad_AroundEntryAndExit_ContinuesWithSameHash()
    {
        // Эталон: один непрерывный прогон; сейв на каждом тике вокруг заезда и выезда, затем 20 тиков - хэш обязан совпасть.
        const int After = 20;
        int entry = -1;
        int exit = -1;
        using (Scene probe = Make(BayModel.Virtual, dwell: 200))
        {
            for (int t = 0; t < 3000 && exit < 0; t++)
            {
                probe.Sim.Step(1);
                int b = probe.Bus();
                if (b >= 0 && entry < 0 && probe.Flag(b, VehicleFlags.InBay))
                {
                    entry = t;
                }
                else if (entry >= 0 && b >= 0 && !probe.Flag(b, VehicleFlags.InBay))
                {
                    exit = t;
                }
            }
        }

        Assert.True(entry > 0 && exit > entry);
        int from = entry - 3;
        int to = exit + 3;
        var saves = new byte[to + 1][];
        var hashes = new ulong[to + After + 1];
        using (Scene sc = Make(BayModel.Virtual, dwell: 200))
        {
            for (int t = 0; t <= to + After; t++)
            {
                if (t == from || (t > from && t <= to && (t <= entry + 3 || t >= exit - 3)))
                {
                    saves[t] = WorldSaveFixtures.SaveBytes(sc.Sim);
                }

                hashes[t] = sc.Sim.ComputeStateHash();
                sc.Sim.Step(1);
            }
        }

        int checkedSaves = 0;
        for (int t = from; t <= to; t++)
        {
            if (saves[t] == null)
            {
                continue;
            }

            using Simulation loaded = WorldSaveFixtures.Load(saves[t]);
            Assert.Equal(hashes[t], loaded.ComputeStateHash());
            Assert.True(loaded.World.Vehicles.AliveCount >= 0);
            Assert.Equal(saves[t], WorldSaveFixtures.SaveBytes(loaded));   // Save(Load(Save)) побайтно
            loaded.Step(After);
            Assert.True(hashes[t + After] == loaded.ComputeStateHash(), $"сейв в тике {t}: хэш через {After} тиков разошёлся (заезд {entry}, выезд {exit})");
            Assert.Null(loaded.World.VerifyDerived());
            checkedSaves++;
        }

        Assert.True(checkedSaves >= 8);
    }

    [Fact]
    public void Bay_NoAllocationsWithBusInBay()
    {
        using Scene sc = Make(BayModel.Virtual, dwell: 1500);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        sc.Sim.Step(5);
        long bytes = AllocationProbe.Measure(() => sc.Sim.Step(100));
        Assert.Equal(0, bytes);
        Assert.True(sc.Flag(sc.Bus(), VehicleFlags.InBay));
    }

    [Fact]
    public void Bay_BrokenState_IsRejectedByValidator()
    {
        using Scene sc = Make(BayModel.Virtual);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        sc.Sim.Step(3);
        int bus = sc.Bus();
        VehicleStore v = sc.W.Vehicles;
        NetworkStore n = sc.W.Net;
        byte[] good = WorldSaveFixtures.SaveBytes(sc.Sim);
        using (Simulation ok = WorldSaveFixtures.Load(good))
        {
            Assert.Equal(1, ok.World.Net.BusStops[0].BayUsed);
            Assert.Equal(bus, ok.World.BayBus[0]);   // производное пересобрано после загрузки
        }

        void Expect(string what, Action tamper, Action restore)
        {
            tamper();
            byte[] bytes = WorldSaveFixtures.SaveBytes(sc.Sim);
            restore();
            LoadStatus st = Simulation.Load(bytes, new LoadOptions { StrictHash = 0 }, default, out Simulation? s, out _);
            s?.Dispose();
            Assert.True(st != LoadStatus.Ok, $"{what}: сейв с нарушенным инвариантом InBay загрузился");
        }

        Expect("InBay с Lane >= 0", () => v.Lane[bus] = sc.Lane0, () => v.Lane[bus] = -1);
        Expect("BayUsed не сходится со счётом автобусов", () => n.BusStops[0].BayUsed = 0, () => n.BusStops[0].BayUsed = 1);
        Expect("BayUsed = 2", () => n.BusStops[0].BayUsed = 2, () => n.BusStops[0].BayUsed = 1);
        Expect("BayExitFrom позже текущего тика", () => n.BusStops[0].BayExitFrom = sc.W.Tick + 5, () => n.BusStops[0].BayExitFrom = -1);
        Expect("неизвестный бит BayFlags", () => n.BusStops[0].BayFlags = 0x80, () => n.BusStops[0].BayFlags = 0);
        byte flags = v.Flags[bus];
        Expect("InBay без AtStop", () => v.Flags[bus] = (byte)(flags & ~(byte)VehicleFlags.AtStop), () => v.Flags[bus] = flags);
        Expect("InBay не автобус", () => v.Flags[bus] = (byte)(flags & ~(byte)VehicleFlags.IsBus), () => v.Flags[bus] = flags);
    }

    [Fact]
    public void Bay_S1kWithBuses_SameHashAt1_2_3Threads_AndBaysUsed()
    {
        ulong Run(int threads, out SimCounters c)
        {
            ScenarioDesc d = Step2.S1k(buses: true);
            d.Demand.TargetVehicles = 300;
            d.Transit.HeadwayTicks = 400;
            d.Transit.BayModel = BayModel.Virtual;
            d.Transit.BayYieldTicks = 60;
            using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
            sim.Step(6000);
            c = sim.Counters;
            Assert.Null(sim.World.VerifyDerived());
            return sim.ComputeStateHash();
        }

        ulong h1 = Run(1, out SimCounters c1);
        Assert.True(c1.BayEntries > 0, "в s1k за 5 мин ни один автобус не зашёл в карман");
        Assert.Equal(h1, Run(2, out _));
        Assert.Equal(h1, Run(3, out _));
    }
}
