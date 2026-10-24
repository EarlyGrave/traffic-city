using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Routing;

/// <summary>
/// H3 (routing.md 9.12): маршрут = план-стек + окно полосовых шагов; дозаполнение окон, дрейф, бюджет работы, детерминизм, 0 B/тик.
/// Тесты H-T6..H-T10.
/// </summary>
public class WindowRoutingTests
{
    // s10k: сеть и зоны из scenarios/s10k.json, машин немного (тест на минуты в Debug), короткий период пересчёта и малый бюджет
    // кастомизации - волна идёт почти всё время.
    private static ScenarioDesc S10k(int vehicles, int levels = 1, float cell = 1000f)
    {
        ScenarioDesc d = CityWorldTests.Desc("s10k");
        d.Capacities.MaxVehicles = vehicles + 256;
        d.Capacities.MaxRoutes = 4096;
        d.Capacities.MaxPendingTrips = 2048;
        d.Demand.TargetVehicles = vehicles;
        d.Demand.SpawnRampTicks = 200;
        d.Demand.TypeMix = new[] { 1f, 0f };
        d.Routing = OverlayFixture.Desc(levels, cell, customizeBudget: 3000);
        d.Routing.PathfindBudgetPerTick = 32;
        d.Routing.CostRecalcTicks = 200;
        return d;
    }

    private static int FirstZoneWithLanes(World w, int from, int step)
    {
        for (int z = from; z >= 0 && z < w.Net.ZoneCount; z += step)
        {
            if (w.Net.Zones[z].SpawnLaneCount > 0 && w.Net.Zones[z].IsExternal == 0)
            {
                return z;
            }
        }

        throw new InvalidOperationException("no zone with spawn lanes");
    }

    // H-T6: два прогона s10k, хэши равны каждые 100 тиков; в прогоне идут волны кастомизации, дозаполнения и плановые проверки.
    [Fact]
    public void S10k_TwoRuns_HashesEqual_WithWavesAndRefills()
    {
        ScenarioDesc d = S10k(600, levels: 2, cell: 500f);
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        for (int t = 0; t < 2000; t += 100)
        {
            a.Step(100);
            b.Step(100);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }

        SimCounters c = a.Counters;
        Assert.True(c.WindowRefills > 0, "refills");
        Assert.True(c.CustomizeRows > 0, "waves");
        Assert.True(c.CostRecalcs > 0, "recalcs");
        Assert.True(c.TimedChecks > 0, "timed checks");
        Assert.True(c.PlanUnpacks > 0, "lazy unpacks");
        Assert.Equal(0, c.RefillLate);
        Assert.Equal(c.WindowRefills, b.Counters.WindowRefills);
        Assert.Equal(c.PlanUnpacks, b.Counters.PlanUnpacks);
    }

    // H-T7: 0 B/тик на s10k с идущими волнами, дозаполнениями и плановыми проверками.
    [Fact]
    public void S10k_StationaryTicks_AllocateZeroBytes()
    {
        ScenarioDesc d = S10k(500);
        using Simulation sim = Simulation.Create(in d);
        sim.Step(2200); // рампа, несколько пересчётов весов
        long refills = sim.Counters.WindowRefills;
        long rows = sim.Counters.CustomizeRows;
        long allocated = AllocationProbe.Measure(() => sim.Step(400), out _);
        Assert.Equal(0, allocated);
        Assert.True(sim.Counters.WindowRefills > refills);
        Assert.True(sim.Counters.CustomizeRows > rows);
    }

    private static Simulation OneLongTrip(ScenarioDesc d, out int zone, out int to)
    {
        d.Demand.TargetVehicles = 0;
        d.Transit.Routes = 0; // без автобусов: в мире одна машина
        Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        zone = FirstZoneWithLanes(w, 0, 1);
        to = FirstZoneWithLanes(w, w.Net.ZoneCount - 1, -1);
        var cmd = new SpawnTripCmd { FromZone = zone, ToZone = to, FromLane = -1, VehicleType = 0, Count = 1 };
        Assert.True(sim.Enqueue(Command.MakeSpawnTrip(in cmd)));
        return sim;
    }

    // H-T9: длинный маршрут через всю карту s10k: окно дозаполняется заранее, RefillLate = 0, машина едет по своему маршруту и прибывает.
    [Fact]
    public void LongRoute_S10k_RefillsAhead_NeverLate_AndArrives()
    {
        ScenarioDesc d = S10k(0);
        using Simulation sim = OneLongTrip(d, out _, out _);
        World w = sim.World;
        int maxPlan = 0;
        int maxWin = 0;
        for (int t = 0; t < 40000 && sim.Counters.TripsCompleted == 0; t++)
        {
            sim.Step(1);
            Assert.Null(Step3.CheckRoutes(w, out _));
            Assert.Null(Step2.CheckGaps(w));
            for (int i = 0; i < w.Vehicles.HighWater; i++)
            {
                if (w.Vehicles.Pool.IsAlive(i) && w.Vehicles.Route[i] >= 0)
                {
                    maxPlan = Math.Max(maxPlan, w.Routes.PlanCount[w.Vehicles.Route[i]]);
                    maxWin = Math.Max(maxWin, w.Routes.WinCount[w.Vehicles.Route[i]]);
                }
            }
        }

        Assert.Equal(1, sim.Counters.TripsCompleted);
        Assert.Equal(0, sim.Counters.RefillLate);
        Assert.Equal(0, sim.Counters.Reroutes);
        Assert.True(sim.Counters.WindowRefills >= 3, $"refills {sim.Counters.WindowRefills}");
        Assert.True(maxPlan > 0, "план за окном был");
        Assert.True(maxWin <= w.Routes.WindowSteps);
        Assert.Equal(0, w.Routes.AliveCount); // слот освобождён при прибытии
    }

    // Окно: после дозаполнения курсор сдвинут, окно по-прежнему содержит текущую полосу и ведёт к цели без разрывов.
    [Fact]
    public void Refill_ShiftsCursor_AndKeepsWindowConsistent()
    {
        ScenarioDesc d = S10k(0);
        using Simulation sim = OneLongTrip(d, out _, out _);
        World w = sim.World;
        sim.Step(60);
        Assert.Equal(1, w.Vehicles.AliveCount);
        int i = 0;
        while (!w.Vehicles.Pool.IsAlive(i))
        {
            i++;
        }

        int r = w.Vehicles.Route[i];
        Assert.True(r >= 0);
        int win0 = w.Routes.WinCount[r];
        Assert.True(win0 <= w.Routes.WindowSteps);
        Assert.True(w.Routes.PlanCount[r] > 0, "маршрут через карту не помещается в окно");
        long refills = sim.Counters.WindowRefills;
        bool shifted = false;
        for (int t = 0; t < 8000 && sim.Counters.WindowRefills < refills + 2; t++)
        {
            int cursorBefore = w.Vehicles.RouteCursor[i];
            sim.Step(1);
            if (!w.Vehicles.Pool.IsAlive(i))
            {
                break;
            }

            shifted |= w.Vehicles.RouteCursor[i] < cursorBefore;
            Assert.Null(Step3.CheckRoutes(w, out _));
        }

        Assert.True(sim.Counters.WindowRefills >= refills + 2);
        Assert.True(shifted, "курсор сдвигается назад на число сохранённых/выброшенных шагов");
    }

    // H-T8: дрейф. Стабильные веса - плановая проверка запрос не ставит; синтетическая пробка (SpeedRatio 0.2) - ставит.
    [Fact]
    public void Drift_StableWeights_NoTrigger_Jam_Triggers()
    {
        ScenarioDesc d = S10k(0);
        d.Routing.CostRecalcTicks = 1_000_000; // веса меняет только тест
        using Simulation calm = OneLongTrip(d, out _, out _);
        calm.Step(4000); // машина индекс 0: проверка в тиках 1200, 2400, 3600 (пропускается, если до конца сегмента < 80 м)
        Assert.True(calm.Counters.TimedChecks > 0);
        Assert.Equal(0, calm.Counters.TimedTriggered);

        using Simulation jam = OneLongTrip(d, out _, out _);
        World w = jam.World;
        jam.Step(100);
        Assert.Equal(1, w.Vehicles.AliveCount);
        var loads = new SegmentLoad[w.Net.SegmentCount];
        for (int s = 0; s < loads.Length; s++)
        {
            loads[s].SpeedRatio = 0.2f;
        }

        w.Graph.RecalcCosts(loads, w.Net.SegmentCount);
        w.Overlay!.AfterRecalc(w.Graph.WeightsVersion);
        jam.Step(3900);
        Assert.True(jam.Counters.TimedChecks > 0);
        Assert.True(jam.Counters.TimedTriggered > 0, "остаток стоимости вырос в разы - запрос поставлен");
    }

    // Остаток стоимости только что построенного маршрута совпадает со стоимостью плана (окно + стек, матричные дуги O(1)).
    [Fact]
    public void RemainingCost_OfFreshRoute_MatchesPlanCost()
    {
        ScenarioDesc d = S10k(0, levels: 2, cell: 500f);
        using Simulation sim = OneLongTrip(d, out _, out _);
        World w = sim.World;
        int i = -1;
        for (int t = 0; t < 300 && i < 0; t++)
        {
            sim.Step(1);
            for (int k = 0; k < w.Vehicles.HighWater; k++)
            {
                if (w.Vehicles.Pool.IsAlive(k) && w.Vehicles.Route[k] >= 0)
                {
                    i = k;
                }
            }
        }

        Assert.True(i >= 0);
        int r = w.Vehicles.Route[i];
        float planCost = w.Routes.PlanCost[r];
        float remaining = w.Router.RemainingCost(i);
        float elapsed = (w.Tick - w.Routes.PlanTick[r]) * 0.05f;
        Assert.True(Math.Abs(remaining - (planCost - elapsed)) <= (0.1f * planCost) + 3f, $"remaining {remaining} plan {planCost} elapsed {elapsed}");
    }

    // H-T10: бюджет работы мал - запросы обслуживаются по одному за тик в порядке очереди, дозаполнения не голодают.
    [Fact]
    public void WorkBudget_SmallServesInQueueOrder_RefillsNotStarved()
    {
        ScenarioDesc d = Step3.S1k();
        d.Routing.WorkBudgetPerTick = 1;
        d.Routing.PathfindBudgetPerTick = 0;
        d.Demand.SpawnRampTicks = 1200;
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        long prev = 0;
        for (int t = 0; t < 4000; t++)
        {
            sim.Step(1);
            long q = sim.Counters.PathfindQueries;
            Assert.True(q - prev <= 1, $"tick {w.Tick}: {q - prev} queries with work budget 1");
            prev = q;
            bool gap = false;
            for (int k = 0; k < w.Trips.Count; k++)
            {
                bool has = w.Trips.At(k).HasRoute;
                Assert.False(gap && has, $"tick {w.Tick}: trip {k} routed after an unrouted one (queue order)");
                gap |= !has;
            }
        }

        Assert.True(sim.Counters.WindowRefills > 0);
        Assert.Equal(0, sim.Counters.RefillLate);
        Assert.True(sim.Counters.RouteDeferred > 0, "очередь упирается в бюджет");
    }

    // Автобусы ездят через ту же иерархическую маршрутизацию: прибывают на остановки, интервал измеряется.
    [Fact]
    public void Buses_UseHierarchicalRouting_AndReachStops()
    {
        ScenarioDesc d = Step2.S1k(buses: true);
        using Simulation sim = Simulation.Create(in d);
        sim.Step(12000);
        SimCounters c = sim.Counters;
        sim.GetStats(out SimStats st);
        Assert.True(c.BusDispatched > 0, "dispatched");
        Assert.True(c.BusStopArrivals > 0, "stop arrivals");
        Assert.True(c.BusHeadwaySamples > 0);
        Assert.True(st.BusHeadwayDeviationS >= 0f);
        Assert.Equal(0, c.RefillLate);
    }

    // Старый кэш магистралей и хабы убраны: сценарий не знает ключа max_route_steps, а оверлей на s1k включён.
    [Fact]
    public void S1k_UsesOverlay_NoRouteCache()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        Assert.NotNull(sim.World.Overlay);
        sim.Step(1200);
        Assert.True(sim.Counters.PathfindQueries > 0);
        Assert.True(sim.Counters.PathfindWork > 0);
        Assert.True(sim.World.Routes.AliveCount <= sim.World.Vehicles.AliveCount + sim.World.Trips.Count);
    }
}
