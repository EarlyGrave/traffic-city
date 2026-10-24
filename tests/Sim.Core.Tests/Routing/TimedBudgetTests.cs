using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Routing;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Routing;

/// <summary>
/// Лимит плановых запросов дрейфа по работе (2.4a): им не больше 1/<see cref="RoutePhase.TimedBudgetDivisor"/> от <c>work_budget_per_tick</c>
/// (релаксации), а не только от числа запросов. Иначе на s100k (запрос ~20 тыс. релаксаций) плановые съедали половину бюджета и вытесняли поездки.
/// </summary>
public class TimedBudgetTests
{
    private readonly ITestOutputHelper _out;

    public TimedBudgetTests(ITestOutputHelper output) => _out = output;

    private static ScenarioDesc S10k(int vehicles, int workBudget)
    {
        ScenarioDesc d = CityWorldTests.Desc("s10k");
        d.Capacities.MaxVehicles = vehicles + 256;
        d.Capacities.MaxRoutes = 4096;
        d.Capacities.MaxPendingTrips = 2048;
        d.Demand.TargetVehicles = vehicles;
        d.Demand.SpawnRampTicks = 100;
        d.Demand.TypeMix = new[] { 1f, 0f };
        d.Transit.Routes = 0;
        d.Routing = OverlayFixture.Desc(1, 1000f, customizeBudget: 3000);
        d.Routing.PathfindBudgetPerTick = 64;          // доля по числу запросов = 16: лимит по работе должен срабатывать раньше
        d.Routing.WorkBudgetPerTick = workBudget;      // 0 = без ограничения по работе
        d.Routing.CostRecalcTicks = 1_000_000;
        return d;
    }

    /// <summary>Поднимает мир с машинами, у которых есть маршрут, и ставит в очередь плановые запросы на все такие машины. Возвращает их число.</summary>
    private static int Prepare(Simulation sim)
    {
        World w = sim.World;
        for (int t = 0; t < 600 && (w.Vehicles.AliveCount < 40 || w.Trips.Unrouted > 0); t++)
        {
            sim.Step(1);
        }

        Assert.True(w.Vehicles.AliveCount >= 40, $"машин {w.Vehicles.AliveCount}");
        Assert.Equal(0, w.Trips.Unrouted);
        int n = 0;
        VehicleStore v = w.Vehicles;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (v.Pool.IsAlive(i) && v.Route[i] >= 0 && v.Lane[i] >= 0)
            {
                var e = new RerouteEntry { Index = i, Generation = v.Pool.GenerationOf(i), ReadyTick = w.Tick, Kind = RouteNav.KindTimed };
                Assert.True(w.Rerouting.TryEnqueue(in e));
                n++;
            }
        }

        return n;
    }

    /// <summary>Запросы и релаксации тика, в котором очередь плановых запросов только что заполнена.</summary>
    private static (long Queries, long Work) FirstTickAfterBurst(int workBudget, out int queued, out long avgCostBefore)
    {
        ScenarioDesc d = S10k(120, workBudget);
        using Simulation sim = Simulation.Create(in d);
        queued = Prepare(sim);
        SimCounters c0 = sim.Counters;
        avgCostBefore = c0.PathfindQueries > 0 ? c0.PathfindWork / c0.PathfindQueries : 0;
        sim.Step(1);
        SimCounters c1 = sim.Counters;
        return (c1.PathfindQueries - c0.PathfindQueries, c1.PathfindWork - c0.PathfindWork);
    }

    [Fact]
    public void TimedRequests_AreLimitedByWorkShare_NotOnlyByCount()
    {
        // Контроль: без лимита по работе в тике обслуживается доля по числу запросов (16 из 64).
        (long qFree, long wFree) = FirstTickAfterBurst(0, out int queued, out long avgCost);
        _out.WriteLine($"free: queued={queued} queries={qFree} work={wFree} avgCostEarlier={avgCost}");
        Assert.True(qFree >= 8, $"контроль: без лимита по работе в тике должно идти много плановых запросов, получено {qFree}");
        long avg = Math.Max(1, wFree / Math.Max(1, qFree));

        // Бюджет порядка 5 запросов: плановым 1/4 = 1.25 запроса -> первый всегда, второй - только если первый был дешёвым.
        int budget = (int)(5 * avg);
        (long qLim, long wLim) = FirstTickAfterBurst(budget, out _, out _);
        long cap = budget / RoutePhase.TimedBudgetDivisor;
        _out.WriteLine($"limited: budget={budget} cap={cap} queries={qLim} work={wLim} (avg query work {avg})");
        Assert.True(qLim >= 1, "плановый запрос обслуживается хотя бы один");
        Assert.True(qLim < qFree, "лимит по работе должен ужимать плановые запросы");

        // Без лимита по доле работа тика доходила бы до всего work_budget (16660); с лимитом - доля 1/4 плюс последний запрос (запросы разной цены).
        Assert.True(wLim < budget / 2, $"работа плановых запросов в тике {wLim} при work_budget {budget} (доля {cap})");
    }
}
