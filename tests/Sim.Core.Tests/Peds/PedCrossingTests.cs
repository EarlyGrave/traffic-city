#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Intersections;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.Peds;

/// <summary>Переходы × машины (pedestrians.md §5, §15 «Переходы»): автомат, обязавшиеся машины, снятие допуска, анти-голодание, PD5, перегон.</summary>
public sealed class PedCrossingTests
{
    private const int MaxFixturePeds = 80;

    /// <summary>Узел-фикстура с пешеходами без собственных поездок: пешеходов в очередь переходов ставит тест.</summary>
    private static ScenarioDesc NodeWithPeds(NodeControl control, params Arm[] arms)
    {
        ScenarioDesc d = GateFixture.Node(control, arms);
        d.Peds = new PedDesc { Enabled = true, Count = 0, Residents = MaxFixturePeds, MidblockAtStops = false, ActivityMinTicks = 100_000, ActivityMaxTicks = 200_000 };
        d.Capacities.MaxPeds = MaxFixturePeds;
        d.Capacities.MaxResidents = MaxFixturePeds;
        d.Capacities.MaxPedPathSteps = 8;
        return d;
    }

    private static Simulation Make(in ScenarioDesc d)
    {
        Simulation sim = Simulation.Create(in d);
        sim.World.Peds!.Wheel.Clear();   // жители не просыпаются сами: пешеходов ставит тест
        return sim;
    }

    /// <summary>Индекс перехода через рукав-сегмент <paramref name="segment"/> узла 0.</summary>
    private static int CrossingOfArm(World w, int segment)
    {
        ref readonly NodeData nd = ref w.Net.Nodes[0];
        for (int a = 0; a < nd.ArmCount; a++)
        {
            if (w.Net.NodeArms[nd.FirstArm + a] == segment)
            {
                Assert.True(w.Peds!.Net.XEdge[nd.FirstArm + a] >= 0, "у рукава нет перехода");
                return nd.FirstArm + a;
            }
        }

        throw new InvalidOperationException("arm not found");
    }

    /// <summary>Ставит пешехода в конец очереди перехода <paramref name="x"/> (как событие «дошёл до бордюра»): житель <paramref name="resident"/> становится активным.</summary>
    private static int AddPed(World w, int x, int resident, int speedMmPerTick = 60)
    {
        PedWorld pw = w.Peds!;
        PedStore ps = pw.P;
        Assert.True(ps.TryAllocate(out int p));
        int step = pw.Net.EdgeOfX(x) | PedStore.StepCross;
        ps.Resident[p] = resident;
        ps.VMmT[p] = speedMmPerTick;
        ps.Leg[p] = (byte)PedLeg.Direct;
        ps.Path[p * ps.MaxSteps] = step;
        ps.PathCount[p] = 1;
        ps.PathCursor[p] = 0;
        ps.GoalEdge[p] = 0;
        ps.GoalSMm[p] = 0;
        ps.TripTick[p] = w.Tick;
        pw.R.Where[resident] = ResidentStore.ToWork;
        pw.R.Ped[resident] = p;
        Crossings.OnArrive(w, p, step);
        return p;
    }

    private static bool RearInZone(World w, int vehicle, int x)
    {
        // Машина на connector'е или выходной полосе, чьё тело накрывает полосу перехода узла x (приближение для проверок).
        PedWorld pw = w.Peds!;
        PedNetwork pn = pw.Net;
        VehicleStore v = w.Vehicles;
        int lane = v.Lane[vehicle];
        ref readonly NodeData nd = ref w.Net.Nodes[pn.Edges[pn.XEdge[x]].Node];
        float zone = pn.CrosswalkWidthM;
        ulong inMask = pn.XIn[x];
        ulong outMask = pn.XOut[x];
        if (w.Net.Lanes[lane].Kind == LaneKind.Connector && w.Net.Lanes[lane].Node == w.Net.Lanes[nd.FirstConnector].Node)
        {
            int b = lane - nd.FirstConnector;
            int g = w.Net.Lanes[lane].ConflictGroup;
            float s = v.S[vehicle];
            float rear = s - v.Length[vehicle];
            if (((inMask >> b) & 1UL) != 0UL && s > 0f && rear < zone)
            {
                return true;
            }

            if (((outMask >> b) & 1UL) != 0UL && s > w.Connectors.Length[g] - zone)
            {
                return true;
            }

            return false;
        }

        for (int b = 0; b < 64; b++)
        {
            if (((outMask >> b) & 1UL) != 0UL)
            {
                int g = w.Net.Lanes[nd.FirstConnector + b].ConflictGroup;
                if (g >= 0 && w.Connectors.OutLane[g] == lane && v.S[vehicle] - v.Length[vehicle] < 0f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------ автомат

    [Fact]
    public void Release_WithoutVehicles_IsImmediate_AndCrossingReopens()
    {
        ScenarioDesc d = NodeWithPeds(NodeControl.Yield, Arm.E(1), Arm.W(1), Arm.S(1));
        using Simulation sim = Make(in d);
        World w = sim.World;
        int x = CrossingOfArm(w, 2);
        int p = AddPed(w, x, 0);
        Assert.Equal((byte)PedState.AtCrossing, w.Peds!.P.State[p]);
        Assert.Equal(-1, w.Peds.P.EvTick[p]);
        sim.Step(1);
        Assert.Equal((byte)PedState.Crossing, w.Peds.P.State[p]);
        Assert.Equal(XStateCode.Busy, w.Peds.X.XState[x]);
        Assert.Equal(1, w.Peds.X.OnCross[x]);
        Assert.NotEqual(0UL, w.Peds.X.NodeXIn[0]);
        int guard = 0;
        while (w.Peds.P.AliveCount > 0 && guard++ < 1000)
        {
            sim.Step(1);
        }

        Assert.Equal(0, w.Peds.P.AliveCount);
        Assert.Equal(XStateCode.Free, w.Peds.X.XState[x]);
        Assert.Equal(0UL, w.Peds.X.NodeXIn[0]);
        Assert.Equal(0UL, w.Peds.X.NodeXOut[0]);
        Assert.Equal(1, sim.Counters.PedCrossReleases);
        Assert.Equal(0, sim.Counters.PedCrossWaitTicksTotal);
        Assert.False(w.Peds.X.IsActive(x));
    }

    [Fact]
    public void Queue_IsFifo_AndReleasedTogether()
    {
        ScenarioDesc d = NodeWithPeds(NodeControl.Yield, Arm.E(1), Arm.W(1), Arm.S(1));
        using Simulation sim = Make(in d);
        World w = sim.World;
        int x = CrossingOfArm(w, 2);
        PedWorld pw = w.Peds!;
        // Окно машин закрыто: пешеходы копятся в FIFO.
        pw.X.CarWindowUntil[x] = 100;
        int a = AddPed(w, x, 0);
        int b = AddPed(w, x, 1);
        int c = AddPed(w, x, 2);
        Assert.Equal(a, pw.X.QHead[x]);
        Assert.Equal(c, pw.X.QTail[x]);
        Assert.Equal(b, pw.P.Next[a]);
        Assert.Equal(3, pw.X.QCount[x]);
        sim.Step(99);
        Assert.Equal(3, pw.X.QCount[x]);
        Assert.Equal(XStateCode.Free, pw.X.XState[x]);
        sim.Step(2);
        Assert.Equal(0, pw.X.QCount[x]);
        Assert.Equal(3, pw.X.OnCross[x]);
        Assert.Equal((byte)PedState.Crossing, pw.P.State[c]);
        Assert.Equal(3, sim.Counters.PedCrossReleases);
        Assert.Equal(300, sim.Counters.PedCrossWaitTicksTotal);
    }

    // ------------------------------------------------------------ машины и переход

    [Fact]
    public void GrantedVehicleThatCannotStop_Passes_PedestrianWaits_NoRevoke()
    {
        ScenarioDesc d = NodeWithPeds(NodeControl.Yield, Arm.E(1), Arm.W(1), Arm.S(1));
        using Simulation sim = Make(in d);
        World w = sim.World;
        int inLane = GateFixture.InLane(w, 2);
        float len = w.Net.LaneLength[inLane];
        int car = GateFixture.Place(sim, inLane, len - 60f, 10f);
        int x = CrossingOfArm(w, 2);
        int guard = 0;
        while (guard++ < 600 && !(GateRules.IsGranted(w.Vehicles, car) && len - w.Vehicles.S[car] - 0.5f < 10f))
        {
            sim.Step(1);
        }

        Assert.True(GateRules.IsGranted(w.Vehicles, car), "машина должна быть допущена");
        Assert.False(PedRules.CanStop(len - w.Vehicles.S[car] - 0.5f, w.Vehicles.Speed[car], 1.5f));
        int p = AddPed(w, x, 0);
        sim.Step(1);
        Assert.Equal((byte)PedState.AtCrossing, w.Peds!.P.State[p]);
        Assert.Equal(XStateCode.Claimed, w.Peds.X.XState[x]);
        bool released = false;
        for (int t = 0; t < 400 && !released; t++)
        {
            bool carClear = w.Vehicles.Lane[car] != inLane && !RearInZone(w, car, x);
            if (w.Peds.P.State[p] == (byte)PedState.Crossing)
            {
                released = true;
                Assert.True(carClear || !w.Vehicles.Pool.IsAlive(car), "пешеход вышел, пока обязавшаяся машина ещё в зоне");
            }
            else
            {
                sim.Step(1);
            }
        }

        Assert.True(released, "пешеход должен выйти после проезда машины");
        Assert.Equal(0, sim.Counters.GrantsRevokedPed);
    }

    [Fact]
    public void GrantedVehicleThatCanStop_LosesGrant_AndStopsBeforeLine_UntilPedestriansPass()
    {
        ScenarioDesc d = NodeWithPeds(NodeControl.Yield, Arm.E(1), Arm.W(1), Arm.S(1));
        using Simulation sim = Make(in d);
        World w = sim.World;
        int inLane = GateFixture.InLane(w, 2);
        float len = w.Net.LaneLength[inLane];
        int car = GateFixture.Place(sim, inLane, len - 70f, 8f);
        int x = CrossingOfArm(w, 2);
        int guard = 0;
        while (guard++ < 800 && !GateRules.IsGranted(w.Vehicles, car))
        {
            sim.Step(1);
        }

        Assert.True(GateRules.IsGranted(w.Vehicles, car));
        float d0 = len - w.Vehicles.S[car] - 0.5f;
        Assert.True(PedRules.CanStop(d0, w.Vehicles.Speed[car], 1.5f), $"d={d0}");
        int p = AddPed(w, x, 0);
        sim.Step(1);
        Assert.Equal(1, sim.Counters.GrantsRevokedPed);
        Assert.False(GateRules.IsGranted(w.Vehicles, car));
        Assert.Equal((byte)PedState.Crossing, w.Peds!.P.State[p]);   // обязавшихся нет: выпуск в том же тике

        bool carMovedOn = false;
        for (int t = 0; t < 900; t++)
        {
            sim.Step(1);
            if (w.Peds.P.State.Length > p && w.Peds.P.Pool.IsAlive(p) && w.Peds.P.State[p] == (byte)PedState.Crossing)
            {
                Assert.True(w.Vehicles.Lane[car] == inLane, "машина въехала в узел, пока пешеход на переходе");
                Assert.True(w.Vehicles.S[car] <= len - 0.5f + 0.01f);
            }

            if (w.Vehicles.Lane[car] != inLane)
            {
                carMovedOn = true;
                break;
            }
        }

        Assert.True(carMovedOn, "после прохода пешехода машина должна проехать");
        Assert.Equal(0, w.Peds.P.AliveCount);
    }

    [Fact]
    public void PedestrianStream_DoesNotStarveVehicle_AntiStarvationWindow()
    {
        ScenarioDesc d = NodeWithPeds(NodeControl.Yield, Arm.E(1), Arm.W(1), Arm.S(1));
        using Simulation sim = Make(in d);
        World w = sim.World;
        int inLane = GateFixture.InLane(w, 2);
        float len = w.Net.LaneLength[inLane];
        int car = GateFixture.Place(sim, inLane, len - 12f, 0f);
        int x = CrossingOfArm(w, 2);
        int resident = 0;
        int leftAt = -1;
        for (int t = 0; t < 1500; t++)
        {
            if (t < 1000 && t % 20 == 0 && resident < MaxFixturePeds - 1 && w.Peds!.P.AliveCount < MaxFixturePeds - 2)
            {
                while (resident < MaxFixturePeds && w.Peds.R.Where[resident] != ResidentStore.AtHome)
                {
                    resident++;
                }

                if (resident < MaxFixturePeds)
                {
                    AddPed(w, x, resident);
                }
            }

            sim.Step(1);
            if (w.Vehicles.Lane[car] != inLane)
            {
                leftAt = t;
                break;
            }
        }

        // max_close 10 с + дохождение вышедших (переход 10 м при 1.1-1.5 м/с ≤ 9 с) + окно машин: машина уезжает не позже 30 с.
        Assert.True(leftAt >= 0 && leftAt < 30 * 20, $"машина не проехала за 30 с (leftAt={leftAt})");
        Assert.True(sim.Counters.PedCrossReleases > 3);
        Assert.True(sim.Counters.CarCrossDelayTicks > 0);
    }

    // ------------------------------------------------------------ PD5 на городе

    [Fact]
    public void SmallCity_NoVehicleEntersCrossingZoneWhilePedestriansCross_AndNothingStuck()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Peds = new PedDesc
        {
            Enabled = true,
            Count = 250,
            Residents = 500,
            SpawnRampTicks = 200,
            ActivityMinTicks = 100,
            ActivityMaxTicks = 400,
            MidblockAtStops = true,
        };
        d.Capacities.MaxPeds = 340;
        d.Capacities.MaxResidents = 500;
        d.Capacities.MaxPedPathSteps = 64;
        d.Transit.PassengerModel = PassengerModel.Agents;
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        World w = sim.World;
        PedWorld pw = w.Peds!;
        PedNetwork pn = pw.Net;
        VehicleStore v = w.Vehicles;
        int xn = PedWorld.XCount(w);
        var baseline = new HashSet<int>[xn];
        var prev = new HashSet<int>[xn];
        var released = new bool[xn];
        var state = new byte[xn];
        for (int x = 0; x < xn; x++)
        {
            prev[x] = new HashSet<int>();
        }

        long busyTicks = 0;
        for (int t = 0; t < 4000; t++)
        {
            sim.Step(1);
            // Занятость зон переходов узлов на конец тика: машины на connector'ах и в начале выходных полос.
            var cur = new HashSet<int>[xn];
            int hw = v.HighWater;
            for (int i = 0; i < hw; i++)
            {
                if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
                {
                    continue;
                }

                ref readonly LaneData ld = ref w.Net.Lanes[v.Lane[i]];
                int node = ld.Kind == LaneKind.Connector ? ld.Node : (ld.Kind == LaneKind.Normal && v.S[i] - v.Length[i] < 0f ? (ld.Direction == 0 ? w.Net.Segments[ld.Segment].NodeA : w.Net.Segments[ld.Segment].NodeB) : -1);
                if (node < 0)
                {
                    continue;
                }

                ref readonly NodeData nd = ref w.Net.Nodes[node];
                for (int a = 0; a < nd.ArmCount; a++)
                {
                    int x = nd.FirstArm + a;
                    if (pn.XEdge[x] >= 0 && RearInZone(w, i, x))
                    {
                        (cur[x] ??= new HashSet<int>()).Add(i);
                    }
                }
            }

            for (int x = 0; x < pn.MidXBase; x++)
            {
                byte st = pw.X.XState[x];
                HashSet<int> now = cur[x] ?? new HashSet<int>();
                if (st != XStateCode.Free && state[x] == XStateCode.Free)
                {
                    baseline[x] = new HashSet<int>(prev[x]);   // закрытие: кто накрывал зону на начало этого тика
                    released[x] = false;
                }

                if (st != XStateCode.Free && pw.X.OnCross[x] > 0)
                {
                    if (!released[x])
                    {
                        released[x] = true;
                        baseline[x].UnionWith(now);   // в момент выпуска в зоне могут стоять не обязавшиеся (исключение 3 с)
                    }

                    foreach (int i in now)
                    {
                        Assert.True(baseline[x].Contains(i), $"тик {sim.Tick}: машина {i} въехала в зону перехода {x} при {pw.X.OnCross[x]} пешеходах на нём");
                    }

                    busyTicks++;
                }

                state[x] = st;
                prev[x] = now;
            }
        }

        Assert.True(sim.Counters.PedCrossReleases > 20, $"releases {sim.Counters.PedCrossReleases}");
        Assert.True(busyTicks > 0);
        Assert.Equal(0, sim.Counters.StuckVehicles);
        Assert.Equal(0, sim.Counters.PedStuckEvents);
    }
}
