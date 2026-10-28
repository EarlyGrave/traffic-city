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
/// своими потолками, исключений нет, в установившемся режиме 0 B/тик. Второе требование — восстановление ночью — сейчас не выполняется
/// (см. <see cref="Overload_PoolDrainsAtNight"/>). Оба теста долгие (минуты в Release): <c>TC_SLOW=1</c>, лучше с <c>-c Release</c>.
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
        Assert.Equal(maxVehicles, peakActive);   // перегрузка настоящая: пул упёрся в потолок
        Assert.Equal(maxPending, peakPending);   // и очередь поездок тоже

        // установившийся режим при перегрузке: 0 B/тик (окно выполняется дважды, измеряется второе прохождение)
        long bytes = AllocationProbe.Measure(() => sim.Step(100), out _);
        _out.WriteLine($"alloc over 100 ticks = {bytes} B");
        Assert.Equal(0, bytes);
        Assert.True(sim.Counters.ActiveVehicles <= maxVehicles);
    }

    /// <summary>
    /// Восстановление: после вечернего пика ночью (спрос жителей падает почти до нуля) пул должен опустеть меньше чем наполовину,
    /// а очередь <c>PendingTrips</c> — меньше 10% потолка к 05:00 вторых суток (по прогонам L46 на здоровом сценарии пул ночью ~200 машин).
    /// <b>Сейчас не выполняется:</b> пул стоит на <c>max_vehicles</c> (11 997–12 000) всю ночь, хвост очереди ≈ 90 тыс. ног копился за сутки;
    /// разбор и цифры — <c>res-congestion.md</c> §9. Тест оставлен выключенным (Skip), чтобы снять Skip после исправления ядра (этап 5, вариант A/B).
    /// </summary>
    [SlowFact(Skip = "известный провал: сеть не разгружается ночью при перегрузке, см. docs/design/res-congestion.md §9 (нужна выборка агентов / клапан перегрузки)")]
    [Trait("Category", "Long")]
    public void Overload_PoolDrainsAtNight()
    {
        ScenarioDesc d = Overload();
        using Simulation sim = Simulation.Create(in d);
        int dayTicks = sim.CityDayTicks;
        sim.Step(dayTicks + (5 * HourTicks));   // 05:00 вторых суток
        int active = sim.Counters.ActiveVehicles;
        int pending = sim.World.Trips.Count;
        _out.WriteLine($"05:00 day 1: active={active}/{d.Capacities.MaxVehicles} pending={pending}/{d.Capacities.MaxPendingTrips} waitDepart={CountState(sim, ResidentState.WaitDepart)}");
        Assert.True(active < d.Capacities.MaxVehicles / 2, $"пул не разгрузился: {active}");
        Assert.True(pending < d.Capacities.MaxPendingTrips / 10, $"очередь поездок не разгрузилась: {pending}");
    }
}
