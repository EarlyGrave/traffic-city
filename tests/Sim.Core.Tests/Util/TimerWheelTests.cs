#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

/// <summary>Колесо таймеров (pedestrians.md §4.3): каждый ключ выдаётся ровно в свой тик, внутри тика - по возрастанию ключа, далёкие события через переполнение.</summary>
public sealed class TimerWheelTests
{
    [Fact]
    public void SameTickKeys_AreCollectedInAscendingOrder()
    {
        var w = new TimerWheel(64);
        foreach (int k in new[] { 17, 3, 40, 9, 22, 1 })
        {
            w.Schedule(k, 10, 0);
        }

        var buf = new int[64];
        for (long t = 1; t < 10; t++)
        {
            Assert.Equal(0, w.Collect(t, buf));
        }

        int n = w.Collect(10, buf);
        Assert.Equal(new[] { 1, 3, 9, 17, 22, 40 }, buf[..n]);
        Assert.Equal(0, w.Count);
    }

    [Fact]
    public void RandomSchedule_MatchesNaiveModel_IncludingOverflowAndRescheduling()
    {
        const int keys = 300;
        const long ticks = 3 * TimerWheel.W + 500;
        var rng = Pcg32.Create(11, 2);
        var w = new TimerWheel(keys);
        var due = new long[keys];
        Array.Fill(due, -1L);
        var buf = new int[keys];
        for (int k = 0; k < keys; k++)
        {
            due[k] = 1 + rng.NextBelow(2 * TimerWheel.W + 300);
            w.Schedule(k, due[k], 0);
        }

        long fired = 0;
        for (long t = 1; t <= ticks; t++)
        {
            int n = w.Collect(t, buf);
            int prev = -1;
            for (int i = 0; i < n; i++)
            {
                int k = buf[i];
                Assert.True(k > prev, "ключи тика идут по возрастанию");
                prev = k;
                Assert.Equal(t, due[k]);
                fired++;
                // Перепланируем: ближний, на границе окна и дальний.
                long delta = rng.NextBelow(3) switch
                {
                    0 => 1 + rng.NextBelow(50),
                    1 => TimerWheel.W - 1 + rng.NextBelow(3),
                    _ => 1 + rng.NextBelow(3 * TimerWheel.W),
                };
                due[k] = t + delta;
                w.Schedule(k, due[k], t);
            }

            for (int k = 0; k < keys; k++)
            {
                Assert.True(due[k] > t, $"ключ {k} пропущен: due {due[k]} при t {t}");
            }
        }

        Assert.True(fired > keys, $"fired {fired}");
        Assert.Equal(keys, w.Count);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var w = new TimerWheel(8);
        w.Schedule(2, 5, 0);
        w.Schedule(3, 100_000, 0);
        Assert.Equal(2, w.Count);
        w.Clear();
        Assert.Equal(0, w.Count);
        Assert.False(w.Contains(2));
        var buf = new int[8];
        for (long t = 1; t < 6; t++)
        {
            Assert.Equal(0, w.Collect(t, buf));
        }

        w.Schedule(2, 7, 5);
        Assert.True(w.Contains(2));
    }
}
