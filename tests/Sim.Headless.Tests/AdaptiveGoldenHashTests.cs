using System.Globalization;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Эталон хэша состояния для дефолта сценариев после 2.5 S2 (ADR-0018): <c>city.signal_mode = adaptive</c>, координация <c>legacy</c>
/// (офсеты не используются). Меняется осознанно, отдельным коммитом с объяснением. Хэш не зависит от числа потоков (ADR-0017).
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
        string[] expected = { "764c5b6de750ae15", "34a5c23411d36b7e", "aeca0135024eff72", "ae9bce579a59f785" };
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
        string[] expected = { "41e484b2dfde848b", "342c2f1b4f50889a", "a46b6ed830102e37" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(200);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }
    }
}
