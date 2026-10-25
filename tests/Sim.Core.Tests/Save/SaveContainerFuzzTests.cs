using System;
using System.Buffers.Binary;
using Sim.Core.Api;
using Sim.Core.Save;

namespace Sim.Core.Tests.Save;

/// <summary>
/// Fuzz контейнера (save-format.md §9 T7 на уровне контейнера): любая порча даёт код ошибки, не <c>Ok</c> и не исключение.
/// Seed фиксирован; всё укладывается в секунды.
/// </summary>
public class SaveContainerFuzzTests
{
    private static readonly uint[] _known = { ContainerTestData.Aaaa, ContainerTestData.Bbbb };

    private static byte[] Sample() => ContainerTestData.Write(ContainerTestData.Sample(11, bigCount: 300));

    [Fact]
    public void EveryTruncation_IsRejected()
    {
        byte[] file = Sample();
        for (int len = 0; len < file.Length; len++)
        {
            LoadStatus s = SaveReader.Read(file.AsSpan(0, len), _known, out SaveTables? t, out LoadReport r);
            Assert.NotEqual(LoadStatus.Ok, s);
            Assert.Null(t);
            Assert.Equal(s, r.Status);
        }
    }

    [Fact]
    public void EveryTruncation_ViaStream_IsRejected()
    {
        byte[] file = Sample();
        for (int len = 0; len < file.Length; len += 13)
        {
            LoadStatus s = SaveReader.Read(new TruncatedSource(file, len), _known, out SaveTables? t, out _);
            Assert.NotEqual(LoadStatus.Ok, s);
            Assert.Null(t);
        }
    }

    [Fact]
    public void EverySingleBitFlip_InHeaderTocAndFooter_IsRejected()
    {
        byte[] file = Sample();
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(36));
        int tocEnd = 64 + (n * 32);
        for (int pos = 0; pos < tocEnd; pos++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] f = (byte[])file.Clone();
                f[pos] ^= (byte)(1 << bit);
                Assert.NotEqual(LoadStatus.Ok, SaveReader.Read(f, _known, out _, out _));
            }
        }

        for (int pos = file.Length - 16; pos < file.Length; pos++)
        {
            byte[] f = (byte[])file.Clone();
            f[pos] ^= 0x10;
            Assert.NotEqual(LoadStatus.Ok, SaveReader.Read(f, _known, out _, out _));
        }
    }

    [Fact]
    public void RandomBitFlips_InBlocks_GiveChecksumMismatch_OrAnyErrorElsewhere()
    {
        byte[] file = Sample();
        (long Offset, long Length)[] toc = ContainerTestData.TocRanges(file);
        var rng = new Random(20261025);
        for (int i = 0; i < 5000; i++)
        {
            byte[] f = (byte[])file.Clone();
            int pos = rng.Next(f.Length);
            f[pos] ^= (byte)(1 << rng.Next(8));
            LoadStatus s = SaveReader.Read(f, _known, out SaveTables? t, out _);
            Assert.NotEqual(LoadStatus.Ok, s);
            Assert.Null(t);

            // Порча нагрузки или хэша блока всегда ловится именно контрольной суммой.
            foreach ((long off, long len) in toc)
            {
                if (pos >= off && pos < off + len + 8)
                {
                    Assert.Equal(LoadStatus.ChecksumMismatch, s);
                }
            }
        }
    }

    [Fact]
    public void RandomMutations_WithRecomputedChecksums_NeverThrow_AndOkMeansConsistent()
    {
        byte[] file = Sample();
        var rng = new Random(12345);
        int ok = 0;
        for (int i = 0; i < 4000; i++)
        {
            byte[] f = (byte[])file.Clone();
            int edits = 1 + rng.Next(3);
            for (int e = 0; e < edits; e++)
            {
                int pos = rng.Next(f.Length - 16); // подвал пересчитывается
                switch (rng.Next(4))
                {
                    case 0:
                        f[pos] ^= (byte)(1 << rng.Next(8));
                        break;
                    case 1:
                        f[pos] = (byte)rng.Next(256);
                        break;
                    case 2:
                        f[pos] = 0xFF;
                        break;
                    default:
                        if (pos + 8 <= f.Length - 16)
                        {
                            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(pos), rng.Next(2) == 0 ? ulong.MaxValue : (ulong)rng.NextInt64());
                        }

                        break;
                }
            }

            ContainerTestData.Rehash(f);
            LoadStatus s = SaveReader.Read(f, _known, out SaveTables? t, out LoadReport r);
            Assert.Equal(s, r.Status);
            if (s == LoadStatus.Ok)
            {
                ok++;
                Assert.NotNull(t);
                Touch(t!); // все столбцы читаются без выхода за границы
            }
            else
            {
                Assert.Null(t);
            }
        }

        // Мутации байта, не влияющего на смысл (значение столбца, tick и т.п.), проходят: проверяем, что прогон не пустой.
        Assert.InRange(ok, 1, 3999);
    }

    [Fact]
    public void RandomBytes_AreNeverAccepted()
    {
        var rng = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            byte[] junk = new byte[rng.Next(0, 400)];
            rng.NextBytes(junk);
            if (i % 2 == 0 && junk.Length >= 64)
            {
                SaveFormat.Magic.CopyTo(junk); // правдоподобное начало
            }

            Assert.NotEqual(LoadStatus.Ok, SaveReader.Read(junk, null, out _, out _));
        }
    }

    private static void Touch(SaveTables t)
    {
        long sum = 0;
        for (int b = 0; b < t.BlockCount; b++)
        {
            SaveBlockDesc d = t.GetBlock(b);
            if (SaveFormat.IsBlockOpaque(d.Flags))
            {
                foreach (byte x in t.OpaquePayload(b).Span)
                {
                    sum += x;
                }

                continue;
            }

            for (int c = 0; c < d.ColumnCount; c++)
            {
                SaveColumnDesc col = t.GetColumn(b, c);
                Assert.True(t.TryGetColumnBytes(b, col.Id, out ReadOnlySpan<byte> bytes));
                Assert.Equal(col.Count * col.ElemSize, bytes.Length);
                foreach (byte x in bytes)
                {
                    sum += x;
                }
            }
        }

        Assert.True(sum >= 0);
    }

    private sealed class TruncatedSource : SaveSource
    {
        private readonly byte[] _data;
        private readonly int _length;
        private int _pos;

        public TruncatedSource(byte[] data, int length)
        {
            _data = data;
            _length = length;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, _length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }
}
