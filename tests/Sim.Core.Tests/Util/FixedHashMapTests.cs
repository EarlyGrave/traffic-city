using System.Collections.Generic;
using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

public class FixedHashMapTests
{
    [Fact]
    public void SetGetRemove()
    {
        var m = new FixedHashMap(8);
        Assert.True(m.TrySet(5, 50));
        Assert.True(m.TrySet(5, 55)); // обновление
        Assert.Equal(1, m.Count);
        Assert.True(m.TryGet(5, out int v));
        Assert.Equal(55, v);
        Assert.False(m.TryGet(6, out _));
        Assert.True(m.Remove(5));
        Assert.False(m.Remove(5));
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void Full_RejectsNewKeys_ButAllowsUpdate()
    {
        var m = new FixedHashMap(2);
        Assert.True(m.TrySet(1, 1));
        Assert.True(m.TrySet(2, 2));
        Assert.False(m.TrySet(3, 3));
        Assert.True(m.TrySet(2, 22));
        m.TryGet(2, out int v);
        Assert.Equal(22, v);
    }

    [Fact]
    public void RandomOps_MatchDictionary()
    {
        var m = new FixedHashMap(64);
        var d = new Dictionary<long, int>();
        var rng = Pcg32.Create(11, 1);
        for (int i = 0; i < 20_000; i++)
        {
            long key = rng.NextBelow(100) * 1024L; // коллизии по младшим битам
            if (rng.NextBelow(3) == 0)
            {
                Assert.Equal(d.Remove(key), m.Remove(key));
            }
            else if (d.ContainsKey(key) || d.Count < 64)
            {
                int val = (int)rng.NextUInt();
                Assert.True(m.TrySet(key, val));
                d[key] = val;
            }

            Assert.Equal(d.Count, m.Count);
        }

        foreach (var kv in d)
        {
            Assert.True(m.TryGet(kv.Key, out int got));
            Assert.Equal(kv.Value, got);
        }
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var m = new FixedHashMap(4);
        m.TrySet(1, 1);
        m.Clear();
        Assert.Equal(0, m.Count);
        Assert.False(m.ContainsKey(1));
    }
}
