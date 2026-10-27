using System.Globalization;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Эталон хэша состояния для дефолта сценариев после 2.5 S2 (ADR-0018): <c>city.signal_mode = adaptive</c>, координация <c>legacy</c>
/// (офсеты не используются; 2.5 S3 хэш не изменила). Меняется осознанно, отдельным коммитом с объяснением. Хэш не зависит от числа потоков (ADR-0017).
/// Полная загрузка s10k (<c>hash --scenario s10k --warmup-until-full --ticks 400 --every 200</c>, 1/2/3 потока одинаково):
/// tick=6100 6faccbd453296b01, tick=6300 8c951ad4eb5e2099. Прежний эталон (fixed + legacy) - в <c>HotPathGoldenHashTests</c>.
/// ADR-0028 (отступы рукавов + <c>corner_radius</c>): все эталоны сняты заново командой <c>hash</c>; полная загрузка s10k: tick=6100 f40d86b642025876, tick=6300 dbcc8b6439b54604.
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
        string[] expected = { "9815c5eda0d5ae1f", "e1ad16aec5792b97", "6d6c2b51922be89f", "aaf89d47f893f7ea" };
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
        string[] expected = { "f23b332309ff00e2", "64b95924476bd2fe", "c2c7b5b096cd635f" };
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
        string[] expected = { "e77459c0a6da6d84", "548036fcdcab6eba", "55f00e554dda8dfa", "1d25c33018718013" };
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
        string[] expected = { "1cd194a5d58dbafe", "8bfd13a20423ee68", "4335775196b902f5" };
        for (int k = 0; k < expected.Length; k++)
        {
            sim.Step(200);
            Assert.Equal(expected[k], Hex(sim.ComputeStateHash()));
        }
    }
}
