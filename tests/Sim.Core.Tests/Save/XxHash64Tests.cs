using System;
using Sim.Core.Util;
using Reference = System.IO.Hashing.XxHash64;

namespace Sim.Core.Tests.Save;

public class XxHash64Tests
{
    [Fact]
    public void KnownVectors()
    {
        Assert.Equal(0xEF46DB3751D8E999UL, XxHash64.Hash(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0xD24EC4F1A98C6E5BUL, XxHash64.Hash("a"u8));
        Assert.Equal(0x44BC2CF5AD770999UL, XxHash64.Hash("abc"u8));
    }

    [Fact]
    public void MatchesReference_AllLengths0To4097()
    {
        var rng = new Random(20261025);
        byte[] data = new byte[4097];
        rng.NextBytes(data);
        for (int len = 0; len <= data.Length; len++)
        {
            ReadOnlySpan<byte> s = data.AsSpan(0, len);
            Assert.Equal(Reference.HashToUInt64(s), XxHash64.Hash(s));
        }
    }

    [Fact]
    public void MatchesReference_WithSeed_AndUnalignedSlices()
    {
        var rng = new Random(7);
        byte[] data = new byte[600];
        rng.NextBytes(data);
        foreach (long seed in new long[] { 0, 1, 0x123456789ABCDEF, -1, long.MinValue })
        {
            for (int start = 0; start < 9; start++)
            {
                for (int len = 0; len < 130; len += 7)
                {
                    ReadOnlySpan<byte> s = data.AsSpan(start, len);
                    Assert.Equal(Reference.HashToUInt64(s, seed), XxHash64.Hash(s, unchecked((ulong)seed)));
                }
            }
        }
    }

    [Fact]
    public void Streaming_AnySplit_EqualsOneShot()
    {
        var rng = new Random(99);
        byte[] data = new byte[5000];
        rng.NextBytes(data);
        for (int round = 0; round < 200; round++)
        {
            int len = rng.Next(0, data.Length + 1);
            var h = XxHash64.Create();
            int pos = 0;
            while (pos < len)
            {
                int take = Math.Min(len - pos, rng.Next(0, 70));
                h.Append(data.AsSpan(pos, take));
                pos += take;
            }

            Assert.Equal(Reference.HashToUInt64(data.AsSpan(0, len)), h.Value);
        }
    }

    [Fact]
    public void Value_DoesNotChangeState()
    {
        var h = XxHash64.Create();
        h.Append("hello "u8);
        _ = h.Value;
        h.Append("world"u8);
        Assert.Equal(Reference.HashToUInt64("hello world"u8), h.Value);
    }
}
