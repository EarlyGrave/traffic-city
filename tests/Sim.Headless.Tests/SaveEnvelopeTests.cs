using System;
using System.IO;
using Sim.Host;

namespace Sim.Headless.Tests;

/// <summary>Конверт <c>.tcsave</c> (save-format.md §7): заголовок + deflate над сырым контейнером.</summary>
public sealed class SaveEnvelopeTests
{
    private static byte[] Container(int length = 5000)
    {
        byte[] c = new byte[length];
        "TCSAVE\r\n"u8.CopyTo(c);
        for (int i = 64; i < c.Length; i++)
        {
            c[i] = (byte)((i / 7) % 13);   // сжимаемо
        }

        return c;
    }

    [Theory]
    [InlineData(SaveEnvelope.CodecDeflate)]
    [InlineData(SaveEnvelope.CodecNone)]
    public void PackUnpack_RoundTrips_AndKeepsContainerHeaderReadable(int codec)
    {
        byte[] raw = Container();
        byte[] file = SaveEnvelope.Pack(raw, codec);
        Assert.True(SaveEnvelope.IsEnvelope(file));
        Assert.True(file.AsSpan(32, 64).SequenceEqual(raw.AsSpan(0, 64)));   // список сейвов читает заголовок без распаковки
        if (codec == SaveEnvelope.CodecDeflate)
        {
            Assert.True(file.Length < raw.Length / 2);
        }

        Assert.True(SaveEnvelope.TryUnpack(file, out byte[] back, out string err), err);
        Assert.True(raw.AsSpan().SequenceEqual(back));
    }

    [Fact]
    public void RawContainer_IsNotAnEnvelope()
    {
        Assert.False(SaveEnvelope.IsEnvelope(Container()));
        Assert.False(SaveEnvelope.TryUnpack(Container(), out _, out string err));
        Assert.Contains("magic", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Corruption_IsRejected_NotThrown()
    {
        byte[] file = SaveEnvelope.Pack(Container());
        foreach (int pos in new[] { 9, 11, 17, 25, 40, 100, file.Length - 5 })
        {
            byte[] bad = (byte[])file.Clone();
            bad[pos] ^= 0x5A;
            Assert.False(SaveEnvelope.TryUnpack(bad, out _, out string err), $"byte {pos} flip accepted");
            Assert.NotEmpty(err);
        }

        Assert.False(SaveEnvelope.TryUnpack(file.AsSpan(0, file.Length - 20), out _, out _));
        Assert.False(SaveEnvelope.TryUnpack(file.AsSpan(0, 50), out _, out _));
    }

    [Fact]
    public void ReadContainer_AcceptsBothForms()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tc-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] raw = Container();
            File.WriteAllBytes(Path.Combine(dir, "a.tcsave"), SaveEnvelope.Pack(raw));
            File.WriteAllBytes(Path.Combine(dir, "b.bin"), raw);
            Assert.True(raw.AsSpan().SequenceEqual(SaveEnvelope.ReadContainer(Path.Combine(dir, "a.tcsave"))));
            Assert.True(raw.AsSpan().SequenceEqual(SaveEnvelope.ReadContainer(Path.Combine(dir, "b.bin"))));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
