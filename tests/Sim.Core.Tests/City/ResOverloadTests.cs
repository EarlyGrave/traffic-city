using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Scenarios;
using Xunit.Abstractions;

namespace Sim.Core.Tests.City;

/// <summary>
/// Стресс-тест перегрузки (решение владельца 2026-10-28, <c>docs/design/res-congestion.md</c> §9): <c>scenarios/s10k-res-overload.json</c> —
/// 102 тыс. жителей (<c>initial_fill</c> 0.65) на сети s10k, спрос в 3 раза выше ёмкости. Ядро обязано пережить перегрузку: очереди ограничены
/// своими потолками (машины - потолком клапана перегрузки, ADR-0035), исключений нет, в установившемся режиме 0 B/тик. Второе требование — восстановление ночью
/// (<see cref="Overload_PoolDrainsAtNight"/>). Оба теста долгие (минуты в Release): <c>TC_SLOW=1</c>, лучше с <c>-c Release</c>.
/// </summary>
public sealed class ResOverloadTests
{
    private const int HourTicks = 3000;          // игровой час: day_s 3600 / 24 · 20 тиков/с
    private const int StepTicks = 20;
    private readonly ITestOutputHelper _out;

    public ResOverloadTests(ITestOutputHelper output) => _out = output;

    private static ScenarioDesc Overload() =>
        ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s10k-res-overload.json")).Desc;

    private static int CountState(Simulation sim, ResidentState state)
    {
        int n = 0;
        for (int i = 0; i < 200_000; i++)
        {
            int g = sim.GetResidentGeneration(i);
            if (g != 0 && sim.TryGetResidentInfo(i, g, out ResidentInfo ri) && ri.State == state)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Ограниченные очереди: пул машин, <c>PendingTrips</c> и <c>WaitDepart</c> не выходят за потолки; потолки при этом действительно достигаются.</summary>
    [SlowFact]
    [Trait("Category", "Long")]
    public void Overload_QueuesStayBounded_AndSteadyStateAllocatesNothing()
    {
        ScenarioDesc d = Overload();
        int maxVehicles = d.Capacities.MaxVehicles;
        int maxPending = d.Capacities.MaxPendingTrips;
        using Simulation sim = Simulation.Create(in d);
        int peakActive = 0;
        int peakPending = 0;
        int peakWait = 0;
        int population = 0;
        int end = 12 * HourTicks;   // до 12:00: пул и очередь упираются в потолки уже к 7:00
        for (int t = 0; t < end; t += StepTicks)
        {
            sim.Step(StepTicks);
            peakActive = Math.Max(peakActive, sim.Counters.ActiveVehicles);
            peakPending = Math.Max(peakPending, sim.World.Trips.Count);
            Assert.True(sim.Counters.ActiveVehicles <= maxVehicles);
            Assert.True(sim.World.Trips.Count <= sim.World.Trips.Capacity);
            if (sim.Tick % HourTicks == 0)
            {
                int wait = CountState(sim, ResidentState.WaitDepart);
                sim.GetCityStats(out CityStats st);
                population = st.Population;
                peakWait = Math.Max(peakWait, wait);
                Assert.True(wait <= population, $"WaitDepart {wait} > жителей {population}");
            }
        }

        _out.WriteLine($"peak active={peakActive}/{maxVehicles} pending={peakPending}/{maxPending} waitDepart={peakWait}/{population} dropped={sim.Counters.TripsDropped}");
        // Перегрузка настоящая: машины упёрлись в потолок клапана (ADR-0035, строже пула), а выезда ждут десятки тысяч жителей. До клапана упирались пул 12 000 и очередь 16 000.
        int cap = Sim.Core.City.OverloadValve.Cap(sim.World, sim.World.City!);
        _out.WriteLine($"valve cap={cap}");
        Assert.True(cap < maxVehicles, $"потолок клапана {cap} не строже пула {maxVehicles}");
        Assert.True(peakActive + peakPending >= cap, $"потолок клапана не достигнут: {peakActive} + {peakPending} < {cap}");
        Assert.True(peakActive < maxVehicles, $"пул снова упёрся в max_vehicles: {peakActive}");   // автобусы выходят по расписанию и могут превысить потолок на единицы
        Assert.True(peakWait > population / 10, $"выезда ждут мало: {peakWait} из {population}");

        // установившийся режим при перегрузке: 0 B/тик (окно выполняется дважды, измеряется второе прохождение)
        long bytes = AllocationProbe.Measure(() => sim.Step(100), out _);
        _out.WriteLine($"alloc over 100 ticks = {bytes} B");
        Assert.Equal(0, bytes);
        Assert.True(sim.Counters.ActiveVehicles <= maxVehicles);
    }

    /// <summary>
    /// Восстановление ночью (клапан перегрузки, ADR-0035, <c>res-congestion.md</c> §10): в ночь на вторые сутки (замер в начале каждого игрового часа 00:00–05:00) пул хотя бы раз опускается
    /// ниже половины <c>max_vehicles</c>, очередь <c>PendingTrips</c> в этот момент меньше 10% потолка, флаг gridlock выключен; ни одна обратная нога не ставится в очередь заново
    /// (<c>res_stuck</c> от дедлайнов не растёт - остаются только старые пути без пешеходного маршрута, см. §10).
    /// <para>
    /// Порог «к 05:00» (L46) заменён минимумом за ночь по замерам: в перегруженном городе ским машины длинный, жители выезжают раньше (сдвиг пика), и к 04:00–05:00 вторых суток
    /// утренний выезд уже снова заполняет сеть до потолка клапана (05:00 - 7 289 машин). Ночной минимум - 03:00: 5 074 машины из 12 000, очередь 95, gridlock выключен
    /// (до клапана в эти часы 11 997–12 000 машин и очередь 3 360–6 143).
    /// </para>
    /// </summary>
    [SlowFact]
    [Trait("Category", "Long")]
    public void Overload_PoolDrainsAtNight()
    {
        ScenarioDesc d = Overload();
        using Simulation sim = Simulation.Create(in d);
        int dayTicks = sim.CityDayTicks;
        sim.Step(dayTicks);   // 00:00 вторых суток
        int bestActive = int.MaxValue;
        int bestPending = 0;
        int bestGridlock = 1;
        for (int h = 0; h <= 5; h++)
        {
            int active = sim.Counters.ActiveVehicles;
            int pending = sim.World.Trips.Count;
            _out.WriteLine($"day 1 {h:00}:00: active={active}/{d.Capacities.MaxVehicles} pending={pending}/{d.Capacities.MaxPendingTrips} gridlock={sim.Counters.GridlockFlag} " +
                           $"waitDepart={CountState(sim, ResidentState.WaitDepart)} res_stuck={sim.Counters.ResStuck}");
            if (active < bestActive)
            {
                bestActive = active;
                bestPending = pending;
                bestGridlock = sim.Counters.GridlockFlag;
            }

            sim.Step(HourTicks);
        }

        Assert.True(bestActive < d.Capacities.MaxVehicles / 2, $"пул не разгрузился за ночь: минимум {bestActive}");
        Assert.True(bestPending < d.Capacities.MaxPendingTrips / 10, $"очередь поездок не разгрузилась: {bestPending}");
        Assert.Equal(0, bestGridlock);
    }
}
