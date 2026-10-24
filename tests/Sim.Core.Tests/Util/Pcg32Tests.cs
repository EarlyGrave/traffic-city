using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

public class Pcg32Tests
{
    // Референсная реализация pcg32 (pcg-random.org, pcg_basic.c), записанная независимо от Pcg32.
    private sealed class Reference
    {
        private ulong _state;
        private readonly ulong _inc;

        public Reference(ulong initState, ulong initSeq)
        {
            _state = 0;
            _inc = (initSeq << 1) | 1u;
            Next();
            _state += initState;
            Next();
        }

        public uint Next()
        {
            ulong oldstate = _state;
            _state = unchecked((oldstate * 6364136223846793005UL) + _inc);
            uint xorshifted = (uint)(((oldstate >> 18) ^ oldstate) >> 27);
            uint rot = (uint)(oldstate >> 59);
            return (xorshifted >> (int)rot) | (xorshifted << (int)((~rot + 1u) & 31));
        }
    }

    [Fact]
    public void Seed42Stream54_MatchesPcg32DemoValues()
    {
        uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
        var rng = Pcg32.Create(42, 54);
        foreach (uint e in expected)
        {
            Assert.Equal(e, rng.NextUInt());
        }
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(42UL, 54UL)]
    [InlineData(ulong.MaxValue, ulong.MaxValue)]
    [InlineData(20261024UL, 3UL)]
    public void MatchesReferenceImplementation(ulong seed, ulong stream)
    {
        var rng = Pcg32.Create(seed, stream);
        var reference = new Reference(seed, stream);
        for (int i = 0; i < 10_000; i++)
        {
            Assert.Equal(reference.Next(), rng.NextUInt());
        }
    }

    [Fact]
    public void IncIsAlwaysOdd()
    {
        Assert.Equal(1UL, Pcg32.Create(1, 0).Inc & 1UL);
        Assert.Equal(1UL, Pcg32.Create(1, ulong.MaxValue).Inc & 1UL);
    }

    [Fact]
    public void SameSeedAndStream_AreDeterministic()
    {
        var a = Pcg32.ForStream(12345, RngStream.Spawn);
        var b = Pcg32.ForStream(12345, RngStream.Spawn);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextUInt(), b.NextUInt());
        }

        Assert.Equal(a.State, b.State);
    }

    [Fact]
    public void DifferentStreams_ProduceDifferentSequences()
    {
        var streams = new[] { RngStream.CityGen, RngStream.Zones, RngStream.Spawn, RngStream.Behaviour, RngStream.Transit, RngStream.Routing };
        var firsts = new System.Collections.Generic.HashSet<ulong>();
        foreach (var s in streams)
        {
            var r = Pcg32.ForStream(777, s);
            ulong v = ((ulong)r.NextUInt() << 32) | r.NextUInt();
            Assert.True(firsts.Add(v), "подпотоки не должны совпадать");
        }
    }

    [Fact]
    public void ConsumingOneStream_DoesNotAffectAnother()
    {
        var spawnA = Pcg32.ForStream(9, RngStream.Spawn);
        var spawnB = Pcg32.ForStream(9, RngStream.Spawn);
        var other = Pcg32.ForStream(9, RngStream.Routing);
        for (int i = 0; i < 100; i++)
        {
            other.NextUInt();
        }

        Assert.Equal(spawnA.NextUInt(), spawnB.NextUInt());
    }

    [Fact]
    public void NextBelow_StaysInRange_AndCoversAllValues()
    {
        var rng = Pcg32.Create(1, 1);
        var seen = new bool[10];
        for (int i = 0; i < 5000; i++)
        {
            uint v = rng.NextBelow(10);
            Assert.True(v < 10);
            seen[v] = true;
        }

        Assert.All(seen, Assert.True);
        Assert.Equal(0u, rng.NextBelow(0));
        Assert.Equal(0u, rng.NextBelow(1));
    }

    [Fact]
    public void NextInt_RespectsBounds()
    {
        var rng = Pcg32.Create(2, 2);
        for (int i = 0; i < 5000; i++)
        {
            int v = rng.NextInt(-5, 5);
            Assert.InRange(v, -5, 4);
        }
    }

    [Fact]
    public void NextFloat01_InHalfOpenUnitInterval_WithExpectedMean()
    {
        var rng = Pcg32.Create(3, 3);
        double sum = 0;
        const int n = 100_000;
        for (int i = 0; i < n; i++)
        {
            float f = rng.NextFloat01();
            Assert.True(f >= 0f && f < 1f);
            sum += f;
        }

        Assert.InRange(sum / n, 0.49, 0.51);
    }

    [Fact]
    public void NextFloat01_UsesTop24Bits()
    {
        var a = Pcg32.Create(5, 5);
        var b = Pcg32.Create(5, 5);
        Assert.Equal((b.NextUInt() >> 8) * (1.0f / 16777216.0f), a.NextFloat01());
    }

    [Fact]
    public void NextRange_StaysInRange()
    {
        var rng = Pcg32.Create(4, 4);
        for (int i = 0; i < 5000; i++)
        {
            float v = rng.NextRange(-2f, 3f);
            Assert.True(v >= -2f && v < 3f);
        }
    }
}
