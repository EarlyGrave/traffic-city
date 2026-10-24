using System.Text;
using Sim.Core.Util;

namespace Sim.Core.Tests.Util;

public class Fnv1a64Tests
{
    private static ulong HashAscii(string s)
    {
        var h = Fnv1a64.Create();
        h.Add(Encoding.ASCII.GetBytes(s));
        return h.Value;
    }

    [Fact]
    public void Empty_IsOffsetBasis() => Assert.Equal(0xcbf29ce484222325UL, Fnv1a64.Create().Value);

    [Theory]
    [InlineData("a", 0xaf63dc4c8601ec8cUL)]
    [InlineData("foobar", 0x85944171f73967e8UL)]
    public void KnownVectors(string input, ulong expected) => Assert.Equal(expected, HashAscii(input));

    [Fact]
    public void Int_IsLittleEndianBytes()
    {
        var a = Fnv1a64.Create();
        a.Add(0x04030201);
        var b = Fnv1a64.Create();
        b.Add(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(b.Value, a.Value);
    }

    [Fact]
    public void Long_IsLittleEndianBytes()
    {
        var a = Fnv1a64.Create();
        a.Add(0x0807060504030201L);
        var b = Fnv1a64.Create();
        b.Add(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Equal(b.Value, a.Value);
    }

    [Fact]
    public void UnsignedOverloads_MatchSigned()
    {
        var a = Fnv1a64.Create();
        a.Add(0xFFFFFFFEu);
        a.Add(0xFFFFFFFFFFFFFFFEUL);
        var b = Fnv1a64.Create();
        b.Add(-2);
        b.Add(-2L);
        Assert.Equal(b.Value, a.Value);
    }

    [Fact]
    public void Float_UsesBitPattern()
    {
        var a = Fnv1a64.Create();
        a.Add(1.0f);
        var b = Fnv1a64.Create();
        b.Add(0x3F800000);
        Assert.Equal(b.Value, a.Value);

        var z = Fnv1a64.Create();
        z.Add(0.0f);
        var nz = Fnv1a64.Create();
        nz.Add(-0.0f);
        Assert.NotEqual(z.Value, nz.Value);
    }
}
