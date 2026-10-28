using System.Globalization;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// ADR-0028: с <c>corner_radius_m = 0</c> (фиксированный отступ 7 м) геометрия и интергрин прежние - хэши состояния равны эталонам до ADR-0028
/// (<c>HotPathGoldenHashTests</c> fixed + legacy и <c>AdaptiveGoldenHashTests</c> adaptive на коммите 00d5a5f), при 1/2/3 потоках.
/// </summary>
public sealed class LegacyCornerGoldenTests
{
    private static string Hex(ulong h) => h.ToString("x16", CultureInfo.InvariantCulture);

    private static Simulation Create(string name, int threads, bool fixedLegacy)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(ScenarioLocator.Resolve(name, null)).Desc;
        d.City.CornerRadius = -1f;
        d.Transit.BayModel = BayModel.Curb;   // ADR-0029: дефолт загрузчика - виртуальный карман; эталоны до ADR-0028 сняты без него (bay_model = "curb" их воспроизводит)
        if (fixedLegacy)
        {
            d.City.SignalMode = CitySignalMode.Fixed;
            d.City.SignalCoordination = CitySignalCoordination.Legacy;
        }

        return Simulation.Create(d, new SimRuntimeOptions { WorkerThreads = threads });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S1k_FixedLegacy_CornerOff_MatchesPreAdr0028Golden(int threads)
    {
        using Simulation sim = Create("s1k", threads, fixedLegacy: true);
        string[] expected = { "86b053c16c320e1a", "577458b344bf0d86", "a6df8be544479ad8", "0e56ca8689b2fe28" };
        foreach (string e in expected)
        {
            sim.Step(1000);
            Assert.Equal(e, Hex(sim.ComputeStateHash()));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S10k_Adaptive_CornerOff_MatchesPreAdr0028Golden(int threads)
    {
        using Simulation sim = Create("s10k", threads, fixedLegacy: false);
        string[] expected = { "6cff314d255c0970", "180a98e3c6d7f20c", "d7748f09e319d72c" };
        foreach (string e in expected)
        {
            sim.Step(200);
            Assert.Equal(e, Hex(sim.ComputeStateHash()));
        }
    }
}
