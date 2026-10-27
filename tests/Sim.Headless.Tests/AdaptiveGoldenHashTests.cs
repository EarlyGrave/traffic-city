using System.Globalization;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Эталон хэша состояния для дефолта сценариев после 2.5 S2 (ADR-0018): <c>city.signal_mode = adaptive</c>, координация <c>legacy</c>
/// (офсеты не используются; 2.5 S3 хэш не изменила). Меняется осознанно, отдельным коммитом с объяснением. Хэш не зависит от числа потоков (ADR-0017).
/// Полная загрузка s10k (<c>hash --scenario s10k --warmup-until-full --ticks 400 --every 200</c>, 1/2/3 потока одинаково):
/// tick=6100 6faccbd453296b01, tick=6300 8c951ad4eb5e2099. Прежний эталон (fixed + legacy) - в <c>HotPathGoldenHashTests</c>.
/// ADR-0028 (отступы рукавов + <c>corner_radius</c>): все эталоны сняты заново командой <c>hash</c>; с интергрином по длине коробки - полная загрузка s10k: tick=6100 37bf4332bf3354f3, tick=6300 25c1e949df9b381f.
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
        string[] expected = { "9a7ecb80908c384d", "68c058a508d7568c", "62f6271181658ed5", "5cac9bedcf5120e7" };
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
        string[] expected = { "a1d2e0ea765acdae", "3be8f865185d9615", "67764242726f2f17" };
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

    // 2.5 S3 + удержание автобусов (hold_alpha 0.9): adaptive + alternate (опция, не дефолт); хэши сняты после слияния S3 с удержанием. Хэши снимает команда hash --signal-mode adaptive --signal-coordination alternate; при 1/2/3 потоках одинаковы.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S1k_AlternateStateHash_MatchesGolden(int threads)
    {
        using Simulation sim = CreateAlternate("s1k", threads);
        string[] expected = { "89ef5134bd77addd", "9bcb38ad7eea4a9b", "c9a93d7bb1ae916c", "593182f61439a215" };
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
        string[] expected = { "16d3d7565a4956a9", "3819bf0d5d3f1342", "e3340ee972ff71dd" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(200);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }
    }
}
