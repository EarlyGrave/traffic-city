using System;
using System.Diagnostics;
using Sim.Core.Api;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Save;

/// <summary>
/// T7 на уровне мира: любой испорченный вход даёт код ошибки, без исключений и падений. Бит-флипы без пересчёта хэшей ловит контрольная сумма;
/// флипы с пересчётом хэшей бьют по проверкам состояния (save-format.md §6.1): ответ - код либо <c>Ok</c> и тогда <c>Step</c> безопасен.
/// </summary>
public class WorldSaveFuzzTests
{
    private readonly ITestOutputHelper _out;

    public WorldSaveFuzzTests(ITestOutputHelper output) => _out = output;

    private static byte[] SmallSave()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Capacities.MaxVehicles = 128;
        using Simulation sim = Simulation.Create(in d);
        sim.Step(700);
        return WorldSaveFixtures.SaveBytes(sim);
    }

    [Fact]
    public void Truncation_AtHeaderAndAtSampledPoints_IsRejected()
    {
        byte[] file = SmallSave();
        int cuts = 0;
        for (int len = 0; len < 160; len++)
        {
            Assert.NotEqual(LoadStatus.Ok, Simulation.Load(file.AsSpan(0, len), default, default, out Simulation? s, out _));
            Assert.Null(s);
            cuts++;
        }

        var rng = new Random(11);
        for (int k = 0; k < 1000; k++)
        {
            int len = rng.Next(160, file.Length);
            Assert.NotEqual(LoadStatus.Ok, Simulation.Load(file.AsSpan(0, len), default, default, out Simulation? s, out _));
            Assert.Null(s);
            cuts++;
        }

        _out.WriteLine($"{cuts} truncations rejected");
    }

    [Fact]
    public void RandomBitFlips_AreAlwaysRejected()
    {
        byte[] file = SmallSave();
        var rng = new Random(12);
        for (int k = 0; k < 3000; k++)
        {
            byte[] f = (byte[])file.Clone();
            int bit = rng.Next(0, 8 * (f.Length - 0));
            f[bit >> 3] ^= (byte)(1 << (bit & 7));
            LoadStatus st = Simulation.Load(f, default, default, out Simulation? s, out _);
            Assert.NotEqual(LoadStatus.Ok, st);
            Assert.Null(s);
        }
    }

    [Fact]
    public void BitFlips_WithRecomputedChecksums_GiveCodeOrWorkingWorld()
    {
        byte[] file = SmallSave();
        var rng = new Random(13);
        int ok = 0;
        int rejected = 0;
        var codes = new System.Collections.Generic.SortedDictionary<LoadStatus, int>();
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 2000; k++)
        {
            byte[] f = (byte[])file.Clone();
            int flips = 1 + (k % 3);
            for (int j = 0; j < flips; j++)
            {
                int bit = rng.Next(8 * 64, 8 * (f.Length - 16));
                f[bit >> 3] ^= (byte)(1 << (bit & 7));
            }

            ContainerTestData.Rehash(f);
            LoadStatus st = Simulation.Load(f, default, default, out Simulation? s, out LoadReport rep);
            if (st == LoadStatus.Ok)
            {
                ok++;
                using (s)
                {
                    s!.Step(40);   // состояние принято: исполнение безопасно (проверка - отсутствие исключений)
                }
            }
            else
            {
                rejected++;
                Assert.Null(s);
                codes[st] = (codes.TryGetValue(st, out int n) ? n : 0) + 1;
                Assert.Equal(st, rep.Status);
            }
        }

        _out.WriteLine($"ok {ok}, rejected {rejected} in {sw.ElapsedMilliseconds} ms: " + string.Join(", ", codes));
        Assert.True(rejected > 0);
    }

    [Fact]
    public void RandomBytes_AreNotASave()
    {
        var rng = new Random(14);
        byte[] junk = new byte[4096];
        for (int k = 0; k < 200; k++)
        {
            rng.NextBytes(junk);
            Assert.NotEqual(LoadStatus.Ok, Simulation.Load(junk.AsSpan(0, rng.Next(0, junk.Length)), default, default, out Simulation? s, out _));
            Assert.Null(s);
        }
    }
}
