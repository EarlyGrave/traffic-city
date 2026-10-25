using System;
using System.IO;

namespace Sim.Headless.Tests;

/// <summary>Конверт <c>.tcsave</c> (save-format.md §7): заголовок + deflate над сырым контейнером.</summary>
public sealed class SaveFileTests
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
    [InlineData(SaveFile.CodecDeflate)]
    [InlineData(SaveFile.CodecNone)]
    public void PackUnpack_RoundTrips_AndKeepsContainerHeaderReadable(int codec)
    {
        byte[] raw = Container();
        byte[] file = SaveFile.Pack(raw, codec);
        Assert.True(SaveFile.IsEnvelope(file));
        Assert.True(file.AsSpan(32, 64).SequenceEqual(raw.AsSpan(0, 64)));   // список сейвов читает заголовок без распаковки
        if (codec == SaveFile.CodecDeflate)
        {
            Assert.True(file.Length < raw.Length / 2);
        }

        Assert.True(SaveFile.TryUnpack(file, out byte[] back, out string err), err);
        Assert.True(raw.AsSpan().SequenceEqual(back));
    }

    [Fact]
    public void RawContainer_IsNotAnEnvelope()
    {
        Assert.False(SaveFile.IsEnvelope(Container()));
        Assert.False(SaveFile.TryUnpack(Container(), out _, out string err));
        Assert.Contains("magic", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Corruption_IsRejected_NotThrown()
    {
        byte[] file = SaveFile.Pack(Container());
        foreach (int pos in new[] { 9, 11, 17, 25, 40, 100, file.Length - 5 })
        {
            byte[] bad = (byte[])file.Clone();
            bad[pos] ^= 0x5A;
            Assert.False(SaveFile.TryUnpack(bad, out _, out string err), $"byte {pos} flip accepted");
            Assert.NotEmpty(err);
        }

        Assert.False(SaveFile.TryUnpack(file.AsSpan(0, file.Length - 20), out _, out _));
        Assert.False(SaveFile.TryUnpack(file.AsSpan(0, 50), out _, out _));
    }

    [Fact]
    public void ReadContainer_AcceptsBothForms()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tc-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] raw = Container();
            File.WriteAllBytes(Path.Combine(dir, "a.tcsave"), SaveFile.Pack(raw));
            File.WriteAllBytes(Path.Combine(dir, "b.bin"), raw);
            Assert.True(raw.AsSpan().SequenceEqual(SaveFile.ReadContainer(Path.Combine(dir, "a.tcsave"))));
            Assert.True(raw.AsSpan().SequenceEqual(SaveFile.ReadContainer(Path.Combine(dir, "b.bin"))));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
