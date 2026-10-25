using System.Globalization;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Эталон хэша состояния для дефолта сценариев после 2.5 S2 (ADR-0018): <c>city.signal_mode = adaptive</c>, координация <c>legacy</c>
/// (офсеты не используются; 2.5 S3 хэш не изменила). Меняется осознанно, отдельным коммитом с объяснением. Хэш не зависит от числа потоков (ADR-0017).
/// Полная загрузка s10k (<c>hash --scenario s10k --warmup-until-full --ticks 400 --every 200</c>, 1/2/3 потока одинаково):
/// tick=6100 6faccbd453296b01, tick=6300 8c951ad4eb5e2099. Прежний эталон (fixed + legacy) - в <c>HotPathGoldenHashTests</c>.
/// </summary>
public sealed class AdaptiveGoldenHashTests
{
    private static string Hex(ulong h) => h.ToString("x16", CultureInfo.InvariantCulture);

    private static Simulation Create(string name, int threads)
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null));
        Assert.Equal(CitySignalMode.Adaptive, sc.Desc.City.SignalMode);
        return Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = threads });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S1k_AdaptiveStateHash_MatchesGolden(int threads)
    {
        using Simulation sim = Create("s1k", threads);
        string[] expected = { "9a067da851a95476", "91d2332112829e50", "71e8589efe51bbe4", "87ce8eeef92ed3ea" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(1000);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S10k_AdaptiveStateHash_MatchesGolden(int threads)
    {
        using Simulation sim = Create("s10k", threads);
        string[] expected = { "86c5fa43f14764c3", "730ebad8907bca72", "ffdf7aea9ac56e76" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(200);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }
    }

    private static Simulation CreateAlternate(string name, int threads)
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null));
        ScenarioDesc d = sc.Desc;
        d.City.SignalCoordination = CitySignalCoordination.Alternate;
        return Simulation.Create(d, new SimRuntimeOptions { WorkerThreads = threads });
    }

    // 2.5 S3: adaptive + alternate (опция, не дефолт). Хэши снимает команда hash --signal-mode adaptive --signal-coordination alternate; при 1/2/3 потоках одинаковы.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S1k_AlternateStateHash_MatchesGolden(int threads)
    {
        using Simulation sim = CreateAlternate("s1k", threads);
        string[] expected = { "fc9579b280188357", "4e12777c8bb6c4ce", "33c135b4de3330ab", "287e5428a032d10d" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(1000);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S10k_AlternateStateHash_MatchesGolden(int threads)
    {
        using Simulation sim = CreateAlternate("s10k", threads);
        string[] expected = { "1b7d7430e152b2bf", "406f3bb6f4ad0663", "1e11e19b970919ff" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(200);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }
    }
}
