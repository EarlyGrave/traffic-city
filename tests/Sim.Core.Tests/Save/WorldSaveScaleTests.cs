using System;
using Sim.Core.Api;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Save;

/// <summary>Тест, который по умолчанию пропускается (минуты в Debug): запускать с TC_SLOW=1 (верификатор после мержа волны).</summary>
public sealed class SlowFactAttribute : FactAttribute
{
    public SlowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TC_SLOW") != "1")
        {
            Skip = "slow: set TC_SLOW=1";
        }
    }
}

/// <summary>T1, T2 на s1k и s10k: round-trip и продолжение (save-format.md §9).</summary>
public class WorldSaveScaleTests
{
    private readonly ITestOutputHelper _out;

    public WorldSaveScaleTests(ITestOutputHelper output) => _out = output;

    private void RoundTripAndContinue(in ScenarioDesc d, int warm, int follow, int every)
    {
        using Simulation a = Simulation.Create(in d);
        using Simulation src = Simulation.Create(in d);
        a.Step(warm);
        src.Step(warm);
        byte[] first = WorldSaveFixtures.SaveBytes(src);
        _out.WriteLine($"{first.Length} bytes at tick {src.Tick}, {src.ActiveVehicleCount} vehicles");
        using Simulation b = WorldSaveFixtures.Load(first, threads: 2);
        Assert.Equal(src.ComputeStateHash(), b.ComputeStateHash());
        byte[] second = WorldSaveFixtures.SaveBytes(b);
        Assert.True(first.AsSpan().SequenceEqual(second), "Save(Load(Save)) != Save");

        for (int t = 0; t < follow; t += every)
        {
            a.Step(every);
            b.Step(every);
            Assert.True(a.ComputeStateHash() == b.ComputeStateHash(), $"diverged at tick {a.Tick}");
        }

        Assert.True(WorldSaveFixtures.SaveBytes(a).AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(b)), "final saves differ");
    }

    [Fact]
    public void S1k_RoundTripAndContinuation()
    {
        ScenarioDesc d = WorldSaveFixtures.S1k();
        RoundTripAndContinue(in d, 1500, 600, 200);
    }

    [SlowFact]
    public void S1k_Full_2400Ticks_Then2000()
    {
        ScenarioDesc d = WorldSaveFixtures.S1k();
        RoundTripAndContinue(in d, 2400, 2000, 200);
    }

    [SlowFact]
    public void S10k_AfterRamp_RoundTripAndContinuation()
    {
        ScenarioDesc d = WorldSaveFixtures.S10k();
        RoundTripAndContinue(in d, 6100, 400, 200);
    }
}
