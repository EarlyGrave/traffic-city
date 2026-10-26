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
    public void Cancel_RemovesFromBucketAndOverflow_AndKeyCanBeRescheduled()
    {
        var w = new TimerWheel(32);
        // 1, 2, 3 - одна корзина (в порядке постановки 1 внизу списка), 4, 5, 6 - переполнение (далеко).
        foreach (int k in new[] { 1, 2, 3 })
        {
            w.Schedule(k, 10, 0);
        }

        foreach (int k in new[] { 4, 5, 6 })
        {
            w.Schedule(k, 9000, 0);
        }

        Assert.Equal(6, w.Count);
        w.Cancel(2);   // середина списка корзины
        w.Cancel(3);   // голова списка корзины
        w.Cancel(6);   // голова переполнения
        w.Cancel(4);   // конец списка переполнения
        w.Cancel(30);  // ключа нет - без последствий
        Assert.Equal(2, w.Count);
        Assert.False(w.Contains(2) || w.Contains(3) || w.Contains(4) || w.Contains(6));
        w.Schedule(3, 12, 0);   // снятый ключ ставится заново
        var buf = new int[32];
        int total = 0;
        for (long t = 1; t <= 9000; t++)
        {
            int n = w.Collect(t, buf);
            if (n == 0)
            {
                continue;
            }

            total += n;
            if (t == 10)
            {
                Assert.Equal(new[] { 1 }, buf[..n]);
            }
            else if (t == 12)
            {
                Assert.Equal(new[] { 3 }, buf[..n]);
            }
            else
            {
                Assert.Equal(9000, t);
                Assert.Equal(new[] { 5 }, buf[..n]);
            }
        }

        Assert.Equal(3, total);
        Assert.Equal(0, w.Count);
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
