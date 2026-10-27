using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Tools;
using Sim.Core.Tests.Worlds;
using Xunit;
using Scene = Sim.Core.Tests.Transit.BusBayTests.Scene;

namespace Sim.Core.Tests.Transit;

/// <summary>BB2 (ADR-0029): уступка ведомых автобусу, ждущему выезда из кармана; правки сети с автобусом в кармане.</summary>
public class BusBayYieldTests
{
    /// <summary>Ставит машину на начало первой полосы, если есть место; false - нет (хвост очереди у стены).</summary>
    private static bool TryCar(Scene sc, float s, float speed)
    {
        World w = sc.W;
        if (w.Router.Build(sc.Lane0, s, sc.Lane1, 500f, w.Tick, out RouteHandle h) != BuildStatus.Ok)
        {
            return false;
        }

        var trip = new PendingTrip { FromZone = -1, ToZone = -1, FromLane = sc.Lane0, FromS = s, Type = 0, DestLane = sc.Lane1, DestS = 500f, Route = h.Index, RouteGen = h.Generation };
        if (!Spawner.TryPlace(w, in trip, in w.Types[0], sc.Lane0, s, true, out int i))
        {
            w.Routes.Free(h);
            return false;
        }

        w.Vehicles.Speed[i] = speed;
        return true;
    }

    /// <summary>Автобус в кармане, у него закончилась стоянка; поток машин по 1.4 с (зазор не открывается) идёт дальше, пока автобус не выехал или не вышло <paramref name="limit"/> тиков.</summary>
    private static (int WaitTicks, Scene Scene, int Bus) RunWithStream(int yieldTicks, int limit)
    {
        Scene sc = BusBayTests.Make(BayModel.Virtual, dwell: 600, yieldTicks: yieldTicks);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int bus = sc.Bus();
        int n = 0;
        while (sc.W.Vehicles.DwellTicks[bus] > 2)
        {
            if (n++ % 28 == 0)
            {
                TryCar(sc, 20f, 13.9f);   // поток успевает дойти до остановки к концу стоянки
            }

            sc.Sim.Step(1);
        }

        int t0 = (int)sc.W.Tick;
        for (int t = 0; t < limit && sc.Flag(bus, VehicleFlags.InBay); t++)
        {
            if (t % 28 == 0)
            {
                TryCar(sc, 20f, 13.9f);
            }

            sc.Sim.Step(1);
        }

        return ((int)sc.W.Tick - t0, sc, bus);
    }

    [Fact]
    public void Yield_StreamOfCars_OpensGapAfterBayYield_Without_Yield_BusStarves()
    {
        var (waitNo, no, busNo) = RunWithStream(yieldTicks: 1200, limit: 700);
        using (no)
        {
            Assert.True(no.Flag(busNo, VehicleFlags.InBay), "без уступки автобус в плотном потоке не выезжает");
            Assert.Equal(0, no.Sim.Counters.BayYields);
            Assert.Equal(700, waitNo);
        }

        var (wait, sc, bus) = RunWithStream(yieldTicks: 60, limit: 700);
        using (sc)
        {
            Assert.False(sc.Flag(bus, VehicleFlags.InBay), $"после уступки автобус должен выехать, ждал {wait} тиков");
            Assert.Equal(1, sc.Sim.Counters.BayYields);
            Assert.True(wait >= 60 && wait < 600, $"выезд через {wait} тиков");
            Assert.Equal(-1f, sc.W.LaneYieldS[sc.Lane0]);   // стена снята с выездом
            Assert.Equal(0, sc.W.BayYieldActive);
            Assert.Equal(0, sc.W.Net.BusStops[0].BayFlags);
            Assert.Null(Step2.CheckGaps(sc.W));
            Assert.Null(sc.W.VerifyDerived());
        }
    }

    [Fact]
    public void Yield_WallIsAtBusRear_AndFollowersStopBehindIt()
    {
        var sc = BusBayTests.Make(BayModel.Virtual, dwell: 200, yieldTicks: 0);
        using (sc)
        {
            Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
            int bus = sc.Bus();
            Assert.True(sc.StepUntil(() => sc.W.Vehicles.DwellTicks[bus] <= 3, 700));
            // Машина A в 38 м позади: остановиться перед стеной не успевает (48 м тормозного пути), проезжает и блокирует выезд; уступка поднимается сразу (bay_yield_s = 0).
            // Машина B в 140 м позади успевает: встаёт перед стеной, и автобус выезжает.
            Assert.True(TryCar(sc, 110f, 13.9f));
            Assert.True(TryCar(sc, 250f, 13.9f));
            int carB = sc.W.Occupancy.LaneTail[sc.Lane0];
            sc.Sim.Step(6);
            Assert.True(sc.Flag(bus, VehicleFlags.InBay));
            Assert.Equal(1, sc.W.Net.BusStops[0].BayFlags);
            Assert.Equal(sc.Stop0S - 12f, sc.W.LaneYieldS[sc.Lane0], 0.001f);
            Assert.Equal(1, sc.W.BayYieldActive);
            Assert.True(sc.StepUntil(() => !sc.Flag(bus, VehicleFlags.InBay), 600));
            Assert.True(sc.W.Vehicles.S[carB] < sc.Stop0S - 12f, "машина B должна была остановиться перед стеной, а не проехать");
            Assert.Null(Step2.CheckGaps(sc.W));
        }
    }

    [Fact]
    public void Yield_BusPack_BayBusDoesNotStarve()
    {
        // Пачка автобусов у одной остановки (интервал короче стоянки) раньше держала автобус в кармане, пока очередь не рассосётся (BB1): теперь после bay_yield_s
        // ведомые встают у тыла автобуса, и он выезжает, как только отправился автобус, стоявший в полосе.
        using Scene sc = BusBayTests.Make(BayModel.Virtual, headway: 200, yieldTicks: 60);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int first = sc.Bus();
        VehicleHandle h = sc.W.Vehicles.HandleOf(first);
        Assert.True(sc.StepUntil(() => !sc.W.Vehicles.IsAlive(h) || !sc.Flag(first, VehicleFlags.InBay), 3000), "автобус в кармане голодает");
        Assert.True(sc.Sim.Counters.BayYields >= 1);
        Assert.Null(Step2.CheckGaps(sc.W));
        Assert.Null(sc.W.VerifyDerived());
    }

    [Fact]
    public void Yield_SaveLoad_RebuildsWall_AndContinuesWithSameHash()
    {
        using Scene sc = BusBayTests.Make(BayModel.Virtual, dwell: 200, yieldTicks: 0);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int bus = sc.Bus();
        Assert.True(sc.StepUntil(() => sc.W.Vehicles.DwellTicks[bus] <= 3, 700));
        Assert.True(TryCar(sc, 250f, 13.9f));
        sc.Sim.Step(8);
        Assert.Equal(1, sc.W.BayYieldActive);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sc.Sim);
        ulong h0 = sc.Sim.ComputeStateHash();
        using Simulation loaded = WorldSaveFixtures.Load(bytes);
        Assert.Equal(h0, loaded.ComputeStateHash());
        Assert.Equal(1, loaded.World.BayYieldActive);
        Assert.Equal(sc.W.LaneYieldS[sc.Lane0], loaded.World.LaneYieldS[sc.Lane0]);
        sc.Sim.Step(300);
        loaded.Step(300);
        Assert.Equal(sc.Sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(0, loaded.World.BayYieldActive);
    }

    [Fact]
    public void Network_RemoveRoadOnStopSegment_IsRejected_BusStaysInBay()
    {
        using Scene sc = BusBayTests.Make(BayModel.Virtual);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int bus = sc.Bus();
        var cmd = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sc.Sim, 0) }, 1);
        Assert.Equal(RejectReason.NotSupported, ToolsFixture.Run(sc.Sim, in cmd));
        Assert.True(sc.Flag(bus, VehicleFlags.InBay));
        Assert.Null(sc.W.VerifyDerived());
    }

    [Fact]
    public void Network_ClosedLaneUnderBus_RemovesBusAndKeepsInvariants()
    {
        using Scene sc = BusBayTests.Make(BayModel.Virtual);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        Assert.Equal(1, SegmentUsage(sc));   // дренаж видит автобус в кармане как занятость сегмента
        sc.W.Net.ToolsTouched = 1;
        sc.W.Net.Lanes[sc.Lane0].Flags |= LaneFlags.Closed;   // как закрытие сегмента дренажем (SegmentClosure): въезд запрещён
        sc.Sim.Step(2);
        Assert.True(sc.Bus() < 0, "полоса закрыта - вставлять некуда, автобус снимается");
        Assert.Equal(0, sc.W.Net.BusStops[0].BayUsed);
        Assert.Equal(-1, sc.W.BayBus[0]);
        Assert.Equal(0, sc.W.Net.BusRoutes[0].ActiveBuses);
        Assert.Null(sc.W.VerifyDerived());
        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sc.Sim));
        Assert.Equal(sc.Sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    private static int SegmentUsage(Scene sc) => ToolOpsPhase.CountOn(sc.W, 0);

    [Fact]
    public void Network_BuildRoadAtStopNode_RelocatesBay_BusExitsAfterwards()
    {
        using Scene sc = BusBayTests.Make(BayModel.Virtual, dwell: 400, yieldTicks: 0);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        int bus = sc.Bus();
        Assert.True(sc.StepUntil(() => sc.W.Vehicles.DwellTicks[bus] <= 3, 700));
        Assert.True(TryCar(sc, 250f, 13.9f));
        sc.Sim.Step(8);
        Assert.Equal(1, sc.W.BayYieldActive);

        // Боковая дорога в узле конца сегмента с остановкой: узел перестраивается, индексы полос переносятся (Relocate).
        var road = new BuildRoadCmd { Ax = 600f, Az = 0f, Bx = 600f, Bz = 120f, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        var cmd = Command.MakeBuildRoad(in road, 2);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sc.Sim, in cmd));
        NetworkStore n = sc.W.Net;
        Assert.True(sc.Flag(bus, VehicleFlags.InBay) || sc.Bus() < 0);
        if (sc.Bus() >= 0)
        {
            int lane = n.BusStops[0].Lane;
            Assert.Equal(sc.Stop0S - 12f, sc.W.LaneYieldS[lane], 0.001f);   // стена переехала вместе с индексом полосы
            Assert.Equal(1, sc.W.BayYieldActive);
        }

        Assert.Null(sc.W.VerifyDerived());
        using (Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sc.Sim)))
        {
            Assert.Equal(sc.Sim.ComputeStateHash(), loaded.ComputeStateHash());
            sc.Sim.Step(200);
            loaded.Step(200);
            Assert.Equal(sc.Sim.ComputeStateHash(), loaded.ComputeStateHash());
        }

        Assert.True(sc.StepUntil(() => sc.Bus() < 0 || !sc.Flag(sc.Bus(), VehicleFlags.InBay), 1500), "автобус выезжает (или снят) после правки сети");
        Assert.Equal(0, sc.W.BayYieldActive);
        Assert.Null(sc.W.VerifyDerived());
    }

    [Fact]
    public void Yield_NoAllocations_S1kThreadsAgree()
    {
        using Scene sc = BusBayTests.Make(BayModel.Virtual, dwell: 3000, yieldTicks: 0);
        Assert.True(sc.StepUntil(() => sc.Bus() >= 0 && sc.Flag(sc.Bus(), VehicleFlags.InBay), 2000));
        sc.Sim.Step(5);
        long bytes = AllocationProbe.Measure(() => sc.Sim.Step(100));
        Assert.Equal(0, bytes);

        ulong Run(int threads)
        {
            ScenarioDesc d = Step2.S1k(buses: true);
            d.Demand.TargetVehicles = 600;
            d.Transit.HeadwayTicks = 300;
            d.Transit.BayModel = BayModel.Virtual;
            d.Transit.BayYieldTicks = 0;
            using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
            sim.Step(6000);
            Assert.True(sim.Counters.BayEntries > 0);
            Assert.Null(sim.World.VerifyDerived());
            return sim.ComputeStateHash();
        }

        ulong h1 = Run(1);
        Assert.Equal(h1, Run(2));
        Assert.Equal(h1, Run(3));
    }
}
