using System;
using Sim.Core.Api;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Save;

/// <summary>T1, T2: round-trip мира (хэш и побайтно повторный сейв) и продолжение после загрузки (save-format.md §9).</summary>
public class WorldSaveRoundTripTests
{
    private readonly ITestOutputHelper _out;

    public WorldSaveRoundTripTests(ITestOutputHelper output) => _out = output;

    private static void AssertSameBytes(byte[] a, byte[] b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        if (!a.AsSpan().SequenceEqual(b))
        {
            int i = 0;
            while (a[i] == b[i])
            {
                i++;
            }

            Assert.Fail($"{what}: bytes differ at offset {i} of {a.Length}: {WorldSaveFixtures.Locate(a, i)}");
        }
    }

    [Fact]
    public void Small_RoundTrip_SameHashAndSameBytes()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        using Simulation a = Simulation.Create(in d);
        a.Step(900);
        ulong hash = a.ComputeStateHash();
        byte[] first = WorldSaveFixtures.SaveBytes(a);
        Assert.Equal(hash, a.ComputeStateHash());   // Save не меняет состояние
        _out.WriteLine($"small: {first.Length} bytes, vehicles {a.ActiveVehicleCount}");

        using Simulation b = WorldSaveFixtures.Load(first);
        Assert.Equal(hash, b.ComputeStateHash());
        Assert.Equal(a.Tick, b.Tick);
        byte[] second = WorldSaveFixtures.SaveBytes(b);
        AssertSameBytes(first, second, "Save(Load(Save(w))) != Save(w)");
    }

    [Fact]
    public void Small_ContinuationAfterLoad_MatchesUninterruptedRun()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        using Simulation a = Simulation.Create(in d);
        using Simulation src = Simulation.Create(in d);
        a.Step(700);
        src.Step(700);
        using Simulation b = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(src));
        for (int t = 0; t < 1500; t += 100)
        {
            a.Step(100);
            b.Step(100);
            Assert.True(a.ComputeStateHash() == b.ComputeStateHash(), $"hash diverged at tick {a.Tick}");
        }

        AssertSameBytes(WorldSaveFixtures.SaveBytes(a), WorldSaveFixtures.SaveBytes(b), "final saves");
    }
}
